using Amazon.CodeGuruReviewer;
using Amazon.CodeGuruReviewer.Model;
using FlociLab.Aws.CodeGuruReviewer;
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
public sealed class AwsCodeGuruReviewerTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private CodeGuruReviewerClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new CodeGuruReviewerClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new CodeGuruReviewerDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new CodeGuruReviewerDemo(this.factory));

        Assert.Collection(
            steps,
            s =>
            {
                Assert.Equal("AssociateRepository, DescribeRepositoryAssociation — a CodeCommit repository", s.Title);

                // Tripwire: floci never reads the repository and reports the association Associated
                // at once; real CodeGuru Reviewer reads Associating. When this fails, floci models it.
                Assert.StartsWith("flocilab-", s.Response);
                Assert.Contains(": Associated, CodeCommit, owner ", s.Response);
            },
            s =>
            {
                Assert.Equal("ListRepositoryAssociations — finding it by name", s.Title);
                Assert.StartsWith("flocilab-", s.Response);
            },
            s =>
            {
                Assert.Equal("TagResource, ListTagsForResource — labelling the association", s.Title);
                Assert.Equal("env=lab", s.Response);
            },
            s =>
            {
                Assert.Equal("DescribeRepositoryAssociation — an ARN that does not exist", s.Title);

                // Matches real CodeGuru Reviewer.
                Assert.StartsWith("NotFoundException", s.Response);
            },
            s =>
            {
                Assert.Equal("ListCodeReviews — the analyses CodeGuru Reviewer has run", s.Title);

                // Tripwire: floci answers UnknownOperationException (HTTP 404, not 501). When this
                // starts failing, code reviews landed.
                Assert.StartsWith("UnknownOperationException", s.Response);
            },
            s =>
            {
                Assert.Equal("DisassociateRepository — cleanup", s.Title);

                // The association was found by name and removed, not "nothing to remove".
                Assert.StartsWith("Disassociated 1 association(s)", s.Response);
            });

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves nothing behind.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_Nothing_Behind()
    {
        CodeGuruReviewerDemo demo = new(this.factory);

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

        await foreach (DemoStep step in new CodeGuruReviewerDemo(this.factory).RunAsync(ct))
        {
            if (step.Title.StartsWith("TagResource", StringComparison.Ordinal))
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
        CodeGuruReviewerDemo demo = new(new CodeGuruReviewerClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task AssertAccountIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonCodeGuruReviewer client = this.factory.Create();

        Assert.DoesNotContain(
            (await client.ListRepositoryAssociationsAsync(new ListRepositoryAssociationsRequest(), ct)).RepositoryAssociationSummaries ?? [],
            a => a.State != RepositoryAssociationState.Disassociated);
    }

    private static async Task<List<DemoStep>> RunAsync(CodeGuruReviewerDemo demo)
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
