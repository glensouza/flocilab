# The RCL template

Phase 1 (`docs/BLAZOR-PLAN.md` §12) shipped four samples — `FlociLab.Aws.S3.Demo`,
`FlociLab.Azure.Blob.Demo`, `FlociLab.Gcp.Storage.Demo`, `FlociLab.Oci.ObjectStorage.Demo` — to prove
one shape works across all four providers before Phase 2 multiplies it by ~20. This page is that
shape, extracted from the four real samples rather than invented ahead of them. It is **not** a
`dotnet new` scaffold; there is no code-generation tool. Copying the nearest existing sample (per
`CLAUDE.md`, provider consistency beats cross-provider consistency) and adjusting against this page
is the intended workflow — that is what `/next` Step 5 already tells you to do.

Provider-specific endpoint wiring (AWS easy, OCI easy-to-sign-but-silent, Azure three planes, GCP
two routes) is `docs/BLAZOR-PLAN.md` §7 — not repeated here.

## Read this first: where the four diverge

The skeletons below are written in AWS's shape because `samples/aws/s3/` is the reference sample.
**Everything in this table diverges**, and copying the AWS answer into the wrong provider produces
a bug that still compiles. Check it before copying anything.

| | AWS | Azure | GCP | OCI |
|---|---|---|---|---|
| `FlociLab.<Provider>.Endpoints` ProjectReference | yes | **no** | **no** | yes |
| Client lifetime | new per `Create()` | new per `Create()` | **cached** | **cached** |
| Factory is `IDisposable` | no | no | **yes** | **yes** |
| `using` on the client in `RunAsync` | **yes** | no | no | no |
| Endpoint property on the factory | `ServiceUrl` | `ServiceUrl` | `BaseUri`, `UploadUri` | `Endpoint` (**nullable**) |
| Test container type | `FlociBuilder` | `ContainerBuilder` | `ContainerBuilder` | `ContainerBuilder` |

Three consequences worth stating outright, because each is a silent failure:

- **`AzureEndpoints` and `GcpEndpoints` live in `FlociLab.Core`**, so those two samples reference
  Core and nothing else. Adding the Endpoints project anyway drags `Azure.Identity` into an
  `Azure.Storage.Blobs` sample and the `Google.Api.Gax.Grpc` stack into a `Google.Cloud.Storage.V1`
  sample that deliberately speaks REST — a second cloud package, which is constraint 1. Confirm
  with `dotnet list <sample> package --include-transitive`.
- **A cached client must not be wrapped in `using`.** `StorageClientFactory` and
  `ObjectStorageClientFactory` hand back a shared instance; `using` disposes the singleton, and
  every run after the first throws `ObjectDisposedException`. The caching is not optional — a
  client per operation is a connection pool per operation, which billed the GCS comparison column
  ~2 s per call (plan §14).
- **Only AWS gets `FlociBuilder`.** `Testcontainers.Floci` models the AWS emulator's single fixed
  port; the other three images need a plain `ContainerBuilder` with the port declared by hand, and
  `IContainer` has no `GetConnectionString()`.

## File tree

```
samples/<provider>/<service>/FlociLab.<Provider>.<Service>.Demo/
├── FlociLab.<Provider>.<Service>.Demo.csproj   # one cloud package + FrameworkReference + refs
├── <Service>ClientFactory.cs                   # endpoint wiring, ServiceUrl, UseEmulator, Create()
├── <Service>Demo.cs                            # IServiceDemo: ProbeAsync, RunAsync, Classify
├── <X><Noun>.cs                                # capability impl — only if the plan row names one
├── ServiceCollectionExtensions.cs              # Add<Provider><Service>Demo()
├── _Imports.razor
└── Pages/
    └── <Service>Page.razor                     # and <Service>Page.razor.css — all four ship one
```

Every file below is a trimmed skeleton. Full worked examples: `samples/aws/s3/` (easy provider,
has a capability), `samples/azure/blob/` (three-plane credentials), `samples/gcp/storage/` (REST,
not gRPC), `samples/oci/objectstorage/` (the `ForFloci`-not-`SetEndpoint` trap, §14).

