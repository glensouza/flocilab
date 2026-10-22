using System.Text.Json;
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Google.Cloud.Firestore;

namespace FlociLab.Gcp.Firestore;

/// <summary>
/// The document-DB column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto Google.Cloud.Firestore: the comparison is only worth anything if
/// each column is the provider's own SDK doing the provider's own thing.
///
/// <para>
/// Firestore has no create-collection or delete-collection RPC — see <see cref="FirestoreDemo"/>'s
/// remarks. <see cref="CreateCollectionAsync"/> writes a placeholder document so the collection
/// exists the moment this method returns, matching the other three columns'
/// <c>CreateCollectionAsync</c> contract; <see cref="DeleteCollectionAsync"/> removes every
/// document the collection holds, placeholder included, which is the only way to make it stop
/// appearing in <see cref="ListCollectionsAsync"/>, and fails when it removed nothing.
/// </para>
/// </summary>
public sealed class FirestoreDocumentDb(FirestoreClientFactory factory) : IDocumentDbCapability
{
    private const string PlaceholderDocumentId = "_flocilab-placeholder";

    public string Provider => CloudProvider.Gcp;

    public string ServiceName => "Google Cloud Firestore";

    public string? SourcePath => "samples/gcp/firestore/FlociLab.Gcp.Firestore.Demo/FirestoreDocumentDb.cs";

    // The same classifier FirestoreDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => FirestoreDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken ct)
    {
        CallTrace.Request("FirestoreDocumentDb.ListCollectionsAsync");
        FirestoreDb db = factory.Create();
        List<CollectionInfo> collections = [];

        await foreach (CollectionReference collection in db.ListRootCollectionsAsync().WithCancellation(ct).ConfigureAwait(false))
        {
            collections.Add(new CollectionInfo(collection.Id));
        }

        return collections;
    }

    public async Task CreateCollectionAsync(string name, CancellationToken ct)
    {
        CallTrace.Request("FirestoreDocumentDb.CreateCollectionAsync");
        await factory.Create().Collection(name).Document(PlaceholderDocumentId)
            .SetAsync(new Dictionary<string, object> { ["id"] = PlaceholderDocumentId }, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The document's own "id" field is overwritten with <paramref name="id"/> rather than trusted
    /// as-is, matching DynamoDbDocumentDb and CosmosDbDocumentDb: it has to be the value
    /// <see cref="GetDocumentAsync"/> will look up by, not whatever the caller's JSON carried.
    /// </summary>
    public async Task UpsertDocumentAsync(string collection, string id, string json, CancellationToken ct)
    {
        CallTrace.Request("FirestoreDocumentDb.UpsertDocumentAsync");
        Dictionary<string, object?> fields = ParseFields(json);
        fields["id"] = id;

        await factory.Create().Collection(collection).Document(id).SetAsync(fields, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<string?> GetDocumentAsync(string collection, string id, CancellationToken ct)
    {
        CallTrace.Request("FirestoreDocumentDb.GetDocumentAsync");
        DocumentSnapshot snapshot = await factory.Create().Collection(collection).Document(id).GetSnapshotAsync(ct).ConfigureAwait(false);

        return snapshot.Exists ? JsonSerializer.Serialize(snapshot.ToDictionary()) : null;
    }

    public async Task DeleteCollectionAsync(string name, CancellationToken ct)
    {
        CallTrace.Request("FirestoreDocumentDb.DeleteCollectionAsync");
        FirestoreDb db = factory.Create();
        CollectionReference collectionRef = db.Collection(name);
        int deleted = 0;

        await foreach (DocumentReference document in collectionRef.ListDocumentsAsync().WithCancellation(ct).ConfigureAwait(false))
        {
            await document.DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
            deleted++;
        }

        // Firestore has no DeleteCollection RPC, so this "delete" is a loop that simply does not
        // iterate when the collection was never created — returning successfully having removed
        // nothing. The other two document-DB columns fault in that case (DynamoDB
        // ResourceNotFoundException, Cosmos 404), so without this throw the comparison page paints
        // GCP green and AWS/Azure red for the identical outcome, on the one page whose whole job is
        // making the columns comparable. A cleanup step is a step, and one that removed nothing has
        // not achieved what its badge claims (§14).
        //
        // FirestoreDemo pushes the same postcondition into the request as Precondition.MustExist,
        // which works because it deletes one known document. There is no per-collection equivalent
        // — ListDocuments on a missing collection is an empty stream, not an error — so here the
        // count is the only postcondition available.
        if (deleted == 0)
        {
            throw new InvalidOperationException($"Collection '{name}' held no documents to delete; nothing was removed.");
        }
    }

    /// <summary>
    /// Firestore's <see cref="DocumentReference.SetAsync"/> serializes a
    /// <c>Dictionary&lt;string, object&gt;</c> natively, so this walks the JSON tree into exactly
    /// that shape rather than reaching for a converter attribute the way an attributed POCO would.
    /// </summary>
    private static Dictionary<string, object?> ParseFields(string json)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);
        return parsed.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => ToFirestoreValue(p.Value));
    }

    private static object? ToFirestoreValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out long integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToFirestoreValue(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToFirestoreValue).ToList(),
        _ => throw new NotSupportedException($"Unsupported JSON value kind: {element.ValueKind}"),
    };
}
