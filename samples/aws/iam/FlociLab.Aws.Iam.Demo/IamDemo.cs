using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.Iam;

/// <summary>
/// AWS IAM against floci — the repo's first Kind C sample (docs/BLAZOR-PLAN.md §4). There is no
/// interactive workload to round-trip, so the "operation" is a scripted provisioning sequence —
/// a user, a role, a policy attachment — and the last step renders the resulting resource tree
/// rather than calling anything. Ordinary AWSSDK.IdentityManagement code otherwise — the only
/// emulator-aware line in the sample is in <see cref="IamClientFactory"/>.
/// </summary>
public sealed class IamDemo(IamClientFactory factory) : IServiceDemo
{
    private const string PolicyArn = "arn:aws:iam::aws:policy/ReadOnlyAccess";

    private const string TrustPolicy = """{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"lambda.amazonaws.com"},"Action":"sts:AssumeRole"}]}""";

    public string Provider => CloudProvider.Aws;

    public string Slug => "iam";

    public string DisplayName => "IAM";

    public string Category => "Security";

    public string Route => "/aws/iam";

    /// <summary>
    /// ListPolicies(Scope=AWS), not ListUsers or ListRoles. A fresh account has no users or roles
    /// until this page creates one, and an empty <c>&lt;Users&gt;&lt;/Users&gt;</c> container from
    /// floci has been observed to throw a bare <see cref="NullReferenceException"/> out of
    /// AWSSDK.IdentityManagement 4.0.103.4's own unmarshalling — reliably from a standalone client,
    /// though not every time under the test host, which points to a client-side race rather than a
    /// clean, deterministic bug (verified against floci 1.7.0, 2026-09-04; see
    /// docs/BLAZOR-PLAN.md §14). It no longer reproduces: on 2026-10-08, against floci 2.2.0,
    /// 4.0.103.4 and 4.0.103.7 each answered twenty standalone ListUsers calls on a fresh account
    /// with a null Users list and no exception. Not worth a probe whose answer has already moved
    /// once — the AWS-managed policy catalog is never empty, so ListPolicies sidesteps the question
    /// entirely rather than depending on the answer.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonIdentityManagementService client = factory.Create();
            ListPoliciesResponse response = await client.ListPoliciesAsync(
                new ListPoliciesRequest { Scope = "AWS" }, ct).ConfigureAwait(false);
            int count = response.Policies?.Count ?? 0;

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListPolicies returned {count} AWS-managed policy(ies).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonIdentityManagementService client = factory.Create();

        // Unique per run, so two runs never collide and a stale name from a failed cleanup never
        // shadows this one.
        string suffix = Guid.NewGuid().ToString("N");
        string userName = $"flocilab-iam-user-{suffix}";
        string roleName = $"flocilab-iam-role-{suffix}";

        bool userCreated = false;
        bool roleCreated = false;
        bool policyAttached = false;
        string? userArn = null;
        string? roleArn = null;

        DemoStep? detachStep;
        DemoStep? deleteRoleStep;
        DemoStep? deleteUserStep;