## `.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <RootNamespace>FlociLab.<Provider>.<Service></RootNamespace>
  </PropertyGroup>

  <!-- ONE official cloud package (constraint 1). No Version= — Directory.Packages.props pins it. -->
  <ItemGroup>
    <PackageReference Include="<Official.Cloud.Sdk.Package>" />
  </ItemGroup>

  <!-- Server-side-only RCL: the shared framework already has the Components types to reference. -->
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\FlociLab.Core\FlociLab.Core.csproj" />

    <!--
      AWS and OCI ONLY. Azure and GCP omit this line: AzureEndpoints and GcpEndpoints live in
      FlociLab.Core, and referencing the Endpoints project anyway pulls a second cloud SDK into
      the sample (constraint 1). See the divergence table above.
    -->
    <ProjectReference Include="..\..\..\..\src\FlociLab.<Provider>.Endpoints\FlociLab.<Provider>.Endpoints.csproj" />
  </ItemGroup>

</Project>
```

## `<Service>ClientFactory.cs`

```csharp
using FlociLab.Core.Endpoints;

namespace FlociLab.<Provider>.<Service>;

public sealed class <Service>ClientFactory(<Provider>Endpoints endpoints)
{
    // What the page prints under "Endpoint". The name is NOT universal — Azure matches AWS here,
    // GCP exposes BaseUri/UploadUri instead, and OCI's is `string? Endpoint`, null in real-cloud
    // mode so the page cannot print a 127.0.0.1 URL under a "REAL Oracle Cloud" banner.
    public string ServiceUrl => endpoints.ServiceUrl.TrimEnd('/');

    public bool UseEmulator => endpoints.UseEmulator;

    public <SdkClientInterface> Create()
    {
        if (!endpoints.UseEmulator)
        {
            // Real cloud: the SDK's own credential chain, no emulator-shaped config knobs.
            return new <SdkClient>(/* real-cloud config */);
        }

        // endpoints.ForFloci(...) plus endpoints.Credentials() / .Credential() — see plan §7 for
        // the per-provider knobs (ForcePathStyle, AmqpTcp port, UseEmulatorHost, ForFloci vs
        // SetEndpoint). MaxErrorRetry/equivalent off here so a dead emulator fails fast and the
        // request shown per step is the only one that went out.
        return new <SdkClient>(/* emulator config */);
    }
}
```

### The cached variant — GCP and OCI

Where constructing the client is expensive enough that one per operation costs a connection pool
per operation, the factory caches it and becomes `IDisposable` (plan §14 — this is what billed the
GCS comparison column ~2 s per call). `StorageClientFactory` and `ObjectStorageClientFactory` are
both this shape. **A sample using this variant must not `using` the client anywhere** — the
factory owns it.

```csharp
public sealed class <Service>ClientFactory(<Provider>Endpoints endpoints) : IDisposable
{
    private readonly Lock @lock = new();
    private <SdkClient>? client;

    public <SdkClient> Create()
    {
        lock (this.@lock)
        {
            return this.client ??= this.Build();
        }
    }

    public void Dispose()
    {
        this.client?.Dispose();
        this.client = null;
    }

    private <SdkClient> Build() => /* the endpoints.UseEmulator split shown above */;
}
```

## `<Service>Demo.cs`

```csharp
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FlociLab.Core;

namespace FlociLab.<Provider>.<Service>;

public sealed class <Service>Demo(<Service>ClientFactory factory) : IServiceDemo
{
    public string Provider => CloudProvider.<Provider>;

    public string Slug => "<service>";

    public string DisplayName => "<Display Name>";

    public string Category => "<Storage|Messaging|Compute|Security|...>";

    public string Route => "/<provider>/<service>";

