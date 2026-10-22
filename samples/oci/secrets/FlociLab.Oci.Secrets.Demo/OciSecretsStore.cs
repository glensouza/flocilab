using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Oci.SecretsService;
using Oci.SecretsService.Requests;
using Oci.SecretsService.Responses;
using Oci.VaultService;
using Oci.VaultService.Models;
using Oci.VaultService.Requests;
using Oci.VaultService.Responses;

namespace FlociLab.Oci.Secrets;

/// <summary>
/// The secrets-store column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto the two OCI SDK packages this sample carries: the comparison is
/// only worth anything if each column is the provider's own SDK doing the provider's own thing.
///
/// <para>
/// The interface addresses a secret by name alone, with no vault or key of its own — every other
/// provider's secret-store capability is similarly flat — so the vault and master encryption key
/// come from configuration, the same two values <see cref="SecretsDemo"/> uses. A capability that
/// provisioned its own would need <c>OCI.DotNetSDK.Keymanagement</c>, and would create permanent
/// (never fully deletable) state behind every comparison-page gesture a viewer makes.
/// </para>
/// </summary>
public sealed class OciSecretsStore(SecretsClientFactory factory) : ISecretStoreCapability
{
    public string Provider => CloudProvider.Oci;

    public string ServiceName => "OCI Vault Secrets";

    public string? SourcePath => "samples/oci/secrets/FlociLab.Oci.Secrets.Demo/OciSecretsStore.cs";

    // The same classifier SecretsDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented, unreachable
    // or genuinely broken. TimeSpan.Zero because only the status is wanted here — the comparison
    // page times the call itself.
    public ProbeStatus Classify(Exception ex) => SecretsDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<SecretInfo>> ListSecretsAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciSecretsStore.ListSecretsAsync");
        VaultsClient secretsManagement = factory.CreateSecretsManagement();
        ListSecretsResponse response = await secretsManagement.ListSecrets(
            new ListSecretsRequest { CompartmentId = factory.CompartmentId }, cancellationToken: ct).ConfigureAwait(false);

        // Version left null deliberately, the same reasoning as the GCP column: SecretSummary
        // carries no version number (only the full Secret from GetSecret does), and a
        // comparison-page cell showing the secret's own creation time under a "Version" header
        // would be worse than an empty cell.
        return response.Items
            .Where(s => s.LifecycleState == SecretSummary.LifecycleStateEnum.Active)
            .Select(s => new SecretInfo(s.SecretName, UpdatedAt: s.TimeCreated))
            .ToList();
    }

    /// <summary>
    /// Updates the secret if one by this name already exists, otherwise creates it in the
    /// configured vault. Mirrors the upsert shape the AWS and Azure columns give for a single call
    /// — OCI has no native upsert, only separate Create/Update operations addressed by OCID rather
    /// than name.
    /// </summary>
    public async Task SetSecretAsync(string name, string value, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciSecretsStore.SetSecretAsync");
        VaultsClient secretsManagement = factory.CreateSecretsManagement();
        string? existingId = await this.FindSecretIdAsync(secretsManagement, name, ct).ConfigureAwait(false);
        Base64SecretContentDetails content = new()
        {
            Content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)),
            Stage = SecretContentDetails.StageEnum.Current,
        };

        if (existingId is not null)
        {
            await secretsManagement.UpdateSecret(
                new UpdateSecretRequest { SecretId = existingId, UpdateSecretDetails = new UpdateSecretDetails { SecretContent = content } },
                cancellationToken: ct).ConfigureAwait(false);

            return;
        }

        // Only the create path needs them: an update addresses the secret by OCID and carries no
        // vault or key, so a column with no vault configured can still read and update.
        if (!factory.TryGetTarget(out string vaultId, out string keyId, out string problem))
        {
            throw new InvalidOperationException(problem);
        }

        await secretsManagement.CreateSecret(
            new CreateSecretRequest
            {
                CreateSecretDetails = new CreateSecretDetails
                {
                    CompartmentId = factory.CompartmentId,
                    VaultId = vaultId,
                    KeyId = keyId,
                    SecretName = name,
                    SecretContent = content,
                },
            },
            cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<string> GetSecretAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciSecretsStore.GetSecretAsync");
        VaultsClient secretsManagement = factory.CreateSecretsManagement();
        string? secretId = await this.FindSecretIdAsync(secretsManagement, name, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No secret named '{name}' exists.");

        SecretsClient secrets = factory.CreateSecrets();
        GetSecretBundleResponse response = await secrets.GetSecretBundle(
            new GetSecretBundleRequest { SecretId = secretId }, cancellationToken: ct).ConfigureAwait(false);

        return SecretsDemo.DecodeContent(response.SecretBundle.SecretBundleContent, "GetSecretBundle");
    }

    /// <summary>Schedules the secret's deletion — the closest OCI Vault gets to "delete"; real Vault never removes a secret synchronously.</summary>
    public async Task DeleteSecretAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.Endpoint}\nOciSecretsStore.DeleteSecretAsync");
        VaultsClient secretsManagement = factory.CreateSecretsManagement();
        string secretId = await this.FindSecretIdAsync(secretsManagement, name, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No secret named '{name}' exists.");

        await secretsManagement.ScheduleSecretDeletion(
            new ScheduleSecretDeletionRequest { SecretId = secretId, ScheduleSecretDeletionDetails = new ScheduleSecretDeletionDetails() },
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Resolves a secret's OCID by name via the emulator's server-side <c>name</c> filter (confirmed by curl 2026-09-02), or <see langword="null"/> if none is active.</summary>
    private async Task<string?> FindSecretIdAsync(VaultsClient secretsManagement, string name, CancellationToken ct)
    {
        ListSecretsResponse response = await secretsManagement.ListSecrets(
            new ListSecretsRequest { CompartmentId = factory.CompartmentId, Name = name }, cancellationToken: ct).ConfigureAwait(false);

        return response.Items
            .FirstOrDefault(s => s.SecretName == name && s.LifecycleState == SecretSummary.LifecycleStateEnum.Active)?.Id;
    }
}
