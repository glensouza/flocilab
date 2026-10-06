using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Azure;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using FlociLab.Core;

namespace FlociLab.Azure.KeyVaultKeys;

/// <summary>
/// Azure Key Vault Keys against floci-az. Ordinary Azure.Security.KeyVault.Keys code — the only
/// emulator-aware line in the sample is in <see cref="KeyVaultKeysClientFactory"/>.
///
/// This sample was ⊘ through floci-az 0.13.0: the SDK's trailing-slash <c>GET keys/</c> list was
/// misrouted, and key bodies carried unset <c>attributes.nbf</c>/<c>exp</c> as JSON <c>null</c>.
/// floci-az 0.14.0 fixed both, via floci-az PR #349 from this project (docs/BLAZOR-PLAN.md §14).
/// </summary>
public sealed class KeyVaultKeysDemo(KeyVaultKeysClientFactory factory) : IServiceDemo
{
    private const string Plaintext = "Hello from FlociLab.";

    public string Provider => CloudProvider.Azure;

    public string Slug => "keyvaultkeys";

    public string DisplayName => "Key Vault Keys";

    public string Category => "Security";

    public string Route => "/azure/keyvaultkeys";

    /// <summary>GetPropertiesOfKeys — one request, no state, and the cheapest call Key Vault Keys has.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            KeyClient client = factory.Create();
            int count = 0;

            await foreach (Page<KeyProperties> page in
                client.GetPropertiesOfKeysAsync(ct).AsPages().ConfigureAwait(false))
            {
                count = page.Values.Count;
                break;
            }

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"GetPropertiesOfKeys returned {count} key(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        KeyClient client = factory.Create();