    // Cheapest list/describe call. MUST distinguish Ok / NotImplemented / Unreachable / Error.
    public async Task<ProbeResult> ProbeAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        try
        {
            // One cheap list/describe, awaited and threading ct — a body with no await is CS1998,
            // which Directory.Build.props turns into a build error.
            <SdkResponse> response = await client.<List>Async(..., ct).ConfigureAwait(false);

            return ProbeResult.Ok(Stopwatch.GetElapsedTime(started), $"<List> returned {...}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Classify(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    public async IAsyncEnumerable<DemoStep> RunAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // AWS only: `using`, because S3ClientFactory builds a client per call. Azure, GCP and OCI
        // drop the `using` — see the divergence table; disposing a cached client breaks re-runs.
        using <SdkClient> client = factory.Create();

        // unique per-run resource name, e.g. $"flocilab-<service>-{Guid.NewGuid():N}"
        string resource = $"flocilab-<service>-{Guid.NewGuid():N}";
        bool created = false;
        DemoStep? cleanup;

        try
        {
            yield return await RunStepAsync("<Create>", "<request text>", async () =>
            {
                // Set BEFORE the call, not after: if the request lands but the response never
                // comes back, the resource exists and cleanup has to know. Cleanup treats an
                // absent resource as a no-op, so claiming it early is free.
                created = true;
                <SdkResponse> response = await client.<Create>Async(resource, ct).ConfigureAwait(false);

                return $"<response text>";
            }).ConfigureAwait(false);

            // ...one yield per operation...
        }
        finally
        {
            // runs on success, failure or cancellation — an iterator can't yield from inside finally
            cleanup = created ? await this.CleanupAsync(ct).ConfigureAwait(false) : null;
        }

        if (cleanup is not null)
        {
            yield return cleanup;
        }
    }

    // Walks the SDK's own exception chain: that SDK's 501 shape first, then transport-level
    // (refused/timeout) -> Unreachable, then any other status code the emulator returned -> Error.
    // ProbeResult.FromException only covers the transport-level cases every provider shares.
    internal static ProbeResult Classify(Exception ex, TimeSpan elapsed)
        => ProbeResult.FromException(ex, elapsed); // replace with SDK-specific unwrapping if its 501 needs it

    private static async Task<DemoStep> RunStepAsync(string title, string request, Func<Task<string>> operation)
    {
        try
        {
            return new DemoStep(title, request, await operation().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DemoStep.Failed(title, ex, request);
        }
    }

    // ct is for reporting, not for threading: the SDK calls below pass CancellationToken.None,
    // because a cancelled run still has state to remove. ct only tells the reader that happened.
    private async Task<DemoStep> CleanupAsync(CancellationToken ct)
        => await RunStepAsync("<Delete> — cleanup", "<request text>", async () =>
        {
            await client.<Delete>Async(resource, CancellationToken.None).ConfigureAwait(false);

            return "<result text>"
                + (ct.IsCancellationRequested ? "
(the run was cancelled; cleanup ran anyway)" : string.Empty);
        }).ConfigureAwait(false);
}
```

## Capability implementation — only if the plan row names one

Named for **what it implements**, not generically: `S3ObjectStore`, `BlobObjectStore`,
`GcsObjectStore` and `OciObjectStore` all implement `IObjectStoreCapability` — none of them is a
`<Service>Capability.cs`. `FlociLab.Core.Capabilities` has the five interfaces; a service with no
genuine cross-cloud analog correctly implements none of them.

```csharp
using FlociLab.Core;
using FlociLab.Core.Capabilities;

namespace FlociLab.<Provider>.<Service>;

public sealed class <ProviderNoun><Interface-sans-I>(<Service>ClientFactory factory) : I<Interface>Capability
{
    public string Provider => CloudProvider.<Provider>;

    public string ServiceName => "<Real service name>";

    // Same classifier <Service>Demo uses, so /coverage and the comparison page never disagree.
    public ProbeStatus Classify(Exception ex) => <Service>Demo.Classify(ex, TimeSpan.Zero).Status;

    // ...interface methods, thinnest possible mapping onto the SDK...
}
```

## `ServiceCollectionExtensions.cs`

```csharp
using FlociLab.Core;
using FlociLab.Core.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FlociLab.<Provider>.<Service>;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection Add<Provider><Service>Demo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<<Service>ClientFactory>();

        // Registered by concrete type too — the page injects the concrete type directly.
        services.TryAddSingleton<<Service>Demo>();
        services.TryAddSingleton<<CapabilityImpl>>(); // only if a capability exists

        // TryAddEnumerable, never TryAddSingleton/AddSingleton: the catalog and comparison pages
        // resolve IEnumerable<T>, so every sample must be additive and de-duplicate on impl type.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IServiceDemo, <Service>Demo>(sp => sp.GetRequiredService<<Service>Demo>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<I<Interface>Capability, <CapabilityImpl>>(
                sp => sp.GetRequiredService<<CapabilityImpl>>()));

