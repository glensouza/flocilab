using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using FlociLab.Core;
using Oci.Common.Model;
using Oci.QueueService;
using Oci.QueueService.Models;
using Oci.QueueService.Requests;
using Oci.QueueService.Responses;

namespace FlociLab.Oci.Queue;

/// <summary>
/// OCI Queue against floci-oci. Ordinary OCI.DotNetSDK.Queue code — the only emulator-aware lines
/// in the sample are in <see cref="QueueClientFactory"/>.
///
/// <para>
/// Unlike the other three queue samples, this one is genuinely two APIs wearing one name:
/// <see cref="QueueAdminClient"/> is the control plane (create/list/delete the queue itself) and
/// <see cref="QueueClient"/> is the data plane (put/get/delete messages), addressed at the
/// per-queue <c>MessagesEndpoint</c> a real tenancy returns from <c>GetQueue</c>. floci-oci builds
/// that endpoint from its own configuration rather than from the address the caller reached, so it
/// is only correct by coincidence — see <see cref="QueueClientFactory.CreateData"/> for what it
/// actually answers and why this sample overrides it.
/// </para>
///
/// <para>
/// <c>CreateQueue</c> and <c>DeleteQueue</c> are asynchronous even against the emulator: both
/// answer <c>202 Accepted</c> with an <c>opc-work-request-id</c>, exactly like real OCI, and the
/// queue's own lifecycle state only becomes final once that work request finishes. This sample
/// polls it with <c>QueueAdminClient.Waiters.ForWorkRequest</c> rather than trusting the 202 —
/// the same shape production code needs, and floci-oci 0.3.0 resolves it on the first poll.
/// </para>
/// </summary>
public sealed class QueueDemo(QueueClientFactory factory) : IServiceDemo
{
    private const string MessageBody = "Hello from FlociLab.";

    public string Provider => CloudProvider.Oci;

    public string Slug => "queue";

    public string DisplayName => "Queue";

    public string Category => "Messaging";

    public string Route => "/oci/queue";

