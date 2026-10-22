using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Google.Api.Gax.ResourceNames;
using Google.Cloud.Kms.V1;
using Google.Protobuf;
using Grpc.Core;

namespace FlociLab.Gcp.Kms;

/// <summary>
/// The key-management column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto Google.Cloud.Kms.V1: the comparison is only worth anything if
/// each column is the provider's own SDK doing the provider's own thing.
///
/// <para>
/// Every operation here reuses the fixed key ring <see cref="KmsDemo"/> reuses, for the same
/// reason — a key ring can never be deleted, so the comparison page must not create a fresh one
/// on every "create a key" gesture a viewer clicks.
/// </para>
/// </summary>
public sealed class KmsKeyManagement(KmsClientFactory factory) : IKeyManagementCapability
{
    private const string KeyRingId = "flocilab";

    public string Provider => CloudProvider.Gcp;

    public string ServiceName => "Google Cloud KMS";

    public string? SourcePath => "samples/gcp/kms/FlociLab.Gcp.Kms.Demo/KmsKeyManagement.cs";

    // The same classifier KmsDemo uses for its probe, so the coverage matrix and the comparison
    // page can never disagree about whether an operation is unimplemented, unreachable or
    // genuinely broken. TimeSpan.Zero because only the status is wanted here — the comparison page
    // times the call itself.
    public ProbeStatus Classify(Exception ex) => KmsDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<KeyInfo>> ListKeysAsync(CancellationToken ct)
    {
        CallTrace.Request("KmsKeyManagement.ListKeysAsync");
        KeyManagementServiceClient client = factory.Create();
        LocationName location = new(factory.ProjectId, factory.LocationId);
        List<KeyInfo> keys = [];

        await foreach (KeyRing keyRing in client.ListKeyRingsAsync(location).WithCancellation(ct).ConfigureAwait(false))
        {
            KeyRingName keyRingName = KeyRingName.Parse(keyRing.Name);

            await foreach (CryptoKey cryptoKey in client.ListCryptoKeysAsync(keyRingName).WithCancellation(ct).ConfigureAwait(false))
            {
                keys.Add(new KeyInfo(cryptoKey.Name, cryptoKey.CryptoKeyName?.CryptoKeyId, cryptoKey.Primary?.Algorithm.ToString()));
            }
        }

        return keys;
    }

    /// <summary>
    /// Ensures the fixed key ring exists (idempotent — see the class remarks) and creates a crypto
    /// key inside it named <paramref name="name"/>. Returns the crypto key's resource name, which
    /// is what <see cref="EncryptAsync"/>, <see cref="DecryptAsync"/> and
    /// <see cref="DeleteKeyAsync"/> expect back as <c>keyId</c>.
    /// </summary>
    public async Task<string> CreateKeyAsync(string name, CancellationToken ct)
    {
        CallTrace.Request("KmsKeyManagement.CreateKeyAsync");
        KeyManagementServiceClient client = factory.Create();
        LocationName location = new(factory.ProjectId, factory.LocationId);
        KeyRingName keyRingName = new(factory.ProjectId, factory.LocationId, KeyRingId);

        try
        {
            await client.CreateKeyRingAsync(location, KeyRingId, new KeyRing(), ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            // Reusing it is the point — see KmsDemo's remarks on why a key ring is provisioned
            // once rather than per call.
        }

        try
        {
            CryptoKey response = await client.CreateCryptoKeyAsync(
                keyRingName,
                name,
                new CryptoKey { Purpose = CryptoKey.Types.CryptoKeyPurpose.EncryptDecrypt },
                ct).ConfigureAwait(false);

            return response.Name;
        }
        // Unlike the key ring above, an AlreadyExists here is not something to reuse quietly. A
        // crypto key can never be deleted and DeleteKeyAsync only destroys its versions, so the
        // name stays taken forever with no usable key material behind it — encrypting under it
        // would fail. Rethrown with the constraint named, because the bare gRPC status reads as the
        // emulator misbehaving when it is in fact real Cloud KMS behaviour the caller has to work
        // around by picking a new name.
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            throw new InvalidOperationException(
                $"CryptoKey \"{name}\" already exists in {keyRingName}. A Cloud KMS crypto key can never be deleted, "
                + "so its name is permanently taken — even after every version has been destroyed. Use a new name.",
                ex);
        }
    }

    public async Task<byte[]> EncryptAsync(string keyId, byte[] plaintext, CancellationToken ct)
    {
        CallTrace.Request("KmsKeyManagement.EncryptAsync");
        KeyManagementServiceClient client = factory.Create();

        EncryptResponse response = await client.EncryptAsync(
            CryptoKeyName.Parse(keyId), ByteString.CopyFrom(plaintext), ct).ConfigureAwait(false);

        return response.Ciphertext.ToByteArray();
    }

    public async Task<byte[]> DecryptAsync(string keyId, byte[] ciphertext, CancellationToken ct)
    {
        CallTrace.Request("KmsKeyManagement.DecryptAsync");
        KeyManagementServiceClient client = factory.Create();

        DecryptResponse response = await client.DecryptAsync(
            CryptoKeyName.Parse(keyId), ByteString.CopyFrom(ciphertext), ct).ConfigureAwait(false);

        return response.Plaintext.ToByteArray();
    }

    /// <summary>
    /// Cloud KMS has no delete for a crypto key at all (see <see cref="KmsDemo"/>'s cleanup step)
    /// — destroying every enabled version's key material is what production code calls "deleting a
    /// key". The crypto key resource itself stays listed by <see cref="ListKeysAsync"/> forever,
    /// unlike every other provider's capability.
    /// </summary>
    public async Task DeleteKeyAsync(string keyId, CancellationToken ct)
    {
        CallTrace.Request("KmsKeyManagement.DeleteKeyAsync");
        KeyManagementServiceClient client = factory.Create();
        CryptoKeyName cryptoKeyName = CryptoKeyName.Parse(keyId);
        int destroyed = 0;

        await foreach (CryptoKeyVersion version in client.ListCryptoKeyVersionsAsync(cryptoKeyName).WithCancellation(ct).ConfigureAwait(false))
        {
            if (version.State != CryptoKeyVersion.Types.CryptoKeyVersionState.Enabled)
            {
                continue;
            }

            await client.DestroyCryptoKeyVersionAsync(CryptoKeyVersionName.Parse(version.Name), ct).ConfigureAwait(false);
            destroyed++;
        }

        // Plan §14's corollary for capability code: a delete that destroyed nothing has not done
        // what its caller asked, and returning quietly leaves the comparison page showing a
        // successful delete over key material that is still enabled. InvalidOperationException
        // rather than TimeoutException on purpose — Classify maps this to Error, where a timeout
        // type would map to Unreachable and blame a responding emulator for being down.
        if (destroyed == 0)
        {
            throw new InvalidOperationException(
                $"DeleteKey found no enabled version to destroy on {cryptoKeyName}; nothing was deleted.");
        }
    }
}
