using FlociLab.Core.Endpoints;
using Google.Cloud.Storage.V1;

namespace FlociLab.Gcp.Storage;

/// <summary>
/// The whole of the emulator-specific wiring for this sample, and a pleasant surprise: GCP is the
/// provider plan §7 calls hardest and §14 lists as the top risk, but Cloud Storage is the one
/// Google service that never touches gRPC. Two properties on the builder and it is done.
/// </summary>
public sealed class StorageClientFactory(GcpEndpoints endpoints) : IDisposable
{
    private readonly Lock @lock = new();

    private StorageClient? client;

    /// <summary>JSON API base, for showing the wire-level request alongside the SDK call.</summary>
    public string BaseUri => endpoints.StorageBaseUri;

    /// <summary>
    /// Where the SDK sends object *bytes*, which is not <see cref="BaseUri"/> — see
    /// <c>StorageDemo</c>'s upload step for why that distinction is worth showing.
    /// </summary>
    public string UploadUri => this.BaseUri.Replace("/storage/v1/", "/upload/storage/v1/", StringComparison.Ordinal);

    /// <summary>The project buckets are created in and listed under. Never validated by floci-gcp.</summary>
    public string ProjectId => endpoints.ProjectId;

    /// <summary>Whether the next <see cref="Create"/> targets floci-gcp or real Google Cloud.</summary>
    public bool UseEmulator => endpoints.UseEmulator;

    /// <summary>
    /// One client for the process, built on first use — which is what production would do, and
    /// now what this does too.
    ///
    /// <para>
    /// It used to hand back a fresh client per call, on the theory that a page re-run after the
    /// endpoint configuration changed wants a new one. That theory was already false: the
    /// endpoints are singletons over <c>IOptions&lt;FlociOptions&gt;</c>, which is a snapshot
    /// taken at startup and never reloaded, so no run can ever see different configuration than
    /// the one before it.
    /// </para>
    ///
    /// <para>
    /// It was also expensive. A new <see cref="StorageClient"/> brings a new connection pool, and
    /// a new pool pays the loopback connect cost described on <c>EmulatorOptions.Endpoint</c> —
    /// so the comparison page billed GCS ~2 s for every single operation while S3, whose SDK
    /// pools one handler, paid it once. <see cref="StorageClient"/> is thread-safe, so one shared
    /// instance is safe for the four providers the comparison page runs concurrently.
    /// </para>
    /// </summary>
    public StorageClient Create()
    {
        // Always under the lock rather than double-checked: this is a handful of nanoseconds
        // against a call that is about to cross the network, and the obvious version cannot be
        // subtly wrong about the memory model.
        lock (this.@lock)
        {
            return this.client ??= this.Build();
        }
    }

    /// <summary>Disposes the shared client. Called by the container — the factory is a singleton.</summary>
    public void Dispose()
    {
        lock (this.@lock)
        {
            this.client?.Dispose();
            this.client = null;
        }
    }

    private StorageClient Build()
    {
        // Real Google Cloud, and deliberately not a variant of the builder below with the endpoint
        // blanked out: both emulator lines are actively wrong here. UnauthenticatedAccess would
        // stop the client looking for the credentials it now genuinely needs, and BaseUri would
        // send it somewhere that is not Google. Everything downstream — StorageDemo, GcsObjectStore
        // — is reached identically either way, which is the claim the series makes out loud and
        // this branch is what makes it checkable rather than just asserted.
        if (!endpoints.UseEmulator)
        {
            return StorageClient.Create();
        }

        return new StorageClientBuilder
        {
            // The risk register's headline worry — that Google.Cloud.Storage.V1 would ignore a
            // custom BaseUri and there would be no way to reach the emulator short of hand-rolling
            // an HttpClient over the JSON API. It does not: verified end to end on 4.15.0 against
            // floci-gcp 0.7.0, and again on 5.0.0 against 0.10.0 (2026-10-08): create, upload,
            // list, download and delete all land. The fallback in §14 is not needed and that row
            // can close.
            //
            // The trailing slash matters. The SDK appends relative paths ("b", "b/{bucket}/o") to
            // this, so dropping it addresses /storage/b instead of /storage/v1/b.
            BaseUri = endpoints.StorageBaseUri,

            // No credentials anywhere in the emulator, and this is what stops the SDK looking for
            // them. Without it the builder walks the ADC chain — GOOGLE_APPLICATION_CREDENTIALS,
            // then gcloud's well-known file, then the GCE metadata server — and fails on a
            // developer machine that has never seen gcloud, with an error about credentials rather
            // than about the endpoint. It is also the honest description of the emulator: it does
            // not check. Against real GCP this line is not redundant, it is wrong — see above.
            UnauthenticatedAccess = true,

            // Deliberately NOT setting EmulatorDetection. It does work here — see the class remarks
            // on StorageDemo — but it is driven by a process-wide environment variable, and a web
            // app that binds its endpoint from configuration should not be reaching for one.
        }.Build();
    }

    // No retry knob, which is the one place this sample departs from its S3 and Blob siblings.
    // Both of those turn retries off (MaxErrorRetry / MaxRetries = 0) so a page whose job is to
    // show "the emulator is down" says so promptly and the request shown beside each step is the
    // only one that went out. Google.Apis' handler already behaves that way for a refused
    // connection: measured against a dead port, NumTries = 1 and the default are the same ~2.0 s,
    // and that 2.0 s is the operating system's own connect timeout — a raw HttpClient to the same
    // port costs exactly as much. There is nothing to turn off.
}
