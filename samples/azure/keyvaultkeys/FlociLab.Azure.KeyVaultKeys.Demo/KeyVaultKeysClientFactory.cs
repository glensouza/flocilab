using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using FlociLab.Core.Endpoints;

namespace FlociLab.Azure.KeyVaultKeys;

/// <summary>
/// The whole of the emulator-specific wiring for this sample. Same shape as
/// <c>KeyVaultSecretsClientFactory</c>: a data plane URI with no vault-name segment
/// (docs/BLAZOR-PLAN.md §7, §14), and a <c>TokenCredential</c> from
/// <c>FlociLab.Azure.Endpoints</c> rather than an account key.
///
/// floci-az 0.13.0 misrouted the SDK's trailing-slash list and sent <c>attributes.nbf</c>/<c>exp</c>
/// as JSON <c>null</c>; 0.14.0 fixed both (floci-az PR #349, docs/BLAZOR-PLAN.md §14).
/// </summary>
public sealed class KeyVaultKeysClientFactory(AzureEndpoints endpoints)
{
    /// <summary>Vault endpoint, for showing the wire-level request alongside the SDK call.</summary>
    public string ServiceUrl => this.VaultUri.ToString().TrimEnd('/');

    /// <summary>Whether the next <see cref="Create"/> targets floci-az or real Azure.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    private Uri VaultUri => endpoints.UseEmulator
        ? endpoints.BaseUri
        : endpoints.RealCloudKeyVaultUri
            ?? throw new InvalidOperationException(
                "Floci:Azure:UseEmulator is false but no Floci:Azure:KeyVaultUri was configured. "
                + "Supply a real Key Vault URI (e.g. https://my-vault.vault.azure.net/) through user "
                + "secrets or an environment variable — never appsettings.json.");

    /// <summary>
    /// A fresh client per demo run. Production would hold one for the process lifetime; a page
    /// that can be re-run after the endpoint configuration changed wants a new one each time.
    /// </summary>
    public KeyClient Create()
    {
        KeyClientOptions options = new();

        // Turned off for the same reason every other factory in this repo turns retries off: a
        // page whose whole job is to show "the emulator does not implement this" has to say so
        // quickly, and the request shown beside each step is meant to be *the* request that went
        // out.
        options.Retry.MaxRetries = 0;

        if (endpoints.UseEmulator)
        {
            // floci-az serves Key Vault over plain HTTP, and the SDK's own auth policy refuses to
            // attach a bearer token to anything but https with no override — see
            // FlociAzureExtensions.AllowInsecureBearerToken for the full story and why this is safe.
            options.AllowInsecureBearerToken(this.VaultUri);

            // floci-az's IMDS token names its resource "https://vault.azure.net" (the real Azure
            // audience), but the challenge-based auth policy also checks that resource against the
            // *requested* host and rejects 127.0.0.1 as a mismatch. Real Key Vault never triggers
            // this: the vault host and the token audience agree there.
            options.DisableChallengeResourceVerification = true;
        }

        return new KeyClient(this.VaultUri, endpoints.Credential(), options);
    }

    /// <summary>
    /// A <see cref="CryptographyClient"/> built directly from a key's own id URI rather than
    /// through <see cref="KeyClient.GetCryptographyClient(string, string?)"/> — the id is what
    /// <see cref="KeyVaultKey.Id"/> and
    /// <see cref="FlociLab.Core.Capabilities.KeyInfo.Id"/> both carry, and building from it works
    /// for a specific version as well as the current one without parsing the name back out.
    /// </summary>
    public CryptographyClient CreateCryptographyClient(Uri keyId)
    {
        CryptographyClientOptions options = new();
        options.Retry.MaxRetries = 0;

        if (!endpoints.UseEmulator)
        {
            return new CryptographyClient(keyId, endpoints.Credential(), options);
        }

        // floci-az reports every key id on the production host — https://{account}.vault.azure.net,
        // hardcoded in its KeyVaultKeys.vaultHost (0.14.0), not echoed from the request — so a
        // client built from the id as-is would send this run's bearer token to real Azure. Keep the
        // vault's own /keys/{name}/{version} path and re-address it at the configured emulator, the
        // same move QueueClientFactory makes with OCI's messagesEndpoint (docs/BLAZOR-PLAN.md §14).
        Uri emulatorKeyId = new(this.VaultUri, keyId.AbsolutePath);

        // Same reason as Create() above — CryptographyClient carries its own auth policy.
        options.AllowInsecureBearerToken(emulatorKeyId);
        options.DisableChallengeResourceVerification = true;

        return new CryptographyClient(emulatorKeyId, endpoints.Credential(), options);
    }
}
