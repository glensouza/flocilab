using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Oci.Common.Model;
using Oci.QueueService;
using Oci.QueueService.Models;
using Oci.QueueService.Requests;
using Oci.QueueService.Responses;

namespace FlociLab.Oci.Queue;

/// <summary>
/// The OCI column of the queue comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the thinnest
/// possible mapping onto OCI.DotNetSDK.Queue: the comparison is only worth anything if each column
/// is the provider's own SDK doing the provider's own thing.
///
/// <para>
/// The interface addresses a queue by name — every other provider's queue capability does the
/// same — so every method here resolves the OCID <c>QueueAdminClient</c> actually wants via
/// <see cref="ResolveQueueAsync"/> first, the same shape <c>SqsQueue</c> uses to turn an SQS name
/// into a queue URL.
/// </para>
/// </summary>
public sealed class OciQueue(QueueClientFactory factory) : IQueueCapability
{
    public string Provider => CloudProvider.Oci;

    public string ServiceName => "OCI Queue";

    public string? SourcePath => "samples/oci/queue/FlociLab.Oci.Queue.Demo/OciQueue.cs";

    // The same classifier QueueDemo uses for its probe, so the coverage matrix and the comparison
    // page can never disagree about whether an operation is unimplemented, unreachable or
    // genuinely broken. TimeSpan.Zero because only the status is wanted here — the comparison page
    // times the call itself.
    public ProbeStatus Classify(Exception ex) => QueueDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<QueueInfo>> ListQueuesAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciQueue.ListQueuesAsync");
        QueueAdminClient admin = factory.CreateAdmin();

        List<QueueInfo> queues = [];
        string? page = null;

        // ListQueues pages with opc-next-page, and the comparison page showing a truncated list
        // would be worse than it showing none. A persistent-mode emulator accumulates queues
        // across runs, so this is reachable in the lab and not only against a real tenancy.
        do
        {
            ListQueuesResponse response = await admin.ListQueues(
                new ListQueuesRequest { CompartmentId = factory.CompartmentId, Page = page }, cancellationToken: ct).ConfigureAwait(false);

            queues.AddRange(response.QueueCollection.Items.Select(q => new QueueInfo(q.DisplayName)));
            page = response.OpcNextPage;
        }
        while (!string.IsNullOrEmpty(page));

