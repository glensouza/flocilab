using Amazon.ElasticFileSystem;
using Amazon.ElasticFileSystem.Model;
using Amazon.Runtime;
using FlociLab.Aws.Efs;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Testcontainers.Floci;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator the
/// AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class AwsEfsTests : IAsyncLifetime
{
    // Same reasoning as AwsRoute53Tests: pinned to :latest so the tripwire tracks the same build
    // the AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private EfsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new EfsClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new EfsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new EfsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("DescribeFileSystems — before", s.Title),
            s => Assert.Equal("CreateFileSystem", s.Title),
            s => Assert.Equal("DescribeFileSystems — by id", s.Title),
            s => Assert.Equal("CreateFileSystem — same token refused", s.Title),
            s => Assert.Equal("UpdateFileSystem — provisioned throughput", s.Title),
            s => Assert.Equal("PutLifecycleConfiguration", s.Title),
            s => Assert.Equal("PutBackupPolicy", s.Title),
            s => Assert.Equal("PutFileSystemPolicy", s.Title),
            s => Assert.Equal("CreateMountTarget", s.Title),
            s => Assert.Equal("DescribeMountTargets", s.Title),
            s => Assert.Equal("ModifyMountTargetSecurityGroups", s.Title),
            s => Assert.Equal("CreateAccessPoint", s.Title),
            s => Assert.Equal("DescribeAccessPoints", s.Title),
            s => Assert.Equal("TagResource", s.Title),
            s => Assert.Equal("ListTagsForResource", s.Title),
            s => Assert.Equal("DeleteFileSystem — refused while it has a mount target", s.Title),
            s => Assert.Equal("DeleteAccessPoint", s.Title),
            s => Assert.Equal("DeleteMountTarget", s.Title),
            s => Assert.Equal("DeleteFileSystem", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_Nothing_Behind()
    {
        EfsDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonElasticFileSystem client = this.factory.Create();
        List<string> before = await FileSystemIdsAsync(client, ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        Assert.Equal(before, await FileSystemIdsAsync(client, ct));
    }

    /// <summary>
    /// The cleanup path on its own: a run stopped after the access point — the consumer walking
    /// away, as the page does on dispose — still has a file system with a mount target and an access
    /// point under it, and cleanup has to find it by creation token and take the tree apart bottom-up.
    /// </summary>
    [Fact]
    public async Task Abandoned_Run_Is_Cleaned_Up_By_Creation_Token()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonElasticFileSystem client = this.factory.Create();
        List<string> before = await FileSystemIdsAsync(client, ct);

        List<DemoStep> steps = [];

        // Stop after CreateAccessPoint: the file system holds a mount target and an access point,
        // the state in which a naive delete is refused.
        await foreach (DemoStep step in new EfsDemo(this.factory).RunAsync(ct))
        {
            steps.Add(step);

            if (step.Title == "CreateAccessPoint")
            {
                break;
            }
        }

        Assert.Equal("CreateAccessPoint", steps[^1].Title);

        Assert.Equal(before, await FileSystemIdsAsync(client, ct));
    }

    /// <summary>
    /// floci enforces the creation token, the in-use rule and one mount target per subnet, which
    /// the sample shows, but stores a resource policy that is not JSON and a backup status of
    /// <c>BOGUS</c>, accepts <c>provisioned</c> throughput with no figure, puts two mount targets
    /// in one availability zone, and does not route replication or account preferences
    /// (docs/BLAZOR-PLAN.md §14). When an assertion here starts failing, upstream fixed it: add the
    /// refused step to the sample.
    /// </summary>
    [Fact]
    public async Task Emulator_Behaviours_The_Sample_Documents()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonElasticFileSystem client = this.factory.Create();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string? fileSystemId = null;

        try
        {
            // Real EFS refuses this with BadRequestException: provisioned throughput needs a figure.
            CreateFileSystemResponse created = await client.CreateFileSystemAsync(
                new CreateFileSystemRequest { CreationToken = $"flocilab-trip-{suffix}", ThroughputMode = ThroughputMode.Provisioned }, ct);
            fileSystemId = created.FileSystemId;

            Assert.Equal(ThroughputMode.Provisioned, created.ThroughputMode);
            Assert.Null(created.ProvisionedThroughputInMibps);

            // Real EFS answers InvalidPolicyException for a document that is not a policy.
            PutFileSystemPolicyResponse policy = await client.PutFileSystemPolicyAsync(
                new PutFileSystemPolicyRequest { FileSystemId = fileSystemId, Policy = "not json" }, ct);

            Assert.Equal("not json", policy.Policy);

            // Stored as sent, not only echoed: the read answers the same string.
            DescribeFileSystemPolicyResponse readPolicy = await client.DescribeFileSystemPolicyAsync(
                new DescribeFileSystemPolicyRequest { FileSystemId = fileSystemId }, ct);

            Assert.Equal("not json", readPolicy.Policy);

            // Real EFS accepts only ENABLED or DISABLED here.
            await client.PutBackupPolicyAsync(
                new PutBackupPolicyRequest { FileSystemId = fileSystemId, BackupPolicy = new BackupPolicy { Status = new Status("BOGUS") } }, ct);
            DescribeBackupPolicyResponse backup = await client.DescribeBackupPolicyAsync(new DescribeBackupPolicyRequest { FileSystemId = fileSystemId }, ct);

            Assert.Equal("BOGUS", backup.BackupPolicy?.Status?.Value);

            // Real EFS answers MountTargetConflictException for a second mount target in the same
            // availability zone; floci hands both the same address.
            CreateMountTargetResponse first = await client.CreateMountTargetAsync(new CreateMountTargetRequest { FileSystemId = fileSystemId, SubnetId = "subnet-aaaaaaaa" }, ct);
            CreateMountTargetResponse second = await client.CreateMountTargetAsync(new CreateMountTargetRequest { FileSystemId = fileSystemId, SubnetId = "subnet-bbbbbbbb" }, ct);

            Assert.Equal(first.AvailabilityZoneName, second.AvailabilityZoneName);
            Assert.Equal(first.IpAddress, second.IpAddress);

            // Enforced as real EFS does: one mount target per subnet.
            await Assert.ThrowsAsync<MountTargetConflictException>(() => client.CreateMountTargetAsync(
                new CreateMountTargetRequest { FileSystemId = fileSystemId, SubnetId = "subnet-aaaaaaaa" }, ct));

            // Replication is not implemented: the route is not found, which is not the
            // FileSystemNotFoundException a missing file system gets.
            AmazonServiceException replication = await Assert.ThrowsAnyAsync<AmazonServiceException>(() => client.CreateReplicationConfigurationAsync(
                new CreateReplicationConfigurationRequest { SourceFileSystemId = fileSystemId, Destinations = [new DestinationToCreate { Region = "us-west-2" }] }, ct));

            Assert.IsNotType<FileSystemNotFoundException>(replication);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, replication.StatusCode);

            // Account preferences are not routed either.
            AmazonServiceException preferences = await Assert.ThrowsAnyAsync<AmazonServiceException>(() => client.DescribeAccountPreferencesAsync(
                new DescribeAccountPreferencesRequest(), ct));

            Assert.Equal(System.Net.HttpStatusCode.NotFound, preferences.StatusCode);
        }
        finally
        {
            // Best effort: a cleanup failure here must not replace the assertion that says which
            // tripwire flipped. The container is thrown away with the class either way.
            if (fileSystemId is not null)
            {
                try
                {
                    DescribeMountTargetsResponse targets = await client.DescribeMountTargetsAsync(new DescribeMountTargetsRequest { FileSystemId = fileSystemId }, CancellationToken.None);

                    foreach (MountTargetDescription target in targets.MountTargets ?? [])
                    {
                        await client.DeleteMountTargetAsync(new DeleteMountTargetRequest { MountTargetId = target.MountTargetId }, CancellationToken.None);
                    }

                    await client.DeleteFileSystemAsync(new DeleteFileSystemRequest { FileSystemId = fileSystemId }, CancellationToken.None);
                }
                catch (AmazonServiceException)
                {
                }
            }
        }
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        EfsDemo demo = new(this.factory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);
            }
        });

        Assert.DoesNotContain(steps, s => !s.Succeeded);
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        EfsDemo demo = new(new EfsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    // Every page, so a leak on page two cannot hide behind a clean page one.
    private static async Task<List<string>> FileSystemIdsAsync(IAmazonElasticFileSystem client, CancellationToken ct)
    {
        List<string> ids = [];
        string? marker = null;

        do
        {
            DescribeFileSystemsResponse page = await client.DescribeFileSystemsAsync(new DescribeFileSystemsRequest { Marker = marker }, ct);
            ids.AddRange((page.FileSystems ?? []).Select(f => f.FileSystemId));
            marker = page.NextMarker;
        }
        while (!string.IsNullOrEmpty(marker));

        ids.Sort(StringComparer.Ordinal);

        return ids;
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
