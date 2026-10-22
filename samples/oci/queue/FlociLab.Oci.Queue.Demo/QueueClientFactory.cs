using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Oci.Common.Auth;
using Oci.QueueService;

namespace FlociLab.Oci.Queue;

/// <summary>
/// The emulator-specific wiring for this sample, split across the two clients OCI Queue itself
/// splits its API across: <see cref="QueueAdminClient"/> for the control plane (create/list/delete
/// a queue) and <see cref="QueueClient"/> for the data plane (put/get/delete messages). Same
/// <c>AuthenticationProvider()</c> plus <c>ForFloci</c> shape as
/// <c>ObjectStorageClientFactory</c> — see that type and plan §7 for why <c>SetEndpoint</c> alone
/// is not enough.
/// </summary>
public sealed class QueueClientFactory(OciEndpoints endpoints) : IDisposable
{
    private readonly Lock @lock = new();

    private QueueAdminClient? adminClient;

    /// <summary>Only meaningful in emulator mode — see <c>ObjectStorageClientFactory.Endpoint</c>.</summary>
    public string? Endpoint => endpoints.UseEmulator ? endpoints.Endpoint : null;

    public string Region => endpoints.Region;

    public string CompartmentId => endpoints.TenancyId;

    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// One control-plane client for the process, built on first use — same reasoning as
    /// <c>ObjectStorageClientFactory.Create</c>: a fresh client per call is a fresh connection pool
    /// per call.
    /// </summary>
    public QueueAdminClient CreateAdmin()
    {
        lock (this.@lock)
        {
            return this.adminClient ??= this.BuildAdmin();
        }
    }

    /// <summary>
    /// The data-plane client for one queue. Real OCI hosts message traffic on the per-queue,
    /// host-routed endpoint <c>Queue.MessagesEndpoint</c> returned by <c>GetQueue</c> — the same
    /// shape Cloud Run and GKE use, and the reason this is a method rather than a cached property
    /// like <see cref="CreateAdmin"/>.
    ///
    /// <para>
    /// floci-oci does not derive that endpoint from the request. It answers
    /// <c>http://{FLOCI_OCI_HOSTNAME}:4599</c>, falling back to the literal <c>localhost</c> when
    /// that variable is unset, and it ignores the <c>Host</c> header entirely — verified by curl
    /// against floci-oci 0.3.0 both ways, 2026-09-02. The AppHost now sets
    /// <c>FLOCI_OCI_HOSTNAME</c> (see <c>AppHost.cs</c>): <c>127.0.0.1</c> unless container-backed
    /// services are on, in which case <c>floci-oci</c>. A throwaway test container still leaves it
    /// unset, so those runs report <c>http://localhost:4599</c>. That string reaches this emulator
    /// only when the caller is on the host *and* the published port is still the default one: it
    /// reaches nothing from a sibling container on the <c>floci</c> network, and nothing under
    /// Testcontainers, where the port is randomly mapped. It is also precisely the <c>localhost</c>
    /// the rest of this repo refuses (plan §14 — the dead IPv6 attempt on Windows). So the
    /// reported value is never trusted here.
    /// </para>
    ///
    /// <para>
    /// In emulator mode this therefore builds the client the way production code would — against
    /// the endpoint the service actually reported — then overrides it with <c>ForFloci</c>, the
    /// same "believe the configured endpoint, not the emulator's self-description" move
    /// <c>ObjectStorageClientFactory</c> makes for <c>SetEndpoint</c>. Real-cloud mode passes
    /// <paramref name="messagesEndpoint"/> straight through unmodified, because there it is the
    /// whole point.
    /// </para>
    /// </summary>
    public QueueClient CreateData(string messagesEndpoint)
    {
        ArgumentNullException.ThrowIfNull(messagesEndpoint);

        if (!endpoints.UseEmulator)
        {
            return new QueueClient(new ConfigFileAuthenticationDetailsProvider("DEFAULT"), endpoint: messagesEndpoint);
        }

        QueueClient client = new(endpoints.AuthenticationProvider());

        return client.ForFloci(endpoints);
    }

    /// <summary>Disposes the shared admin client. Called by the container — the factory is a singleton.</summary>
    public void Dispose()
    {
        lock (this.@lock)
        {
            this.adminClient?.Dispose();
            this.adminClient = null;
        }
    }

    private QueueAdminClient BuildAdmin()
    {
        // Real Oracle Cloud — see ObjectStorageClientFactory.Build for why this branch refuses a
        // run against the lab's synthetic tenancy rather than quietly creating queues in it.
        if (!endpoints.UseEmulator)
        {
            if (string.IsNullOrWhiteSpace(endpoints.ConfiguredTenancyId)
                || endpoints.ConfiguredTenancyId == OciEmulatorOptions.DefaultTenancyId)
            {
                throw new InvalidOperationException(
                    "Floci:Oci:UseEmulator is false, so this targets real Oracle Cloud, but "
                    + "Floci:Oci:TenancyId is unset or still the lab's synthetic default. Set it "
                    + "explicitly to the OCID of the compartment the queue should live in — "
                    + "FLOCI_OCI_DEFAULT_TENANCY_ID does not count, it configures the emulator.");
            }

            return new QueueAdminClient(new ConfigFileAuthenticationDetailsProvider("DEFAULT"));
        }

        QueueAdminClient client = new(endpoints.AuthenticationProvider());

        return client.ForFloci(endpoints);
    }
}
