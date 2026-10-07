using System.Diagnostics;
using System.Text.RegularExpressions;
using FlociLab.Core.Configuration;

// FlociLab orchestration: four emulator containers plus the unified web app, one F5.
// See docs/BLAZOR-PLAN.md §9. Aspire.Hosting.Floci does not exist yet (upstream issue #1242),
// so these are plain AddContainer resources with the README's Compose settings.

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// The emulator images ship their own HEALTHCHECK, but Aspire needs to be told which endpoint to
// poll for WaitFor to gate on real readiness rather than "the container started". The path is not
// uniform — floci-gcp and floci-oci namespace theirs and 404 on /_floci/health (checked against
// each image's HEALTHCHECK command, and by curl).
const string AwsHealth = "/_floci/health";
const string AzureHealth = "/_floci/health";
const string GcpHealth = "/_floci-gcp/health";
const string OciHealth = "/_floci-oci/health";

// Container-to-container DNS between the emulators needs nothing: Aspire aliases every container
// by its resource name on the app network, so http://floci-az:4577 resolves from inside floci.
// The sibling containers are the problem. Lambda, Functions, Cloud Run, Fn and the rest are
// started by the emulators themselves through the mounted Docker socket, and they land on the
// network named by FLOCI_SERVICES_LAMBDA_DOCKER_NETWORK — a name that has to be known when the
// emulator starts. Aspire's own network is aspire-persistent-network-<hash>-<apphost>, generated
// per machine, so it cannot be written down here. Hence a second, stably named network that every
// emulator also joins, using the same name as the README's Compose stack.
const string SharedNetwork = "floci";
EnsureNetwork(SharedNetwork);

IResourceBuilder<ContainerResource> aws = builder.AddContainer("floci", "floci/floci", "latest")
    .WithHttpEndpoint(port: 4566, targetPort: 4566, name: "http")
    // Deliberately NOT setting FLOCI_HOSTNAME, which would pin the advertised host explicitly.
    // What an unset FLOCI_HOSTNAME yields has changed under us: floci 1.7.0 handed back
    // http://floci:4566/... from CreateQueue (measured 2026-08-30), and floci 2.1.0 hands back
    // http://localhost:4566/... (measured 2026-09-28). Neither is right for both consumers —
    // the first does not resolve from FlociLab.All.Web on the host, the second not from a
    // sibling container.
    //
    // That is harmless for every sample so far: AWSSDK.SQS ships no endpoint-rewriting pipeline
    // handler, so a QueueUrl travels as a request parameter and the SDK always dials
    // config.ServiceURL. It would bite anything that treats a returned URL as a connect target --
    // a pre-signed S3 link being the obvious case, and no sample builds one yet. Revisit in
    // Phase 4, when emulator-started sibling containers become the second consumer (plan §14).
    .WithEnvironment("FLOCI_DEFAULT_REGION", "us-east-1")
    .WithEnvironment("FLOCI_STORAGE_MODE", "persistent")
    // Where the emulator puts the throwaway containers it starts for Lambda, ECS, EKS and friends.
    .WithEnvironment("FLOCI_SERVICES_LAMBDA_DOCKER_NETWORK", SharedNetwork)
    // S3 virtual-host-style addressing resolves against this name inside the network.
    .WithContainerNetworkAlias("localhost.floci.io")
    .WithVolume("flocilab-aws-data", "/app/data")
    .WithDockerSocket()
    .WithSharedNetwork(SharedNetwork, "floci", "localhost.floci.io")
    .WithHttpHealthCheck(AwsHealth, endpointName: "http")
    .WithLifetime(ContainerLifetime.Persistent);

