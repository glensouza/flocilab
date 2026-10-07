using Amazon.Scheduler;
using Amazon.Scheduler.Model;
using FlociLab.Aws.EventBridgeScheduler;
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
public sealed class AwsEventBridgeSchedulerTests : IAsyncLifetime
{
    // Same reasoning as AwsEventBridgePipesTests: pinned to :latest so the tripwire tracks the
    // same build the AppHost and the README's Compose stack run, not whatever
    // Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();

    private EventBridgeSchedulerClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.floci.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new EventBridgeSchedulerClientFactory(EndpointsFor(this.floci.GetConnectionString()));
    }

    public async ValueTask DisposeAsync() => await this.floci.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new EventBridgeSchedulerDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new EventBridgeSchedulerDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateSchedule", s.Title),
            s => Assert.Equal("GetSchedule", s.Title),
            s => Assert.Equal("ListSchedules", s.Title),
            s => Assert.Equal("UpdateSchedule", s.Title),
            s => Assert.Equal("DeleteSchedule — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));

        // "including" is the postcondition, not the count: the step throws unless the listing
        // actually contained the schedule this run created.
        Assert.Contains("including flocilab-schedule-", steps.Single(s => s.Title == "ListSchedules").Response);

        // The update step reads the schedule back rather than trusting the echoed ARN, so its
        // response carries the stored expression. This is the assertion that fails the day floci
        // starts accepting a PUT and ignoring it.
        Assert.Contains("Read back — ScheduleExpression: rate(10 minutes)", steps.Single(s => s.Title == "UpdateSchedule").Response);
    }

    /// <summary>
    /// The other half of the cleanup story: the demo's <c>ResourceNotFoundException</c> catch
    /// reports "the schedule was never created" as a *success*, which is only truthful because a
    /// 404 is proof nothing exists. If floci ever answers 200 to a delete of a missing schedule,
    /// that green badge becomes a lie and this test is what says so.
    /// </summary>
    [Fact]
    public async Task Deleting_A_Schedule_That_Was_Never_Created_Answers_NotFound()
    {
        using IAmazonScheduler client = this.factory.Create();

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => client.DeleteScheduleAsync(
                new DeleteScheduleRequest { Name = $"flocilab-schedule-missing-{Guid.NewGuid():N}" },
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Re-runnable because every run cleans up after itself, which is what makes the page safe to
    /// hammer during a recording.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Leaves_No_Schedules_Behind()
    {
        EventBridgeSchedulerDemo demo = new(this.factory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using IAmazonScheduler client = this.factory.Create();
        ListSchedulesResponse before = await client.ListSchedulesAsync(new ListSchedulesRequest(), ct);

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }

        ListSchedulesResponse after = await client.ListSchedulesAsync(new ListSchedulesRequest(), ct);

        // ?? [] rather than a bare dereference: AWSSDK v4 leaves an unset response collection
        // null, and floci only happens to send a populated array today — the same nullable-shape
        // trap the EventBridge Pipes sample recorded.
        Assert.Equal(
            (before.Schedules ?? []).Select(s => s.Name).Order(),
            (after.Schedules ?? []).Select(s => s.Name).Order());
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. The page cancels its token on
    /// dispose, so without this the act of navigating away would render five red steps blaming
    /// the emulator for the user leaving.
    ///
    /// Cancelled mid-run rather than up front: a token that is already cancelled makes the very
    /// first SDK call throw, so no step is ever yielded and "no failed steps" holds vacuously. The
    /// assertion only has teeth once at least one step has been observed.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        EventBridgeSchedulerDemo demo = new(this.factory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);

                // Cancel once the run is genuinely under way, which is what the page does when
                // the user navigates away mid-round-trip.
                await cts.CancelAsync();
            }
        });

        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// floci 2.2.0 refuses a <c>ScheduleExpression</c> that is not a <c>rate</c>, <c>cron</c> or
    /// <c>at</c> expression with <c>ValidationException</c>, as real EventBridge Scheduler does.
    /// 1.7.0 and 2.1.0 stored it verbatim — silent, because a typo in a cron expression
    /// round-tripped perfectly on the emulator and failed at deploy (docs/BLAZOR-PLAN.md §14).
    /// Pinned so a regression to silent acceptance fails loudly.
    /// </summary>
    [Fact]
    public async Task Floci_Refuses_A_Malformed_ScheduleExpression()
    {
        using IAmazonScheduler client = this.factory.Create();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = $"flocilab-schedule-malformed-{Guid.NewGuid():N}";

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(
            () => client.CreateScheduleAsync(
                new CreateScheduleRequest
                {
                    Name = name,
                    ScheduleExpression = "not-a-rate",
                    FlexibleTimeWindow = new FlexibleTimeWindow { Mode = FlexibleTimeWindowMode.OFF },
                    Target = new Target
                    {
                        Arn = "arn:aws:sqs:us-east-1:000000000000:flocilab-scheduler-target",
                        RoleArn = "arn:aws:iam::000000000000:role/flocilab-scheduler-role",
                    },
                }, ct));

        Assert.False(string.IsNullOrEmpty(ex.Message));

        // Refused means nothing was stored, so there is nothing to clean up.
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => client.GetScheduleAsync(new GetScheduleRequest { Name = name }, ct));
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        EventBridgeSchedulerDemo demo = new(new EventBridgeSchedulerClientFactory(EndpointsFor("http://127.0.0.1:1")));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
