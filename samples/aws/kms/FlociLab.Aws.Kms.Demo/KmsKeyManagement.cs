using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using FlociLab.Core;
using FlociLab.Core.Capabilities;

namespace FlociLab.Aws.Kms;

/// <summary>
/// The key-management column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto AWSSDK.KeyManagementService: the comparison is only worth
/// anything if each column is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class KmsKeyManagement(KmsClientFactory factory) : IKeyManagementCapability
{
    public string Provider => CloudProvider.Aws;

    public string ServiceName => "AWS KMS";

    public string? SourcePath => "samples/aws/kms/FlociLab.Aws.Kms.Demo/KmsKeyManagement.cs";

    // The same classifier KmsDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented,
    // unreachable or genuinely broken. TimeSpan.Zero because only the status is wanted
    // here — the comparison page times the call itself.
    public ProbeStatus Classify(Exception ex) => KmsDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<KeyInfo>> ListKeysAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nKmsKeyManagement.ListKeysAsync");
        using IAmazonKeyManagementService client = factory.Create();

        List<KeyInfo> keys = [];
        string? marker = null;

        // ListKeys pages at 100 keys per call by default, so one call is a truncated answer rather
        // than a short one. The lab never holds that many, but a listing that silently stops
        // partway is the shape a reader would copy into production.
        //
        // The loop condition is the marker, not Truncated — the shape the S3, SQS and DynamoDB
        // samples already use. A response that set Truncated without returning a NextMarker would
        // otherwise re-request page one forever, appending the same keys on every pass.
        do
        {
            ListKeysResponse response = await client.ListKeysAsync(
                new ListKeysRequest { Marker = marker }, ct).ConfigureAwait(false);

            keys.AddRange((response.Keys ?? []).Select(k => new KeyInfo(k.KeyId)));
            marker = response.Truncated is true ? response.NextMarker : null;
        }
        while (!string.IsNullOrEmpty(marker));

        return keys;
    }

    /// <summary>KMS has no name field on a key — <paramref name="name"/> becomes the
    /// description, which is the closest thing the comparison page's generic "create with a name"
    /// gesture maps onto.</summary>
    public async Task<string> CreateKeyAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nKmsKeyManagement.CreateKeyAsync");
        using IAmazonKeyManagementService client = factory.Create();

        CreateKeyResponse response = await client.CreateKeyAsync(
            new CreateKeyRequest { Description = name }, ct).ConfigureAwait(false);

        return KmsResponse.Require(response.KeyMetadata?.KeyId, "CreateKey", "KeyMetadata.KeyId");
    }

    public async Task<byte[]> EncryptAsync(string keyId, byte[] plaintext, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nKmsKeyManagement.EncryptAsync");
        using IAmazonKeyManagementService client = factory.Create();

        EncryptResponse response = await client.EncryptAsync(
            new EncryptRequest { KeyId = keyId, Plaintext = new MemoryStream(plaintext) }, ct).ConfigureAwait(false);

        return KmsResponse.Require(response.CiphertextBlob, "Encrypt", "CiphertextBlob").ToArray();
    }

    public async Task<byte[]> DecryptAsync(string keyId, byte[] ciphertext, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nKmsKeyManagement.DecryptAsync");
        using IAmazonKeyManagementService client = factory.Create();

        DecryptResponse response = await client.DecryptAsync(
            new DecryptRequest { KeyId = keyId, CiphertextBlob = new MemoryStream(ciphertext) }, ct).ConfigureAwait(false);

        return KmsResponse.Require(response.Plaintext, "Decrypt", "Plaintext").ToArray();
    }

    /// <summary>
    /// KMS has no immediate delete (see <see cref="KmsDemo"/>'s cleanup step) —
    /// <c>ScheduleKeyDeletion</c> with the API's minimum seven-day window is what production code
    /// calls "deleting a key". The key stays listed as <c>PendingDeletion</c> until the window
    /// elapses, unlike every other capability's delete.
    /// </summary>
    public async Task DeleteKeyAsync(string keyId, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nKmsKeyManagement.DeleteKeyAsync");
        using IAmazonKeyManagementService client = factory.Create();

        await client.ScheduleKeyDeletionAsync(
            new ScheduleKeyDeletionRequest { KeyId = keyId, PendingWindowInDays = 7 }, ct).ConfigureAwait(false);
    }
}