IResourceBuilder<ContainerResource> azure = builder.AddContainer("floci-az", "floci/floci-az", "latest")
    .WithHttpEndpoint(port: 4577, targetPort: 4577, name: "http")
    // Event Hubs is AMQP 1.0 and does not go over the HTTP port (plan §7).
    .WithEndpoint(port: 5672, targetPort: 5672, name: "amqp-eventhubs")
    // Service Bus's AMQP port is deliberately NOT published here. floci-az's own process never
    // listens on 5673 — on the first Service Bus management call it launches a sidecar
    // `apache/activemq-artemis` container (via the mounted Docker socket) that binds host port
    // 5673 itself. Publishing 5673 on the floci-az container too made that sidecar's own bind fail
    // with "port is already allocated", which left the namespace stuck failing to start on every
    // retry (§14). Event Hubs' AMQP port above is unaffected only because no sample calls it yet.
    .WithEndpoint(port: 9093, targetPort: 9093, name: "kafka")
    // FLOCI_AZ_HOSTNAME / FLOCI_AZ_BASE_URL omitted for the same reason as FLOCI_HOSTNAME above:
    // the Blob and Queue endpoints they stamp into responses have to resolve from the host.
    .WithEnvironment("FLOCI_AZ_STORAGE_MODE", "persistent")
    // Service Bus defaults to mocked mode (management plane only, no Artemis sidecar) — MOCKED=false
    // is the one setting that actually matters; without it the AMQP data plane never accepts a
    // connection at all. START_ON_BOOT is the docs' recommendation for an orchestrator (start the
    // `default` namespace's sidecar with the emulator instead of on the first management call), kept
    // here for when a future floci-az honours it — but as of 0.11.0 the startup banner still reports
    // `(on-demand)` and the sidecar starts lazily on the first management call regardless, so the
    // AMQP port is not guaranteed listening the instant a client dials it (docs/BLAZOR-PLAN.md §14).
    .WithEnvironment("FLOCI_AZ_SERVICES_SERVICE_BUS_MOCKED", "false")
    .WithEnvironment("FLOCI_AZ_SERVICES_SERVICE_BUS_START_ON_BOOT", "true")
    .WithVolume("flocilab-az-data", "/app/data")
    .WithDockerSocket()
    .WithSharedNetwork(SharedNetwork, "floci-az")
    .WithHttpHealthCheck(AzureHealth, endpointName: "http")
    .WithLifetime(ContainerLifetime.Persistent);

IResourceBuilder<ContainerResource> gcp = builder.AddContainer("floci-gcp", "floci/floci-gcp", "latest")
    .WithHttpEndpoint(port: 4588, targetPort: 4588, name: "http")
    // FLOCI_GCP_HOSTNAME / FLOCI_GCP_BASE_URL omitted — see FLOCI_HOSTNAME above.
    .WithEnvironment("FLOCI_GCP_DEFAULT_PROJECT_ID", "floci-local")
    .WithEnvironment("FLOCI_GCP_STORAGE_MODE", "persistent")
    .WithVolume("flocilab-gcp-data", "/app/data")
    .WithDockerSocket()
    .WithSharedNetwork(SharedNetwork, "floci-gcp")
    .WithHttpHealthCheck(GcpHealth, endpointName: "http")
    .WithLifetime(ContainerLifetime.Persistent);

IResourceBuilder<ContainerResource> oci = builder.AddContainer("floci-oci", "floci/floci-oci", "latest")
    .WithHttpEndpoint(port: 4599, targetPort: 4599, name: "http")
    // FLOCI_OCI_HOSTNAME omitted — see FLOCI_HOSTNAME above.
    .WithEnvironment("FLOCI_OCI_STORAGE_MODE", "persistent")
    // The image issues no tenancy OCID of its own, so the lab supplies one and the samples
    // default to the same value.
    .WithEnvironment("FLOCI_OCI_DEFAULT_TENANCY_ID", OciEmulatorOptions.DefaultTenancyId)
    .WithVolume("flocilab-oci-data", "/app/data")
    .WithDockerSocket()
    .WithSharedNetwork(SharedNetwork, "floci-oci")
    .WithHttpHealthCheck(OciHealth, endpointName: "http")
    .WithLifetime(ContainerLifetime.Persistent);

