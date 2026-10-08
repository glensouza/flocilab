using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using FlociLab.Gcp.SecretManager;
using Google.Cloud.SecretManager.V1;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-gcp per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class GcpSecretManagerTests : IAsyncLifetime
{
    private const int FlociGcpPort = 4588;

    // A plain ContainerBuilder rather than the FlociBuilder the S3/SQS tests use, for the same
    // reason GcpStorageTests, GcpPubSubTests and GcpFirestoreTests do: Testcontainers.Floci 4.16.0
    // hardcodes 4566, and floci-gcp listens on 4588 with its health path namespaced as
    // /_floci-gcp/health.
    private readonly IContainer flociGcp = new ContainerBuilder("floci/floci-gcp:latest")
        .WithPortBinding(FlociGcpPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci-gcp/health").ForPort(FlociGcpPort)))
        .Build();

    private SecretManagerClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociGcp.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new SecretManagerClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociGcp.DisposeAsync();

    // Hostname rather than "localhost" deliberately — see GcpStorageTests for why: Testcontainers
    // hands back an address, and on a Windows host "localhost" resolves to ::1 first while the
    // published port is IPv4-only, costing a ~2 s dead IPv6 attempt on every first connection.
    private string Endpoint => $"http://{this.flociGcp.Hostname}:{this.flociGcp.GetMappedPublicPort(FlociGcpPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new SecretManagerDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new SecretManagerDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListSecrets — before", s.Title),
            s => Assert.Equal("CreateSecret", s.Title),
            s => Assert.Equal("AddSecretVersion", s.Title),
            s => Assert.Equal("AccessSecretVersion", s.Title),
            s => Assert.Equal("AddSecretVersion — update", s.Title),
            s => Assert.Equal("AccessSecretVersion — after update", s.Title),
            s => Assert.Equal("DeleteSecret — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "AccessSecretVersion").Response);
        Assert.Contains("Updated from FlociLab.", steps.Single(s => s.Title == "AccessSecretVersion — after update").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Secrets_Behind()
    {
        SecretManagerDemo demo = new(this.factory);
        SecretManagerSecretStore secretStore = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IReadOnlyList<SecretInfo> before = await secretStore.ListSecretsAsync(ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<SecretInfo> after = await secretStore.ListSecretsAsync(ct);

        Assert.Equal(before.Select(s => s.Name).Order(), after.Select(s => s.Name).Order());
    }

    /// <summary>The capability the secrets comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task SecretStore_Capability_RoundTrips()
    {
        SecretManagerSecretStore secretStore = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-cap-{Guid.NewGuid():N}";

        // SetSecretAsync creates the container on first use — there is no separate CreateSecret
        // call in the capability contract, unlike the demo's explicit step.
        await secretStore.SetSecretAsync(name, "capability round-trip", ct);

        try
        {
            Assert.Contains(name, (await secretStore.ListSecretsAsync(ct)).Select(s => s.Name));
            Assert.Equal("capability round-trip", await secretStore.GetSecretAsync(name, ct));

            await secretStore.SetSecretAsync(name, "capability round-trip, updated", ct);

            Assert.Equal("capability round-trip, updated", await secretStore.GetSecretAsync(name, ct));
        }
        finally
        {
            await secretStore.DeleteSecretAsync(name, CancellationToken.None);
        }

        Assert.DoesNotContain(name, (await secretStore.ListSecretsAsync(ct)).Select(s => s.Name));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        SecretManagerDemo demo = new(this.factory);
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
        SecretManagerDemo demo = new(this.factory);
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
        SecretManagerDemo demo = new(this.factory);

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
        SecretManagerDemo demo = new(new SecretManagerClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// The emulator behaviour <c>SecretManagerDemo</c>'s cleanup step rests on, pinned so it fails
    /// loudly if floci-gcp ever makes DeleteSecret idempotent. Unlike Firestore’s delete — which
    /// succeeds on a document that was never written, and so needs a Precondition to have a
    /// postcondition at all — Secret Manager answers NOT_FOUND, which is what lets the cleanup step
    /// treat the status alone as proof it removed something. See docs/BLAZOR-PLAN.md §14 on cleanup
    /// steps that render green having achieved nothing.
    /// </summary>
    [Fact]
    public async Task Deleting_A_Secret_That_Was_Never_Created_Answers_NotFound()
    {
        SecretManagerServiceClient client = this.factory.Create();
        SecretName neverCreated = new(this.factory.ProjectId, $"flocilab-absent-{Guid.NewGuid():N}");

        RpcException ex = await Assert.ThrowsAsync<RpcException>(
            () => client.DeleteSecretAsync(neverCreated, TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    private static GcpEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Gcp = new GcpEmulatorOptions { Endpoint = endpoint } }));
}
