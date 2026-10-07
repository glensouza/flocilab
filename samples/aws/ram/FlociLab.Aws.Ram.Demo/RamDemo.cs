using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.RAM;
using Amazon.RAM.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Ram;

/// <summary>
/// Creates a tagged resource share, tags it again, renames it, associates a principal and a
/// resource, reads each change back, disassociates the principal and deletes the share — then
/// shows the second delete being refused. Ordinary AWSSDK.RAM code — the only emulator-aware line in
/// the sample is in <see cref="RamClientFactory"/>. The principal and resource are attached only
/// against the emulator: on real AWS a principal outside the account is sent an invitation, and
/// floci takes any ARN as a resource, which real RAM does not (docs/BLAZOR-PLAN.md §14).
/// </summary>
public sealed class RamDemo(RamClientFactory factory) : IServiceDemo
{
    private const string PrincipalId = "111122223333";
    private const string ResourceArn = "arn:aws:ec2:us-east-1:000000000000:subnet/subnet-flocilab";

    public string Provider => CloudProvider.Aws;

    public string Slug => "ram";

    public string DisplayName => "Resource Access Manager";

    public string Category => "Identity and access";

    public string Route => "/aws/ram";

    /// <summary>GetResourceShares. An account with no shares answers an empty list, which still means the service is up.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonRAM client = factory.Create();
            GetResourceSharesResponse response = await client.GetResourceSharesAsync(new GetResourceSharesRequest { ResourceOwner = ResourceOwner.SELF }, ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"GetResourceShares returned {(response.ResourceShares ?? []).Count} share(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonRAM client = factory.Create();

        // Unique per run, so two runs never collide. Shares are addressed by ARN, but a share is
        // found again by this name, which is all cleanup needs if a response was lost. The renamed
        // share keeps the prefix, so one search covers both names.
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string shareName = $"flocilab-share-{suffix}";
        string renamed = $"{shareName}-renamed";
        bool attachPrincipal = factory.UseEmulator;

        List<Tag> tags = [new Tag { Key = "env", Value = "lab" }, new Tag { Key = "owner", Value = "flocilab" }];

        string? shareArn = null;

        // Claimed before the call, not after: a request that lands while its response is lost (the
        // page's Dispose cancels mid-flight) still created the share.
        bool createAttempted = false;
        bool deleted = false;

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep createStep = await RunStepAsync(
                "CreateResourceShare",
                this.Wire("POST", "/createresourceshare", $"client.CreateResourceShareAsync(new CreateResourceShareRequest {{ Name = \"{shareName}\", AllowExternalPrincipals = {(attachPrincipal ? "true" : "false")}, Tags = [env=lab, owner=flocilab] }})"),
                async () =>
                {
                    createAttempted = true;

                    // An external principal needs AllowExternalPrincipals; against real AWS no
                    // principal is attached, so the share stays closed to other accounts.
                    CreateResourceShareResponse response = await client.CreateResourceShareAsync(
                        new CreateResourceShareRequest { Name = shareName, AllowExternalPrincipals = attachPrincipal, Tags = tags }, ct).ConfigureAwait(false);
                    ResourceShare share = response.ResourceShare;

                    shareArn = share.ResourceShareArn ?? throw new InvalidOperationException("CreateResourceShare returned no ARN.");

                    if (share.Status != ResourceShareStatus.ACTIVE)
                    {
                        throw new InvalidOperationException($"the new share is {share.Status}, not ACTIVE.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {share.ResourceShareArn}, status {share.Status}\n{tags.Count} tag(s) sent, {(share.Tags ?? []).Count} on the share it returned";
                }).ConfigureAwait(false);

            yield return createStep;

            if (!createStep.Succeeded)
            {
                yield break;
            }

            // A succeeded create step always set the ARN; it throws before returning otherwise.
            string arn = shareArn!;

            yield return await RunStepAsync(
                "GetResourceShares — what the create left behind",
                this.Wire("POST", "/getresourceshares", $"client.GetResourceSharesAsync(new GetResourceSharesRequest {{ ResourceOwner = SELF, ResourceShareArns = [\"{arn}\"] }})"),
                async () =>
                {
                    ResourceShare share = await FindShareAsync(client, arn, ct).ConfigureAwait(false);
                    int kept = tags.Count(t => (share.Tags ?? []).Exists(s => s.Key == t.Key && s.Value == t.Value));

                    // Reported, not thrown: what the create stored is an observation. The TagResource
                    // step below is the one that has to leave both tags readable.
                    return kept == tags.Count
                        ? $"{share.Name}, {share.Status} — all {kept} tag(s) sent with CreateResourceShare are on the share."
                        : $"{share.Name}, {share.Status} — {kept} of {tags.Count} tag(s) sent with CreateResourceShare are on the share.\nNOTE: the tags on the create request were not stored; tagging afterwards is what sticks.";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "TagResource",
                this.Wire("POST", "/tagresource", $"client.TagResourceAsync(new TagResourceRequest {{ ResourceShareArn = \"{arn}\", Tags = [env=lab, owner=flocilab] }})"),
                async () =>
                {
                    TagResourceResponse response = await client.TagResourceAsync(new TagResourceRequest { ResourceShareArn = arn, Tags = tags }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetResourceShares — both tags read back",
                this.Wire("POST", "/getresourceshares", $"client.GetResourceSharesAsync(new GetResourceSharesRequest {{ ResourceOwner = SELF, ResourceShareArns = [\"{arn}\"] }})"),
                async () =>
                {
                    ResourceShare share = await FindShareAsync(client, arn, ct).ConfigureAwait(false);
                    List<Tag> present = share.Tags ?? [];
                    List<string> missing = [.. tags.Where(t => !present.Exists(s => s.Key == t.Key && s.Value == t.Value)).Select(t => $"{t.Key}={t.Value}")];

                    if (missing.Count != 0)
                    {
                        throw new InvalidOperationException($"TagResource returned, but {string.Join(", ", missing)} did not read back.");
                    }

                    return $"{share.Name} — {string.Join(", ", present.Select(t => $"{t.Key}={t.Value}"))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UpdateResourceShare — rename",
                this.Wire("POST", "/updateresourceshare", $"client.UpdateResourceShareAsync(new UpdateResourceShareRequest {{ ResourceShareArn = \"{arn}\", Name = \"{renamed}\" }})"),
                async () =>
                {
                    UpdateResourceShareResponse response = await client.UpdateResourceShareAsync(new UpdateResourceShareRequest { ResourceShareArn = arn, Name = renamed }, ct).ConfigureAwait(false);
                    ResourceShare share = await FindShareAsync(client, arn, ct).ConfigureAwait(false);

                    if (share.Name != renamed)
                    {
                        throw new InvalidOperationException($"UpdateResourceShare returned HTTP {(int)response.HttpStatusCode}, but the share is still named {share.Name}.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — the share reads back as {share.Name}";
                }).ConfigureAwait(false);

            if (attachPrincipal)
            {
                yield return await RunStepAsync(
                    "AssociateResourceShare — a principal and a resource",
                    this.Wire("POST", "/associateresourceshare", $"client.AssociateResourceShareAsync(new AssociateResourceShareRequest {{ ResourceShareArn = \"{arn}\", Principals = [\"{PrincipalId}\"], ResourceArns = [\"{ResourceArn}\"] }})"),
                    async () =>
                    {
                        AssociateResourceShareResponse response = await client.AssociateResourceShareAsync(
                            new AssociateResourceShareRequest { ResourceShareArn = arn, Principals = [PrincipalId], ResourceArns = [ResourceArn] }, ct).ConfigureAwait(false);
                        List<ResourceShareAssociation> associations = response.ResourceShareAssociations ?? [];
                        string[] entities = [PrincipalId, ResourceArn];

                        foreach (string entity in entities)
                        {
                            if (!associations.Exists(a => a.ResourceShareArn == arn && a.AssociatedEntity == entity && a.Status == ResourceShareAssociationStatus.ASSOCIATED))
                            {
                                throw new InvalidOperationException($"{entity} did not come back ASSOCIATED on {arn}.");
                            }
                        }

                        return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", associations.Where(a => a.ResourceShareArn == arn).Select(a => $"{a.AssociationType} {a.AssociatedEntity} {a.Status}"))}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "ListPrincipals and ListResources — both read back",
                    this.Wire("POST", "/listprincipals", $"client.ListPrincipalsAsync(new ListPrincipalsRequest {{ ResourceOwner = SELF, ResourceShareArns = [\"{arn}\"] }})\nPOST {factory.ServiceUrl}/listresources\nclient.ListResourcesAsync(new ListResourcesRequest {{ ResourceOwner = SELF, ResourceShareArns = [\"{arn}\"] }})"),
                    async () =>
                    {
                        List<Principal> principals = await ListPrincipalsAsync(client, arn, ct).ConfigureAwait(false);
                        List<Resource> resources = await ListResourcesAsync(client, arn, ct).ConfigureAwait(false);

                        if (!principals.Exists(p => p.Id == PrincipalId))
                        {
                            throw new InvalidOperationException($"ListPrincipals does not list {PrincipalId} on the share.");
                        }

                        if (!resources.Exists(r => r.Arn == ResourceArn))
                        {
                            throw new InvalidOperationException($"ListResources does not list {ResourceArn} on the share.");
                        }

                        return $"principals: {string.Join(", ", principals.Select(p => p.Id))}\nresources: {string.Join(", ", resources.Select(r => $"{r.Arn} ({r.Type})"))}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "DisassociateResourceShare — the principal",
                    this.Wire("POST", "/disassociateresourceshare", $"client.DisassociateResourceShareAsync(new DisassociateResourceShareRequest {{ ResourceShareArn = \"{arn}\", Principals = [\"{PrincipalId}\"] }})"),
                    async () =>
                    {
                        DisassociateResourceShareResponse response = await client.DisassociateResourceShareAsync(
                            new DisassociateResourceShareRequest { ResourceShareArn = arn, Principals = [PrincipalId] }, ct).ConfigureAwait(false);

                        if (!(response.ResourceShareAssociations ?? []).Exists(a => a.AssociatedEntity == PrincipalId && a.Status == ResourceShareAssociationStatus.DISASSOCIATED))
                        {
                            throw new InvalidOperationException($"{PrincipalId} did not come back DISASSOCIATED.");
                        }

                        List<Principal> remaining = await ListPrincipalsAsync(client, arn, ct).ConfigureAwait(false);

                        if (remaining.Exists(p => p.Id == PrincipalId))
                        {
                            throw new InvalidOperationException($"{PrincipalId} is still listed on the share after DisassociateResourceShare.");
                        }

                        return $"HTTP {(int)response.HttpStatusCode} — {PrincipalId} DISASSOCIATED and no longer listed";
                    }).ConfigureAwait(false);
            }

            DemoStep deleteStep = await RunStepAsync(
                "DeleteResourceShare",
                this.Wire("DELETE", $"/deleteresourceshare?resourceShareArn={arn}", $"client.DeleteResourceShareAsync(new DeleteResourceShareRequest {{ ResourceShareArn = \"{arn}\" }})"),
                async () =>
                {
                    DeleteResourceShareResponse response = await client.DeleteResourceShareAsync(new DeleteResourceShareRequest { ResourceShareArn = arn }, ct).ConfigureAwait(false);

                    if (response.ReturnValue != true)
                    {
                        throw new InvalidOperationException($"DeleteResourceShare returned HTTP {(int)response.HttpStatusCode} with ReturnValue {response.ReturnValue}.");
                    }

                    deleted = true;

                    return $"HTTP {(int)response.HttpStatusCode} — ReturnValue true";
                }).ConfigureAwait(false);

            yield return deleteStep;

            if (!deleteStep.Succeeded)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "GetResourceShares — no longer ACTIVE",
                this.Wire("POST", "/getresourceshares", $"client.GetResourceSharesAsync(new GetResourceSharesRequest {{ ResourceOwner = SELF, ResourceShareArns = [\"{arn}\"] }})"),
                async () =>
                {
                    // A deleted share is not removed from the listing: it stays, with status DELETED.
                    // Real RAM passes through DELETING first, which is just as much "deleted" here.
                    GetResourceSharesResponse response = await client.GetResourceSharesAsync(
                        new GetResourceSharesRequest { ResourceOwner = ResourceOwner.SELF, ResourceShareArns = [arn] }, ct).ConfigureAwait(false);
                    ResourceShare? share = (response.ResourceShares ?? []).Find(s => s.ResourceShareArn == arn);

                    if (share is not null && share.Status != ResourceShareStatus.DELETED && share.Status != ResourceShareStatus.DELETING)
                    {
                        throw new InvalidOperationException($"the share is {share.Status?.Value ?? "without a status"} after DeleteResourceShare, not DELETED.");
                    }

                    return share is null ? "The share is no longer listed." : $"{share.Name} — status {share.Status}; a deleted share stays listed.";
                }).ConfigureAwait(false);

            // UnknownResourceException on a second delete is what floci answers; real RAM's answer
            // for a share that is DELETING or DELETED is unverified, so the step is emulator-only.
            if (!factory.UseEmulator)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "DeleteResourceShare — refused once deleted",
                this.Wire("DELETE", $"/deleteresourceshare?resourceShareArn={arn}", $"client.DeleteResourceShareAsync(new DeleteResourceShareRequest {{ ResourceShareArn = \"{arn}\" }})"),
                async () =>
                {
                    try
                    {
                        await client.DeleteResourceShareAsync(new DeleteResourceShareRequest { ResourceShareArn = arn }, ct).ConfigureAwait(false);
                    }
                    // The expected answer, shown as a successful step.
                    catch (UnknownResourceException ex)
                    {
                        return $"{ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException("the share was deleted twice without an error.");
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean account. The step it produces is yielded below
            // — an iterator may not yield from inside a finally.
            if (createAttempted && !deleted)
            {
                cleanup.Add(await this.DeleteSharesByNameAsync(client, suffix, ct).ConfigureAwait(false));
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
    /// the emulator does something real RAM would not.
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

    /// <summary>The wire-level request shown beside an SDK call: RAM is REST-JSON, one path per operation.</summary>
    private string Wire(string method, string path, string call) => $"{method} {factory.ServiceUrl}{path}\n{call}";

    private static async Task<ResourceShare> FindShareAsync(IAmazonRAM client, string arn, CancellationToken ct)
    {
        GetResourceSharesResponse response = await client.GetResourceSharesAsync(new GetResourceSharesRequest { ResourceOwner = ResourceOwner.SELF, ResourceShareArns = [arn] }, ct).ConfigureAwait(false);

        return (response.ResourceShares ?? []).Find(s => s.ResourceShareArn == arn) ?? throw new InvalidOperationException($"GetResourceShares does not list {arn}.");
    }

    /// <summary>Pages <c>ListPrincipals</c>, since a real share's principals can be on any page.</summary>
    private static async Task<List<Principal>> ListPrincipalsAsync(IAmazonRAM client, string arn, CancellationToken ct)
    {
        List<Principal> all = [];
        string? token = null;

        do
        {
            ListPrincipalsResponse page = await client.ListPrincipalsAsync(new ListPrincipalsRequest { ResourceOwner = ResourceOwner.SELF, ResourceShareArns = [arn], NextToken = token }, ct).ConfigureAwait(false);
            all.AddRange(page.Principals ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    /// <summary>Pages <c>ListResources</c>, since a real share's resources can be on any page.</summary>
    private static async Task<List<Resource>> ListResourcesAsync(IAmazonRAM client, string arn, CancellationToken ct)
    {
        List<Resource> all = [];
        string? token = null;

        do
        {
            ListResourcesResponse page = await client.ListResourcesAsync(new ListResourcesRequest { ResourceOwner = ResourceOwner.SELF, ResourceShareArns = [arn], NextToken = token }, ct).ConfigureAwait(false);
            all.AddRange(page.Resources ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    /// <summary>
    /// Cleanup uses <see cref="CancellationToken.None"/> — a run that was cancelled still has a
    /// share to remove. It asks the server which live shares carry this run's name prefix rather
    /// than trusting an ARN the run may never have received; a create that never landed finds
    /// nothing, which is a truthful "nothing to remove". There is no status filter, because a share
    /// the create step refused for being PENDING still has to go; DELETING and DELETED are skipped.
    /// </summary>
    private async Task<DemoStep> DeleteSharesByNameAsync(IAmazonRAM client, string suffix, CancellationToken ct)
    {
        string prefix = $"flocilab-share-{suffix}";
        string request = this.Wire("DELETE", "/deleteresourceshare?resourceShareArn=<arn>", $"client.DeleteResourceShareAsync(new DeleteResourceShareRequest {{ ResourceShareArn = <ARN of \"{prefix}*\"> }})");

        return await RunStepAsync("DeleteResourceShare — cleanup", request, async () =>
        {
            List<ResourceShare> found = [];
            string? token = null;

            do
            {
                GetResourceSharesResponse page = await client.GetResourceSharesAsync(
                    new GetResourceSharesRequest { ResourceOwner = ResourceOwner.SELF, NextToken = token }, CancellationToken.None).ConfigureAwait(false);
                found.AddRange((page.ResourceShares ?? []).Where(s => s.Name is not null && s.Name.StartsWith(prefix, StringComparison.Ordinal)
                    && s.Status != ResourceShareStatus.DELETED && s.Status != ResourceShareStatus.DELETING));
                token = page.NextToken;
            }
            while (!string.IsNullOrEmpty(token));

            if (found.Count == 0)
            {
                return $"No live share named {prefix}* exists — nothing to remove.";
            }

            foreach (ResourceShare share in found)
            {
                DeleteResourceShareResponse response = await client.DeleteResourceShareAsync(new DeleteResourceShareRequest { ResourceShareArn = share.ResourceShareArn }, CancellationToken.None).ConfigureAwait(false);

                if (response.ReturnValue != true)
                {
                    throw new InvalidOperationException($"DeleteResourceShare on {share.Name} returned HTTP {(int)response.HttpStatusCode} with ReturnValue {response.ReturnValue}.");
                }
            }

            return $"Deleted {string.Join(", ", found.Select(s => s.Name))}" + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }
}
