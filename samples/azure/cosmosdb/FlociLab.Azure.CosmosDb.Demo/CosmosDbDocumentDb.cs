using System.Net;
using System.Text.Json;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Microsoft.Azure.Cosmos;

namespace FlociLab.Azure.CosmosDb;

/// <summary>
/// The document-DB column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto Microsoft.Azure.Cosmos: the comparison is only worth anything if
/// each column is the provider's own SDK doing the provider's own thing.
///
/// <para>
/// Cosmos's "collection" maps onto a container inside a single fixed, persistent database
/// (<see cref="DatabaseId"/>) — see <see cref="CosmosDbDemo"/>'s remarks on the two-level hierarchy.
/// Only <see cref="CreateCollectionAsync"/> creates that database, idempotently: it is the one
/// operation whose whole job is to provision, so it is the one place a side effect belongs. The
/// reads and the delete address the database directly and let a missing one surface as itself —
/// <see cref="ListCollectionsAsync"/> as an empty list, the others as the SDK's own 404.
/// </para>
/// </summary>
public sealed class CosmosDbDocumentDb(CosmosDbClientFactory factory) : IDocumentDbCapability
{
    private const string DatabaseId = "flocilab";

    public string Provider => CloudProvider.Azure;

    public string ServiceName => "Azure Cosmos DB";

    public string? SourcePath => "samples/azure/cosmosdb/FlociLab.Azure.CosmosDb.Demo/CosmosDbDocumentDb.cs";

    // The same classifier CosmosDbDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => CosmosDbDemo.Classify(ex, TimeSpan.Zero).Status;

    /// <summary>
    /// A read, so it deliberately does not call <see cref="EnsureDatabaseAsync"/>: provisioning
    /// from a list operation would mean merely rendering the comparison column creates the
    /// database, and against a real account (<c>UseEmulator=false</c>) that is a billable resource
    /// created by what the caller believes is a read. A database that does not exist yet is simply
    /// an empty list.
    /// </summary>
    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nCosmosDbDocumentDb.ListCollectionsAsync");
        Database database = factory.Create().GetDatabase(DatabaseId);
        List<CollectionInfo> collections = [];

        using FeedIterator<ContainerProperties> iterator = database.GetContainerQueryIterator<ContainerProperties>();

        try
        {
            while (iterator.HasMoreResults)
            {
                FeedResponse<ContainerProperties> page = await iterator.ReadNextAsync(ct).ConfigureAwait(false);
                collections.AddRange(page.Select(c => new CollectionInfo(c.Id)));
            }
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No database yet — nothing has been created through this capability. Not an error.
            return [];
        }

        return collections;
    }

    /// <summary>Single "id" (String) partition key — the same shape DynamoDbDocumentDb uses, so
    /// the comparison page has an identical schema across both document-DB columns.</summary>
    public async Task CreateCollectionAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nCosmosDbDocumentDb.CreateCollectionAsync");
        Database database = await this.EnsureDatabaseAsync(ct).ConfigureAwait(false);
        await database.CreateContainerAsync(name, "/id", cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The document's own "id" field is overwritten with <paramref name="id"/> rather than trusted
    /// as-is, matching DynamoDbDocumentDb: the partition key has to be the value
    /// <see cref="GetDocumentAsync"/> will look up by, not whatever the caller's JSON carried.
    /// </summary>
    public async Task UpsertDocumentAsync(string collection, string id, string json, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nCosmosDbDocumentDb.UpsertDocumentAsync");
        Container container = factory.Create().GetContainer(DatabaseId, collection);

        using JsonDocument parsed = JsonDocument.Parse(json);
        Dictionary<string, JsonElement> fields = parsed.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        // SerializeToElement rather than JsonDocument.Parse(...).RootElement: the latter allocates
        // a document nobody owns, so its pooled buffer is never returned — once per document
        // written, on a path the comparison page drives.
        fields["id"] = JsonSerializer.SerializeToElement(id);

        await using MemoryStream body = new(JsonSerializer.SerializeToUtf8Bytes(fields));

        using ResponseMessage response = await container.UpsertItemStreamAsync(body, new PartitionKey(id), cancellationToken: ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task<string?> GetDocumentAsync(string collection, string id, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nCosmosDbDocumentDb.GetDocumentAsync");
        Container container = factory.Create().GetContainer(DatabaseId, collection);

        using ResponseMessage response = await container.ReadItemStreamAsync(id, new PartitionKey(id), cancellationToken: ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        using StreamReader reader = new(response.Content);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteCollectionAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nCosmosDbDocumentDb.DeleteCollectionAsync");
        Container container = factory.Create().GetContainer(DatabaseId, name);
        await container.DeleteContainerAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<Database> EnsureDatabaseAsync(CancellationToken ct)
    {
        DatabaseResponse response = await factory.Create().CreateDatabaseIfNotExistsAsync(DatabaseId, cancellationToken: ct).ConfigureAwait(false);
        return response.Database;
    }
}
