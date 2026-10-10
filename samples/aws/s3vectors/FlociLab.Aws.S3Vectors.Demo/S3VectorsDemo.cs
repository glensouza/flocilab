using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.Runtime.Documents;
using Amazon.S3Vectors;
using Amazon.S3Vectors.Model;
using FlociLab.Core;

namespace FlociLab.Aws.S3Vectors;

/// <summary>
/// Creates a vector bucket and a three-dimensional cosine index, puts four vectors with a genre in
/// their metadata, reads them back and lists them, asks for the two nearest to a query vector, asks
/// again with a metadata filter, deletes one, then tries a vector of the wrong dimension, a
/// duplicate index and a bucket policy. Ordinary AWSSDK.S3Vectors code — the only emulator wiring is
/// in <see cref="S3VectorsClientFactory"/>; this class reads <c>UseEmulator</c> only to name floci's
/// gaps where it differs from AWS. Every step reports what the engine actually answered rather than
/// what the docs promise.
/// </summary>
public sealed class S3VectorsDemo(S3VectorsClientFactory factory) : IServiceDemo
{
    private const string IndexName = "films";

    private const int Dimension = 3;

    public string Provider => CloudProvider.Aws;

    public string Slug => "s3vectors";

    public string DisplayName => "S3 Vectors";

    public string Category => "Storage, transfer and backup";

    public string Route => "/aws/s3vectors";

