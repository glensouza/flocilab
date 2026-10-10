using Amazon.S3Vectors;
using Amazon.S3Vectors.Model;
using FlociLab.Aws.S3Vectors;
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
public sealed class AwsS3VectorsTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private S3VectorsClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new S3VectorsClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new S3VectorsDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new S3VectorsDemo(this.factory));

        Assert.Collection(
            steps,
            s =>
            {
                Assert.Equal("CreateVectorBucket, GetVectorBucket — a vector bucket", s.Title);
                Assert.StartsWith("flocilab-", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateIndex, GetIndex, ListIndexes — a three-dimensional cosine index", s.Title);
                Assert.StartsWith("films (float32, 3 dimensions, cosine)", s.Response);
                Assert.EndsWith("ListIndexes lists 1", s.Response);
            },
            s =>
            {
                Assert.Equal("PutVectors — four films as points in space", s.Title);
                Assert.Equal("4 vectors stored", s.Response);
            },
            s =>
            {
                Assert.Equal("GetVectors, ListVectors — reading them back", s.Title);
                Assert.StartsWith("alien: [1, 0, 0]", s.Response);
                Assert.EndsWith("ListVectors lists 4: alien, amelie, blade-runner, notting-hill", s.Response);
            },
            s =>
            {
                Assert.Equal("QueryVectors — the two nearest to [1, 0.1, 0]", s.Title);
                Assert.Equal("alien (scifi): distance 0.005\nblade-runner (scifi): distance 0.025", s.Response);
            },
            s =>
            {
                Assert.Equal("QueryVectors — the same query, filtered to romance", s.Title);
                Assert.StartsWith("amelie (romance): distance", s.Response);
                Assert.Contains("notting-hill (romance): distance", s.Response);
                Assert.DoesNotContain("scifi", s.Response);
            },
            s =>
            {
                Assert.Equal("DeleteVectors — removing one", s.Title);
                Assert.Equal("ListVectors lists 3: amelie, blade-runner, notting-hill", s.Response);
            },
            s =>
            {
                Assert.Equal("PutVectors — two dimensions into a three-dimensional index", s.Title);
                Assert.StartsWith("ValidationException:", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateIndex — a name that is taken", s.Title);
                Assert.StartsWith("ConflictException:", s.Response);
            },
            s =>
            {
                Assert.Equal("PutVectorBucketPolicy — a bucket policy", s.Title);

                // Tripwire: floci answers HTTP 404 UnknownOperationException. When this starts
                // failing, bucket policies landed.
                Assert.StartsWith("HTTP 404 UnknownOperationException", s.Response);
            },
            s =>
            {
                Assert.Equal("DeleteVectorBucket — cleanup", s.Title);
                Assert.StartsWith("Deleted 1 vector bucket(s), 1 index(es).", s.Response);
            });

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves no vector bucket behind.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_Nothing_Behind()
    {
        S3VectorsDemo demo = new(this.factory);

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

        await foreach (DemoStep step in new S3VectorsDemo(this.factory).RunAsync(ct))
        {
            if (step.Title.StartsWith("QueryVectors", StringComparison.Ordinal))
            {
                break;
            }
        }

        await this.AssertAccountIsEmptyAsync();
    }

    /// <summary>
    /// Tripwire: floci ignores ListVectorBuckets' Prefix and MaxResults and answers every bucket on
    /// one page, where AWS filters and pages. When this fails, upstream honours them.
    /// </summary>
    [Fact]
    public async Task Floci_Ignores_ListVectorBuckets_Prefix_And_MaxResults()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonS3Vectors client = this.factory.Create();
        // Two buckets, so a page of one would have to leave one out.
        List<string> names = [$"flocilab-{Guid.NewGuid().ToString("N")[..12]}", $"flocilab-{Guid.NewGuid().ToString("N")[..12]}"];

        try
        {
            foreach (string name in names)
            {
                await client.CreateVectorBucketAsync(new CreateVectorBucketRequest { VectorBucketName = name }, ct);
            }

            ListVectorBucketsResponse other = await client.ListVectorBucketsAsync(new ListVectorBucketsRequest { Prefix = "no-such-prefix-" }, ct);
            ListVectorBucketsResponse one = await client.ListVectorBucketsAsync(new ListVectorBucketsRequest { MaxResults = 1 }, ct);

            Assert.All(names, n => Assert.Contains(other.VectorBuckets ?? [], b => b.VectorBucketName == n));
            Assert.All(names, n => Assert.Contains(one.VectorBuckets ?? [], b => b.VectorBucketName == n));
            Assert.True(string.IsNullOrEmpty(one.NextToken), $"a page of one answered NextToken {one.NextToken}");
        }
        finally
        {
            foreach (string name in names)
            {
                try
                {
                    await client.DeleteVectorBucketAsync(new DeleteVectorBucketRequest { VectorBucketName = name }, CancellationToken.None);
                }
                catch (NotFoundException)
                {
                    // Never created: the create above failed first.
                }
            }
        }
    }

    /// <summary>A stopped emulator must read as Unreachable, not as a broken sample.</summary>
    [Fact]
    public async Task Probe_Against_A_Stopped_Emulator_Reports_Unreachable()
    {
        S3VectorsDemo demo = new(new S3VectorsClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task AssertAccountIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonS3Vectors client = this.factory.Create();

        // Every page: a leak on the second one must still fail the test.
        List<VectorBucketSummary> buckets = [];
        string? next = null;

        do
        {
            ListVectorBucketsResponse page = await client.ListVectorBucketsAsync(new ListVectorBucketsRequest { Prefix = "flocilab-", NextToken = next }, ct);

            buckets.AddRange(page.VectorBuckets ?? []);
            next = page.NextToken;
        }
        while (!string.IsNullOrEmpty(next));

        Assert.DoesNotContain(buckets, b => b.VectorBucketName.StartsWith("flocilab-", StringComparison.Ordinal));
    }

    private static async Task<List<DemoStep>> RunAsync(S3VectorsDemo demo)
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
