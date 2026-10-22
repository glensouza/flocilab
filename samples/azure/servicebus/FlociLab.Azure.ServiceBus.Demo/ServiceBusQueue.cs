using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using FlociLab.Core;
using FlociLab.Core.Capabilities;

namespace FlociLab.Azure.ServiceBus;

/// <summary>
/// The Service Bus column of the queue comparison page (docs/BLAZOR-PLAN.md §8), alongside Queue
/// Storage's — the plan's comparison table lists both under Azure's slot, keyed by capability
/// instance rather than provider so neither overwrites the other (see
/// <c>ObjectStoragePage.razor</c>'s <c>results</c> dictionary for why that is safe).
/// </summary>
public sealed class ServiceBusQueue(ServiceBusClientFactory factory) : IQueueCapability
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    public string Provider => CloudProvider.Azure;

    public string ServiceName => "Azure Service Bus";

    public string? SourcePath => "samples/azure/servicebus/FlociLab.Azure.ServiceBus.Demo/ServiceBusQueue.cs";

    // The same classifier ServiceBusDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented, unreachable
    // or genuinely broken.
    public ProbeStatus Classify(Exception ex) => ServiceBusDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<QueueInfo>> ListQueuesAsync(CancellationToken ct)
    {
        CallTrace.Request("ServiceBusQueue.ListQueuesAsync");
        ServiceBusAdministrationClient admin = factory.CreateAdministrationClient();
        List<QueueInfo> queues = [];

        await foreach (QueueProperties queue in admin.GetQueuesAsync(ct).ConfigureAwait(false))
        {
            queues.Add(new QueueInfo(queue.Name));
        }

        return queues;
    }

    public async Task CreateQueueAsync(string name, CancellationToken ct)
    {
        CallTrace.Request("ServiceBusQueue.CreateQueueAsync");
        ServiceBusAdministrationClient admin = factory.CreateAdministrationClient();
        await admin.CreateQueueAsync(name, ct).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(string queue, string body, CancellationToken ct)
    {
        CallTrace.Request("ServiceBusQueue.SendMessageAsync");
        await using ServiceBusClient client = factory.CreateClient();
        await using ServiceBusSender sender = client.CreateSender(queue);

        await sender.SendMessageAsync(new ServiceBusMessage(body), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes/acks every message it returns, per the interface contract — Service Bus makes that
    /// a second call, exactly like Queue Storage: peek-lock receive only hides a message behind a
    /// lock rather than removing it. Only the successfully completed messages come back; one whose
    /// complete failed stays on the queue until its lock expires.
    /// </summary>
    public async Task<IReadOnlyList<QueueMessage>> ReceiveMessagesAsync(string queue, int maxMessages, CancellationToken ct)
    {
        CallTrace.Request("ServiceBusQueue.ReceiveMessagesAsync");
        // A guard, not a clamp on the low side: this method completes what it returns, so clamping
        // 0 up to 1 would dequeue and permanently remove a message the caller never asked for and
        // never gets back.
        if (maxMessages <= 0)
        {
            return [];
        }

        await using ServiceBusClient client = factory.CreateClient();
        await using ServiceBusReceiver receiver = client.CreateReceiver(queue);

        IReadOnlyList<ServiceBusReceivedMessage> received = await receiver.ReceiveMessagesAsync(
            maxMessages, MaxWait, ct).ConfigureAwait(false);

        if (received.Count == 0)
        {
            return [];
        }

        List<QueueMessage> acked = [];

        foreach (ServiceBusReceivedMessage message in received)
        {
            try
            {
                await receiver.CompleteMessageAsync(message, ct).ConfigureAwait(false);
                acked.Add(new QueueMessage(message.MessageId, message.Body.ToString()));
            }
            catch (ServiceBusException)
            {
                // Complete failed; the message stays locked until it expires and becomes visible
                // again, so it must not be reported as acked here.
            }
        }

        return acked;
    }

    /// <summary>
    /// Threw a 501 on every call through floci-az 0.12.0, which misrouted the bare queue-name DELETE
    /// to its Blob handler (see <see cref="ServiceBusDemo"/>'s remarks); routed since 0.13.0.
    /// </summary>
    public async Task DeleteQueueAsync(string name, CancellationToken ct)
    {
        CallTrace.Request("ServiceBusQueue.DeleteQueueAsync");
        ServiceBusAdministrationClient admin = factory.CreateAdministrationClient();
        await admin.DeleteQueueAsync(name, ct).ConfigureAwait(false);
    }
}
