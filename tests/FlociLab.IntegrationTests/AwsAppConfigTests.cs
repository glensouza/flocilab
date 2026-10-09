using Amazon.AppConfig;
using Amazon.AppConfig.Model;
using FlociLab.Aws.AppConfig;
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
public sealed class AwsAppConfigTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private AppConfigClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new AppConfigClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new AppConfigDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new AppConfigDemo(this.factory));

        Assert.Collection(
            steps,
            s =>
            {
                Assert.Equal("CreateApplication, GetApplication — an application", s.Title);
                Assert.StartsWith("flocilab-", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateEnvironment, ListEnvironments — an environment", s.Title);
                Assert.StartsWith("prod (", s.Response);
                Assert.Contains("READY", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateConfigurationProfile — a hosted profile", s.Title);
                Assert.StartsWith("checkout (", s.Response);
                Assert.EndsWith("): hosted", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateHostedConfigurationVersion, GetHostedConfigurationVersion — two versions of a configuration", s.Title);
                Assert.StartsWith("versions 1, 2; ListHostedConfigurationVersions lists 2", s.Response);
                Assert.Contains("""{"checkout":{"newFlow":true},"timeoutSeconds":30}""", s.Response);
            },
            s =>
            {
                Assert.Equal("ListDeploymentStrategies, GetDeploymentStrategy — the strategies AppConfig ships with", s.Title);
                Assert.Contains("AppConfig.AllAtOnce", s.Response);
            },
            s =>
            {
                Assert.Equal("StartDeployment — a version that was never created", s.Title);

                // Tripwire: floci deploys a version the profile does not have and reports it
                // complete. Real AppConfig refuses. When this starts failing, floci validates it.
                Assert.StartsWith("deployment 1: version 9, COMPLETE", s.Response);
            },
            s =>
            {
                Assert.Equal("StartDeployment, GetDeployment — version 1 into the environment", s.Title);

                // Tripwire: floci completes the deployment at once; real AppConfig reads Deploying
                // and then Baking. When this fails, floci models the rollout.
                Assert.StartsWith("deployment 2: version 1, COMPLETE", s.Response);
            },
            s =>
            {
                Assert.Equal("TagResource, ListTagsForResource — labelling the application", s.Title);
                Assert.Equal("env=lab", s.Response);
            },
            s =>
            {
                Assert.Equal("GetApplication — an id that does not exist", s.Title);

                // Matches real AppConfig.
                Assert.StartsWith("ResourceNotFoundException", s.Response);
            },
            s =>
            {
                Assert.Equal("UpdateApplication — changing its description", s.Title);

                // Tripwire: floci answers an update with HTTP 405 (not 501). When this starts
                // failing, the update operations landed.
                Assert.StartsWith("HTTP 405 MethodNotAllowed", s.Response);
            },
            s =>
            {
                Assert.Equal("DeleteApplication — cleanup", s.Title);

                // The application was found by name and removed, not "nothing to remove".
                Assert.StartsWith("Deleted 1 application(s)", s.Response);

                // Tripwire: floci has no DeleteEnvironment. When this starts failing, it does.
                Assert.Contains("0 environment(s)", s.Response);
                Assert.Contains("floci cannot delete an environment", s.Response);
            });

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves no application behind.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_Nothing_Behind()
    {
        AppConfigDemo demo = new(this.factory);

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

        await foreach (DemoStep step in new AppConfigDemo(this.factory).RunAsync(ct))
        {
            if (step.Title.StartsWith("StartDeployment, GetDeployment", StringComparison.Ordinal))
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
        AppConfigDemo demo = new(new AppConfigClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task AssertAccountIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonAppConfig client = this.factory.Create();

        Assert.DoesNotContain(
            (await client.ListApplicationsAsync(new ListApplicationsRequest(), ct)).Items ?? [],
            a => a.Name.StartsWith("flocilab-", StringComparison.Ordinal));
    }

    private static async Task<List<DemoStep>> RunAsync(AppConfigDemo demo)
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
