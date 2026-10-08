using System.Net;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using FlociLab.Gcp.Storage;
using Google;
using Google.Api.Gax;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Options;
using Xunit;
using GcsBucket = Google.Apis.Storage.v1.Data.Bucket;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-gcp per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class GcpStorageTests : IAsyncLifetime
{
    private const int FlociGcpPort = 4588;
    private const string StorageEmulatorHostVariable = "STORAGE_EMULATOR_HOST";

    // A plain ContainerBuilder rather than the FlociBuilder the S3 tests use, for the same reason
    // the Azure tests use one: Testcontainers.Floci 4.16.0 hardcodes 4566, and floci-gcp listens
    // on 4588. The health path is namespaced too — /_floci-gcp/health, not /_floci/health, which
    // 404s here and would fail the wait strategy on a perfectly healthy container.
    private readonly IContainer flociGcp = new ContainerBuilder("floci/floci-gcp:latest")
        .WithPortBinding(FlociGcpPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci-gcp/health").ForPort(FlociGcpPort)))
        .Build();

    private StorageClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociGcp.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new StorageClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociGcp.DisposeAsync();

    // Hostname rather than "localhost" deliberately. Testcontainers hands back an address, and on
    // a Windows host "localhost" resolves to ::1 first while the published port is IPv4-only —
    // every first connection then eats a ~2 s dead IPv6 attempt before falling back. It is only
    // latency, not a failure, but it is 2 s on every test class.
    private string Endpoint => $"http://{this.flociGcp.Hostname}:{this.flociGcp.GetMappedPublicPort(FlociGcpPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new StorageDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    /// <summary>
    /// Real GCS refuses to delete a bucket that still holds objects, and floci-gcp does too since
    /// 0.9.0 — through 0.8.0 it answered 204 and orphaned the objects (plan §14). The demo's cleanup
    /// and the capability's DeleteContainer drain first either way; this pins the emulator matching
    /// the cloud, so a regression back to 204 fails here.
    /// </summary>
    [Fact]
    public async Task Deleting_A_Non_Empty_Bucket_Is_Refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GcsObjectStore store = new(this.factory);
        string bucket = $"flocilab-409-{Guid.NewGuid():N}"[..24];

        await store.CreateContainerAsync(bucket, ct);

        try
        {
            using MemoryStream payload = new(Encoding.UTF8.GetBytes("still here"));
            await store.PutObjectAsync(bucket, "occupied.txt", payload, ct);

            StorageClient client = this.factory.Create();

            GoogleApiException ex = await Assert.ThrowsAsync<GoogleApiException>(
                async () => await client.DeleteBucketAsync(bucket, cancellationToken: ct));

            Assert.Equal(HttpStatusCode.Conflict, ex.HttpStatusCode);
        }
        finally
        {
            await store.DeleteContainerAsync(bucket, CancellationToken.None);
        }
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new StorageDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListBuckets — before", s.Title),
            s => Assert.Equal("CreateBucket", s.Title),
            s => Assert.Equal("UploadObject", s.Title),
            s => Assert.Equal("ListObjects", s.Title),
            s => Assert.Equal("DownloadObject", s.Title),
            s => Assert.Equal("DeleteObject", s.Title),
            s => Assert.Equal("DeleteBucket — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "DownloadObject").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Buckets_Behind()
    {
        StorageDemo demo = new(this.factory);
        GcsObjectStore store = new(this.factory);
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

    /// <summary>The capability the object-storage comparison page consumes (plan §8).</summary>
    [Fact]
    public async Task ObjectStore_Capability_RoundTrips()
    {
        GcsObjectStore store = new(this.factory);
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
    /// dispose, so without this the act of navigating away would render six red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        StorageDemo demo = new(this.factory);
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
        StorageDemo demo = new(new StorageClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// The headline risk in plan §14 — that Google.Cloud.Storage.V1 would ignore a custom BaseUri
    /// and force a hand-rolled HttpClient over the JSON API. It does not, and this pins that: if a
    /// future SDK version starts ignoring BaseUri, the call reaches real Google Cloud (or fails
    /// looking for credentials) rather than the emulator, and this fails loudly.
    /// </summary>
    [Fact]
    public async Task Sdk_Honours_Custom_BaseUri()
    {
        // No using: the factory owns this client now and disposes it with the fixture. Disposing
        // it here would pull the shared connection pool out from under the rest of the test.
        StorageClient client = this.factory.Create();
        string bucket = $"flocilab-uri-{Guid.NewGuid():N}"[..24];

        GcsBucket created = await client.CreateBucketAsync(
            this.factory.ProjectId, bucket, cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // floci-gcp echoes its own address back in selfLink, so this is the emulator saying
            // where it thinks it lives — proof the call never left for storage.googleapis.com.
            Assert.Equal(bucket, created.Name);
            Assert.Contains(this.flociGcp.GetMappedPublicPort(FlociGcpPort).ToString(), created.SelfLink);
        }
        finally
        {
            await client.DeleteBucketAsync(bucket, cancellationToken: CancellationToken.None);
        }
    }

    /// <summary>
    /// Plan §7 says this client ignores STORAGE_EMULATOR_HOST. On Google.Cloud.Storage.V1 5.0.0
    /// it does not: StorageClientBuilder carries an EmulatorDetection property, and EmulatorOnly
    /// plus that variable reaches floci-gcp. The sample deliberately uses BaseUri instead — a web
    /// app binding its endpoint from configuration should not depend on a process-wide environment
    /// variable — but the second route is worth pinning, because it is the one every other GCP
    /// sample (Pub/Sub, Firestore, Datastore) will take.
    ///
    /// <para>
    /// Sets a process-wide environment variable, which is only safe because xUnit runs one class's
    /// tests sequentially. The other tests here would not notice — they go through
    /// <c>StorageClientFactory</c>, whose builder leaves <c>EmulatorDetection</c> at its default and
    /// therefore never reads this variable — but a future move to parallel-within-collection would
    /// make this racy against anything that does.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EmulatorDetection_Also_Reaches_The_Emulator()
    {
        string? original = Environment.GetEnvironmentVariable(StorageEmulatorHostVariable);
        Environment.SetEnvironmentVariable(StorageEmulatorHostVariable, this.Endpoint);

        try
        {
            using StorageClient client = new StorageClientBuilder
            {
                EmulatorDetection = EmulatorDetection.EmulatorOnly,
            }.Build();

            Page<GcsBucket> page = await client.ListBucketsAsync(this.factory.ProjectId)
                .ReadPageAsync(10, TestContext.Current.CancellationToken);

            Assert.NotNull(page);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StorageEmulatorHostVariable, original);
        }
    }

    private static GcpEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Gcp = new GcpEmulatorOptions { Endpoint = endpoint } }));
}
