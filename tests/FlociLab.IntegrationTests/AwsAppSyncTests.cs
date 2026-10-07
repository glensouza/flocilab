using System.Diagnostics;
using Amazon.AppSync;
using Amazon.AppSync.Model;
using FlociLab.Aws.AppSync;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.Floci;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci per class (docs/BLAZOR-PLAN.md §10). Nothing here talks to the emulator the
/// AppHost runs, so the suite passes on a machine that has never started the lab.
///
/// <para>
/// Since floci 2.2.0 the GraphQL engine is a <i>sidecar</i> container floci starts itself through
/// the Docker socket, on the first schema it has to load. Without the socket the schema stays
/// <c>PROCESSING</c> forever. So the socket is mounted, and the sidecar — <c>floci-aws-graphql</c>,
/// a Docker-host singleton like Service Bus's Artemis — is brought up in
/// <see cref="InitializeAsync"/> (the first image pull can outlast the demo's own 30 s budget) and
/// removed afterwards only if this run is what started it.
/// </para>
/// </summary>
public sealed class AwsAppSyncTests : IAsyncLifetime
{
    private const string GraphqlSidecarName = "floci-aws-graphql";

    // Pinned to :latest so the tripwire tracks the same build the AppHost and the README's Compose
    // stack run, not whatever Testcontainers.Floci defaults to.
    private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest")
        .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock")
        .Build();

    private bool sidecarCreatedByThisRun;

    // The real IHttpClientFactory the demo runs under in a host, from a minimal service provider.
    private readonly ServiceProvider httpServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();

    private AppSyncClientFactory factory = null!;

    private IHttpClientFactory HttpClientFactory => this.httpServices.GetRequiredService<IHttpClientFactory>();

    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await this.floci.StartAsync(ct);
        this.factory = new AppSyncClientFactory(EndpointsFor(this.floci.GetConnectionString()));

        // Set before the warm-up, not after: a warm-up that throws may already have started the
        // sidecar, and it must still go.
        this.sidecarCreatedByThisRun = !await SidecarExistsAsync();

        // Loading any schema is what starts the sidecar. Waits far longer than the demo does, so a
        // slow first pull fails setup with the cause rather than every test with "still PROCESSING".
        using IAmazonAppSync client = this.factory.Create();
        CreateGraphqlApiResponse api = await client.CreateGraphqlApiAsync(
            new CreateGraphqlApiRequest { Name = $"flocilab-warmup-{Guid.NewGuid().ToString("N")[..8]}", AuthenticationType = AuthenticationType.API_KEY }, ct);

        using MemoryStream definition = new("schema { query: Query }\ntype Query { ping: String }"u8.ToArray());
        await client.StartSchemaCreationAsync(new StartSchemaCreationRequest { ApiId = api.GraphqlApi.ApiId, Definition = definition }, ct);

        long deadline = Stopwatch.GetTimestamp() + (long)(TimeSpan.FromMinutes(3).TotalSeconds * Stopwatch.Frequency);
        GetSchemaCreationStatusResponse status = await client.GetSchemaCreationStatusAsync(new GetSchemaCreationStatusRequest { ApiId = api.GraphqlApi.ApiId }, ct);

