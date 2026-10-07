using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Lightsail;
using Amazon.Lightsail.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Lightsail;

/// <summary>
/// Reads the blueprint and bundle catalog, creates an instance, stops and starts it, opens a port,
/// gives it a static IP, attaches a disk and adds a key pair, then asks for a duplicate name, a
/// blueprint that does not exist and a snapshot, and deletes everything it made. Ordinary
/// AWSSDK.Lightsail code — the only emulator-aware line is in <see cref="LightsailClientFactory"/>.
/// What differs from real Lightsail is how much is behind the answer: floci keeps the records and
/// answers every call "completed locally" — no virtual machine starts, the public address is a
/// documentation-range placeholder, and while it refuses a duplicate name it never checks a
/// blueprint id. Every step
/// reports what the engine actually answered rather than what the docs promise.
/// </summary>
public sealed class LightsailDemo(LightsailClientFactory factory) : IServiceDemo
{
    private const string BlueprintId = "amazon_linux_2023";
    private const string BundleId = "nano_3_0";

    public string Provider => CloudProvider.Aws;

    public string Slug => "lightsail";

    public string DisplayName => "Lightsail";

    public string Category => "Containers and compute";

    public string Route => "/aws/lightsail";

    /// <summary>GetInstances — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonLightsail client = factory.Create();
            GetInstancesResponse response = await client.GetInstancesAsync(new GetInstancesRequest(), ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"GetInstances: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonLightsail client = factory.Create();

        string suffix = Guid.NewGuid().ToString("N")[..12];
        string zone = $"{factory.Region}a";
        string instance = $"flocilab-{suffix}";
        string odd = $"flocilab-{suffix}-odd";
        string disk = $"flocilab-{suffix}-disk";
        string staticIp = $"flocilab-{suffix}-ip";
        string keyPair = $"flocilab-{suffix}-key";
        string snapshot = $"flocilab-{suffix}-snap";

        // Claimed before each create: a call that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created the resource, so the finally looks each name
        // up rather than trusting that a response arrived.
        Claimed claimed = new();

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "GetBlueprints, GetBundles — the catalog",
                this.Call("GetBlueprints", "client.GetBlueprintsAsync(new GetBlueprintsRequest())\nclient.GetBundlesAsync(new GetBundlesRequest())"),
                async () =>
                {
                    List<Blueprint> blueprints = await ListBlueprintsAsync(client, ct).ConfigureAwait(false);
                    List<Bundle> bundles = await ListBundlesAsync(client, ct).ConfigureAwait(false);

                    Blueprint? blueprint = blueprints.Find(b => b.BlueprintId == BlueprintId);
                    Bundle? bundle = bundles.Find(b => b.BundleId == BundleId);

                    if (blueprint is null || bundle is null)
                    {
                        throw new InvalidOperationException($"the catalog does not list {(blueprint is null ? BlueprintId : BundleId)}, which this demo creates an instance from.");
                    }

                    return $"{blueprints.Count} blueprint(s), {bundles.Count} bundle(s)\n{blueprint.BlueprintId}: {blueprint.Name}, {blueprint.Platform?.Value}\n{bundle.BundleId}: {bundle.CpuCount} vCPU, {bundle.RamSizeInGb} GB RAM, {bundle.DiskSizeInGb} GB disk, ${bundle.Price}/month";
                }).ConfigureAwait(false);

            DemoStep create = await RunStepAsync(
                "CreateInstances — a nano Amazon Linux instance",
                this.Call("CreateInstances", $"client.CreateInstancesAsync(new CreateInstancesRequest {{ InstanceNames = [\"{instance}\"], AvailabilityZone = \"{zone}\", BlueprintId = \"{BlueprintId}\", BundleId = \"{BundleId}\", Tags = [run = {suffix}] }})"),
                async () =>
                {
                    claimed.Instance = true;
                    CreateInstancesResponse response = await client.CreateInstancesAsync(
                        new CreateInstancesRequest
                        {
                            InstanceNames = [instance],
                            AvailabilityZone = zone,
                            BlueprintId = BlueprintId,
                            BundleId = BundleId,
                            Tags = [new Tag { Key = "run", Value = suffix }],
                        }, ct).ConfigureAwait(false);

                    return string.Join("\n", (response.Operations ?? []).Select(DescribeOperation));
                }).ConfigureAwait(false);

