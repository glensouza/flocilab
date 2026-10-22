using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Oci.KeymanagementService;
using Oci.KeymanagementService.Models;
using VaultModel = Oci.KeymanagementService.Models.Vault;
using Oci.KeymanagementService.Requests;
using Oci.KeymanagementService.Responses;

namespace FlociLab.Oci.Vault;

/// <summary>
/// The key-management column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto OCI.DotNetSDK.Keymanagement: the comparison is only worth
/// anything if each column is the provider's own SDK doing the provider's own thing.
///
/// <para>
/// The interface addresses a key with no vault of its own — every other provider's key-management
/// capability is similarly flat — so every method here reuses one fixed vault named
/// <see cref="VaultName"/>, the same shape <c>KmsKeyManagement</c> reuses a fixed key ring for:
/// a fresh vault per click would be real, permanent (if never fully deleted) state behind every
/// comparison-page gesture a viewer makes.
/// </para>
/// </summary>
public sealed class OciVault(VaultClientFactory factory) : IKeyManagementCapability, IDisposable
{
    private const string VaultName = "flocilab";

    // Serialises GetOrCreateVaultAsync. A SemaphoreSlim rather than a Lock, because the section
    // it guards awaits: OCI has no create-if-absent for a vault, so two callers that both saw
    // none would both create one — and a vault can only ever be scheduled for deletion, never
    // removed, so that duplicate would be permanent and the lookup below would then pick between
    // the two arbitrarily.
    private readonly SemaphoreSlim vaultGate = new(1, 1);

    public string Provider => CloudProvider.Oci;

    public string ServiceName => "OCI Vault";

    public string? SourcePath => "samples/oci/vault/FlociLab.Oci.Vault.Demo/OciVault.cs";

    // The same classifier VaultDemo uses for its probe, so the coverage matrix and the comparison
    // page can never disagree about whether an operation is unimplemented, unreachable or
    // genuinely broken. TimeSpan.Zero because only the status is wanted here — the comparison page
    // times the call itself.
    public ProbeStatus Classify(Exception ex) => VaultDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<KeyInfo>> ListKeysAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciVault.ListKeysAsync");
        KmsVaultClient vaultClient = factory.CreateVault();

        // Finds, never creates. A read that provisioned a vault as a side effect would leave real,
        // permanent state behind every render of the comparison page — and since OCI only ever
        // schedules a vault for deletion, that state could never be cleaned up afterwards.
        VaultModel? vault = await this.FindVaultAsync(vaultClient, ct).ConfigureAwait(false);

        if (vault is null)
        {
            return [];
        }

        using KmsManagementClient management = factory.CreateManagement(vault.ManagementEndpoint);
        ListKeysResponse response = await management.ListKeys(
            new ListKeysRequest { CompartmentId = factory.CompartmentId }, cancellationToken: ct).ConfigureAwait(false);

