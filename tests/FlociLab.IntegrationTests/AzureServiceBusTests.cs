using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FlociLab.Azure.ServiceBus;
using FlociLab.Core;
using FlociLab.Core.Configuration;
using FlociLab.Core.Endpoints;
using Microsoft.Extensions.Options;
using Xunit;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace FlociLab.IntegrationTests;

/// <summary>
/// One throwaway floci-az per class (docs/BLAZOR-PLAN.md §10), with its Service Bus Artemis sidecar
/// enabled and the Docker socket mounted so it can start one — Service Bus defaults to mocked mode
/// (management plane only) otherwise.
///
/// <para>
/// <b>The sidecar is a Docker-host singleton, and that shapes this whole fixture.</b> floci-az names
/// it <c>floci-az-servicebus-default</c> — derived from the namespace ("default"), with no per-run
/// or per-port suffix — and launches it as a *sibling* on the host daemon through the mounted
/// socket, not as a child Testcontainers can reap. So it outlives <c>this.flociAz</c>, and only one
/// can exist per machine no matter how many floci-az containers are alive. Two consequences, both
/// of which this fixture has to handle rather than assume away:
/// </para>
///
/// <para>
/// 1. <b>The AMQP port cannot simply be pinned.</b> <see cref="PreferredAmqpPort"/> is what this run
/// *asks* for, and it is deliberately not 5673 (the README/AppHost default) so a dev stack's own
/// sidecar is never mistaken for this run's. But if a sidecar of that fixed name is already up —
/// `dotnet run --project src/FlociLab.AppHost` in another terminal, both documented workflows in
/// CLAUDE.md — floci-az attaches to it and the asked-for port is simply not what is listening.
/// <see cref="InitializeAsync"/> therefore *discovers* the published port off the running container
/// instead of assuming it, so the tests work either way.
/// </para>
///
/// <para>
/// 2. <b>Only a sidecar this run created may be removed.</b> A blanket <c>docker rm -f</c> on that
/// fixed name would tear a developer's running dev stack apart from underneath it. Existence is
/// recorded before the sidecar is started and <see cref="DisposeAsync"/> removes it only if this run
/// is what brought it up.
/// </para>
///
/// <para>
/// The sidecar also starts <i>lazily</i>, on the first entity-management call rather than at boot —
/// as of floci-az 0.11.0 <c>START_ON_BOOT</c> is accepted but not honoured (§14). Since the demo's
/// clients run with <c>MaxRetries = 0</c>, a test that dialled AMQP before Artemis finished booting
/// would fail spuriously, so <see cref="InitializeAsync"/> forces the sidecar up and waits for the
/// port to accept a connection before any <c>[Fact]</c> runs.
/// </para>
///
/// <para>
/// <c>DeleteQueue — cleanup</c> failed every run through floci-az 0.12.0: its router misread the
/// bare-queue-name DELETE the official <see cref="ServiceBusAdministrationClient"/> sends as a Blob
/// request, which 501'd. floci-az 0.13.0 routes it (found 2026-09-28, §14), so the round trip is
/// now green end to end and the delete is asserted to remove the queue.
/// </para>
/// </summary>
public sealed class AzureServiceBusTests : IAsyncLifetime
{
    private const int FlociAzPort = 4577;

    // What this run asks for when it is the one starting the sidecar. Deliberately not 5673 — see
    // this class's remarks.
    private const int PreferredAmqpPort = 5683;

    // Artemis's own container port. floci-az publishes it to a host port; which one is what
    // SidecarPublishedPortAsync reads back.
    private const string ArtemisAmqpContainerPort = "5672/tcp";

    // The sidecar's fixed name — see this class's remarks.
    private const string ArtemisSidecarName = "floci-az-servicebus-default";

    private static readonly TimeSpan AmqpStartupTimeout = TimeSpan.FromMinutes(2);

