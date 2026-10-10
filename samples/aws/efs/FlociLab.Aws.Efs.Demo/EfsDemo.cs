using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.ElasticFileSystem;
using Amazon.ElasticFileSystem.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Efs;

/// <summary>
/// An encrypted file system with a throughput change, a lifecycle rule, a backup policy and a
/// resource policy, a mount target with its security groups, an access point and tags, refused on
/// delete while the mount target remains and then taken apart, against floci. Ordinary
/// AWSSDK.ElasticFileSystem code — the only emulator-aware line in the sample is in
/// <see cref="EfsClientFactory"/>.
/// </summary>
public sealed class EfsDemo(EfsClientFactory factory) : IServiceDemo
{
    // A subnet and a security group in the right shape and nothing behind them, so the sample needs
    // no VPC and no second service (EC2 would be a second SDK package). floci never looks them up;
    // real EFS rejects a subnet it cannot find, so against real AWS substitute a subnet in your
    // default VPC and a security group in that same VPC (docs/BLAZOR-PLAN.md §14).
    private const string SubnetId = "subnet-12345678";

    private const string SecurityGroupId = "sg-12345678";

    // Real file systems and mount targets sit in creating/deleting for tens of seconds to minutes;
    // floci is available at once. The ceiling is generous because the cost of waiting is a page
    // that says "Running…", and the cost of not waiting is a leaked file system that bills by the GB.
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SettlePollInterval = TimeSpan.FromSeconds(5);

    public string Provider => CloudProvider.Aws;

    public string Slug => "efs";

    public string DisplayName => "EFS";

    public string Category => "Storage, transfer and backup";

    public string Route => "/aws/efs";

    /// <summary>
    /// DescribeFileSystems with <c>MaxItems = 1</c>. Like Global Accelerator, EFS has no read that is
    /// not empty on a fresh account, and zero is a good answer — the count is read through a
    /// null-conditional.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonElasticFileSystem client = factory.Create();
            DescribeFileSystemsResponse response = await client.DescribeFileSystemsAsync(new DescribeFileSystemsRequest { MaxItems = 1 }, ct).ConfigureAwait(false);
            int count = response.FileSystems?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"DescribeFileSystems returned {count} file system(s) on the first page.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonElasticFileSystem client = factory.Create();

        // Unique per run, so two runs never collide. EFS file systems have no name to find them by
        // (Name is just a tag), but CreationToken is the idempotency handle the API itself offers
        // and DescribeFileSystems can filter on it — so it is what cleanup uses if a create landed
        // while its response was lost.
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string token = $"flocilab-efs-{suffix}";
        string url = $"{factory.ServiceUrl}/2015-02-01";

        string fileSystemId = string.Empty;
        string fileSystemArn = string.Empty;
        string mountTargetId = string.Empty;
        string accessPointId = string.Empty;

