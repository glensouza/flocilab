using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Azure.Blob;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-az per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator
/// the AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class AzureBlobTests : IAsyncLifetime
{
    private const int FlociAzPort = 4577;

    // A plain ContainerBuilder rather than the FlociBuilder the S3 tests use. Testcontainers.Floci
    // 4.15.0 is built for floci/floci specifically: its configuration hardcodes 4566 as both the
    // exposed port and the port binding, and GetConnectionString() maps that one. floci-az listens
    // on 4577, so the module would bind the wrong port and wait on a socket nothing is serving.
    private readonly IContainer flociAz = new ContainerBuilder("floci/floci-az:latest")
        .WithPortBinding(FlociAzPort, assignRandomHostPort: true)
        // Storage mode is left at the image default. The AppHost asks for "persistent" against a
        // named volume so the lab survives a restart; a container that exists for one test class
        // wants the opposite.
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci/health").ForPort(FlociAzPort)))
        .Build();

    private BlobClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.flociAz.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new BlobClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.flociAz.DisposeAsync();

    private string Endpoint => $"http://{this.flociAz.Hostname}:{this.flociAz.GetMappedPublicPort(FlociAzPort)}";

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new BlobDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new BlobDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListContainers — before", s.Title),
            s => Assert.Equal("CreateContainer", s.Title),
            s => Assert.Equal("UploadBlob", s.Title),
            s => Assert.Equal("ListBlobs", s.Title),
            s => Assert.Equal("DownloadBlob", s.Title),
            s => Assert.Equal("DeleteBlob", s.Title),
            s => Assert.Equal("DeleteContainer — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps.Single(s => s.Title == "DownloadBlob").Response);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. If this ever fails on the second pass, the demo is leaking state.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Containers_Behind()
    {
        BlobDemo demo = new(this.factory);
        BlobObjectStore store = new(this.factory);
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
        BlobObjectStore store = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        string container = $"flocilab-cap-{Guid.NewGuid():N}"[..24];

        await store.CreateContainerAsync(container, ct);

        try
        {
            Assert.Contains(container, (await store.ListContainersAsync(ct)).Select(c => c.Name));

            using MemoryStream payload = new(Encoding.UTF8.GetBytes("capability round-trip"));
            await store.PutObjectAsync(container, "probe.txt", payload, ct);

            using Stream fetched = await store.GetObjectAsync(container, "probe.txt", ct);
            using StreamReader reader = new(fetched);

            Assert.Equal("capability round-trip", await reader.ReadToEndAsync(ct));
        }
        finally
        {
            await store.DeleteContainerAsync(container, CancellationToken.None);
        }

        Assert.DoesNotContain(container, (await store.ListContainersAsync(ct)).Select(c => c.Name));
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render six red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        BlobDemo demo = new(this.factory);
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
        BlobDemo demo = new(new BlobClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// The one Blob operation floci-az does not implement. Nothing in the demo calls it, so this
    /// is the tripwire rather than an assertion about the page: when it starts failing, that is
    /// the signal GetAccountInfo landed upstream and the note in plan §13 can go.
    /// </summary>
    [Fact]
    public async Task GetAccountInfo_Is_Not_Implemented()
    {
        BlobServiceClient client = this.factory.Create();

        RequestFailedException ex = await Assert.ThrowsAsync<RequestFailedException>(
            async () => await client.GetAccountInfoAsync(TestContext.Current.CancellationToken));

        Assert.Equal(501, ex.Status);
    }

    /// <summary>
    /// GetProperties, by contrast, is implemented — worth pinning so the 501 above reads as one
    /// missing operation rather than "the service client does nothing".
    /// </summary>
    [Fact]
    public async Task GetServiceProperties_Succeeds()
    {
        BlobServiceClient client = this.factory.Create();

        Response<BlobServiceProperties> response =
            await client.GetPropertiesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(200, response.GetRawResponse().Status);
    }

    private static AzureEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Azure = new AzureEmulatorOptions { Endpoint = endpoint } }));
}
