using System.Net;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using FlociLab.Oci;
using FlociLab.Oci.ObjectStorage;
using Microsoft.Extensions.Options;
using Oci.Common.Model;
using Oci.ObjectstorageService;
using Oci.ObjectstorageService.Requests;
using Oci.ObjectstorageService.Responses;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-oci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class OciObjectStorageTests : IAsyncLifetime
{
    private const int FlociOciPort = 4599;

    // A plain ContainerBuilder rather than the FlociBuilder the S3 tests use, for the same reason
    // the Azure and GCP tests use one: Testcontainers.Floci 4.15.0 hardcodes 4566, and floci-oci
    // listens on 4599. The health path is namespaced too — /_floci-oci/health, not /_floci/health,
    // which 404s here and would fail the wait strategy on a perfectly healthy container.
    private readonly IContainer flociOci = new ContainerBuilder("floci/floci-oci:latest")
        .WithPortBinding(FlociOciPort, assignRandomHostPort: true)
        // The tenancy OCID the lab uses everywhere. The image issues none of its own and verifies
        // nothing, but passing it keeps the container's idea of the tenancy and the sample's
        // compartment OCID the same value, which is what the AppHost does too.
        .WithEnvironment("FLOCI_OCI_DEFAULT_TENANCY_ID", OciEmulatorOptions.DefaultTenancyId)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci-oci/health").ForPort(FlociOciPort)))
        .Build();

    private ObjectStorageClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociOci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new ObjectStorageClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociOci.DisposeAsync();

    // Hostname rather than "localhost" deliberately. Testcontainers hands back an address, and on
    // a Windows host "localhost" resolves to ::1 first while the published port is IPv4-only —
    // every first connection then eats a ~2 s dead IPv6 attempt before falling back.
    private string Endpoint => $"http://{this.flociOci.Hostname}:{this.flociOci.GetMappedPublicPort(FlociOciPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new ObjectStorageDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new ObjectStorageDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("GetNamespace", s.Title),
            s => Assert.Equal("ListBuckets — before", s.Title),
            s => Assert.Equal("CreateBucket", s.Title),
            s => Assert.Equal("PutObject", s.Title),
            s => Assert.Equal("ListObjects", s.Title),
            s => Assert.Equal("GetObject", s.Title),
            s => Assert.Equal("DeleteObject", s.Title),
            s => Assert.Equal("DeleteBucket — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "GetObject").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Buckets_Behind()
    {
        ObjectStorageDemo demo = new(this.factory);
        OciObjectStore store = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IReadOnlyList<ContainerInfo> before = await store.ListContainersAsync(ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        IReadOnlyList<ContainerInfo> after = await store.ListContainersAsync(ct);

        Assert.Equal(before.Select(c => c.Name).Order(), after.Select(c => c.Name).Order());
    }

    /// <summary>
    /// A run that cannot even build its client has to render the reason rather than take the page
    /// down with it. <c>ObjectStorageClientFactory.Create()</c> refuses real-cloud mode with the
    /// lab's synthetic tenancy, and that refusal happens before the first request — so if the
    /// construction ever moves back outside <c>RunAsync</c>'s try, the iterator throws on the first
    /// <c>MoveNextAsync</c>, escapes the page's <c>OperationCanceledException</c>-only catch, and
    /// kills the Blazor circuit instead of showing a failed step.
    /// </summary>
    [Fact]
    public async Task Client_Construction_Failure_Becomes_A_Failed_Step()
    {
        ObjectStorageClientFactory refusing = new(new OciEndpoints(
            Options.Create(new FlociOptions { Oci = new OciEmulatorOptions { UseEmulator = false } })));

        List<DemoStep> steps = [];

        await foreach (DemoStep step in new ObjectStorageDemo(refusing).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        DemoStep only = Assert.Single(steps);

        Assert.False(only.Succeeded);
        Assert.Contains("TenancyId", only.Error);
    }

    /// <summary>The capability the object-storage comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task ObjectStore_Capability_RoundTrips()
    {
        OciObjectStore store = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string bucket = $"flocilab-cap-{Guid.NewGuid():N}"[..24];

        await store.CreateContainerAsync(bucket, ct);

        try
        {
            Assert.Contains(bucket, (await store.ListContainersAsync(ct)).Select(c => c.Name));

            using MemoryStream payload = new(Encoding.UTF8.GetBytes("capability round-trip"));
            await store.PutObjectAsync(bucket, "probe.txt", payload, ct);

            using Stream fetched = await store.GetObjectAsync(bucket, "probe.txt", ct);
            using StreamReader reader = new(fetched);

            Assert.Equal("capability round-trip", await reader.ReadToEndAsync(ct));
        }
        finally
        {
            await store.DeleteContainerAsync(bucket, CancellationToken.None);
        }

        Assert.DoesNotContain(bucket, (await store.ListContainersAsync(ct)).Select(c => c.Name));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render seven red steps blaming
    /// the emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        ObjectStorageDemo demo = new(this.factory);
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
    ///
    /// <para>
    /// This test is also the tripwire for the realm-template trap below. Before that fix a client
    /// aimed at port 1 reached real Oracle Cloud and came back 401, which classifies as Error —
    /// so a regression here fails as Unreachable-expected-but-got-Error rather than silently
    /// passing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        ObjectStorageDemo demo = new(new ObjectStorageClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// The sharpest edge in the OCI half of the repo, pinned. <c>SetEndpoint</c> alone is ignored:
    /// the client resolves every operation's URI from a realm-specific endpoint template built
    /// from the credential's region, so a client configured for the emulator sends its requests to
    /// real Oracle Cloud while <c>GetEndpoint()</c> keeps reporting the emulator. Verified on
    /// OCI.DotNetSDK 145.0.0, and still on 148.1.0 (2026-10-08).
    ///
    /// <para>
    /// The half-configured client below is what a sample following plan §7's original advice would
    /// have built. If a future SDK version makes <c>SetEndpoint</c> authoritative this test starts
    /// failing, and that is good news worth noticing rather than a breakage: it means
    /// <c>ForFloci</c>'s second line has become redundant.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SetEndpoint_Alone_Does_Not_Reach_The_Emulator()
    {
        OciEndpoints endpoints = EndpointsFor(this.Endpoint);

        using ObjectStorageClient halfConfigured = new(endpoints.AuthenticationProvider());
        halfConfigured.SetEndpoint(endpoints.Endpoint);

        // Reports the emulator, which is exactly what makes the trap so easy to fall into.
        Assert.Equal(endpoints.Endpoint, halfConfigured.GetEndpoint().ToString().TrimEnd('/'));

        // ...and yet the call does not land there.
        string? reached = null;

        try
        {
            reached = (await halfConfigured.GetNamespace(new GetNamespaceRequest(), cancellationToken: TestContext.Current.CancellationToken)).Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed on purpose, and deliberately broad: every way of failing to reach
            // floci-oci counts the same here. On a machine with a route out this is a real 401
            // from Oracle Cloud; on one without, a transport error. The assertion is that nothing
            // came back — not how it failed.
        }

        Assert.Null(reached);

        // The same client, with the one extra line ForFloci adds, does reach it.
        halfConfigured.SetRealmSpecificEndpointTemplate(endpoints.Endpoint);
        GetNamespaceResponse response = await halfConfigured.GetNamespace(
            new GetNamespaceRequest(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("floci-local", response.Value);
    }

    /// <summary>
    /// floci-oci enforces the rule real OCI enforces (as floci-gcp does since 0.9.0): a bucket that
    /// still holds objects cannot be deleted. Both the demo's cleanup and the capability's
    /// DeleteContainer drain first because of it, so this pins the behaviour they rely on.
    /// </summary>
    [Fact]
    public async Task Deleting_A_Non_Empty_Bucket_Is_Refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        OciObjectStore store = new(this.factory);
        string bucket = $"flocilab-409-{Guid.NewGuid():N}"[..24];

        await store.CreateContainerAsync(bucket, ct);

        try
        {
            using MemoryStream payload = new(Encoding.UTF8.GetBytes("still here"));
            await store.PutObjectAsync(bucket, "occupied.txt", payload, ct);

            // No using: the factory owns this client now and disposes it with the fixture.
            // Disposing it here would break the cleanup in the finally below, which goes
            // through the same shared client.
            ObjectStorageClient client = this.factory.Create();
            string space = (await client.GetNamespace(new GetNamespaceRequest(), cancellationToken: ct)).Value;

            OciException ex = await Assert.ThrowsAsync<OciException>(
                async () => await client.DeleteBucket(
                    new DeleteBucketRequest { NamespaceName = space, BucketName = bucket }, cancellationToken: ct));

            Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        }
        finally
        {
            await store.DeleteContainerAsync(bucket, CancellationToken.None);
        }
    }

    private static OciEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Oci = new OciEmulatorOptions { Endpoint = endpoint } }));
}