        return queues;
    }

    public async Task CreateQueueAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciQueue.CreateQueueAsync");
        QueueAdminClient admin = factory.CreateAdmin();
        CreateQueueResponse response = await admin.CreateQueue(
            new CreateQueueRequest
            {
                CreateQueueDetails = new CreateQueueDetails { DisplayName = name, CompartmentId = factory.CompartmentId },
            },
            cancellationToken: ct).ConfigureAwait(false);

        // CreateQueue is asynchronous even against the emulator (QueueDemo's lede) — the caller's
        // next ListQueuesAsync would otherwise race the work request and could still miss it.
        await AwaitWorkRequestAsync(admin, response.OpcWorkRequestId, "CreateQueue").ConfigureAwait(false);
    }

    public async Task SendMessageAsync(string queue, string body, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciQueue.SendMessageAsync");
        QueueAdminClient admin = factory.CreateAdmin();
        QueueSummary resolved = await ResolveQueueAsync(admin, factory.CompartmentId, queue, ct).ConfigureAwait(false);

        // A fresh data-plane client per call: the comparison page invokes this capability once per
        // provider per click rather than in a tight loop, so the connection-pool cost QueueDemo
        // avoids by caching within one run does not apply here.
        using QueueClient client = factory.CreateData(resolved.MessagesEndpoint);
        await client.PutMessages(
            new PutMessagesRequest
            {
                QueueId = resolved.Id,
                PutMessagesDetails = new PutMessagesDetails { Messages = [new PutMessagesDetailsEntry { Content = body }] },
            },
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes/acks every message it returns, per the interface contract — OCI Queue makes that a
    /// second call, since GetMessages only hides messages behind a visibility timeout rather than
    /// removing them. Only the successfully acked messages come back: one whose delete failed
    /// reappears when its visibility timeout expires, so returning it would hand the caller a
    /// message that is still on the queue.
    /// </summary>
    public async Task<IReadOnlyList<QueueMessage>> ReceiveMessagesAsync(string queue, int maxMessages, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciQueue.ReceiveMessagesAsync");
        QueueAdminClient admin = factory.CreateAdmin();
        QueueSummary resolved = await ResolveQueueAsync(admin, factory.CompartmentId, queue, ct).ConfigureAwait(false);

        using QueueClient client = factory.CreateData(resolved.MessagesEndpoint);
        GetMessagesResponse response = await client.GetMessages(
            // Clamped, like every other provider's column: the comparison page hands the same
            // batch size to all four, and real OCI Queue's limit is 1..32 — outside that it is a
            // 400. floci-oci 0.3.0 answers 200 to limit=0 and limit=100 alike (probed 2026-09-02),
            // so no test here can catch it and the clamp has to be written rather than discovered.
            new GetMessagesRequest { QueueId = resolved.Id, VisibilityInSeconds = 30, TimeoutInSeconds = 2, Limit = Math.Clamp(maxMessages, 1, 32) },
            cancellationToken: ct).ConfigureAwait(false);
        List<GetMessage> received = response.GetMessages.Messages;

        List<QueueMessage> acked = [];

        foreach (GetMessage message in received)
        {
            try
            {
                await client.DeleteMessage(
                    new DeleteMessageRequest { QueueId = resolved.Id, MessageReceipt = message.Receipt }, cancellationToken: ct).ConfigureAwait(false);
            }
            // Per-message, so one failed ack costs only its own message. Letting this escape would
            // throw away the messages already acked above — they are gone from the queue and would
            // never reach the caller, which is the one data-loss shape this method can produce.
            // The unacked message is simply left out: it reappears when its visibility expires.
            catch (OciException)
            {
                continue;
            }

            acked.Add(new QueueMessage(message.Id?.ToString() ?? "", message.Content));
        }

        return acked;
    }

    public async Task DeleteQueueAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciQueue.DeleteQueueAsync");
        QueueAdminClient admin = factory.CreateAdmin();
        QueueSummary resolved = await ResolveQueueAsync(admin, factory.CompartmentId, name, ct).ConfigureAwait(false);

        DeleteQueueResponse response = await admin.DeleteQueue(
            new DeleteQueueRequest { QueueId = resolved.Id }, cancellationToken: ct).ConfigureAwait(false);

        await AwaitWorkRequestAsync(admin, response.OpcWorkRequestId, "DeleteQueue").ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for one work request and <em>checks how it ended</em>. Waiting for
    /// <c>[Succeeded, Failed]</c> and ignoring which one arrived is the plan §14 failure the whole
    /// repo keeps re-finding: the comparison page would paint the OCI column green over a create
    /// that never created anything, and the caller's next call would fail somewhere unrelated.
    /// <see cref="InvalidOperationException"/> rather than a timeout type, because
    /// <see cref="QueueDemo.Classify"/> maps that to <c>Error</c> — an emulator that answered is
    /// misbehaving, not unreachable.
    /// </summary>
    private static async Task AwaitWorkRequestAsync(QueueAdminClient admin, string workRequestId, string operation)
    {
        GetWorkRequestResponse finished = await admin.Waiters
            .ForWorkRequest(new GetWorkRequestRequest { WorkRequestId = workRequestId }, [OperationStatus.Succeeded, OperationStatus.Failed])
            .ExecuteAsync().ConfigureAwait(false);

        if (finished.WorkRequest.Status != OperationStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"{operation} work request {workRequestId} finished as {finished.WorkRequest.Status}, not Succeeded.");
        }
    }

    private static async Task<QueueSummary> ResolveQueueAsync(QueueAdminClient admin, string compartmentId, string name, CancellationToken ct)
    {
        ListQueuesResponse response = await admin.ListQueues(
            new ListQueuesRequest { CompartmentId = compartmentId, DisplayName = name }, cancellationToken: ct).ConfigureAwait(false);

        // The DisplayName filter is re-checked rather than trusted. floci-oci 0.3.0 does honour it
        // (probed 2026-09-02), but it ignores ListObjects' `fields` the same way (plan §14), and
        // an emulator that ignored this one would hand back an arbitrary queue — which
        // DeleteQueueAsync would then delete.
        return response.QueueCollection.Items.FirstOrDefault(q => q.DisplayName == name)
            ?? throw new InvalidOperationException($"No queue named \"{name}\" in compartment {compartmentId}.");
    }
}
