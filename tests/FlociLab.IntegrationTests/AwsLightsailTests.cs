using Amazon.Lightsail;
using Amazon.Lightsail.Model;
using FlociLab.Aws.Lightsail;
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
public sealed class AwsLightsailTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private LightsailClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new LightsailClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new LightsailDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new LightsailDemo(this.factory));

        Assert.Collection(
            steps,
            s => Assert.Equal("GetBlueprints, GetBundles — the catalog", s.Title),
            s => Assert.Equal("CreateInstances — a nano Amazon Linux instance", s.Title),
            s => Assert.Equal("GetInstance — what was created", s.Title),
            s => Assert.Equal("StopInstance, StartInstance, GetInstanceState", s.Title),
            s => Assert.Equal("OpenInstancePublicPorts, GetInstancePortStates", s.Title),
            s => Assert.Equal("AllocateStaticIp, AttachStaticIp, GetStaticIp", s.Title),
            s => Assert.Equal("CreateDisk, AttachDisk, GetDisk", s.Title),
            s => Assert.Equal("CreateKeyPair, GetKeyPairs", s.Title),
            s => Assert.Equal("CreateInstances — a name that already exists", s.Title),
            s =>
            {
                Assert.Equal("CreateInstances — a blueprint that does not exist", s.Title);

                // Tripwire: floci does not check the blueprint id; real Lightsail refuses it.
                Assert.StartsWith("accepted", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateInstanceSnapshot", s.Title);

                // Tripwire: floci answers UnsupportedOperation. When this starts failing,
                // snapshots landed and the demo can grow a snapshot step.
                Assert.StartsWith("UnsupportedOperation", s.Response);
            },
            s => Assert.Equal("Delete everything — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves nothing behind — including the instance made from a blueprint that does not exist.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_Nothing_Behind()
    {
        LightsailDemo demo = new(this.factory);

        for (int run = 0; run < 2; run++)
        {
            List<DemoStep> steps = await RunAsync(demo);

            Assert.All(steps, s => Assert.True(s.Succeeded, $"run {run}, {s.Title}: {s.Error}"));
        }

        await this.AssertAccountIsEmptyAsync();
    }

    /// <summary>A consumer that stops early (the page navigated away) still has everything deleted by the finally.</summary>
    [Fact]
    public async Task Abandoned_Run_Deletes_What_It_Created()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await foreach (DemoStep step in new LightsailDemo(this.factory).RunAsync(ct))
        {
            if (step.Title == "CreateDisk, AttachDisk, GetDisk")
            {
                break;
            }
        }

        await this.AssertAccountIsEmptyAsync();
    }

    /// <summary>A stopped emulator must read as Unreachable, not as a broken sample.</summary>
    [Fact]
    public async Task Probe_Against_A_Stopped_Emulator_Reports_Unreachable()
    {
        LightsailDemo demo = new(new LightsailClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task AssertAccountIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonLightsail client = this.factory.Create();

        Assert.Empty((await client.GetInstancesAsync(new GetInstancesRequest(), ct)).Instances ?? []);
        Assert.Empty((await client.GetDisksAsync(new GetDisksRequest(), ct)).Disks ?? []);
        Assert.Empty((await client.GetStaticIpsAsync(new GetStaticIpsRequest(), ct)).StaticIps ?? []);
        Assert.Empty((await client.GetKeyPairsAsync(new GetKeyPairsRequest(), ct)).KeyPairs ?? []);
    }

    private static async Task<List<DemoStep>> RunAsync(LightsailDemo demo)
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in demo.RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        return steps;
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
