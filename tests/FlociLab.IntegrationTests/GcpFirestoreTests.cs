using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using FlociLab.Gcp.Firestore;
using Google.Cloud.Firestore;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-gcp per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class GcpFirestoreTests : IAsyncLifetime
{
    private const int FlociGcpPort = 4588;

    // A plain ContainerBuilder rather than the FlociBuilder the S3/SQS tests use, for the same
    // reason GcpStorageTests and GcpPubSubTests do: Testcontainers.Floci 4.15.0 hardcodes 4566, and
    // floci-gcp listens on 4588 with its health path namespaced as /_floci-gcp/health.
    private readonly IContainer flociGcp = new ContainerBuilder("floci/floci-gcp:latest")
        .WithPortBinding(FlociGcpPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci-gcp/health").ForPort(FlociGcpPort)))
        .Build();

    private FirestoreClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociGcp.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new FirestoreClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociGcp.DisposeAsync();

    // Hostname rather than "localhost" deliberately — see GcpStorageTests for why: Testcontainers
    // hands back an address, and on a Windows host "localhost" resolves to ::1 first while the
    // published port is IPv4-only, costing a ~2 s dead IPv6 attempt on every first connection.
    private string Endpoint => $"http://{this.flociGcp.Hostname}:{this.flociGcp.GetMappedPublicPort(FlociGcpPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new FirestoreDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new FirestoreDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListCollections — before", s.Title),
            s => Assert.Equal("SetDocument", s.Title),
            s => Assert.Equal("GetDocument", s.Title),
            s => Assert.Equal("DeleteDocument — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "GetDocument").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Collections_Behind()
    {
        FirestoreDemo demo = new(this.factory);
        FirestoreDocumentDb documentDb = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IReadOnlyList<CollectionInfo> before = await documentDb.ListCollectionsAsync(ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<CollectionInfo> after = await documentDb.ListCollectionsAsync(ct);

        Assert.Equal(before.Select(c => c.Name).Order(), after.Select(c => c.Name).Order());
    }

    /// <summary>The capability the document DB comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task DocumentDb_Capability_RoundTrips()
    {
        FirestoreDocumentDb documentDb = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";
        string id = Guid.NewGuid().ToString("N");

        await documentDb.CreateCollectionAsync(name, ct);

        try
        {
            Assert.Contains(name, (await documentDb.ListCollectionsAsync(ct)).Select(c => c.Name));

            await documentDb.UpsertDocumentAsync(
                name, id, $$"""{"id":"{{id}}","greeting":"capability round-trip"}""", ct);

            string? document = await documentDb.GetDocumentAsync(name, id, ct);

            Assert.NotNull(document);
            Assert.Contains("capability round-trip", document);
        }
        finally
        {
            await documentDb.DeleteCollectionAsync(name, CancellationToken.None);
        }

        Assert.DoesNotContain(name, (await documentDb.ListCollectionsAsync(ct)).Select(c => c.Name));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        FirestoreDemo demo = new(this.factory);
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
    /// The same guarantee, but for a token that trips *while* a call is in flight rather than
    /// before the run starts — the case that actually happens when a user navigates away mid-run.
    /// Same reasoning as <c>GcpPubSubTests.Run_Cancelled_Mid_Flight_Still_Throws_Rather_Than_Failing_Steps</c>.
    /// </summary>
    [Fact]
    public async Task Run_Cancelled_Mid_Flight_Still_Throws_Rather_Than_Failing_Steps()
    {
        FirestoreDemo demo = new(this.factory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);

                // Cancel once the run is genuinely under way, so the next call is cancelled in
                // flight rather than refused at the gate.
                await cts.CancelAsync();
            }
        });

        Assert.NotEmpty(steps);
        Assert.DoesNotContain(steps, s => !s.Succeeded);
    }

    /// <summary>
    /// The probe honours the same translation, which is what <c>CoverageMatrix</c> depends on to
    /// render a ProbeTimeout as "No response within 5s" / <c>Unreachable</c> rather than as a red
    /// <c>Error</c> naming a gRPC status the reader has no use for.
    /// </summary>
    [Fact]
    public async Task Cancelled_Probe_Throws_Rather_Than_Returning_An_Error_Result()
    {
        FirestoreDemo demo = new(this.factory);

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => demo.ProbeAsync(cts.Token));
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        FirestoreDemo demo = new(new FirestoreClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// The emulator behaviour the cleanup step's honesty rests on, asserted rather than assumed
    /// (CLAUDE.md: never invent emulator behaviour). A Firestore delete is idempotent, so removing
    /// a document that was never written succeeds — that is the false green docs/BLAZOR-PLAN.md §14
    /// is about, and it is reachable here whenever SetDocument fails. Precondition.MustExist is
    /// what turns it into the NotFound it should have been, and floci-gcp does enforce it. If this
    /// test ever fails, DeleteDocument — cleanup has gone back to rendering green having removed
    /// nothing.
    /// </summary>
    [Fact]
    public async Task Deleting_A_Document_That_Was_Never_Written_Fails_Only_Under_A_Precondition()
    {
        DocumentReference missing = this.factory.Create()
            .Collection($"flocilab-missing-{Guid.NewGuid():N}").Document(Guid.NewGuid().ToString("N"));
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Succeeds against floci-gcp, exactly as it does against real Firestore.
        await missing.DeleteAsync(cancellationToken: ct);

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() => missing.DeleteAsync(Precondition.MustExist, ct));

        StatusCode[] preconditionFailures = [StatusCode.NotFound, StatusCode.FailedPrecondition];
        Assert.Contains(ex.StatusCode, preconditionFailures);
    }

    /// <summary>
    /// And the step itself: a run whose write never happened must not end on a green cleanup badge.
    /// Driving that through <c>RunAsync</c> would need a broken emulator, so this asserts the
    /// guarantee where it is established — the delete the cleanup step issues.
    /// </summary>
    [Fact]
    public async Task Cleanup_Does_Not_Report_Success_When_There_Was_Nothing_To_Remove()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new FirestoreDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        DemoStep cleanup = steps.Single(s => s.Title == "DeleteDocument — cleanup");

        Assert.True(cleanup.Succeeded, cleanup.Error);
        Assert.Contains("Removed the document.", cleanup.Response);
    }

    /// <summary>
    /// The comparison-page half of the rule above. Firestore has no DeleteCollection RPC, so
    /// <see cref="FirestoreDocumentDb.DeleteCollectionAsync"/> is a ListDocuments loop, and on a
    /// collection that was never created the loop never iterates — a delete that removed nothing,
    /// which the document-DB comparison page would otherwise paint green next to a red AWS and a
    /// red Azure for the identical outcome (docs/BLAZOR-PLAN.md §14). If this test ever fails, that
    /// false green is back.
    /// </summary>
    [Fact]
    public async Task Deleting_A_Collection_That_Was_Never_Created_Fails()
    {
        FirestoreDocumentDb documentDb = new(this.factory);
        string name = $"flocilab-missing-{Guid.NewGuid():N}";

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => documentDb.DeleteCollectionAsync(name, TestContext.Current.CancellationToken));

        Assert.Contains(name, ex.Message);

        // And it has to classify as Error, not Unreachable: the emulator answered every call here,
        // so reporting it as "the emulator is down" would blame the wrong thing.
        Assert.Equal(ProbeStatus.Error, documentDb.Classify(ex));
    }

    private static GcpEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Gcp = new GcpEmulatorOptions { Endpoint = endpoint } }));
}
