using Amazon;
using Amazon.S3Vectors;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.S3Vectors;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; S3 Vectors needs none of its own. It is a REST JSON service: each
/// operation is a POST to its own path (<c>POST /CreateVectorBucket</c>, <c>POST /PutVectors</c>), the
/// request is a JSON body naming the bucket and the index, and the answer is JSON.
/// </summary>
public sealed class S3VectorsClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>A fresh client per demo run, so a page re-run after the endpoint configuration changed picks it up.</summary>
    public IAmazonS3Vectors Create()
    {
        // Real AWS: the SDK's own credential chain; the static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonS3VectorsClient(new AmazonS3VectorsConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonS3VectorsConfig config = new AmazonS3VectorsConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns one
            // refused connection into ~8 s, and would make the request shown per step not the only one.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonS3VectorsClient(endpoints.Credentials(), config);
    }
}
