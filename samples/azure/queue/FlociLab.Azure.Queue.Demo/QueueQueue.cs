using Azure;
using Azure.Storage.Queues;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using QueueItem = Azure.Storage.Queues.Models.QueueItem;
using SdkQueueMessage = Azure.Storage.Queues.Models.QueueMessage;

namespace FlociLab.Azure.Queue;

/// <summary>
/// The Queue Storage column of the queue comparison page (docs/BLAZOR-PLAN.md §8). Deliberately
/// the thinnest possible mapping onto Azure.Storage.Queues: the comparison is only worth anything
/// if each column is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class QueueQueue(QueueClientFactory factory) : IQueueCapability
{
    public string Provider => CloudProvider.Azure;

    public string ServiceName => "Azure Queue Storage";

    public string? SourcePath => "samples/azure/queue/FlociLab.Azure.Queue.Demo/QueueQueue.cs";

    // The same classifier QueueDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => QueueDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<QueueInfo>> ListQueuesAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nQueueQueue.ListQueuesAsync");
        QueueServiceClient client = factory.Create();
        List<QueueInfo> queues = [];

        await foreach (QueueItem item in client.GetQueuesAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            queues.Add(new QueueInfo(item.Name));
        }

        return queues;
    }

    public async Task CreateQueueAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nQueueQueue.CreateQueueAsync");
        QueueServiceClient client = factory.Create();
        await client.CreateQueueAsync(name, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(string queue, string body, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nQueueQueue.SendMessageAsync");
        QueueServiceClient client = factory.Create();
        QueueClient queueClient = client.GetQueueClient(queue);

        await queueClient.SendMessageAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes/acks every message it returns, per the interface contract — Queue Storage makes
    /// that a second call, since ReceiveMessages only hides messages behind a visibility timeout
    /// rather than removing them. Only the successfully acked messages come back: one whose delete
    /// failed reappears when its visibility timeout expires, so returning it would hand the caller
    /// a message that is still on the queue.
    /// </summary>
    public async Task<IReadOnlyList<QueueMessage>> ReceiveMessagesAsync(string queue, int maxMessages, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nQueueQueue.ReceiveMessagesAsync");
        // A guard, not a clamp on the low side: this method deletes what it returns, so clamping 0
        // up to 1 would dequeue and permanently ack a message the caller never asked for and never
        // gets back.
        if (maxMessages <= 0)
        {
            return [];
        }

        QueueServiceClient client = factory.Create();
        QueueClient queueClient = client.GetQueueClient(queue);

        // Queue Storage caps a batch at 32 messages, where SQS caps at 10 — clamping the high side
        // keeps the interface's "up to maxMessages" honest instead of throwing on a larger
        // comparison batch that SQS itself would already have rejected.
        Response<SdkQueueMessage[]> response = await queueClient.ReceiveMessagesAsync(
            Math.Min(maxMessages, 32), cancellationToken: ct).ConfigureAwait(false);
        SdkQueueMessage[] received = response.Value;

        if (received.Length == 0)
        {
            return [];
        }

        List<QueueMessage> acked = [];

        foreach (SdkQueueMessage message in received)
        {
            try
            {
                await queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct).ConfigureAwait(false);
                acked.Add(new QueueMessage(message.MessageId, message.MessageText));
            }
            catch (RequestFailedException)
            {
                // Delete failed; the message stays on the queue until its visibility timeout
                // expires, so it must not be reported as acked here.
            }
        }

        return acked;
    }

    public async Task DeleteQueueAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nQueueQueue.DeleteQueueAsync");
        QueueServiceClient client = factory.Create();
        QueueClient queueClient = client.GetQueueClient(name);

        await queueClient.DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
    }
}