        try
        {
            yield return await RunStepAsync(
                "CreateUser",
                $"POST {factory.ServiceUrl}/\nAction=CreateUser&UserName={userName}\nclient.CreateUserAsync(new CreateUserRequest {{ UserName = \"{userName}\" }})",
                async () =>
                {
                    // Set before the call, not after: if the request lands but the response does
                    // not come back, the user exists and cleanup has to know about it. Cleanup
                    // treats an absent user as a no-op, so claiming it early is free.
                    userCreated = true;
                    CreateUserResponse response = await client.CreateUserAsync(
                        new CreateUserRequest { UserName = userName }, ct).ConfigureAwait(false);
                    userArn = IamResponse.Require(response.User?.Arn, "CreateUser", "User.Arn");

                    return $"HTTP {(int)response.HttpStatusCode} — Arn: {userArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateRole",
                $"POST {factory.ServiceUrl}/\nAction=CreateRole&RoleName={roleName}\nclient.CreateRoleAsync(new CreateRoleRequest {{ RoleName = \"{roleName}\", AssumeRolePolicyDocument = <trust policy> }})",
                async () =>
                {
                    // Claimed before the call for the same reason as the user above.
                    roleCreated = true;
                    CreateRoleResponse response = await client.CreateRoleAsync(
                        new CreateRoleRequest { RoleName = roleName, AssumeRolePolicyDocument = TrustPolicy }, ct).ConfigureAwait(false);
                    roleArn = IamResponse.Require(response.Role?.Arn, "CreateRole", "Role.Arn");

                    return $"HTTP {(int)response.HttpStatusCode} — Arn: {roleArn}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "AttachRolePolicy",
                $"POST {factory.ServiceUrl}/\nAction=AttachRolePolicy&RoleName={roleName}&PolicyArn={PolicyArn}\nclient.AttachRolePolicyAsync(new AttachRolePolicyRequest {{ RoleName = \"{roleName}\", PolicyArn = \"{PolicyArn}\" }})",
                async () =>
                {
                    // Claimed before the call, and this one matters most: a lost AttachRolePolicy
                    // response would skip DetachRolePolicy, and the DeleteRole that still runs
                    // would then fail with DeleteConflictException, stranding both the role and
                    // its attachment. Detaching a policy that was never attached is a no-op.
                    policyAttached = true;
                    AttachRolePolicyResponse response = await client.AttachRolePolicyAsync(
                        new AttachRolePolicyRequest { RoleName = roleName, PolicyArn = PolicyArn }, ct).ConfigureAwait(false);

                    return $"HTTP {(int)response.HttpStatusCode} — attached {PolicyArn}";
                }).ConfigureAwait(false);

            List<AttachedPolicyType> attached = [];

            yield return await RunStepAsync(
                "ListAttachedRolePolicies",
                $"POST {factory.ServiceUrl}/\nAction=ListAttachedRolePolicies&RoleName={roleName}\nclient.ListAttachedRolePoliciesAsync(new ListAttachedRolePoliciesRequest {{ RoleName = \"{roleName}\" }})",
                async () =>
                {
                    // Safe from the empty-list unmarshalling issue ProbeAsync's doc comment
                    // describes — the role just had a policy attached above, so this list can
                    // never come back empty.
                    ListAttachedRolePoliciesResponse response = await client.ListAttachedRolePoliciesAsync(
                        new ListAttachedRolePoliciesRequest { RoleName = roleName }, ct).ConfigureAwait(false);
                    attached.AddRange(IamResponse.Require(response.AttachedPolicies, "ListAttachedRolePolicies", "AttachedPolicies"));

                    if (attached.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"HTTP {(int)response.HttpStatusCode} — AttachRolePolicy reported success but the role has no attached policies.");
                    }

                    return $"HTTP {(int)response.HttpStatusCode} — {string.Join(", ", attached.Select(p => p.PolicyName))}";
                }).ConfigureAwait(false);

            // The tree is the whole point of a Kind C sample, and it is the one step with no
            // wire call to fail on its own — so it has to check the steps above actually
            // happened. Rendering a green tree of ARNs after a failed CreateUser would describe
            // an account state that does not exist (plan §14, "a step that did not achieve what
            // it claims still renders green").
            yield return userArn is null || roleArn is null || attached.Count == 0
                ? DemoStep.Failed(
                    "Resource tree",
                    new InvalidOperationException("Nothing to render — an earlier step failed, so the user, the role or the attachment never existed."))
                : new DemoStep(
                    "Resource tree",
                    Request: null,
                    Response: RenderResourceTree(userName, userArn, roleName, roleArn, attached));
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // Detach before delete: real IAM (and floci, verified 2026-09-04) refuses DeleteRole
            // with DeleteConflictException while a policy is still attached. The steps are yielded
            // below — an iterator may not yield from inside a finally.
            detachStep = policyAttached
                ? await this.DetachRolePolicyAsync(client, roleName, ct).ConfigureAwait(false)
                : null;
            deleteRoleStep = roleCreated
                ? await this.DeleteRoleAsync(client, roleName, ct).ConfigureAwait(false)
                : null;
            deleteUserStep = userCreated
                ? await this.DeleteUserAsync(client, userName, ct).ConfigureAwait(false)
                : null;
        }

        if (detachStep is not null)
        {
            yield return detachStep;
        }

        if (deleteRoleStep is not null)
        {
            yield return deleteRoleStep;
        }

        if (deleteUserStep is not null)
        {
            yield return deleteUserStep;
        }
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> handles only the transport-level cases; the AWS SDK
    /// reports both a 501 and floci's own error responses inside an
    /// <see cref="AmazonServiceException"/>, so they need unwrapping here. Same shape as
    /// <c>KmsDemo.Classify</c>.
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

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real IAM would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still detaches and deletes
        // whatever was created. Catching it here would instead fabricate a "Failed" step for every
        // remaining operation, reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>
    /// Renders the resource tree Kind C promises (docs/BLAZOR-PLAN.md §4) — not a wire call, so
    /// <see cref="DemoStep.Request"/> is null; the response is composed from what the steps above
    /// already fetched.
    /// </summary>
    private static string RenderResourceTree(string userName, string userArn, string roleName, string roleArn, IReadOnlyList<AttachedPolicyType> attached)
    {
        StringBuilder tree = new();
        tree.AppendLine($"User: {userName}");
        tree.AppendLine($"  {userArn}");
        tree.AppendLine($"Role: {roleName}");
        tree.AppendLine($"  {roleArn}");

        for (int i = 0; i < attached.Count; i++)
        {
            string branch = i == attached.Count - 1 ? "└──" : "├──";
            tree.AppendLine($"  {branch} AttachedPolicy: {attached[i].PolicyName} ({attached[i].PolicyArn})");
        }

        return tree.ToString().TrimEnd();
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    private async Task<DemoStep> DetachRolePolicyAsync(IAmazonIdentityManagementService client, string roleName, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nAction=DetachRolePolicy&RoleName={roleName}&PolicyArn={PolicyArn}\nclient.DetachRolePolicyAsync(new DetachRolePolicyRequest {{ RoleName = \"{roleName}\", PolicyArn = \"{PolicyArn}\" }})";

        return await RunStepAsync("DetachRolePolicy — cleanup", request, async () =>
        {
            DetachRolePolicyResponse response = await client.DetachRolePolicyAsync(
                new DetachRolePolicyRequest { RoleName = roleName, PolicyArn = PolicyArn }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — detached {PolicyArn}"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }

    private async Task<DemoStep> DeleteRoleAsync(IAmazonIdentityManagementService client, string roleName, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nAction=DeleteRole&RoleName={roleName}\nclient.DeleteRoleAsync(new DeleteRoleRequest {{ RoleName = \"{roleName}\" }})";

        return await RunStepAsync("DeleteRole — cleanup", request, async () =>
        {
            DeleteRoleResponse response = await client.DeleteRoleAsync(
                new DeleteRoleRequest { RoleName = roleName }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — deleted"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }

    private async Task<DemoStep> DeleteUserAsync(IAmazonIdentityManagementService client, string userName, CancellationToken ct)
    {
        string request = $"POST {factory.ServiceUrl}/\nAction=DeleteUser&UserName={userName}\nclient.DeleteUserAsync(new DeleteUserRequest {{ UserName = \"{userName}\" }})";

        return await RunStepAsync("DeleteUser — cleanup", request, async () =>
        {
            DeleteUserResponse response = await client.DeleteUserAsync(
                new DeleteUserRequest { UserName = userName }, CancellationToken.None).ConfigureAwait(false);

            return $"HTTP {(int)response.HttpStatusCode} — deleted"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }
}
