using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.AutoScaling;
using Amazon.AutoScaling.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.AutoScaling;

/// <summary>
/// Creates a launch configuration and an auto scaling group with no instances in it, attaches a
/// scaling policy, a target-tracking policy, a lifecycle hook and a scheduled action, suspends and
/// resumes a process, reads the group back as the resource tree it now is, then asks for a
/// duplicate name, a size range that cannot hold, and a launch configuration that does not exist,
/// and deletes everything. Ordinary AWSSDK.AutoScaling code — the only emulator-aware line is in
/// <see cref="AutoScalingClientFactory"/>. Capacity is kept at zero deliberately: floci runs a real
/// reconciler that starts an EC2 container for every instance a group wants, and EC2 is a Phase 4
/// service. What the demo shows is the group's configuration, which is all of Auto Scaling that
/// floci does without Docker. Every step reports what the engine actually answered rather than
/// what the docs promise.
/// <para>
/// Launch configurations are what floci documents and what this uses, but real AWS stopped
/// letting accounts created after 2024-10-01 create them: a new account must use a launch
/// template, and that is an EC2 call, which would be a second SDK package (constraint 1).
/// </para>
/// </summary>
public sealed class AutoScalingDemo(AutoScalingClientFactory factory) : IServiceDemo
{
    private const string ImageId = "ami-12345678";
    private const string InstanceType = "t3.micro";

    public string Provider => CloudProvider.Aws;

    public string Slug => "autoscaling";

    public string DisplayName => "Auto Scaling";

    public string Category => "Containers and compute";

    public string Route => "/aws/autoscaling";