        // Deliberately no client-side VaultId filter. In real OCI the management endpoint *is* the
        // vault selector — this request went to this vault's own host, so the answer is already
        // this vault's keys and a filter would be dead code. On floci-oci, where every vault's
        // planes are multiplexed onto one address (§14), it would be worse than dead: a key this
        // capability just created can be attributed to whichever vault was created most recently,
        // and the filter would then hide that key from the very list that created it.
        return response.Items
            .Select(k => new KeyInfo(k.Id, k.DisplayName, k.Algorithm.ToString()))
            .ToList();
    }

    /// <summary>
    /// Ensures the fixed vault exists (idempotent — see the class remarks) and creates a
    /// software-protected AES-256 key inside it named <paramref name="name"/>. Returns the key's
    /// OCID, which is what <see cref="EncryptAsync"/>, <see cref="DecryptAsync"/> and
    /// <see cref="DeleteKeyAsync"/> expect back as <c>keyId</c>.
    /// </summary>
    public async Task<string> CreateKeyAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciVault.CreateKeyAsync");
        KmsVaultClient vaultClient = factory.CreateVault();
        VaultModel vault = await GetOrCreateVaultAsync(vaultClient, ct).ConfigureAwait(false);

        using KmsManagementClient management = factory.CreateManagement(vault.ManagementEndpoint);
        CreateKeyResponse response = await management.CreateKey(
            new CreateKeyRequest
            {
                CreateKeyDetails = new CreateKeyDetails
                {
                    CompartmentId = factory.CompartmentId,
                    DisplayName = name,
                    KeyShape = new KeyShape { Algorithm = KeyShape.AlgorithmEnum.Aes, Length = 32 },
                    ProtectionMode = CreateKeyDetails.ProtectionModeEnum.Software,
                },
            },
            cancellationToken: ct).ConfigureAwait(false);

        return response.Key.Id;
    }

    public async Task<byte[]> EncryptAsync(string keyId, byte[] plaintext, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciVault.EncryptAsync");
        KmsVaultClient vaultClient = factory.CreateVault();
        VaultModel vault = await GetOrCreateVaultAsync(vaultClient, ct).ConfigureAwait(false);

        using KmsCryptoClient crypto = factory.CreateCrypto(vault.CryptoEndpoint);
        EncryptResponse response = await crypto.Encrypt(
            new EncryptRequest
            {
                EncryptDataDetails = new EncryptDataDetails { KeyId = keyId, Plaintext = Convert.ToBase64String(plaintext) },
            },
            cancellationToken: ct).ConfigureAwait(false);

        return Convert.FromBase64String(response.EncryptedData.Ciphertext);
    }

    public async Task<byte[]> DecryptAsync(string keyId, byte[] ciphertext, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciVault.DecryptAsync");
        KmsVaultClient vaultClient = factory.CreateVault();
        VaultModel vault = await GetOrCreateVaultAsync(vaultClient, ct).ConfigureAwait(false);

        using KmsCryptoClient crypto = factory.CreateCrypto(vault.CryptoEndpoint);
        DecryptResponse response = await crypto.Decrypt(
            new DecryptRequest
            {
                DecryptDataDetails = new DecryptDataDetails { KeyId = keyId, Ciphertext = Convert.ToBase64String(ciphertext) },
            },
            cancellationToken: ct).ConfigureAwait(false);

        return Convert.FromBase64String(response.DecryptedData.Plaintext);
    }

    /// <summary>Schedules the key's deletion — the closest OCI Vault gets to "delete"; real Vault never removes a key synchronously.</summary>
    public async Task DeleteKeyAsync(string keyId, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciVault.DeleteKeyAsync");
        KmsVaultClient vaultClient = factory.CreateVault();
        VaultModel vault = await GetOrCreateVaultAsync(vaultClient, ct).ConfigureAwait(false);

        using KmsManagementClient management = factory.CreateManagement(vault.ManagementEndpoint);
        await management.ScheduleKeyDeletion(
            new ScheduleKeyDeletionRequest { KeyId = keyId, ScheduleKeyDeletionDetails = new ScheduleKeyDeletionDetails() },
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Disposes the vault gate. Called by the container — the capability is a singleton.</summary>
    public void Dispose() => this.vaultGate.Dispose();

    /// <summary>
    /// Resolves the fixed vault by name, or <see langword="null"/> if no <c>ACTIVE</c> one exists.
    /// OCI Vault has no server-side "get by name" the way a client-named GCP key ring does, so the
    /// name is resolved here via <c>ListVaults</c>; the summary that comes back carries no
    /// endpoints, hence the <c>GetVault</c> behind it.
    /// </summary>
    private async Task<VaultModel?> FindVaultAsync(KmsVaultClient vaultClient, CancellationToken ct)
    {
        ListVaultsResponse existing = await vaultClient.ListVaults(
            new ListVaultsRequest { CompartmentId = factory.CompartmentId }, cancellationToken: ct).ConfigureAwait(false);
        VaultSummary? found = existing.Items.FirstOrDefault(
            v => v.DisplayName == VaultName && v.LifecycleState == VaultSummary.LifecycleStateEnum.Active);

        if (found is null)
        {
            return null;
        }

        GetVaultResponse reused = await vaultClient.GetVault(
            new GetVaultRequest { VaultId = found.Id }, cancellationToken: ct).ConfigureAwait(false);

        return reused.Vault;
    }

    /// <summary>
    /// Reuses the fixed vault if it is <c>ACTIVE</c>, otherwise creates it and waits for that state
    /// — the same idempotent-provisioning shape <c>KmsKeyManagement.CreateKeyAsync</c> uses for its
    /// key ring. Only the write paths call this; see <see cref="ListKeysAsync"/> for why reads use
    /// <see cref="FindVaultAsync"/> instead.
    /// </summary>
    private async Task<VaultModel> GetOrCreateVaultAsync(KmsVaultClient vaultClient, CancellationToken ct)
    {
        VaultModel? existing = await this.FindVaultAsync(vaultClient, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        // Only the create path is serialised, and the lookup is repeated inside: the common case is
        // a vault that already exists, which should not queue behind anything. See the gate's own
        // remarks for why a duplicate here would be permanent.
        await this.vaultGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            VaultModel? raced = await this.FindVaultAsync(vaultClient, ct).ConfigureAwait(false);

            if (raced is not null)
            {
                return raced;
            }

            return await this.CreateVaultAsync(vaultClient, ct).ConfigureAwait(false);
        }
        finally
        {
            this.vaultGate.Release();
        }
    }

    private async Task<VaultModel> CreateVaultAsync(KmsVaultClient vaultClient, CancellationToken ct)
    {
        CreateVaultResponse created = await vaultClient.CreateVault(
            new CreateVaultRequest
            {
                CreateVaultDetails = new CreateVaultDetails
                {
                    CompartmentId = factory.CompartmentId,
                    DisplayName = VaultName,
                    VaultType = CreateVaultDetails.VaultTypeEnum.Default,
                },
            },
            cancellationToken: ct).ConfigureAwait(false);

        GetVaultResponse finished = await vaultClient.Waiters
            .ForVault(new GetVaultRequest { VaultId = created.Vault.Id }, VaultModel.LifecycleStateEnum.Active)
            .ExecuteAsync().ConfigureAwait(false);

        return finished.Vault;
    }
}
