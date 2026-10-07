using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.VerifiedPermissions;
using Amazon.VerifiedPermissions.Model;
using FlociLab.Core;

namespace FlociLab.Aws.VerifiedPermissions;

/// <summary>
/// Creates a policy store, adds a Cedar permit and a forbid, asks IsAuthorized the same question
/// several ways, shows a malformed policy refused and deletes the store. Ordinary
/// AWSSDK.VerifiedPermissions code — the only emulator-aware line is in
/// <see cref="VerifiedPermissionsClientFactory"/>. floci evaluates real Cedar, so the decisions
/// below are the engine's own, not canned answers.
/// </summary>
public sealed class VerifiedPermissionsDemo(VerifiedPermissionsClientFactory factory) : IServiceDemo
{
    public string Provider => CloudProvider.Aws;

    public string Slug => "verifiedpermissions";

    public string DisplayName => "Verified Permissions";

    public string Category => "Identity and access";

    public string Route => "/aws/verifiedpermissions";

    /// <summary>ListPolicyStores — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonVerifiedPermissions client = factory.Create();
            ListPolicyStoresResponse response = await client.ListPolicyStoresAsync(new ListPolicyStoresRequest { MaxResults = 1 }, ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListPolicyStores: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonVerifiedPermissions client = factory.Create();

        string suffix = Guid.NewGuid().ToString("N")[..12];
        string user = $"alice-{suffix}";
        string stranger = $"bob-{suffix}";
        string description = $"flocilab-{suffix}";
        string storeId = string.Empty;

        // Claimed before the call: a request that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created a store, so the finally looks it up by its
        // unique description rather than trusting the id from a response that may never arrive.
        bool createAttempted = false;
        string? permitId = null;
        string? forbidId = null;

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep createStore = await RunStepAsync(
                "CreatePolicyStore",
                this.Call("CreatePolicyStore", "client.CreatePolicyStoreAsync(new CreatePolicyStoreRequest { ValidationSettings = { Mode = OFF } })"),
                async () =>
                {
                    createAttempted = true;
                    CreatePolicyStoreResponse response = await client.CreatePolicyStoreAsync(
                        new CreatePolicyStoreRequest
                        {
                            // OFF: no schema is registered, so STRICT validation would refuse every policy.
                            ValidationSettings = new ValidationSettings { Mode = ValidationMode.OFF },
                            Description = description,
                        }, ct).ConfigureAwait(false);

                    storeId = response.PolicyStoreId;

                    return $"{response.PolicyStoreId}\n{response.Arn}";
                }).ConfigureAwait(false);

            yield return createStore;

            if (!createStore.Succeeded)
            {
                yield break;
            }

            string permit = $"permit(principal == User::\"{user}\", action == Action::\"view\", resource);";
            DemoStep permitStep = await RunStepAsync(
                "CreatePolicy — permit",
                this.Call("CreatePolicy", $"client.CreatePolicyAsync(... Statement = {permit})"),
                async () =>
                {
                    CreatePolicyResponse response = await client.CreatePolicyAsync(StaticPolicy(storeId, permit), ct).ConfigureAwait(false);
                    permitId = response.PolicyId;

                    return $"{response.PolicyId} — {response.PolicyType}, {response.Effect}";
                }).ConfigureAwait(false);

            yield return permitStep;

            if (!permitStep.Succeeded)
            {
                yield break;
            }

            yield return await RunStepAsync(
                "IsAuthorized — the permitted principal",
                this.Call("IsAuthorized", $"client.IsAuthorizedAsync(principal = User::{user}, action = Action::view, resource = Photo::holiday)"),
                async () => await DecideAsync(client, storeId, user, "ALLOW", permitId, ct).ConfigureAwait(false)).ConfigureAwait(false);

            yield return await RunStepAsync(
                "IsAuthorized — a principal no policy mentions",
                this.Call("IsAuthorized", $"client.IsAuthorizedAsync(principal = User::{stranger}, action = Action::view, resource = Photo::holiday)"),
                async () => await DecideAsync(client, storeId, stranger, "DENY", null, ct).ConfigureAwait(false)).ConfigureAwait(false);

            string forbid = $"forbid(principal == User::\"{user}\", action, resource);";
            DemoStep forbidStep = await RunStepAsync(
                "CreatePolicy — forbid",
                this.Call("CreatePolicy", $"client.CreatePolicyAsync(... Statement = {forbid})"),
                async () =>
                {
                    CreatePolicyResponse response = await client.CreatePolicyAsync(StaticPolicy(storeId, forbid), ct).ConfigureAwait(false);
                    forbidId = response.PolicyId;

                    return $"{response.PolicyId} — {response.PolicyType}, {response.Effect}";
                }).ConfigureAwait(false);

            yield return forbidStep;

            if (forbidStep.Succeeded)
            {
                // Cedar: an applicable forbid beats any permit, so the same question now has the opposite answer.
                yield return await RunStepAsync(
                    "IsAuthorized — the forbid wins",
                    this.Call("IsAuthorized", $"client.IsAuthorizedAsync(principal = User::{user}, action = Action::view, resource = Photo::holiday)"),
                    async () => await DecideAsync(client, storeId, user, "DENY", forbidId, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            yield return await RunStepAsync(
                "ListPolicies",
                this.Call("ListPolicies", "client.ListPoliciesAsync(new ListPoliciesRequest { PolicyStoreId = ... })"),
                async () =>
                {
                    ListPoliciesResponse response = await client.ListPoliciesAsync(new ListPoliciesRequest { PolicyStoreId = storeId }, ct).ConfigureAwait(false);
                    string?[] created = [permitId, forbidId];
                    string[] known = [.. created.OfType<string>()];
                    string[] missing = [.. known.Where(id => !response.Policies.Exists(p => p.PolicyId == id))];

                    if (missing.Length != 0)
                    {
                        throw new InvalidOperationException($"policies {string.Join(", ", missing)} were created but are not listed.");
                    }

                    // Only exact when both creates answered: a forbid whose response was lost may
                    // still have been stored, and that is the earlier step's failure, not this one's.
                    if (forbidStep.Succeeded && response.Policies.Count != known.Length)
                    {
                        throw new InvalidOperationException($"expected {known.Length} policies in the store, found {response.Policies.Count}.");
                    }

                    return string.Join("\n", response.Policies.Select(p => $"{p.PolicyId} — {p.Effect}"));
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreatePolicy — refused when it is not Cedar",
                this.Call("CreatePolicy", "client.CreatePolicyAsync(... Statement = \"not a policy\")"),
                async () =>
                {
                    try
                    {
                        await client.CreatePolicyAsync(StaticPolicy(storeId, "not a policy"), ct).ConfigureAwait(false);
                    }
                    // The expected answer, shown as a successful step.
                    catch (ValidationException ex)
                    {
                        return $"{ex.GetType().Name}: {ex.Message}";
                    }

                    throw new InvalidOperationException("a statement that is not Cedar was accepted as a policy.");
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // An iterator may not yield from a finally, so the step is yielded below.
            if (createAttempted)
            {
                cleanup.Add(await this.DeleteStoresAsync(client, description, ct).ConfigureAwait(false));
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
    /// inspects only the outermost exception — cannot classify them on its own.
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
    /// failure becomes a step carrying the error text.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still deletes the store.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static CreatePolicyRequest StaticPolicy(string storeId, string statement) => new()
    {
        PolicyStoreId = storeId,
        Definition = new PolicyDefinition { Static = new StaticPolicyDefinition { Statement = statement } },
    };

    /// <summary>Asks whether <paramref name="who"/> may view the photo, and fails the step if the engine disagrees with <paramref name="expected"/>.</summary>
    private static async Task<string> DecideAsync(IAmazonVerifiedPermissions client, string storeId, string who, string expected, string? determiningId, CancellationToken ct)
    {
        IsAuthorizedResponse response = await client.IsAuthorizedAsync(
            new IsAuthorizedRequest
            {
                PolicyStoreId = storeId,
                Principal = new EntityIdentifier { EntityType = "User", EntityId = who },
                Action = new ActionIdentifier { ActionType = "Action", ActionId = "view" },
                Resource = new EntityIdentifier { EntityType = "Photo", EntityId = "holiday" },
            }, ct).ConfigureAwait(false);

        string decision = response.Decision.Value;
        string[] determining = [.. response.DeterminingPolicies.Select(p => p.PolicyId)];

        if (decision != expected)
        {
            throw new InvalidOperationException($"expected {expected}, got {decision} (determining: {string.Join(", ", determining)}).");
        }

        // No applicable policy at all is a DENY with nothing determining it — Cedar is default-deny.
        bool matches = determiningId is null ? determining.Length == 0 : determining is [string only] && only == determiningId;

        if (!matches)
        {
            throw new InvalidOperationException($"{decision}, but determined by [{string.Join(", ", determining)}] rather than [{determiningId}].");
        }

        return determining.Length == 0 ? $"{decision} — no policy applies, so the default is deny" : $"{decision} — determined by {determining[0]}";
    }

    /// <summary>The wire-level request shown beside an SDK call: AWS JSON 1.0, one POST / per operation, the operation in the target header.</summary>
    private string Call(string operation, string call) => $"POST {factory.ServiceUrl}/\nX-Amz-Target: VerifiedPermissions.{operation}\n{call}";

    /// <summary>
    /// Finds this run's stores by their unique description, so a store whose create response was
    /// lost is still found, and a create the server refused finds nothing — a truthful "nothing to
    /// remove". Uses <see cref="CancellationToken.None"/>: a cancelled run still has a store to
    /// delete. Deleting a store takes its policies with it, so they need no deletes of their own.
    /// </summary>
    private async Task<DemoStep> DeleteStoresAsync(IAmazonVerifiedPermissions client, string description, CancellationToken ct)
    {
        string request = this.Call("DeletePolicyStore", $"client.DeletePolicyStoreAsync(new DeletePolicyStoreRequest {{ PolicyStoreId = <id of \"{description}\"> }})");

        return await RunStepAsync("DeletePolicyStore — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            List<string> found = [];
            string? token = null;

            do
            {
                ListPolicyStoresResponse page = await client.ListPolicyStoresAsync(new ListPolicyStoresRequest { NextToken = token }, CancellationToken.None).ConfigureAwait(false);
                found.AddRange((page.PolicyStores ?? []).Where(s => s.Description == description).Select(s => s.PolicyStoreId));
                token = page.NextToken;
            }
            while (!string.IsNullOrEmpty(token));

            if (found.Count == 0)
            {
                return $"No policy store described {description} exists — nothing to remove.{cancelled}";
            }

            foreach (string storeId in found)
            {
                await client.DeletePolicyStoreAsync(new DeletePolicyStoreRequest { PolicyStoreId = storeId }, CancellationToken.None).ConfigureAwait(false);

                // The store must be gone, not merely the call accepted.
                try
                {
                    await client.GetPolicyStoreAsync(new GetPolicyStoreRequest { PolicyStoreId = storeId }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ResourceNotFoundException)
                {
                    // The answer this check is waiting for: the store is gone.
                    continue;
                }

                throw new InvalidOperationException($"DeletePolicyStore returned, but {storeId} can still be read.{cancelled}");
            }

            return $"Deleted {string.Join(", ", found)}; GetPolicyStore now answers ResourceNotFoundException.{cancelled}";
        }).ConfigureAwait(false);
    }
}
