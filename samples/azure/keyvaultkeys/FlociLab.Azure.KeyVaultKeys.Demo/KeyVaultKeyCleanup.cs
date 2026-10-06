using Azure.Security.KeyVault.Keys;

namespace FlociLab.Azure.KeyVaultKeys;

/// <summary>
/// The one delete path both <see cref="KeyVaultKeysDemo"/> and <see cref="KeyVaultKeyManagement"/>
/// use: a soft delete followed by a purge, by name, on <see cref="CancellationToken.None"/> — a run
/// that was cancelled still has a key to remove.
/// </summary>
internal static class KeyVaultKeyCleanup
{
    /// <summary>
    /// Purge matters as much as the delete: a soft-deleted key keeps its name reserved, so a cleanup
    /// that stopped at delete would leave the deleted-keys list growing by one per run. A status the
    /// vault answered with — a 404 for a key that never existed included — propagates.
    /// </summary>
    internal static async Task DeleteAndPurgeAsync(KeyClient client, string name)
    {
        DeleteKeyOperation operation = await client.StartDeleteKeyAsync(name, CancellationToken.None).ConfigureAwait(false);
        await operation.WaitForCompletionAsync(CancellationToken.None).ConfigureAwait(false);
        await client.PurgeDeletedKeyAsync(name, CancellationToken.None).ConfigureAwait(false);
    }
}
