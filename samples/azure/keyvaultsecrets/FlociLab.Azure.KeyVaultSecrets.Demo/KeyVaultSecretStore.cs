using Azure;
using Azure.Security.KeyVault.Secrets;
using FlociLab.Core;
using FlociLab.Core.Capabilities;

namespace FlociLab.Azure.KeyVaultSecrets;

/// <summary>
/// The secrets-store column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto Azure.Security.KeyVault.Secrets: the comparison is only worth
/// anything if each column is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class KeyVaultSecretStore(KeyVaultSecretsClientFactory factory) : ISecretStoreCapability
{
    public string Provider => CloudProvider.Azure;

    public string ServiceName => "Azure Key Vault";

    public string? SourcePath => "samples/azure/keyvaultsecrets/FlociLab.Azure.KeyVaultSecrets.Demo/KeyVaultSecretStore.cs";

    // The same classifier KeyVaultSecretsDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented, unreachable
    // or genuinely broken. TimeSpan.Zero because only the status is wanted here — the comparison
    // page times the call itself.
    public ProbeStatus Classify(Exception ex) => KeyVaultSecretsDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<SecretInfo>> ListSecretsAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nKeyVaultSecretStore.ListSecretsAsync");
        SecretClient client = factory.Create();
        List<SecretInfo> secrets = [];

        // GetPropertiesOfSecretsAsync pages by default, so one call is a truncated answer rather
        // than a short one. The lab never holds enough secrets to page, but a listing that
        // silently stops partway is the shape a reader would copy into production.
        await foreach (SecretProperties properties in client.GetPropertiesOfSecretsAsync(ct).ConfigureAwait(false))
        {
            secrets.Add(new SecretInfo(properties.Name, properties.Version, properties.UpdatedOn));
        }

        return secrets;
    }

    /// <summary>
    /// Key Vault's SetSecret is a genuine upsert — unlike Secrets Manager, there is no separate
    /// create call to fall back from.
    /// </summary>
    public async Task SetSecretAsync(string name, string value, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nKeyVaultSecretStore.SetSecretAsync");
        SecretClient client = factory.Create();

        await client.SetSecretAsync(name, value, ct).ConfigureAwait(false);
    }

    public async Task<string> GetSecretAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nKeyVaultSecretStore.GetSecretAsync");
        SecretClient client = factory.Create();
        Response<KeyVaultSecret> response = await client.GetSecretAsync(name, cancellationToken: ct).ConfigureAwait(false);

        return response.Value.Value;
    }

    /// <summary>
    /// Mirrors <see cref="KeyVaultSecretsDemo"/>'s cleanup: a soft delete followed by a purge, so
    /// the comparison page's delete gesture actually frees the name rather than leaving it reserved
    /// for the recovery window.
    /// </summary>
    public async Task DeleteSecretAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nKeyVaultSecretStore.DeleteSecretAsync");
        SecretClient client = factory.Create();

        DeleteSecretOperation operation = await client.StartDeleteSecretAsync(name, ct).ConfigureAwait(false);
        await operation.WaitForCompletionAsync(ct).ConfigureAwait(false);
        await client.PurgeDeletedSecretAsync(name, ct).ConfigureAwait(false);
    }
}