// The web console. Not an emulator — it is a client of three of them, and it reaches them by
// container name over the app network, so it needs no host ports of theirs. There is no OCI
// support in the image, which is why floci-oci is absent below.
builder.AddContainer("floci-ui", "floci/floci-ui", "latest")
    .WithHttpEndpoint(port: 4500, targetPort: 4500, name: "http")
    .WithEnvironment("PORT", "4500")
    .WithEnvironment("FLOCI_ENDPOINT", "http://floci:4566")
    .WithEnvironment("FLOCI_AZURE_ENDPOINT", "http://floci-az:4577")
    .WithEnvironment("FLOCI_AZURE_ACCOUNT_NAME", "devstoreaccount1")
    .WithEnvironment("FLOCI_GCP_ENDPOINT", "http://floci-gcp:4588")
    .WithEnvironment("FLOCI_GCP_PROJECT", "floci-local")
    .WithEnvironment("AWS_REGION", "us-east-1")
    .WithEnvironment("AWS_ACCESS_KEY_ID", "test")
    .WithEnvironment("AWS_SECRET_ACCESS_KEY", "test")
    .WithSharedNetwork(SharedNetwork, "floci-ui")
    // This image ships no HEALTHCHECK of its own, unlike the four emulators.
    .WithHttpHealthCheck("/", endpointName: "http")
    .WithLifetime(ContainerLifetime.Persistent)
    .WaitFor(aws)
    .WaitFor(azure)
    .WaitFor(gcp);

builder.AddProject<Projects.FlociLab_All_Web>("all")
    // Bound by FlociOptions. The web app runs on the host, so these resolve to localhost:45xx;
    // the same build reads Floci__Aws__Endpoint=http://floci:4566 inside the Compose network.
    .WithEnvironment("Floci__Aws__Endpoint", aws.GetEndpoint("http"))
    .WithEnvironment("Floci__Azure__Endpoint", azure.GetEndpoint("http"))
    .WithEnvironment("Floci__Gcp__Endpoint", gcp.GetEndpoint("http"))
    .WithEnvironment("Floci__Oci__Endpoint", oci.GetEndpoint("http"))
    // Azure.Identity reads only this variable to find a non-default IMDS address, and it wants a
    // host — it appends /metadata/identity/oauth2/token itself.
    .WithEnvironment("AZURE_POD_IDENTITY_AUTHORITY_HOST", azure.GetEndpoint("http"))
    .WaitFor(aws)
    .WaitFor(azure)
    .WaitFor(gcp)
    .WaitFor(oci);

// Persistent containers outlive the AppHost, and Aspire reuses one as long as its spec is
// unchanged — a new image behind the same :latest tag is not a change. So without this the lab
// kept running whatever floci it first started: an eight-day-old 2.1.0 while the tests, which
// start fresh containers, were already on 2.2.0. Set FlociLab:RefreshImages=false to skip it
// (offline, or pinned on purpose).
if (!string.Equals(builder.Configuration["FlociLab:RefreshImages"], "false", StringComparison.OrdinalIgnoreCase))
{
    RefreshFlociImages(["floci", "floci-az", "floci-gcp", "floci-oci", "floci-ui"]);
}

builder.Build().Run();

