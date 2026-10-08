using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using FlociLab.Core;
using Google;
using Google.Api.Gax;
using Google.Cloud.Storage.V1;
using GcsBucket = Google.Apis.Storage.v1.Data.Bucket;

// Google.Apis.Storage.v1.Data.Object is the JSON API's name for a stored object, and it hides
// System.Object anywhere it is in scope unqualified. Aliasing it is the difference between the
// rest of this file reading like ordinary C# and it being littered with full type names.
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace FlociLab.Gcp.Storage;

/// <summary>
/// Google Cloud Storage against floci-gcp. Ordinary Google.Cloud.Storage.V1 code — the only
/// emulator-aware lines in the sample are in <see cref="StorageClientFactory"/>.
///
/// <para>
/// Two things here are unlike the S3 and Blob samples, and they are why this page is worth
/// watching rather than reading. First, the SDK returns <em>resources</em>, not responses: there
/// is no status code to print, because a call either hands back a populated <c>Bucket</c> or
/// <c>Object</c> or it throws. Second, an upload is not one request. GCS defaults to a resumable
/// upload — a POST to <c>/upload/storage/v1/</c> that reserves an upload session, then a PUT of
/// the bytes to the URL it hands back — against a base path that is not the JSON API's. Both
/// steps below say so.
/// </para>
///
/// <para>
/// Worth recording against plan §7, which says this client ignores STORAGE_EMULATOR_HOST: on
/// 5.0.0 it does not. <c>StorageClientBuilder</c> carries an <c>EmulatorDetection</c> property,
/// and <c>EmulatorOnly</c> plus that variable reaches floci-gcp on all three host spellings.
/// The sample still uses <c>BaseUri</c> — see the factory — but the claim is stale.
/// </para>
/// </summary>
public sealed class StorageDemo(StorageClientFactory factory) : IServiceDemo
{
    private const string ObjectName = "hello/floci.txt";
    private const string ObjectBody = "Hello from FlociLab.";
    private const string ObjectContentType = "text/plain";

    public string Provider => CloudProvider.Gcp;

    public string Slug => "storage";

    public string DisplayName => "Cloud Storage";

    public string Category => "Storage";

    public string Route => "/gcp/storage";

