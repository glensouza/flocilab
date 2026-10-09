using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.Runtime;
using FlociLab.Core;

// The SDK has its own InvalidOperationException; this sample means the BCL one.
using InvalidOperationException = System.InvalidOperationException;

namespace FlociLab.Aws.CloudFormation;

/// <summary>
/// Validates a template, creates a stack from it, reads the stack back as the resource tree it is,
/// follows its events, fetches the template, changes the stack through a change set, then asks for
/// a second stack under a name that is taken, a stack with a resource type that does not exist and
/// a drift check, and deletes everything. Ordinary AWSSDK.CloudFormation code — the only emulator-aware line
/// is in <see cref="CloudFormationClientFactory"/>. floci provisions the template's resources
/// itself, synchronously: a stack reads CREATE_COMPLETE before the create call has returned its
/// answer to the next one, where real CloudFormation spends minutes in CREATE_IN_PROGRESS, so the
/// page does not wait for one. Every step reports what the engine actually answered rather than
/// what the docs promise.
/// </summary>
public sealed class CloudFormationDemo(CloudFormationClientFactory factory) : IServiceDemo
{
    /// <summary>
    /// What the stack is made of. The queue and the bucket are named from the <c>Prefix</c> parameter, so
    /// two runs never collide on a physical name; the output hands the queue's identifier back.
    /// </summary>
    private const string Template = """
        {
          "AWSTemplateFormatVersion": "2010-09-09",
          "Description": "FlociLab demo stack",
          "Parameters": { "Prefix": { "Type": "String" } },
          "Resources": {
            "Queue": { "Type": "AWS::SQS::Queue", "Properties": { "QueueName": { "Fn::Sub": "${Prefix}-queue" } } },
            "Bucket": { "Type": "AWS::S3::Bucket", "Properties": { "BucketName": { "Fn::Sub": "${Prefix}-bucket" } } }
          },
          "Outputs": { "QueueUrl": { "Value": { "Ref": "Queue" } } }
        }
        """;

    /// <summary>The same stack plus a topic: what the change set adds.</summary>
    private const string TemplateWithTopic = """
        {
          "AWSTemplateFormatVersion": "2010-09-09",
          "Description": "FlociLab demo stack",
          "Parameters": { "Prefix": { "Type": "String" } },
          "Resources": {
            "Queue": { "Type": "AWS::SQS::Queue", "Properties": { "QueueName": { "Fn::Sub": "${Prefix}-queue" } } },
            "Bucket": { "Type": "AWS::S3::Bucket", "Properties": { "BucketName": { "Fn::Sub": "${Prefix}-bucket" } } },
            "Topic": { "Type": "AWS::SNS::Topic", "Properties": { "TopicName": { "Fn::Sub": "${Prefix}-topic" } } }
          },
          "Outputs": { "QueueUrl": { "Value": { "Ref": "Queue" } } }
        }
        """;

    private const string ChangeSetName = "flocilab-add-topic";

    /// <summary>A resource type that does not exist: real CloudFormation refuses the template before creating anything.</summary>
    private const string UnknownTypeTemplate = """{ "Resources": { "Thing": { "Type": "AWS::Nope::Thing" } } }""";

    public string Provider => CloudProvider.Aws;

    public string Slug => "cloudformation";

    public string DisplayName => "CloudFormation";

    public string Category => "Developer tools and delivery";

    public string Route => "/aws/cloudformation";

