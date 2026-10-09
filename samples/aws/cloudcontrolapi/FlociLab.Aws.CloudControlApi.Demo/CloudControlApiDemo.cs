using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.CloudControlApi;
using Amazon.CloudControlApi.Model;
using Amazon.Runtime;
using FlociLab.Core;

// The SDK has its own InvalidOperationException; this sample means the BCL one.
using InvalidOperationException = System.InvalidOperationException;

namespace FlociLab.Aws.CloudControlApi;

/// <summary>
/// Creates a bucket, a queue and a topic through one API that knows none of those services' own,
/// reads them back as a resource tree, lists what the account holds, tries the operations floci has
/// not built, asks for a bucket whose name is taken and for things that are not there, and deletes
/// everything. Ordinary AWSSDK.CloudControlApi code — the only emulator wiring is in
/// <see cref="CloudControlApiClientFactory"/>; this class reads <c>UseEmulator</c> only to poll
/// faster and to name floci's gap where it differs. A Cloud Control call is a request that finishes
/// later: <c>CreateResource</c> answers <c>IN_PROGRESS</c> with a token, and
/// <c>GetResourceRequestStatus</c> says when it is done and what the resource is called. Every step
/// reports what the engine actually answered rather than what the docs promise.
/// </summary>
public sealed class CloudControlApiDemo(CloudControlApiClientFactory factory) : IServiceDemo
{
    private const string BucketType = "AWS::S3::Bucket";

    private const string QueueType = "AWS::SQS::Queue";

    private const string TopicType = "AWS::SNS::Topic";

    private const string RoleType = "AWS::IAM::Role";

    /// <summary>A JSON Patch (RFC 6902), the form <c>UpdateResource</c> takes: it adds a tag.</summary>
    private const string TagPatch = """[{"op":"add","path":"/Tags","value":[{"Key":"owner","Value":"flocilab"}]}]""";

    public string Provider => CloudProvider.Aws;

    public string Slug => "cloudcontrolapi";

    public string DisplayName => "Cloud Control API";

    public string Category => "Developer tools and delivery";

    public string Route => "/aws/cloudcontrolapi";

