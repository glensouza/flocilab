using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FlociLab.Core;
using FlociLab.Core.Capabilities;

namespace FlociLab.Azure.Blob;

/// <summary>
/// The Blob Storage column of the object-storage comparison page (docs/BLAZOR-PLAN.md §8).
/// Deliberately the thinnest possible mapping onto Azure.Storage.Blobs: the comparison is only
/// worth anything if each column is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class BlobObjectStore(BlobClientFactory factory) : IObjectStoreCapability
{
    public string Provider => CloudProvider.Azure;

    public string ServiceName => "Azure Blob Storage";

    public string? SourcePath => "samples/azure/blob/FlociLab.Azure.Blob.Demo/BlobObjectStore.cs";

    // The same classifier BlobDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => BlobDemo.Classify(ex, TimeSpan.Zero).Status;

    /// <summary>
    /// Containers have no creation time in the Blob API — the closest thing the list returns is
    /// the last-modified stamp, which for a container nobody has touched is when it was made.
    /// </summary>
    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nBlobObjectStore.ListContainersAsync");
        BlobServiceClient client = factory.Create();
        List<ContainerInfo> containers = [];

        await foreach (BlobContainerItem item in client.GetBlobContainersAsync(cancellationToken: ct).ConfigureAwait(false))
        {
            containers.Add(new ContainerInfo(item.Name, item.Properties.LastModified));
        }

        return containers;
    }

    public async Task CreateContainerAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nBlobObjectStore.CreateContainerAsync");
        BlobServiceClient client = factory.Create();
        await client.CreateBlobContainerAsync(name, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task PutObjectAsync(string container, string key, Stream data, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nBlobObjectStore.PutObjectAsync");
        BlobServiceClient client = factory.Create();
        BlobClient blob = client.GetBlobContainerClient(container).GetBlobClient(key);

        await blob.UploadAsync(data, overwrite: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Buffered rather than handed back live, to match the S3 column: the caller owns the stream
    /// it gets, and it should not also own a live HTTP response it did not ask for.
    /// </summary>
    public async Task<Stream> GetObjectAsync(string container, string key, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nBlobObjectStore.GetObjectAsync");
        BlobServiceClient client = factory.Create();
        BlobClient blob = client.GetBlobContainerClient(container).GetBlobClient(key);

        MemoryStream buffer = new();
        await blob.DownloadToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        return buffer;
    }

    /// <summary>
    /// One call, blobs included. S3 needs the keys deleted first or DeleteBucket is a 409 — that
    /// asymmetry is exactly what the comparison page is for.
    /// </summary>
    public async Task DeleteContainerAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nBlobObjectStore.DeleteContainerAsync");
        BlobServiceClient client = factory.Create();
        await client.DeleteBlobContainerAsync(name, cancellationToken: ct).ConfigureAwait(false);
    }
}
