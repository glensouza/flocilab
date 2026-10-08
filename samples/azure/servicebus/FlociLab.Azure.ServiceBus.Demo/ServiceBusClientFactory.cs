using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using FlociLab.Core.Endpoints;

namespace FlociLab.Azure.ServiceBus;

/// <summary>
/// The whole of the emulator-specific wiring for this sample. Service Bus is two planes
/// (docs/BLAZOR-PLAN.md §7, floci-az docs/services/service-bus.md): entity management over plain
/// HTTP on the port Blob, Queue and Cosmos share, and an AMQP 1.0 data plane on its own port
/// (<see cref="AzureEndpoints.ServiceBusAmqpPort"/>, Artemis-backed).
///
/// <para>
/// Emulator mode cannot use <c>AzureEndpoints.Credential()</c> the way Key Vault does. Both
/// SDK client types only disable TLS and honour a custom host:port when constructed from a
/// connection string carrying <c>UseDevelopmentEmulator=true</c> — the credential-based
/// constructors always assume real Azure's TLS endpoint (confirmed by decompiling
/// <c>ServiceBusConnection</c> in Azure.Messaging.ServiceBus 7.20.2; 7.21.0's constructors are
/// unchanged). floci-az does not check the
/// <c>SharedAccessKey</c> value in this mode — "Artemis runs without authentication in dev mode"
/// per floci-az's own docs — so the placeholder key below grants nothing and is not a secret.
/// </para>
/// </summary>
public sealed class ServiceBusClientFactory(AzureEndpoints endpoints)
{
    // Real Service Bus's AMQP-over-TLS port. Fixed by the service, not configurable, and not the
    // emulator's port — floci-az's Artemis sidecar serves plain AMQP on ServiceBusAmqpPort instead.
    private const int AmqpsPort = 5671;

    /// <summary>
    /// Management-plane endpoint, for showing the wire-level request alongside the SDK call.
    /// Branches on the target for the same reason <c>KeyVaultSecretsClientFactory.ServiceUrl</c>
    /// does: the page renders this string, and every step's Request pane is built from it, so in
    /// real-cloud mode it has to name the host the SDK actually dials. Rendering the emulator's
    /// address under a "REAL Azure" banner would make the one page whose promise is showing the
    /// real request show a request that never went out.
    /// </summary>
    public string ManagementUrl => endpoints.UseEmulator
        ? $"{endpoints.BaseUri.Scheme}://{this.ManagementHostAndPort}"
        : $"https://{this.RealCloudNamespace}";

    // Bare host:port, for the connection string below — which prefixes its own "sb://" scheme, so
    // reusing ManagementUrl there would double it up into "Endpoint=sb://http://...", a host the
    // SDK cannot resolve. AmqpHost rather than BaseUri.Host: both planes need the same literal-IPv4
    // rewrite (AzureEndpoints.AmqpHost), or a "localhost" endpoint pays a dead ::1 connect attempt
    // on every management call too.
    private string ManagementHostAndPort => $"{endpoints.AmqpHost}:{endpoints.BaseUri.Port}";

    /// <summary>
    /// AMQP 1.0 data-plane endpoint — a different port from the management plane above, and in
    /// real-cloud mode a different host too (see <see cref="ManagementUrl"/> for why this branches).
    /// Real Service Bus speaks AMQP over TLS on 5671; floci-az's Artemis sidecar is plain AMQP on
    /// the configured port.
    /// </summary>
    public string AmqpEndpoint => endpoints.UseEmulator
        ? $"{endpoints.AmqpHost}:{endpoints.ServiceBusAmqpPort}"
        : $"{this.RealCloudNamespace}:{AmqpsPort}";

    /// <summary>Whether the next <see cref="CreateAdministrationClient"/>/<see cref="CreateClient"/> targets floci-az or real Azure.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>A fresh administration client per demo run — the management-plane, entity-CRUD half.</summary>
    public ServiceBusAdministrationClient CreateAdministrationClient()
    {
        if (!endpoints.UseEmulator)
        {
            return new ServiceBusAdministrationClient(this.RealCloudNamespace, endpoints.Credential());
        }

        ServiceBusAdministrationClientOptions options = new();

        // Off for the same reason every factory in this repo turns retries off: a page whose whole
        // job is to show "the emulator is down" has to say so quickly.
        options.Retry.MaxRetries = 0;

        return new ServiceBusAdministrationClient(EmulatorConnectionString(this.ManagementHostAndPort), options);
    }

    /// <summary>A fresh data-plane client per demo run — AMQP send/receive.</summary>
    public ServiceBusClient CreateClient()
    {
        if (!endpoints.UseEmulator)
        {
            return new ServiceBusClient(this.RealCloudNamespace, endpoints.Credential());
        }

        return new ServiceBusClient(
            EmulatorConnectionString(this.AmqpEndpoint),
            new ServiceBusClientOptions { RetryOptions = { MaxRetries = 0 } });
    }

    private string RealCloudNamespace => endpoints.RealCloudServiceBusNamespace
        ?? throw new InvalidOperationException(
            "Floci:Azure:UseEmulator is false but no Floci:Azure:ServiceBusNamespace was configured. "
            + "Supply the namespace host (e.g. my-namespace.servicebus.windows.net) through configuration "
            + "— this is not a secret, since real-cloud mode authenticates with a TokenCredential.");

    // "sb://" plus UseDevelopmentEmulator=true is what makes both client types disable TLS and
    // accept floci-az's plain HTTP / plain AMQP at the given host:port (plan §7).
    private static string EmulatorConnectionString(string hostAndPort)
        => $"Endpoint=sb://{hostAndPort};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=devkey;UseDevelopmentEmulator=true;";
}