    /// <summary>ListResources for buckets — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonCloudControlApi client = factory.Create();
            ListResourcesResponse response = await client.ListResourcesAsync(new ListResourcesRequest { TypeName = BucketType }, ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListResources: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonCloudControlApi client = factory.Create();

        string suffix = Guid.NewGuid().ToString("N")[..12];
        string bucketName = $"flocilab-{suffix}";

        // Each resource is marked started before its create call: a call that lands while its
        // response is lost (the page's Dispose cancels mid-flight) still created the resource, so
        // the finally looks each one up rather than trusting that a response arrived.
        Resource bucket = new(BucketType, $$"""{"BucketName":"{{bucketName}}"}""") { Fallback = bucketName };
        Resource queue = new(QueueType, $$"""{"QueueName":"flocilab-{{suffix}}"}""");
        Resource topic = new(TopicType, $$"""{"TopicName":"flocilab-{{suffix}}"}""");
        List<Resource> resources = [bucket, queue, topic];

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep created = await RunStepAsync(
                "CreateResource, GetResourceRequestStatus — a bucket, a queue and a topic",
                this.Call("CreateResource", $"client.CreateResourceAsync(new CreateResourceRequest {{ TypeName = \"...\", DesiredState = \"...\" }}) — three times\nclient.GetResourceRequestStatusAsync(new GetResourceRequestStatusRequest {{ RequestToken = ... }})\n\n{string.Join("\n", resources.Select(r => $"{r.Type}  {r.DesiredState}"))}"),
                async () =>
                {
                    List<string> lines = [];

                    // Started first, all three, then waited on: a Cloud Control request is a token
                    // the caller comes back for, and nothing makes the three wait on one another.
                    foreach (Resource resource in resources)
                    {
                        resource.Started = true;
                        CreateResourceResponse response;

                        try
                        {
                            response = await client.CreateResourceAsync(
                                new CreateResourceRequest { TypeName = resource.Type, DesiredState = resource.DesiredState }, ct).ConfigureAwait(false);
                        }
                        catch (AmazonServiceException ex) when (ex.StatusCode != 0)
                        {
                            // Refused with an answer: no request was accepted, so there is nothing to delete.
                            resource.Started = false;
                            throw;
                        }

                        resource.Token = response.ProgressEvent.RequestToken;
                        resource.FirstStatus = response.ProgressEvent.OperationStatus?.Value;
                    }

                    foreach (Resource resource in resources)
                    {
                        ProgressEvent done = await this.WaitForRequestAsync(client, resource.Token!, ct).ConfigureAwait(false);

                        if (done.OperationStatus?.Value != "SUCCESS")
                        {
                            throw new InvalidOperationException($"{resource.Type} reads {done.OperationStatus?.Value}: {done.ErrorCode} {done.StatusMessage}");
                        }

                        resource.Identifier = done.Identifier;
                        lines.Add($"{resource.Type}: {resource.FirstStatus} → {done.OperationStatus.Value}\n  {done.Identifier}");
                    }

                    return string.Join("\n", lines);
                }).ConfigureAwait(false);

            yield return created;

            // A failed create means every later step would look for resources that are not there.
            // Skipped rather than `yield break`, which would also skip yielding the cleanup step below.
            if (created.Succeeded)
            {
                yield return await RunStepAsync(
                    "GetResource — the resource tree",
                    this.Call("GetResource", $"client.GetResourceAsync(new GetResourceRequest {{ TypeName = \"...\", Identifier = \"...\" }}) — once per resource"),
                    async () =>
                    {
                        List<string> lines = ["resources created by this run"];

                        for (int i = 0; i < resources.Count; i++)
                        {
                            Resource resource = resources[i];
                            ResourceDescription description = (await client.GetResourceAsync(
                                new GetResourceRequest { TypeName = resource.Type, Identifier = resource.Identifier }, ct).ConfigureAwait(false)).ResourceDescription;

                            lines.Add($"{(i == resources.Count - 1 ? "└─" : "├─")} {resource.Type} {description.Identifier}\n{(i == resources.Count - 1 ? "   " : "│  ")} {description.Properties}");
                        }

                        return string.Join("\n", lines);
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "ListResources — what the account holds",
                    this.Call("ListResources", $"client.ListResourcesAsync(new ListResourcesRequest {{ TypeName = \"{BucketType}\" }})\nclient.ListResourcesAsync(new ListResourcesRequest {{ TypeName = \"{RoleType}\" }})\nclient.ListResourcesAsync(new ListResourcesRequest {{ TypeName = \"{QueueType}\" }})"),
                    async () =>
                    {
                        List<ResourceDescription> buckets = await ListAllAsync(client, BucketType, ct).ConfigureAwait(false);

                        if (!buckets.Any(b => b.Identifier == bucket.Identifier))
                        {
                            throw new InvalidOperationException($"ListResources lists {buckets.Count} bucket(s) but not {bucket.Identifier}, which was just created.");
                        }

                        List<string> lines =
                        [
                            $"{BucketType}: {buckets.Count} listed, this run's among them",
                            $"{RoleType}: {(await ListAllAsync(client, RoleType, ct).ConfigureAwait(false)).Count} listed",
                        ];

                        // Tripwire: floci lists buckets and roles, and refuses every other type, queues
                        // included. Real Cloud Control lists any type that supports it.
                        try
                        {
                            List<ResourceDescription> queues = await ListAllAsync(client, QueueType, ct).ConfigureAwait(false);

                            if (!queues.Any(q => q.Identifier == queue.Identifier))
                            {
                                throw new InvalidOperationException($"ListResources lists {queues.Count} queue(s) but not {queue.Identifier}, which was just created.");
                            }

                            lines.Add($"{QueueType}: {queues.Count} listed, this run's among them");
                        }
                        catch (UnsupportedActionException ex)
                        {
                            lines.Add($"{QueueType}: {ex.ErrorCode}: {ex.Message}\nreal Cloud Control lists queues; floci lists only the types it has built a lister for");
                        }

                        return string.Join("\n", lines);
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "UpdateResource, ListResourceRequests — what floci has not built",
                    this.Call("UpdateResource", $"client.UpdateResourceAsync(new UpdateResourceRequest {{ TypeName = \"{BucketType}\", Identifier = \"{bucket.Identifier}\", PatchDocument = ... }})\nclient.ListResourceRequestsAsync(new ListResourceRequestsRequest())\n\nPatchDocument:\n{TagPatch}"),
                    async () =>
                    {
                        string updated = await NotBuiltAsync("UpdateResource", async () =>
                        {
                            UpdateResourceResponse response = await client.UpdateResourceAsync(
                                new UpdateResourceRequest { TypeName = BucketType, Identifier = bucket.Identifier, PatchDocument = TagPatch }, ct).ConfigureAwait(false);
                            ProgressEvent done = await this.WaitForRequestAsync(client, response.ProgressEvent.RequestToken, ct).ConfigureAwait(false);

                            return $"UpdateResource: {done.OperationStatus?.Value}";
                        }).ConfigureAwait(false);

                        string listed = await NotBuiltAsync("ListResourceRequests", async () =>
                        {
                            ListResourceRequestsResponse response = await client.ListResourceRequestsAsync(new ListResourceRequestsRequest(), ct).ConfigureAwait(false);

                            return $"ListResourceRequests: {response.ResourceRequestStatusSummaries?.Count ?? 0} request(s)";
                        }).ConfigureAwait(false);

                        return $"{updated}\n{listed}";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "CreateResource — a bucket name that is taken",
                    this.Call("CreateResource", $"client.CreateResourceAsync(new CreateResourceRequest {{ TypeName = \"{BucketType}\", DesiredState = \"{{\\\"BucketName\\\":\\\"{bucketName}\\\"}}\" }})"),
                    async () =>
                    {
                        CreateResourceResponse response = await client.CreateResourceAsync(
                            new CreateResourceRequest { TypeName = BucketType, DesiredState = bucket.DesiredState }, ct).ConfigureAwait(false);
                        ProgressEvent done = await this.WaitForRequestAsync(client, response.ProgressEvent.RequestToken, ct).ConfigureAwait(false);

                        // Tripwire: real Cloud Control reports the request FAILED, with an error code.
                        // floci reports SUCCESS and creates nothing new.
                        return done.OperationStatus?.Value == "SUCCESS" && factory.UseEmulator
                            ? $"{done.OperationStatus.Value} for {done.Identifier}\nfloci accepts a bucket that exists: real Cloud Control reports FAILED here, with AlreadyExists"
                            : $"{done.OperationStatus?.Value}: {done.ErrorCode} {done.StatusMessage}".TrimEnd();
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "GetResourceRequestStatus, GetResource, CreateResource — things that are not there",
                    this.Call("GetResourceRequestStatus", "client.GetResourceRequestStatusAsync(new GetResourceRequestStatusRequest { RequestToken = \"flocilab-no-such-token\" })\nclient.GetResourceAsync(new GetResourceRequest { TypeName = \"AWS::S3::Bucket\", Identifier = \"flocilab-no-such-bucket\" })\nclient.CreateResourceAsync(new CreateResourceRequest { TypeName = \"AWS::S3::Bucket\", DesiredState = \"not json\" })"),
                    async () =>
                    {
                        List<string> lines =
                        [
                            await RefusalAsync("GetResourceRequestStatus", () => client.GetResourceRequestStatusAsync(new GetResourceRequestStatusRequest { RequestToken = "flocilab-no-such-token" }, ct)).ConfigureAwait(false),
                            await RefusalAsync("GetResource", () => client.GetResourceAsync(new GetResourceRequest { TypeName = BucketType, Identifier = "flocilab-no-such-bucket" }, ct)).ConfigureAwait(false),
                            await RefusalAsync("CreateResource", () => client.CreateResourceAsync(new CreateResourceRequest { TypeName = BucketType, DesiredState = "not json" }, ct)).ConfigureAwait(false),
                        ];

                        return string.Join("\n", lines);
                    }).ConfigureAwait(false);
            }
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // An iterator may not yield from a finally, so the step is yielded below.
            if (resources.Any(r => r.Started))
            {
                cleanup.Add(await this.DeleteEverythingAsync(client, resources, ct).ConfigureAwait(false));
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
    /// error code <c>UnsupportedOperation</c>, which is the not-implemented outcome here.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException service when IsNotBuilt(service):
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

    /// <summary>The answers that mean "not implemented": HTTP 501, or a 400 naming the operation as unknown.</summary>
    private static bool IsNotBuilt(AmazonServiceException ex)
        => ex.StatusCode == HttpStatusCode.NotImplemented || ex.ErrorCode is "UnknownAction" or "UnsupportedOperation";

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
        // stops the run at the step it reached, and RunAsync's finally still deletes everything.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>Every description of one type, following <c>NextToken</c> until the listing ends.</summary>
    private static async Task<List<ResourceDescription>> ListAllAsync(IAmazonCloudControlApi client, string typeName, CancellationToken ct)
    {
        List<ResourceDescription> all = [];
        string? token = null;

        do
        {
            ListResourcesResponse page = await client.ListResourcesAsync(new ListResourcesRequest { TypeName = typeName, NextToken = token }, ct).ConfigureAwait(false);

            all.AddRange(page.ResourceDescriptions ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    /// <summary>
    /// Runs an operation floci may not have built. A not-implemented answer (the same ones
    /// <see cref="Classify"/> recognises) is reported as such; anything else is a real failure and propagates.
    /// </summary>
    private static async Task<string> NotBuiltAsync(string operation, Func<Task<string>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (AmazonServiceException ex) when (IsNotBuilt(ex))
        {
            return $"{operation}: {ex.ErrorCode ?? $"HTTP {(int)ex.StatusCode}"}: {ex.Message}\nreal Cloud Control answers this; floci has not built it";
        }
    }

    /// <summary>A call that must be refused: the refusal's error code is the answer. Answering normally is the failure.</summary>
    private static async Task<string> RefusalAsync(string operation, Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (AmazonCloudControlApiException ex)
        {
            return $"{operation}: {ex.ErrorCode}: {ex.Message}";
        }

        throw new InvalidOperationException($"{operation} answered normally, where a refusal was expected.");
    }

    /// <summary>
    /// Reads the request until it is no longer pending. Against floci the request finishes within
    /// moments, so that polls for ten seconds at most; real Cloud Control spends seconds to minutes,
    /// so there it polls for up to ten minutes and returns whatever it last read.
    /// </summary>
    private async Task<ProgressEvent> WaitForRequestAsync(IAmazonCloudControlApi client, string token, CancellationToken ct)
    {
        TimeSpan limit = factory.UseEmulator ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(10);
        TimeSpan interval = factory.UseEmulator ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(5);
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            ProgressEvent progress = (await client.GetResourceRequestStatusAsync(new GetResourceRequestStatusRequest { RequestToken = token }, ct).ConfigureAwait(false)).ProgressEvent;

            if (progress.OperationStatus?.Value is "SUCCESS" or "FAILED" or "CANCEL_COMPLETE" || Stopwatch.GetElapsedTime(started) >= limit)
            {
                return progress;
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The wire-level request shown beside an SDK call: the AWS JSON 1.0 protocol, one POST / per operation named by X-Amz-Target.</summary>
    private string Call(string operation, string call) => $"POST {factory.ServiceUrl}/\nX-Amz-Target: CloudApiService.{operation}\nContent-Type: application/x-amz-json-1.0\n{call}";

    /// <summary>
    /// Deletes whatever this run created, newest first. If the create step never learned a
    /// resource's identifier, its request is waited on until settled: SUCCESS gives the identifier,
    /// FAILED means nothing exists. A queue or topic whose create response was lost before it gave
    /// a token cannot be found; that is reported, not hidden. Every delete is
    /// attempted before any failure is reported, so one stuck delete does not leak the rest. Uses
    /// <see cref="CancellationToken.None"/>: a cancelled run still has resources to delete.
    /// </summary>
    private async Task<DemoStep> DeleteEverythingAsync(IAmazonCloudControlApi client, List<Resource> resources, CancellationToken ct)
    {
        string request = this.Call("DeleteResource", "client.DeleteResourceAsync(new DeleteResourceRequest { TypeName = ..., Identifier = ... }) — each resource this run created\nclient.GetResourceAsync(...) — each must now be refused");

        return await RunStepAsync("Delete everything — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<string> removed = [];
            List<string> failures = [];

            foreach (Resource resource in Enumerable.Reverse(resources).Where(r => r.Started))
            {
                try
                {
                    string? identifier = resource.Identifier;

                    if (identifier is null && resource.Token is not null)
                    {
                        // The run stopped while this create was in flight: let it settle first, since
                        // the identifier arrives only on SUCCESS and a delete would race the create.
                        ProgressEvent create = await this.WaitForRequestAsync(client, resource.Token, none).ConfigureAwait(false);

                        if (create.OperationStatus?.Value is "FAILED" or "CANCEL_COMPLETE")
                        {
                            continue; // Nothing was created.
                        }

                        if (create.OperationStatus?.Value != "SUCCESS")
                        {
                            failures.Add($"{resource.Type}: its create still reads {create.OperationStatus?.Value} after waiting, so it was not deleted.");
                            continue;
                        }

                        identifier = create.Identifier;
                    }

                    // Only a create whose response was lost has no token; a bucket is named by what was asked for.
                    identifier ??= resource.Token is null ? resource.Fallback : null;

                    if (identifier is null)
                    {
                        failures.Add($"{resource.Type}: {(resource.Token is null ? "its create never returned a token" : "its create reads SUCCESS without an identifier")}, so there is nothing to delete by.");
                        continue;
                    }

                    DeleteResourceResponse response = await client.DeleteResourceAsync(new DeleteResourceRequest { TypeName = resource.Type, Identifier = identifier }, none).ConfigureAwait(false);
                    ProgressEvent done = await this.WaitForRequestAsync(client, response.ProgressEvent.RequestToken, none).ConfigureAwait(false);

                    if (done.OperationStatus?.Value != "SUCCESS")
                    {
                        failures.Add($"{resource.Type} {identifier}: delete reads {done.OperationStatus?.Value}: {done.ErrorCode} {done.StatusMessage}");
                        continue;
                    }

                    try
                    {
                        await client.GetResourceAsync(new GetResourceRequest { TypeName = resource.Type, Identifier = identifier }, none).ConfigureAwait(false);
                        failures.Add($"{resource.Type} {identifier}: delete reads SUCCESS, but GetResource still returns it.");
                    }
                    catch (ResourceNotFoundException)
                    {
                        removed.Add($"{resource.Type} {identifier}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{resource.Type}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (failures.Count != 0)
            {
                throw new InvalidOperationException($"{failures.Count} resource(s) not deleted:\n{string.Join("\n", failures)}{cancelled}");
            }

            return removed.Count == 0
                ? $"Nothing this run created exists — nothing to remove.{cancelled}"
                : $"Deleted {removed.Count} resource(s), each now refused by GetResource:\n{string.Join("\n", removed)}{cancelled}";
        }).ConfigureAwait(false);
    }

    /// <summary>One resource this run creates: what to ask for, and what is known about it so far.</summary>
    private sealed class Resource(string type, string desiredState)
    {
        public string Type { get; } = type;

        public string DesiredState { get; } = desiredState;

        /// <summary>A create call has been started, whether or not its answer arrived.</summary>
        public bool Started { get; set; }

        public string? Token { get; set; }

        public string? FirstStatus { get; set; }

        public string? Identifier { get; set; }

        /// <summary>What to delete by when no identifier was learned: only a bucket is named by the name it was asked for.</summary>
        public string? Fallback { get; init; }
    }
}
