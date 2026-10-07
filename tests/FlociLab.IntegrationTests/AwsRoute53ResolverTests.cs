using Amazon.Route53Resolver;
using Amazon.Route53Resolver.Model;
using Amazon.Runtime;
using FlociLab.Aws.Route53Resolver;
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
public sealed class AwsRoute53ResolverTests : IAsyncLifetime
{
    // Same reasoning as AwsRoute53Tests: pinned to :latest so the tripwires track the same build
    // the AppHost and the README's Compose stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private Route53ResolverClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new Route53ResolverClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new Route53ResolverDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new Route53ResolverDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListResolverRules — before", s.Title),
            s => Assert.Equal("CreateResolverRule", s.Title),
            s => Assert.Equal("CreateResolverRule — repeated CreatorRequestId", s.Title),
            s => Assert.Equal("GetResolverRule", s.Title),
            s => Assert.Equal("UpdateResolverRule", s.Title),
            s => Assert.Equal("AssociateResolverRule", s.Title),
            s => Assert.Equal("ListResolverRuleAssociations", s.Title),
            s => Assert.Equal("CreateFirewallDomainList", s.Title),
            s => Assert.Equal("GetFirewallDomainList", s.Title),
            s => Assert.Equal("DisassociateResolverRule", s.Title),
            s => Assert.Equal("DeleteResolverRule — cleanup", s.Title),
            s => Assert.Equal("DeleteFirewallDomainList — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("ResourceExists", steps.Single(s => s.Title == "CreateResolverRule — repeated CreatorRequestId").Response ?? string.Empty, StringComparison.Ordinal);

        // The rule delete that follows would be refused on real AWS until the association is gone.
        Assert.Contains("association gone", steps.Single(s => s.Title == "DisassociateResolverRule").Response ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_Nothing_Behind()
    {
        Route53ResolverDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonRoute53Resolver client = this.factory.Create();
        ListResolverRulesResponse rulesBefore = await client.ListResolverRulesAsync(new ListResolverRulesRequest(), ct);
        ListFirewallDomainListsResponse listsBefore = await client.ListFirewallDomainListsAsync(new ListFirewallDomainListsRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListResolverRulesResponse rulesAfter = await client.ListResolverRulesAsync(new ListResolverRulesRequest(), ct);
        ListFirewallDomainListsResponse listsAfter = await client.ListFirewallDomainListsAsync(new ListFirewallDomainListsRequest(), ct);

        Assert.Equal((rulesBefore.ResolverRules ?? []).Select(r => r.Id).Order(), (rulesAfter.ResolverRules ?? []).Select(r => r.Id).Order());
        Assert.Equal((listsBefore.FirewallDomainLists ?? []).Select(l => l.Id).Order(), (listsAfter.FirewallDomainLists ?? []).Select(l => l.Id).Order());
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render red steps blaming the
    /// emulator for the user leaving.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        Route53ResolverDemo demo = new(this.factory);
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
        Route53ResolverDemo demo = new(new Route53ResolverClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// floci 2.2.0 reads the endpoint's IPs from <c>IpAddresses</c>, as the real API and every SDK
    /// send them; 2.1.0 demanded <c>IpAddressRequests</c>, so no SDK could create a resolver
    /// endpoint (plan §14). The sample does not yet use endpoints or FORWARD rules.
    /// </summary>
    [Fact]
    public async Task CreateResolverEndpoint_Works_Through_The_Sdk()
    {
        using IAmazonRoute53Resolver client = this.factory.Create();
        CancellationToken ct = TestContext.Current.CancellationToken;

        CreateResolverEndpointResponse created = await client.CreateResolverEndpointAsync(
            new CreateResolverEndpointRequest
            {
                CreatorRequestId = Guid.NewGuid().ToString("N"),
                Name = "endpoint-check",
                Direction = ResolverEndpointDirection.OUTBOUND,
                SecurityGroupIds = ["sg-0123456789abcdef0"],
                IpAddresses = [new IpAddressRequest { SubnetId = "subnet-1" }, new IpAddressRequest { SubnetId = "subnet-2" }],
            },
            ct);

        try
        {
            Assert.False(string.IsNullOrEmpty(created.ResolverEndpoint.Id));
            Assert.Equal(ResolverEndpointDirection.OUTBOUND, created.ResolverEndpoint.Direction);
        }
        finally
        {
            await client.DeleteResolverEndpointAsync(new DeleteResolverEndpointRequest { ResolverEndpointId = created.ResolverEndpoint.Id }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tripwire (plan §14). floci 2.1.0 has no <c>UpdateFirewallDomains</c>, so a domain list can
    /// be created but never filled. When this starts failing, extend the domain-list steps.
    /// </summary>
    [Fact]
    public async Task Tripwire_UpdateFirewallDomains_Is_Unknown_To_Floci()
    {
        using IAmazonRoute53Resolver client = this.factory.Create();
        string id = Guid.NewGuid().ToString("N");

        CreateFirewallDomainListResponse created = await client.CreateFirewallDomainListAsync(
            new CreateFirewallDomainListRequest { CreatorRequestId = id, Name = $"tripwire-{id[..8]}" }, TestContext.Current.CancellationToken);

        try
        {
            AmazonServiceException ex = await Assert.ThrowsAnyAsync<AmazonServiceException>(async () =>
                await client.UpdateFirewallDomainsAsync(
                    new UpdateFirewallDomainsRequest
                    {
                        FirewallDomainListId = created.FirewallDomainList.Id,
                        Operation = FirewallDomainUpdateOperation.ADD,
                        Domains = ["bad.flocilab.test"],
                    },
                    TestContext.Current.CancellationToken));

            Assert.Contains("UnknownOperation", ex.ErrorCode ?? ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DeleteFirewallDomainListAsync(new DeleteFirewallDomainListRequest { FirewallDomainListId = created.FirewallDomainList.Id }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tripwire (plan §14). Real Route 53 Resolver refuses to delete a rule that a VPC is still
    /// associated with (<c>ResourceInUseException</c>); floci 2.1.0 deletes it and leaves the
    /// association behind. When this starts failing, the sample can show the refusal as a step.
    /// </summary>
    [Fact]
    public async Task Tripwire_Rule_Delete_While_Associated_Succeeds_On_Floci()
    {
        using IAmazonRoute53Resolver client = this.factory.Create();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string id = Guid.NewGuid().ToString("N");
        const string vpcId = "vpc-0123456789abcdef0";

        CreateResolverRuleResponse rule = await client.CreateResolverRuleAsync(
            new CreateResolverRuleRequest { CreatorRequestId = id, Name = $"tripwire-{id[..8]}", RuleType = RuleTypeOption.SYSTEM, DomainName = $"{id[..8]}.flocilab.test" }, ct);
        await client.AssociateResolverRuleAsync(new AssociateResolverRuleRequest { ResolverRuleId = rule.ResolverRule.Id, VPCId = vpcId }, ct);

        try
        {
            DeleteResolverRuleResponse deleted = await client.DeleteResolverRuleAsync(new DeleteResolverRuleRequest { ResolverRuleId = rule.ResolverRule.Id }, ct);

            Assert.Equal(ResolverRuleStatus.DELETING, deleted.ResolverRule.Status);
        }
        finally
        {
            await client.DisassociateResolverRuleAsync(new DisassociateResolverRuleRequest { ResolverRuleId = rule.ResolverRule.Id, VPCId = vpcId }, CancellationToken.None);
        }
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
