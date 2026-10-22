using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using FlociLab.Core;
using FlociLab.Core.Capabilities;

namespace FlociLab.Aws.DynamoDb;

/// <summary>
/// The document-DB column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto AWSSDK.DynamoDBv2: the comparison is only worth anything if each
/// column is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class DynamoDbDocumentDb(DynamoDbClientFactory factory) : IDocumentDbCapability
{
    public string Provider => CloudProvider.Aws;

    public string ServiceName => "Amazon DynamoDB";

    public string? SourcePath => "samples/aws/dynamodb/FlociLab.Aws.DynamoDb.Demo/DynamoDbDocumentDb.cs";

    // The same classifier DynamoDbDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => DynamoDbDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nDynamoDbDocumentDb.ListCollectionsAsync");
        using IAmazonDynamoDB client = factory.Create();

        List<CollectionInfo> tables = [];
        string? exclusiveStart = null;

        // ListTables pages at 100 names per call by default, so one call is a truncated answer
        // rather than a short one. The lab never holds that many, but a listing that silently
        // stops partway is the shape a reader would copy into production.
        do
        {
            ListTablesResponse response = await client.ListTablesAsync(
                new ListTablesRequest { ExclusiveStartTableName = exclusiveStart }, ct).ConfigureAwait(false);

            tables.AddRange((response.TableNames ?? []).Select(name => new CollectionInfo(name)));
            exclusiveStart = response.LastEvaluatedTableName;
        }
        while (!string.IsNullOrEmpty(exclusiveStart));

        return tables;
    }

    /// <summary>Pay-per-request billing and a single "id" (String) partition key — the comparison
    /// page has no schema to offer beyond the document's own id, which every provider's column
    /// keys on the same way.</summary>
    public async Task CreateCollectionAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nDynamoDbDocumentDb.CreateCollectionAsync");
        using IAmazonDynamoDB client = factory.Create();

        CreateTableResponse response = await client.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
        }, ct).ConfigureAwait(false);

        // floci returns ACTIVE synchronously, so this loop never iterates against the emulator.
        // Real AWS answers CREATING and needs seconds to reach ACTIVE, and the comparison page's
        // very next call is UpsertDocumentAsync. The bound is 30 polls — 30 s of delay plus the
        // DescribeTable round-trips — so a stuck table fails rather than hanging forever.
        TableStatus status = response.TableDescription.TableStatus;

        for (int attempt = 0; status == TableStatus.CREATING && attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            DescribeTableResponse describe = await client.DescribeTableAsync(
                new DescribeTableRequest { TableName = name }, ct).ConfigureAwait(false);
            status = describe.Table.TableStatus;
        }

        // Returning normally here would tell the comparison page the collection exists when it
        // does not, and the failure would then surface as a ResourceNotFoundException from the
        // upsert — pinning the error on the wrong operation. Fail on the call that actually
        // timed out.
        //
        // InvalidOperationException and deliberately not TimeoutException: Classify maps
        // TimeoutException to Unreachable, which on the comparison page means "the emulator is
        // down". It answered every DescribeTable here — the table just never activated — so this
        // has to fall through to Error instead.
        if (status != TableStatus.ACTIVE)
        {
            throw new InvalidOperationException($"Table '{name}' was still {status} after 30 polls; it never became ACTIVE.");
        }
    }

    /// <summary>
    /// PutItem always replaces the whole item, which is what makes DynamoDB's write a genuine
    /// upsert with no separate create/update call to choose between. The document's own "id"
    /// field is overwritten with <paramref name="id"/> rather than trusted as-is: the partition
    /// key has to be the value <see cref="GetDocumentAsync"/> will look up by, not whatever the
    /// caller's JSON happened to carry.
    /// </summary>
    public async Task UpsertDocumentAsync(string collection, string id, string json, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nDynamoDbDocumentDb.UpsertDocumentAsync");
        using IAmazonDynamoDB client = factory.Create();

        Document document = Document.FromJson(json);
        document["id"] = id;

        await client.PutItemAsync(new PutItemRequest
        {
            TableName = collection,
            Item = document.ToAttributeMap(),
        }, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetDocumentAsync(string collection, string id, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nDynamoDbDocumentDb.GetDocumentAsync");
        using IAmazonDynamoDB client = factory.Create();

        GetItemResponse response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = collection,
            Key = new Dictionary<string, AttributeValue> { ["id"] = new AttributeValue(id) },
        }, ct).ConfigureAwait(false);

        return response.IsItemSet ? Document.FromAttributeMap(response.Item).ToJson() : null;
    }

    public async Task DeleteCollectionAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nDynamoDbDocumentDb.DeleteCollectionAsync");
        using IAmazonDynamoDB client = factory.Create();
        await client.DeleteTableAsync(new DeleteTableRequest { TableName = name }, ct).ConfigureAwait(false);
    }
}
