using Amazon;
using Amazon.IdentityManagement;
using Amazon.Runtime;
using Amazon.SecurityToken;
using FlociLab.Core.Endpoints;

namespace FlociLab.Aws.Sts;

/// <summary>
/// The whole of the emulator-specific wiring for this sample — AWS is the easy provider
/// (docs/BLAZOR-PLAN.md §7). <see cref="FlociAwsExtensions.ForFloci"/> sets the three knobs every
/// <c>Amazon*Config</c> shares; STS needs none of its own, because every operation already
/// addresses a single base endpoint. Nothing else about the SDK usage differs from production.
/// </summary>
public sealed class StsClientFactory(AwsEndpoints endpoints)
{
    /// <summary>Base URL, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    /// <summary>Whether the next <see cref="Create()"/> targets floci or real AWS.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// A fresh client per demo run, signed with the lab's own credentials (or the SDK's chain
    /// against real AWS). Production would hold one for the process lifetime; a page that can be
    /// re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public IAmazonSecurityTokenService Create()
    {
        // Real AWS. The credentials go too — the SDK's own chain (environment, profile, SSO, IMDS)
        // is what a production app uses, and the static "test"/"test" pair would be rejected.
        if (!endpoints.UseEmulator)
        {
            return new AmazonSecurityTokenServiceClient(this.Config());
        }

        return new AmazonSecurityTokenServiceClient(endpoints.Credentials(), this.Config());
    }

    /// <summary>
    /// A client that signs with the temporary credentials an earlier <c>AssumeRole</c> returned —
    /// the point of STS is that those credentials are then used like any others.
    /// </summary>
    public IAmazonSecurityTokenService Create(AWSCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        return new AmazonSecurityTokenServiceClient(credentials, this.Config());
    }

    /// <summary>
    /// The IAM client that makes the role <c>AssumeRole</c> needs — the one place this sample
    /// reaches for a second package. Same endpoint, same credentials, same retry policy as the STS
    /// client, so the page's one set of emulator-shaped lines covers both.
    /// </summary>
    public IAmazonIdentityManagementService CreateIam()
        => endpoints.UseEmulator
            ? new AmazonIdentityManagementServiceClient(endpoints.Credentials(), this.Shared(new AmazonIdentityManagementServiceConfig()))
            : new AmazonIdentityManagementServiceClient(this.Shared(new AmazonIdentityManagementServiceConfig()));

    // Retries come back to the SDK default against real AWS, because the reason they are off is a
    // lab-ergonomics one that does not apply there.
    private AmazonSecurityTokenServiceConfig Config()
        => this.Shared(new AmazonSecurityTokenServiceConfig());

    // The one place the emulator/real-AWS split lives, so the STS and IAM clients cannot drift
    // apart: real AWS gets the region and the SDK's own retries, floci gets EmulatorConfig's.
    private T Shared<T>(T config) where T : ClientConfig
    {
        if (!endpoints.UseEmulator)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(endpoints.Region);

            return config;
        }

        return this.EmulatorConfig(config);
    }

    private T EmulatorConfig<T>(T config) where T : ClientConfig
    {
        // The SDK default is 4 retries with backoff, which against a stopped emulator turns one
        // refused connection into ~8 s and a whole run into ~49 s of "Running…". Two reasons to
        // turn it off here: a page whose whole job is to show "the emulator is down" has to say so
        // quickly, and the request shown beside each step is meant to be *the* request — silently
        // sending five would make the page lie about the wire. A production app against real STS
        // wants the retries; this is the second and last emulator-shaped line in the sample.
        config.MaxErrorRetry = 0;

        return config.ForFloci(endpoints);
    }
}
