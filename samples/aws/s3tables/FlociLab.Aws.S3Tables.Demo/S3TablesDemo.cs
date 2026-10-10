using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3Tables;
using Amazon.S3Tables.Model;
using FlociLab.Core;

namespace FlociLab.Aws.S3Tables;

/// <summary>
/// Creates a table bucket, a namespace and an Apache Iceberg table with a two-column schema, reads
/// the table back, reads and moves its metadata location, renames it, sets a maintenance rule and a
/// bucket policy, then tries a duplicate table, a table in a namespace that does not exist, tagging
/// and bucket encryption. Ordinary AWSSDK.S3Tables code — the only emulator wiring is in
/// <see cref="S3TablesClientFactory"/>; this class reads <c>UseEmulator</c> only to name floci's
/// gaps where it differs from AWS and to skip the one step that points a real table at a made-up file. Every step
/// reports what the engine actually answered rather than what the docs promise.
/// </summary>
public sealed class S3TablesDemo(S3TablesClientFactory factory) : IServiceDemo
{
    private const string Namespace = "sales";

    private const string TableName = "orders";

    private const string RenamedTable = "orders_v2";

    public string Provider => CloudProvider.Aws;

    public string Slug => "s3tables";

    public string DisplayName => "S3 Tables";

    public string Category => "Storage, transfer and backup";

    public string Route => "/aws/s3tables";

