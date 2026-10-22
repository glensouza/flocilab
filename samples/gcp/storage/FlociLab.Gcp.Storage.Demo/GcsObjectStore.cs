using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Google.Cloud.Storage.V1;
using GcsBucket = Google.Apis.Storage.v1.Data.Bucket;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace FlociLab.Gcp.Storage;

/// <summary>
/// The GCS column of the object-storage comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto Google.Cloud.Storage.V1: the comparison is only worth anything
/// if each column is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class GcsObjectStore(StorageClientFactory factory) : IObjectStoreCapability
{
    public string Provider => CloudProvider.Gcp;

    public string ServiceName => "Google Cloud Storage";

    public string? SourcePath => "samples/gcp/storage/FlociLab.Gcp.Storage.Demo/GcsObjectStore.cs";

    // The same classifier StorageDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => StorageDemo.Classify(ex, TimeSpan.Zero).Status;

    /// <summary>
    /// Buckets are listed per project, not per account — the one place this interface's shape has
    /// to bend for GCP, and a difference the comparison page is there to show. AWS and Azure both
    /// list from the credential's own scope; GCS needs to be told which project to look in.
    /// </summary>
    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.BaseUri}\nGcsObjectStore.ListContainersAsync");
        // No using: the factory owns this client for the process lifetime now and disposes it
        // itself. Disposing it here would close the shared connection pool underneath every
        // other call — see StorageClientFactory.Create.
        StorageClient client = factory.Create();
        List<ContainerInfo> containers = [];

        await foreach (GcsBucket bucket in client.ListBucketsAsync(factory.ProjectId).WithCancellation(ct).ConfigureAwait(false))
        {
            containers.Add(new ContainerInfo(bucket.Name, bucket.TimeCreatedDateTimeOffset));
        }

        return containers;
    }

    public async Task CreateContainerAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.BaseUri}\nGcsObjectStore.CreateContainerAsync");
        StorageClient client = factory.Create();
        await client.CreateBucketAsync(factory.ProjectId, name, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task PutObjectAsync(string container, string key, Stream data, CancellationToken ct)
    {
        CallTrace.Request($"{factory.BaseUri}\nGcsObjectStore.PutObjectAsync");
        StorageClient client = factory.Create();

        // Null content type: the SDK falls back to application/octet-stream, which is the right
        // default for a capability that is handed bytes and told nothing about them.
        await client.UploadObjectAsync(container, key, contentType: null, data, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Buffered rather than streamed from the response, matching the other providers': the caller
    /// owns the stream it gets back, and GCS hands bytes to a destination rather than returning
    /// one, so a buffer is the only shape available anyway.
    /// </summary>
    public async Task<Stream> GetObjectAsync(string container, string key, CancellationToken ct)
    {
        CallTrace.Request($"{factory.BaseUri}\nGcsObjectStore.GetObjectAsync");
        StorageClient client = factory.Create();

        MemoryStream buffer = new();
        await client.DownloadObjectAsync(container, key, buffer, cancellationToken: ct).ConfigureAwait(false);
        buffer.Position = 0;

        return buffer;
    }

    /// <summary>
    /// Real GCS refuses to delete a non-empty bucket, so the objects go first — the same two-step
    /// dance S3 needs, and the opposite of Azure, whose container delete takes its blobs with it.
    ///
    /// <para>
    /// floci-gcp enforces it since 0.9.0 (409, pinned by
    /// <c>GcpStorageTests.Deleting_A_Non_Empty_Bucket_Is_Refused</c>). Through 0.8.0 it answered 204,
    /// dropped the bucket and left the objects readable as orphans — the case this method was
    /// written against, since capability code has to be correct for the real service rather than
    /// for whatever the emulator happens to allow. See docs/BLAZOR-PLAN.md §14.
    /// </para>
    /// </summary>
    public async Task DeleteContainerAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.BaseUri}\nGcsObjectStore.DeleteContainerAsync");
        StorageClient client = factory.Create();

        await foreach (GcsObject stored in client.ListObjectsAsync(name, prefix: null).WithCancellation(ct).ConfigureAwait(false))
        {
            await client.DeleteObjectAsync(name, stored.Name, cancellationToken: ct).ConfigureAwait(false);
        }

        await client.DeleteBucketAsync(name, cancellationToken: ct).ConfigureAwait(false);
    }
}