    /// <summary>
    /// ListBuckets, bounded to one page — the cheapest call that proves the JSON API is answering.
    /// The paged enumerable is lazy, so it has to be read before anything goes over the wire.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            StorageClient client = factory.Create();
            Page<GcsBucket> page = await client.ListBucketsAsync(factory.ProjectId)
                .ReadPageAsync(10, ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListBuckets returned {page.Count()} bucket(s).");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        StorageClient client = factory.Create();

        // Unique per run, so two runs never collide and a leftover bucket from a crashed run never
        // makes the next one fail. 24 chars, inside GCS's 3-63 lowercase-and-hyphens rule.
        string bucket = $"flocilab-gcs-{Guid.NewGuid():N}"[..24];
        bool created = false;

        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync(
                "ListBuckets — before",
                $"GET {factory.BaseUri}b?project={factory.ProjectId}\nstorage.ListBucketsAsync(\"{factory.ProjectId}\")",
                async () =>
                {
                    Page<GcsBucket> page = await client.ListBucketsAsync(factory.ProjectId)
                        .ReadPageAsync(100, ct).ConfigureAwait(false);
                    IEnumerable<string> names = page.Select(b => $"  {b.Name}");

                    return $"{page.Count()} bucket(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "CreateBucket",
                $"POST {factory.BaseUri}b?project={factory.ProjectId}\nContent-Type: application/json\n\n{{ \"name\": \"{bucket}\" }}",
                async () =>
                {
                    // Set before the call, not after: if the POST lands but the response does not
                    // come back, the bucket exists and cleanup has to know about it. Cleanup
                    // treats an absent bucket as a no-op, so claiming it early is free.
                    created = true;
                    GcsBucket response = await client.CreateBucketAsync(factory.ProjectId, bucket, cancellationToken: ct)
                        .ConfigureAwait(false);

                    // No status code to report — the SDK either returns the resource or throws.
                    return $"Bucket {response.Name}\n"
                        + $"  location:     {response.Location}\n"
                        + $"  storageClass: {response.StorageClass}\n"
                        + $"  timeCreated:  {response.TimeCreatedDateTimeOffset:O}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "UploadObject",
                // Two requests, and a base path that is not the JSON API's. This is what the SDK
                // actually sends; a single PUT — the shape S3 and Blob use — is not how GCS
                // uploads.
                $"POST {factory.UploadUri}b/{bucket}/o?uploadType=resumable&name={ObjectName}\n"
                    + "  -> 200, Location: ...&upload_id=...\n"
                    + $"PUT  {factory.UploadUri}b/{bucket}/o?uploadType=resumable&upload_id=...\n"
                    + $"Content-Type: {ObjectContentType}\n\n{ObjectBody}",
                async () =>
                {
                    using MemoryStream body = new(Encoding.UTF8.GetBytes(ObjectBody));
                    GcsObject response = await client.UploadObjectAsync(
                        bucket, ObjectName, ObjectContentType, body, cancellationToken: ct).ConfigureAwait(false);

                    return $"Object {response.Name}\n"
                        + $"  size:       {response.Size} bytes\n"
                        + $"  generation: {response.Generation}\n"
                        + $"  md5Hash:    {response.Md5Hash}\n"
                        + $"  crc32c:     {response.Crc32c}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "ListObjects",
                $"GET {factory.BaseUri}b/{bucket}/o\nstorage.ListObjectsAsync(\"{bucket}\", null)",
                async () =>
                {
                    Page<GcsObject> page = await client.ListObjectsAsync(bucket, prefix: null)
                        .ReadPageAsync(100, ct).ConfigureAwait(false);
                    IEnumerable<string> names = page.Select(o => $"  {o.Name} ({o.Size} bytes)");

                    return $"{page.Count()} object(s)\n" + string.Join('\n', names);
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DownloadObject",
                // The slash in the name is percent-encoded: GCS has no directories, so "hello/" is
                // part of the name rather than a path segment, and the SDK escapes it accordingly.
                $"GET {factory.BaseUri}b/{bucket}/o/{Uri.EscapeDataString(ObjectName)}?alt=media\n"
                    + $"storage.DownloadObjectAsync(\"{bucket}\", \"{ObjectName}\", destination)",
                async () =>
                {
                    // The SDK writes into a stream the caller owns rather than handing one back.
                    using MemoryStream destination = new();
                    await client.DownloadObjectAsync(bucket, ObjectName, destination, cancellationToken: ct)
                        .ConfigureAwait(false);

                    return $"{destination.Length} bytes\n\n{Encoding.UTF8.GetString(destination.ToArray())}";
                }).ConfigureAwait(false);

            yield return await RunStepAsync(
                "DeleteObject",
                $"DELETE {factory.BaseUri}b/{bucket}/o/{Uri.EscapeDataString(ObjectName)}\n"
                    + $"storage.DeleteObjectAsync(\"{bucket}\", \"{ObjectName}\")",
                async () =>
                {
                    await client.DeleteObjectAsync(bucket, ObjectName, cancellationToken: ct).ConfigureAwait(false);

                    // A 204 with no body, so there is genuinely nothing to echo back.
                    return "204 No Content — the object is gone.";
                }).ConfigureAwait(false);
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating,
            // so a re-run always starts from a clean project. The step it produces is yielded
            // below — an iterator may not yield from inside a finally.
            cleanup = created ? await this.DeleteBucketAsync(client, bucket, ct).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    /// <summary>
    /// <see cref="ProbeResult.FromException"/> handles the transport cases but cannot see a status
    /// code hiding inside a <see cref="GoogleApiException"/>, which is where this SDK puts every
    /// answer the server gave. A refused connection is the other shape: it arrives as a plain
    /// <see cref="HttpRequestException"/> with no status and a <see cref="SocketException"/> under
    /// it, unwrapped — the Google stack does not bury transport failures inside its own exception
    /// type the way the AWS SDK does.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case GoogleApiException { HttpStatusCode: HttpStatusCode.NotImplemented }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case GoogleApiException:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text, which is what keeps the page honest when
    /// the emulator does something real GCS would not.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still removes the bucket.
        // Catching it here would instead fabricate a "Failed" step for every remaining operation,
        // reporting the user navigating away as the emulator misbehaving.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    /// <summary>
    /// Cleanup. Real GCS agrees with S3 and disagrees with Azure here: deleting a bucket that
    /// still holds objects is a 409, so every object goes first. The calls use
    /// <see cref="CancellationToken.None"/> — a run that was cancelled still has a bucket to
    /// remove.
    ///
    /// <para>
    /// floci-gcp enforces the rule since 0.9.0; through 0.8.0 it answered 204 to a non-empty
    /// bucket delete and left the objects readable as orphans, so this loop was the only thing
    /// standing between a copied one-call delete and a 409 the first time it met Google
    /// (docs/BLAZOR-PLAN.md §14).
    /// </para>
    /// </summary>
    private async Task<DemoStep> DeleteBucketAsync(StorageClient client, string bucket, CancellationToken ct)
    {
        string request = $"DELETE {factory.BaseUri}b/{bucket}\nstorage.DeleteBucketAsync(\"{bucket}\")";

        return await RunStepAsync("DeleteBucket — cleanup", request, async () =>
        {
            int removed = 0;

            try
            {
                await foreach (GcsObject stored in client.ListObjectsAsync(bucket, prefix: null)
                    .WithCancellation(CancellationToken.None).ConfigureAwait(false))
                {
                    await client.DeleteObjectAsync(bucket, stored.Name, cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                    removed++;
                }
            }
            // CreateBucket claims the name before it calls, so the bucket may never have been
            // made — that is a clean run to finish, not a cleanup failure worth showing in red.
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                return "Nothing to remove — the bucket was never created.";
            }

            await client.DeleteBucketAsync(bucket, cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return $"204 No Content — removed {removed} object(s) and the bucket"
                + (ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
    }
}
