using Amazon.S3Tables;
using Amazon.S3Tables.Model;
using FlociLab.Aws.S3Tables;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Testcontainers.Floci;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator the
/// AppHost runs, so the suite passes on a machine that has never started the lab.
/// </summary>
public sealed class AwsS3TablesTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private S3TablesClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new S3TablesClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new S3TablesDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new S3TablesDemo(this.factory));

        Assert.Collection(
            steps,
            s =>
            {
                Assert.Equal("CreateTableBucket, GetTableBucket — a table bucket", s.Title);
                Assert.StartsWith("flocilab-", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateNamespace, ListNamespaces — a namespace", s.Title);
                Assert.Equal("sales", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateTable, GetTable, ListTables — an Iceberg table", s.Title);
                Assert.StartsWith("orders (ICEBERG, customer) in sales", s.Response);
                Assert.EndsWith("ListTables lists 1", s.Response);
            },
            s =>
            {
                Assert.Equal("GetTableMetadataLocation — where the table's metadata lives", s.Title);

                // Pinned as floci's behaviour: it writes no metadata file at create. No claim about real AWS.
                Assert.StartsWith("MetadataLocation: (none)", s.Response);
            },
            s =>
            {
                Assert.Equal("UpdateTableMetadataLocation — moving the table to a metadata file", s.Title);

                // Pinned as floci's behaviour, with no claim about real AWS beyond the step's own note.
                Assert.StartsWith("s3://flocilab/orders/metadata/00001.metadata.json", s.Response);
            },
            s =>
            {
                Assert.Equal("RenameTable — orders to orders_v2", s.Title);
                Assert.StartsWith("orders_v2\n", s.Response);

                // floci puts the table's name in its ARN, so a rename changes it, and the page says so.
                Assert.Contains("/table/orders_v2\n", s.Response);
                Assert.EndsWith("on AWS it ends in the table's id", s.Response);
            },
            s =>
            {
                Assert.Equal("PutTableMaintenanceConfiguration, GetTableMaintenanceConfiguration — a clean-up rule", s.Title);
                Assert.Equal("icebergSnapshotManagement: enabled, keep at least 2 snapshot(s), expire after 72 hour(s)", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateTable — a name that is taken", s.Title);
                Assert.StartsWith("ConflictException:", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateTable — a namespace that does not exist", s.Title);
                Assert.StartsWith("NotFoundException:", s.Response);
            },
            s =>
            {
                Assert.Equal("PutTableBucketPolicy, GetTableBucketPolicy, DeleteTableBucketPolicy — a bucket policy", s.Title);
                Assert.Contains("s3tables:GetTableBucket", s.Response);
                Assert.EndsWith("(deleted again)", s.Response);
            },
            s =>
            {
                Assert.Equal("TagResource — labelling the table bucket", s.Title);

                // Tripwire: floci answers HTTP 404 UnknownOperationException. When this starts
                // failing, tagging landed.
                Assert.StartsWith("HTTP 404 UnknownOperationException", s.Response);
            },
            s =>
            {
                Assert.Equal("GetTableBucketEncryption — the bucket's encryption", s.Title);

                // Tripwire: the same for the encryption operations.
                Assert.StartsWith("HTTP 404 UnknownOperationException", s.Response);
            },
            s =>
            {
                Assert.Equal("DeleteTableBucket — cleanup", s.Title);
                Assert.StartsWith("Deleted 1 table bucket(s), 1 namespace(s), 1 table(s).", s.Response);
            });

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves no table bucket behind.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_Nothing_Behind()
    {
        S3TablesDemo demo = new(this.factory);

        for (int run = 0; run < 2; run++)
        {
            List<DemoStep> steps = await RunAsync(demo);

            Assert.All(steps, s => Assert.True(s.Succeeded, $"run {run}, {s.Title}: {s.Error}"));
        }

        await this.AssertAccountIsEmptyAsync();
    }

    /// <summary>A consumer that stops early (the page navigated away) still has everything deleted by the finally.</summary>
    [Fact]
    public async Task Abandoned_Run_Deletes_What_It_Created()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await foreach (DemoStep step in new S3TablesDemo(this.factory).RunAsync(ct))
        {
            if (step.Title.StartsWith("RenameTable", StringComparison.Ordinal))
            {
                break;
            }
        }

        await this.AssertAccountIsEmptyAsync();
    }

    /// <summary>A stopped emulator must read as Unreachable, not as a broken sample.</summary>
    [Fact]
    public async Task Probe_Against_A_Stopped_Emulator_Reports_Unreachable()
    {
        S3TablesDemo demo = new(new S3TablesClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task AssertAccountIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonS3Tables client = this.factory.Create();

        Assert.DoesNotContain(
            (await client.ListTableBucketsAsync(new ListTableBucketsRequest(), ct)).TableBuckets ?? [],
            b => b.Name.StartsWith("flocilab-", StringComparison.Ordinal));
    }

    private static async Task<List<DemoStep>> RunAsync(S3TablesDemo demo)
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in demo.RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        return steps;
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
