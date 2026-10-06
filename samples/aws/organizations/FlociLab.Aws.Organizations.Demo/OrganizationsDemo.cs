using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Organizations;
using Amazon.Organizations.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Organizations;

/// <summary>
/// Stands up an organization if the account has none, then builds a tagged organizational unit and a
/// service control policy under its root, attaches one to the other, reads both back, and tears it
/// all down again — refusing a duplicate OU, a duplicate attachment, an in-use delete and every
/// delete-twice along the way. Ordinary AWSSDK.Organizations code — the only emulator-aware line in
/// the sample is in <see cref="OrganizationsClientFactory"/>. Member accounts are left out
/// on purpose: on real AWS <c>CreateAccount</c> is asynchronous and makes an account that can only be
/// closed (90 days pending, with a quota on closures), so a demo that ran twice would use up
/// something that cannot be given back. floci answers <c>CreateAccount</c> with <c>SUCCEEDED</c> at
/// once (docs/BLAZOR-PLAN.md §14).
/// </summary>
public sealed class OrganizationsDemo(OrganizationsClientFactory factory) : IServiceDemo
{
    private const string TargetPrefix = "AWSOrganizationsV20161128";

    public string Provider => CloudProvider.Aws;

    public string Slug => "organizations";

    public string DisplayName => "Organizations";

    public string Category => "Identity and access";

    public string Route => "/aws/organizations";

