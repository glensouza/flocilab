using Amazon.Organizations;
using Amazon.Organizations.Model;
using FlociLab.Aws.Organizations;
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
public sealed class AwsOrganizationsTests : IAsyncLifetime
{
    // Pinned to :latest so the tripwire tracks the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private OrganizationsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new OrganizationsClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok_Before_And_After_An_Organization_Exists()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        OrganizationsDemo demo = new(this.factory);
        using IAmazonOrganizations client = this.factory.Create();

        // A fresh account answers AWSOrganizationsNotInUseException, which still means "up".
        Assert.Equal(ProbeStatus.Ok, (await demo.ProbeAsync(ct)).Status);

        await client.CreateOrganizationAsync(new CreateOrganizationRequest { FeatureSet = OrganizationFeatureSet.ALL }, ct);

        try
        {
            Assert.Equal(ProbeStatus.Ok, (await demo.ProbeAsync(ct)).Status);
        }
        finally
        {
            await client.DeleteOrganizationAsync(new DeleteOrganizationRequest(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new OrganizationsDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("DescribeOrganization", s.Title),
            s => Assert.Equal("CreateOrganization", s.Title),
            s => Assert.Equal("ListRoots", s.Title),
            s =>
            {
                // floci switches SCPs on for a new root; AWS documents a new root as having none.
                Assert.Equal("EnablePolicyType", s.Title);
                Assert.Contains(nameof(PolicyTypeAlreadyEnabledException), s.Response);
            },
            s => Assert.Equal("CreateOrganizationalUnit", s.Title),
            s => Assert.Equal("CreateOrganizationalUnit — refused as a duplicate", s.Title),
            s => Assert.Equal("CreatePolicy", s.Title),
            s => Assert.Equal("AttachPolicy", s.Title),
            s => Assert.Equal("AttachPolicy — refused as a duplicate", s.Title),
            s => Assert.Equal("ListPoliciesForTarget — the policy reads back on the OU", s.Title),
            s => Assert.Equal("TagResource", s.Title),
            s => Assert.Equal("ListTagsForResource — both tags read back", s.Title),
            s => Assert.Equal("DeletePolicy — refused while attached", s.Title),
            s => Assert.Equal("DetachPolicy", s.Title),
            s => Assert.Equal("DeletePolicy", s.Title),
            s => Assert.Equal("DeleteOrganizationalUnit", s.Title),
            s => Assert.Equal("DescribeOrganizationalUnit — refused once deleted", s.Title),
            s => Assert.Equal("DeleteOrganization", s.Title),
            s => Assert.Equal("DescribeOrganization — refused once deleted", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording. The second run only passes if the first deleted the organization
    /// it created — floci answers AlreadyInOrganization to a second CreateOrganization.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_Nothing_Behind()
    {
        OrganizationsDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonOrganizations client = this.factory.Create();

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        await Assert.ThrowsAsync<AWSOrganizationsNotInUseException>(() => client.DescribeOrganizationAsync(new DescribeOrganizationRequest(), ct));
    }

    /// <summary>
    /// An account that already has an organization keeps it: the run reuses it, skips the create and
    /// the delete, and removes only its own OU and policy.
    /// </summary>
    [Fact]
    public async Task Existing_Organization_Is_Reused_And_Left_In_Place()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonOrganizations client = this.factory.Create();

        CreateOrganizationResponse created = await client.CreateOrganizationAsync(new CreateOrganizationRequest { FeatureSet = OrganizationFeatureSet.ALL }, ct);

        try
        {
            List<DemoStep> steps = [];

            await foreach (DemoStep step in new OrganizationsDemo(this.factory).RunAsync(ct))
            {
                steps.Add(step);
            }

            Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
            Assert.DoesNotContain(steps, s => s.Title == "CreateOrganization");
            Assert.DoesNotContain(steps, s => s.Title == "DeleteOrganization");

            DescribeOrganizationResponse after = await client.DescribeOrganizationAsync(new DescribeOrganizationRequest(), ct);
            Assert.Equal(created.Organization.Id, after.Organization.Id);

            ListRootsResponse roots = await client.ListRootsAsync(new ListRootsRequest(), ct);
            ListOrganizationalUnitsForParentResponse units = await client.ListOrganizationalUnitsForParentAsync(new ListOrganizationalUnitsForParentRequest { ParentId = roots.Roots.Single().Id }, ct);
            ListPoliciesResponse policies = await client.ListPoliciesAsync(new ListPoliciesRequest { Filter = PolicyType.SERVICE_CONTROL_POLICY }, ct);

            Assert.Empty(units.OrganizationalUnits ?? []);
            Assert.Equal(["FullAWSAccess"], policies.Policies.Select(p => p.Name));
        }
        finally
        {
            await client.DeleteOrganizationAsync(new DeleteOrganizationRequest(), CancellationToken.None);
        }
    }

    /// <summary>
    /// The cleanup path on its own: a run stopped after the policy was attached — the consumer
    /// walking away, as the page does on dispose — still has an organization, an OU and an attached
    /// policy, and cleanup has to find the first two by name and detach before deleting the third.
    /// </summary>
    [Fact]
    public async Task Abandoned_Run_Is_Cleaned_Up_By_Name()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonOrganizations client = this.factory.Create();

        List<DemoStep> steps = [];

        await foreach (DemoStep step in new OrganizationsDemo(this.factory).RunAsync(ct))
        {
            steps.Add(step);

            if (step.Title == "AttachPolicy")
            {
                break;
            }
        }

        Assert.Equal("AttachPolicy", steps[^1].Title);

        await Assert.ThrowsAsync<AWSOrganizationsNotInUseException>(() => client.DescribeOrganizationAsync(new DescribeOrganizationRequest(), ct));
    }

    /// <summary>
    /// The same abandoned run inside an organization the run does not own, so the organization
    /// survives and the OU and policy cleanup can be seen. The test above cannot see it: floci
    /// deletes an organization that still holds a policy, so "organization gone" proves nothing
    /// about the detach-then-delete.
    /// </summary>
    [Fact]
    public async Task Abandoned_Run_In_An_Existing_Organization_Removes_Its_Ou_And_Policy()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonOrganizations client = this.factory.Create();

        await client.CreateOrganizationAsync(new CreateOrganizationRequest { FeatureSet = OrganizationFeatureSet.ALL }, ct);

        try
        {
            await foreach (DemoStep step in new OrganizationsDemo(this.factory).RunAsync(ct))
            {
                if (step.Title == "AttachPolicy")
                {
                    break;
                }
            }

            ListRootsResponse roots = await client.ListRootsAsync(new ListRootsRequest(), ct);
            ListOrganizationalUnitsForParentResponse units = await client.ListOrganizationalUnitsForParentAsync(new ListOrganizationalUnitsForParentRequest { ParentId = roots.Roots.Single().Id }, ct);
            ListPoliciesResponse policies = await client.ListPoliciesAsync(new ListPoliciesRequest { Filter = PolicyType.SERVICE_CONTROL_POLICY }, ct);

            Assert.Empty(units.OrganizationalUnits ?? []);
            Assert.Equal(["FullAWSAccess"], policies.Policies.Select(p => p.Name));
        }
        finally
        {
            await client.DeleteOrganizationAsync(new DeleteOrganizationRequest(), CancellationToken.None);
        }
    }

    /// <summary>
    /// An organization the run did not create keeps its policy types: with SCPs off, the run stops
    /// at EnablePolicyType rather than switching them on, and creates nothing.
    /// </summary>
    [Fact]
    public async Task Existing_Organization_Without_Scps_Is_Not_Changed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonOrganizations client = this.factory.Create();

        await client.CreateOrganizationAsync(new CreateOrganizationRequest { FeatureSet = OrganizationFeatureSet.ALL }, ct);

        try
        {
            string rootId = (await client.ListRootsAsync(new ListRootsRequest(), ct)).Roots.Single().Id;
            await client.DisablePolicyTypeAsync(new DisablePolicyTypeRequest { RootId = rootId, PolicyType = PolicyType.SERVICE_CONTROL_POLICY }, ct);

            List<DemoStep> steps = [];

            await foreach (DemoStep step in new OrganizationsDemo(this.factory).RunAsync(ct))
            {
                steps.Add(step);
            }

            Assert.Equal(["DescribeOrganization", "ListRoots", "EnablePolicyType"], steps.Select(s => s.Title));
            Assert.False(steps[^1].Succeeded);

            ListRootsResponse after = await client.ListRootsAsync(new ListRootsRequest(), ct);
            Assert.Empty(after.Roots.Single().PolicyTypes ?? []);
        }
        finally
        {
            await client.DeleteOrganizationAsync(new DeleteOrganizationRequest(), CancellationToken.None);
        }
    }

