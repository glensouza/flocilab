using Amazon;
using Amazon.ElasticFileSystem;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.Efs;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; EFS needs none of its own. It is a REST-JSON
/// service: every operation is its own method and path under <c>/2015-02-01/</c> — <c>POST
/// /file-systems</c>, <c>GET /mount-targets</c> — with a JSON body.
/// </summary>
public sealed class EfsClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>A fresh client per demo run, so a page re-run after the endpoint configuration changed picks it up.</summary>
    public IAmazonElasticFileSystem Create()
    {
        // Real AWS: the SDK's own credential chain; the static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonElasticFileSystemClient(new AmazonElasticFileSystemConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonElasticFileSystemConfig config = new AmazonElasticFileSystemConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns one
            // refused connection into ~8 s, and would make the request shown per step not the only one.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonElasticFileSystemClient(endpoints.Credentials(), config);
    }
}