        return services;
    }
}
```

## `Pages/<Service>Page.razor`

```razor
@page "/<provider>/<service>"
@inject <Service>Demo Demo
@inject <Service>ClientFactory Factory
@implements IDisposable

<PageTitle><Display Name> — FlociLab</PageTitle>

<h1><Display Name></h1>

<p class="lede"><!-- what this page does, and what in <Service>ClientFactory is emulator-specific --></p>

<dl class="facts">
    <dt>Target</dt>
    <dd class="target @(this.Factory.UseEmulator ? "target-emulator" : "target-real")">
        @(this.Factory.UseEmulator ? "floci emulator" : "REAL <PROVIDER> — this costs money")
    </dd>
    @* OCI's Endpoint is nullable and null against real cloud; that sample guards this row with
       @if (this.Factory.Endpoint is not null). GCP prints BaseUri. Use the factory's real name. *@
    <dt>Endpoint</dt>
    <dd><code>@this.Factory.ServiceUrl</code></dd>
    <dt>Package</dt>
    <dd><code><Official.Cloud.Sdk.Package></code></dd>
</dl>

<div class="toolbar">
    <button class="button" @onclick="this.RunAsync" disabled="@(this.running || !this.RendererInfo.IsInteractive)">
        @(this.running ? "Running…" : "Run the round-trip")
    </button>
</div>

@foreach (DemoStep step in this.steps)
{
    <section class="step @(step.Succeeded ? "step-ok" : "step-failed")">
        <h2>@step.Title</h2>
        @if (step.Request is not null) { <pre>@step.Request</pre> }
        @if (step.Response is not null) { <pre>@step.Response</pre> }
        @if (step.Error is not null) { <pre class="error">@step.Error</pre> }
    </section>
}