    /// <summary>ListStacks — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonCloudFormation client = factory.Create();
            ListStacksResponse response = await client.ListStacksAsync(new ListStacksRequest(), ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListStacks: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonCloudFormation client = factory.Create();

        string suffix = Guid.NewGuid().ToString("N")[..12];
        Names names = new($"flocilab-{suffix}", $"flocilab-{suffix}-unknown", $"fl{suffix}");

        // Claimed before each create: a call that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created the stack, so the finally looks each name up
        // rather than trusting that a response arrived.
        Claimed claimed = new();

        List<DemoStep> cleanup = [];

        try
        {
            yield return await RunStepAsync(
                "ValidateTemplate — what the template declares",
                this.Call("ValidateTemplate", $"client.ValidateTemplateAsync(new ValidateTemplateRequest {{ TemplateBody = Template }})\n\nTemplateBody:\n{Template}"),
                async () =>
                {
                    ValidateTemplateResponse response = await client.ValidateTemplateAsync(new ValidateTemplateRequest { TemplateBody = Template }, ct).ConfigureAwait(false);
                    List<TemplateParameter> parameters = response.Parameters ?? [];

                    // Tripwire: the template declares one parameter. Real CloudFormation lists it; floci
                    // answers an empty list for any body, including text that is not a template at all.
                    return parameters.Count != 0
                        ? $"{parameters.Count} parameter(s): {string.Join(", ", parameters.Select(p => p.ParameterKey))}"
                        : factory.UseEmulator
                            ? "0 parameters, although the template declares Prefix\nfloci accepts the body without reading it: real CloudFormation lists Prefix here and refuses a body that is not a template"
                            : "0 parameters";
                }).ConfigureAwait(false);

            DemoStep created = await RunStepAsync(
                "CreateStack, DescribeStacks — a queue and a bucket from one template",
                this.Call("CreateStack", $"client.CreateStackAsync(new CreateStackRequest {{ StackName = \"{names.Stack}\", TemplateBody = Template, Parameters = [new Parameter {{ ParameterKey = \"Prefix\", ParameterValue = \"{names.Prefix}\" }}] }})\nclient.DescribeStacksAsync(new DescribeStacksRequest {{ StackName = \"{names.Stack}\" }})"),
                async () =>
                {
                    claimed.Stack = true;
                    CreateStackResponse response = await client.CreateStackAsync(
                        new CreateStackRequest
                        {
                            StackName = names.Stack,
                            TemplateBody = Template,
                            Parameters = [new Parameter { ParameterKey = "Prefix", ParameterValue = names.Prefix }],
                        }, ct).ConfigureAwait(false);

                    Stack stack = await this.WaitForStackAsync(client, names.Stack, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("CreateStack was accepted but DescribeStacks does not list the stack.");

                    if (stack.StackStatus != StackStatus.CREATE_COMPLETE)
                    {
                        throw new InvalidOperationException($"the stack reads {stack.StackStatus?.Value}: {stack.StackStatusReason}");
                    }

                    string note = factory.UseEmulator
                        ? "\nfloci provisions at once: the stack reads CREATE_COMPLETE on the first read, real CloudFormation reads CREATE_IN_PROGRESS for minutes"
                        : string.Empty;

                    return $"{DescribeStack(stack)}\n{response.StackId}\n{string.Join("\n", (stack.Outputs ?? []).Select(o => $"output {o.OutputKey} = {o.OutputValue}"))}{note}";
                }).ConfigureAwait(false);

            yield return created;

            // A failed create means every later step would look for a stack that is not there. Skipped
            // rather than `yield break`, which would also skip yielding the cleanup step below.
            if (created.Succeeded)
            {
                yield return await RunStepAsync(
                    "DescribeStackResources, ListStackResources — the resource tree",
                    this.Call("DescribeStackResources", $"client.DescribeStackResourcesAsync(new DescribeStackResourcesRequest {{ StackName = \"{names.Stack}\" }})\nclient.ListStackResourcesAsync(new ListStackResourcesRequest {{ StackName = \"{names.Stack}\" }})"),
                    async () =>
                    {
                        List<StackResource> resources = (await client.DescribeStackResourcesAsync(new DescribeStackResourcesRequest { StackName = names.Stack }, ct).ConfigureAwait(false)).StackResources ?? [];
                        List<StackResourceSummary> summaries = (await client.ListStackResourcesAsync(new ListStackResourcesRequest { StackName = names.Stack }, ct).ConfigureAwait(false)).StackResourceSummaries ?? [];

                        if (summaries.Count != resources.Count)
                        {
                            throw new InvalidOperationException($"DescribeStackResources lists {resources.Count} resource(s) and ListStackResources lists {summaries.Count}.");
                        }

                        return DescribeTree(names.Stack, resources);
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "DescribeStackEvents — what happened, oldest first",
                    this.Call("DescribeStackEvents", $"client.DescribeStackEventsAsync(new DescribeStackEventsRequest {{ StackName = \"{names.Stack}\" }})"),
                    async () =>
                    {
                        List<StackEvent> events = (await client.DescribeStackEventsAsync(new DescribeStackEventsRequest { StackName = names.Stack }, ct).ConfigureAwait(false)).StackEvents ?? [];

                        if (events.Count == 0)
                        {
                            throw new InvalidOperationException("DescribeStackEvents answered an empty list for a stack that was just created.");
                        }

                        // The service answers newest first; a story reads the other way. Reversed before
                        // the sort, because floci provisions in one go and events can share a timestamp:
                        // the stable sort would keep those ties newest first.
                        return string.Join("\n", events.AsEnumerable().Reverse().OrderBy(e => e.Timestamp).Select(e => $"{e.LogicalResourceId}  {e.ResourceType}  {e.ResourceStatus?.Value}"));
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "GetTemplate — the body comes back",
                    this.Call("GetTemplate", $"client.GetTemplateAsync(new GetTemplateRequest {{ StackName = \"{names.Stack}\" }})"),
                    async () =>
                    {
                        GetTemplateResponse response = await client.GetTemplateAsync(new GetTemplateRequest { StackName = names.Stack }, ct).ConfigureAwait(false);

                        if (response.TemplateBody is null || !response.TemplateBody.Contains("AWS::SQS::Queue", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("GetTemplate did not return the template the stack was created from.");
                        }

                        return $"{response.TemplateBody.Length} characters, stages: {string.Join(", ", response.StagesAvailable ?? [])}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateChangeSet, DescribeChangeSet, ExecuteChangeSet — adding a topic",
                    this.Call("CreateChangeSet", $"client.CreateChangeSetAsync(new CreateChangeSetRequest {{ StackName = \"{names.Stack}\", ChangeSetName = \"{ChangeSetName}\", ChangeSetType = ChangeSetType.UPDATE, TemplateBody = TemplateWithTopic, Parameters = [...] }})\nclient.DescribeChangeSetAsync(new DescribeChangeSetRequest {{ ... }})\nclient.ExecuteChangeSetAsync(new ExecuteChangeSetRequest {{ ... }})"),
                    async () =>
                    {
                        // A change set is deleted with its stack, so cleanup needs no delete of its own.
                        // ChangeSetType.UPDATE is spelled out because floci defaults to CREATE and answers
                        // AlreadyExistsException for a stack that is there.
                        await client.CreateChangeSetAsync(
                            new CreateChangeSetRequest
                            {
                                StackName = names.Stack,
                                ChangeSetName = ChangeSetName,
                                ChangeSetType = ChangeSetType.UPDATE,
                                TemplateBody = TemplateWithTopic,
                                Parameters = [new Parameter { ParameterKey = "Prefix", ParameterValue = names.Prefix }],
                            }, ct).ConfigureAwait(false);

                        DescribeChangeSetResponse changeSet = await this.WaitAsync(
                            () => client.DescribeChangeSetAsync(new DescribeChangeSetRequest { StackName = names.Stack, ChangeSetName = ChangeSetName }, ct),
                            r => r.Status != ChangeSetStatus.CREATE_PENDING && r.Status != ChangeSetStatus.CREATE_IN_PROGRESS,
                            ct).ConfigureAwait(false);

                        if (changeSet.Status != ChangeSetStatus.CREATE_COMPLETE)
                        {
                            throw new InvalidOperationException($"the change set reads {changeSet.Status?.Value}: {changeSet.StatusReason}");
                        }

                        string changes = string.Join("\n", (changeSet.Changes ?? []).Select(c => $"{c.ResourceChange?.Action?.Value} {c.ResourceChange?.LogicalResourceId} ({c.ResourceChange?.ResourceType})"));

                        await client.ExecuteChangeSetAsync(new ExecuteChangeSetRequest { StackName = names.Stack, ChangeSetName = ChangeSetName }, ct).ConfigureAwait(false);

                        // ExecuteChangeSet returns before the update starts: real CloudFormation can still
                        // read CREATE_COMPLETE on the first look, so only a settled UPDATE_* status ends the wait.
                        Stack stack = await this.WaitAsync(
                            () => DescribeStackAsync(client, names.Stack, ct),
                            s => s is null || (s.StackStatus?.Value.StartsWith("UPDATE_", StringComparison.Ordinal) == true && !s.StackStatus.Value.EndsWith("_IN_PROGRESS", StringComparison.Ordinal)),
                            ct).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("the stack is gone after ExecuteChangeSet.");

                        if (stack.StackStatus != StackStatus.UPDATE_COMPLETE)
                        {
                            throw new InvalidOperationException($"after ExecuteChangeSet the stack reads {stack.StackStatus?.Value}: {stack.StackStatusReason}");
                        }

                        int resources = (await client.ListStackResourcesAsync(new ListStackResourcesRequest { StackName = names.Stack }, ct).ConfigureAwait(false)).StackResourceSummaries?.Count ?? 0;

                        return $"change set {changeSet.ChangeSetName}: {changeSet.Status?.Value}, {changeSet.ExecutionStatus?.Value}\n{changes}\n{DescribeStack(stack)}\n{resources} resource(s) now";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateStack — a name that is taken",
                    this.Call("CreateStack", $"client.CreateStackAsync(new CreateStackRequest {{ StackName = \"{names.Stack}\", TemplateBody = Template, Parameters = [...] }})"),
                    async () =>
                    {
                        try
                        {
                            await client.CreateStackAsync(
                                new CreateStackRequest
                                {
                                    StackName = names.Stack,
                                    TemplateBody = Template,
                                    Parameters = [new Parameter { ParameterKey = "Prefix", ParameterValue = names.Prefix }],
                                }, ct).ConfigureAwait(false);

                            return factory.UseEmulator
                                ? "accepted\nreal CloudFormation refuses a second stack of the same name with AlreadyExistsException; floci has started to accept it"
                                : "accepted, although a stack of that name exists";
                        }
                        // What real CloudFormation answers: the expected outcome, shown as a successful step.
                        catch (AlreadyExistsException ex)
                        {
                            return $"{ex.ErrorCode}: {ex.Message}";
                        }
                    }).ConfigureAwait(false);
            }

            yield return await RunStepAsync(
                "CreateStack — a resource type that does not exist",
                this.Call("CreateStack", $"client.CreateStackAsync(new CreateStackRequest {{ StackName = \"{names.UnknownStack}\", TemplateBody = \"\"\"{UnknownTypeTemplate}\"\"\" }})"),
                async () =>
                {
                    try
                    {
                        claimed.UnknownStack = true;
                        await client.CreateStackAsync(new CreateStackRequest { StackName = names.UnknownStack, TemplateBody = UnknownTypeTemplate }, ct).ConfigureAwait(false);

                        Stack? stack = await this.WaitForStackAsync(client, names.UnknownStack, ct).ConfigureAwait(false);

                        // Tripwire: real CloudFormation refuses the template before it creates anything.
                        return $"accepted, and the stack reads {stack?.StackStatus?.Value}\nreal CloudFormation answers ValidationError (Unrecognized resource types); floci does not check a resource type against anything";
                    }
                    // What real CloudFormation answers: the expected outcome. Only a refusal about the
                    // template counts, not any bad request.
                    catch (AmazonCloudFormationException ex) when (ex.ErrorCode == "ValidationError" && ex.Message.Contains("resource type", StringComparison.OrdinalIgnoreCase))
                    {
                        claimed.UnknownStack = false;

                        return $"{ex.ErrorCode}: {ex.Message}";
                    }
                }).ConfigureAwait(false);

            // Drift needs the stack; the unknown-type step above does not.
            if (created.Succeeded)
            {
                yield return await RunStepAsync(
                    "DetectStackDrift — has the stack moved from its template",
                    this.Call("DetectStackDrift", $"client.DetectStackDriftAsync(new DetectStackDriftRequest {{ StackName = \"{names.Stack}\" }})"),
                    async () =>
                    {
                        try
                        {
                            DetectStackDriftResponse response = await client.DetectStackDriftAsync(new DetectStackDriftRequest { StackName = names.Stack }, ct).ConfigureAwait(false);

                            return factory.UseEmulator
                                ? $"accepted: {response.StackDriftDetectionId}\nfloci now implements drift detection: delete this step's tripwire and read the result with DescribeStackDriftDetectionStatus"
                                : $"accepted: {response.StackDriftDetectionId}";
                        }
                        // Not a 501: floci answers an operation it does not know with HTTP 400 and this
                        // code. Real CloudFormation accepts the call.
                        catch (AmazonCloudFormationException ex) when (ex.ErrorCode == "UnknownAction")
                        {
                            return $"{ex.ErrorCode}: {ex.Message}\nreal CloudFormation starts a drift detection; floci has not built it";
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
    /// cannot classify it on its own. floci answers an operation it does not know with HTTP 400 and the
    /// error code <c>UnknownAction</c>, which is the not-implemented outcome here.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                case AmazonServiceException { ErrorCode: "UnknownAction" or "UnsupportedOperation" }:
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

    private static string DescribeStack(Stack stack)
        => $"{stack.StackName}: {stack.StackStatus?.Value}";

    private static string DescribeTree(string stackName, List<StackResource> resources)
    {
        List<string> lines = [$"stack {stackName}"];

        for (int i = 0; i < resources.Count; i++)
        {
            StackResource resource = resources[i];
            lines.Add($"{(i == resources.Count - 1 ? "└─" : "├─")} {resource.LogicalResourceId} {resource.ResourceType}: {resource.ResourceStatus?.Value} → {resource.PhysicalResourceId}");
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

    /// <summary>
    /// The stack of that name; a deleted one is history, not a resource to clean up. Real
    /// CloudFormation answers ValidationError for a stack that is gone, and floci does the same.
    /// </summary>
    private static async Task<Stack?> DescribeStackAsync(IAmazonCloudFormation client, string name, CancellationToken ct)
    {
        try
        {
            Stack? stack = (await client.DescribeStacksAsync(new DescribeStacksRequest { StackName = name }, ct).ConfigureAwait(false)).Stacks?.FirstOrDefault();

            return stack?.StackStatus == StackStatus.DELETE_COMPLETE ? null : stack;
        }
        catch (AmazonCloudFormationException ex) when (ex.ErrorCode == "ValidationError" && ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    /// <summary>Reads the stack until it is no longer in a transitional <c>*_IN_PROGRESS</c> state.</summary>
    private async Task<Stack?> WaitForStackAsync(IAmazonCloudFormation client, string name, CancellationToken ct)
        => await this.WaitAsync(
            () => DescribeStackAsync(client, name, ct),
            s => s is null || s.StackStatus?.Value.EndsWith("_IN_PROGRESS", StringComparison.Ordinal) != true,
            ct).ConfigureAwait(false);

    /// <summary>
    /// Reads until <paramref name="settled"/> holds. Against floci it reads once, so a stack that reads
    /// complete at once stays a tripwire rather than something this waits out; real CloudFormation
    /// spends minutes creating or deleting, so there it polls for up to twenty minutes and returns
    /// whatever it last read.
    /// </summary>
    private async Task<T> WaitAsync<T>(Func<Task<T>> read, Func<T, bool> settled, CancellationToken ct)
    {
        TimeSpan limit = factory.UseEmulator ? TimeSpan.Zero : TimeSpan.FromMinutes(20);
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            T value = await read().ConfigureAwait(false);

            if (settled(value) || Stopwatch.GetElapsedTime(started) >= limit)
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The wire-level request shown beside an SDK call: the AWS Query protocol, one form-encoded POST / per operation named by Action.</summary>
    private string Call(string operation, string call) => $"POST {factory.ServiceUrl}/\nAction={operation}&Version=2010-05-15\n{call}";

    /// <summary>
    /// Deletes whatever this run created, looking each stack up by its unique name so one whose create
    /// response was lost is still found, and one that was never created reads as a truthful "nothing
    /// to remove". Deleting a stack deletes the resources it provisioned and its change sets. Every
    /// delete is attempted before any failure is reported, so one stuck delete does not leak the
    /// rest. Uses <see cref="CancellationToken.None"/>: a cancelled run still has resources to delete.
    /// </summary>
    private async Task<DemoStep> DeleteEverythingAsync(IAmazonCloudFormation client, Claimed claimed, Names names, CancellationToken ct)
    {
        string request = this.Call("DeleteStack", "client.DeleteStackAsync(new DeleteStackRequest { StackName = ... }) — each stack this run created");

        return await RunStepAsync("Delete everything — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<string> removed = [];
            List<string> failures = [];

            List<string> claimedNames = [];

            if (claimed.UnknownStack)
            {
                claimedNames.Add(names.UnknownStack);
            }

            if (claimed.Stack)
            {
                claimedNames.Add(names.Stack);
            }

            foreach (string name in claimedNames)
            {
                try
                {
                    if (await DescribeStackAsync(client, name, none).ConfigureAwait(false) is null)
                    {
                        continue;
                    }

                    await client.DeleteStackAsync(new DeleteStackRequest { StackName = name }, none).ConfigureAwait(false);

                    // Real CloudFormation reads DELETE_IN_PROGRESS for minutes. DELETE_FAILED is settled
                    // too: waiting on it would poll the full twenty minutes, with nothing able to stop it.
                    if (await this.WaitForStackAsync(client, name, none).ConfigureAwait(false) is { } left)
                    {
                        failures.Add($"Delete returned, but stack {name} still reads {left.StackStatus?.Value}.");
                    }
                    else
                    {
                        removed.Add($"stack {name} (and what it provisioned)");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"stack {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (failures.Count != 0)
            {
                throw new InvalidOperationException($"{failures.Count} stack(s) not deleted:\n{string.Join("\n", failures)}{cancelled}");
            }

            return removed.Count == 0
                ? $"No stack for this run exists — nothing to remove.{cancelled}"
                : $"Deleted {removed.Count} stack(s), each now absent from DescribeStacks:\n{string.Join("\n", removed)}{cancelled}";
        }).ConfigureAwait(false);
    }

    /// <summary>The unique names of everything one run creates. <paramref name="Prefix"/> names the stack's physical resources.</summary>
    private sealed record Names(string Stack, string UnknownStack, string Prefix);

    /// <summary>Which of this run's stacks have had a create call started.</summary>
    private sealed class Claimed
    {
        public bool Stack { get; set; }

        public bool UnknownStack { get; set; }

        public bool Any => this.Stack || this.UnknownStack;
    }
}