    /// <summary>DescribeAutoScalingGroups — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonAutoScaling client = factory.Create();
            DescribeAutoScalingGroupsResponse response = await client.DescribeAutoScalingGroupsAsync(new DescribeAutoScalingGroupsRequest(), ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"DescribeAutoScalingGroups: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonAutoScaling client = factory.Create();

        string suffix = Guid.NewGuid().ToString("N")[..12];
        // Assumed, not looked up: listing zones is an EC2 call (constraint 1). floci accepts any
        // name; on real AWS a region whose "a" zone this account cannot use refuses the create.
        string zone = $"{factory.Region}a";
        string launchConfig = $"flocilab-{suffix}-lc";
        string group = $"flocilab-{suffix}";
        string oddRange = $"flocilab-{suffix}-range";
        string oddLaunch = $"flocilab-{suffix}-nolc";
        string stepPolicy = "scale-out";
        string trackingPolicy = "cpu-50";
        string hook = "wait-for-launch";
        string schedule = "nightly-shrink";

        // Claimed before each create: a call that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created the resource, so the finally looks each name
        // up rather than trusting that a response arrived.
        Claimed claimed = new();

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "DescribeAccountLimits, DescribeAdjustmentTypes — the catalog",
                this.Call("DescribeAccountLimits", "client.DescribeAccountLimitsAsync(new DescribeAccountLimitsRequest())\nclient.DescribeAdjustmentTypesAsync(new DescribeAdjustmentTypesRequest())"),
                async () =>
                {
                    DescribeAccountLimitsResponse limits = await client.DescribeAccountLimitsAsync(new DescribeAccountLimitsRequest(), ct).ConfigureAwait(false);
                    DescribeAdjustmentTypesResponse types = await client.DescribeAdjustmentTypesAsync(new DescribeAdjustmentTypesRequest(), ct).ConfigureAwait(false);

                    return $"max groups {limits.MaxNumberOfAutoScalingGroups}, max launch configurations {limits.MaxNumberOfLaunchConfigurations}\nin use: {limits.NumberOfAutoScalingGroups} group(s), {limits.NumberOfLaunchConfigurations} launch configuration(s)\nadjustment types: {string.Join(", ", (types.AdjustmentTypes ?? []).Select(t => t.Type))}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateLaunchConfiguration — what each instance would be",
                this.Call("CreateLaunchConfiguration", $"client.CreateLaunchConfigurationAsync(new CreateLaunchConfigurationRequest {{ LaunchConfigurationName = \"{launchConfig}\", ImageId = \"{ImageId}\", InstanceType = \"{InstanceType}\" }})"),
                async () =>
                {
                    claimed.LaunchConfig = true;
                    await client.CreateLaunchConfigurationAsync(new CreateLaunchConfigurationRequest { LaunchConfigurationName = launchConfig, ImageId = ImageId, InstanceType = InstanceType }, ct).ConfigureAwait(false);

                    LaunchConfiguration found = ((await client.DescribeLaunchConfigurationsAsync(new DescribeLaunchConfigurationsRequest { LaunchConfigurationNames = [launchConfig] }, ct).ConfigureAwait(false)).LaunchConfigurations ?? []).SingleOrDefault()
                        ?? throw new InvalidOperationException($"{launchConfig} was accepted but DescribeLaunchConfigurations does not list it.");

                    if (found.ImageId != ImageId || found.InstanceType != InstanceType)
                    {
                        throw new InvalidOperationException($"CreateLaunchConfiguration was accepted but it reads back as {found.ImageId}, {found.InstanceType}.");
                    }

                    return $"{found.LaunchConfigurationName}: {found.ImageId}, {found.InstanceType}\n{found.LaunchConfigurationARN}";
                }).ConfigureAwait(false);

            DemoStep create = await RunStepAsync(
                "CreateAutoScalingGroup — a group of zero",
                this.Call("CreateAutoScalingGroup", $"client.CreateAutoScalingGroupAsync(new CreateAutoScalingGroupRequest {{ AutoScalingGroupName = \"{group}\", LaunchConfigurationName = \"{launchConfig}\", MinSize = 0, MaxSize = 2, DesiredCapacity = 0, AvailabilityZones = [\"{zone}\"], Tags = [run = {suffix}] }})"),
                async () =>
                {
                    claimed.Group = true;
                    await client.CreateAutoScalingGroupAsync(
                        new CreateAutoScalingGroupRequest
                        {
                            AutoScalingGroupName = group,
                            LaunchConfigurationName = launchConfig,
                            MinSize = 0,
                            MaxSize = 2,
                            DesiredCapacity = 0,
                            AvailabilityZones = [zone],
                            Tags = [new Tag { Key = "run", Value = suffix, PropagateAtLaunch = true }],
                        }, ct).ConfigureAwait(false);

                    AutoScalingGroup found = await DescribeGroupAsync(client, group, ct).ConfigureAwait(false);

                    return $"{found.AutoScalingGroupName}: {found.MinSize}..{found.MaxSize}, desired {found.DesiredCapacity}, {found.Instances?.Count ?? 0} instance(s)\n{found.AutoScalingGroupARN}\nzero on purpose: floci starts a real EC2 container for each instance a group wants";
                }).ConfigureAwait(false);

            yield return create;

            // A failed create means every later step would look for a group that is not there.
            // Skipped rather than `yield break`, which would also skip yielding the cleanup step below.
            if (create.Succeeded)
            {
                yield return await RunStepAsync(
                    "PutScalingPolicy ×2, DescribePolicies — a step and a target-tracking policy",
                    this.Call("PutScalingPolicy", $"client.PutScalingPolicyAsync(new PutScalingPolicyRequest {{ AutoScalingGroupName = \"{group}\", PolicyName = \"{stepPolicy}\", PolicyType = \"SimpleScaling\", AdjustmentType = \"ChangeInCapacity\", ScalingAdjustment = 1, Cooldown = 60 }})\nclient.PutScalingPolicyAsync(new PutScalingPolicyRequest {{ AutoScalingGroupName = \"{group}\", PolicyName = \"{trackingPolicy}\", PolicyType = \"TargetTrackingScaling\", TargetTrackingConfiguration = {{ ASGAverageCPUUtilization, TargetValue = 50 }} }})"),
                    async () =>
                    {
                        await client.PutScalingPolicyAsync(
                            new PutScalingPolicyRequest
                            {
                                AutoScalingGroupName = group,
                                PolicyName = stepPolicy,
                                PolicyType = "SimpleScaling",
                                AdjustmentType = "ChangeInCapacity",
                                ScalingAdjustment = 1,
                                Cooldown = 60,
                            }, ct).ConfigureAwait(false);

                        await client.PutScalingPolicyAsync(
                            new PutScalingPolicyRequest
                            {
                                AutoScalingGroupName = group,
                                PolicyName = trackingPolicy,
                                PolicyType = "TargetTrackingScaling",
                                TargetTrackingConfiguration = new TargetTrackingConfiguration
                                {
                                    PredefinedMetricSpecification = new PredefinedMetricSpecification { PredefinedMetricType = MetricType.ASGAverageCPUUtilization },
                                    TargetValue = 50,
                                },
                            }, ct).ConfigureAwait(false);

                        List<ScalingPolicy> policies = await ListPoliciesAsync(client, group, ct).ConfigureAwait(false);

                        if (policies.Count != 2)
                        {
                            throw new InvalidOperationException($"two policies were accepted but DescribePolicies lists {policies.Count}.");
                        }

                        return string.Join("\n", policies.Select(DescribePolicy));
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "PutLifecycleHook, DescribeLifecycleHooks",
                    this.Call("PutLifecycleHook", $"client.PutLifecycleHookAsync(new PutLifecycleHookRequest {{ AutoScalingGroupName = \"{group}\", LifecycleHookName = \"{hook}\", LifecycleTransition = \"autoscaling:EC2_INSTANCE_LAUNCHING\", DefaultResult = \"CONTINUE\", HeartbeatTimeout = 300 }})"),
                    async () =>
                    {
                        await client.PutLifecycleHookAsync(
                            new PutLifecycleHookRequest
                            {
                                AutoScalingGroupName = group,
                                LifecycleHookName = hook,
                                LifecycleTransition = "autoscaling:EC2_INSTANCE_LAUNCHING",
                                DefaultResult = "CONTINUE",
                                HeartbeatTimeout = 300,
                            }, ct).ConfigureAwait(false);

                        List<LifecycleHook> hooks = (await client.DescribeLifecycleHooksAsync(new DescribeLifecycleHooksRequest { AutoScalingGroupName = group }, ct).ConfigureAwait(false)).LifecycleHooks ?? [];

                        if (!hooks.Exists(h => h.LifecycleHookName == hook))
                        {
                            throw new InvalidOperationException($"{hook} was accepted but DescribeLifecycleHooks does not list it.");
                        }

                        return string.Join("\n", hooks.Select(DescribeHook));
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "PutScheduledUpdateGroupAction, DescribeScheduledActions",
                    this.Call("PutScheduledUpdateGroupAction", $"client.PutScheduledUpdateGroupActionAsync(new PutScheduledUpdateGroupActionRequest {{ AutoScalingGroupName = \"{group}\", ScheduledActionName = \"{schedule}\", Recurrence = \"0 0 * * *\", MinSize = 0, MaxSize = 1 }})"),
                    async () =>
                    {
                        await client.PutScheduledUpdateGroupActionAsync(
                            new PutScheduledUpdateGroupActionRequest
                            {
                                AutoScalingGroupName = group,
                                ScheduledActionName = schedule,
                                Recurrence = "0 0 * * *",
                                MinSize = 0,
                                MaxSize = 1,
                            }, ct).ConfigureAwait(false);

                        List<ScheduledUpdateGroupAction> actions = (await client.DescribeScheduledActionsAsync(new DescribeScheduledActionsRequest { AutoScalingGroupName = group }, ct).ConfigureAwait(false)).ScheduledUpdateGroupActions ?? [];

                        if (!actions.Exists(a => a.ScheduledActionName == schedule))
                        {
                            throw new InvalidOperationException($"{schedule} was accepted but DescribeScheduledActions does not list it.");
                        }

                        return string.Join("\n", actions.Select(a => $"{a.ScheduledActionName}: cron \"{a.Recurrence}\", {a.MinSize}..{a.MaxSize}"));
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "UpdateAutoScalingGroup, SuspendProcesses, ResumeProcesses",
                    this.Call("UpdateAutoScalingGroup", $"client.UpdateAutoScalingGroupAsync(new UpdateAutoScalingGroupRequest {{ AutoScalingGroupName = \"{group}\", MaxSize = 4, DefaultCooldown = 120 }})\nclient.SuspendProcessesAsync(new SuspendProcessesRequest {{ AutoScalingGroupName = \"{group}\", ScalingProcesses = [\"AZRebalance\"] }})\nclient.ResumeProcessesAsync(new ResumeProcessesRequest {{ AutoScalingGroupName = \"{group}\", ScalingProcesses = [\"AZRebalance\"] }})"),
                    async () =>
                    {
                        await client.UpdateAutoScalingGroupAsync(new UpdateAutoScalingGroupRequest { AutoScalingGroupName = group, MaxSize = 4, DefaultCooldown = 120 }, ct).ConfigureAwait(false);
                        await client.SuspendProcessesAsync(new SuspendProcessesRequest { AutoScalingGroupName = group, ScalingProcesses = ["AZRebalance"] }, ct).ConfigureAwait(false);

                        AutoScalingGroup suspended = await DescribeGroupAsync(client, group, ct).ConfigureAwait(false);

                        if (suspended.MaxSize != 4 || suspended.DefaultCooldown != 120)
                        {
                            throw new InvalidOperationException($"UpdateAutoScalingGroup was accepted but the group reads back as max {suspended.MaxSize}, cooldown {suspended.DefaultCooldown}.");
                        }

                        if (!(suspended.SuspendedProcesses ?? []).Exists(p => p.ProcessName == "AZRebalance"))
                        {
                            throw new InvalidOperationException("SuspendProcesses was accepted but the group does not list AZRebalance as suspended.");
                        }

                        await client.ResumeProcessesAsync(new ResumeProcessesRequest { AutoScalingGroupName = group, ScalingProcesses = ["AZRebalance"] }, ct).ConfigureAwait(false);
                        AutoScalingGroup resumed = await DescribeGroupAsync(client, group, ct).ConfigureAwait(false);

                        if ((resumed.SuspendedProcesses ?? []).Exists(p => p.ProcessName == "AZRebalance"))
                        {
                            throw new InvalidOperationException("ResumeProcesses was accepted but the group still lists AZRebalance as suspended.");
                        }

                        return $"after UpdateAutoScalingGroup: max {suspended.MaxSize}, cooldown {suspended.DefaultCooldown}s\nafter SuspendProcesses: {string.Join(", ", suspended.SuspendedProcesses!.Select(p => p.ProcessName))}\nafter ResumeProcesses: {resumed.SuspendedProcesses?.Count ?? 0} suspended";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "DescribeAutoScalingGroups — the resource tree",
                    this.Call("DescribeAutoScalingGroups", $"client.DescribeAutoScalingGroupsAsync(new DescribeAutoScalingGroupsRequest {{ AutoScalingGroupNames = [\"{group}\"] }})\nclient.DescribePoliciesAsync, DescribeLifecycleHooksAsync, DescribeScheduledActionsAsync — for the same group"),
                    async () =>
                    {
                        AutoScalingGroup found = await DescribeGroupAsync(client, group, ct).ConfigureAwait(false);
                        List<ScalingPolicy> policies = await ListPoliciesAsync(client, group, ct).ConfigureAwait(false);
                        List<LifecycleHook> hooks = (await client.DescribeLifecycleHooksAsync(new DescribeLifecycleHooksRequest { AutoScalingGroupName = group }, ct).ConfigureAwait(false)).LifecycleHooks ?? [];
                        List<ScheduledUpdateGroupAction> actions = (await client.DescribeScheduledActionsAsync(new DescribeScheduledActionsRequest { AutoScalingGroupName = group }, ct).ConfigureAwait(false)).ScheduledUpdateGroupActions ?? [];

                        if (found.LaunchConfigurationName != launchConfig || policies.Count != 2 || hooks.Count != 1 || actions.Count != 1)
                        {
                            throw new InvalidOperationException($"the tree is not what the earlier steps built: launch configuration {found.LaunchConfigurationName}, {policies.Count} policies, {hooks.Count} hooks, {actions.Count} scheduled actions.");
                        }

                        return RenderTree(found, policies, hooks, actions);
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateAutoScalingGroup — a name that already exists",
                    this.Call("CreateAutoScalingGroup", $"client.CreateAutoScalingGroupAsync(new CreateAutoScalingGroupRequest {{ AutoScalingGroupName = \"{group}\", ... }})"),
                    async () =>
                    {
                        try
                        {
                            await client.CreateAutoScalingGroupAsync(
                                new CreateAutoScalingGroupRequest { AutoScalingGroupName = group, LaunchConfigurationName = launchConfig, MinSize = 0, MaxSize = 2, DesiredCapacity = 0, AvailabilityZones = [zone] }, ct).ConfigureAwait(false);
                        }
                        // What real Auto Scaling answers: the expected outcome, shown as a successful step.
                        catch (AlreadyExistsException ex)
                        {
                            return $"{ex.GetType().Name}: {ex.Message}";
                        }

                        throw new InvalidOperationException("a second group with an existing name was accepted.");
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateAutoScalingGroup — MinSize above MaxSize",
                    this.Call("CreateAutoScalingGroup", $"client.CreateAutoScalingGroupAsync(new CreateAutoScalingGroupRequest {{ AutoScalingGroupName = \"{oddRange}\", MinSize = 1, MaxSize = 0, DesiredCapacity = 0, ... }})"),
                    async () =>
                    {
                        try
                        {
                            // 1..0 with a desired capacity of zero, not 3..2: floci accepts either,
                            // and for 3..2 it sets the desired capacity to 3 and its reconciler starts
                            // three real EC2 containers within ten seconds (probed 2026-10-07).
                            claimed.OddRange = true;
                            await client.CreateAutoScalingGroupAsync(
                                new CreateAutoScalingGroupRequest { AutoScalingGroupName = oddRange, LaunchConfigurationName = launchConfig, MinSize = 1, MaxSize = 0, DesiredCapacity = 0, AvailabilityZones = [zone] }, ct).ConfigureAwait(false);

                            return "accepted\nreal Auto Scaling refuses a minimum above the maximum with ValidationError; floci does not check";
                        }
                        // What real Auto Scaling answers: the expected outcome, shown as a successful step.
                        catch (AmazonAutoScalingException ex) when (ex.ErrorCode == "ValidationError")
                        {
                            return $"{ex.ErrorCode}: {ex.Message}";
                        }
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateAutoScalingGroup — a launch configuration that does not exist",
                    this.Call("CreateAutoScalingGroup", $"client.CreateAutoScalingGroupAsync(new CreateAutoScalingGroupRequest {{ AutoScalingGroupName = \"{oddLaunch}\", LaunchConfigurationName = \"no-such-launch-configuration\", ... }})"),
                    async () =>
                    {
                        try
                        {
                            claimed.OddLaunch = true;
                            await client.CreateAutoScalingGroupAsync(
                                new CreateAutoScalingGroupRequest { AutoScalingGroupName = oddLaunch, LaunchConfigurationName = "no-such-launch-configuration", MinSize = 0, MaxSize = 2, AvailabilityZones = [zone] }, ct).ConfigureAwait(false);

                            return "accepted\nreal Auto Scaling refuses a launch configuration it cannot find with ValidationError; floci does not check";
                        }
                        // What real Auto Scaling answers: the expected outcome, shown as a successful step.
                        catch (AmazonAutoScalingException ex) when (ex.ErrorCode == "ValidationError")
                        {
                            return $"{ex.ErrorCode}: {ex.Message}";
                        }
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "DeleteLaunchConfiguration — while a group still uses it",
                    this.Call("DeleteLaunchConfiguration", $"client.DeleteLaunchConfigurationAsync(new DeleteLaunchConfigurationRequest {{ LaunchConfigurationName = \"{launchConfig}\" }})"),
                    async () =>
                    {
                        try
                        {
                            await client.DeleteLaunchConfigurationAsync(new DeleteLaunchConfigurationRequest { LaunchConfigurationName = launchConfig }, ct).ConfigureAwait(false);

                            return "accepted\nreal Auto Scaling refuses with ResourceInUse while a group uses the launch configuration; floci deletes it and the group is left pointing at nothing";
                        }
                        // What real Auto Scaling answers: the expected outcome, shown as a successful step.
                        catch (ResourceInUseException ex)
                        {
                            return $"{ex.GetType().Name}: {ex.Message}";
                        }
                    }).ConfigureAwait(false);
            }
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // An iterator may not yield from a finally, so the step is yielded below.
            if (claimed.Any)
            {
                cleanup.Add(await this.DeleteEverythingAsync(client, claimed, new Names(launchConfig, group, oddRange, oddLaunch), ct).ConfigureAwait(false));
            }
        }

        foreach (DemoStep step in cleanup)
        {
            yield return step;
        }
    }

    /// <summary>
    /// The AWS SDK reports a not-implemented operation inside an <see cref="AmazonServiceException"/>,
    /// so <see cref="ProbeResult.FromException"/> — which inspects only the outermost exception —
    /// cannot classify it on its own. floci answers an operation it recognises but has not built
    /// with HTTP 400 and the error code <c>UnsupportedOperation</c>, which is the not-implemented
    /// outcome here.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                case AmazonServiceException { ErrorCode: "UnsupportedOperation" }:
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

    private static string DescribePolicy(ScalingPolicy policy)
        => policy.PolicyType == "TargetTrackingScaling"
            ? $"{policy.PolicyName}: {policy.PolicyType}, {policy.TargetTrackingConfiguration?.PredefinedMetricSpecification?.PredefinedMetricType?.Value} at {policy.TargetTrackingConfiguration?.TargetValue}"
            : $"{policy.PolicyName}: {policy.PolicyType}, {policy.AdjustmentType} {policy.ScalingAdjustment:+#;-#;0}, cooldown {policy.Cooldown}s";

    private static string DescribeHook(LifecycleHook hook)
        => $"{hook.LifecycleHookName}: {hook.LifecycleTransition}, default {hook.DefaultResult}, heartbeat {hook.HeartbeatTimeout}s";

    /// <summary>The group and everything hanging off it, drawn as the tree the console would show.</summary>
    private static string RenderTree(AutoScalingGroup group, List<ScalingPolicy> policies, List<LifecycleHook> hooks, List<ScheduledUpdateGroupAction> actions)
    {
        List<string> branches =
        [
            $"launch configuration {group.LaunchConfigurationName}",
            $"zones {string.Join(", ", group.AvailabilityZones ?? [])}",
            $"size {group.MinSize}..{group.MaxSize}, desired {group.DesiredCapacity}, cooldown {group.DefaultCooldown}s, health check {group.HealthCheckType}",
            $"tags {string.Join(", ", (group.Tags ?? []).Select(t => $"{t.Key}={t.Value}"))}",
            $"instances {group.Instances?.Count ?? 0}",
            .. policies.Select(p => $"policy {DescribePolicy(p)}"),
            .. hooks.Select(h => $"hook {DescribeHook(h)}"),
            .. actions.Select(a => $"schedule {a.ScheduledActionName}: cron \"{a.Recurrence}\", {a.MinSize}..{a.MaxSize}"),
        ];

        List<string> lines = [$"auto scaling group {group.AutoScalingGroupName}"];

        for (int i = 0; i < branches.Count; i++)
        {
            lines.Add($"{(i == branches.Count - 1 ? "└─" : "├─")} {branches[i]}");
        }

        return string.Join("\n", lines);
    }

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
        // stops the run at the step it reached, and RunAsync's finally still deletes everything.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static async Task<AutoScalingGroup> DescribeGroupAsync(IAmazonAutoScaling client, string group, CancellationToken ct)
    {
        List<AutoScalingGroup> found = (await client.DescribeAutoScalingGroupsAsync(new DescribeAutoScalingGroupsRequest { AutoScalingGroupNames = [group] }, ct).ConfigureAwait(false)).AutoScalingGroups ?? [];

        return found.Count == 1
            ? found[0]
            : throw new InvalidOperationException($"DescribeAutoScalingGroups listed {found.Count} group(s) named {group}, expected one.");
    }

    private static async Task<List<ScalingPolicy>> ListPoliciesAsync(IAmazonAutoScaling client, string group, CancellationToken ct)
    {
        List<ScalingPolicy> all = [];
        string? token = null;

        do
        {
            DescribePoliciesResponse page = await client.DescribePoliciesAsync(new DescribePoliciesRequest { AutoScalingGroupName = group, NextToken = token }, ct).ConfigureAwait(false);
            all.AddRange(page.ScalingPolicies ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    /// <summary>The wire-level request shown beside an SDK call: the AWS Query protocol, one form-encoded POST / per operation.</summary>
    private string Call(string operation, string call) => $"POST {factory.ServiceUrl}/\nAction={operation}&Version=2011-01-01\n{call}";

    /// <summary>
    /// Deletes whatever this run created, looking each one up by its unique name so a resource whose
    /// create response was lost is still found, and one that was never created reads as a truthful
    /// "nothing to remove". Groups go before the launch configuration they use. Real Auto Scaling
    /// deletes a group asynchronously — it reads back as "Delete in progress" for a while — so each
    /// delete is followed by reads until the group is gone rather than one read; floci answers it on
    /// the first. Every delete is attempted before any failure is reported, so one stuck delete
    /// does not leak the rest.
    /// Uses <see cref="CancellationToken.None"/>: a cancelled run still has resources to delete.
    /// </summary>
    private async Task<DemoStep> DeleteEverythingAsync(IAmazonAutoScaling client, Claimed claimed, Names names, CancellationToken ct)
    {
        string request = this.Call("DeleteAutoScalingGroup", "client.DeleteAutoScalingGroupAsync { ForceDelete = true }, DeleteLaunchConfigurationAsync — each resource this run created");

        return await RunStepAsync("Delete everything — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<string> removed = [];
            List<string> failures = [];

            // Deleting a group that is not there is a ValidationError on floci and on AWS alike, so
            // each name is looked up first: absent reads as "nothing to remove", present is deleted
            // and then read until it is gone. Any failure is recorded, never thrown, so the
            // removals after it still run.
            async Task RemoveGroupAsync(string name)
            {
                try
                {
                    if ((await client.DescribeAutoScalingGroupsAsync(new DescribeAutoScalingGroupsRequest { AutoScalingGroupNames = [name] }, none).ConfigureAwait(false)).AutoScalingGroups is not { Count: > 0 })
                    {
                        return;
                    }

                    await client.DeleteAutoScalingGroupAsync(new DeleteAutoScalingGroupRequest { AutoScalingGroupName = name, ForceDelete = true }, none).ConfigureAwait(false);

                    for (int attempt = 0; attempt < 60; attempt++)
                    {
                        if ((await client.DescribeAutoScalingGroupsAsync(new DescribeAutoScalingGroupsRequest { AutoScalingGroupNames = [name] }, none).ConfigureAwait(false)).AutoScalingGroups is not { Count: > 0 })
                        {
                            removed.Add($"group {name}");
                            return;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(2), none).ConfigureAwait(false);
                    }

                    failures.Add($"Delete returned, but group {name} can still be read after two minutes.");
                }
                catch (Exception ex)
                {
                    failures.Add($"group {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            List<(bool Claim, string Name)> groups = [(claimed.OddRange, names.OddRange), (claimed.OddLaunch, names.OddLaunch), (claimed.Group, names.Group)];

            foreach ((bool claim, string name) in groups)
            {
                if (claim)
                {
                    await RemoveGroupAsync(name).ConfigureAwait(false);
                }
            }

            if (claimed.LaunchConfig)
            {
                try
                {
                    // The demo may already have deleted it (floci allows that while a group uses it).
                    if ((await client.DescribeLaunchConfigurationsAsync(new DescribeLaunchConfigurationsRequest { LaunchConfigurationNames = [names.LaunchConfig] }, none).ConfigureAwait(false)).LaunchConfigurations is { Count: > 0 })
                    {
                        await client.DeleteLaunchConfigurationAsync(new DeleteLaunchConfigurationRequest { LaunchConfigurationName = names.LaunchConfig }, none).ConfigureAwait(false);

                        if ((await client.DescribeLaunchConfigurationsAsync(new DescribeLaunchConfigurationsRequest { LaunchConfigurationNames = [names.LaunchConfig] }, none).ConfigureAwait(false)).LaunchConfigurations is { Count: > 0 })
                        {
                            failures.Add($"Delete returned, but launch configuration {names.LaunchConfig} can still be read.");
                        }
                        else
                        {
                            removed.Add($"launch configuration {names.LaunchConfig}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"launch configuration {names.LaunchConfig}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (failures.Count != 0)
            {
                throw new InvalidOperationException($"{failures.Count} resource(s) not deleted:\n{string.Join("\n", failures)}{cancelled}");
            }

            return removed.Count == 0
                ? $"No resource for this run exists — nothing to remove.{cancelled}"
                : $"Deleted {removed.Count} resource(s), each now absent from its Describe call:\n{string.Join("\n", removed)}{cancelled}";
        }).ConfigureAwait(false);
    }

    /// <summary>The unique names of everything one run creates.</summary>
    private sealed record Names(string LaunchConfig, string Group, string OddRange, string OddLaunch);

    /// <summary>Which of this run's resources have had a create call started.</summary>
    private sealed class Claimed
    {
        public bool LaunchConfig { get; set; }

        public bool Group { get; set; }

        public bool OddRange { get; set; }

        public bool OddLaunch { get; set; }

        public bool Any => this.LaunchConfig || this.Group || this.OddRange || this.OddLaunch;
    }
}
