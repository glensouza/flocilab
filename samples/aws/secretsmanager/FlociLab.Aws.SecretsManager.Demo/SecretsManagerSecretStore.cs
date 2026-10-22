using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using FlociLab.Core;
using FlociLab.Core.Capabilities;

namespace FlociLab.Aws.SecretsManager;

/// <summary>
/// The secrets-store column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto AWSSDK.SecretsManager: the comparison is only worth anything if
/// each column is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class SecretsManagerSecretStore(SecretsManagerClientFactory factory) : ISecretStoreCapability
{
    public string Provider => CloudProvider.Aws;

    public string ServiceName => "AWS Secrets Manager";

    public string? SourcePath => "samples/aws/secretsmanager/FlociLab.Aws.SecretsManager.Demo/SecretsManagerSecretStore.cs";

    // The same classifier SecretsManagerDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented, unreachable
    // or genuinely broken. TimeSpan.Zero because only the status is wanted here — the comparison
    // page times the call itself.
    public ProbeStatus Classify(Exception ex) => SecretsManagerDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<SecretInfo>> ListSecretsAsync(CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nSecretsManagerSecretStore.ListSecretsAsync");
        using IAmazonSecretsManager client = factory.Create();

        List<SecretInfo> secrets = [];
        string? nextToken = null;

        // ListSecrets pages by default, so one call is a truncated answer rather than a short one.
        // The lab never holds enough secrets to page, but a listing that silently stops partway is
        // the shape a reader would copy into production.
        //
        // Version is left null deliberately: ListSecrets does not return a version id at all, and
        // the timestamp that is in reach is not one. A comparison-page column headed "Version"
        // showing a date would be worse than an empty cell. GetSecretValue and PutSecretValue are
        // where a real VersionId comes from.
        do
        {
            ListSecretsResponse response = await client.ListSecretsAsync(
                new ListSecretsRequest { NextToken = nextToken }, ct).ConfigureAwait(false);

            secrets.AddRange((response.SecretList ?? [])
                .Select(s => new SecretInfo(s.Name, UpdatedAt: s.LastChangedDate)));

            // Stop on a token the server just handed back unchanged. floci increments it ("1",
            // "2", …) so this never fires today, but an echoed token would otherwise re-request
            // the same page forever, appending the same secrets and growing the list without
            // bound — the same guard the KMS sample's marker loop carries.
            string? previousToken = nextToken;
            nextToken = response.NextToken == previousToken ? null : response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return secrets;
    }

    /// <summary>
    /// Secrets Manager has no single "create or update" call — <c>CreateSecret</c> fails on a name
    /// that already exists, and <c>PutSecretValue</c> fails on one that does not. The comparison
    /// page's generic "set a secret" gesture has to try the update first and fall back to create,
    /// which is the same upsert shape Key Vault's <c>SetSecret</c> gives for free.
    /// </summary>
    public async Task SetSecretAsync(string name, string value, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nSecretsManagerSecretStore.SetSecretAsync");
        using IAmazonSecretsManager client = factory.Create();

        try
        {
            await client.PutSecretValueAsync(
                new PutSecretValueRequest { SecretId = name, SecretString = value }, ct).ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            await client.CreateSecretAsync(
                new CreateSecretRequest { Name = name, SecretString = value }, ct).ConfigureAwait(false);
        }
    }

    public async Task<string> GetSecretAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nSecretsManagerSecretStore.GetSecretAsync");
        using IAmazonSecretsManager client = factory.Create();

        GetSecretValueResponse response = await client.GetSecretValueAsync(
            new GetSecretValueRequest { SecretId = name }, ct).ConfigureAwait(false);

        // Null for a secret stored as SecretBinary, and for anything the emulator answers without
        // the field. Throwing here names the operation that actually failed rather than handing
        // the caller a NullReferenceException from somewhere further down (plan §14).
        return SecretsManagerResponse.Require(response.SecretString, "GetSecretValue", "a SecretString (the secret may be binary)");
    }

    /// <summary>
    /// Mirrors <see cref="SecretsManagerDemo"/>'s cleanup: <c>ForceDeleteWithoutRecovery</c> rather
    /// than real Secrets Manager's default 30-day recovery window, so the comparison page's delete
    /// gesture actually removes the secret instead of leaving it pending.
    /// </summary>
    public async Task DeleteSecretAsync(string name, CancellationToken ct)
    {
        CallTrace.Request($"{factory.ServiceUrl}\nSecretsManagerSecretStore.DeleteSecretAsync");
        using IAmazonSecretsManager client = factory.Create();

        await client.DeleteSecretAsync(
            new DeleteSecretRequest { SecretId = name, ForceDeleteWithoutRecovery = true }, ct).ConfigureAwait(false);
    }
}
