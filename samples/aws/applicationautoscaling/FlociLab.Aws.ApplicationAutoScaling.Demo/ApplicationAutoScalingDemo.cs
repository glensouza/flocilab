using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.ApplicationAutoScaling;
using Amazon.ApplicationAutoScaling.Model;
using Amazon.Runtime;
using FlociLab.Core;

namespace FlociLab.Aws.ApplicationAutoScaling;

/// <summary>
/// Registers a DynamoDB table's write capacity as a scalable target, attaches a target-tracking
/// policy to it, reads the target back as the resource tree it now is, then asks for a scheduled
/// action, a capacity range that cannot hold, and the deregistration of a target that was never
/// registered, and deletes everything. Ordinary AWSSDK.ApplicationAutoScaling code — the only
/// emulator-aware line is in <see cref="ApplicationAutoScalingClientFactory"/>. The table does not
/// exist and does not need to: floci registers a target for any resource id, where real AWS looks
/// the resource up and refuses one that is not there, so the page does not run against a real
/// account as written. Every step reports what the engine actually answered rather than what the
/// docs promise.
/// </summary>
public sealed class ApplicationAutoScalingDemo(ApplicationAutoScalingClientFactory factory) : IServiceDemo
{
    private const int MinCapacity = 1;
    private const int MaxCapacity = 10;
    private const double TargetUtilization = 70;
    private const int ScheduledMinCapacity = 1;
    private const int ScheduledMaxCapacity = 2;
    private const string NightlyCron = "cron(0 2 * * ? *)";

    public string Provider => CloudProvider.Aws;

    public string Slug => "applicationautoscaling";

    public string DisplayName => "Application Auto Scaling";

    public string Category => "Containers and compute";

    public string Route => "/aws/applicationautoscaling";