    /// <summary>
    /// Tripwire (plan §14). floci 2.1.0 does not validate an SCP document: <c>CreatePolicy</c>
    /// accepts any string as <c>Content</c>, where real AWS answers
    /// <c>MalformedPolicyDocumentException</c>. When this fails, upstream started validating — add
    /// a refused-malformed-policy step to the sample.
    /// </summary>
    [Fact]
    public async Task Floci_Accepts_A_Malformed_Policy_Document()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonOrganizations client = this.factory.Create();

        await client.CreateOrganizationAsync(new CreateOrganizationRequest { FeatureSet = OrganizationFeatureSet.ALL }, ct);

        try
        {
            CreatePolicyResponse created = await client.CreatePolicyAsync(
                new CreatePolicyRequest
                {
                    Name = $"flocilab-bad-{Guid.NewGuid().ToString("N")[..8]}",
                    Description = "not JSON",
                    Type = PolicyType.SERVICE_CONTROL_POLICY,
                    Content = "not json",
                }, ct);

            Assert.Equal("not json", created.Policy.Content);
        }
        finally
        {
            await client.DeleteOrganizationAsync(new DeleteOrganizationRequest(), CancellationToken.None);
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
        OrganizationsDemo demo = new(this.factory);
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
        OrganizationsDemo demo = new(new OrganizationsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
