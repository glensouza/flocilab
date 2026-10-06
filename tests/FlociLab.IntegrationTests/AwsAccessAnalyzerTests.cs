using Amazon.AccessAnalyzer;
using Amazon.AccessAnalyzer.Model;
using FlociLab.Aws.AccessAnalyzer;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Testcontainers.Floci;
using Xunit;
using AnalyzerType = Amazon.AccessAnalyzer.Type;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator the
/// AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class AwsAccessAnalyzerTests : IAsyncLifetime
{
    // Pinned to :latest so the tripwire tracks the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private AccessAnalyzerClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new AccessAnalyzerClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new AccessAnalyzerDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new AccessAnalyzerDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListAnalyzers", s.Title),
            s => Assert.Equal("CreateAnalyzer", s.Title),
            s => Assert.Equal("CreateAnalyzer — refused as a duplicate", s.Title),
            s => Assert.Equal("ListAnalyzers — the new analyzer reads back ACTIVE", s.Title),
            s => Assert.Equal("DeleteAnalyzer", s.Title),
            s => Assert.Equal("DeleteAnalyzer — refused once deleted", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_Nothing_Behind()
    {
        AccessAnalyzerDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonAccessAnalyzer client = this.factory.Create();

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListAnalyzersResponse after = await client.ListAnalyzersAsync(new ListAnalyzersRequest(), ct);

        Assert.Empty(after.Analyzers ?? []);
    }

    /// <summary>
    /// The cleanup path on its own: a run stopped after the create — the consumer walking away, as
    /// the page does on dispose — still has an analyzer, and cleanup has to find it by name.
    /// </summary>
    [Fact]
    public async Task Abandoned_Run_Is_Cleaned_Up_By_Name()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonAccessAnalyzer client = this.factory.Create();

        List<DemoStep> steps = [];

        await foreach (DemoStep step in new AccessAnalyzerDemo(this.factory).RunAsync(ct))
        {
            steps.Add(step);

            if (step.Title == "CreateAnalyzer — refused as a duplicate")
            {
                break;
            }
        }

        Assert.Equal("CreateAnalyzer — refused as a duplicate", steps[^1].Title);

        ListAnalyzersResponse after = await client.ListAnalyzersAsync(new ListAnalyzersRequest(), ct);

        Assert.Empty(after.Analyzers ?? []);
    }

    /// <summary>
    /// Tripwire (plan §14). floci 2.1.0 implements CreateAnalyzer, ListAnalyzers and DeleteAnalyzer
    /// and nothing else: every one of these answers <c>UnknownOperationException</c> (HTTP 404, not
    /// 501), and the tag operations refuse a well-formed analyzer ARN. When one starts failing,
    /// upstream shipped it — add a step for it to the sample.
    /// </summary>
    [Fact]
    public async Task Floci_Implements_Only_Create_List_And_Delete()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonAccessAnalyzer client = this.factory.Create();
        string name = $"flocilab-aa-tripwire-{Guid.NewGuid().ToString("N")[..8]}";

        CreateAnalyzerResponse created = await client.CreateAnalyzerAsync(new CreateAnalyzerRequest { AnalyzerName = name, Type = AnalyzerType.ACCOUNT }, ct);

        try
        {
            AmazonAccessAnalyzerException get = await Assert.ThrowsAsync<AmazonAccessAnalyzerException>(
                () => client.GetAnalyzerAsync(new GetAnalyzerRequest { AnalyzerName = name }, ct));
            Assert.Contains("UnknownOperation", get.ErrorCode ?? get.Message, StringComparison.Ordinal);

            AmazonAccessAnalyzerException rules = await Assert.ThrowsAsync<AmazonAccessAnalyzerException>(
                () => client.ListArchiveRulesAsync(new ListArchiveRulesRequest { AnalyzerName = name }, ct));
            Assert.Contains("UnknownOperation", rules.ErrorCode ?? rules.Message, StringComparison.Ordinal);

            AmazonAccessAnalyzerException findings = await Assert.ThrowsAsync<AmazonAccessAnalyzerException>(
                () => client.ListFindingsV2Async(new ListFindingsV2Request { AnalyzerArn = created.Arn }, ct));
            Assert.Contains("UnknownOperation", findings.ErrorCode ?? findings.Message, StringComparison.Ordinal);

            // Not unknown but broken: the route exists and rejects the very ARN CreateAnalyzer returned.
            AmazonAccessAnalyzerException tagged = await Assert.ThrowsAsync<AmazonAccessAnalyzerException>(
                () => client.TagResourceAsync(new TagResourceRequest { ResourceArn = created.Arn, Tags = new Dictionary<string, string> { ["a"] = "b" } }, ct));
            Assert.Contains("Invalid resource ARN", tagged.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DeleteAnalyzerAsync(new DeleteAnalyzerRequest { AnalyzerName = name }, CancellationToken.None);
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
        AccessAnalyzerDemo demo = new(this.factory);
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
        AccessAnalyzerDemo demo = new(new AccessAnalyzerClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
