using Amazon;
using Amazon.Organizations;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.Organizations;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; Organizations needs none of its own. It is a JSON 1.1 service
/// (<c>X-Amz-Target: AWSOrganizationsV20161128.*</c>) posted to the root path.
/// </summary>
public sealed class OrganizationsClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonOrganizations Create()
    {
        // Real AWS: the SDK's own credential chain, which is what a production app uses. The
        // static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonOrganizationsClient(new AmazonOrganizationsConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region),
            });
        }

        AmazonOrganizationsConfig config = new AmazonOrganizationsConfig
        {
            // The SDK default is 4 retries with backoff, which against a stopped emulator turns
            // one refused connection into ~8 s. A page whose whole job is to show "the emulator is
            // down" has to say so quickly, and the request shown beside each step is meant to be
            // *the* request. A production app against real Organizations wants the retries.
            MaxErrorRetry = 0,
        }.ForFloci(endpoints);

        return new AmazonOrganizationsClient(endpoints.Credentials(), config);
    }
}
