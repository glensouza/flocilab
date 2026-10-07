using Amazon.CertificateManager;
using Amazon.CertificateManager.Model;
using FlociLab.Aws.Acm;
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
public sealed class AwsAcmTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private AcmClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new AcmClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new AcmDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new AcmDemo(this.factory));

        Assert.Collection(
            steps,
            s => Assert.Equal("RequestCertificate — DNS validation", s.Title),
            s =>
            {
                Assert.Equal("DescribeCertificate — the status straight after the request", s.Title);

                // Tripwire: real ACM answers PENDING_VALIDATION here. When floci starts waiting for
                // the CNAME, this assertion is the signal that it landed.
                Assert.StartsWith("ISSUED already", s.Response);
            },
            s => Assert.Equal("GetCertificate — the PEM", s.Title),
            s => Assert.Equal("AddTagsToCertificate, ListTagsForCertificate", s.Title),
            s => Assert.Equal("ListCertificates", s.Title),
            s =>
            {
                Assert.Equal("RequestCertificate — a name that is not a domain", s.Title);

                // Tripwire: floci does not validate the domain's syntax; real ACM refuses it.
                Assert.StartsWith("accepted", s.Response);
            },
            s => Assert.Equal("RequestCertificate — refused when the validation method is unknown", s.Title),
            s => Assert.Equal("DeleteCertificate — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves no certificate behind — including the one for a name that is not a domain.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_No_Certificate()
    {
        AcmDemo demo = new(this.factory);

        for (int run = 0; run < 2; run++)
        {
            List<DemoStep> steps = await RunAsync(demo);

            Assert.All(steps, s => Assert.True(s.Succeeded, $"run {run}, {s.Title}: {s.Error}"));
        }

        using IAmazonCertificateManager client = this.factory.Create();
        ListCertificatesResponse certificates = await client.ListCertificatesAsync(new ListCertificatesRequest(), TestContext.Current.CancellationToken);

        Assert.Empty(certificates.CertificateSummaryList ?? []);
    }

    /// <summary>A consumer that stops early (the page navigated away) still has the certificate deleted by the finally.</summary>
    [Fact]
    public async Task Abandoned_Run_Deletes_The_Certificate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await foreach (DemoStep step in new AcmDemo(this.factory).RunAsync(ct))
        {
            if (step.Title == "RequestCertificate — DNS validation")
            {
                break;
            }
        }

        using IAmazonCertificateManager client = this.factory.Create();
        ListCertificatesResponse certificates = await client.ListCertificatesAsync(new ListCertificatesRequest(), ct);

        Assert.Empty(certificates.CertificateSummaryList ?? []);
    }

    /// <summary>A stopped emulator must read as Unreachable, not as a broken sample.</summary>
    [Fact]
    public async Task Probe_Against_A_Stopped_Emulator_Reports_Unreachable()
    {
        AcmDemo demo = new(new AcmClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static async Task<List<DemoStep>> RunAsync(AcmDemo demo)
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