    /// <summary>ListVectorBuckets — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonS3Vectors client = factory.Create();
            ListVectorBucketsResponse response = await client.ListVectorBucketsAsync(new ListVectorBucketsRequest(), ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListVectorBuckets: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonS3Vectors client = factory.Create();

        string name = $"flocilab-{Guid.NewGuid().ToString("N")[..12]}";

        // Claimed before the create: a call that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created the bucket, so the finally looks it up by name
        // rather than trusting that a response arrived.
        bool claimed = false;
        string? bucketArn = null;

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep created = await RunStepAsync(
                "CreateVectorBucket, GetVectorBucket — a vector bucket",
                this.Call("CreateVectorBucket", $"client.CreateVectorBucketAsync(new CreateVectorBucketRequest {{ VectorBucketName = \"{name}\" }})\nclient.GetVectorBucketAsync(new GetVectorBucketRequest {{ VectorBucketName = \"{name}\" }})"),
                async () =>
                {
                    claimed = true;
                    CreateVectorBucketResponse response = await client.CreateVectorBucketAsync(new CreateVectorBucketRequest { VectorBucketName = name }, ct).ConfigureAwait(false);

                    bucketArn = response.VectorBucketArn;

                    GetVectorBucketResponse found = await client.GetVectorBucketAsync(new GetVectorBucketRequest { VectorBucketName = name }, ct).ConfigureAwait(false);

                    return $"{found.VectorBucket.VectorBucketName}\n{bucketArn}";
                }).ConfigureAwait(false);

            yield return created;

            // A failed create means every later step would look for a bucket that is not there.
            // Skipped rather than `yield break`, which would also skip yielding the cleanup step below.
            if (created.Succeeded)
            {
                DemoStep index = await RunStepAsync(
                    "CreateIndex, GetIndex, ListIndexes — a three-dimensional cosine index",
                    this.Call("CreateIndex", $"client.CreateIndexAsync(new CreateIndexRequest {{ VectorBucketName = bucket, IndexName = \"{IndexName}\", DataType = DataType.Float32, Dimension = {Dimension}, DistanceMetric = DistanceMetric.Cosine }})\nclient.GetIndexAsync(new GetIndexRequest {{ ... }})\nclient.ListIndexesAsync(new ListIndexesRequest {{ VectorBucketName = bucket }})"),
                    async () =>
                    {
                        CreateIndexResponse response = await client.CreateIndexAsync(
                            new CreateIndexRequest { VectorBucketName = name, IndexName = IndexName, DataType = DataType.Float32, Dimension = Dimension, DistanceMetric = DistanceMetric.Cosine }, ct).ConfigureAwait(false);

                        GetIndexResponse found = await client.GetIndexAsync(new GetIndexRequest { VectorBucketName = name, IndexName = IndexName }, ct).ConfigureAwait(false);
                        List<IndexSummary> listed = await ListIndexesAsync(client, name, ct).ConfigureAwait(false);

                        if (!listed.Any(i => i.IndexName == IndexName))
                        {
                            throw new InvalidOperationException($"the index was accepted but ListIndexes lists {listed.Count} index(es) without it.");
                        }

                        return $"{found.Index.IndexName} ({found.Index.DataType}, {found.Index.Dimension} dimensions, {found.Index.DistanceMetric})\n{response.IndexArn}\nListIndexes lists {listed.Count}";
                    }).ConfigureAwait(false);

                yield return index;

                if (index.Succeeded)
                {
                    yield return await RunStepAsync(
                        "PutVectors — four films as points in space",
                        this.Call("PutVectors", $"client.PutVectorsAsync(new PutVectorsRequest {{ VectorBucketName = bucket, IndexName = \"{IndexName}\",\n    Vectors = [new PutInputVector {{ Key = \"alien\", Data = new VectorData {{ Float32 = [1f, 0f, 0f] }}, Metadata = Document({{ genre: \"scifi\" }}) }}, ...] }})"),
                        async () =>
                        {
                            await client.PutVectorsAsync(
                                new PutVectorsRequest
                                {
                                    VectorBucketName = name,
                                    IndexName = IndexName,
                                    Vectors =
                                    [
                                        Film("alien", [1f, 0f, 0f], "scifi"),
                                        Film("blade-runner", [0.9f, 0.3f, 0f], "scifi"),
                                        Film("amelie", [0f, 1f, 0f], "romance"),
                                        Film("notting-hill", [0f, 0.8f, 0.4f], "romance"),
                                    ],
                                }, ct).ConfigureAwait(false);

                            return "4 vectors stored";
                        }).ConfigureAwait(false);

                    yield return await RunStepAsync(
                        "GetVectors, ListVectors — reading them back",
                        this.Call("GetVectors", $"client.GetVectorsAsync(new GetVectorsRequest {{ ..., Keys = [\"alien\"], ReturnData = true, ReturnMetadata = true }})\nclient.ListVectorsAsync(new ListVectorsRequest {{ VectorBucketName = bucket, IndexName = \"{IndexName}\" }})"),
                        async () =>
                        {
                            List<GetOutputVector> got = (await client.GetVectorsAsync(
                                new GetVectorsRequest { VectorBucketName = name, IndexName = IndexName, Keys = ["alien"], ReturnData = true, ReturnMetadata = true }, ct).ConfigureAwait(false)).Vectors ?? [];

                            if (got.Count != 1)
                            {
                                throw new InvalidOperationException($"GetVectors for one key returned {got.Count} vector(s).");
                            }

                            List<ListOutputVector> listed = await ListVectorsAsync(client, name, ct).ConfigureAwait(false);

                            return $"{got[0].Key}: [{string.Join(", ", (got[0].Data?.Float32 ?? []).Select(f => f.ToString(CultureInfo.InvariantCulture)))}]\nListVectors lists {listed.Count}: {string.Join(", ", listed.Select(v => v.Key).Order(StringComparer.Ordinal))}";
                        }).ConfigureAwait(false);

                    yield return await RunStepAsync(
                        "QueryVectors — the two nearest to [1, 0.1, 0]",
                        this.Call("QueryVectors", $"client.QueryVectorsAsync(new QueryVectorsRequest {{ VectorBucketName = bucket, IndexName = \"{IndexName}\", TopK = 2,\n    QueryVector = new VectorData {{ Float32 = [1f, 0.1f, 0f] }}, ReturnDistance = true, ReturnMetadata = true }})"),
                        async () =>
                        {
                            QueryVectorsResponse response = await client.QueryVectorsAsync(
                                new QueryVectorsRequest
                                {
                                    VectorBucketName = name,
                                    IndexName = IndexName,
                                    TopK = 2,
                                    QueryVector = new VectorData { Float32 = [1f, 0.1f, 0f] },
                                    ReturnDistance = true,
                                    ReturnMetadata = true,
                                }, ct).ConfigureAwait(false);

                            return Describe(response.Vectors ?? []);
                        }).ConfigureAwait(false);

                    yield return await RunStepAsync(
                        "QueryVectors — the same query, filtered to romance",
                        this.Call("QueryVectors", "client.QueryVectorsAsync(new QueryVectorsRequest { ..., TopK = 2, Filter = Document({ genre: \"romance\" }) })"),
                        async () =>
                        {
                            QueryVectorsResponse response = await client.QueryVectorsAsync(
                                new QueryVectorsRequest
                                {
                                    VectorBucketName = name,
                                    IndexName = IndexName,
                                    TopK = 2,
                                    QueryVector = new VectorData { Float32 = [1f, 0.1f, 0f] },
                                    Filter = new Document(new Dictionary<string, Document> { ["genre"] = "romance" }),
                                    ReturnDistance = true,
                                    ReturnMetadata = true,
                                }, ct).ConfigureAwait(false);

                            return Describe(response.Vectors ?? []);
                        }).ConfigureAwait(false);

                    yield return await RunStepAsync(
                        "DeleteVectors — removing one",
                        this.Call("DeleteVectors", $"client.DeleteVectorsAsync(new DeleteVectorsRequest {{ VectorBucketName = bucket, IndexName = \"{IndexName}\", Keys = [\"alien\"] }})"),
                        async () =>
                        {
                            await client.DeleteVectorsAsync(new DeleteVectorsRequest { VectorBucketName = name, IndexName = IndexName, Keys = ["alien"] }, ct).ConfigureAwait(false);

                            List<ListOutputVector> listed = await ListVectorsAsync(client, name, ct).ConfigureAwait(false);

                            if (listed.Any(v => v.Key == "alien"))
                            {
                                throw new InvalidOperationException("the vector was deleted but ListVectors still lists it.");
                            }

                            return $"ListVectors lists {listed.Count}: {string.Join(", ", listed.Select(v => v.Key).Order(StringComparer.Ordinal))}";
                        }).ConfigureAwait(false);

                    yield return await RunStepAsync(
                        "PutVectors — two dimensions into a three-dimensional index",
                        this.Call("PutVectors", "client.PutVectorsAsync(new PutVectorsRequest { ..., Vectors = [new PutInputVector { Key = \"flat\", Data = new VectorData { Float32 = [1f, 0f] } }] })"),
                        async () =>
                        {
                            try
                            {
                                await client.PutVectorsAsync(
                                    new PutVectorsRequest
                                    {
                                        VectorBucketName = name,
                                        IndexName = IndexName,
                                        Vectors = [new PutInputVector { Key = "flat", Data = new VectorData { Float32 = [1f, 0f] } }],
                                    }, ct).ConfigureAwait(false);

                                return factory.UseEmulator
                                    ? "accepted\nreal S3 Vectors rejects a vector whose dimension is not the index's; floci has started to accept it"
                                    : "accepted";
                            }
                            // What real S3 Vectors answers, and floci too: the expected outcome, shown as a successful step.
                            catch (ValidationException ex)
                            {
                                return $"{ex.ErrorCode}: {ex.Message}";
                            }
                        }).ConfigureAwait(false);

                    yield return await RunStepAsync(
                        "CreateIndex — a name that is taken",
                        this.Call("CreateIndex", $"client.CreateIndexAsync(new CreateIndexRequest {{ ..., IndexName = \"{IndexName}\", DataType = DataType.Float32, Dimension = {Dimension}, DistanceMetric = DistanceMetric.Cosine }})"),
                        async () =>
                        {
                            try
                            {
                                await client.CreateIndexAsync(
                                    new CreateIndexRequest { VectorBucketName = name, IndexName = IndexName, DataType = DataType.Float32, Dimension = Dimension, DistanceMetric = DistanceMetric.Cosine }, ct).ConfigureAwait(false);

                                return factory.UseEmulator
                                    ? "accepted\nreal S3 Vectors answers ConflictException for an index that already exists; floci has started to accept it"
                                    : "accepted";
                            }
                            catch (ConflictException ex)
                            {
                                return $"{ex.ErrorCode}: {ex.Message}";
                            }
                        }).ConfigureAwait(false);
                }

                yield return await RunStepAsync(
                    "PutVectorBucketPolicy — a bucket policy",
                    this.Call("PutVectorBucketPolicy", "client.PutVectorBucketPolicyAsync(new PutVectorBucketPolicyRequest { VectorBucketName = bucket, Policy = json })"),
                    async () =>
                    {
                        // Without the bucket's ARN there is no account to name, and a guessed principal could
                        // grant more than intended: refuse rather than send one.
                        if (bucketArn is null || AccountRoot(bucketArn) is not string root)
                        {
                            throw new InvalidOperationException($"CreateVectorBucket answered no usable ARN ({bucketArn ?? "none"}), so there is no account to write a policy for.");
                        }

                        // A policy that allows nothing new: the account's own root may read the bucket, which
                        // it can already do.
                        string policy = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":\"" + root + "\"},\"Action\":\"s3vectors:GetVectorBucket\",\"Resource\":\"" + bucketArn + "\"}]}";

                        try
                        {
                            await client.PutVectorBucketPolicyAsync(new PutVectorBucketPolicyRequest { VectorBucketName = name, Policy = policy }, ct).ConfigureAwait(false);

                            return factory.UseEmulator
                                ? "stored\nfloci now implements bucket policies: delete this step's tripwire and add Get and DeleteVectorBucketPolicy"
                                : "stored";
                        }
                        // Not a 501: floci answers an operation it has not built with HTTP 404 and UnknownOperationException.
                        catch (AmazonS3VectorsException ex) when (factory.UseEmulator && IsNotImplemented(ex))
                        {
                            return $"HTTP {(int)ex.StatusCode} {ex.ErrorCode}: {ex.Message}\nreal S3 Vectors takes a resource policy on a vector bucket; floci has not built the policy operations";
                        }
                    }).ConfigureAwait(false);
            }
        }
        finally
        {
            // Runs whether the steps above succeeded, failed, or the consumer stopped enumerating.
            // An iterator may not yield from a finally, so the step is yielded below.
            if (claimed)
            {
                cleanup.Add(await this.DeleteAsync(client, name, ct).ConfigureAwait(false));
            }
        }

