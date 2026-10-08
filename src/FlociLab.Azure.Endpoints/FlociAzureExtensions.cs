using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FlociLab.Azure;

/// <summary>
/// The credential half of plan §7. floci-az implements the IMDS token endpoint and signs real
/// v1.0 JWTs verifiable via JWKS, so samples authenticate with the same
/// <see cref="ManagedIdentityCredential"/> they would use on a real VM — no hand-rolled fake
/// <see cref="TokenCredential"/> anywhere in the repo.
/// </summary>
public static class FlociAzureExtensions
{
    /// <summary>
    /// The only supported way to point Azure.Identity at a different IMDS address. It takes a
    /// host — "http://localhost:4577" — and the credential appends
    /// /metadata/identity/oauth2/token itself. (It was called AZURE_POD_IDENTITY_TOKEN_URL and did
    /// take a full URL, which is the shape plan §7 still shows.)
    /// </summary>
    public const string PodIdentityAuthorityHostVariable = "AZURE_POD_IDENTITY_AUTHORITY_HOST";

    /// <summary>
    /// Remembers the value this method last wrote, so a later call can tell "nothing external has
    /// touched this since" from "the AppHost (or a real deployment) already set it" — see
    /// <see cref="Credential"/>. Guarded by <see cref="AuthorityHostLock"/>.
    /// </summary>
    private static string? lastSetAuthorityHost;

    /// <summary>
    /// Serialises the read-modify-write below. The environment variable and the field have to move
    /// together or two racing callers can each read a value the other is about to replace.
    /// </summary>
    private static readonly Lock AuthorityHostLock = new();

    /// <summary>
    /// A credential that gets its tokens from the emulator. Sets the environment variable if
    /// nothing has set it yet, so a standalone sample host works with no extra configuration; when
    /// the AppHost has already set it, the existing value wins.
    ///
    /// <para>
    /// The variable is process-wide, so a plain "set only if empty" guard is correct for every real
    /// host — one process ever targets one Key Vault — but breaks the moment two independently
    /// targeted <see cref="AzureEndpoints"/> instances call this in the same process. Comparing
    /// against <see cref="lastSetAuthorityHost"/> instead of checking only for emptiness lets this
    /// method update a value it wrote itself while still never touching one it did not.
    /// </para>
    ///
    /// <para>
    /// That makes <em>sequential</em> re-targeting correct; it cannot make concurrent re-targeting
    /// correct, because one process has exactly one of this variable. A test assembly is the only
    /// place in this repo that targets two emulators at once, so the two Key Vault test classes
    /// share an xUnit collection (<c>AzureKeyVaultCollection</c>) to keep them off each other —
    /// without it, one class's <c>Probe_Reports_Unreachable_When_Nothing_Is_Listening</c> points the
    /// authority host at the dead <c>127.0.0.1:1</c> while the other class is mid-token-acquisition,
    /// and that class reports <c>Unreachable</c> for a container that is up. The lock below removes
    /// the data race on the pair; the collection removes the interference the lock cannot.
    /// </para>
    /// </summary>
    public static TokenCredential Credential(this AzureEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        lock (AuthorityHostLock)
        {
            string? current = Environment.GetEnvironmentVariable(PodIdentityAuthorityHostVariable);

            // IsNullOrEmpty, not "is null": an env var exported empty (AZURE_POD_IDENTITY_AUTHORITY_HOST=,
            // the ordinary container and CI shape) reads back as "" on Linux, and leaving that in
            // place would send ManagedIdentityCredential to the real IMDS address instead of the
            // emulator's.
            if (string.IsNullOrEmpty(current) || current == lastSetAuthorityHost)
            {
                Environment.SetEnvironmentVariable(PodIdentityAuthorityHostVariable, endpoints.ImdsAuthorityHost);
                lastSetAuthorityHost = endpoints.ImdsAuthorityHost;
            }
        }

        // The parameterless and (clientId, options) constructors are obsolete in Azure.Identity
        // 1.21; the options overload is the supported way to ask for the system-assigned identity.
        return new ManagedIdentityCredential(new ManagedIdentityCredentialOptions());
    }