// Pulls floci/<name>:latest for each lab container, then removes any lab container still running
// an older image so the AppHost recreates it on the new one. Only the containers are removed:
// emulator state lives in the flocilab-*-data volumes and survives. A failed pull (offline, Docker
// Hub rate limit) is reported and the existing container is kept, because an old emulator is
// better than none.
static void RefreshFlociImages(string[] resources)
{
    // Aspire names a persistent container <resource>-<8 hex chars, fixed per AppHost>.
    (int listed, string containers) = Capture("docker", "ps -a --format \"{{.Names}} {{.Image}} {{.ID}}\"", 30_000);

    if (listed != 0)
    {
        Console.Error.WriteLine("RefreshFlociImages: docker is not answering; leaving the lab containers as they are.");
        return;
    }

    foreach (string resource in resources)
    {
        string image = $"floci/{resource}:latest";
        (int pulled, string pullOutput) = Capture("docker", $"pull -q {image}", 300_000);

        if (pulled != 0)
        {
            Console.Error.WriteLine($"RefreshFlociImages: could not pull {image}, keeping the current container. {pullOutput.Trim()}");
            continue;
        }

        (_, string latestId) = Capture("docker", $"image inspect --format \"{{{{.Id}}}}\" {image}", 30_000);

        foreach (string line in containers.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split(' ');

            if (fields.Length != 3 || !Regex.IsMatch(fields[0], $"^{Regex.Escape(resource)}-[0-9a-f]{{8}}$"))
            {
                continue;
            }

            (_, string runningId) = Capture("docker", $"inspect --format \"{{{{.Image}}}}\" {fields[2]}", 30_000);

            if (runningId.Trim() == latestId.Trim())
            {
                continue;
            }

            Console.WriteLine($"RefreshFlociImages: {fields[0]} runs an older {image}; removing it so the AppHost recreates it.");
            Capture("docker", $"rm -f {fields[2]}", 60_000);
        }
    }

    static (int ExitCode, string Output) Capture(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return (-1, string.Empty);
            }

            // Drain both pipes before waiting, for the reason EnsureNetwork's Run gives.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "timed out");
            }

            Task.WaitAll(stdout, stderr);
            return (process.ExitCode, process.ExitCode == 0 ? stdout.Result : stderr.Result);
        }
        catch (Exception ex)
        {
            // Docker missing or not on PATH: report it as a failed command, which every caller handles.
            return (-1, ex.Message);
        }
    }
}

// Creates the shared network if it does not exist yet. Has to run before the app starts, because
// "docker run --network floci" fails rather than creating one. Idempotent, and safe against a
// network the README's Compose stack created first.
static void EnsureNetwork(string name)
{
    string[] runtimes = ["docker", "podman"];

    foreach (string runtime in runtimes)
    {
        if (Run(runtime, $"network inspect {name}") == 0 || Run(runtime, $"network create {name}") == 0)
        {
            return;
        }
    }

    throw new InvalidOperationException(
        $"Could not create the '{name}' container network with docker or podman. Start the " +
        $"container runtime, or create it by hand: docker network create {name}");

    static int Run(string fileName, string arguments)
    {
        const int TimeoutMs = 30_000;

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return -1;
            }

            // Both pipes must be drained before waiting. `docker network inspect floci` prints the
            // whole network document, including an endpoint block per attached container, which
            // outgrows the 4 KB redirect buffer once a few emulators have joined. The child then
            // blocks on write, the wait times out, and an existing healthy network gets reported
            // as uncreatable.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return -1;
            }

            Task.WaitAll(stdout, stderr);
            return process.ExitCode;
        }
        catch (Exception)
        {
            // Runtime missing or not on PATH — try the next one.
            return -1;
        }
    }
}

internal static class FlociContainerExtensions
{
    /// <summary>
    /// Lambda, RDS, ECS, EKS, Functions, Cloud Run, GKE and OKE are backed by real containers, so
    /// the emulators drive the host Docker daemon. Passed as a raw runtime argument rather than
    /// WithBindMount because the socket path is not a host filesystem path on Windows — the daemon
    /// resolves it, and Aspire would otherwise try to make it absolute against the AppHost folder.
    /// </summary>
    public static IResourceBuilder<ContainerResource> WithDockerSocket(
        this IResourceBuilder<ContainerResource> builder)
        => builder.WithContainerRuntimeArgs("-v", "/var/run/docker.sock:/var/run/docker.sock");

    /// <summary>
    /// Attaches the container to a second, stably named network alongside Aspire's own, so the
    /// containers an emulator starts later can be given a network name that exists. Docker accepts
    /// repeated --network flags and joins all of them.
    ///
    /// The alias matters as much as the network. On its own network Aspire aliases each container
    /// by resource name, but on this one the container is only known by its generated name
    /// (floci-0ba94062), so a Lambda sibling resolving http://floci:4566 would not find it. The
    /// name=,alias= form registers the short name here too.
    /// </summary>
    public static IResourceBuilder<ContainerResource> WithSharedNetwork(
        this IResourceBuilder<ContainerResource> builder, string network, params string[] aliases)
        => builder.WithContainerRuntimeArgs(
            "--network",
            string.Join(',', [$"name={network}", .. aliases.Select(a => $"alias={a}")]));
}
