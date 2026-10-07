using Amazon.AutoScaling;
using Amazon.AutoScaling.Model;
using FlociLab.Aws.AutoScaling;
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
public sealed class AwsAutoScalingTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private AutoScalingClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new AutoScalingClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new AutoScalingDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new AutoScalingDemo(this.factory));

        Assert.Collection(
            steps,
            s => Assert.Equal("DescribeAccountLimits, DescribeAdjustmentTypes — the catalog", s.Title),
            s => Assert.Equal("CreateLaunchConfiguration — what each instance would be", s.Title),
            s => Assert.Equal("CreateAutoScalingGroup — a group of zero", s.Title),
            s => Assert.Equal("PutScalingPolicy ×2, DescribePolicies — a step and a target-tracking policy", s.Title),
            s => Assert.Equal("PutLifecycleHook, DescribeLifecycleHooks", s.Title),
            s => Assert.Equal("PutScheduledUpdateGroupAction, DescribeScheduledActions", s.Title),
            s => Assert.Equal("UpdateAutoScalingGroup, SuspendProcesses, ResumeProcesses", s.Title),
            s =>
            {
                Assert.Equal("DescribeAutoScalingGroups — the resource tree", s.Title);
                Assert.Contains("auto scaling group flocilab-", s.Response);
                Assert.Contains("policy cpu-50: TargetTrackingScaling", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateAutoScalingGroup — a name that already exists", s.Title);
                Assert.StartsWith("AlreadyExistsException", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateAutoScalingGroup — MinSize above MaxSize", s.Title);

                // Tripwire: floci does not check the range; real Auto Scaling answers ValidationError.
                Assert.StartsWith("accepted", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateAutoScalingGroup — a launch configuration that does not exist", s.Title);

                // Tripwire: floci does not look the launch configuration up; real Auto Scaling answers ValidationError.
                Assert.StartsWith("accepted", s.Response);
            },
            s =>
            {
                Assert.Equal("DeleteLaunchConfiguration — while a group still uses it", s.Title);

                // Tripwire: floci deletes it; real Auto Scaling answers ResourceInUse.
                Assert.StartsWith("accepted", s.Response);
            },
            s => Assert.Equal("Delete everything — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves nothing behind — including the two groups floci should have refused.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_Nothing_Behind()
    {
        AutoScalingDemo demo = new(this.factory);

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

        await foreach (DemoStep step in new AutoScalingDemo(this.factory).RunAsync(ct))
        {
            if (step.Title == "PutLifecycleHook, DescribeLifecycleHooks")
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
        AutoScalingDemo demo = new(new AutoScalingClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task AssertAccountIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonAutoScaling client = this.factory.Create();

        Assert.Empty((await client.DescribeAutoScalingGroupsAsync(new DescribeAutoScalingGroupsRequest(), ct)).AutoScalingGroups ?? []);
        Assert.Empty((await client.DescribeLaunchConfigurationsAsync(new DescribeLaunchConfigurationsRequest(), ct)).LaunchConfigurations ?? []);
    }

    private static async Task<List<DemoStep>> RunAsync(AutoScalingDemo demo)
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