    /// <summary>ListTableBuckets — read-only, and an empty account answers an empty list.</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            using IAmazonS3Tables client = factory.Create();
            ListTableBucketsResponse response = await client.ListTableBucketsAsync(new ListTableBucketsRequest(), ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"ListTableBuckets: HTTP {(int)response.HttpStatusCode}.");
        }
        // Cancellation is the caller giving up, not an outcome of the probe (see CoverageMatrix).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using IAmazonS3Tables client = factory.Create();

        string name = $"flocilab-{Guid.NewGuid().ToString("N")[..12]}";

        // Claimed before the create: a call that lands while its response is lost (the page's
        // Dispose cancels mid-flight) still created the bucket, so the finally looks it up by name
        // rather than trusting that a response arrived.
        bool claimed = false;
        string? bucketArn = null;
        string tableName = TableName;
        string? versionToken = null;

        List<DemoStep> cleanup = [];

        try
        {
            DemoStep created = await RunStepAsync(
                "CreateTableBucket, GetTableBucket — a table bucket",
                this.Call("PUT", "/buckets", $"client.CreateTableBucketAsync(new CreateTableBucketRequest {{ Name = \"{name}\" }})\nclient.GetTableBucketAsync(new GetTableBucketRequest {{ TableBucketARN = arn }})"),
                async () =>
                {
                    claimed = true;
                    CreateTableBucketResponse response = await client.CreateTableBucketAsync(new CreateTableBucketRequest { Name = name }, ct).ConfigureAwait(false);

                    bucketArn = response.Arn;

                    GetTableBucketResponse found = await client.GetTableBucketAsync(new GetTableBucketRequest { TableBucketARN = bucketArn }, ct).ConfigureAwait(false);

                    return $"{found.Name}\n{found.Arn}";
                }).ConfigureAwait(false);

            yield return created;

            // A failed create means every later step would look for a bucket that is not there.
            // Skipped rather than `yield break`, which would also skip yielding the cleanup step below.
            if (created.Succeeded)
            {
                DemoStep ns = await RunStepAsync(
                    "CreateNamespace, ListNamespaces — a namespace",
                    this.Call("PUT", "/namespaces/{arn}", $"client.CreateNamespaceAsync(new CreateNamespaceRequest {{ TableBucketARN = arn, Namespace = [\"{Namespace}\"] }})\nclient.ListNamespacesAsync(new ListNamespacesRequest {{ TableBucketARN = arn }})"),
                    async () =>
                    {
                        await client.CreateNamespaceAsync(new CreateNamespaceRequest { TableBucketARN = bucketArn, Namespace = [Namespace] }, ct).ConfigureAwait(false);

                        List<NamespaceSummary> found = (await client.ListNamespacesAsync(new ListNamespacesRequest { TableBucketARN = bucketArn }, ct).ConfigureAwait(false)).Namespaces ?? [];

                        // The v4 SDK leaves a list it did not receive null, not empty.
                        if (!found.Any(n => (n.Namespace ?? []).Contains(Namespace)))
                        {
                            throw new InvalidOperationException($"the namespace was accepted but ListNamespaces lists {found.Count} namespace(s) without it.");
                        }

                        return string.Join("\n", found.Select(n => string.Join(".", n.Namespace ?? [])));
                    }).ConfigureAwait(false);

                yield return ns;

                if (ns.Succeeded)
                {
                    DemoStep table = await RunStepAsync(
                        "CreateTable, GetTable, ListTables — an Iceberg table",
                        this.Call("PUT", $"/tables/{{arn}}/{Namespace}", $"client.CreateTableAsync(new CreateTableRequest {{ TableBucketARN = arn, Namespace = \"{Namespace}\", Name = \"{TableName}\", Format = OpenTableFormat.ICEBERG,\n    Metadata = new TableMetadata {{ Iceberg = new IcebergMetadata {{ Schema = new IcebergSchema {{ Fields = [id long required, item string] }} }} }} }})\nclient.GetTableAsync(new GetTableRequest {{ ... }})\nclient.ListTablesAsync(new ListTablesRequest {{ TableBucketARN = arn }})"),
                        async () =>
                        {
                            CreateTableResponse response = await client.CreateTableAsync(
                                new CreateTableRequest
                                {
                                    TableBucketARN = bucketArn,
                                    Namespace = Namespace,
                                    Name = TableName,
                                    Format = OpenTableFormat.ICEBERG,
                                    Metadata = new TableMetadata
                                    {
                                        Iceberg = new IcebergMetadata
                                        {
                                            Schema = new IcebergSchema
                                            {
                                                Fields =
                                                [
                                                    new SchemaField { Name = "id", Type = "long", Required = true },
                                                    new SchemaField { Name = "item", Type = "string" },
                                                ],
                                            },
                                        },
                                    },
                                }, ct).ConfigureAwait(false);

                            versionToken = response.VersionToken;

                            GetTableResponse found = await client.GetTableAsync(new GetTableRequest { TableBucketARN = bucketArn, Namespace = Namespace, Name = TableName }, ct).ConfigureAwait(false);
                            List<TableSummary> listed = (await client.ListTablesAsync(new ListTablesRequest { TableBucketARN = bucketArn }, ct).ConfigureAwait(false)).Tables ?? [];

                            if (!listed.Any(t => t.Name == TableName))
                            {
                                throw new InvalidOperationException($"the table was accepted but ListTables lists {listed.Count} table(s) without it.");
                            }

                            return $"{found.Name} ({found.Format}, {found.Type}) in {string.Join(".", found.Namespace ?? [])}\n{response.TableARN}\nListTables lists {listed.Count}";
                        }).ConfigureAwait(false);

                    yield return table;

                    if (table.Succeeded)
                    {
                        yield return await RunStepAsync(
                            "GetTableMetadataLocation — where the table's metadata lives",
                            this.Call("GET", $"/tables/{{arn}}/{Namespace}/{TableName}/metadata-location", "client.GetTableMetadataLocationAsync(new GetTableMetadataLocationRequest { ... })"),
                            async () =>
                            {
                                GetTableMetadataLocationResponse response = await client.GetTableMetadataLocationAsync(
                                    new GetTableMetadataLocationRequest { TableBucketARN = bucketArn, Namespace = Namespace, Name = TableName }, ct).ConfigureAwait(false);

                                versionToken = response.VersionToken;

                                return $"MetadataLocation: {(string.IsNullOrEmpty(response.MetadataLocation) ? "(none)" : response.MetadataLocation)}\nVersionToken: {response.VersionToken}";
                            }).ConfigureAwait(false);

                        yield return await RunStepAsync(
                            "UpdateTableMetadataLocation — moving the table to a metadata file",
                            this.Call("PUT", $"/tables/{{arn}}/{Namespace}/{TableName}/metadata-location", "client.UpdateTableMetadataLocationAsync(new UpdateTableMetadataLocationRequest { ..., VersionToken = token, MetadataLocation = \"s3://…/metadata/00001.metadata.json\" })"),
                            async () =>
                            {
                                // The location below is made up: nothing writes a metadata file there. floci takes
                                // it; on real AWS the table would point at a file that does not exist, so it is skipped.
                                if (!factory.UseEmulator)
                                {
                                    return "skipped on real AWS: this sample writes no Iceberg metadata file, so it has no real location to move the table to";
                                }

                                const string location = "s3://flocilab/orders/metadata/00001.metadata.json";

                                UpdateTableMetadataLocationResponse response = await client.UpdateTableMetadataLocationAsync(
                                    new UpdateTableMetadataLocationRequest { TableBucketARN = bucketArn, Namespace = Namespace, Name = TableName, VersionToken = versionToken, MetadataLocation = location }, ct).ConfigureAwait(false);

                                GetTableMetadataLocationResponse read = await client.GetTableMetadataLocationAsync(
                                    new GetTableMetadataLocationRequest { TableBucketARN = bucketArn, Namespace = Namespace, Name = TableName }, ct).ConfigureAwait(false);

                                if (read.MetadataLocation != location)
                                {
                                    throw new InvalidOperationException($"the location was accepted but GetTableMetadataLocation answers \"{read.MetadataLocation}\".");
                                }

                                versionToken = response.VersionToken;

                                return $"{read.MetadataLocation}\nfloci accepts a location nothing was ever written to";
                            }).ConfigureAwait(false);

                        yield return await RunStepAsync(
                            "RenameTable — orders to orders_v2",
                            this.Call("PUT", $"/tables/{{arn}}/{Namespace}/{TableName}/rename", $"client.RenameTableAsync(new RenameTableRequest {{ ..., Name = \"{TableName}\", NewName = \"{RenamedTable}\" }})"),
                            async () =>
                            {
                                await client.RenameTableAsync(
                                    new RenameTableRequest { TableBucketARN = bucketArn, Namespace = Namespace, Name = TableName, NewName = RenamedTable }, ct).ConfigureAwait(false);

                                tableName = RenamedTable;

                                GetTableResponse found = await client.GetTableAsync(new GetTableRequest { TableBucketARN = bucketArn, Namespace = Namespace, Name = RenamedTable }, ct).ConfigureAwait(false);

                                // floci builds a table's ARN from its name, so the rename moved it; AWS's ends in the table's id.
                                string note = factory.UseEmulator && found.TableARN.EndsWith($"/table/{RenamedTable}", StringComparison.Ordinal)
                                    ? "\nfloci builds the ARN from the table's name, so the rename changed it; on AWS it ends in the table's id"
                                    : string.Empty;

                                return $"{found.Name}\n{found.TableARN}{note}";
                            }).ConfigureAwait(false);

                        yield return await RunStepAsync(
                            "PutTableMaintenanceConfiguration, GetTableMaintenanceConfiguration — a clean-up rule",
                            this.Call("PUT", $"/tables/{{arn}}/{Namespace}/{{table}}/maintenance/icebergSnapshotManagement", "client.PutTableMaintenanceConfigurationAsync(new PutTableMaintenanceConfigurationRequest { ..., Type = TableMaintenanceType.IcebergSnapshotManagement,\n    Value = new TableMaintenanceConfigurationValue { Status = enabled, Settings = { MinSnapshotsToKeep = 2, MaxSnapshotAgeHours = 72 } } })\nclient.GetTableMaintenanceConfigurationAsync(new GetTableMaintenanceConfigurationRequest { ... })"),
                            async () =>
                            {
                                await client.PutTableMaintenanceConfigurationAsync(
                                    new PutTableMaintenanceConfigurationRequest
                                    {
                                        TableBucketARN = bucketArn,
                                        Namespace = Namespace,
                                        Name = tableName,
                                        Type = TableMaintenanceType.IcebergSnapshotManagement,
                                        Value = new TableMaintenanceConfigurationValue
                                        {
                                            Status = MaintenanceStatus.Enabled,
                                            Settings = new TableMaintenanceSettings
                                            {
                                                IcebergSnapshotManagement = new IcebergSnapshotManagementSettings { MinSnapshotsToKeep = 2, MaxSnapshotAgeHours = 72 },
                                            },
                                        },
                                    }, ct).ConfigureAwait(false);

                                GetTableMaintenanceConfigurationResponse read = await client.GetTableMaintenanceConfigurationAsync(
                                    new GetTableMaintenanceConfigurationRequest { TableBucketARN = bucketArn, Namespace = Namespace, Name = tableName }, ct).ConfigureAwait(false);

                                // The v4 SDK leaves a map or a member it did not receive null, not empty.
                                Dictionary<string, TableMaintenanceConfigurationValue> rules = read.Configuration ?? [];

                                if (!rules.TryGetValue("icebergSnapshotManagement", out TableMaintenanceConfigurationValue? rule))
                                {
                                    throw new InvalidOperationException($"the rule was accepted but GetTableMaintenanceConfiguration lists {rules.Count} rule(s) without it.");
                                }

                                IcebergSnapshotManagementSettings settings = rule.Settings?.IcebergSnapshotManagement
                                    ?? throw new InvalidOperationException($"the rule reads {rule.Status} but carries no snapshot settings.");

                                return $"icebergSnapshotManagement: {rule.Status}, keep at least {settings.MinSnapshotsToKeep} snapshot(s), expire after {settings.MaxSnapshotAgeHours} hour(s)";
                            }).ConfigureAwait(false);

                        yield return await RunStepAsync(
                            "CreateTable — a name that is taken",
                            this.Call("PUT", $"/tables/{{arn}}/{Namespace}", $"client.CreateTableAsync(new CreateTableRequest {{ ..., Name = \"{tableName}\", Format = OpenTableFormat.ICEBERG }})"),
                            async () =>
                            {
                                // tableName, not RenamedTable: if the rename failed, the table still has its old name.
                                try
                                {
                                    await client.CreateTableAsync(
                                        new CreateTableRequest { TableBucketARN = bucketArn, Namespace = Namespace, Name = tableName, Format = OpenTableFormat.ICEBERG }, ct).ConfigureAwait(false);

                                    return factory.UseEmulator
                                        ? "accepted\nreal S3 Tables answers ConflictException for a table that already exists; floci has started to accept it"
                                        : "accepted";
                                }
                                // What real S3 Tables answers: the expected outcome, shown as a successful step.
                                catch (ConflictException ex)
                                {
                                    return $"{ex.ErrorCode}: {ex.Message}";
                                }
                            }).ConfigureAwait(false);
                    }

                    yield return await RunStepAsync(
                        "CreateTable — a namespace that does not exist",
                        this.Call("PUT", "/tables/{arn}/nonesuch", "client.CreateTableAsync(new CreateTableRequest { ..., Namespace = \"nonesuch\", Name = \"orders\", Format = OpenTableFormat.ICEBERG })"),
                        async () =>
                        {
                            try
                            {
                                await client.CreateTableAsync(
                                    new CreateTableRequest { TableBucketARN = bucketArn, Namespace = "nonesuch", Name = TableName, Format = OpenTableFormat.ICEBERG }, ct).ConfigureAwait(false);

                                return factory.UseEmulator
                                    ? "accepted\nreal S3 Tables answers NotFoundException for a namespace that does not exist; floci has started to accept it"
                                    : "accepted";
                            }
                            catch (NotFoundException ex)
                            {
                                return $"{ex.ErrorCode}: {ex.Message}";
                            }
                        }).ConfigureAwait(false);
                }

                yield return await RunStepAsync(
                    "PutTableBucketPolicy, GetTableBucketPolicy, DeleteTableBucketPolicy — a bucket policy",
                    this.Call("PUT", "/buckets/{arn}/policy", "client.PutTableBucketPolicyAsync(new PutTableBucketPolicyRequest { TableBucketARN = arn, ResourcePolicy = json })\nclient.GetTableBucketPolicyAsync(new GetTableBucketPolicyRequest { TableBucketARN = arn })\nclient.DeleteTableBucketPolicyAsync(new DeleteTableBucketPolicyRequest { TableBucketARN = arn })"),
                    async () =>
                    {
                        // A policy that allows nothing new: the account's own root may read the bucket, which
                        // it can already do. Real S3 Tables validates the document; floci stores it as is.
                        string policy = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":\"" + AccountRoot(bucketArn) + "\"},\"Action\":\"s3tables:GetTableBucket\",\"Resource\":\"" + bucketArn + "\"}]}";

                        await client.PutTableBucketPolicyAsync(new PutTableBucketPolicyRequest { TableBucketARN = bucketArn, ResourcePolicy = policy }, ct).ConfigureAwait(false);

                        string read = (await client.GetTableBucketPolicyAsync(new GetTableBucketPolicyRequest { TableBucketARN = bucketArn }, ct).ConfigureAwait(false)).ResourcePolicy;

                        await client.DeleteTableBucketPolicyAsync(new DeleteTableBucketPolicyRequest { TableBucketARN = bucketArn }, ct).ConfigureAwait(false);

                        return $"{read}\n(deleted again)";
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "TagResource — labelling the table bucket",
                    this.Call("POST", "/tag/{arn}", "client.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = new Dictionary<string, string> { [\"env\"] = \"lab\" } })"),
                    async () =>
                    {
                        try
                        {
                            await client.TagResourceAsync(new TagResourceRequest { ResourceArn = bucketArn, Tags = new Dictionary<string, string> { ["env"] = "lab" } }, ct).ConfigureAwait(false);

                            return factory.UseEmulator
                                ? "tagged\nfloci now implements tagging: delete this step's tripwire and add ListTagsForResource and UntagResource"
                                : "tagged";
                        }
                        // Not a 501: floci answers an operation it has not built with HTTP 404 and UnknownOperationException.
                        catch (AmazonS3TablesException ex) when (IsNotImplemented(ex))
                        {
                            return $"HTTP {(int)ex.StatusCode} {ex.ErrorCode}: {ex.Message}\nreal S3 Tables tags a table bucket and a table; floci has not built tagging";
                        }
                    }).ConfigureAwait(false);

                yield return await RunStepAsync(
                    "GetTableBucketEncryption — the bucket's encryption",
                    this.Call("GET", "/buckets/{arn}/encryption", "client.GetTableBucketEncryptionAsync(new GetTableBucketEncryptionRequest { TableBucketARN = arn })"),
                    async () =>
                    {
                        try
                        {
                            GetTableBucketEncryptionResponse response = await client.GetTableBucketEncryptionAsync(new GetTableBucketEncryptionRequest { TableBucketARN = bucketArn }, ct).ConfigureAwait(false);

                            string algorithm = response.EncryptionConfiguration?.SseAlgorithm?.Value ?? "(none)";

                            return factory.UseEmulator
                                ? $"{algorithm}\nfloci now implements bucket encryption: delete this step's tripwire and add Put and DeleteTableBucketEncryption"
                                : algorithm;
                        }
                        catch (AmazonS3TablesException ex) when (IsNotImplemented(ex))
                        {
                            return $"HTTP {(int)ex.StatusCode} {ex.ErrorCode}: {ex.Message}\nreal S3 Tables encrypts every table bucket (AES256 unless a KMS key is set); floci has not built the encryption operations";
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

    /// <summary>The account root inside <c>arn:aws:s3tables:region:account:bucket/name</c>, as an IAM principal.</summary>
    private static string AccountRoot(string? arn)
        => arn?.Split(':') is { Length: > 4 } parts ? $"arn:aws:iam::{parts[4]}:root" : "*";

    private static string Describe(Exception ex)
        => ex.InnerException is null || ex.InnerException.Message == ex.Message
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";

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

    /// <summary>The table buckets of that name; names are unique per run, so more than one means a lost create was retried.</summary>
    private static async Task<List<TableBucketSummary>> FindAsync(IAmazonS3Tables client, string name, CancellationToken ct)
    {
        List<TableBucketSummary> found = [];
        string? next = null;

        do
        {
            ListTableBucketsResponse page = await client.ListTableBucketsAsync(new ListTableBucketsRequest { Prefix = name, ContinuationToken = next }, ct).ConfigureAwait(false);

            found.AddRange((page.TableBuckets ?? []).Where(b => b.Name == name));
            next = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(next));

        return found;
    }

    /// <summary>Every table in the bucket, across every namespace and every page.</summary>
    private static async Task<List<TableSummary>> ListTablesAsync(IAmazonS3Tables client, string bucketArn, CancellationToken ct)
    {
        List<TableSummary> found = [];
        string? next = null;

        do
        {
            ListTablesResponse page = await client.ListTablesAsync(new ListTablesRequest { TableBucketARN = bucketArn, ContinuationToken = next }, ct).ConfigureAwait(false);

            found.AddRange(page.Tables ?? []);
            next = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(next));

        return found;
    }

    /// <summary>Every namespace in the bucket, across every page.</summary>
    private static async Task<List<NamespaceSummary>> ListNamespacesAsync(IAmazonS3Tables client, string bucketArn, CancellationToken ct)
    {
        List<NamespaceSummary> found = [];
        string? next = null;

        do
        {
            ListNamespacesResponse page = await client.ListNamespacesAsync(new ListNamespacesRequest { TableBucketARN = bucketArn, ContinuationToken = next }, ct).ConfigureAwait(false);

            found.AddRange(page.Namespaces ?? []);
            next = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(next));

        return found;
    }

    /// <summary>The wire-level request shown beside an SDK call: REST JSON, one method and path per operation.</summary>
    private string Call(string method, string path, string call) => $"{method} {factory.ServiceUrl}{path}\n{call}";

    /// <summary>
    /// Deletes whatever this run created, looking the bucket up by its unique name so one whose
    /// create response was lost is still found, and one that was never created reads as a truthful
    /// "nothing to remove". Works inside out — tables, namespaces, the bucket — because real S3
    /// Tables refuses to delete a parent that still has children. Uses
    /// <see cref="CancellationToken.None"/>: a cancelled run still has a bucket to remove.
    /// </summary>
    private async Task<DemoStep> DeleteAsync(IAmazonS3Tables client, string name, CancellationToken ct)
    {
        string request = this.Call("DELETE", "/buckets/{arn}", "client.DeleteTableAsync, DeleteNamespaceAsync, DeleteTableBucketAsync");

        return await RunStepAsync("DeleteTableBucket — cleanup", request, async () =>
        {
            string cancelled = ct.IsCancellationRequested ? "\n(the run was cancelled; cleanup ran anyway)" : string.Empty;
            CancellationToken none = CancellationToken.None;
            List<TableBucketSummary> found = await FindAsync(client, name, none).ConfigureAwait(false);

            if (found.Count == 0)
            {
                return $"No table bucket for this run exists — nothing to remove.{cancelled}";
            }

            int tables = 0;
            int namespaces = 0;
            int buckets = 0;

            // One failed delete must not stop the rest: every child is tried, and what failed is
            // reported at the end, so the step reads as failed and names what was left behind.
            List<string> failed = [];

            foreach (TableBucketSummary bucket in found)
            {
                // Tables are listed across the whole bucket rather than per namespace, so a table whose
                // namespace is not listed (if floci ever accepts one in a missing namespace) is still found.
                foreach (TableSummary table in await ListTablesAsync(client, bucket.Arn, none).ConfigureAwait(false))
                {
                    string spaceName = string.Join(".", table.Namespace ?? []);

                    try
                    {
                        await client.DeleteTableAsync(new DeleteTableRequest { TableBucketARN = bucket.Arn, Namespace = spaceName, Name = table.Name }, none).ConfigureAwait(false);
                        tables++;
                    }
                    catch (AmazonServiceException ex)
                    {
                        failed.Add($"table {spaceName}.{table.Name}: {ex.ErrorCode}");
                    }
                }

                foreach (NamespaceSummary space in await ListNamespacesAsync(client, bucket.Arn, none).ConfigureAwait(false))
                {
                    string spaceName = string.Join(".", space.Namespace ?? []);

                    try
                    {
                        await client.DeleteNamespaceAsync(new DeleteNamespaceRequest { TableBucketARN = bucket.Arn, Namespace = spaceName }, none).ConfigureAwait(false);
                        namespaces++;
                    }
                    catch (AmazonServiceException ex)
                    {
                        failed.Add($"namespace {spaceName}: {ex.ErrorCode}");
                    }
                }

                try
                {
                    await client.DeleteTableBucketAsync(new DeleteTableBucketRequest { TableBucketARN = bucket.Arn }, none).ConfigureAwait(false);
                    buckets++;
                }
                catch (AmazonServiceException ex)
                {
                    failed.Add($"table bucket {bucket.Name}: {ex.ErrorCode}");
                }
            }

            string deleted = $"Deleted {buckets} table bucket(s), {namespaces} namespace(s), {tables} table(s).";

            if (failed.Count > 0)
            {
                throw new InvalidOperationException($"{deleted} Left behind: {string.Join("; ", failed)}.{cancelled}");
            }

            return $"{deleted}{cancelled}";
        }).ConfigureAwait(false);
    }
}
