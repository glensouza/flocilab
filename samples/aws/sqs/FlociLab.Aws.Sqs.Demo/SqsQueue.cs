using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using FlociLab.Core;
using FlociLab.Core.Capabilities;

namespace FlociLab.Aws.Sqs;

/// <summary>
/// The SQS column of the queue comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto AWSSDK.SQS: the comparison is only worth anything if each column
/// is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class SqsQueue(SqsClientFactory factory) : IQueueCapability
{
    public string Provider => CloudProvider.Aws;

    public string ServiceName => "Amazon SQS";

    public string? SourcePath => "samples/aws/sqs/FlociLab.Aws.Sqs.Demo/SqsQueue.cs";

    // The same classifier SqsDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => SqsDemo.Classify(ex, TimeSpan.Zero).Status;

    /// <summary>SQS hands back full queue URLs, never bare names — every other provider's queue
    /// listing is name-first, so the comparison page needs one to normalize against.</summary>
    public async Task<IReadOnlyList<QueueInfo>> ListQueuesAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nSqsQueue.ListQueuesAsync");
        using IAmazonSQS client = factory.Create();

        List<QueueInfo> queues = [];
        string? nextToken = null;

        // SQS caps a page at 1000 queue URLs, so one call is a truncated answer rather than a
        // short one. The lab never holds that many, but a listing that silently stops at 1000 is
        // the shape a reader would copy into production.
        do
        {
            ListQueuesResponse response = await client.ListQueuesAsync(
                new ListQueuesRequest { NextToken = nextToken }, ct).ConfigureAwait(false);

            queues.AddRange((response.QueueUrls ?? []).Select(url => new QueueInfo(url[(url.LastIndexOf('/') + 1)..])));
            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return queues;
    }

    public async Task CreateQueueAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nSqsQueue.CreateQueueAsync");
        using IAmazonSQS client = factory.Create();
        await client.CreateQueueAsync(new CreateQueueRequest { QueueName = name }, ct).ConfigureAwait(false);
    }

    public async Task SendMessageAsync(string queue, string body, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nSqsQueue.SendMessageAsync");
        using IAmazonSQS client = factory.Create();
        string queueUrl = await ResolveQueueUrlAsync(client, queue, ct).ConfigureAwait(false);

        await client.SendMessageAsync(
            new SendMessageRequest { QueueUrl = queueUrl, MessageBody = body }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes/acks every message it returns, per the interface contract — SQS makes that a
    /// second call, since ReceiveMessage only hides messages behind a visibility timeout rather
    /// than removing them. Only the successfully acked messages come back: one whose delete failed
    /// reappears when its visibility timeout expires, so returning it would hand the caller a
    /// message that is still on the queue.
    /// </summary>
    public async Task<IReadOnlyList<QueueMessage>> ReceiveMessagesAsync(string queue, int maxMessages, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nSqsQueue.ReceiveMessagesAsync");
        using IAmazonSQS client = factory.Create();
        string queueUrl = await ResolveQueueUrlAsync(client, queue, ct).ConfigureAwait(false);

        ReceiveMessageResponse response = await client.ReceiveMessageAsync(
            new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                // SQS rejects anything outside 1..10, while Pub/Sub and Service Bus take larger
                // batches. Clamping here keeps the interface's "up to maxMessages" honest instead
                // of answering a comparison page's batch of 20 with InvalidParameterValue.
                MaxNumberOfMessages = Math.Clamp(maxMessages, 1, 10),
                // The same long poll SqsDemo uses: the default 0s short poll samples only a subset
                // of SQS's servers, so a receive that immediately follows a send can legitimately
                // come back empty. Two seconds absorbs that without a healthy call feeling slow.
                WaitTimeSeconds = 2,
            }, ct).ConfigureAwait(false);
        List<Message> received = response.Messages ?? [];

        if (received.Count == 0)
        {
            return [];
        }

        // One batch call rather than a delete per message: it is the idiomatic ack for a batch,
        // and it reports success per entry, so a partial failure neither throws away the acks that
        // did land nor claims the ones that did not. Entry ids are the index into `received`,
        // which is what maps the result back.
        DeleteMessageBatchResponse acked = await client.DeleteMessageBatchAsync(
            new DeleteMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries =
                [
                    .. received.Select((message, index) => new DeleteMessageBatchRequestEntry
                    {
                        Id = index.ToString(CultureInfo.InvariantCulture),
                        ReceiptHandle = message.ReceiptHandle,
                    }),
                ],
            }, ct).ConfigureAwait(false);

        return
        [
            .. (acked.Successful ?? [])
                .Select(entry => received[int.Parse(entry.Id, CultureInfo.InvariantCulture)])
                .Select(message => new QueueMessage(message.MessageId, message.Body)),
        ];
    }

    public async Task DeleteQueueAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nSqsQueue.DeleteQueueAsync");
        using IAmazonSQS client = factory.Create();
        string queueUrl = await ResolveQueueUrlAsync(client, name, ct).ConfigureAwait(false);

        await client.DeleteQueueAsync(queueUrl, ct).ConfigureAwait(false);
    }

    private static async Task<string> ResolveQueueUrlAsync(IAmazonSQS client, string name, CancellationToken ct)
    {
        GetQueueUrlResponse response = await client.GetQueueUrlAsync(
            new GetQueueUrlRequest { QueueName = name }, ct).ConfigureAwait(false);

        return response.QueueUrl;
    }
}
