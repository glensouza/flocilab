using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Google.Cloud.SecretManager.V1;
using Google.Protobuf;
using Google.Api.Gax.ResourceNames;
using Grpc.Core;

namespace FlociLab.Gcp.SecretManager;

/// <summary>
/// The secrets-store column of the comparison page (docs/BLAZOR-PLAN.md §8). Deliberately the
/// thinnest possible mapping onto Google.Cloud.SecretManager.V1: the comparison is only worth
/// anything if each column is the provider's own SDK doing the provider's own thing.
/// </summary>
public sealed class SecretManagerSecretStore(SecretManagerClientFactory factory) : ISecretStoreCapability
{
    public string Provider => CloudProvider.Gcp;

    public string ServiceName => "Google Cloud Secret Manager";

    public string? SourcePath => "samples/gcp/secretmanager/FlociLab.Gcp.SecretManager.Demo/SecretManagerSecretStore.cs";

    // The same classifier SecretManagerDemo uses for its probe, so the coverage matrix and the
    // comparison page can never disagree about whether an operation is unimplemented, unreachable
    // or genuinely broken. TimeSpan.Zero because only the status is wanted here — the comparison
    // page times the call itself.
    public ProbeStatus Classify(Exception ex) => SecretManagerDemo.Classify(ex, TimeSpan.Zero).Status;

    public async Task<IReadOnlyList<SecretInfo>> ListSecretsAsync(CancellationToken ct)
    {
        CallTrace.Request("SecretManagerSecretStore.ListSecretsAsync");
        SecretManagerServiceClient client = factory.Create();
        List<SecretInfo> secrets = [];

        // Version is left null deliberately, the same reasoning as the AWS column: ListSecrets
        // returns the container, not its current version, and a comparison-page cell showing the
        // secret's own creation time under a "Version" header would be worse than an empty cell.
        // AccessSecretVersion is where a real version id comes from.
        await foreach (Secret secret in client.ListSecretsAsync(ProjectName.FromProject(factory.ProjectId))
            .WithCancellation(ct).ConfigureAwait(false))
        {
            // SecretName rather than SecretName.Parse: the typed view yields null on a resource
            // name whose shape the SDK's patterns do not cover, where Parse throws. One unexpected
            // Name from the emulator should cost that row its short id, not turn the whole
            // comparison-page column into an error.
            secrets.Add(new SecretInfo(
                secret.SecretName?.SecretId ?? secret.Name,
                UpdatedAt: secret.CreateTime?.ToDateTimeOffset()));
        }

        return secrets;
    }

    /// <summary>
    /// Secret Manager separates the container from its value — <c>AddSecretVersion</c> fails on a
    /// container that does not exist yet, and <c>CreateSecret</c> carries no payload at all. The
    /// comparison page's generic "set a secret" gesture has to try adding a version first and fall
    /// back to creating the container, mirroring the upsert shape the AWS and Azure columns give
    /// for a single call.
    /// </summary>
    public async Task SetSecretAsync(string name, string value, CancellationToken ct)
    {
        CallTrace.Request("SecretManagerSecretStore.SetSecretAsync");
        SecretManagerServiceClient client = factory.Create();
        SecretName secretName = new(factory.ProjectId, name);
        SecretPayload payload = new() { Data = ByteString.CopyFromUtf8(value) };

        try
        {
            await client.AddSecretVersionAsync(secretName, payload, ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            await client.CreateSecretAsync(
                ProjectName.FromProject(factory.ProjectId),
                name,
                new Secret { Replication = new Replication { Automatic = new Replication.Types.Automatic() } },
                ct).ConfigureAwait(false);

            await client.AddSecretVersionAsync(secretName, payload, ct).ConfigureAwait(false);
        }
    }

    public async Task<string> GetSecretAsync(string name, CancellationToken ct)
    {
        CallTrace.Request("SecretManagerSecretStore.GetSecretAsync");
        SecretManagerServiceClient client = factory.Create();
        SecretVersionName latest = new(factory.ProjectId, name, "latest");

        AccessSecretVersionResponse response = await client.AccessSecretVersionAsync(latest, ct).ConfigureAwait(false);

        // Named rather than dereferenced blind: a reply without a payload is a proto3 absent field,
        // and letting it NRE would hand the comparison page an error naming no operation at all.
        return SecretManagerResponse.Require(response.Payload, "AccessSecretVersion", "a payload").Data.ToStringUtf8();
    }

    /// <summary>
    /// Mirrors <see cref="SecretManagerDemo"/>'s cleanup: unlike AWS Secrets Manager, real Secret
    /// Manager's <c>DeleteSecret</c> has no recovery window to defeat — it removes the container
    /// and every version immediately.
    /// </summary>
    public async Task DeleteSecretAsync(string name, CancellationToken ct)
    {
        CallTrace.Request("SecretManagerSecretStore.DeleteSecretAsync");
        SecretManagerServiceClient client = factory.Create();

        await client.DeleteSecretAsync(new SecretName(factory.ProjectId, name), ct).ConfigureAwait(false);
    }
}
