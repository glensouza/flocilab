using System.Text;
using FlociLab.Aws.S3;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
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
public sealed class AwsS3Tests : IAsyncLifetime
{
    // The image is explicit because Testcontainers.Floci 4.16.0 would otherwise pick
    // floci/floci:1.5.13, while the AppHost and the README's Compose stack both run :latest.
    // Tests that exercise an older build than the lab does would not be the tripwire section 13
    // needs them to be, so they are pinned together. (The module's parameterless constructor is
    // obsolete in 4.16.0, as it was in 4.15.0 for exactly this reason.)
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private S3ClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new S3ClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new S3Demo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new S3Demo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListBuckets — before", s.Title),
            s => Assert.Equal("CreateBucket", s.Title),
            s => Assert.Equal("PutObject", s.Title),
            s => Assert.Equal("ListObjectsV2", s.Title),
            s => Assert.Equal("GetObject", s.Title),
            s => Assert.Equal("DeleteObject", s.Title),
            s => Assert.Equal("DeleteBucket — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "GetObject").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Buckets_Behind()
    {
        S3Demo demo = new(this.factory);
        S3ObjectStore store = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IReadOnlyList<ContainerInfo> before = await store.ListContainersAsync(ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<ContainerInfo> after = await store.ListContainersAsync(ct);

        Assert.Equal(before.Select(c => c.Name).Order(), after.Select(c => c.Name).Order());
    }

    /// <summary>The capability the object-storage comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task ObjectStore_Capability_RoundTrips()
    {
        S3ObjectStore store = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string bucket = $"flocilab-cap-{Guid.NewGuid():N}"[..24];

        await store.CreateContainerAsync(bucket, ct);

        try
        {
            Assert.Contains(bucket, (await store.ListContainersAsync(ct)).Select(c => c.Name));

            using MemoryStream payload = new(Encoding.UTF8.GetBytes("capability round-trip"));
            await store.PutObjectAsync(bucket, "probe.txt", payload, ct);

            using Stream fetched = await store.GetObjectAsync(bucket, "probe.txt", ct);
            using StreamReader reader = new(fetched);

            Assert.Equal("capability round-trip", await reader.ReadToEndAsync(ct));
        }
        finally
        {
            await store.DeleteContainerAsync(bucket, CancellationToken.None);
        }

        Assert.DoesNotContain(bucket, (await store.ListContainersAsync(ct)).Select(c => c.Name));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render six red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        S3Demo demo = new(this.factory);
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
        S3Demo demo = new(new S3ClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
