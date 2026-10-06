using Amazon;
using Amazon.AccessAnalyzer;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.AccessAnalyzer;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; Access Analyzer needs none of its own. It is a REST/JSON service
/// (<c>PUT /analyzer</c>), so there is no <c>X-Amz-Target</c> to show.
/// </summary>
public sealed class AccessAnalyzerClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonAccessAnalyzer Create()
    {
        // Real AWS: the SDK's own credential chain, which is what a production app uses. The
        // static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonAccessAnalyzerClient(new AmazonAccessAnalyzerConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonAccessAnalyzerConfig config = new AmazonAccessAnalyzerConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s. A page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request. A production app against real Access Analyzer wants the retries.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonAccessAnalyzerClient(endpoints.Credentials(), config);
    }
}
