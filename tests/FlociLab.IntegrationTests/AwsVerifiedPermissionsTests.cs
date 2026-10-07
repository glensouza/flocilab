using System.Diagnostics;
using Amazon.VerifiedPermissions;
using Amazon.VerifiedPermissions.Model;
using FlociLab.Aws.VerifiedPermissions;
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
///
/// <para>
/// floci evaluates Cedar in a <i>sidecar</i> container it starts itself through the Docker socket,
/// on the first policy it has to parse, pulling <c>floci/floci:latest-cedar</c> if the host lacks it
/// (about 75 s measured 2026-10-06). So the socket is mounted, and the sidecar — named
/// <c>floci-cedar</c> with no per-run suffix, a Docker-host singleton like Service Bus's Artemis —
/// is forced up in <see cref="InitializeAsync"/> and removed afterwards only if this run is what
/// started it. A blanket <c>docker rm -f</c> would tear a running dev stack's Cedar out from under it.
/// </para>
/// </summary>
public sealed class AwsVerifiedPermissionsTests : IAsyncLifetime
{
    private const string CedarSidecarName = "floci-cedar";

    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest")
        .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock")
        .Build();

    private bool sidecarCreatedByThisRun;

    private VerifiedPermissionsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new VerifiedPermissionsClientFactory(EndpointsFor(this.floci.GetConnectionString()));

        // Recorded before anything starts the sidecar, so DisposeAsync can tell "we brought this
        // up" from "it was someone else's and must be left alone".
        // Set before the warm-up, not after: a warm-up that throws (a slow first pull outlasting
        // the cancellation window) may already have started the sidecar, and it must still go.
        this.sidecarCreatedByThisRun = !await SidecarExistsAsync();

        // A full run is the cheapest call that parses a policy, which is what starts the sidecar.
        // Its steps are checked here so a sidecar that never came up (no socket reachable from
        // floci) fails setup with the cause, rather than every test failing on CreatePolicy.
        List<DemoStep> warmUp = await RunAsync(new VerifiedPermissionsDemo(this.factory));
        DemoStep? failed = warmUp.Find(s => !s.Succeeded);

        if (failed is not null)
        {
            throw new InvalidOperationException($"the Cedar sidecar warm-up failed at '{failed.Title}': {failed.Error}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.floci.DisposeAsync();

        // floci does not stop the sidecar when it stops itself, so it has to go explicitly.
        if (this.sidecarCreatedByThisRun)
        {
            await RunDockerAsync($"rm -f {CedarSidecarName}");
        }
    }

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new VerifiedPermissionsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new VerifiedPermissionsDemo(this.factory));

        Assert.Collection(
            steps,
            s => Assert.Equal("CreatePolicyStore", s.Title),
            s => Assert.Equal("CreatePolicy — permit", s.Title),
            s =>
            {
                Assert.Equal("IsAuthorized — the permitted principal", s.Title);
                Assert.StartsWith("ALLOW", s.Response);
            },
            s =>
            {
                Assert.Equal("IsAuthorized — a principal no policy mentions", s.Title);
                Assert.StartsWith("DENY — no policy applies", s.Response);
            },
            s => Assert.Equal("CreatePolicy — forbid", s.Title),
            s =>
            {
                Assert.Equal("IsAuthorized — the forbid wins", s.Title);
                Assert.StartsWith("DENY — determined by", s.Response);
            },
            s => Assert.Equal("ListPolicies", s.Title),
            s => Assert.Equal("CreatePolicy — refused when it is not Cedar", s.Title),
            s => Assert.Equal("DeletePolicyStore — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves no store behind.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_No_Policy_Store()
    {
        VerifiedPermissionsDemo demo = new(this.factory);

        for (int run = 0; run < 2; run++)
        {
            List<DemoStep> steps = await RunAsync(demo);

            Assert.All(steps, s => Assert.True(s.Succeeded, $"run {run}, {s.Title}: {s.Error}"));
        }

        using IAmazonVerifiedPermissions client = this.factory.Create();
        ListPolicyStoresResponse stores = await client.ListPolicyStoresAsync(new ListPolicyStoresRequest(), TestContext.Current.CancellationToken);

        Assert.Empty(stores.PolicyStores);
    }

    /// <summary>A consumer that stops early (the page navigated away) still has the store deleted by the finally.</summary>
    [Fact]
    public async Task Abandoned_Run_Deletes_The_Store()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await foreach (DemoStep step in new VerifiedPermissionsDemo(this.factory).RunAsync(ct))
        {
            if (step.Title == "CreatePolicy — permit")
            {
                break;
            }
        }

        using IAmazonVerifiedPermissions client = this.factory.Create();
        ListPolicyStoresResponse stores = await client.ListPolicyStoresAsync(new ListPolicyStoresRequest(), ct);

        Assert.Empty(stores.PolicyStores);
    }

    /// <summary>A stopped emulator must read as Unreachable, not as a broken sample.</summary>
    [Fact]
    public async Task Probe_Against_A_Stopped_Emulator_Reports_Unreachable()
    {
        VerifiedPermissionsDemo demo = new(new VerifiedPermissionsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static async Task<bool> SidecarExistsAsync()
        => (await RunDockerAsync($"ps -a --filter name=^{CedarSidecarName}$ --format {{{{.Names}}}}")).Contains(CedarSidecarName, StringComparison.Ordinal);

    private static async Task<string> RunDockerAsync(string arguments)
    {
        ProcessStartInfo startInfo = new("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("docker did not start.");

        // Read before waiting: a process whose output fills the pipe buffer blocks forever on exit.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync();

        return await stdout;
    }

    private static async Task<List<DemoStep>> RunAsync(VerifiedPermissionsDemo demo)
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
