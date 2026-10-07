using Amazon;
using Amazon.Lightsail;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.Lightsail;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; Lightsail needs none of its own. It is AWS JSON 1.1:
/// every operation is <c>POST /</c> with an <c>X-Amz-Target</c> header.
/// </summary>
public sealed class LightsailClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>The region, for the availability zone every Lightsail create call names.</summary>
    public string Region => endpoints.Region;

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>A fresh client per demo run, so a page re-run after the endpoint configuration changed picks it up.</summary>
    public IAmazonLightsail Create()
    {
        // Real AWS: the SDK's own credential chain; the static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonLightsailClient(new AmazonLightsailConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonLightsailConfig config = new AmazonLightsailConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns one
            // refused connection into ~8 s, and would make the request shown per step not the only one.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonLightsailClient(endpoints.Credentials(), config);
    }
}
