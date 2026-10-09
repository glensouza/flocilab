using Amazon;
using Amazon.AppConfig;
using Amazon.AppConfigData;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.AppConfigData;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; AppConfigData needs none of its own. It is a REST JSON service
/// with two operations: <c>POST /configurationsessions</c> opens a session and
/// <c>GET /configuration?configuration_token=…</c> polls it.
/// </summary>
public sealed class AppConfigDataClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>A fresh client per demo run, so a page re-run after the endpoint configuration changed picks it up.</summary>
    public IAmazonAppConfigData Create()
    {
        // Real AWS: the SDK's own credential chain; the static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonAppConfigDataClient(new AmazonAppConfigDataConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonAppConfigDataConfig config = new AmazonAppConfigDataConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns one
            // refused connection into ~8 s, and would make the request shown per step not the only one.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonAppConfigDataClient(endpoints.Credentials(), config);
    }

    /// <summary>
    /// The control-plane client the seeding uses: AWSSDK.AppConfig, the sample's second package
    /// (see the csproj). Only ever created against the emulator — <see cref="AppConfigDataDemo"/>
    /// seeds nothing on real AWS — so there is no real-cloud branch to keep.
    /// </summary>
    public IAmazonAppConfig CreateAppConfig()
    {
        AmazonAppConfigConfig config = new AmazonAppConfigConfig
        {
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonAppConfigClient(endpoints.Credentials(), config);
    }
}
