using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Google.Api.Gax.ResourceNames;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;

namespace FlociLab.Gcp.PubSub;

/// <summary>
/// The Pub/Sub column of the queue comparison page (docs/BLAZOR-PLAN.md §8). Pub/Sub has no single
/// "queue" resource — a topic accepts publishes and a subscription is what a reader pulls from —
/// so <see cref="IQueueCapability"/>'s one name is treated as both: <see cref="CreateQueueAsync"/>
/// makes a topic and a pull subscription bound to it, sharing the name, and every other method
/// resolves that same name back into a <see cref="TopicName"/> or <see cref="SubscriptionName"/>
/// depending on which side of the pipe it talks to.
/// </summary>
public sealed class PubSubQueue(PubSubClientFactory factory) : IQueueCapability
{
    public string Provider => CloudProvider.Gcp;

    public string ServiceName => "Google Cloud Pub/Sub";

    public string? SourcePath => "samples/gcp/pubsub/FlociLab.Gcp.PubSub.Demo/PubSubQueue.cs";

    // The same classifier PubSubDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => PubSubDemo.Classify(ex, TimeSpan.Zero).Status;

    /// <summary>
    /// Lists subscriptions rather than topics: a subscription is the resource a reader pulls
    /// from, which is what the rest of this interface's operations act on.
    /// </summary>
    public async Task<IReadOnlyList<QueueInfo>> ListQueuesAsync(CancellationToken ct)
    {
        CallTrace.Request("PubSubQueue.ListQueuesAsync");
        SubscriberServiceApiClient subscriber = factory.Subscriber();
        List<QueueInfo> queues = [];

        await foreach (Subscription subscription in subscriber
            .ListSubscriptionsAsync(ProjectName.FromProject(factory.ProjectId))
            .WithCancellation(ct).ConfigureAwait(false))
        {
            queues.Add(new QueueInfo(SubscriptionName.Parse(subscription.Name).SubscriptionId));
        }

        return queues;
    }

    public async Task CreateQueueAsync(string name, CancellationToken ct)
    {
        CallTrace.Request("PubSubQueue.CreateQueueAsync");
        PublisherServiceApiClient publisher = factory.Publisher();
        SubscriberServiceApiClient subscriber = factory.Subscriber();
        TopicName topicName = TopicName.FromProjectTopic(factory.ProjectId, name);

        await publisher.CreateTopicAsync(topicName, ct).ConfigureAwait(false);
        await subscriber.CreateSubscriptionAsync(
            SubscriptionName.FromProjectSubscription(factory.ProjectId, name), topicName, pushConfig: null, ackDeadlineSeconds: 10, ct)
            .ConfigureAwait(false);
    }

    public async Task SendMessageAsync(string queue, string body, CancellationToken ct)
    {
        CallTrace.Request("PubSubQueue.SendMessageAsync");
        PublisherServiceApiClient publisher = factory.Publisher();
        TopicName topicName = TopicName.FromProjectTopic(factory.ProjectId, queue);

        await publisher.PublishAsync(topicName, [new PubsubMessage { Data = ByteString.CopyFromUtf8(body) }], ct)
            .ConfigureAwait(false);
    }

    /// <summary>Acks every message it returns, per the interface contract — a message Pull hides
    /// behind an ack deadline rather than removing outright, so a delete that fails leaves it to
    /// reappear once that deadline expires.</summary>
    public async Task<IReadOnlyList<QueueMessage>> ReceiveMessagesAsync(string queue, int maxMessages, CancellationToken ct)
    {
        CallTrace.Request("PubSubQueue.ReceiveMessagesAsync");
        SubscriberServiceApiClient subscriber = factory.Subscriber();
        SubscriptionName subscriptionName = SubscriptionName.FromProjectSubscription(factory.ProjectId, queue);

        // Clamped for the same reason SqsQueue clamps to 1..10: the comparison page hands every
        // column the same batch size, and Pub/Sub answers anything outside 1..1000 with
        // InvalidArgument. Clamping keeps the interface's "up to maxMessages" honest instead of
        // making Pub/Sub the one column that throws where the others return a result.
        PullResponse response = await subscriber
            .PullAsync(subscriptionName, Math.Clamp(maxMessages, 1, 1000), ct).ConfigureAwait(false);

        if (response.ReceivedMessages.Count == 0)
        {
            return [];
        }

        await subscriber.AcknowledgeAsync(
            subscriptionName, response.ReceivedMessages.Select(m => m.AckId), ct).ConfigureAwait(false);

        return
        [
            .. response.ReceivedMessages.Select(m => new QueueMessage(m.Message.MessageId, m.Message.Data.ToStringUtf8())),
        ];
    }

    /// <summary>
    /// Removes both halves of the pair <see cref="CreateQueueAsync"/> made. A missing subscription
    /// does not stop the topic being removed: <see cref="CreateQueueAsync"/> creates the topic
    /// first, so a failure in between leaves a topic with no subscription, and letting the
    /// NotFound propagate here would strand that topic forever — the next
    /// <see cref="CreateQueueAsync"/> under the same name would then fail AlreadyExists.
    /// </summary>
    public async Task DeleteQueueAsync(string name, CancellationToken ct)
    {
        CallTrace.Request("PubSubQueue.DeleteQueueAsync");
        SubscriberServiceApiClient subscriber = factory.Subscriber();
        PublisherServiceApiClient publisher = factory.Publisher();

        try
        {
            await subscriber.DeleteSubscriptionAsync(
                SubscriptionName.FromProjectSubscription(factory.ProjectId, name), ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            /* already gone is the outcome this method wants, so it is not an error to report */
        }

        await publisher.DeleteTopicAsync(TopicName.FromProjectTopic(factory.ProjectId, name), ct).ConfigureAwait(false);
    }
}