@code {
    private readonly List<DemoStep> steps = [];
    private readonly CancellationTokenSource cts = new();
    private bool running;
    private bool disposed;

    private async Task RunAsync()
    {
        if (this.running)
        {
            return;
        }

        this.running = true;
        this.steps.Clear();
        this.StateHasChanged();

        try
        {
            // Rendered as they arrive, not collected first — watching each step land is the point.
            await foreach (DemoStep step in this.Demo.RunAsync(this.cts.Token))
            {
                this.steps.Add(step);
                this.StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // navigated away mid-run; the demo's own finally already cleaned up
        }
        finally
        {
            this.running = false;

            if (!this.disposed)
            {
                this.StateHasChanged();
            }
        }
    }

    public void Dispose()
    {
        this.disposed = true;

        // Cancel without disposing: RunAsync may still be inside an SDK call registered on this
        // token, and disposing underneath it throws ObjectDisposedException instead of cancelling.
        this.cts.Cancel();
    }
}
```

Every sample also ships `Pages/<Service>Page.razor.css` — scoped styles are part of what an RCL
owns (`CLAUDE.md` constraint 4), and the page skeleton above depends on `lede`, `facts`, `target`,
`target-emulator`, `target-real`, `toolbar`, `button`, `step`, `step-ok`, `step-failed` and
`error`. Copy the nearest sample's stylesheet; without it the ok/failed colour distinction that
makes the page readable on video is gone.

## `_Imports.razor`

```razor
@using System.Threading
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using FlociLab.Core
@using FlociLab.<Provider>.<Service>
```

## Registration checklist

Both hosts every time — missing one is a compile-clean, 404-at-runtime mistake — plus the
solution and the test project:

1. `hosts/FlociLab.All.Web/FlociLab.All.Web.csproj` and `hosts/FlociLab.<Provider>.Web/FlociLab.<Provider>.Web.csproj`
   each get a `<ProjectReference>` to the new sample.
2. Both `Program.cs` files add `.Add<Provider><Service>Demo()` to the `AddFlociCore(...)` chain.
3. `FlociLab.slnx` gets the project, and `tests/FlociLab.IntegrationTests.csproj` gets a
   `<ProjectReference>` to it — without the latter the test file below cannot compile.
4. Nothing else in the hosts — `IDemoCatalog.PageAssemblies` derives the nav, the coverage row,
   the endpoint route table (`MapRazorComponents<App>().AddAdditionalAssemblies(...)`) and the in-circuit router
   (`Routes.razor`'s `AdditionalAssemblies`) from the registrations above. Both read the same
   property, so there is nothing to keep in sync by hand — only to remember to add in the first
   place.

## Test skeleton — `tests/FlociLab.IntegrationTests/<Provider><Service>Tests.cs`

**AWS only** gets `FlociBuilder`. `Testcontainers.Floci` 4.15.0 hardcodes port 4566 and the
`/_floci/health` path, so the other three need a plain `ContainerBuilder` with their own port and
namespaced health path — and `IContainer` has no `GetConnectionString()`, so the endpoint is built
from `GetMappedPublicPort`:

```csharp
// AWS
private readonly FlociContainer floci = new FlociBuilder("floci/floci:latest").Build();
// this.floci.GetConnectionString()

// Azure (4577, /_floci/health) · GCP (4588, /_floci-gcp/health) · OCI (4599, /_floci-oci/health)
private const int Port = <4577|4588|4599>;
private readonly IContainer container = new ContainerBuilder("floci/floci-<az|gcp|oci>:latest")
    .WithPortBinding(Port, assignRandomHostPort: true)
    .WithWaitStrategy(Wait.ForUnixContainer()
        .UntilHttpRequestIsSucceeded(request => request.ForPath("<health path>").ForPort(Port)))
    .Build();
// $"http://127.0.0.1:{this.container.GetMappedPublicPort(Port)}"
```

```csharp
public sealed class <Provider><Service>Tests : IAsyncLifetime
{
    // One of the two container shapes above.
    private <Service>ClientFactory factory = null!;

    public async ValueTask InitializeAsync()
    {
        await this.container.StartAsync(TestContext.Current.CancellationToken);
        this.factory = new <Service>ClientFactory(EndpointsFor(this.Endpoint));
    }

    public async ValueTask DisposeAsync() => await this.container.DisposeAsync();

    [Fact]
    public async Task Probe_Reports_Ok() { /* ProbeStatus.Ok, or NotImplemented if upstream 501s — assert it, don't skip */ }

    [Fact]
    public async Task RoundTrip_Every_Step_Succeeds() { /* Assert.Collection of step titles in order, all Succeeded */ }

    [Fact]
    public async Task RoundTrip_Leaves_No_<Resource>_Behind() { /* run RunAsync twice; list before/after must match */ }

    [Fact]
    public async Task Cancelled_Run_Throws_Rather_Than_Reporting_Failed_Steps() { /* pre-cancelled token -> OperationCanceledException, zero failed steps */ }

    [Fact]
    public async Task Probe_Reports_Unreachable_When_Nothing_Is_Listening()
    {
        // port 1 is reserved and never bound — no container needed to prove Unreachable
        <Service>Demo demo = new(new <Service>ClientFactory(EndpointsFor("http://127.0.0.1:1")));
        ProbeResult result = await demo.ProbeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProbeStatus.Unreachable, result.Status);
    }

    private static <Provider>Endpoints EndpointsFor(string endpoint) => new(/* FlociOptions bound to endpoint */);
}
```

Add a capability round-trip test too where a capability exists — see
`AwsS3Tests.ObjectStore_Capability_RoundTrips` for the shape.