    // A plain ContainerBuilder rather than the FlociBuilder the S3 tests use — see AzureBlobTests
    // for why (Testcontainers.Floci hardcodes port 4566, floci-az listens on 4577).
    private readonly IContainer flociAz = new ContainerBuilder("floci/floci-az:latest")
        .WithPortBinding(FlociAzPort, assignRandomHostPort: true)
        .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock")
        .WithEnvironment("FLOCI_AZ_SERVICES_SERVICE_BUS_MOCKED", "false")
        .WithEnvironment("FLOCI_AZ_SERVICES_SERVICE_BUS_AMQP_PORT", PreferredAmqpPort.ToString(CultureInfo.InvariantCulture))
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request.ForPath("/_floci/health").ForPort(FlociAzPort)))
        .Build();

    private ServiceBusClientFactory factory = null!;

    private bool sidecarCreatedByThisRun;

    private string Endpoint => $"http://{this.flociAz.Hostname}:{this.flociAz.GetMappedPublicPort(FlociAzPort)}";

    public async ValueTask InitializeAsync()
    {
        await this.flociAz.StartAsync(TestContext.Current.CancellationToken);

        // Recorded *before* anything starts the sidecar, so DisposeAsync can tell "we brought this
        // up" from "it was someone else's and must be left alone".
        bool sidecarExistedAlready = await SidecarPublishedPortAsync() is not null;

        // Force the lazily-started sidecar up. Creating a queue is the cheapest call that does it;
        // listing queues answers 200 without ever starting Artemis (verified by curl against a
        // running floci-az 0.11.0, 2026-09-03). The queue is left behind — harmless in a throwaway
        // container.
        ServiceBusAdministrationClient warmup =
            new ServiceBusClientFactory(EndpointsFor(this.Endpoint, PreferredAmqpPort)).CreateAdministrationClient();

        await warmup.CreateQueueAsync($"flocilab-warmup-{Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        this.sidecarCreatedByThisRun = !sidecarExistedAlready;

        // Discovered rather than assumed: if the sidecar was already running, it is published on
        // whichever port *that* stack asked for, not PreferredAmqpPort.
        int amqpPort = await SidecarPublishedPortAsync() ?? PreferredAmqpPort;

        this.factory = new ServiceBusClientFactory(EndpointsFor(this.Endpoint, amqpPort));

        await WaitForAmqpAsync(amqpPort, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await this.flociAz.DisposeAsync();

        // floci-az does not stop this sidecar when it stops itself, so it has to go explicitly —
        // but only when this run is what started it. Removing a sidecar that was already up would
        // take down a concurrently running dev stack's Service Bus (see this class's remarks).
        if (this.sidecarCreatedByThisRun)
        {
            await RunDockerAsync($"rm -f {ArtemisSidecarName}");
        }
    }

    [Fact]
    public async Task Probe_Reports_Ok()
    {
        ProbeResult result = await new ServiceBusDemo(this.factory).ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Ok, result.Status);
    }

    /// <summary>
    /// The classification the coverage matrix depends on: nothing listening has to read as
    /// Unreachable, not Error, or a stopped emulator looks like a broken sample. Port 1 is
    /// reserved and never bound, so no container is needed. The management plane is HTTP, so this
    /// never touches the AMQP port discovered above.
    /// </summary>
    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        ServiceBusDemo demo = new(new ServiceBusClientFactory(EndpointsFor("http://127.0.0.1:1", PreferredAmqpPort)));

        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// Every step genuinely round-trips — management over HTTP, data over the Artemis-backed AMQP
    /// plane — including the cleanup that floci-az could not route through 0.12.0 (§14).
    /// </summary>
    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds()
    {
        List<DemoStep> steps = [];

        await foreach (DemoStep step in new ServiceBusDemo(this.factory).RunAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(step);
        }

        Assert.Collection(
            steps,
            s => Assert.Equal("ListQueues — before", s.Title),
            s => Assert.Equal("CreateQueue", s.Title),
            s => Assert.Equal("SendMessage", s.Title),
            s => Assert.Equal("ReceiveMessage", s.Title),
            s => Assert.Equal("CompleteMessage", s.Title),
            s => Assert.Equal("DeleteQueue — cleanup", s.Title));

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Title}: {s.Error}"));
        Assert.Contains("Hello from FlociLab.", steps[3].Response);

        // A first delivery counts 1, as on real Service Bus. floci-az 0.13.0 reported 2; 0.14.0
        // restored zero-based AMQP delivery counts (floci-az #314, plan §13).
        Assert.Contains("DeliveryCount: 1", steps[3].Response);
    }

    /// <summary>
    /// The delete in isolation, and its postcondition rather than its status code: the queue is
    /// gone afterwards. Was a clean 501 through floci-az 0.12.0 (§14); this is the tripwire the
    /// other way now.
    /// </summary>
    [Fact]
    public async Task DeleteQueue_Removes_The_Queue()
    {
        ServiceBusAdministrationClient admin = this.factory.CreateAdministrationClient();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string queueName = $"flocilab-probe-{Guid.NewGuid():N}";

        await admin.CreateQueueAsync(queueName, ct);
        await admin.DeleteQueueAsync(queueName, ct);

        Assert.False((await admin.QueueExistsAsync(queueName, ct)).Value);
    }

    /// <summary>
    /// The host port the running Artemis sidecar publishes its AMQP listener on, or <c>null</c> if
    /// no sidecar is running. Doubles as the existence check in <see cref="InitializeAsync"/>.
    /// </summary>
    private static async Task<int?> SidecarPublishedPortAsync()
    {
        (int exitCode, string output) = await RunDockerAsync(
            $"inspect {ArtemisSidecarName} --format \"{{{{(index (index .NetworkSettings.Ports \\\"{ArtemisAmqpContainerPort}\\\") 0).HostPort}}}}\"");

        if (exitCode != 0)
        {
            return null;
        }

        return int.TryParse(output.Trim(), CultureInfo.InvariantCulture, out int port) ? port : null;
    }

    /// <summary>
    /// Waits for Artemis to accept a TCP connection. Booting the <c>apache/activemq-artemis</c>
    /// image takes seconds — longer on the first run, which pulls it — and the demo's clients run
    /// with <c>MaxRetries = 0</c>, so without this the first AMQP test to run would fail
    /// spuriously depending on which <c>[Fact]</c> xUnit happened to schedule first.
    /// </summary>
    private static async Task WaitForAmqpAsync(int port, CancellationToken ct)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(AmqpStartupTimeout.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            try
            {
                using TcpClient probe = new();
                await probe.ConnectAsync("127.0.0.1", port, ct);

                return;
            }
            // Nothing listening yet. Retried until the deadline, then reported as a failure naming
            // the port, which is far easier to read than an AMQP timeout deep inside a demo step.
            catch (SocketException) when (Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(
                    $"The Artemis sidecar '{ArtemisSidecarName}' never accepted a connection on port {port} "
                    + $"within {AmqpStartupTimeout.TotalSeconds:0}s. Check `docker logs {ArtemisSidecarName}`.",
                    ex);
            }
        }
    }

    /// <summary>
    /// Runs the Docker CLI. Used rather than Testcontainers' own client because this sibling
    /// container was launched by floci-az through the mounted socket, so Testcontainers never had a
    /// handle on it to reap. It therefore resolves the daemon the way every other Docker call in
    /// this repo's tooling does — through the ambient CLI context.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunDockerAsync(string arguments)
    {
        ProcessStartInfo startInfo = new("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using Process? process = Process.Start(startInfo);

            if (process is null)
            {
                return (-1, string.Empty);
            }

            // Read before waiting: a process whose output fills the pipe buffer blocks forever on
            // exit if nobody is draining it.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
        // No Docker CLI on PATH. Every caller here treats that as "no sidecar to see or remove",
        // which degrades to the previous pinned-port behaviour rather than failing the class in
        // its constructor-equivalent.
        catch (Win32Exception)
        {
            return (-1, string.Empty);
        }
    }

    private static AzureEndpoints EndpointsFor(string endpoint, int amqpPort) => new(Options.Create(new FlociOptions
    {
        Azure = new AzureEmulatorOptions { Endpoint = endpoint, ServiceBusAmqpPort = amqpPort },
    }));
}