        bool createAttempted = false;
        bool deleted = false;

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "DescribeFileSystems — before",
                $"GET {url}/file-systems\nclient.DescribeFileSystemsAsync(new DescribeFileSystemsRequest())",
                async () =>
                {
                    DescribeFileSystemsResponse response = await client.DescribeFileSystemsAsync(new DescribeFileSystemsRequest(), ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {response.FileSystems?.Count ?? 0} file system(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateFileSystem",
                $"POST {url}/file-systems\nclient.CreateFileSystemAsync(new CreateFileSystemRequest {{ CreationToken = \"{token}\", PerformanceMode = generalPurpose, ThroughputMode = bursting, Encrypted = true, Tags = [Name={token}, lab=flocilab] }})",
                async () =>
                {
                    // Claimed before the call, not after: a request that lands while its response is
                    // lost (the page's Dispose cancels mid-flight) still created a file system.
                    // Cleanup asks the server, by creation token, rather than trusting this flag (plan §14).
                    createAttempted = true;

                    CreateFileSystemResponse response = await client.CreateFileSystemAsync(
                        new CreateFileSystemRequest
                        {
                            CreationToken = token,
                            PerformanceMode = PerformanceMode.GeneralPurpose,
                            ThroughputMode = ThroughputMode.Bursting,
                            Encrypted = true,
                            Tags = [new Tag { Key = "Name", Value = token }, new Tag { Key = "lab", Value = "flocilab" }],
                        }, ct).ConfigureAwait(false);

                    fileSystemId = response.FileSystemId;
                    fileSystemArn = response.FileSystemArn;

                    return $"HTTP {(int)response.HttpStatusCode} — {fileSystemId}, {response.LifeCycleState}, encrypted {response.Encrypted}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeFileSystems — by id",
                $"GET {url}/file-systems?FileSystemId={fileSystemId}\nclient.DescribeFileSystemsAsync(new DescribeFileSystemsRequest {{ FileSystemId = \"{fileSystemId}\" }})",
                async () =>
                {
                    FileSystemDescription description = await WaitForFileSystemAvailableAsync(client, fileSystemId, ct).ConfigureAwait(false);

                    // What CreateFileSystem was told has to read back from the one description.
                    if (description.Encrypted != true
                        || description.PerformanceMode != PerformanceMode.GeneralPurpose
                        || description.ThroughputMode != ThroughputMode.Bursting
                        || description.CreationToken != token
                        || description.Tags?.Find(t => t.Key == "lab")?.Value != "flocilab")
                    {
                        throw new InvalidOperationException($"expected an encrypted generalPurpose bursting file system with token {token} and lab=flocilab but got encrypted {description.Encrypted}, {description.PerformanceMode}, {description.ThroughputMode}, token {description.CreationToken}.");
                    }

                    return $"{description.LifeCycleState} — {description.FileSystemId}, {description.PerformanceMode}, {description.ThroughputMode}, {description.NumberOfMountTargets} mount target(s), {description.SizeInBytes?.Value} byte(s)";
                }).ConfigureAwait(false);

            // The creation token is EFS's idempotency key. The API answers a replay with the
            // existing file system's id rather than creating a second one, and the id is in the
            // error — the only way a caller that lost its response gets back to what it made.
            yield return await RunStepAsync(
                "CreateFileSystem — same token refused",
                $"POST {url}/file-systems\nclient.CreateFileSystemAsync(new CreateFileSystemRequest {{ CreationToken = \"{token}\" }})   // expect FileSystemAlreadyExistsException",
                async () =>
                {
                    try
                    {
                        await client.CreateFileSystemAsync(new CreateFileSystemRequest { CreationToken = token }, ct).ConfigureAwait(false);
                    }
                    catch (FileSystemAlreadyExistsException ex)
                    {
                        if (ex.FileSystemId != fileSystemId)
                        {
                            throw new InvalidOperationException($"the refusal named {ex.FileSystemId}, not {fileSystemId}.");
                        }

                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message} ({ex.FileSystemId})";
                    }

                    throw new InvalidOperationException("a second file system was created for a creation token already in use.");
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpdateFileSystem — provisioned throughput",
                $"PUT {url}/file-systems/{fileSystemId}\nclient.UpdateFileSystemAsync(new UpdateFileSystemRequest {{ FileSystemId = \"{fileSystemId}\", ThroughputMode = provisioned, ProvisionedThroughputInMibps = 10 }})",
                async () =>
                {
                    UpdateFileSystemResponse response = await client.UpdateFileSystemAsync(
                        new UpdateFileSystemRequest
                        {
                            FileSystemId = fileSystemId,
                            ThroughputMode = ThroughputMode.Provisioned,
                            ProvisionedThroughputInMibps = 10,
                        }, ct).ConfigureAwait(false);

                    // Real EFS answers 202 with the file system "updating" and refuses further
                    // changes until it is available again.
                    FileSystemDescription description = await WaitForFileSystemAvailableAsync(client, fileSystemId, ct).ConfigureAwait(false);

                    if (description.ThroughputMode != ThroughputMode.Provisioned || description.ProvisionedThroughputInMibps != 10)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected provisioned at 10 MiB/s but got {description.ThroughputMode} at {description.ProvisionedThroughputInMibps}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {description.ThroughputMode}, {description.ProvisionedThroughputInMibps} MiB/s, {description.LifeCycleState}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutLifecycleConfiguration",
                $"PUT {url}/file-systems/{fileSystemId}/lifecycle-configuration\nclient.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest {{ FileSystemId = \"{fileSystemId}\", LifecyclePolicies = [{{ TransitionToIA = AFTER_30_DAYS }}] }})",
                async () =>
                {
                    PutLifecycleConfigurationResponse response = await client.PutLifecycleConfigurationAsync(
                        new PutLifecycleConfigurationRequest
                        {
                            FileSystemId = fileSystemId,
                            LifecyclePolicies = [new LifecyclePolicy { TransitionToIA = TransitionToIARules.AFTER_30_DAYS }],
                        }, ct).ConfigureAwait(false);

                    DescribeLifecycleConfigurationResponse read = await client.DescribeLifecycleConfigurationAsync(new DescribeLifecycleConfigurationRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);
                    List<LifecyclePolicy> policies = read.LifecyclePolicies ?? [];

                    if (policies.Count != 1 || policies[0].TransitionToIA != TransitionToIARules.AFTER_30_DAYS)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected one rule, TransitionToIA AFTER_30_DAYS, but got {policies.Count}: {string.Join(", ", policies.Select(p => p.TransitionToIA?.Value))}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — read back TransitionToIA {policies[0].TransitionToIA}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutBackupPolicy",
                $"PUT {url}/file-systems/{fileSystemId}/backup-policy\nclient.PutBackupPolicyAsync(new PutBackupPolicyRequest {{ FileSystemId = \"{fileSystemId}\", BackupPolicy = {{ Status = ENABLED }} }})",
                async () =>
                {
                    PutBackupPolicyResponse response = await client.PutBackupPolicyAsync(
                        new PutBackupPolicyRequest { FileSystemId = fileSystemId, BackupPolicy = new BackupPolicy { Status = Status.ENABLED } }, ct).ConfigureAwait(false);

                    // Real EFS moves the policy through ENABLING before ENABLED, which is why the
                    // read-back accepts either rather than racing it.
                    DescribeBackupPolicyResponse read = await client.DescribeBackupPolicyAsync(new DescribeBackupPolicyRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);

                    if (read.BackupPolicy?.Status != Status.ENABLED && read.BackupPolicy?.Status != Status.ENABLING)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected the backup policy ENABLED or ENABLING but got {read.BackupPolicy?.Status}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — read back {read.BackupPolicy.Status}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "PutFileSystemPolicy",
                $"PUT {url}/file-systems/{fileSystemId}/policy\nclient.PutFileSystemPolicyAsync(new PutFileSystemPolicyRequest {{ FileSystemId = \"{fileSystemId}\", Policy = <deny every action unless aws:SecureTransport> }})",
                async () =>
                {
                    // The policy AWS's own documentation recommends first: refuse anything that is
                    // not encrypted in transit. Resource is the file system itself.
                    string policy = JsonSerializer.Serialize(new
                    {
                        Version = "2012-10-17",
                        Statement = new[]
                        {
                            new
                            {
                                Sid = "DenyUnencryptedTransport",
                                Effect = "Deny",
                                Principal = new { AWS = "*" },
                                Action = "*",
                                Resource = fileSystemArn,
                                Condition = new { Bool = new Dictionary<string, string> { ["aws:SecureTransport"] = "false" } },
                            },
                        },
                    });

                    PutFileSystemPolicyResponse response = await client.PutFileSystemPolicyAsync(
                        new PutFileSystemPolicyRequest { FileSystemId = fileSystemId, Policy = policy }, ct).ConfigureAwait(false);

                    DescribeFileSystemPolicyResponse read = await client.DescribeFileSystemPolicyAsync(new DescribeFileSystemPolicyRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);

                    // Compared as JSON, not as a string: real EFS re-serialises the document.
                    using JsonDocument document = JsonDocument.Parse(read.Policy);
                    JsonElement statements = document.RootElement.GetProperty("Statement");
                    bool denies = statements.ValueKind == JsonValueKind.Array
                        && statements.GetArrayLength() == 1
                        && statements[0].GetProperty("Effect").GetString() == "Deny";

                    if (!denies)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected one Deny statement but read back: {read.Policy}");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — one Deny statement on {fileSystemId} read back";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateMountTarget",
                $"POST {url}/mount-targets\nclient.CreateMountTargetAsync(new CreateMountTargetRequest {{ FileSystemId = \"{fileSystemId}\", SubnetId = \"{SubnetId}\" }})",
                async () =>
                {
                    CreateMountTargetResponse response = await client.CreateMountTargetAsync(
                        new CreateMountTargetRequest { FileSystemId = fileSystemId, SubnetId = SubnetId }, ct).ConfigureAwait(false);

                    mountTargetId = response.MountTargetId;

                    MountTargetDescription mountTarget = await WaitForMountTargetAvailableAsync(client, fileSystemId, mountTargetId, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {mountTargetId}, {mountTarget.LifeCycleState}, {mountTarget.IpAddress} in {mountTarget.AvailabilityZoneName}, {mountTarget.NetworkInterfaceId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeMountTargets",
                $"GET {url}/mount-targets?FileSystemId={fileSystemId}\nclient.DescribeMountTargetsAsync(new DescribeMountTargetsRequest {{ FileSystemId = \"{fileSystemId}\" }})",
                async () =>
                {
                    DescribeMountTargetsResponse response = await client.DescribeMountTargetsAsync(new DescribeMountTargetsRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);
                    List<MountTargetDescription> targets = response.MountTargets ?? [];
                    FileSystemDescription description = await DescribeFileSystemAsync(client, fileSystemId, ct).ConfigureAwait(false);

                    // The mount target has to be listed under its file system, and the file system has to count it.
                    if (targets.Find(t => t.MountTargetId == mountTargetId)?.SubnetId != SubnetId || description.NumberOfMountTargets != 1)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected {mountTargetId} in {SubnetId} and NumberOfMountTargets 1 but got {targets.Count} listed and {description.NumberOfMountTargets} counted.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join("; ", targets.Select(t => $"{t.MountTargetId} {t.IpAddress} {t.LifeCycleState}"))}; file system counts {description.NumberOfMountTargets}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ModifyMountTargetSecurityGroups",
                $"PUT {url}/mount-targets/{mountTargetId}/security-groups\nclient.ModifyMountTargetSecurityGroupsAsync(new ModifyMountTargetSecurityGroupsRequest {{ MountTargetId = \"{mountTargetId}\", SecurityGroups = [\"{SecurityGroupId}\"] }})",
                async () =>
                {
                    ModifyMountTargetSecurityGroupsResponse response = await client.ModifyMountTargetSecurityGroupsAsync(
                        new ModifyMountTargetSecurityGroupsRequest { MountTargetId = mountTargetId, SecurityGroups = [SecurityGroupId] }, ct).ConfigureAwait(false);

                    DescribeMountTargetSecurityGroupsResponse read = await client.DescribeMountTargetSecurityGroupsAsync(new DescribeMountTargetSecurityGroupsRequest { MountTargetId = mountTargetId }, ct).ConfigureAwait(false);
                    List<string> groups = read.SecurityGroups ?? [];

                    if (groups.Count != 1 || groups[0] != SecurityGroupId)
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected [{SecurityGroupId}] but got [{string.Join(", ", groups)}].");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — read back {string.Join(", ", groups)}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateAccessPoint",
                $"POST {url}/access-points\nclient.CreateAccessPointAsync(new CreateAccessPointRequest {{ ClientToken = \"{token}\", FileSystemId = \"{fileSystemId}\", PosixUser = 1000:1000, RootDirectory = {{ Path = \"/data\", CreationInfo = 1000:1000 755 }} }})",
                async () =>
                {
                    CreateAccessPointResponse response = await client.CreateAccessPointAsync(
                        new CreateAccessPointRequest
                        {
                            ClientToken = token,
                            FileSystemId = fileSystemId,
                            PosixUser = new PosixUser { Uid = 1000, Gid = 1000 },
                            RootDirectory = new RootDirectory
                            {
                                Path = "/data",
                                CreationInfo = new CreationInfo { OwnerUid = 1000, OwnerGid = 1000, Permissions = "755" },
                            },
                            Tags = [new Tag { Key = "Name", Value = "data" }],
                        }, ct).ConfigureAwait(false);

                    accessPointId = response.AccessPointId;

                    return $"HTTP {(int)response.HttpStatusCode} — {accessPointId}, {response.LifeCycleState}, root {response.RootDirectory?.Path} as {response.PosixUser?.Uid}:{response.PosixUser?.Gid}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeAccessPoints",
                $"GET {url}/access-points?FileSystemId={fileSystemId}\nclient.DescribeAccessPointsAsync(new DescribeAccessPointsRequest {{ FileSystemId = \"{fileSystemId}\" }})",
                async () =>
                {
                    DescribeAccessPointsResponse response = await client.DescribeAccessPointsAsync(new DescribeAccessPointsRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);
                    AccessPointDescription? accessPoint = (response.AccessPoints ?? []).Find(a => a.AccessPointId == accessPointId);

                    if (accessPoint?.RootDirectory?.Path != "/data"
                        || accessPoint.PosixUser?.Uid != 1000
                        || accessPoint.RootDirectory.CreationInfo?.Permissions != "755")
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected {accessPointId} rooted at /data as uid 1000 with 755 but got {accessPoint?.RootDirectory?.Path}, uid {accessPoint?.PosixUser?.Uid}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {accessPoint.AccessPointId} {accessPoint.RootDirectory.Path} uid {accessPoint.PosixUser.Uid} gid {accessPoint.PosixUser.Gid} perms {accessPoint.RootDirectory.CreationInfo.Permissions}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "TagResource",
                $"POST {url}/resource-tags/{fileSystemId}\nclient.TagResourceAsync(new TagResourceRequest {{ ResourceId = \"{fileSystemId}\", Tags = [episode=efs] }})",
                async () =>
                {
                    TagResourceResponse response = await client.TagResourceAsync(
                        new TagResourceRequest { ResourceId = fileSystemId, Tags = [new Tag { Key = "episode", Value = "efs" }] }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListTagsForResource",
                $"GET {url}/resource-tags/{fileSystemId}\nclient.ListTagsForResourceAsync(new ListTagsForResourceRequest {{ ResourceId = \"{fileSystemId}\" }})",
                async () =>
                {
                    ListTagsForResourceResponse response = await client.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceId = fileSystemId }, ct).ConfigureAwait(false);
                    List<Tag> tags = response.Tags ?? [];

                    // Two from CreateFileSystem and one from TagResource: both routes end up in the same set.
                    if (tags.Find(t => t.Key == "lab")?.Value != "flocilab" || tags.Find(t => t.Key == "episode")?.Value != "efs")
                    {
                        throw new InvalidOperationException($"HTTP {(int)response.HttpStatusCode} — expected lab=flocilab and episode=efs but got: {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}";
                }).ConfigureAwait(false);

            // The refusal is the point of the step, not a failure of it: a file system with a mount
            // target is in use, and a page that only ever showed the happy path would hide the rule
            // that bites first in real use.
            yield return await RunStepAsync(
                "DeleteFileSystem — refused while it has a mount target",
                $"DELETE {url}/file-systems/{fileSystemId}\nclient.DeleteFileSystemAsync(new DeleteFileSystemRequest {{ FileSystemId = \"{fileSystemId}\" }})   // expect FileSystemInUseException",
                async () =>
                {
                    // Without a mount target this delete is not refused, it succeeds: on real AWS the
                    // made-up subnet fails CreateMountTarget, and the "refusal" would take the file
                    // system with it. Ask the server, not the flag, what is holding it.
                    DescribeMountTargetsResponse held = await client.DescribeMountTargetsAsync(new DescribeMountTargetsRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);

                    if ((held.MountTargets ?? []).Count == 0)
                    {
                        throw new InvalidOperationException($"skipped: {fileSystemId} has no mount target, so DeleteFileSystem would delete it rather than refuse.");
                    }

                    try
                    {
                        await client.DeleteFileSystemAsync(new DeleteFileSystemRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);
                    }
                    catch (FileSystemInUseException ex)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException("the file system was deleted although it still has a mount target.");
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteAccessPoint",
                $"DELETE {url}/access-points/{accessPointId}\nclient.DeleteAccessPointAsync(new DeleteAccessPointRequest {{ AccessPointId = \"{accessPointId}\" }})",
                async () =>
                {
                    DeleteAccessPointResponse response = await client.DeleteAccessPointAsync(new DeleteAccessPointRequest { AccessPointId = accessPointId }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteMountTarget",
                $"DELETE {url}/mount-targets/{mountTargetId}\nclient.DeleteMountTargetAsync(new DeleteMountTargetRequest {{ MountTargetId = \"{mountTargetId}\" }})",
                async () =>
                {
                    DeleteMountTargetResponse response = await client.DeleteMountTargetAsync(new DeleteMountTargetRequest { MountTargetId = mountTargetId }, ct).ConfigureAwait(false);

                    // Real EFS holds the mount target in "deleting" for a while, and refuses to
                    // delete the file system until it is gone.
                    await WaitForMountTargetsGoneAsync(client, fileSystemId, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteFileSystem",
                $"DELETE {url}/file-systems/{fileSystemId}\nclient.DeleteFileSystemAsync(new DeleteFileSystemRequest {{ FileSystemId = \"{fileSystemId}\" }})",
                async () =>
                {
                    DeleteFileSystemResponse response = await client.DeleteFileSystemAsync(new DeleteFileSystemRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);

                    deleted = true;

                    await WaitForFileSystemGoneAsync(client, fileSystemId, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — {fileSystemId} no longer described";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            if (createAttempted && !deleted)
            {
                cleanup.Add(await this.DeleteFileSystemTreeAsync(client, token, ct).ConfigureAwait(false));
            }
        }

        foreach (DemoStep step in cleanup)
        {
            yield return step;
        }
    }

    /// <summary>
    /// The AWS SDK reports both of the interesting failures inside an
    /// <see cref="AmazonServiceException"/>, so <see cref="ProbeResult.FromException"/> — which
    /// inspects only the outermost exception — cannot classify them on its own. A 501 arrives as
    /// a status code on the exception; a refused connection arrives with no status code at all
    /// and a transport exception underneath.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real EFS would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes what exists.
        // Catching it here would instead fabricate a "Failed" step for every remaining
        // operation, reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static async Task<FileSystemDescription> DescribeFileSystemAsync(IAmazonElasticFileSystem client, string fileSystemId, CancellationToken ct)
    {
        DescribeFileSystemsResponse response = await client.DescribeFileSystemsAsync(new DescribeFileSystemsRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);

        return (response.FileSystems ?? []).Find(f => f.FileSystemId == fileSystemId)
            ?? throw new InvalidOperationException($"DescribeFileSystems did not list {fileSystemId}.");
    }

    /// <summary>
    /// Polls until the file system is <c>available</c>. Real EFS holds it in <c>creating</c> after
    /// the create and <c>updating</c> after a throughput change, and refuses the next change until
    /// it settles.
    /// </summary>
    private static async Task<FileSystemDescription> WaitForFileSystemAvailableAsync(IAmazonElasticFileSystem client, string fileSystemId, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            FileSystemDescription description = await DescribeFileSystemAsync(client, fileSystemId, ct).ConfigureAwait(false);

            if (description.LifeCycleState == LifeCycleState.Available)
            {
                return description;
            }

            if (Stopwatch.GetElapsedTime(started) > SettleTimeout)
            {
                throw new TimeoutException($"{fileSystemId} was still {description.LifeCycleState} after {SettleTimeout.TotalMinutes:0} minutes.");
            }

            await Task.Delay(SettlePollInterval, ct).ConfigureAwait(false);
        }
    }

    private static async Task<MountTargetDescription> WaitForMountTargetAvailableAsync(IAmazonElasticFileSystem client, string fileSystemId, string mountTargetId, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            DescribeMountTargetsResponse response = await client.DescribeMountTargetsAsync(new DescribeMountTargetsRequest { MountTargetId = mountTargetId }, ct).ConfigureAwait(false);
            MountTargetDescription mountTarget = (response.MountTargets ?? []).Find(m => m.MountTargetId == mountTargetId)
                ?? throw new InvalidOperationException($"DescribeMountTargets did not list {mountTargetId} of {fileSystemId}.");

            if (mountTarget.LifeCycleState == LifeCycleState.Available)
            {
                return mountTarget;
            }

            if (Stopwatch.GetElapsedTime(started) > SettleTimeout)
            {
                throw new TimeoutException($"{mountTargetId} was still {mountTarget.LifeCycleState} after {SettleTimeout.TotalMinutes:0} minutes.");
            }

            await Task.Delay(SettlePollInterval, ct).ConfigureAwait(false);
        }
    }

    private static async Task WaitForMountTargetsGoneAsync(IAmazonElasticFileSystem client, string fileSystemId, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            DescribeMountTargetsResponse response = await client.DescribeMountTargetsAsync(new DescribeMountTargetsRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);

            if ((response.MountTargets ?? []).Count == 0)
            {
                return;
            }

            if (Stopwatch.GetElapsedTime(started) > SettleTimeout)
            {
                throw new TimeoutException($"{fileSystemId} still had {response.MountTargets!.Count} mount target(s) after {SettleTimeout.TotalMinutes:0} minutes.");
            }

            await Task.Delay(SettlePollInterval, ct).ConfigureAwait(false);
        }
    }

    private static async Task WaitForFileSystemGoneAsync(IAmazonElasticFileSystem client, string fileSystemId, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            try
            {
                await client.DescribeFileSystemsAsync(new DescribeFileSystemsRequest { FileSystemId = fileSystemId }, ct).ConfigureAwait(false);
            }
            catch (FileSystemNotFoundException)
            {
                return;
            }

            if (Stopwatch.GetElapsedTime(started) > SettleTimeout)
            {
                throw new TimeoutException($"{fileSystemId} was still described after {SettleTimeout.TotalMinutes:0} minutes.");
            }

            await Task.Delay(SettlePollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cleanup uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a
    /// file system to remove. The id is minted by the server, so a create whose response was lost
    /// leaves nothing to delete by; the run's unique creation token is what finds it. Removal is
    /// bottom-up because EFS refuses anything else: access points and mount targets first (and the
    /// mount targets gone, not merely deleting), then the file system.
    /// </summary>
    private async Task<DemoStep> DeleteFileSystemTreeAsync(IAmazonElasticFileSystem client, string token, CancellationToken ct)
    {
        string request = $"{factory.ServiceUrl}/2015-02-01\nDescribeFileSystems, DescribeAccessPoints, DescribeMountTargets, then Delete* bottom-up\nclient.DeleteFileSystemAsync(new DeleteFileSystemRequest {{ FileSystemId = <the file system created with token \"{token}\"> }})";

        return await RunStepAsync("DeleteFileSystem — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            FileSystemDescription? fileSystem = await FindFileSystemAsync(client, token).ConfigureAwait(false);

            if (fileSystem is null)
            {
                // Not a failure: the create never landed, and the server says so.
                return $"No file system was created with token {token} — nothing to remove.{cancelled}";
            }

            string id = fileSystem.FileSystemId;

            // A DeleteFileSystem that landed while its response was lost (the run cancelled mid-call)
            // leaves the file system deleting. A second delete would be refused; waiting is the cleanup.
            if (fileSystem.LifeCycleState == LifeCycleState.Deleting || fileSystem.LifeCycleState == LifeCycleState.Deleted)
            {
                await WaitForFileSystemGoneAsync(client, id, CancellationToken.None).ConfigureAwait(false);

                return $"{id} was already {fileSystem.LifeCycleState} — waited for it to go.{cancelled}";
            }

            int accessPoints = 0;
            int mountTargets = 0;

            // One failed delete must not stop the rest: every child is tried, and what failed is
            // reported at the end, so the step reads as failed and names what was left behind.
            List<string> failed = [];

            foreach (AccessPointDescription accessPoint in await ListAccessPointsAsync(client, id).ConfigureAwait(false))
            {
                try
                {
                    await client.DeleteAccessPointAsync(new DeleteAccessPointRequest { AccessPointId = accessPoint.AccessPointId }, CancellationToken.None).ConfigureAwait(false);
                    accessPoints++;
                }
                catch (AmazonServiceException ex)
                {
                    failed.Add($"access point {accessPoint.AccessPointId}: {ex.ErrorCode}");
                }
            }

            foreach (MountTargetDescription mountTarget in await ListMountTargetsAsync(client, id).ConfigureAwait(false))
            {
                if (mountTarget.LifeCycleState == LifeCycleState.Deleting || mountTarget.LifeCycleState == LifeCycleState.Deleted)
                {
                    continue;
                }

                try
                {
                    // Real EFS refuses to delete a mount target that is still creating, which is the
                    // state a run abandoned during CreateMountTarget's wait leaves behind.
                    if (mountTarget.LifeCycleState == LifeCycleState.Creating)
                    {
                        await WaitForMountTargetAvailableAsync(client, id, mountTarget.MountTargetId, CancellationToken.None).ConfigureAwait(false);
                    }

                    await client.DeleteMountTargetAsync(new DeleteMountTargetRequest { MountTargetId = mountTarget.MountTargetId }, CancellationToken.None).ConfigureAwait(false);
                    mountTargets++;
                }
                catch (Exception ex) when (ex is AmazonServiceException or TimeoutException or InvalidOperationException)
                {
                    failed.Add($"mount target {mountTarget.MountTargetId}: {Reason(ex)}");
                }
            }

            // With a child left, EFS refuses the delete and the wait for the mount targets to go would
            // run out its ten minutes, so the file system is named rather than tried.
            if (failed.Count > 0)
            {
                failed.Add($"file system {id}: not deleted, its children remain");
            }
            else
            {
                try
                {
                    await WaitForMountTargetsGoneAsync(client, id, CancellationToken.None).ConfigureAwait(false);
                    await client.DeleteFileSystemAsync(new DeleteFileSystemRequest { FileSystemId = id }, CancellationToken.None).ConfigureAwait(false);
                    await WaitForFileSystemGoneAsync(client, id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AmazonServiceException or TimeoutException)
                {
                    failed.Add($"file system {id}: {Reason(ex)}");
                }
            }

            string deleted = $"Deleted {accessPoints} access point(s) and {mountTargets} mount target(s).";

            if (failed.Count > 0)
            {
                throw new InvalidOperationException($"{deleted} Left behind: {string.Join("; ", failed)}.{cancelled}");
            }

            return $"{deleted} Deleted {id}.{cancelled}";
        }).ConfigureAwait(false);
    }

    private static string Reason(Exception ex) => ex is AmazonServiceException service ? service.ErrorCode : ex.Message;

    // Every page is read before anything is deleted: deleting from the page being walked shifts
    // what follows under the cursor, and the next page would skip it.
    private static async Task<List<AccessPointDescription>> ListAccessPointsAsync(IAmazonElasticFileSystem client, string fileSystemId)
    {
        List<AccessPointDescription> all = [];
        string? next = null;

        do
        {
            DescribeAccessPointsResponse page = await client.DescribeAccessPointsAsync(new DescribeAccessPointsRequest { FileSystemId = fileSystemId, NextToken = next }, CancellationToken.None).ConfigureAwait(false);
            all.AddRange(page.AccessPoints ?? []);
            next = page.NextToken;
        }
        while (!string.IsNullOrEmpty(next));

        return all;
    }

    private static async Task<List<MountTargetDescription>> ListMountTargetsAsync(IAmazonElasticFileSystem client, string fileSystemId)
    {
        List<MountTargetDescription> all = [];
        string? marker = null;

        do
        {
            DescribeMountTargetsResponse page = await client.DescribeMountTargetsAsync(new DescribeMountTargetsRequest { FileSystemId = fileSystemId, Marker = marker }, CancellationToken.None).ConfigureAwait(false);
            all.AddRange(page.MountTargets ?? []);
            marker = page.NextMarker;
        }
        while (!string.IsNullOrEmpty(marker));

        return all;
    }

    // The creation token is a server-side filter, so this is one call rather than a walk of every
    // page of every file system in the account (plan §14, the Cloud Map row).
    private static async Task<FileSystemDescription?> FindFileSystemAsync(IAmazonElasticFileSystem client, string token)
    {
        DescribeFileSystemsResponse response = await client.DescribeFileSystemsAsync(new DescribeFileSystemsRequest { CreationToken = token }, CancellationToken.None).ConfigureAwait(false);

        return (response.FileSystems ?? []).Find(f => f.CreationToken == token);
    }
}