    /// <summary>
    /// floci-az serves every data-plane service over plain HTTP, but a bearer-token-authenticating
    /// client's own pipeline — <c>BearerTokenAuthenticationPolicy</c> and Key Vault's
    /// <c>ChallengeBasedAuthenticationPolicy</c> alike — refuses outright to attach a token to a
    /// non-<c>https</c> request: <c>if (message.Request.Uri.Scheme != Uri.UriSchemeHttps) throw new
    /// InvalidOperationException("Bearer token authentication is not permitted for non TLS
    /// protected (https) endpoints.")</c>. There is no constructor flag, settable property, or
    /// AppContext switch to disable this — confirmed by decompiling Azure.Core 1.55.0 and
    /// Azure.Security.KeyVault.Secrets 4.11.0 (re-checked on 1.62.0 and 4.11.2), whose only
    /// "insecure" string is a log message for a blocked HTTPS-to-HTTP redirect
    /// (docs/BLAZOR-PLAN.md §14) — and floci-az has no TLS port to point the client at instead.
    ///
    /// <para>
    /// This makes the check pass without ever putting a token on an unencrypted wire to anything
    /// but the local emulator: a <c>PerCall</c> policy upgrades the URI to <c>https</c> before the
    /// client's own auth policy inspects it — which covers every leg of a challenge-based 401 retry,
    /// since a <c>PerCall</c> policy wraps the whole retry loop — and a custom transport flips it
    /// back to <c>http</c> as the very last step, immediately before the real socket connects. Real
    /// Azure never takes this path: call only when <c>endpoints.UseEmulator</c> is true.
    /// </para>
    ///
    /// <para>
    /// <c>UseEmulator</c> alone is not enough to make that "local emulator" claim true, which is why
    /// <paramref name="endpoint"/> is required and must be loopback. <c>Floci:Azure:Endpoint</c> is
    /// free-form configuration, and <see cref="Credential"/> deliberately yields to an authority
    /// host somebody else set — so on a machine that already has
    /// <c>AZURE_POD_IDENTITY_AUTHORITY_HOST</c> pointing at a real IMDS (an Azure VM, an AKS pod),
    /// an emulator endpoint aimed at a non-loopback host would put a genuine managed-identity token
    /// on a cleartext wire leaving the box. That is exactly the leak the SDK's check exists to
    /// prevent, so this refuses rather than defeating it.
    /// </para>
    /// </summary>
    public static void AllowInsecureBearerToken(this ClientOptions options, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsLoopback)
        {
            throw new InvalidOperationException(
                $"Refusing to disable the SDK's TLS check for '{endpoint}'. The bearer token would leave this "
                + "machine unencrypted, and the token may be a real managed-identity token rather than one "
                + "floci-az minted. Point Floci:Azure:Endpoint at a loopback address (127.0.0.1), or set "
                + "Floci:Azure:UseEmulator to false and target a real https vault.");
        }

        options.AddPolicy(new UpgradeSchemeForAuthCheckPolicy(), HttpPipelinePosition.PerCall);
        options.Transport = new DowngradeSchemeBeforeConnectTransport(options.Transport);
    }

    /// <summary>
    /// Warms Azure.Identity's managed-identity source probe at host startup, so no demo page's
    /// timing cell carries it. See <see cref="AzureCredentialWarmup"/> for the measurement this
    /// exists to fix. Called by every host that registers an Azure sample; harmless to call twice,
    /// and harmless when floci-az is down.
    /// </summary>
    public static IServiceCollection AddFlociAzureCredentialWarmup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AzureCredentialWarmup>());

        return services;
    }

    private sealed class UpgradeSchemeForAuthCheckPolicy : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            if (message.Request.Uri.Scheme == Uri.UriSchemeHttp)
            {
                message.Request.Uri.Scheme = Uri.UriSchemeHttps;
            }
        }
    }

    private sealed class DowngradeSchemeBeforeConnectTransport(HttpPipelineTransport inner) : HttpPipelineTransport
    {
        public override Request CreateRequest() => inner.CreateRequest();

        public override void Process(HttpMessage message)
        {
            Downgrade(message);
            inner.Process(message);
        }

        public override ValueTask ProcessAsync(HttpMessage message)
        {
            Downgrade(message);

            return inner.ProcessAsync(message);
        }

        private static void Downgrade(HttpMessage message)
        {
            if (message.Request.Uri.Scheme == Uri.UriSchemeHttps)
            {
                message.Request.Uri.Scheme = Uri.UriSchemeHttp;
            }
        }
    }
}
