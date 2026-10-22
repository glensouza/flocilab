using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Oci.Common.Auth;
using Oci.KeymanagementService;

namespace FlociLab.Oci.Vault;

/// <summary>
/// The emulator-specific wiring for this sample, split across the three clients OCI Vault + KMS
/// itself splits its API across: <see cref="KmsVaultClient"/> for the control plane (create/list a
/// vault, schedule its deletion — resolved from the region, like <c>ObjectStorageClient</c>), and
/// <see cref="KmsManagementClient"/> / <see cref="KmsCryptoClient"/> for a single vault's own
/// key-management and crypto planes — each addressed at the per-vault, host-routed endpoint
/// <c>GetVault</c> hands back, the same shape <c>QueueClientFactory</c> uses for a queue's
/// <c>messagesEndpoint</c>. See that type and plan §7 for why <c>SetEndpoint</c> alone is not
/// enough for any OCI client.
/// </summary>
public sealed class VaultClientFactory(OciEndpoints endpoints) : IDisposable
{
    private readonly Lock @lock = new();

    private KmsVaultClient? vaultClient;

    /// <summary>Only meaningful in emulator mode — see <c>ObjectStorageClientFactory.Endpoint</c>.</summary>
    public string? Endpoint => endpoints.UseEmulator ? endpoints.Endpoint : null;

    public string Region => endpoints.Region;

    public string CompartmentId => endpoints.TenancyId;

    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// One control-plane client for the process, built on first use — same reasoning as
    /// <c>ObjectStorageClientFactory.Create</c>: a fresh client per call is a fresh connection pool
    /// per call. Region-resolved, like <c>ObjectStorageClient</c> and <c>QueueAdminClient</c>, so
    /// the same <c>ForFloci</c> override applies.
    /// </summary>
    public KmsVaultClient CreateVault()
    {
        lock (this.@lock)
        {
            return this.vaultClient ??= this.BuildVault();
        }
    }

    /// <summary>
    /// The management-plane client for one vault. Real OCI Vault is host-routed per vault — every
    /// vault answers for its own keys at a <c>managementEndpoint</c> that <c>CreateVault</c> and
    /// <c>GetVault</c> hand back, a different host from the one that created it.
    ///
    /// <para>
    /// floci-oci does not derive that endpoint from the request — the identical gap
    /// <c>QueueClientFactory.CreateData</c> documents for a queue's <c>messagesEndpoint</c>, and
    /// the identical answer. It reports <c>http://{FLOCI_OCI_HOSTNAME}:4599</c> for both planes,
    /// falling back to the literal <c>localhost</c> when that variable is unset, and it ignores the
    /// <c>Host</c> header entirely — verified by curl against a *fresh* floci-oci 0.3.0 container
    /// all three ways, 2026-09-02. The AppHost now sets <c>FLOCI_OCI_HOSTNAME</c>
    /// (see <c>AppHost.cs</c>): <c>127.0.0.1</c> unless container-backed services are on, in which
    /// case <c>floci-oci</c>. A throwaway test container still leaves it unset, so those runs
    /// report <c>http://localhost:4599</c> — reachable only from the host and only while the published
    /// port is still the default, never from a sibling container on the <c>floci</c> network and
    /// never under Testcontainers, where the port is randomly mapped. It is also exactly the
    /// <c>localhost</c> the rest of this repo refuses (plan §14 — the dead IPv6 attempt on
    /// Windows). So the reported value is never trusted here: this builds the client the way
    /// production code would, against the endpoint the service actually reported, then overrides it
    /// with <c>ForFloci</c> in emulator mode. Real-cloud mode passes
    /// <paramref name="managementEndpoint"/> straight through unmodified, because there it is the
    /// whole point.
    /// </para>
    /// </summary>
    public KmsManagementClient CreateManagement(string managementEndpoint)
    {
        ArgumentNullException.ThrowIfNull(managementEndpoint);

        if (!endpoints.UseEmulator)
        {
            return new KmsManagementClient(new ConfigFileAuthenticationDetailsProvider("DEFAULT"), endpoint: managementEndpoint);
        }

        KmsManagementClient client = new(endpoints.AuthenticationProvider(), endpoint: managementEndpoint);

        return client.ForFloci(endpoints);
    }

    /// <summary>The crypto-plane client for one vault. Same per-vault, host-routed shape as <see cref="CreateManagement"/>, and the same override.</summary>
    public KmsCryptoClient CreateCrypto(string cryptoEndpoint)
    {
        ArgumentNullException.ThrowIfNull(cryptoEndpoint);

        if (!endpoints.UseEmulator)
        {
            return new KmsCryptoClient(new ConfigFileAuthenticationDetailsProvider("DEFAULT"), endpoint: cryptoEndpoint);
        }

        KmsCryptoClient client = new(endpoints.AuthenticationProvider(), endpoint: cryptoEndpoint);

        return client.ForFloci(endpoints);
    }

    /// <summary>Disposes the shared vault client. Called by the container — the factory is a singleton.</summary>
    public void Dispose()
    {
        lock (this.@lock)
        {
            this.vaultClient?.Dispose();
            this.vaultClient = null;
        }
    }

    private KmsVaultClient BuildVault()
    {
        // Real Oracle Cloud — see ObjectStorageClientFactory.Build for why this branch refuses a
        // run against the lab's synthetic tenancy rather than quietly creating vaults in it.
        if (!endpoints.UseEmulator)
        {
            if (string.IsNullOrWhiteSpace(endpoints.ConfiguredTenancyId)
                || endpoints.ConfiguredTenancyId == OciEmulatorOptions.DefaultTenancyId)
            {
                throw new InvalidOperationException(
                    "Floci:Oci:UseEmulator is false, so this targets real Oracle Cloud, but "
                    + "Floci:Oci:TenancyId is unset or still the lab's synthetic default. Set it "
                    + "explicitly to the OCID of the compartment the vault should live in — "
                    + "FLOCI_OCI_DEFAULT_TENANCY_ID does not count, it configures the emulator.");
            }

            return new KmsVaultClient(new ConfigFileAuthenticationDetailsProvider("DEFAULT"));
        }

        KmsVaultClient client = new(endpoints.AuthenticationProvider());

        return client.ForFloci(endpoints);
    }
}