    /// <summary>ListQueues — one request, no state, and the cheapest call the service answers.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            QueueAdminClient admin = factory.CreateAdmin();
            ListQueuesResponse response = await admin.ListQueues(
                new ListQueuesRequest { CompartmentId = factory.CompartmentId }, cancellationToken: ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListQueues returned {response.QueueCollection.Items.Count} queue(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Building the admin client can itself fail: the real-cloud branch of the factory refuses
        // a run that would create a queue in the lab's synthetic compartment. That has to become a
        // failed step like any other — an iterator that throws on the first MoveNextAsync takes
        // down the circuit instead of rendering the reason. Caught here and yielded below, because
        // C# forbids a yield inside a try that has a catch.
        QueueAdminClient? constructed = null;
        Exception? clientFailure = null;

        try
        {
            constructed = factory.CreateAdmin();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clientFailure = ex;
        }

        if (constructed is null)
        {
            yield return DemoStep.Failed(
                "QueueAdminClient",
                clientFailure!,
                "new QueueAdminClient(endpoints.AuthenticationProvider())");

            yield break;
        }

        QueueAdminClient admin = constructed;

        // Asked of the client rather than taken from the factory — see ObjectStorageDemo.RunAsync
        // for why: in emulator mode ForFloci has set both the endpoint and the realm template to
        // the emulator, so this is the emulator; in real-cloud mode this is whatever the SDK
        // resolved from the region.
        string origin = admin.GetEndpoint().ToString().TrimEnd('/');

        // Unique per run, so two runs never collide and a leftover queue from a crashed run never
        // makes the next one fail.
        string queueName = $"flocilab-queue-{Guid.NewGuid():N}";
        bool created = false;
        string? queueId = null;
        QueueClient? data = null;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListQueues — before",
                $"GET {origin}/20210201/queues?compartmentId={factory.CompartmentId}\nqueueAdmin.ListQueues(new ListQueuesRequest {{ CompartmentId }})",
                async () =>
                {
                    // OCI.DotNetSDK.Queue 145.0.0 (not re-measured on 148.1.0) does not reliably
                    // check an already-cancelled token before it starts a request — measured
                    // against floci-oci: the same
                    // pre-cancelled token let anywhere from zero to all six steps of this run
                    // complete before a cancellation happened to land. Checking explicitly here
                    // makes cancellation land where the caller asked for it — before the next
                    // step starts — rather than at whatever point the SDK's own plumbing notices.
                    ct.ThrowIfCancellationRequested();

                    ListQueuesResponse response = await admin.ListQueues(
                        new ListQueuesRequest { CompartmentId = factory.CompartmentId }, cancellationToken: ct).ConfigureAwait(false);
                    IEnumerable<string> names = response.QueueCollection.Items.Select(q => $"  {q.DisplayName} ({q.Id})");

                    return $"{response.QueueCollection.Items.Count} queue(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            string? messagesEndpoint = null;

            yield return await RunStepAsync(
                "CreateQueue",
                $"POST {origin}/20210201/queues\nContent-Type: application/json\n\n{{ \"displayName\": \"{queueName}\", \"compartmentId\": \"{factory.CompartmentId}\" }}",
                async () =>
                {
                    // See the ThrowIfCancellationRequested comment on the ListQueues step above.
                    ct.ThrowIfCancellationRequested();

                    // Set before the call, not after: if the POST lands but the response does not
                    // come back, the queue exists and cleanup has to know about it. Cleanup treats
                    // an absent queue as a no-op, so claiming it early is free.
                    created = true;
                    CreateQueueResponse createResponse = await admin.CreateQueue(
                        new CreateQueueRequest
                        {
                            CreateQueueDetails = new CreateQueueDetails { DisplayName = queueName, CompartmentId = factory.CompartmentId },
                        },
                        cancellationToken: ct).ConfigureAwait(false);

                    GetWorkRequestResponse finished = await admin.Waiters
                        .ForWorkRequest(
                            new GetWorkRequestRequest { WorkRequestId = createResponse.OpcWorkRequestId },
                            [OperationStatus.Succeeded, OperationStatus.Failed])
                        .ExecuteAsync().ConfigureAwait(false);

                    if (finished.WorkRequest.Status != OperationStatus.Succeeded)
                    {
                        throw new InvalidOperationException(
                            $"opc-work-request-id {createResponse.OpcWorkRequestId} finished as {finished.WorkRequest.Status}, not Succeeded.");
                    }

                    WorkRequestResource resource = finished.WorkRequest.Resources.Single(r => r.EntityType == "QUEUE");
                    queueId = resource.Identifier;

                    GetQueueResponse queueResponse = await admin.GetQueue(
                        new GetQueueRequest { QueueId = queueId }, cancellationToken: ct).ConfigureAwait(false);
                    messagesEndpoint = queueResponse.Queue.MessagesEndpoint;

                    return $"opc-work-request-id: {createResponse.OpcWorkRequestId}\n"
                        + $"work request status:  {finished.WorkRequest.Status}\n\n"
                        + $"Queue {queueResponse.Queue.Id}\n"
                        + $"  lifecycleState:   {queueResponse.Queue.LifecycleState}\n"
                        + $"  messagesEndpoint: {messagesEndpoint} (reported by floci-oci from its own config, not from this request — see PutMessages below)";
                }).ConfigureAwait(false);

            // CreateQueue failed, so there is nothing to address. Stop rather than emitting three
            // more steps whose only news is a null queue id.
            if (queueId is null || messagesEndpoint is null)
            {
                yield break;
            }

            // Built once and reused for the rest of the run — a fresh QueueClient per message
            // operation would be a fresh connection pool per operation (plan §14).
            //
            // Guarded exactly like CreateAdmin above, and for the same reason: a throw in the
            // iterator body escapes RunAsync, and the page catches only OperationCanceledException,
            // so it would take the circuit down instead of rendering the reason. Real-cloud mode is
            // where this bites — that branch builds a ConfigFileAuthenticationDetailsProvider and
            // hands GetQueue's own messagesEndpoint to the QueueClient constructor, so a bad
            // profile or a malformed endpoint throws here rather than at a call site.
            string? dataOrigin = null;
            Exception? dataFailure = null;

            try
            {
                data = factory.CreateData(messagesEndpoint);
                dataOrigin = data.GetEndpoint().ToString().TrimEnd('/');
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                dataFailure = ex;
            }

            // Re-bound to a non-nullable local: `data` stays nullable so the finally block below
            // can dispose-if-built, but the steps below capture this one, so the compiler can see
            // every call on it is on a client that definitely exists.
            if (data is null || dataOrigin is null)
            {
                yield return DemoStep.Failed(
                    "QueueClient",
                    dataFailure!,
                    $"new QueueClient(auth, endpoint: \"{messagesEndpoint}\")");

                yield break;
            }

            QueueClient client = data;
            string? messageReceipt = null;

            yield return await RunStepAsync(
                "PutMessages",
                $"POST {dataOrigin}/20210201/queues/{queueId}/messages\nContent-Type: application/json\n\n{{ \"messages\": [ {{ \"content\": \"{MessageBody}\" }} ] }}",
                async () =>
                {
                    // See the ThrowIfCancellationRequested comment on the ListQueues step above.
                    ct.ThrowIfCancellationRequested();

                    PutMessagesResponse response = await client.PutMessages(
                        new PutMessagesRequest
                        {
                            QueueId = queueId,
                            PutMessagesDetails = new PutMessagesDetails { Messages = [new PutMessagesDetailsEntry { Content = MessageBody }] },
                        },
                        cancellationToken: ct).ConfigureAwait(false);
                    PutMessage sent = response.PutMessages.Messages.Single();

                    return $"id: {sent.Id}\nexpireAfter: {sent.ExpireAfter:O}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "GetMessages",
                $"GET {dataOrigin}/20210201/queues/{queueId}/messages?visibilityInSeconds=30&timeoutInSeconds=2&limit=1\nqueue.GetMessages(new GetMessagesRequest {{ QueueId, VisibilityInSeconds = 30, TimeoutInSeconds = 2, Limit = 1 }})",
                async () =>
                {
                    // See the ThrowIfCancellationRequested comment on the ListQueues step above.
                    ct.ThrowIfCancellationRequested();

                    GetMessagesResponse response = await client.GetMessages(
                        new GetMessagesRequest { QueueId = queueId, VisibilityInSeconds = 30, TimeoutInSeconds = 2, Limit = 1 },
                        cancellationToken: ct).ConfigureAwait(false);
                    GetMessage? message = response.GetMessages.Messages.FirstOrDefault();
                    messageReceipt = message?.Receipt;

                    // A round-trip that received nothing did not round-trip — the same rule
                    // SqsDemo applies. Six green steps for a run that delivered no message would
                    // be the page lying about what floci-oci actually did.
                    if (message is null)
                    {
                        throw new InvalidOperationException(
                            "0 message(s); the message sent above did not arrive within the poll.");
                    }

                    return $"id: {message.Id}\nContent: {message.Content}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteMessage",
                $"DELETE {dataOrigin}/20210201/queues/{queueId}/messages/{messageReceipt}\nqueue.DeleteMessage(new DeleteMessageRequest {{ QueueId, MessageReceipt }})",
                async () =>
                {
                    // See the ThrowIfCancellationRequested comment on the ListQueues step above.
                    ct.ThrowIfCancellationRequested();

                    if (messageReceipt is null)
                    {
                        throw new InvalidOperationException("Skipped — GetMessages returned no message to delete.");
                    }

                    await client.DeleteMessage(
                        new DeleteMessageRequest { QueueId = queueId, MessageReceipt = messageReceipt }, cancellationToken: ct).ConfigureAwait(false);

                    return "204 No Content — the message is gone.";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean compartment. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await DeleteQueueAsync(admin, origin, factory.CompartmentId, queueName, queueId, ct).ConfigureAwait(false) : null;
            data?.Dispose();
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> handles the transport cases but cannot see a status
    /// code hiding inside an <see cref="OciException"/>, which is where this SDK puts every answer
    /// the server gave. Same shape as <c>ObjectStorageDemo.Classify</c>: both clients throw the
    /// same exception type.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OciException { StatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case OciException:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real OCI would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the queue.
        // Catching it here would instead fabricate a "Failed" step for every remaining operation,
        // reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static string Describe(Exception ex)
    {
        string message = ex is OciException oci
            ? $"{(int)oci.StatusCode} {oci.ServiceCode}: {FirstLine(oci.Message)}"
            : ex.Message;

        return ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? message
            : $"{message} ({FirstLine(ex.InnerException.Message)})";
    }

    private static string FirstLine(string message)
        => message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? message;

    /// <summary>
    /// Cleanup. Resolves the queue by name rather than reusing <paramref name="queueId"/> in case
    /// CreateQueue's own response never made it back (a dropped connection after the request
    /// landed leaves the queue created server-side, and the name is all cleanup has). DeleteQueue
    /// is asynchronous too, so this waits on its work request the same way CreateQueue's step
    /// does — otherwise a re-run's ListQueues could still see the queue this run is deleting. The
    /// calls use <see cref="CancellationToken.None"/> — a run that was cancelled still has a queue
    /// to remove.
    /// </summary>
    private static async Task<DemoStep> DeleteQueueAsync(QueueAdminClient admin, string origin, string compartmentId, string queueName, string? queueId, CancellationToken ct)
    {
        string request = $"DELETE {origin}/20210201/queues/{{id}}\nqueueAdmin.DeleteQueue(new DeleteQueueRequest {{ QueueId }})";

        return await RunStepAsync("DeleteQueue — cleanup", request, async () =>
        {
            string? resolvedId = queueId;

            // CreateQueue claims the name before it calls, so the queue may never have been made —
            // that is a clean run to finish, not a cleanup failure worth showing in red.
            if (resolvedId is null)
            {
                ListQueuesResponse lookup = await admin.ListQueues(
                    new ListQueuesRequest { CompartmentId = compartmentId, DisplayName = queueName },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                // The DisplayName filter is re-checked, not trusted — see OciQueue.ResolveQueueAsync.
                // Here it matters more: an emulator that ignored it would make cleanup delete
                // somebody else's queue.
                resolvedId = lookup.QueueCollection.Items.FirstOrDefault(q => q.DisplayName == queueName)?.Id;

                if (resolvedId is null)
                {
                    return "Nothing to remove — the queue was never created.";
                }
            }

            DeleteQueueResponse deleteResponse = await admin.DeleteQueue(
                new DeleteQueueRequest { QueueId = resolvedId }, cancellationToken: CancellationToken.None).ConfigureAwait(false);

            GetWorkRequestResponse finished = await admin.Waiters
                .ForWorkRequest(
                    new GetWorkRequestRequest { WorkRequestId = deleteResponse.OpcWorkRequestId },
                    [OperationStatus.Succeeded, OperationStatus.Failed])
                .ExecuteAsync().ConfigureAwait(false);

            return $"opc-work-request-id: {deleteResponse.OpcWorkRequestId} — {finished.WorkRequest.Status}"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }
}
