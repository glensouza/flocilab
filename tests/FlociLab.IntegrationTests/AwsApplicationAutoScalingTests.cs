using Amazon.ApplicationAutoScaling;
using Amazon.ApplicationAutoScaling.Model;
using FlociLab.Aws.ApplicationAutoScaling;
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
public sealed class AwsApplicationAutoScalingTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private ApplicationAutoScalingClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new ApplicationAutoScalingClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new ApplicationAutoScalingDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new ApplicationAutoScalingDemo(this.factory));

        Assert.Collection(
            steps,
            s =>
            {
                Assert.Equal("RegisterScalableTarget — a table's write capacity, 1 to 10 units", s.Title);

                // Tripwire: the table does not exist and floci registers it anyway; real Application
                // Auto Scaling looks it up and answers ValidationException. When this fails, floci
                // started validating resource ids and the sample needs a real table.
                Assert.True(s.Succeeded, $"floci refused a made-up resource id: {s.Error}");
            },
            s => Assert.Equal("PutScalingPolicy, DescribeScalingPolicies — track 70% write utilization", s.Title),
            s =>
            {
                Assert.Equal("DescribeScalableTargets, DescribeScalingPolicies — the resource tree", s.Title);
                Assert.Contains("scalable target dynamodb/table/flocilab-", s.Response);
                Assert.Contains("policy write-70: TargetTrackingScaling", s.Response);
            },
            s => Assert.Equal("DescribeScalingActivities — what the policy has done so far", s.Title),
            s =>
            {
                Assert.Equal("PutScheduledAction — a nightly shrink", s.Title);

                // Tripwire: floci answers UnsupportedOperation (HTTP 400, not 501). When this starts
                // failing, scheduled actions landed.
                Assert.StartsWith("UnsupportedOperation", s.Response);
            },
            s =>
            {
                Assert.Equal("RegisterScalableTarget — MinCapacity above MaxCapacity", s.Title);

                // Tripwire: floci does not check the range; real Application Auto Scaling answers ValidationException.
                Assert.StartsWith("accepted", s.Response);
            },
            s =>
            {
                Assert.Equal("DeregisterScalableTarget — a target that was never registered", s.Title);

                // Matches real Application Auto Scaling.
                Assert.StartsWith("ObjectNotFoundException", s.Response);
            },
            s => Assert.Equal("Delete everything — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves nothing behind — including the target floci should have refused.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_Nothing_Behind()
    {
        ApplicationAutoScalingDemo demo = new(this.factory);

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

        await foreach (DemoStep step in new ApplicationAutoScalingDemo(this.factory).RunAsync(ct))
        {
            if (step.Title.StartsWith("PutScalingPolicy", StringComparison.Ordinal))
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
        ApplicationAutoScalingDemo demo = new(new ApplicationAutoScalingClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task AssertAccountIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonApplicationAutoScaling client = this.factory.Create();

        Assert.Empty((await client.DescribeScalableTargetsAsync(new DescribeScalableTargetsRequest { ServiceNamespace = ServiceNamespace.Dynamodb }, ct)).ScalableTargets ?? []);
        Assert.Empty((await client.DescribeScalingPoliciesAsync(new DescribeScalingPoliciesRequest { ServiceNamespace = ServiceNamespace.Dynamodb }, ct)).ScalingPolicies ?? []);
    }

    private static async Task<List<DemoStep>> RunAsync(ApplicationAutoScalingDemo demo)
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
