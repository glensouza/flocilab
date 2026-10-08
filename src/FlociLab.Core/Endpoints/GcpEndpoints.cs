using FlociLab.Core.Configuration;
using Microsoft.Extensions.Options;

namespace FlociLab.Core.Endpoints;

/// <summary>
/// The hard provider (docs/BLAZOR-PLAN.md §7 and the risk register in §14). Three separate
/// problems, and which one a sample hits depends on its client:
///
/// <list type="number">
///   <item>Pub/Sub, Firestore and Datastore honour <c>EmulatorDetection.EmulatorOnly</c> plus
///         <see cref="EmulatorHost"/> in PUBSUB_EMULATOR_HOST / FIRESTORE_EMULATOR_HOST.</item>
///   <item><c>Google.Cloud.Storage.V1</c> is REST/JSON, and it is the easy one — settled in
///         Phase 1 against 4.15.0 and unchanged on 5.0.0. Use <see cref="StorageBaseUri"/> with
///         <c>StorageClientBuilder { BaseUri, UnauthenticatedAccess = true }</c>. There is no
///         HttpClient fallback to budget for, and STORAGE_EMULATOR_HOST is not ignored either:
///         the builder carries an <c>EmulatorDetection</c> property, and <c>EmulatorOnly</c> plus
///         that variable also reaches the emulator. Samples take the <see cref="StorageBaseUri"/>
///         route because a web app binding its endpoint from configuration should not depend on a
///         process-wide environment variable. This is the one Google service with no gRPC in its
///         dependency tree, which is why it dodges problems 1 and 3 — do not read its easiness as
///         a forecast for Pub/Sub or Firestore.</item>
///   <item>Everything is multiplexed on one port over HTTP/2 ALPN, so gRPC clients need
///         <c>ChannelCredentials.Insecure</c> against <see cref="GrpcTarget"/>.</item>
/// </list>
/// </summary>
public sealed class GcpEndpoints(IOptions<FlociOptions> options)
{
    private readonly GcpEmulatorOptions emulatorOptions = options.Value.Gcp;

    /// <summary>
    /// False targets real Google Cloud: the factory drops <c>BaseUri</c> and
    /// <c>UnauthenticatedAccess</c> and lets the client use Application Default Credentials.
    /// </summary>
    public bool UseEmulator => this.emulatorOptions.UseEmulator;

    public Uri BaseUri => new(this.emulatorOptions.Endpoint);

    public string ProjectId => this.emulatorOptions.ProjectId;

    /// <summary>host:port form, for the *_EMULATOR_HOST environment variables and gRPC channels.</summary>
    public string EmulatorHost => $"{this.BaseUri.Host}:{this.BaseUri.Port}";

    /// <summary>Same host:port — named separately because gRPC clients call it a target.</summary>
    public string GrpcTarget => this.EmulatorHost;

    /// <summary>
    /// Base for the JSON API. Trailing slash is required: the SDK appends relative paths to it.
    /// </summary>
    public string StorageBaseUri => $"{this.emulatorOptions.Endpoint.TrimEnd('/')}/storage/v1/";
}
