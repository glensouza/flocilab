using Azure;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using FlociLab.Core;
using FlociLab.Core.Capabilities;

namespace FlociLab.Azure.KeyVaultKeys;

/// <summary>
/// The key-management column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto Azure.Security.KeyVault.Keys: the comparison is only worth
/// anything if each column is the provider's own SDK doing the provider's own thing. Every method
/// here fails against floci-az today — see <see cref="KeyVaultKeysDemo"/>.
/// </summary>
public sealed class KeyVaultKeyManagement(KeyVaultKeysClientFactory factory) : IKeyManagementCapability
{
    public string Provider => CloudProvider.Azure;

    public string ServiceName => "Azure Key Vault";

    // The same classifier KeyVaultKeysDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented, unreachable
    // or genuinely broken. TimeSpan.Zero because only the status is wanted here — the comparison
    // page times the call itself.
    public ProbeStatus Classify(Exception ex) => KeyVaultKeysDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<KeyInfo>> ListKeysAsync(CancellationToken ct)
    {
        KeyClient client = factory.Create();
        List<KeyInfo> keys = [];

        // GetPropertiesOfKeysAsync pages by default, so one call is a truncated answer rather than
        // a short one. The lab never holds enough keys to page, but a listing that silently stops
        // partway is the shape a reader would copy into production.
        await foreach (KeyProperties properties in client.GetPropertiesOfKeysAsync(ct).ConfigureAwait(false))
        {
            keys.Add(new KeyInfo(properties.Id.ToString(), properties.Name));
        }

        return keys;
    }

    public async Task<string> CreateKeyAsync(string name, CancellationToken ct)
    {
        KeyClient client = factory.Create();

        try
        {
            Response<KeyVaultKey> response = await client.CreateKeyAsync(name, KeyType.Rsa, cancellationToken: ct).ConfigureAwait(false);

            return response.Value.Id.ToString();
        }
        // The comparison page can only delete a key whose id came back from here, so a create that
        // landed without a readable reply would leak a key — any run a viewer cancels mid-create,
        // and every run on floci-az 0.13.0, whose replies did not parse (docs/BLAZOR-PLAN.md §14). Key
        // Vault addresses keys by name, so undo it by name before the failure propagates. Not when
        // the vault answered with a status — it refused, so nothing was created, and undoing a 409
        // would purge a key this call never made — and not when nothing answered at all.
        catch (Exception ex) when (ex is not RequestFailedException { Status: not 0 } && this.Classify(ex) != ProbeStatus.Unreachable)
        {
            await UndoCreateAsync(client, name, ex).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<byte[]> EncryptAsync(string keyId, byte[] plaintext, CancellationToken ct)
    {
        CryptographyClient crypto = factory.CreateCryptographyClient(new Uri(keyId));
        EncryptResult result = await crypto.EncryptAsync(EncryptionAlgorithm.RsaOaep, plaintext, ct).ConfigureAwait(false);

        return result.Ciphertext;
    }

    public async Task<byte[]> DecryptAsync(string keyId, byte[] ciphertext, CancellationToken ct)
    {
        CryptographyClient crypto = factory.CreateCryptographyClient(new Uri(keyId));
        DecryptResult result = await crypto.DecryptAsync(EncryptionAlgorithm.RsaOaep, ciphertext, ct).ConfigureAwait(false);

        return result.Plaintext;
    }

    /// <summary>
    /// Mirrors <see cref="KeyVaultKeysDemo"/>'s cleanup (<see cref="KeyVaultKeyCleanup"/>): a soft delete followed by a purge, unlike
    /// AWS KMS's <c>ScheduleKeyDeletion</c> — Key Vault's delete completes (or in this emulator's
    /// case, does complete) immediately rather than after a mandatory waiting period.
    /// </summary>
    public async Task DeleteKeyAsync(string keyId, CancellationToken ct)
    {
        KeyClient client = factory.Create();

        // Segments[2], not Segments[^1]. A Key Vault key id is /keys/{name}/{version}, and the id
        // CreateKeyAsync hands back above is always versioned — so the last segment is the version
        // guid, and deleting by it 404s KeyNotFound while the key stays in the vault. Segments[2] is
        // the name in both shapes: the versioned id from a create, and the unversioned one
        // ListKeysAsync returns.
        string name = new Uri(keyId).Segments[2].TrimEnd('/');

        await KeyVaultKeyCleanup.DeleteAndPurgeAsync(client, name).ConfigureAwait(false);
    }

    private static async Task UndoCreateAsync(KeyClient client, string name, Exception createFailure)
    {
        try
        {
            // The caller sees the create's own failure; this only makes sure no key is left behind.
            await KeyVaultKeyCleanup.DeleteAndPurgeAsync(client, name).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Nothing to undo — the create failed before the key existed.
        }
        catch (Exception ex)
        {
            throw new AggregateException(
                $"CreateKey failed, and removing \"{name}\" by name failed too — the key may still be in the vault.", createFailure, ex);
        }
    }
}