            yield return create;

            // A failed create means every later step would look for an instance that is not there.
            // Skipped rather than `yield break`, which would also skip yielding the cleanup step below.
            if (create.Succeeded)
            {
                yield return await RunStepAsync(
                    "GetInstance — what was created",
                    this.Call("GetInstance", $"client.GetInstanceAsync(new GetInstanceRequest {{ InstanceName = \"{instance}\" }})"),
                    async () =>
                    {
                        // Real Lightsail answers "pending" for a minute or so; floci is running at once.
                        await WaitForStateAsync(client, instance, "running", ct).ConfigureAwait(false);

                        Instance found = (await client.GetInstanceAsync(new GetInstanceRequest { InstanceName = instance }, ct).ConfigureAwait(false)).Instance;

                        if (!(found.Tags ?? []).Exists(t => t.Key == "run" && t.Value == suffix))
                        {
                            throw new InvalidOperationException("the tag was accepted by CreateInstances but GetInstance does not return it.");
                        }

                        string ports = string.Join(", ", (found.Networking?.Ports ?? []).Select(p => $"{p.Protocol?.Value}/{p.FromPort}"));

                        return $"{found.Name}: {found.State?.Name}, {found.BlueprintName}, {found.BundleId}\npublic {found.PublicIpAddress}, private {found.PrivateIpAddress}, login {found.Username}\nports {ports}\nfloci keeps the record only: nothing is listening on that address";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "StopInstance, StartInstance, GetInstanceState",
                    this.Call("StopInstance", $"client.StopInstanceAsync(new StopInstanceRequest {{ InstanceName = \"{instance}\" }})\nclient.StartInstanceAsync(new StartInstanceRequest {{ InstanceName = \"{instance}\" }})"),
                    async () =>
                    {
                        await client.StopInstanceAsync(new StopInstanceRequest { InstanceName = instance }, ct).ConfigureAwait(false);
                        string stopped = await WaitForStateAsync(client, instance, "stopped", ct).ConfigureAwait(false);

                        await client.StartInstanceAsync(new StartInstanceRequest { InstanceName = instance }, ct).ConfigureAwait(false);
                        string started = await WaitForStateAsync(client, instance, "running", ct).ConfigureAwait(false);

                        return $"after StopInstance: {stopped}\nafter StartInstance: {started}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "OpenInstancePublicPorts, GetInstancePortStates",
                    this.Call("OpenInstancePublicPorts", $"client.OpenInstancePublicPortsAsync(new OpenInstancePublicPortsRequest {{ InstanceName = \"{instance}\", PortInfo = new PortInfo {{ FromPort = 8080, ToPort = 8080, Protocol = Tcp }} }})"),
                    async () =>
                    {
                        await client.OpenInstancePublicPortsAsync(
                            new OpenInstancePublicPortsRequest
                            {
                                InstanceName = instance,
                                PortInfo = new PortInfo { FromPort = 8080, ToPort = 8080, Protocol = NetworkProtocol.Tcp },
                            }, ct).ConfigureAwait(false);

                        GetInstancePortStatesResponse states = await client.GetInstancePortStatesAsync(new GetInstancePortStatesRequest { InstanceName = instance }, ct).ConfigureAwait(false);

                        if (!(states.PortStates ?? []).Exists(s => s.FromPort == 8080 && s.State == PortState.Open))
                        {
                            throw new InvalidOperationException("OpenInstancePublicPorts was accepted but GetInstancePortStates does not list 8080 as open.");
                        }

                        return string.Join("\n", states.PortStates!.Select(s => $"{s.Protocol?.Value}/{s.FromPort}-{s.ToPort}: {s.State?.Value}"));
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "AllocateStaticIp, AttachStaticIp, GetStaticIp",
                    this.Call("AllocateStaticIp", $"client.AllocateStaticIpAsync(new AllocateStaticIpRequest {{ StaticIpName = \"{staticIp}\" }})\nclient.AttachStaticIpAsync(new AttachStaticIpRequest {{ StaticIpName = \"{staticIp}\", InstanceName = \"{instance}\" }})"),
                    async () =>
                    {
                        claimed.StaticIp = true;
                        await client.AllocateStaticIpAsync(new AllocateStaticIpRequest { StaticIpName = staticIp }, ct).ConfigureAwait(false);
                        await client.AttachStaticIpAsync(new AttachStaticIpRequest { StaticIpName = staticIp, InstanceName = instance }, ct).ConfigureAwait(false);

                        StaticIp ip = (await client.GetStaticIpAsync(new GetStaticIpRequest { StaticIpName = staticIp }, ct).ConfigureAwait(false)).StaticIp;
                        Instance found = (await client.GetInstanceAsync(new GetInstanceRequest { InstanceName = instance }, ct).ConfigureAwait(false)).Instance;

                        if (ip.IsAttached != true || ip.AttachedTo != instance)
                        {
                            throw new InvalidOperationException($"AttachStaticIp was accepted but the static IP reports attached = {ip.IsAttached}, to {ip.AttachedTo}.");
                        }

                        if (found.IsStaticIp != true || found.PublicIpAddress != ip.IpAddress)
                        {
                            throw new InvalidOperationException($"the static IP is {ip.IpAddress} but the instance reports {found.PublicIpAddress} (static: {found.IsStaticIp}).");
                        }

                        return $"{ip.Name}: {ip.IpAddress}, attached to {ip.AttachedTo}\nGetInstance now reports {found.PublicIpAddress}, static = {found.IsStaticIp}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateDisk, AttachDisk, GetDisk",
                    this.Call("CreateDisk", $"client.CreateDiskAsync(new CreateDiskRequest {{ DiskName = \"{disk}\", AvailabilityZone = \"{zone}\", SizeInGb = 8 }})\nclient.AttachDiskAsync(new AttachDiskRequest {{ DiskName = \"{disk}\", InstanceName = \"{instance}\", DiskPath = \"/dev/xvdf\" }})"),
                    async () =>
                    {
                        claimed.Disk = true;
                        await client.CreateDiskAsync(new CreateDiskRequest { DiskName = disk, AvailabilityZone = zone, SizeInGb = 8 }, ct).ConfigureAwait(false);
                        await WaitForDiskAsync(client, disk, ct).ConfigureAwait(false);
                        await client.AttachDiskAsync(new AttachDiskRequest { DiskName = disk, InstanceName = instance, DiskPath = "/dev/xvdf" }, ct).ConfigureAwait(false);

                        Disk found = (await client.GetDiskAsync(new GetDiskRequest { DiskName = disk }, ct).ConfigureAwait(false)).Disk;

                        if (found.IsAttached != true || found.AttachedTo != instance)
                        {
                            throw new InvalidOperationException($"AttachDisk was accepted but the disk reports attached = {found.IsAttached}, to {found.AttachedTo}.");
                        }

                        return $"{found.Name}: {found.SizeInGb} GB, {found.State?.Value}, attached to {found.AttachedTo} at {found.Path}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateKeyPair, GetKeyPairs",
                    this.Call("CreateKeyPair", $"client.CreateKeyPairAsync(new CreateKeyPairRequest {{ KeyPairName = \"{keyPair}\" }})"),
                    async () =>
                    {
                        claimed.KeyPair = true;
                        CreateKeyPairResponse created = await client.CreateKeyPairAsync(new CreateKeyPairRequest { KeyPairName = keyPair }, ct).ConfigureAwait(false);

                        if (string.IsNullOrEmpty(created.PrivateKeyBase64) || string.IsNullOrEmpty(created.PublicKeyBase64))
                        {
                            throw new InvalidOperationException("CreateKeyPair returned without a private and a public key.");
                        }

                        List<KeyPair> all = await ListKeyPairsAsync(client, ct).ConfigureAwait(false);

                        if (!all.Exists(k => k.Name == keyPair))
                        {
                            throw new InvalidOperationException($"{keyPair} was created but GetKeyPairs does not list it.");
                        }

                        return $"{created.KeyPair.Name}: fingerprint {created.KeyPair.Fingerprint}\nprivate key {created.PrivateKeyBase64.Length} chars, public key {created.PublicKeyBase64.Length} chars (the private key is shown once and not kept here)\n{all.Count} key pair(s) in the account";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateInstances — a name that already exists",
                    this.Call("CreateInstances", $"client.CreateInstancesAsync(new CreateInstancesRequest {{ InstanceNames = [\"{instance}\"], ... }})"),
                    async () =>
                    {
                        try
                        {
                            await client.CreateInstancesAsync(
                                new CreateInstancesRequest { InstanceNames = [instance], AvailabilityZone = zone, BlueprintId = BlueprintId, BundleId = BundleId }, ct).ConfigureAwait(false);
                        }
                        // What real Lightsail answers: the expected outcome, shown as a successful step.
                        catch (InvalidInputException ex)
                        {
                            return $"{ex.GetType().Name}: {ex.Message}";
                        }

                        throw new InvalidOperationException("a second instance with an existing name was accepted.");
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateInstances — a blueprint that does not exist",
                    this.Call("CreateInstances", $"client.CreateInstancesAsync(new CreateInstancesRequest {{ InstanceNames = [\"{odd}\"], BlueprintId = \"no-such-blueprint\", ... }})"),
                    async () =>
                    {
                        try
                        {
                            claimed.Odd = true;
                            await client.CreateInstancesAsync(
                                new CreateInstancesRequest { InstanceNames = [odd], AvailabilityZone = zone, BlueprintId = "no-such-blueprint", BundleId = BundleId }, ct).ConfigureAwait(false);

                            return "accepted\nreal Lightsail refuses a blueprint id it does not list; floci does not check";
                        }
                        // What real Lightsail answers: the expected outcome, shown as a successful step.
                        catch (InvalidInputException ex)
                        {
                            return $"{ex.GetType().Name}: {ex.Message}";
                        }
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateInstanceSnapshot",
                    this.Call("CreateInstanceSnapshot", $"client.CreateInstanceSnapshotAsync(new CreateInstanceSnapshotRequest {{ InstanceName = \"{instance}\", InstanceSnapshotName = \"{snapshot}\" }})"),
                    async () =>
                    {
                        try
                        {
                            claimed.Snapshot = true;
                            await client.CreateInstanceSnapshotAsync(
                                new CreateInstanceSnapshotRequest { InstanceName = instance, InstanceSnapshotName = snapshot }, ct).ConfigureAwait(false);

                            return factory.UseEmulator
                                ? "accepted\nfloci answered UnsupportedOperation when this demo was written; it now implements snapshots"
                                : "accepted";
                        }
                        // A documented outcome, not a failure: floci recognises the operation and says so.
                        // It arrives as HTTP 400 with this error code, not as a 501.
                        catch (AmazonLightsailException ex) when (ex.ErrorCode == "UnsupportedOperation")
                        {
                            return $"{ex.ErrorCode}: {ex.Message}";
                        }
                    }).ConfigureAwait(false);
            }
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // An iterator may not yield from a finally, so the step is yielded below.
            if (claimed.Any)
            {
                cleanup.Add(await this.DeleteEverythingAsync(client, claimed, new Names(instance, odd, disk, staticIp, keyPair, snapshot), ct).ConfigureAwait(false));
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
    /// inspects only the outermost exception — cannot classify them on its own. floci answers an
    /// operation it recognises but has not built with HTTP 400 and the error code
    /// <c>UnsupportedOperation</c>, which is the not-implemented outcome here.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                case AmazonServiceException { ErrorCode: "UnsupportedOperation" }:
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

    private static string DescribeOperation(Operation operation)
        => $"{operation.OperationType?.Value} {operation.ResourceName}: {operation.Status?.Value}" +
           (string.IsNullOrEmpty(operation.OperationDetails) ? string.Empty : $" ({operation.OperationDetails})");

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still deletes everything.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// Polls until the instance reports <paramref name="wanted"/>. Real Lightsail goes through
    /// pending, stopping and so on over seconds to minutes; floci is there on the first read, so
    /// against it this never sleeps.
    /// </summary>
    private static async Task<string> WaitForStateAsync(IAmazonLightsail client, string instance, string wanted, CancellationToken ct)
    {
        string state = string.Empty;

        for (int attempt = 0; attempt < 60; attempt++)
        {
            state = (await client.GetInstanceStateAsync(new GetInstanceStateRequest { InstanceName = instance }, ct).ConfigureAwait(false)).State?.Name ?? string.Empty;

            if (state == wanted)
            {
                return state;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"{instance} is {state}, not {wanted}, after two minutes.");
    }

    /// <summary>Polls until a new disk leaves "pending"; real Lightsail will not attach it before then.</summary>
    private static async Task WaitForDiskAsync(IAmazonLightsail client, string disk, CancellationToken ct)
    {
        string state = string.Empty;

        for (int attempt = 0; attempt < 60; attempt++)
        {
            state = (await client.GetDiskAsync(new GetDiskRequest { DiskName = disk }, ct).ConfigureAwait(false)).Disk.State?.Value ?? string.Empty;

            if (state != "pending")
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"{disk} is still {state} after two minutes.");
    }

    private static async Task<List<Blueprint>> ListBlueprintsAsync(IAmazonLightsail client, CancellationToken ct)
    {
        List<Blueprint> all = [];
        string? token = null;

        do
        {
            GetBlueprintsResponse page = await client.GetBlueprintsAsync(new GetBlueprintsRequest { PageToken = token }, ct).ConfigureAwait(false);
            all.AddRange(page.Blueprints ?? []);
            token = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    private static async Task<List<Bundle>> ListBundlesAsync(IAmazonLightsail client, CancellationToken ct)
    {
        List<Bundle> all = [];
        string? token = null;

        do
        {
            GetBundlesResponse page = await client.GetBundlesAsync(new GetBundlesRequest { PageToken = token }, ct).ConfigureAwait(false);
            all.AddRange(page.Bundles ?? []);
            token = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    private static async Task<List<KeyPair>> ListKeyPairsAsync(IAmazonLightsail client, CancellationToken ct)
    {
        List<KeyPair> all = [];
        string? token = null;

        do
        {
            GetKeyPairsResponse page = await client.GetKeyPairsAsync(new GetKeyPairsRequest { PageToken = token }, ct).ConfigureAwait(false);
            all.AddRange(page.KeyPairs ?? []);
            token = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    /// <summary>The wire-level request shown beside an SDK call: AWS JSON 1.1, one POST / per operation, the operation in the target header.</summary>
    private string Call(string operation, string call) => $"POST {factory.ServiceUrl}/\nX-Amz-Target: Lightsail_20161128.{operation}\n{call}";

    /// <summary>
    /// Deletes whatever this run created, looking each one up by its unique name so a resource whose
    /// create response was lost is still found, and one that was never created reads as a truthful
    /// "nothing to remove". Instances go before disks: real Lightsail detaches a disk only from a
    /// stopped instance, and deleting the instance detaches it instead. Real Lightsail also deletes
    /// asynchronously, so each delete is followed by reads until NotFoundException rather than one
    /// read; floci answers it on the first. Every delete is attempted before any failure is
    /// reported, so one stuck delete does not leak the rest.
    /// Uses <see cref="CancellationToken.None"/>: a cancelled run still has resources to delete.
    /// </summary>
    private async Task<DemoStep> DeleteEverythingAsync(IAmazonLightsail client, Claimed claimed, Names names, CancellationToken ct)
    {
        string request = this.Call("DeleteInstance", "client.DeleteInstanceSnapshotAsync, DetachStaticIpAsync, ReleaseStaticIpAsync, DeleteInstanceAsync, DetachDiskAsync, DeleteDiskAsync, DeleteKeyPairAsync — each resource this run created");

        return await RunStepAsync("Delete everything — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<string> removed = [];
            List<string> failures = [];

            // Each removal: the delete call, then reads until one answers NotFoundException — the
            // resource must be gone, not merely the call accepted. Any failure is recorded, never
            // thrown, so the removals after it still run.
            async Task RemoveAsync(string what, string name, Func<Task> delete, Func<Task> read)
            {
                try
                {
                    await delete().ConfigureAwait(false);
                }
                catch (NotFoundException)
                {
                    // Never created, or already gone, which is what this step wants.
                    return;
                }
                catch (Exception ex)
                {
                    failures.Add($"{what} {name}: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                try
                {
                    for (int attempt = 0; attempt < 60; attempt++)
                    {
                        await read().ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromSeconds(2), none).ConfigureAwait(false);
                    }

                    failures.Add($"Delete returned, but {what} {name} can still be read after two minutes.");
                }
                catch (NotFoundException)
                {
                    removed.Add($"{what} {name}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{what} {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Detaching what was never attached is an error that does not matter here.
            async Task IgnoreAsync(Func<Task> operation)
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (AmazonLightsailException)
                {
                    // Not attached or not found: nothing to undo either way.
                }
            }

            if (claimed.Snapshot)
            {
                // floci answers UnsupportedOperation for snapshots, so there is nothing to remove;
                // anywhere they exist, the snapshot gets the same delete-and-confirm as the rest.
                bool exists;

                try
                {
                    await client.GetInstanceSnapshotAsync(new GetInstanceSnapshotRequest { InstanceSnapshotName = names.Snapshot }, none).ConfigureAwait(false);
                    exists = true;
                }
                catch (AmazonLightsailException ex) when (ex is NotFoundException || ex.ErrorCode == "UnsupportedOperation")
                {
                    exists = false;
                }

                if (exists)
                {
                    await RemoveAsync("snapshot", names.Snapshot, () => client.DeleteInstanceSnapshotAsync(new DeleteInstanceSnapshotRequest { InstanceSnapshotName = names.Snapshot }, none), () => client.GetInstanceSnapshotAsync(new GetInstanceSnapshotRequest { InstanceSnapshotName = names.Snapshot }, none)).ConfigureAwait(false);
                }
            }

            if (claimed.StaticIp)
            {
                await IgnoreAsync(() => client.DetachStaticIpAsync(new DetachStaticIpRequest { StaticIpName = names.StaticIp }, none)).ConfigureAwait(false);
                await RemoveAsync("static IP", names.StaticIp, () => client.ReleaseStaticIpAsync(new ReleaseStaticIpRequest { StaticIpName = names.StaticIp }, none), () => client.GetStaticIpAsync(new GetStaticIpRequest { StaticIpName = names.StaticIp }, none)).ConfigureAwait(false);
            }

            foreach (string name in new[] { (claimed.Odd, names.Odd), (claimed.Instance, names.Instance) }.Where(n => n.Item1).Select(n => n.Item2))
            {
                await RemoveAsync("instance", name, () => client.DeleteInstanceAsync(new DeleteInstanceRequest { InstanceName = name }, none), () => client.GetInstanceAsync(new GetInstanceRequest { InstanceName = name }, none)).ConfigureAwait(false);
            }

            if (claimed.Disk)
            {
                // Already detached if its instance was deleted above; this covers an instance that was not.
                await IgnoreAsync(() => client.DetachDiskAsync(new DetachDiskRequest { DiskName = names.Disk }, none)).ConfigureAwait(false);
                await RemoveAsync("disk", names.Disk, () => client.DeleteDiskAsync(new DeleteDiskRequest { DiskName = names.Disk }, none), () => client.GetDiskAsync(new GetDiskRequest { DiskName = names.Disk }, none)).ConfigureAwait(false);
            }

            if (claimed.KeyPair)
            {
                await RemoveAsync("key pair", names.KeyPair, () => client.DeleteKeyPairAsync(new DeleteKeyPairRequest { KeyPairName = names.KeyPair }, none), () => client.GetKeyPairAsync(new GetKeyPairRequest { KeyPairName = names.KeyPair }, none)).ConfigureAwait(false);
            }

            if (failures.Count != 0)
            {
                throw new InvalidOperationException($"{failures.Count} resource(s) not deleted:\n{string.Join("\n", failures)}{cancelled}");
            }

            return removed.Count == 0
                ? $"No resource for this run exists — nothing to remove.{cancelled}"
                : $"Deleted {removed.Count} resource(s), each now answering NotFoundException:\n{string.Join("\n", removed)}{cancelled}";
        }).ConfigureAwait(false);
    }

    /// <summary>The unique names of everything one run creates.</summary>
    private sealed record Names(string Instance, string Odd, string Disk, string StaticIp, string KeyPair, string Snapshot);

    /// <summary>Which of this run's resources have had a create call started.</summary>
    private sealed class Claimed
    {
        public bool Instance { get; set; }

        public bool Odd { get; set; }

        public bool Disk { get; set; }

        public bool StaticIp { get; set; }

        public bool KeyPair { get; set; }

        public bool Snapshot { get; set; }

        public bool Any => this.Instance || this.Odd || this.Disk || this.StaticIp || this.KeyPair || this.Snapshot;
    }
}