        while (status.Status == SchemaStatus.PROCESSING)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new InvalidOperationException("the GraphQL sidecar warm-up never left PROCESSING within 3 minutes — is the Docker socket reachable from floci?");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            status = await client.GetSchemaCreationStatusAsync(new GetSchemaCreationStatusRequest { ApiId = api.GraphqlApi.ApiId }, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.floci.DisposeAsync();
        await this.httpServices.DisposeAsync();

        // floci does not stop the sidecar when it stops itself, so it has to go explicitly — but a
        // blanket rm would tear a running dev stack's engine out from under it.
        if (this.sidecarCreatedByThisRun)
        {
            await RunDockerAsync($"rm -f {GraphqlSidecarName}");
        }
    }

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new AppSyncDemo(this.factory, this.HttpClientFactory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new AppSyncDemo(this.factory, this.HttpClientFactory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateGraphqlApi", s.Title),
            s => Assert.Equal("CreateApiKey", s.Title),
            s => Assert.Equal("StartSchemaCreation", s.Title),
            s => Assert.Equal("CreateDataSource (NONE)", s.Title),
            s => Assert.Equal("CreateResolver (Query.echo)", s.Title),
            s => Assert.Equal("Query with the API key", s.Title),
            s => Assert.Equal("Query without a key (expect 401)", s.Title),
            s => Assert.Equal("DeleteGraphqlApi — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// floci 2.2.0 executes a UNIT resolver over a NONE data source through its GraphQL sidecar,
    /// so the echoed string comes back; 2.1.0 answered <c>{"data":{"echo":null}}</c> for the same
    /// API (docs/BLAZOR-PLAN.md §14). If this starts failing with a null, the sidecar is not
    /// executing resolvers.
    /// </summary>
    [Fact]
    public async Task Query_Resolver_Executes_And_Echoes_The_Argument()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new AppSyncDemo(this.factory, this.HttpClientFactory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        DemoStep query = steps.Single(s => s.Title == "Query with the API key");

        Assert.Contains("The resolver executed", query.Response);
        Assert.DoesNotContain("\"echo\":null", query.Response);
    }

    /// <summary>
    /// Re-runnable because every run creates a uniquely named GraphQL API and removes it in cleanup.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Runs_Twice_Without_Colliding()
    {
        AppSyncDemo demo = new(this.factory, this.HttpClientFactory);
        CancellationToken ct = TestContext.Current.CancellationToken;

        for (int run = 0; run < 2; run++)
        {
            await foreach (DemoStep step in demo.RunAsync(ct))
            {
                Assert.True(step.Succeeded, $"run {run}, {step.Title}: {step.Error}");
            }
        }
    }

    /// <summary>
    /// A cancelled run stops; it does not manufacture failed steps. Cancelled mid-run rather than
    /// up front: a token that is already cancelled makes the first SDK call throw, so no step is
    /// ever yielded and "no failed steps" would hold vacuously.
    /// </summary>
    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps()
    {
        AppSyncDemo demo = new(this.factory, this.HttpClientFactory);
        List<DemoStep> steps = [];

        using CancellationTokenSource cts = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (DemoStep step in demo.RunAsync(cts.Token))
            {
                steps.Add(step);

                await cts.CancelAsync();
            }
        });

        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
    }

    /// <summary>
    /// Nothing listening has to read as Unreachable, not Error, or a stopped emulator looks like a
    /// broken sample. Port 1 is reserved and never bound, so no container is needed.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        AppSyncDemo demo = new(new AppSyncClientFactory(EndpointsFor("http://127.0.0.1:1")), this.HttpClientFactory);

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// A run that fails at the first step yields exactly that step plus the cleanup the finally
    /// always produces. With no id back, cleanup finds the API by name — and against a stopped
    /// emulator that lookup is honestly red too.
    /// </summary>
    [Fact]
    public async Task Failed_Run_Yields_Only_The_Steps_It_Reached()
    {
        AppSyncDemo demo = new(new AppSyncClientFactory(EndpointsFor("http://127.0.0.1:1")), this.HttpClientFactory);
        List<DemoStep> steps = [];

        await foreach (DemoStep step in demo.RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("CreateGraphqlApi", s.Title),
            s => Assert.Equal("DeleteGraphqlApi — cleanup", s.Title));
        Assert.False(steps[0].Succeeded);
        Assert.False(steps[1].Succeeded, "cleanup claimed success against an emulator it could not reach");
        Assert.Contains("ListGraphqlApisAsync", steps[1].Request);
    }

    private static async Task<bool> SidecarExistsAsync()
        => (await RunDockerAsync($"ps -a --filter name=^{GraphqlSidecarName}$ --format {{{{.Names}}}}")).Contains(GraphqlSidecarName, StringComparison.Ordinal);

    private static async Task<string> RunDockerAsync(string arguments)
    {
        ProcessStartInfo startInfo = new("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("docker did not start.");

        // Read before waiting: a process whose output fills the pipe buffer blocks forever on exit.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync();

        // A failed `docker ps` must not read as "no sidecar": that would mark someone else's
        // sidecar as ours, and DisposeAsync would then remove it.
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {arguments} exited {process.ExitCode}: {await stderr}");
        }

        return await stdout;
    }

    private static AwsEndpoints EndpointsFor(string endpoint)
        => new(Options.Create(new FlociOptions { Aws = new AwsEmulatorOptions { Endpoint = endpoint } }));
}
