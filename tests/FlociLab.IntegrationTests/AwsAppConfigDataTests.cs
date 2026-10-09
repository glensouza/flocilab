using Amazon.AppConfig;
using Amazon.AppConfig.Model;
using FlociLab.Aws.AppConfigData;
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
public sealed class AwsAppConfigDataTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private AppConfigDataClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new AppConfigDataClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new AppConfigDataDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new AppConfigDataDemo(this.factory));

        Assert.Collection(
            steps,
            s =>
            {
                Assert.Equal("Seeding with AWSSDK.AppConfig — an application, a profile and version 1 deployed", s.Title);
                Assert.StartsWith("flocilab-", s.Response);
                Assert.Contains("version 1 published and deployed: COMPLETE", s.Response);
            },
            s =>
            {
                Assert.Equal("StartConfigurationSession — opening a session", s.Title);
                Assert.StartsWith("session open, initial token of ", s.Response);
            },
            s =>
            {
                Assert.Equal("GetLatestConfiguration — the first poll", s.Title);
                Assert.StartsWith("version 1 (application/json): {\"checkout\":{\"newFlow\":false}", s.Response);

                // The session asked for a 60 second minimum and the answer carries it back.
                Assert.Contains("next poll in 60 s", s.Response);
            },
            s =>
            {
                Assert.Equal("GetLatestConfiguration — polling again, nothing has changed", s.Title);

                // An empty body and no version label: the deployed version is the one already held.
                Assert.StartsWith("0 bytes, version label ''", s.Response);
            },
            s =>
            {
                Assert.Equal("GetLatestConfiguration — after deploying version 2", s.Title);
                Assert.Contains("version 2: {\"checkout\":{\"newFlow\":true}", s.Response);
            },
            s =>
            {
                Assert.Equal("GetLatestConfiguration — a token that was already used", s.Title);

                // Tripwire: floci rejects a token a poll already spent. When this starts failing,
                // it accepts one.
                Assert.StartsWith("BadRequestException", s.Response);
            },
            s =>
            {
                Assert.Equal("StartConfigurationSession — a profile that does not exist", s.Title);

                // Matches real AppConfigData.
                Assert.StartsWith("ResourceNotFoundException", s.Response);
            },
            s =>
            {
                Assert.Equal("DeleteApplication — cleanup", s.Title);

                // The application was found by name and removed, not "nothing to remove".
                Assert.StartsWith("Deleted 1 application(s)", s.Response);
                Assert.Contains("2 hosted version(s)", s.Response);

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
        AppConfigDataDemo demo = new(this.factory);

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

        await foreach (DemoStep step in new AppConfigDataDemo(this.factory).RunAsync(ct))
        {
            if (step.Title.StartsWith("GetLatestConfiguration — the first poll", StringComparison.Ordinal))
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
        AppConfigDataDemo demo = new(new AppConfigDataClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// No application of any run is left, across every page. The environments are not checked:
    /// floci cannot delete one, so each run leaves its <c>prod</c> listed under the deleted
    /// application id, and the round-trip test pins that as a tripwire instead.
    /// </summary>
    private async Task AssertAccountIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonAppConfig client = this.factory.CreateAppConfig();
        List<string> names = [];
        string? next = null;

        do
        {
            ListApplicationsResponse page = await client.ListApplicationsAsync(new ListApplicationsRequest { NextToken = next }, ct);

            names.AddRange((page.Items ?? []).Select(a => a.Name));
            next = page.NextToken;
        }
        while (!string.IsNullOrEmpty(next));

        Assert.DoesNotContain(names, n => n.StartsWith("flocilab-", StringComparison.Ordinal));
    }

    private static async Task<List<DemoStep>> RunAsync(AppConfigDataDemo demo)
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
