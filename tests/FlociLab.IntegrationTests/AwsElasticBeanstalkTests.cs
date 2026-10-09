using Amazon.ElasticBeanstalk;
using Amazon.ElasticBeanstalk.Model;
using FlociLab.Aws.ElasticBeanstalk;
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
public sealed class AwsElasticBeanstalkTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private ElasticBeanstalkClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new ElasticBeanstalkClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new ElasticBeanstalkDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new ElasticBeanstalkDemo(this.factory));

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateApplication, DescribeApplications — a home for versions and environments", s.Title),
            s => Assert.Equal("CreateApplicationVersion, DescribeApplicationVersions — version 1.0.0, no source bundle", s.Title),
            s => Assert.Equal("ListAvailableSolutionStacks — what an environment can run", s.Title),
            s =>
            {
                Assert.StartsWith("CreateEnvironment — 1.0.0 on ", s.Title);

                // Tripwire: floci launches nothing and reports the environment Ready at once; real
                // Elastic Beanstalk reads Launching. When this fails, floci models the launch.
                Assert.Contains(": Ready, Green, ", s.Response);
            },
            s =>
            {
                Assert.Equal("DescribeApplications, DescribeEnvironments — the resource tree", s.Title);
                Assert.Contains("application flocilab-", s.Response);
                Assert.Contains("version 1.0.0", s.Response);
                Assert.Contains("environment fl-", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateConfigurationTemplate — saving an environment's settings", s.Title);

                // Tripwire: floci answers UnsupportedOperation (HTTP 400, not 501). When this starts
                // failing, configuration templates landed.
                Assert.StartsWith("UnsupportedOperation", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateApplication — a name that is taken", s.Title);

                // Matches real Elastic Beanstalk.
                Assert.StartsWith("InvalidParameterValue", s.Response);
            },
            s =>
            {
                Assert.Equal("DeleteApplication — while it still has an environment", s.Title);

                // Matches real Elastic Beanstalk.
                Assert.StartsWith("InvalidParameterValue", s.Response);
            },
            s =>
            {
                Assert.Equal("Delete everything — cleanup", s.Title);

                // Both resources were found by name and removed, not "nothing to remove".
                Assert.StartsWith("Deleted 2 resource(s)", s.Response);
            });

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves nothing behind.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_Nothing_Behind()
    {
        ElasticBeanstalkDemo demo = new(this.factory);

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

        await foreach (DemoStep step in new ElasticBeanstalkDemo(this.factory).RunAsync(ct))
        {
            if (step.Title.StartsWith("CreateEnvironment", StringComparison.Ordinal))
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
        ElasticBeanstalkDemo demo = new(new ElasticBeanstalkClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task AssertAccountIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonElasticBeanstalk client = this.factory.Create();

        Assert.Empty((await client.DescribeApplicationsAsync(new DescribeApplicationsRequest(), ct)).Applications ?? []);
        Assert.DoesNotContain(
            (await client.DescribeEnvironmentsAsync(new DescribeEnvironmentsRequest(), ct)).Environments ?? [],
            e => e.Status != EnvironmentStatus.Terminated);
    }

    private static async Task<List<DemoStep>> RunAsync(ElasticBeanstalkDemo demo)
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
