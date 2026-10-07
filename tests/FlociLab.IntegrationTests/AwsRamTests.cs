using System.Net;
using Amazon.RAM;
using Amazon.RAM.Model;
using FlociLab.Aws.Ram;
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
public sealed class AwsRamTests : IAsyncLifetime
{
    // Pinned to :latest so the tripwires track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private RamClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new RamClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new RamDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new RamDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateResourceShare", s.Title),
            s =>
            {
                // The create's own tags are the Short's gotcha: floci stores none of them.
                Assert.Equal("GetResourceShares — what the create left behind", s.Title);
                Assert.Contains("0 of 2 tag(s)", s.Response);
            },
            s => Assert.Equal("TagResource", s.Title),
            s => Assert.Equal("GetResourceShares — both tags read back", s.Title),
            s => Assert.Equal("UpdateResourceShare — rename", s.Title),
            s => Assert.Equal("AssociateResourceShare — a principal and a resource", s.Title),
            s => Assert.Equal("ListPrincipals and ListResources — both read back", s.Title),
            s => Assert.Equal("DisassociateResourceShare — the principal", s.Title),
            s => Assert.Equal("DeleteResourceShare", s.Title),
            s => Assert.Equal("GetResourceShares — no longer ACTIVE", s.Title),
            s => Assert.Equal("DeleteResourceShare — refused once deleted", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Re-runnable because every run names its share uniquely and deletes it. A deleted share stays
    /// listed as DELETED, so "left nothing behind" means no ACTIVE share, not an empty list.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_Nothing_Behind()
    {
        RamDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        Assert.Empty(await this.ActiveSharesAsync(ct));
    }

    /// <summary>A consumer that stops early (the page navigated away) still has its share removed by the finally.</summary>
    [Fact]
    public async Task Abandoned_Run_Removes_Its_Share()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await foreach (DemoStep step in new RamDemo(this.factory).RunAsync(ct))
        {
            if (step.Title == "AssociateResourceShare — a principal and a resource")
            {
                break;
            }
        }

        Assert.Empty(await this.ActiveSharesAsync(ct));
    }

    /// <summary>
    /// Tripwire (plan §14). floci 2.1.0 drops <c>Tags</c> on <c>CreateResourceShare</c> — the share
    /// comes back with none — while <c>TagResource</c> works. When this fails, upstream started
    /// storing them: the sample's NOTE step turns into a plain read-back.
    /// </summary>
    [Fact]
    public async Task Floci_Drops_The_Tags_Sent_With_CreateResourceShare()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonRAM client = this.factory.Create();

        CreateResourceShareResponse created = await client.CreateResourceShareAsync(
            new CreateResourceShareRequest { Name = $"flocilab-tags-{Guid.NewGuid().ToString("N")[..8]}", Tags = [new Tag { Key = "env", Value = "lab" }] }, ct);

        try
        {
            Assert.Empty(created.ResourceShare.Tags ?? []);

            await client.TagResourceAsync(new TagResourceRequest { ResourceShareArn = created.ResourceShare.ResourceShareArn, Tags = [new Tag { Key = "env", Value = "lab" }] }, ct);
            GetResourceSharesResponse read = await client.GetResourceSharesAsync(
                new GetResourceSharesRequest { ResourceOwner = ResourceOwner.SELF, ResourceShareArns = [created.ResourceShare.ResourceShareArn] }, ct);

            Assert.Equal("lab", read.ResourceShares.Single().Tags.Single(t => t.Key == "env").Value);
        }
        finally
        {
            await client.DeleteResourceShareAsync(new DeleteResourceShareRequest { ResourceShareArn = created.ResourceShare.ResourceShareArn }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tripwire (plan §14). floci 2.1.0 has no <c>GetResourceShareAssociations</c> — the router
    /// answers <c>UnknownOperationException</c> with a 404, not a 501, so it does not read as
    /// <c>NotImplemented</c> to <c>Classify</c>. When this fails, upstream routed it: read the
    /// associations back with it instead of ListPrincipals and ListResources.
    /// </summary>
    [Fact]
    public async Task Floci_Has_No_GetResourceShareAssociations()
    {
        using IAmazonRAM client = this.factory.Create();

        AmazonRAMException ex = await Assert.ThrowsAsync<AmazonRAMException>(
            () => client.GetResourceShareAssociationsAsync(new GetResourceShareAssociationsRequest { AssociationType = ResourceShareAssociationType.PRINCIPAL }, TestContext.Current.CancellationToken));

        Assert.Equal("UnknownOperationException", ex.ErrorCode);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    /// <summary>
    /// Tripwire (plan §14). floci takes any ARN as a shareable resource and invents its type from
    /// the ARN's shape — an S3 bucket, which RAM cannot share at all, is accepted. When this
    /// fails, upstream started checking.
    /// </summary>
    [Fact]
    public async Task Floci_Accepts_An_Arn_That_Is_Not_Shareable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonRAM client = this.factory.Create();

        CreateResourceShareResponse created = await client.CreateResourceShareAsync(
            new CreateResourceShareRequest { Name = $"flocilab-arn-{Guid.NewGuid().ToString("N")[..8]}", ResourceArns = ["arn:aws:s3:::flocilab-not-shareable"] }, ct);

        try
        {
            ListResourcesResponse listed = await client.ListResourcesAsync(
                new ListResourcesRequest { ResourceOwner = ResourceOwner.SELF, ResourceShareArns = [created.ResourceShare.ResourceShareArn] }, ct);

            Assert.Equal("arn:aws:s3:::flocilab-not-shareable", listed.Resources.Single().Arn);
        }
        finally
        {
            await client.DeleteResourceShareAsync(new DeleteResourceShareRequest { ResourceShareArn = created.ResourceShare.ResourceShareArn }, CancellationToken.None);
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
        RamDemo demo = new(this.factory);
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
        RamDemo demo = new(new RamClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task<List<ResourceShare>> ActiveSharesAsync(CancellationToken ct)
    {
        using IAmazonRAM client = this.factory.Create();
        GetResourceSharesResponse response = await client.GetResourceSharesAsync(
            new GetResourceSharesRequest { ResourceOwner = ResourceOwner.SELF, ResourceShareStatus = ResourceShareStatus.ACTIVE }, ct);

        return response.ResourceShares ?? [];
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
