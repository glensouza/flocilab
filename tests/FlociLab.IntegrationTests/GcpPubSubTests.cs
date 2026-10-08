using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using FlociLab.Gcp.PubSub;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-gcp per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class GcpPubSubTests : IAsyncLifetime
{
    private const int FlociGcpPort = 4588;

    // A plain ContainerBuilder rather than the FlociBuilder the S3/SQS tests use, for the same
    // reason GcpStorageTests does: Testcontainers.Floci 4.16.0 hardcodes 4566, and floci-gcp
    // listens on 4588 with its health path namespaced as /_floci-gcp/health.
    private readonly IContainer flociGcp = new ContainerBuilder("floci/floci-gcp:latest")
        .WithPortBinding(FlociGcpPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci-gcp/health").ForPort(FlociGcpPort)))
        .Build();

    private PubSubClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociGcp.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new PubSubClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociGcp.DisposeAsync();

    // Hostname rather than "localhost" deliberately — see GcpStorageTests for why: Testcontainers
    // hands back an address, and on a Windows host "localhost" resolves to ::1 first while the
    // published port is IPv4-only, costing a ~2 s dead IPv6 attempt on every first connection.
    private string Endpoint => $"http://{this.flociGcp.Hostname}:{this.flociGcp.GetMappedPublicPort(FlociGcpPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new PubSubDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new PubSubDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListTopics — before", s.Title),
            s => Assert.Equal("CreateTopic", s.Title),
            s => Assert.Equal("CreateSubscription", s.Title),
            s => Assert.Equal("Publish", s.Title),
            s => Assert.Equal("Pull", s.Title),
            s => Assert.Equal("Acknowledge", s.Title),
            s => Assert.Equal("DeleteSubscription — cleanup", s.Title),
            s => Assert.Equal("DeleteTopic — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "Pull").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Subscriptions_Behind()
    {
        PubSubDemo demo = new(this.factory);
        PubSubQueue queue = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IReadOnlyList<QueueInfo> before = await queue.ListQueuesAsync(ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<QueueInfo> after = await queue.ListQueuesAsync(ct);

        Assert.Equal(before.Select(q => q.Name).Order(), after.Select(q => q.Name).Order());
    }

    /// <summary>The capability the queue comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task Queue_Capability_RoundTrips()
    {
        PubSubQueue queue = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        await queue.CreateQueueAsync(name, ct);

        try
        {
            Assert.Contains(name, (await queue.ListQueuesAsync(ct)).Select(q => q.Name));

            await queue.SendMessageAsync(name, "capability round-trip", ct);

            IReadOnlyList<QueueMessage> received = await queue.ReceiveMessagesAsync(name, 1, ct);

            Assert.Single(received);
            Assert.Equal("capability round-trip", received[0].Body);

            // ReceiveMessagesAsync acks what it returns (interface contract), so a second receive
            // on the same subscription finds nothing left.
            Assert.Empty(await queue.ReceiveMessagesAsync(name, 1, ct));
        }
        finally
        {
            await queue.DeleteQueueAsync(name, CancellationToken.None);
        }

        Assert.DoesNotContain(name, (await queue.ListQueuesAsync(ct)).Select(q => q.Name));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        PubSubDemo demo = new(this.factory);
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
    /// before the run starts — the case that actually happens when a user navigates away mid-run,
    /// and the one the test above cannot reach.
    ///
    /// <para>
    /// It is a distinct case because gRPC reports the two differently: a token already cancelled
    /// throws <see cref="OperationCanceledException"/>, but one cancelled mid-call surfaces as
    /// <c>RpcException(StatusCode.Cancelled)</c>. Without <c>PubSubDemo</c> translating that back,
    /// every remaining step renders red and <c>CoverageMatrix</c>'s ProbeTimeout reads a wedged
    /// emulator as <c>Error</c> instead of <c>Unreachable</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Run_Cancelled_Mid_Flight_Still_Throws_Rather_Than_Failing_Steps()
    {
        PubSubDemo demo = new(this.factory);
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
        PubSubDemo demo = new(this.factory);

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
        PubSubDemo demo = new(new PubSubClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static GcpEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Gcp = new GcpEmulatorOptions { Endpoint = endpoint } }));
}