        foreach (DemoStep step in cleanup)
        {
            yield return step;
        }
    }

    /// <summary>
    /// The AWS SDK reports a not-implemented operation inside an <see cref="AmazonServiceException"/>,
    /// so <see cref="ProbeResult.FromException"/> — which inspects only the outermost exception —
    /// cannot classify it on its own. floci answers an operation it has not built with HTTP 404 and
    /// the error code <c>UnknownOperationException</c>: that is the not-implemented outcome here.
    /// </summary>
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AmazonServiceException { StatusCode: HttpStatusCode.NotImplemented }:
                case AmazonServiceException { ErrorCode: "UnknownOperationException" }:
                    return ProbeResult.NotImplemented(Describe(ex), elapsed);

                case SocketException or TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return ProbeResult.Unreachable(Describe(ex), elapsed);

                // A status code means the emulator answered, so this is it behaving badly rather
                // than being absent. Stop unwrapping and report the error.
                case AmazonServiceException { StatusCode: not 0 }:
                    return ProbeResult.Error(Describe(ex), elapsed);
            }
        }

        return ProbeResult.Error(Describe(ex), elapsed);
    }

    private static bool IsNotImplemented(AmazonServiceException ex)
        => ex.StatusCode == HttpStatusCode.NotImplemented || ex.ErrorCode == "UnknownOperationException";

    /// <summary>The account root inside <c>arn:aws:s3vectors:region:account:bucket/name</c>, as an IAM principal; null when there is no account in it.</summary>
    private static string? AccountRoot(string arn)
        => arn.Split(':') is { Length: > 4 } parts && parts[4].Length > 0 ? $"arn:aws:iam::{parts[4]}:root" : null;

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

    private static PutInputVector Film(string key, List<float> data, string genre)
        => new()
        {
            Key = key,
            Data = new VectorData { Float32 = data },
            Metadata = new Document(new Dictionary<string, Document> { ["genre"] = genre }),
        };

    /// <summary>
    /// One line per hit, nearest first: the key, its genre and its cosine distance (0 is identical).
    /// A hit that came back without a genre or a distance says so rather than throwing.
    /// </summary>
    private static string Describe(List<QueryOutputVector> hits)
        => string.Join("\n", hits.Select(h => $"{h.Key} ({Genre(h.Metadata)}): distance {h.Distance?.ToString("0.000", CultureInfo.InvariantCulture) ?? "(none)"}"));

    private static string Genre(Document metadata)
        => metadata.IsDictionary() && metadata.AsDictionary().TryGetValue("genre", out Document genre) && genre.IsString()
            ? genre.AsString()
            : "no genre";

    /// <summary>
    /// Runs one operation and turns it into a <see cref="DemoStep"/>. Nothing is swallowed: a
    /// failure becomes a step carrying the error text.
    /// </summary>
    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        // Cancellation is the consumer giving up, not a step that failed. Letting it propagate
        // stops the run at the step it reached, and RunAsync's finally still deletes the bucket.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    /// <summary>The vector buckets of that name; names are unique per run, so more than one means a lost create was retried.</summary>
    private static async Task<List<VectorBucketSummary>> FindAsync(IAmazonS3Vectors client, string name, CancellationToken ct)
    {
        List<VectorBucketSummary> found = [];
        string? next = null;

        do
        {
            ListVectorBucketsResponse page = await client.ListVectorBucketsAsync(new ListVectorBucketsRequest { Prefix = name, NextToken = next }, ct).ConfigureAwait(false);

            // Matched by name, not trusted to the prefix: floci ignores Prefix and answers every bucket.
            found.AddRange((page.VectorBuckets ?? []).Where(b => b.VectorBucketName == name));
            next = page.NextToken;
        }
        while (!string.IsNullOrEmpty(next));

        return found;
    }

    /// <summary>Every index in the bucket, across every page.</summary>
    private static async Task<List<IndexSummary>> ListIndexesAsync(IAmazonS3Vectors client, string name, CancellationToken ct)
    {
        List<IndexSummary> found = [];
        string? next = null;

        do
        {
            ListIndexesResponse page = await client.ListIndexesAsync(new ListIndexesRequest { VectorBucketName = name, NextToken = next }, ct).ConfigureAwait(false);

            found.AddRange(page.Indexes ?? []);
            next = page.NextToken;
        }
        while (!string.IsNullOrEmpty(next));

        return found;
    }

    /// <summary>Every vector in the sample's index, across every page.</summary>
    private static async Task<List<ListOutputVector>> ListVectorsAsync(IAmazonS3Vectors client, string name, CancellationToken ct)
    {
        List<ListOutputVector> found = [];
        string? next = null;

        do
        {
            ListVectorsResponse page = await client.ListVectorsAsync(new ListVectorsRequest { VectorBucketName = name, IndexName = IndexName, NextToken = next }, ct).ConfigureAwait(false);

            found.AddRange(page.Vectors ?? []);
            next = page.NextToken;
        }
        while (!string.IsNullOrEmpty(next));

        return found;
    }

    /// <summary>The wire-level request shown beside an SDK call: REST JSON, a POST to one path per operation.</summary>
    private string Call(string operation, string call) => $"POST {factory.ServiceUrl}/{operation}\n{call}";

    /// <summary>
    /// Deletes whatever this run created, looking the bucket up by its unique name so one whose
    /// create response was lost is still found, and one that was never created reads as a truthful
    /// "nothing to remove". Indexes go first, because real S3 Vectors refuses to delete a bucket
    /// that still holds one. Uses <see cref="CancellationToken.None"/>: a cancelled run still has a
    /// bucket to remove.
    /// </summary>
    private async Task<DemoStep> DeleteAsync(IAmazonS3Vectors client, string name, CancellationToken ct)
    {
        string request = this.Call("DeleteVectorBucket", "client.DeleteIndexAsync, DeleteVectorBucketAsync");

        return await RunStepAsync("DeleteVectorBucket — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<VectorBucketSummary> found = await FindAsync(client, name, none).ConfigureAwait(false);

            if (found.Count == 0)
            {
                return $"No vector bucket for this run exists — nothing to remove.{cancelled}";
            }

            int indexes = 0;
            int buckets = 0;

            // One failed delete must not stop the rest: every child is tried, and what failed is
            // reported at the end, so the step reads as failed and names what was left behind.
            List<string> failed = [];

            foreach (VectorBucketSummary bucket in found)
            {
                // A failed listing is one more thing left behind, not a reason to skip the bucket:
                // its delete below still runs, and names the indexes it was refused for.
                List<IndexSummary> bucketIndexes = [];

                try
                {
                    bucketIndexes = await ListIndexesAsync(client, bucket.VectorBucketName, none).ConfigureAwait(false);
                }
                catch (AmazonServiceException ex)
                {
                    failed.Add($"indexes of {bucket.VectorBucketName}: {ex.ErrorCode}");
                }

                foreach (IndexSummary index in bucketIndexes)
                {
                    try
                    {
                        await client.DeleteIndexAsync(new DeleteIndexRequest { VectorBucketName = bucket.VectorBucketName, IndexName = index.IndexName }, none).ConfigureAwait(false);
                        indexes++;
                    }
                    catch (AmazonServiceException ex)
                    {
                        failed.Add($"index {index.IndexName}: {ex.ErrorCode}");
                    }
                }

                try
                {
                    await client.DeleteVectorBucketAsync(new DeleteVectorBucketRequest { VectorBucketName = bucket.VectorBucketName }, none).ConfigureAwait(false);
                    buckets++;
                }
                catch (AmazonServiceException ex)
                {
                    failed.Add($"vector bucket {bucket.VectorBucketName}: {ex.ErrorCode}");
                }
            }

            string deleted = $"Deleted {buckets} vector bucket(s), {indexes} index(es).";

            if (failed.Count > 0)
            {
                throw new InvalidOperationException($"{deleted} Left behind: {string.Join("; ", failed)}.{cancelled}");
            }

            return $"{deleted}{cancelled}";
        }).ConfigureAwait(false);
    }
}