        // Unique per run, so two runs never collide and a leftover key never fails the next run.
        string name = $"flocilab-kvkey-{Guid.NewGuid():N}";
        bool created = false;
        Uri? keyId = null;
        byte[] ciphertext = [];

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListKeys — before",
                $"GET {factory.ServiceUrl}/keys\nclient.GetPropertiesOfKeysAsync()",
                async () =>
                {
                    int count = 0;

                    await foreach (Page<KeyProperties> page in
                        client.GetPropertiesOfKeysAsync(ct).AsPages().ConfigureAwait(false))
                    {
                        count += page.Values.Count;
                    }

                    return $"{count} key(s)";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateKey",
                $"POST {factory.ServiceUrl}/keys/{name}/create\nclient.CreateKeyAsync(\"{name}\", KeyType.Rsa)",
                async () =>
                {
                    // Claimed before the call, not after: a create can land while its response is
                    // lost — a cancelled run, or floci-az 0.13.0's unparseable reply (§14) — leaving
                    // a key no response ever named. Cleanup deletes by name and treats a 404 as
                    // nothing to remove, so claiming early is free.
                    created = true;
                    Response<KeyVaultKey> response = await client.CreateKeyAsync(name, KeyType.Rsa, cancellationToken: ct).ConfigureAwait(false);

                    // The id gates Encrypt/Decrypt, which need a key the SDK actually parsed.
                    keyId = response.Value.Id;

                    return $"HTTP {response.GetRawResponse().Status} — Id: {keyId}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Encrypt",
                // Two requests, and the second is conditional — CryptographyClient fetches the key
                // first and, if it can read the RSA public key out, does RSA-OAEP locally and never
                // sends an encrypt request at all. Naming only the POST would claim a wire call
                // that a working vault would not make.
                $"GET {factory.ServiceUrl}/keys/{name}\n"
                    + $"POST {factory.ServiceUrl}/keys/{name}/encrypt  (only if the public key could not be fetched;\n"
                    + "                                                RSA-OAEP runs locally when it could)\n"
                    + $"client.GetCryptographyClient(\"{name}\").EncryptAsync(RsaOaep, \"{Plaintext}\")",
                async () =>
                {
                    if (keyId is null)
                    {
                        throw new InvalidOperationException("Skipped — CreateKey did not return a key to encrypt with.");
                    }

                    CryptographyClient crypto = factory.CreateCryptographyClient(keyId);
                    byte[] plaintextBytes = Encoding.UTF8.GetBytes(Plaintext);
                    EncryptResult result = await crypto.EncryptAsync(EncryptionAlgorithm.RsaOaep, plaintextBytes, ct).ConfigureAwait(false);
                    ciphertext = result.Ciphertext;

                    // Same rule the KMS sample's Encrypt step uses: an Encrypt that hands back the
                    // plaintext untouched is not an encryption at all, and should not read as one.
                    if (ciphertext.Length == 0 || ciphertext.AsSpan().SequenceEqual(plaintextBytes))
                    {
                        throw new InvalidOperationException(
                            $"Encrypt returned {ciphertext.Length} byte(s) that are the plaintext itself; nothing was encrypted.");
                    }

                    return $"{ciphertext.Length} byte(s) of ciphertext";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "Decrypt",
                $"POST {factory.ServiceUrl}/keys/{name}/decrypt\nclient.GetCryptographyClient(\"{name}\").DecryptAsync(RsaOaep, <ciphertext>)",
                async () =>
                {
                    if (keyId is null)
                    {
                        throw new InvalidOperationException("Skipped — CreateKey did not return a key to decrypt with.");
                    }

                    // Guarded separately from keyId: the day /keys ships, CreateKey can succeed
                    // while Encrypt still fails, and sending the empty initializer to the vault
                    // would report an opaque SDK error instead of naming the step that broke.
                    if (ciphertext.Length == 0)
                    {
                        throw new InvalidOperationException("Skipped — Encrypt produced no ciphertext to decrypt.");
                    }

                    CryptographyClient crypto = factory.CreateCryptographyClient(keyId);
                    DecryptResult result = await crypto.DecryptAsync(EncryptionAlgorithm.RsaOaep, ciphertext, ct).ConfigureAwait(false);
                    string decrypted = Encoding.UTF8.GetString(result.Plaintext);

                    if (decrypted != Plaintext)
                    {
                        throw new InvalidOperationException(
                            $"Decrypted \"{decrypted}\" does not match the plaintext that was encrypted.");
                    }

                    return $"plaintext: {decrypted}";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // The step it produces is yielded below — an iterator may not yield from inside a
            // finally.
            cleanup = created ? await this.DeleteKeyAsync(client, name, ct).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// Azure reports both of the interesting failures inside a <see cref="RequestFailedException"/>,
    /// so <see cref="ProbeResult.FromException"/> — which inspects only the outermost exception —
    /// cannot classify them on its own. floci-az's missing <c>/keys</c> route answers a plain 404
    /// with no <c>x-ms-error-code: NotImplemented</c> header (unlike the storage plane's clean 501),
    /// so it classifies as <see cref="ProbeStatus.Error"/> here, not <see cref="ProbeStatus.NotImplemented"/> —
    /// an honest read of what the emulator actually said, verified 2026-09-01 (docs/BLAZOR-PLAN.md §14).
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case RequestFailedException { Status: (int)HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means something answered, so this is it behaving badly rather than
                // being absent. Stop unwrapping and report the error.
                case RequestFailedException { Status: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest about
    /// what floci-az actually does.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached. Catching it here would instead fabricate a
        // "Failed" step for every remaining operation, reporting the user navigating away as the
        // emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// A soft delete followed by a purge, mirroring <c>KeyVaultSecretsDemo</c>'s cleanup — see
    /// <see cref="KeyVaultKeyCleanup"/> for why the purge runs even when the delete's reply could
    /// not be read.
    /// </summary>
    private async Task<DemoStep> DeleteKeyAsync(KeyClient client, string name, CancellationToken ct)
    {
        string request = $"DELETE {factory.ServiceUrl}/keys/{name}\nclient.StartDeleteKeyAsync(\"{name}\")\n"
            + $"DELETE {factory.ServiceUrl}/deletedkeys/{name}\nclient.PurgeDeletedKeyAsync(\"{name}\")";

        return await RunStepAsync("DeleteKey — cleanup", request, async () =>
        {
            try
            {
                await KeyVaultKeyCleanup.DeleteAndPurgeAsync(client, name).ConfigureAwait(false);

                return "Deleted and purged"
                    + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return "Nothing to remove — the key was never created.";
            }
        }).ConfigureAwait(false);
    }
}
