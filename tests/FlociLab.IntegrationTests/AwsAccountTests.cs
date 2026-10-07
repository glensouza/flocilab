using System.Net;
using Amazon.Account;
using Amazon.Account.Model;
using FlociLab.Aws.Account;
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
public sealed class AwsAccountTests : IAsyncLifetime
{
    // Pinned to :latest so the tripwires track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private AccountClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new AccountClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new AccountDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new AccountDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("GetAlternateContact — what is there already", s.Title),
            s => Assert.Equal("PutAlternateContact", s.Title),
            s => Assert.Equal("GetAlternateContact — read back", s.Title),
            s => Assert.Equal("PutAlternateContact — overwrite", s.Title),
            s => Assert.Equal("PutAlternateContact — refused without an email", s.Title),
            s =>
            {
                // The Short's gotcha: floci has no DeleteAlternateContact, so the step is a NOTE.
                Assert.Equal("DeleteAlternateContact", s.Title);
                Assert.Contains("NOTE", s.Response);
            },
            s => Assert.Equal("Put the account back — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Re-runnable. floci cannot delete a contact, so the first run's contact stays; the second run
    /// must recognise it as a leftover rather than snapshot it as the account's own and restore it.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Twice_Restores_What_It_Found()
    {
        AccountDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;
        List<DemoStep> second = [];

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");

                if (run == 1)
                {
                    second.Add(step);
                }
            }
        }

        Assert.Contains("left by an earlier FlociLab run", second[0].Response);
        Assert.Contains(second, s => s.Title == "DeleteAlternateContact");
        Assert.DoesNotContain("Restored", second[^1].Response);
    }

    /// <summary>
    /// The no-prior-contact branch of the cleanup: a run abandoned before its own delete step still
    /// ends cleanly, and on floci leaves its contact, which the next run treats as a leftover.
    /// </summary>
    [Fact]
    public async Task Abandoned_Run_Without_A_Prior_Contact_Leaves_A_Recognised_Leftover()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        AccountDemo demo = new(this.factory);

        await foreach (DemoStep step in demo.RunAsync(ct))
        {
            if (step.Title == "PutAlternateContact")
            {
                break;
            }
        }

        using IAmazonAccount client = this.factory.Create();
        GetAlternateContactResponse after = await client.GetAlternateContactAsync(new GetAlternateContactRequest { AlternateContactType = AlternateContactType.SECURITY }, ct);

        Assert.StartsWith("flocilab-", after.AlternateContact.Name);

        await foreach (DemoStep step in demo.RunAsync(ct))
        {
            Assert.Contains("left by an earlier FlociLab run", step.Response);
            break;
        }
    }

    /// <summary>The contact is one value per account, so a second run while one is in flight is refused rather than interleaved.</summary>
    [Fact]
    public async Task Overlapping_Run_Is_Refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        AccountDemo demo = new(this.factory);

        await using IAsyncEnumerator<DemoStep> first = demo.RunAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await first.MoveNextAsync());

        List<DemoStep> second = [];

        await foreach (DemoStep step in demo.RunAsync(ct))
        {
            second.Add(step);
        }

        DemoStep refused = Assert.Single(second);
        Assert.False(refused.Succeeded);
        Assert.Equal("Another run is in progress", refused.Title);
    }

    /// <summary>A contact that existed before the run is the one that is there afterwards.</summary>
    [Fact]
    public async Task Existing_Contact_Is_Put_Back()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonAccount client = this.factory.Create();

        await client.PutAlternateContactAsync(
            new PutAlternateContactRequest
            {
                AlternateContactType = AlternateContactType.SECURITY,
                Name = "Pat Original",
                Title = "CISO",
                EmailAddress = "pat@example.com",
                PhoneNumber = "+15555550111",
            }, ct);

        await foreach (DemoStep step in new AccountDemo(this.factory).RunAsync(ct))
        {
            Assert.True(step.Succeeded, $"{step.Title}: {step.Error}");
        }

        GetAlternateContactResponse after = await client.GetAlternateContactAsync(new GetAlternateContactRequest { AlternateContactType = AlternateContactType.SECURITY }, ct);

        Assert.Equal("Pat Original", after.AlternateContact.Name);
        Assert.Equal("CISO", after.AlternateContact.Title);
        Assert.Equal("pat@example.com", after.AlternateContact.EmailAddress);
    }

    /// <summary>A consumer that stops early (the page navigated away) still has the contact put back by the finally.</summary>
    [Fact]
    public async Task Abandoned_Run_Restores_The_Contact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonAccount client = this.factory.Create();

        await client.PutAlternateContactAsync(
            new PutAlternateContactRequest
            {
                AlternateContactType = AlternateContactType.SECURITY,
                Name = "Sam Original",
                Title = "Lead",
                EmailAddress = "sam@example.com",
                PhoneNumber = "+15555550122",
            }, ct);

        await foreach (DemoStep step in new AccountDemo(this.factory).RunAsync(ct))
        {
            if (step.Title == "PutAlternateContact — overwrite")
            {
                break;
            }
        }

        GetAlternateContactResponse after = await client.GetAlternateContactAsync(new GetAlternateContactRequest { AlternateContactType = AlternateContactType.SECURITY }, ct);

        Assert.Equal("Sam Original", after.AlternateContact.Name);
    }

    /// <summary>
    /// Tripwire (plan §14). floci 2.1.0 serves only Put and GetAlternateContact; every other Account
    /// Management operation answers a 404 <c>UnknownOperationException</c>, not a 501, so
    /// <see cref="AccountDemo.Classify"/> reads it as an error. When this fails, upstream added
    /// <c>DeleteAlternateContact</c>: the sample's NOTE step becomes a plain delete.
    /// </summary>
    [Fact]
    public async Task Floci_Has_No_DeleteAlternateContact()
    {
        using IAmazonAccount client = this.factory.Create();

        AmazonAccountException ex = await Assert.ThrowsAsync<AmazonAccountException>(
            () => client.DeleteAlternateContactAsync(new DeleteAlternateContactRequest { AlternateContactType = AlternateContactType.SECURITY }, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("UnknownOperationException", ex.ErrorCode);
    }

    /// <summary>A contact type nobody has set is ResourceNotFoundException, which the probe still reads as Ok.</summary>
    [Fact]
    public async Task Missing_Contact_Is_ResourceNotFound()
    {
        using IAmazonAccount client = this.factory.Create();

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => client.GetAlternateContactAsync(new GetAlternateContactRequest { AlternateContactType = AlternateContactType.BILLING }, TestContext.Current.CancellationToken));
    }

    /// <summary>A stopped emulator must read as Unreachable, not as a broken sample.</summary>
    [Fact]
    public async Task Probe_Against_A_Stopped_Emulator_Reports_Unreachable()
    {
        AccountDemo demo = new(new AccountClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
