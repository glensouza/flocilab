using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Oci.ObjectstorageService;
using Oci.ObjectstorageService.Models;
using Oci.ObjectstorageService.Requests;
using Oci.ObjectstorageService.Responses;

namespace FlociLab.Oci.ObjectStorage;

/// <summary>
/// The OCI column of the object-storage comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto OCI.DotNetSDK.Objectstorage: the comparison is only worth
/// anything if each column is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class OciObjectStore(ObjectStorageClientFactory factory) : IObjectStoreCapability
{
    // The namespace is a property of the tenancy, not of a request, so real OCI code looks it up
    // once and keeps it — every operation below would otherwise cost two round trips. The race
    // between two concurrent first callers is benign: both ask, both get the same string, and the
    // second write stores what the first one did.
    private string? space;

    public string Provider => CloudProvider.Oci;

    public string ServiceName => "OCI Object Storage";

    public string? SourcePath => "samples/oci/objectstorage/FlociLab.Oci.ObjectStorage.Demo/OciObjectStore.cs";

    // The same classifier ObjectStorageDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => ObjectStorageDemo.Classify(ex, TimeSpan.Zero).Status;

    /// <summary>
    /// Buckets are listed per compartment, not per account — the same bend GCS needs for projects,
    /// and one of the differences the comparison page exists to show. AWS and Azure both list from
    /// the credential's own scope.
    /// </summary>
    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciObjectStore.ListContainersAsync");
        // No using: the factory owns this client for the process lifetime now and disposes it
        // itself. Disposing it here would close the shared connection pool underneath every
        // other call — see ObjectStorageClientFactory.Create.
        ObjectStorageClient client = factory.Create();
        string tenancySpace = await this.NamespaceAsync(client, ct).ConfigureAwait(false);

        List<ContainerInfo> buckets = [];

        // ListBuckets pages with opc-next-page, and the comparison page showing a truncated list
        // would be worse than it showing none. A persistent-mode emulator accumulates buckets
        // across runs, so this is reachable in the lab and not only against a real tenancy.
        string? page = null;

        do
        {
            ListBucketsResponse response = await client.ListBuckets(
                new ListBucketsRequest
                {
                    NamespaceName = tenancySpace,
                    CompartmentId = factory.CompartmentId,
                    Page = page,
                },
                cancellationToken: ct).ConfigureAwait(false);

            buckets.AddRange(response.Items.Select(b => new ContainerInfo(b.Name, b.TimeCreated)));
            page = response.OpcNextPage;
        }
        while (!string.IsNullOrEmpty(page));

        return buckets;
    }

    public async Task CreateContainerAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciObjectStore.CreateContainerAsync");
        ObjectStorageClient client = factory.Create();
        await client.CreateBucket(
            new CreateBucketRequest
            {
                NamespaceName = await this.NamespaceAsync(client, ct).ConfigureAwait(false),
                CreateBucketDetails = new CreateBucketDetails { Name = name, CompartmentId = factory.CompartmentId },
            },
            cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task PutObjectAsync(string container, string key, Stream data, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciObjectStore.PutObjectAsync");
        ObjectStorageClient client = factory.Create();

        // No content type: the service falls back to application/octet-stream, which is the right
        // default for a capability that is handed bytes and told nothing about them.
        await client.PutObject(
            new PutObjectRequest
            {
                NamespaceName = await this.NamespaceAsync(client, ct).ConfigureAwait(false),
                BucketName = container,
                ObjectName = key,
                PutObjectBody = data,
            },
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Buffered rather than handed back live, matching the other providers'. The SDK's own stream
    /// is tied to the <see cref="ObjectStorageClient"/> disposed on the way out of this method, so
    /// returning it directly would hand the caller a stream that is already dead.
    /// </summary>
    public async Task<Stream> GetObjectAsync(string container, string key, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciObjectStore.GetObjectAsync");
        ObjectStorageClient client = factory.Create();
        GetObjectResponse response = await client.GetObject(
            new GetObjectRequest
            {
                NamespaceName = await this.NamespaceAsync(client, ct).ConfigureAwait(false),
                BucketName = container,
                ObjectName = key,
            },
            cancellationToken: ct).ConfigureAwait(false);

        MemoryStream buffer = new();
        await using (Stream source = response.InputStream)
        {
            await source.CopyToAsync(buffer, ct).ConfigureAwait(false);
        }

        buffer.Position = 0;

        return buffer;
    }

    /// <summary>
    /// Real OCI refuses to delete a non-empty bucket with a 409, so the objects go first — the
    /// same two-step dance S3 and GCS need, and the opposite of Azure, whose container delete
    /// takes its blobs with it. floci-oci 0.3.0 enforces this, unlike floci-gcp.
    /// </summary>
    public async Task DeleteContainerAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciObjectStore.DeleteContainerAsync");
        ObjectStorageClient client = factory.Create();
        string tenancySpace = await this.NamespaceAsync(client, ct).ConfigureAwait(false);

        // Restarting the listing after each page is correct because the page just went away, and
        // it saves carrying ListObjects' NextStartWith cursor. Bounded on progress rather than
        // left as a bare while (true): this runs on a Blazor request thread, and a service that
        // accepts DeleteObject without honouring it would otherwise spin here forever.
        int previouslyListed = int.MaxValue;

        while (true)
        {
            ListObjectsResponse listed = await client.ListObjects(
                new ListObjectsRequest { NamespaceName = tenancySpace, BucketName = name },
                cancellationToken: ct).ConfigureAwait(false);

            int stillThere = listed.ListObjects.Objects.Count;

            if (stillThere == 0)
            {
                break;
            }

            if (stillThere >= previouslyListed)
            {
                throw new InvalidOperationException(
                    $"Bucket {name} still lists {stillThere} object(s) after a pass that deleted "
                    + $"{previouslyListed}. The service is accepting DeleteObject without removing.");
            }

            previouslyListed = stillThere;

            foreach (ObjectSummary stored in listed.ListObjects.Objects)
            {
                await client.DeleteObject(
                    new DeleteObjectRequest { NamespaceName = tenancySpace, BucketName = name, ObjectName = stored.Name },
                    cancellationToken: ct).ConfigureAwait(false);
            }
        }

        await client.DeleteBucket(
            new DeleteBucketRequest { NamespaceName = tenancySpace, BucketName = name },
            cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<string> NamespaceAsync(ObjectStorageClient client, CancellationToken ct)
    {
        if (this.space is not null)
        {
            return this.space;
        }

        GetNamespaceResponse response = await client.GetNamespace(new GetNamespaceRequest(), cancellationToken: ct).ConfigureAwait(false);

        return this.space = response.Value;
    }
}