    /// <summary>DescribeScalableTargets — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonApplicationAutoScaling client = factory.Create();
            DescribeScalableTargetsResponse response = await client.DescribeScalableTargetsAsync(new DescribeScalableTargetsRequest { ServiceNamespace = ServiceNamespace.Dynamodb }, ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"DescribeScalableTargets: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonApplicationAutoScaling client = factory.Create();

        string suffix = Guid.NewGuid().ToString("N")[..12];
        Names names = new($"table/flocilab-{suffix}", $"table/flocilab-{suffix}-range", $"table/flocilab-{suffix}-gone", $"write-{TargetUtilization}", $"flocilab-{suffix}-nightly");

        // Claimed before each create: a call that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created the resource, so the finally looks each name
        // up rather than trusting that a response arrived.
        Claimed claimed = new();

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep register = await RunStepAsync(
                "RegisterScalableTarget — a table's write capacity, 1 to 10 units",
                this.Call("RegisterScalableTarget", $"client.RegisterScalableTargetAsync(new RegisterScalableTargetRequest {{ ServiceNamespace = Dynamodb, ResourceId = \"{names.Table}\", ScalableDimension = DynamodbTableWriteCapacityUnits, MinCapacity = {MinCapacity}, MaxCapacity = {MaxCapacity} }})"),
                async () =>
                {
                    claimed.Table = true;
                    RegisterScalableTargetResponse registered = await client.RegisterScalableTargetAsync(NewTarget(names.Table, MinCapacity, MaxCapacity), ct).ConfigureAwait(false);

                    ScalableTarget found = await DescribeTargetAsync(client, names.Table, ct).ConfigureAwait(false);

                    if (found.MinCapacity != MinCapacity || found.MaxCapacity != MaxCapacity)
                    {
                        throw new InvalidOperationException($"RegisterScalableTarget was accepted but it reads back as {found.MinCapacity}..{found.MaxCapacity}.");
                    }

                    return $"{found.ResourceId}: {found.ScalableDimension?.Value}, {found.MinCapacity}..{found.MaxCapacity}\n{registered.ScalableTargetARN}\nthe table does not exist: floci registers any resource id, real AWS looks it up";
                }).ConfigureAwait(false);

            yield return register;

            // A failed register means every later step would look for a target that is not there.
            // Skipped rather than `yield break`, which would also skip yielding the cleanup step below.
            if (register.Succeeded)
            {
                yield return await RunStepAsync(
                    $"PutScalingPolicy, DescribeScalingPolicies — track {TargetUtilization}% write utilization",
                    this.Call("PutScalingPolicy", $"client.PutScalingPolicyAsync(new PutScalingPolicyRequest {{ PolicyName = \"{names.Policy}\", ServiceNamespace = Dynamodb, ResourceId = \"{names.Table}\", ScalableDimension = DynamodbTableWriteCapacityUnits, PolicyType = TargetTrackingScaling, TargetTrackingScalingPolicyConfiguration = {{ DynamoDBWriteCapacityUtilization, TargetValue = {TargetUtilization} }} }})"),
                    async () =>
                    {
                        claimed.Policy = true;
                        PutScalingPolicyResponse put = await client.PutScalingPolicyAsync(
                            new PutScalingPolicyRequest
                            {
                                PolicyName = names.Policy,
                                ServiceNamespace = ServiceNamespace.Dynamodb,
                                ResourceId = names.Table,
                                ScalableDimension = ScalableDimension.DynamodbTableWriteCapacityUnits,
                                PolicyType = PolicyType.TargetTrackingScaling,
                                TargetTrackingScalingPolicyConfiguration = new TargetTrackingScalingPolicyConfiguration
                                {
                                    PredefinedMetricSpecification = new PredefinedMetricSpecification { PredefinedMetricType = MetricType.DynamoDBWriteCapacityUtilization },
                                    TargetValue = TargetUtilization,
                                },
                            }, ct).ConfigureAwait(false);

                        List<ScalingPolicy> policies = await ListPoliciesAsync(client, names.Table, ct).ConfigureAwait(false);

                        if (policies.Count != 1)
                        {
                            throw new InvalidOperationException($"the policy was accepted but DescribeScalingPolicies lists {policies.Count}.");
                        }

                        ScalingPolicy read = policies[0];

                        if (read.PolicyName != names.Policy
                            || read.TargetTrackingScalingPolicyConfiguration?.PredefinedMetricSpecification?.PredefinedMetricType != MetricType.DynamoDBWriteCapacityUtilization
                            || read.TargetTrackingScalingPolicyConfiguration.TargetValue != TargetUtilization)
                        {
                            throw new InvalidOperationException($"the policy was accepted but it reads back as {DescribePolicy(read)}.");
                        }

                        return $"{DescribePolicy(policies[0])}\n{put.PolicyARN}\nalarms: {string.Join(", ", (put.Alarms ?? []).Select(a => a.AlarmName))}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "DescribeScalableTargets, DescribeScalingPolicies — the resource tree",
                    this.Call("DescribeScalableTargets", $"client.DescribeScalableTargetsAsync(new DescribeScalableTargetsRequest {{ ServiceNamespace = Dynamodb, ResourceIds = [\"{names.Table}\"] }})\nclient.DescribeScalingPoliciesAsync(new DescribeScalingPoliciesRequest {{ ServiceNamespace = Dynamodb, ResourceId = \"{names.Table}\" }})"),
                    async () =>
                    {
                        ScalableTarget target = await DescribeTargetAsync(client, names.Table, ct).ConfigureAwait(false);
                        List<ScalingPolicy> policies = await ListPoliciesAsync(client, names.Table, ct).ConfigureAwait(false);

                        return DescribeTree(target, policies);
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "DescribeScalingActivities — what the policy has done so far",
                    this.Call("DescribeScalingActivities", $"client.DescribeScalingActivitiesAsync(new DescribeScalingActivitiesRequest {{ ServiceNamespace = Dynamodb, ResourceId = \"{names.Table}\" }})"),
                    async () =>
                    {
                        DescribeScalingActivitiesResponse response = await client.DescribeScalingActivitiesAsync(
                            new DescribeScalingActivitiesRequest
                            {
                                ServiceNamespace = ServiceNamespace.Dynamodb,
                                ResourceId = names.Table,
                                ScalableDimension = ScalableDimension.DynamodbTableWriteCapacityUnits,
                            }, ct).ConfigureAwait(false);

                        List<ScalingActivity> activities = response.ScalingActivities ?? [];

                        return activities.Count == 0
                            ? "no activities: nothing has measured this table, so nothing has scaled it"
                            : string.Join("\n", activities.Select(a => $"{a.StatusCode}: {a.Description}"));
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "PutScheduledAction — a nightly shrink",
                    this.Call("PutScheduledAction", $"client.PutScheduledActionAsync(new PutScheduledActionRequest {{ ScheduledActionName = \"{names.Schedule}\", ServiceNamespace = Dynamodb, ResourceId = \"{names.Table}\", Schedule = \"{NightlyCron}\", ScalableTargetAction = {{ MinCapacity = {ScheduledMinCapacity}, MaxCapacity = {ScheduledMaxCapacity} }} }})"),
                    async () =>
                    {
                        try
                        {
                            claimed.Schedule = true;
                            await client.PutScheduledActionAsync(
                                new PutScheduledActionRequest
                                {
                                    ScheduledActionName = names.Schedule,
                                    ServiceNamespace = ServiceNamespace.Dynamodb,
                                    ResourceId = names.Table,
                                    ScalableDimension = ScalableDimension.DynamodbTableWriteCapacityUnits,
                                    Schedule = NightlyCron,
                                    ScalableTargetAction = new ScalableTargetAction { MinCapacity = ScheduledMinCapacity, MaxCapacity = ScheduledMaxCapacity },
                                }, ct).ConfigureAwait(false);

                            return "accepted\nfloci now implements scheduled actions: delete this step's tripwire and add the action to the resource tree";
                        }
                        // Not a 501: floci answers an operation it recognises but has not built with
                        // HTTP 400 and this code. Real Application Auto Scaling accepts the call.
                        catch (AmazonApplicationAutoScalingException ex) when (ex.ErrorCode == "UnsupportedOperation")
                        {
                            return $"{ex.ErrorCode}: {ex.Message}\nreal Application Auto Scaling accepts a scheduled action; floci has not built it";
                        }
                    }).ConfigureAwait(false);
            }

            yield return await RunStepAsync(
                "RegisterScalableTarget — MinCapacity above MaxCapacity",
                this.Call("RegisterScalableTarget", $"client.RegisterScalableTargetAsync(new RegisterScalableTargetRequest {{ ServiceNamespace = Dynamodb, ResourceId = \"{names.Range}\", ScalableDimension = DynamodbTableWriteCapacityUnits, MinCapacity = 10, MaxCapacity = 1 }})"),
                async () =>
                {
                    try
                    {
                        claimed.Range = true;
                        await client.RegisterScalableTargetAsync(NewTarget(names.Range, 10, 1), ct).ConfigureAwait(false);

                        return "accepted\nreal Application Auto Scaling refuses a minimum above the maximum with ValidationException; floci does not check";
                    }
                    // What real Application Auto Scaling answers: the expected outcome, shown as a successful step.
                    // Only a refusal about the capacities counts: against real AWS the table does not
                    // exist either, and a ValidationException for that is not the range check.
                    catch (AmazonApplicationAutoScalingException ex) when (ex.ErrorCode == "ValidationException" && ex.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"{ex.ErrorCode}: {ex.Message}";
                    }
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeregisterScalableTarget — a target that was never registered",
                this.Call("DeregisterScalableTarget", $"client.DeregisterScalableTargetAsync(new DeregisterScalableTargetRequest {{ ServiceNamespace = Dynamodb, ResourceId = \"{names.Gone}\", ScalableDimension = DynamodbTableWriteCapacityUnits }})"),
                async () =>
                {
                    try
                    {
                        await client.DeregisterScalableTargetAsync(
                            new DeregisterScalableTargetRequest
                            {
                                ServiceNamespace = ServiceNamespace.Dynamodb,
                                ResourceId = names.Gone,
                                ScalableDimension = ScalableDimension.DynamodbTableWriteCapacityUnits,
                            }, ct).ConfigureAwait(false);

                        return "accepted\nreal Application Auto Scaling answers ObjectNotFoundException; floci has started to accept it";
                    }
                    // What real Application Auto Scaling answers: the expected outcome, shown as a successful step.
                    catch (ObjectNotFoundException ex)
                    {
                        return $"{ex.GetType().Name}: {ex.Message}";
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // An iterator may not yield from a finally, so the step is yielded below.
            if (claimed.Any)
            {
                cleanup.Add(await this.DeleteEverythingAsync(client, claimed, names, ct).ConfigureAwait(false));
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

    private static RegisterScalableTargetRequest NewTarget(string resourceId, int min, int max)
        => new()
        {
            ServiceNamespace = ServiceNamespace.Dynamodb,
            ResourceId = resourceId,
            ScalableDimension = ScalableDimension.DynamodbTableWriteCapacityUnits,
            MinCapacity = min,
            MaxCapacity = max,
        };

    private static string DescribePolicy(ScalingPolicy policy)
        => $"{policy.PolicyName}: {policy.PolicyType?.Value}, {policy.TargetTrackingScalingPolicyConfiguration?.PredefinedMetricSpecification?.PredefinedMetricType?.Value} at {policy.TargetTrackingScalingPolicyConfiguration?.TargetValue}";

    private static string DescribeTree(ScalableTarget target, List<ScalingPolicy> policies)
    {
        List<string> branches =
        [
            $"{target.ScalableDimension?.Value}: {target.MinCapacity}..{target.MaxCapacity}",
            .. policies.Select(p => $"policy {DescribePolicy(p)}"),
        ];

        List<string> lines = [$"scalable target {target.ServiceNamespace?.Value}/{target.ResourceId}"];

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

    private static async Task<ScalableTarget> DescribeTargetAsync(IAmazonApplicationAutoScaling client, string resourceId, CancellationToken ct)
    {
        List<ScalableTarget> found = (await client.DescribeScalableTargetsAsync(new DescribeScalableTargetsRequest { ServiceNamespace = ServiceNamespace.Dynamodb, ResourceIds = [resourceId] }, ct).ConfigureAwait(false)).ScalableTargets ?? [];

        return found.Count == 1
            ? found[0]
            : throw new InvalidOperationException($"DescribeScalableTargets listed {found.Count} target(s) for {resourceId}, expected one.");
    }

    private static async Task<List<ScalingPolicy>> ListPoliciesAsync(IAmazonApplicationAutoScaling client, string resourceId, CancellationToken ct)
    {
        List<ScalingPolicy> all = [];
        string? token = null;

        do
        {
            DescribeScalingPoliciesResponse page = await client.DescribeScalingPoliciesAsync(new DescribeScalingPoliciesRequest { ServiceNamespace = ServiceNamespace.Dynamodb, ResourceId = resourceId, NextToken = token }, ct).ConfigureAwait(false);
            all.AddRange(page.ScalingPolicies ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    /// <summary>The wire-level request shown beside an SDK call: the AWS JSON 1.1 protocol, one POST / per operation named by X-Amz-Target.</summary>
    private string Call(string operation, string call) => $"POST {factory.ServiceUrl}/\nX-Amz-Target: AnyScaleFrontendService.{operation}\n{call}";

    /// <summary>
    /// Deletes whatever this run created, looking each one up by its unique name so a resource whose
    /// create response was lost is still found, and one that was never created reads as a truthful
    /// "nothing to remove". The policy goes before the target it is attached to. Every delete is
    /// attempted before any failure is reported, so one stuck delete does not leak the rest.
    /// Uses <see cref="CancellationToken.None"/>: a cancelled run still has resources to delete.
    /// </summary>
    private async Task<DemoStep> DeleteEverythingAsync(IAmazonApplicationAutoScaling client, Claimed claimed, Names names, CancellationToken ct)
    {
        string request = this.Call("DeleteScalingPolicy", "client.DeleteScheduledActionAsync, DeleteScalingPolicyAsync, DeregisterScalableTargetAsync — each resource this run created");

        return await RunStepAsync("Delete everything — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<string> removed = [];
            List<string> failures = [];

            // Deleting or deregistering what is not there is an ObjectNotFoundException, so each
            // target is looked up first: absent reads as "nothing to remove", present is cleaned up
            // and then read until it is gone. Any failure is recorded, never thrown, so the
            // removals after it still run.
            async Task RemoveTargetAsync(string resourceId, bool withPolicy)
            {
                try
                {
                    if ((await client.DescribeScalableTargetsAsync(new DescribeScalableTargetsRequest { ServiceNamespace = ServiceNamespace.Dynamodb, ResourceIds = [resourceId] }, none).ConfigureAwait(false)).ScalableTargets is not { Count: > 0 })
                    {
                        return;
                    }

                    if (withPolicy && (await ListPoliciesAsync(client, resourceId, none).ConfigureAwait(false)).Any(p => p.PolicyName == names.Policy))
                    {
                        await client.DeleteScalingPolicyAsync(
                            new DeleteScalingPolicyRequest
                            {
                                PolicyName = names.Policy,
                                ServiceNamespace = ServiceNamespace.Dynamodb,
                                ResourceId = resourceId,
                                ScalableDimension = ScalableDimension.DynamodbTableWriteCapacityUnits,
                            }, none).ConfigureAwait(false);

                        if ((await ListPoliciesAsync(client, resourceId, none).ConfigureAwait(false)).Any(p => p.PolicyName == names.Policy))
                        {
                            failures.Add($"Delete returned, but policy {names.Policy} on {resourceId} can still be read.");
                        }
                        else
                        {
                            removed.Add($"policy {names.Policy} on {resourceId}");
                        }
                    }

                    await client.DeregisterScalableTargetAsync(
                        new DeregisterScalableTargetRequest
                        {
                            ServiceNamespace = ServiceNamespace.Dynamodb,
                            ResourceId = resourceId,
                            ScalableDimension = ScalableDimension.DynamodbTableWriteCapacityUnits,
                        }, none).ConfigureAwait(false);

                    if ((await client.DescribeScalableTargetsAsync(new DescribeScalableTargetsRequest { ServiceNamespace = ServiceNamespace.Dynamodb, ResourceIds = [resourceId] }, none).ConfigureAwait(false)).ScalableTargets is { Count: > 0 })
                    {
                        failures.Add($"Deregister returned, but target {resourceId} can still be read.");
                    }
                    else
                    {
                        removed.Add($"target {resourceId}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"target {resourceId}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Before the target it belongs to. floci answers every scheduled-action call with
            // UnsupportedOperation, which means there is nothing to remove; once it builds them,
            // this deletes the run's action instead of leaving it behind.
            if (claimed.Schedule)
            {
                try
                {
                    DescribeScheduledActionsResponse scheduled = await client.DescribeScheduledActionsAsync(
                        new DescribeScheduledActionsRequest
                        {
                            ServiceNamespace = ServiceNamespace.Dynamodb,
                            ResourceId = names.Table,
                            ScheduledActionNames = [names.Schedule],
                        }, none).ConfigureAwait(false);

                    if (scheduled.ScheduledActions is { Count: > 0 })
                    {
                        await client.DeleteScheduledActionAsync(
                            new DeleteScheduledActionRequest
                            {
                                ScheduledActionName = names.Schedule,
                                ServiceNamespace = ServiceNamespace.Dynamodb,
                                ResourceId = names.Table,
                                ScalableDimension = ScalableDimension.DynamodbTableWriteCapacityUnits,
                            }, none).ConfigureAwait(false);

                        removed.Add($"scheduled action {names.Schedule}");
                    }
                }
                catch (AmazonApplicationAutoScalingException ex) when (ex.ErrorCode == "UnsupportedOperation")
                {
                }
                catch (Exception ex)
                {
                    failures.Add($"scheduled action {names.Schedule}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (claimed.Range)
            {
                await RemoveTargetAsync(names.Range, withPolicy: false).ConfigureAwait(false);
            }

            if (claimed.Table)
            {
                await RemoveTargetAsync(names.Table, withPolicy: claimed.Policy).ConfigureAwait(false);
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

    /// <summary>The unique names of everything one run creates or asks about.</summary>
    private sealed record Names(string Table, string Range, string Gone, string Policy, string Schedule);

    /// <summary>Which of this run's resources have had a create call started.</summary>
    private sealed class Claimed
    {
        public bool Table { get; set; }

        public bool Policy { get; set; }

        public bool Range { get; set; }

        public bool Schedule { get; set; }

        public bool Any => this.Table || this.Policy || this.Range || this.Schedule;
    }
}
