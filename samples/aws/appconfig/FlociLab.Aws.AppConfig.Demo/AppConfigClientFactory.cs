using Amazon;
using Amazon.AppConfig;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.AppConfig;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; AppConfig needs none of its own. It is a REST JSON
/// service: each operation is its own method and path (<c>POST /applications</c>,
/// <c>GET /applications/{id}/environments</c>), and the answer is JSON.
/// </summary>
public sealed class AppConfigClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Region, for building the ARN that tagging addresses.</summary>
    public string Region => endpoints.Region;

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>A fresh client per demo run, so a page re-run after the endpoint configuration changed picks it up.</summary>
    public IAmazonAppConfig Create()
    {
        // Real AWS: the SDK's own credential chain; the static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonAppConfigClient(new AmazonAppConfigConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonAppConfigConfig config = new AmazonAppConfigConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns one
            // refused connection into ~8 s, and would make the request shown per step not the only one.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonAppConfigClient(endpoints.Credentials(), config);
    }
}
