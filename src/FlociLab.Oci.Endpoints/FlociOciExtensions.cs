using FlociLab.Core.Endpoints;
using Oci.Common;
using Oci.Common.Auth;

namespace FlociLab.Oci;

/// <summary>
/// The two halves of plan §7 for OCI: signing, and actually reaching the emulator.
///
/// <para>
/// Signing is the easy half. The emulator parses request signatures but never verifies them, so
/// the profile only has to be well-formed — which means a sample can be handed a real
/// <see cref="IBasicAuthenticationDetailsProvider"/> built from a key generated at startup, with
/// no key material in the repo and no config file on disk.
/// </para>
///
/// <code>
/// ObjectStorageClient client = new(endpoints.AuthenticationProvider());
/// client.ForFloci(endpoints);
/// </code>
/// </summary>
public static class FlociOciExtensions
{
    public static IBasicAuthenticationDetailsProvider AuthenticationProvider(this OciEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return new SimpleAuthenticationDetailsProvider
        {
            TenantId = endpoints.TenancyId,
            UserId = endpoints.UserId,
            Fingerprint = endpoints.SigningKey.Fingerprint,
            // Takes the PEM itself, not a path — verified against 148.1.0, which parses
            // ExportPkcs8PrivateKeyPem output into BouncyCastle RsaKeyParameters.
            PrivateKeySupplier = new PrivateKeySupplier(endpoints.SigningKey.PrivateKeyPem),
            // Required: RegionalClientBase's constructor calls SetRegion(provider.Region)
            // unconditionally and NullReferences on a provider that has none.
            Region = Region.FromRegionCodeOrId(endpoints.Region),
        };
    }

    /// <summary>
    /// Points a constructed OCI client at the emulator. **Both** lines are load-bearing, and the
    /// second one is the single most important line in the OCI half of this repo.
    ///
    /// <para>
    /// Plan §7 used to say <c>client.SetEndpoint(endpoints.Endpoint)</c> was all it took. It is
    /// not, and the way it fails is genuinely dangerous. A client whose authentication provider
    /// carries a <see cref="Region"/> — which is mandatory, see above — is built with a
    /// *realm-specific endpoint template* derived from that region
    /// (<c>https://objectstorage.us-ashburn-1.{dualStack?ds.oci.:}oraclecloud.com</c>), and every
    /// operation resolves its URI from that template rather than from the endpoint. So
    /// <c>SetEndpoint</c> is silently ignored, <c>GetEndpoint()</c> keeps cheerfully reporting the
    /// emulator address you set, and the request goes to <em>real Oracle Cloud</em>. Measured on
    /// OCI.DotNetSDK 145.0.0 (the redirect is still pinned by a test on 148.1.0): a client
    /// configured for <c>http://127.0.0.1:1</c> spent 2.0 s
    /// reaching Ashburn and came back with a real 401 NotAuthenticated and a real
    /// <c>iad-1:</c>-prefixed opc-request-id, while floci-oci's own log stayed empty.
    /// </para>
    ///
    /// <para>
    /// <c>UseRealmSpecificEndpointTemplate(false)</c> does not help — it toggles a flag read
    /// elsewhere and leaves the template populated. Overwriting the template is what works.
    /// Passing the emulator's own address rather than <c>null</c> keeps the two settings agreeing
    /// with each other, so whichever one a future SDK version reads, it reads the emulator.
    /// </para>
    /// </summary>
    public static TClient ForFloci<TClient>(this TClient client, OciEndpoints endpoints) where TClient : ClientBase
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoints);

        client.SetEndpoint(endpoints.Endpoint);
        client.SetRealmSpecificEndpointTemplate(endpoints.Endpoint);

        return client;
    }
}
