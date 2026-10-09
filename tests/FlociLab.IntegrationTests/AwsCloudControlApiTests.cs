using Amazon.CloudControlApi;
using Amazon.CloudControlApi.Model;
using FlociLab.Aws.CloudControlApi;
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
public sealed class AwsCloudControlApiTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private CloudControlApiClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new CloudControlApiClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new CloudControlApiDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new CloudControlApiDemo(this.factory));

        Assert.Collection(
            steps,
            s =>
            {
                Assert.Equal("CreateResource, GetResourceRequestStatus — a bucket, a queue and a topic", s.Title);

                // A create answers IN_PROGRESS and is finished on a later read, as real Cloud Control does.
                Assert.Contains("AWS::S3::Bucket: IN_PROGRESS → SUCCESS", s.Response);
                Assert.Contains("AWS::SQS::Queue: IN_PROGRESS → SUCCESS", s.Response);
                Assert.Contains("AWS::SNS::Topic: IN_PROGRESS → SUCCESS", s.Response);
                Assert.Contains("arn:aws:sns:", s.Response);
            },
            s =>
            {
                Assert.Equal("GetResource — the resource tree", s.Title);
                Assert.Contains("├─ AWS::S3::Bucket flocilab-", s.Response);
                Assert.Contains("├─ AWS::SQS::Queue ", s.Response);
                Assert.Contains("└─ AWS::SNS::Topic arn:aws:sns:", s.Response);
            },
            s =>
            {
                Assert.Equal("ListResources — what the account holds", s.Title);
                Assert.Contains("AWS::S3::Bucket: 1 listed, this run's among them", s.Response);

                // Tripwire: floci lists buckets and roles only. When this fails, floci lists queues
                // too and the step reports the queue among them.
                Assert.Contains("AWS::SQS::Queue: UnsupportedActionException", s.Response);
            },
            s =>
            {
                Assert.Equal("UpdateResource, ListResourceRequests — what floci has not built", s.Title);

                // Tripwire: neither operation is built. When this fails, the step reports a status instead.
                Assert.Contains("UpdateResource: UnsupportedOperation", s.Response);
                Assert.Contains("ListResourceRequests: UnsupportedOperation", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateResource — a bucket name that is taken", s.Title);

                // Tripwire: real Cloud Control reports FAILED, AlreadyExists. When this fails, so does floci.
                Assert.StartsWith("SUCCESS for flocilab-", s.Response);
            },
            s =>
            {
                Assert.Equal("GetResourceRequestStatus, GetResource, CreateResource — things that are not there", s.Title);
                Assert.Contains("GetResourceRequestStatus: RequestTokenNotFoundException", s.Response);
                Assert.Contains("GetResource: ResourceNotFoundException", s.Response);
                Assert.Contains("CreateResource: InvalidRequestException", s.Response);
            },
            s =>
            {
                Assert.Equal("Delete everything — cleanup", s.Title);

                // All three were found by identifier and removed, not "nothing to remove".
                Assert.StartsWith("Deleted 3 resource(s)", s.Response);
            });

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves nothing behind.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_Nothing_Behind()
    {
        CloudControlApiDemo demo = new(this.factory);
        List<DemoStep> created = [];

        for (int run = 0; run < 2; run++)
        {
            List<DemoStep> steps = await RunAsync(demo);

            Assert.All(steps, s => Assert.True(s.Succeeded, $"run {run}, {s.Title}: {s.Error}"));
            created.Add(steps[0]);
        }

        await this.AssertNoBucketsAsync();

        foreach (DemoStep step in created)
        {
            await this.AssertGoneAsync(step);
        }
    }

    /// <summary>A consumer that stops early (the page navigated away) still has everything deleted by the finally.</summary>
    [Fact]
    public async Task Abandoned_Run_Deletes_What_It_Created()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DemoStep? created = null;

        await foreach (DemoStep step in new CloudControlApiDemo(this.factory).RunAsync(ct))
        {
            created ??= step;

            if (step.Title.StartsWith("GetResource —", StringComparison.Ordinal))
            {
                break;
            }
        }

        Assert.NotNull(created);
        await this.AssertNoBucketsAsync();
        await this.AssertGoneAsync(created);
    }

    /// <summary>A stopped emulator must read as Unreachable, not as a broken sample.</summary>
    [Fact]
    public async Task Probe_Against_A_Stopped_Emulator_Reports_Unreachable()
    {
        CloudControlApiDemo demo = new(new CloudControlApiClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task AssertNoBucketsAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonCloudControlApi client = this.factory.Create();

        Assert.Empty((await client.ListResourcesAsync(new ListResourcesRequest { TypeName = "AWS::S3::Bucket" }, ct)).ResourceDescriptions ?? []);
    }

    /// <summary>
    /// Every resource the create step reported (a "Type: … → SUCCESS" line, then its identifier
    /// indented below it) is refused by GetResource: floci lists only buckets, so a leaked queue or
    /// topic has to be asked for by name.
    /// </summary>
    private async Task AssertGoneAsync(DemoStep created)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonCloudControlApi client = this.factory.Create();
        string[] lines = created.Response!.Split('\n');
        int checkedCount = 0;

        for (int i = 0; i + 1 < lines.Length; i++)
        {
            if (lines[i].StartsWith("AWS::", StringComparison.Ordinal) && lines[i + 1].StartsWith("  ", StringComparison.Ordinal))
            {
                string type = lines[i][..lines[i].IndexOf(": ", StringComparison.Ordinal)];
                string identifier = lines[i + 1].Trim();

                await Assert.ThrowsAsync<ResourceNotFoundException>(
                    () => client.GetResourceAsync(new GetResourceRequest { TypeName = type, Identifier = identifier }, ct));
                checkedCount++;
            }
        }

        Assert.Equal(3, checkedCount);
    }

    private static async Task<List<DemoStep>> RunAsync(CloudControlApiDemo demo)
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
