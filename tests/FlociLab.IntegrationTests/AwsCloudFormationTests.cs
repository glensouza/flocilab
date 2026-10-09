using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using FlociLab.Aws.CloudFormation;
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
public sealed class AwsCloudFormationTests : IAsyncLifetime
{
    // Pinned to :latest so the tests track the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private CloudFormationClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new CloudFormationClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new CloudFormationDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = await RunAsync(new CloudFormationDemo(this.factory));

        Assert.Collection(
            steps,
            s =>
            {
                Assert.Equal("ValidateTemplate — what the template declares", s.Title);

                // Tripwire: floci answers an empty parameter list for any body. When this fails,
                // floci reads the template and lists Prefix.
                Assert.StartsWith("0 parameters, although the template declares Prefix", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateStack, DescribeStacks — a queue and a bucket from one template", s.Title);

                // Tripwire: floci provisions synchronously; real CloudFormation reads CREATE_IN_PROGRESS first.
                Assert.StartsWith("flocilab-", s.Response);
                Assert.Contains(": CREATE_COMPLETE", s.Response);
                Assert.Contains("output QueueUrl = ", s.Response);
            },
            s =>
            {
                Assert.Equal("DescribeStackResources, ListStackResources — the resource tree", s.Title);
                Assert.Contains("Queue AWS::SQS::Queue: CREATE_COMPLETE", s.Response);
                Assert.Contains("Bucket AWS::S3::Bucket: CREATE_COMPLETE", s.Response);
            },
            s =>
            {
                Assert.Equal("DescribeStackEvents — what happened, oldest first", s.Title);
                Assert.Contains("AWS::CloudFormation::Stack  CREATE_COMPLETE", s.Response);
            },
            s => Assert.Equal("GetTemplate — the body comes back", s.Title),
            s =>
            {
                Assert.Equal("CreateChangeSet, DescribeChangeSet, ExecuteChangeSet — adding a topic", s.Title);
                Assert.Contains("Add Topic (AWS::SNS::Topic)", s.Response);
                Assert.Contains(": UPDATE_COMPLETE", s.Response);
                Assert.Contains("3 resource(s) now", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateStack — a name that is taken", s.Title);

                // Matches real CloudFormation.
                Assert.StartsWith("AlreadyExistsException", s.Response);
            },
            s =>
            {
                Assert.Equal("CreateStack — a resource type that does not exist", s.Title);

                // Tripwire: floci accepts any resource type; real CloudFormation answers ValidationError.
                Assert.StartsWith("accepted, and the stack reads CREATE_COMPLETE", s.Response);
            },
            s =>
            {
                Assert.Equal("DetectStackDrift — has the stack moved from its template", s.Title);

                // Tripwire: floci answers UnknownAction (HTTP 400, not 501). When this starts
                // failing, drift detection landed.
                Assert.StartsWith("UnknownAction", s.Response);
            },
            s =>
            {
                Assert.Equal("Delete everything — cleanup", s.Title);

                // Both stacks were found by name and removed, not "nothing to remove".
                Assert.StartsWith("Deleted 2 stack(s)", s.Response);
            });

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>Re-runnable, and a run leaves nothing behind.</summary>
    [Fact]
    public async Task RoundTrip_Twice_Leaves_Nothing_Behind()
    {
        CloudFormationDemo demo = new(this.factory);

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

        await foreach (DemoStep step in new CloudFormationDemo(this.factory).RunAsync(ct))
        {
            if (step.Title.StartsWith("DescribeStackResources", StringComparison.Ordinal))
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
        CloudFormationDemo demo = new(new CloudFormationClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private async Task AssertAccountIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IAmazonCloudFormation client = this.factory.Create();

        // ListStacks keeps deleted stacks as DELETE_COMPLETE history; a stack still alive is the leak.
        Assert.DoesNotContain(
            (await client.ListStacksAsync(new ListStacksRequest(), ct)).StackSummaries ?? [],
            s => s.StackStatus != StackStatus.DELETE_COMPLETE);
    }

    private static async Task<List<DemoStep>> RunAsync(CloudFormationDemo demo)
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
