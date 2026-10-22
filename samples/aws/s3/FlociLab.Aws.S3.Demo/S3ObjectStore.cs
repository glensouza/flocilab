using Amazon.S3;
using Amazon.S3.Model;
using FlociLab.Core;
using FlociLab.Core.Capabilities;

namespace FlociLab.Aws.S3;

/// <summary>
/// The S3 column of the object-storage comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto AWSSDK.S3: the comparison is only worth anything if each column
/// is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class S3ObjectStore(S3ClientFactory factory) : IObjectStoreCapability
{
    public string Provider => CloudProvider.Aws;

    public string ServiceName => "Amazon S3";

    public string? SourcePath => "samples/aws/s3/FlociLab.Aws.S3.Demo/S3ObjectStore.cs";

    // The same classifier S3Demo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => S3Demo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nS3ObjectStore.ListContainersAsync");
        using IAmazonS3 client = factory.Create();
        ListBucketsResponse response = await client.ListBucketsAsync(new ListBucketsRequest(), ct).ConfigureAwait(false);

        return [.. (response.Buckets ?? []).Select(b => new ContainerInfo(b.BucketName, b.CreationDate))];
    }

    public async Task CreateContainerAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nS3ObjectStore.CreateContainerAsync");
        using IAmazonS3 client = factory.Create();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = name }, ct).ConfigureAwait(false);
    }

    public async Task PutObjectAsync(string container, string key, Stream data, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nS3ObjectStore.PutObjectAsync");
        using IAmazonS3 client = factory.Create();
        await client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = container,
                Key = key,
                InputStream = data,
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Buffered rather than handed back live: the caller owns the stream it gets, and the S3
    /// response it came from has to be disposed before this method returns.
    /// </summary>
    public async Task<Stream> GetObjectAsync(string container, string key, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nS3ObjectStore.GetObjectAsync");
        using IAmazonS3 client = factory.Create();
        using GetObjectResponse response = await client.GetObjectAsync(container, key, ct).ConfigureAwait(false);

        MemoryStream buffer = new();
        await response.ResponseStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        return buffer;
    }

    /// <summary>
    /// S3 refuses to delete a non-empty bucket, so the keys go first. Every other provider's
    /// container delete is one call — this asymmetry is exactly what the comparison page is for.
    /// </summary>
    public async Task DeleteContainerAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nS3ObjectStore.DeleteContainerAsync");
        using IAmazonS3 client = factory.Create();

        string? continuationToken = null;

        do
        {
            ListObjectsV2Response listed = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = name, ContinuationToken = continuationToken },
                ct).ConfigureAwait(false);

            foreach (S3Object s3Object in listed.S3Objects ?? [])
            {
                await client.DeleteObjectAsync(name, s3Object.Key, ct).ConfigureAwait(false);
            }

            continuationToken = listed.IsTruncated is true ? listed.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        await client.DeleteBucketAsync(name, ct).ConfigureAwait(false);
    }
}