    /// <summary>
    /// DescribeOrganization. An account that is not in an organization answers
    /// <c>AWSOrganizationsNotInUseException</c> — the service answered, so that is an Ok probe, not
    /// an error: a fresh floci container is in exactly that state.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonOrganizations client = factory.Create();
            DescribeOrganizationResponse response = await client.DescribeOrganizationAsync(new DescribeOrganizationRequest(), ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"DescribeOrganization returned {response.Organization?.Id}.");
        }
        catch (AWSOrganizationsNotInUseException)
        {
            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), "DescribeOrganization answered: the account is not in an organization yet.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonOrganizations client = factory.Create();

        // Unique per run, so two runs never collide. OUs and policies are addressed by id, but both
        // are found again by these names, which is all cleanup needs if a response was lost.
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string ouName = $"flocilab-ou-{suffix}";
        string policyName = $"flocilab-scp-{suffix}";

        string? rootId = null;
        string? ouId = null;
        string? policyId = null;

        // Claimed before each call, not after: a request that lands while its response is lost (the
        // page's Dispose cancels mid-flight) still created the resource.
        bool scpEnabled = false;
        bool orgCreateAttempted = false;
        bool orgDeleted = false;
        bool ouCreateAttempted = false;
        bool ouDeleted = false;
        bool policyCreateAttempted = false;
        bool policyDeleted = false;

        List<DemoStep> cleanup = [];

        try
        {
            // An account is in at most one organization and real AWS will not make a second, so the
            // run only owns — and only deletes — an organization it created itself.
            bool existingOrganization = false;

            DemoStep describeStep = await RunStepAsync(
                "DescribeOrganization",
                this.Wire("DescribeOrganization", "client.DescribeOrganizationAsync(new DescribeOrganizationRequest())"),
                async () =>
                {
                    try
                    {
                        DescribeOrganizationResponse response = await client.DescribeOrganizationAsync(new DescribeOrganizationRequest(), ct).ConfigureAwait(false);
                        existingOrganization = true;

                        return $"HTTP {(int)response.HttpStatusCode} — the account already belongs to {response.Organization.Id}; this run reuses it and leaves it in place.";
                    }
                    // The expected answer on a fresh account, not a failure of the step.
                    catch (AWSOrganizationsNotInUseException ex)
                    {
                        return $"No organization yet — {ex.GetType().Name}: {ex.Message}";
                    }
                }).ConfigureAwait(false);

            yield return describeStep;

            if (!describeStep.Succeeded)
            {
                yield break;
            }

            if (!existingOrganization)
            {
                DemoStep createOrgStep = await RunStepAsync(
                    "CreateOrganization",
                    this.Wire("CreateOrganization", "client.CreateOrganizationAsync(new CreateOrganizationRequest { FeatureSet = ALL })"),
                    async () =>
                    {
                        orgCreateAttempted = true;

                        try
                        {
                            CreateOrganizationResponse response = await client.CreateOrganizationAsync(new CreateOrganizationRequest { FeatureSet = OrganizationFeatureSet.ALL }, ct).ConfigureAwait(false);

                            return $"HTTP {(int)response.HttpStatusCode} — {response.Organization.Arn}";
                        }
                        // Another run created one between DescribeOrganization and here. That
                        // organization is not this run's, so the finally must not delete it.
                        catch (AlreadyInOrganizationException)
                        {
                            orgCreateAttempted = false;
                            throw;
                        }
                    }).ConfigureAwait(false);

                yield return createOrgStep;

                if (!createOrgStep.Succeeded)
                {
                    yield break;
                }
            }

            DemoStep rootsStep = await RunStepAsync(
                "ListRoots",
                this.Wire("ListRoots", "client.ListRootsAsync(new ListRootsRequest())"),
                async () =>
                {
                    ListRootsResponse response = await client.ListRootsAsync(new ListRootsRequest(), ct).ConfigureAwait(false);
                    Root root = (response.Roots ?? []).Single();
                    string policyTypes = string.Join(", ", (root.PolicyTypes ?? []).Select(t => $"{t.Type}={t.Status}"));

                    rootId = root.Id;
                    scpEnabled = (root.PolicyTypes ?? []).Exists(t => t.Type == PolicyType.SERVICE_CONTROL_POLICY && t.Status == PolicyTypeStatus.ENABLED);

                    return $"HTTP {(int)response.HttpStatusCode} — {root.Id} ({root.Name}), policy types: {policyTypes}";
                }).ConfigureAwait(false);

            yield return rootsStep;

            if (!rootsStep.Succeeded)
            {
                yield break;
            }

            // SCPs are a policy type the root has to have switched on, and AWS documents a new
            // root as having none. floci switches SCPs on for every new root, so on floci this
            // step only ever reports "already enabled" — the call is here because real AWS needs it.
            if (orgCreateAttempted || !scpEnabled)
            {
                DemoStep enableStep = await RunStepAsync(
                    "EnablePolicyType",
                    this.Wire("EnablePolicyType", $"client.EnablePolicyTypeAsync(new EnablePolicyTypeRequest {{ RootId = \"{rootId}\", PolicyType = SERVICE_CONTROL_POLICY }})"),
                    async () =>
                    {
                        if (!orgCreateAttempted)
                        {
                            throw new InvalidOperationException($"SCPs are not enabled on {rootId}, and this run will not change the policy types of an organization it did not create.");
                        }

                        try
                        {
                            EnablePolicyTypeResponse response = await client.EnablePolicyTypeAsync(
                                new EnablePolicyTypeRequest { RootId = rootId, PolicyType = PolicyType.SERVICE_CONTROL_POLICY }, ct).ConfigureAwait(false);

                            return $"HTTP {(int)response.HttpStatusCode} — SCPs enabled on {rootId}";
                        }
                        // floci's answer: the new root came with SCPs on. Not a failure of the step.
                        catch (PolicyTypeAlreadyEnabledException ex)
                        {
                            return $"Already enabled — {ex.GetType().Name}: {ex.Message}";
                        }
                    }).ConfigureAwait(false);

                yield return enableStep;

                if (!enableStep.Succeeded)
                {
                    yield break;
                }
            }

            DemoStep createOuStep = await RunStepAsync(
                "CreateOrganizationalUnit",
                this.Wire("CreateOrganizationalUnit", $"client.CreateOrganizationalUnitAsync(new CreateOrganizationalUnitRequest {{ ParentId = \"{rootId}\", Name = \"{ouName}\", Tags = [lab=flocilab] }})"),
                async () =>
                {
                    ouCreateAttempted = true;

                    CreateOrganizationalUnitResponse response = await client.CreateOrganizationalUnitAsync(
                        new CreateOrganizationalUnitRequest
                        {
                            ParentId = rootId,
                            Name = ouName,
                            Tags = [new Tag { Key = "lab", Value = "flocilab" }],
                        }, ct).ConfigureAwait(false);

                    ouId = response.OrganizationalUnit.Id;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.OrganizationalUnit.Arn}";
                }).ConfigureAwait(false);

            yield return createOuStep;

            if (!createOuStep.Succeeded)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "CreateOrganizationalUnit — refused as a duplicate",
                this.Wire("CreateOrganizationalUnit", $"client.CreateOrganizationalUnitAsync(new CreateOrganizationalUnitRequest {{ ParentId = \"{rootId}\", Name = \"{ouName}\" }})   // expect DuplicateOrganizationalUnitException"),
                async () =>
                {
                    CreateOrganizationalUnitResponse duplicate;

                    try
                    {
                        duplicate = await client.CreateOrganizationalUnitAsync(new CreateOrganizationalUnitRequest { ParentId = rootId, Name = ouName }, ct).ConfigureAwait(false);
                    }
                    // The refusal is the point of the step, not a failure of it.
                    catch (DuplicateOrganizationalUnitException ex)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException($"a second OU named {ouName} was accepted ({duplicate.OrganizationalUnit.Id}).");
                }).ConfigureAwait(false);

            const string scp = """{"Version":"2012-10-17","Statement":[{"Effect":"Deny","Action":"s3:DeleteBucket","Resource":"*"}]}""";

            DemoStep createPolicyStep = await RunStepAsync(
                "CreatePolicy",
                this.Wire("CreatePolicy", $"client.CreatePolicyAsync(new CreatePolicyRequest {{ Name = \"{policyName}\", Type = SERVICE_CONTROL_POLICY, Content = {scp}, Tags = [lab=flocilab] }})"),
                async () =>
                {
                    policyCreateAttempted = true;

                    CreatePolicyResponse response = await client.CreatePolicyAsync(
                        new CreatePolicyRequest
                        {
                            Name = policyName,
                            Description = "Created by FlociLab: nobody may delete a bucket.",
                            Type = PolicyType.SERVICE_CONTROL_POLICY,
                            Content = scp,
                            Tags = [new Tag { Key = "lab", Value = "flocilab" }],
                        }, ct).ConfigureAwait(false);

                    policyId = response.Policy.PolicySummary.Id;

                    return $"HTTP {(int)response.HttpStatusCode} — {response.Policy.PolicySummary.Arn}";
                }).ConfigureAwait(false);

            yield return createPolicyStep;

            if (!createPolicyStep.Succeeded)
            {
                yield break;
            }

            DemoStep attachStep = await RunStepAsync(
                "AttachPolicy",
                this.Wire("AttachPolicy", $"client.AttachPolicyAsync(new AttachPolicyRequest {{ PolicyId = \"{policyId}\", TargetId = \"{ouId}\" }})"),
                async () =>
                {
                    AttachPolicyResponse response = await client.AttachPolicyAsync(new AttachPolicyRequest { PolicyId = policyId, TargetId = ouId }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return attachStep;

            if (!attachStep.Succeeded)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "AttachPolicy — refused as a duplicate",
                this.Wire("AttachPolicy", $"client.AttachPolicyAsync(new AttachPolicyRequest {{ PolicyId = \"{policyId}\", TargetId = \"{ouId}\" }})   // expect DuplicatePolicyAttachmentException"),
                async () =>
                {
                    try
                    {
                        await client.AttachPolicyAsync(new AttachPolicyRequest { PolicyId = policyId, TargetId = ouId }, ct).ConfigureAwait(false);
                    }
                    catch (DuplicatePolicyAttachmentException ex)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException("the same policy was attached to the same OU twice.");
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListPoliciesForTarget — the policy reads back on the OU",
                this.Wire("ListPoliciesForTarget", $"client.ListPoliciesForTargetAsync(new ListPoliciesForTargetRequest {{ TargetId = \"{ouId}\", Filter = SERVICE_CONTROL_POLICY }})"),
                async () =>
                {
                    ListPoliciesForTargetResponse response = await client.ListPoliciesForTargetAsync(
                        new ListPoliciesForTargetRequest { TargetId = ouId, Filter = PolicyType.SERVICE_CONTROL_POLICY }, ct).ConfigureAwait(false);
                    List<PolicySummary> policies = response.Policies ?? [];

                    if (!policies.Exists(p => p.Id == policyId))
                    {
                        throw new InvalidOperationException($"{policyName} ({policyId}) is attached to {ouId} but ListPoliciesForTarget does not return it.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", policies.Select(p => p.Name))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "TagResource",
                this.Wire("TagResource", $"client.TagResourceAsync(new TagResourceRequest {{ ResourceId = \"{ouId}\", Tags = [team=platform] }})"),
                async () =>
                {
                    TagResourceResponse response = await client.TagResourceAsync(
                        new TagResourceRequest { ResourceId = ouId, Tags = [new Tag { Key = "team", Value = "platform" }] }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListTagsForResource — both tags read back",
                this.Wire("ListTagsForResource", $"client.ListTagsForResourceAsync(new ListTagsForResourceRequest {{ ResourceId = \"{ouId}\" }})"),
                async () =>
                {
                    ListTagsForResourceResponse response = await client.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceId = ouId }, ct).ConfigureAwait(false);
                    Dictionary<string, string> tags = (response.Tags ?? []).ToDictionary(t => t.Key, t => t.Value);
                    string text = string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"));

                    if (tags.GetValueOrDefault("lab") != "flocilab" || tags.GetValueOrDefault("team") != "platform")
                    {
                        throw new InvalidOperationException($"expected lab=flocilab and team=platform on {ouId} but got: {text}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {text}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeletePolicy — refused while attached",
                this.Wire("DeletePolicy", $"client.DeletePolicyAsync(new DeletePolicyRequest {{ PolicyId = \"{policyId}\" }})   // expect PolicyInUseException"),
                async () =>
                {
                    try
                    {
                        await client.DeletePolicyAsync(new DeletePolicyRequest { PolicyId = policyId }, ct).ConfigureAwait(false);
                    }
                    catch (PolicyInUseException ex)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    // The delete succeeded, so the finally has nothing left to do for it.
                    policyDeleted = true;

                    throw new InvalidOperationException("a policy that is still attached was deleted.");
                }).ConfigureAwait(false);

            // An in-use delete the server wrongly accepted already removed the policy; detaching and
            // deleting it again would only bury that one real failure under two more red steps.
            if (!policyDeleted)
            {
                yield return await RunStepAsync(
                    "DetachPolicy",
                    this.Wire("DetachPolicy", $"client.DetachPolicyAsync(new DetachPolicyRequest {{ PolicyId = \"{policyId}\", TargetId = \"{ouId}\" }})"),
                    async () =>
                    {
                        DetachPolicyResponse response = await client.DetachPolicyAsync(new DetachPolicyRequest { PolicyId = policyId, TargetId = ouId }, ct).ConfigureAwait(false);

                        return $"HTTP {(int)response.HttpStatusCode}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "DeletePolicy",
                    this.Wire("DeletePolicy", $"client.DeletePolicyAsync(new DeletePolicyRequest {{ PolicyId = \"{policyId}\" }})"),
                    async () =>
                    {
                        DeletePolicyResponse response = await client.DeletePolicyAsync(new DeletePolicyRequest { PolicyId = policyId }, ct).ConfigureAwait(false);

                        policyDeleted = true;

                        return $"HTTP {(int)response.HttpStatusCode}";
                    }).ConfigureAwait(false);
            }

            yield return await RunStepAsync(
                "DeleteOrganizationalUnit",
                this.Wire("DeleteOrganizationalUnit", $"client.DeleteOrganizationalUnitAsync(new DeleteOrganizationalUnitRequest {{ OrganizationalUnitId = \"{ouId}\" }})"),
                async () =>
                {
                    DeleteOrganizationalUnitResponse response = await client.DeleteOrganizationalUnitAsync(new DeleteOrganizationalUnitRequest { OrganizationalUnitId = ouId }, ct).ConfigureAwait(false);

                    ouDeleted = true;

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DescribeOrganizationalUnit — refused once deleted",
                this.Wire("DescribeOrganizationalUnit", $"client.DescribeOrganizationalUnitAsync(new DescribeOrganizationalUnitRequest {{ OrganizationalUnitId = \"{ouId}\" }})   // expect OrganizationalUnitNotFoundException"),
                async () =>
                {
                    try
                    {
                        await client.DescribeOrganizationalUnitAsync(new DescribeOrganizationalUnitRequest { OrganizationalUnitId = ouId }, ct).ConfigureAwait(false);
                    }
                    catch (OrganizationalUnitNotFoundException ex)
                    {
                        return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException("the OU can still be described after it was deleted.");
                }).ConfigureAwait(false);

            // Only once the policy and the OU are gone. floci deletes an organization that still
            // holds a policy, and the finally's by-name cleanup then has no organization to search.
            // Anything left over is the finally's job, which removes it before the organization.
            if (orgCreateAttempted && policyDeleted && ouDeleted)
            {
                DemoStep deleteOrgStep = await RunStepAsync(
                    "DeleteOrganization",
                    this.Wire("DeleteOrganization", "client.DeleteOrganizationAsync(new DeleteOrganizationRequest())"),
                    async () =>
                    {
                        DeleteOrganizationResponse response = await client.DeleteOrganizationAsync(new DeleteOrganizationRequest(), ct).ConfigureAwait(false);

                        orgDeleted = true;

                        return $"HTTP {(int)response.HttpStatusCode}";
                    }).ConfigureAwait(false);

                yield return deleteOrgStep;

                if (deleteOrgStep.Succeeded)
                {
                    yield return await RunStepAsync(
                        "DescribeOrganization — refused once deleted",
                        this.Wire("DescribeOrganization", "client.DescribeOrganizationAsync(new DescribeOrganizationRequest())   // expect AWSOrganizationsNotInUseException"),
                        async () =>
                        {
                            try
                            {
                                await client.DescribeOrganizationAsync(new DescribeOrganizationRequest(), ct).ConfigureAwait(false);
                            }
                            catch (AWSOrganizationsNotInUseException ex)
                            {
                                return $"Refused as expected — {ex.GetType().Name}: {ex.Message}";
                            }

                            throw new InvalidOperationException("the organization can still be described after it was deleted.");
                        }).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. Policy first, then the OU it was
            // attached to, then the organization they lived in. The steps it produces are yielded
            // below — an iterator may not yield from inside a finally.
            if (policyCreateAttempted && !policyDeleted)
            {
                cleanup.Add(await this.DeletePolicyByNameAsync(client, policyName, ct).ConfigureAwait(false));
            }

            if (ouCreateAttempted && !ouDeleted)
            {
                cleanup.Add(await this.DeleteOrganizationalUnitByNameAsync(client, ouName, ct).ConfigureAwait(false));
            }

            if (orgCreateAttempted && !orgDeleted)
            {
                cleanup.Add(await this.DeleteOrganizationAsync(client, ct).ConfigureAwait(false));
            }
        }

        foreach (DemoStep step in cleanup)
        {
            yield return step;
        }
    }

    /// <summary>
    /// The AWS SDK reports both of the interesting failures inside an
    /// <see cref="AmazonServiceException"/>, so <see cref="ProbeResult.FromException"/> — which
    /// inspects only the outermost exception — cannot classify them on its own. A 501 arrives as
    /// a status code on the exception; a refused connection arrives with no status code at all
    /// and a transport exception underneath.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real Organizations would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes what exists.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>The wire-level request shown beside an SDK call: every operation is a JSON 1.1 POST to the root path.</summary>
    private string Wire(string operation, string call) => $"POST {factory.ServiceUrl}/\nX-Amz-Target: {TargetPrefix}.{operation}\n{call}";

    /// <summary>Pages <c>ListOrganizationalUnitsForParent</c>, since a real organization's OU can be on any page.</summary>
    private static async Task<OrganizationalUnit?> FindOrganizationalUnitAsync(IAmazonOrganizations client, string parentId, string name)
    {
        string? token = null;

        do
        {
            ListOrganizationalUnitsForParentResponse page = await client.ListOrganizationalUnitsForParentAsync(
                new ListOrganizationalUnitsForParentRequest { ParentId = parentId, NextToken = token }, CancellationToken.None).ConfigureAwait(false);
            OrganizationalUnit? match = page.OrganizationalUnits?.Find(u => u.Name == name);

            if (match is not null)
            {
                return match;
            }

            token = page.NextToken;
        }
        while (token is not null);

        return null;
    }

    /// <summary>Pages <c>ListPolicies</c>, since a real organization's policy can be on any page.</summary>
    private static async Task<PolicySummary?> FindPolicyAsync(IAmazonOrganizations client, string name)
    {
        string? token = null;

        do
        {
            ListPoliciesResponse page = await client.ListPoliciesAsync(
                new ListPoliciesRequest { Filter = PolicyType.SERVICE_CONTROL_POLICY, NextToken = token }, CancellationToken.None).ConfigureAwait(false);
            PolicySummary? match = page.Policies?.Find(p => p.Name == name);

            if (match is not null)
            {
                return match;
            }

            token = page.NextToken;
        }
        while (token is not null);

        return null;
    }

    /// <summary>
    /// Cleanup uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a
    /// policy to remove. A policy cannot be deleted while attached, so this detaches it from every
    /// target first; a create that never landed finds nothing, which is a truthful "nothing to remove".
    /// </summary>
    private async Task<DemoStep> DeletePolicyByNameAsync(IAmazonOrganizations client, string name, CancellationToken ct)
    {
        string request = this.Wire("DeletePolicy", $"client.DeletePolicyAsync(new DeletePolicyRequest {{ PolicyId = <id of \"{name}\"> }})");

        return await RunStepAsync("DeletePolicy — cleanup", request, async () =>
        {
            PolicySummary? policy = await FindPolicyAsync(client, name).ConfigureAwait(false);

            if (policy is null)
            {
                return $"No policy named {name} exists — nothing to remove.";
            }

            // Collected before detaching, so a detach never shifts what the next page holds.
            List<string> targetIds = [];
            string? token = null;

            do
            {
                ListTargetsForPolicyResponse page = await client.ListTargetsForPolicyAsync(
                    new ListTargetsForPolicyRequest { PolicyId = policy.Id, NextToken = token }, CancellationToken.None).ConfigureAwait(false);
                targetIds.AddRange((page.Targets ?? []).Select(t => t.TargetId));
                token = page.NextToken;
            }
            while (token is not null);

            foreach (string targetId in targetIds)
            {
                await client.DetachPolicyAsync(new DetachPolicyRequest { PolicyId = policy.Id, TargetId = targetId }, CancellationToken.None).ConfigureAwait(false);
            }

            DeletePolicyResponse response = await client.DeletePolicyAsync(new DeletePolicyRequest { PolicyId = policy.Id }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — deleted {name}" + CancelledNote(ct);
        }).ConfigureAwait(false);
    }

    /// <summary>The OU lives under the organization's single root, which a cancelled run may not have read yet.</summary>
    private async Task<DemoStep> DeleteOrganizationalUnitByNameAsync(IAmazonOrganizations client, string name, CancellationToken ct)
    {
        string request = this.Wire("DeleteOrganizationalUnit", $"client.DeleteOrganizationalUnitAsync(new DeleteOrganizationalUnitRequest {{ OrganizationalUnitId = <id of \"{name}\"> }})");

        return await RunStepAsync("DeleteOrganizationalUnit — cleanup", request, async () =>
        {
            ListRootsResponse roots = await client.ListRootsAsync(new ListRootsRequest(), CancellationToken.None).ConfigureAwait(false);
            OrganizationalUnit? unit = await FindOrganizationalUnitAsync(client, (roots.Roots ?? []).Single().Id, name).ConfigureAwait(false);

            if (unit is null)
            {
                return $"No OU named {name} exists — nothing to remove.";
            }

            DeleteOrganizationalUnitResponse response = await client.DeleteOrganizationalUnitAsync(new DeleteOrganizationalUnitRequest { OrganizationalUnitId = unit.Id }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — deleted {name}" + CancelledNote(ct);
        }).ConfigureAwait(false);
    }

    /// <summary>Only reached for an organization this run created, so removing it is removing its own work.</summary>
    private async Task<DemoStep> DeleteOrganizationAsync(IAmazonOrganizations client, CancellationToken ct)
    {
        string request = this.Wire("DeleteOrganization", "client.DeleteOrganizationAsync(new DeleteOrganizationRequest())");

        return await RunStepAsync("DeleteOrganization — cleanup", request, async () =>
        {
            try
            {
                DeleteOrganizationResponse response = await client.DeleteOrganizationAsync(new DeleteOrganizationRequest(), CancellationToken.None).ConfigureAwait(false);

                return $"HTTP {(int)response.HttpStatusCode} — deleted the organization" + CancelledNote(ct);
            }
            // Not a failure: the create never landed, and the server says so.
            catch (AWSOrganizationsNotInUseException)
            {
                return "The account is not in an organization — nothing to remove.";
            }
        }).ConfigureAwait(false);
    }

    private static string CancelledNote(CancellationToken ct) => ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
}
