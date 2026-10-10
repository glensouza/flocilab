using Amazon;
using Amazon.S3Tables;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.S3Tables;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; S3 Tables needs none of its own. It is a REST JSON service: each
/// operation is its own method and path (<c>PUT /buckets</c>, <c>PUT /tables/{arn}/{namespace}</c>),
/// the table bucket's ARN travels in the path, and the answer is JSON.
/// </summary>
public sealed class S3TablesClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>A fresh client per demo run, so a page re-run after the endpoint configuration changed picks it up.</summary>
    public IAmazonS3Tables Create()
    {
        // Real AWS: the SDK's own credential chain; the static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonS3TablesClient(new AmazonS3TablesConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonS3TablesConfig config = new AmazonS3TablesConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns one
            // refused connection into ~8 s, and would make the request shown per step not the only one.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonS3TablesClient(endpoints.Credentials(), config);
    }
}
