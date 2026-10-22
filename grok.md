# FlociLab gallery surface

Written 2026-10-22. This branch holds the changes below. They are not merged to `main`,
and no checkbox in `../floci-content/docs/BACKLOG.md` was ticked.

`dotnet build FlociLab.slnx -warnaserror` succeeds. `ServiceFindingTests` passes.
`dotnet run lab.cs` from `samples/aws/s3` compiles, runs the S3 demo, and exits non-zero
when nothing is listening on `127.0.0.1:4566`.

## 1. Home pages and sidebar

The All.Web overview used to describe a console with no demos and no OCI. It now reads
`IDemoCatalog` and shows the live counts (checked with the emulators down: AWS 47, Azure 6,
GCP 5, OCI 4, 62 demos). The extra demo past the 61 `Add*Demo()` calls is API Gateway v2's
second registration, the WebSocket demo. The page links the five comparison routes, the
coverage matrix, and `/differences`, and it says floci-ui 0.6.0 covers AWS, Azure, GCP, and
OCI. Emulator endpoints on the page are `127.0.0.1`.

Each provider home (`Aws`, `Azure`, `Gcp`, `Oci`) counts that host's demos the same way.
The OCI home no longer says the console has no OCI. It says the console covers OCI from
floci-ui 0.6.0, and that the AppHost runs `FlociLab.All.Web` rather than this host.

The sidebar in `src/FlociLab.Shell` has a search box when the catalog is non-empty. It
filters on display name, slug, and category, and groups the matches by category. Compare
links stay. An empty match says `No services match "…"`. The sidebar is sticky, `100vh`,
and scrolls.

Files: `hosts/FlociLab.*.Web/Components/Pages/Home.razor`,
`src/FlociLab.Shell/Layout/NavMenu.razor`, `src/FlociLab.Shell/wwwroot/app.css`.

## 2. Comparison cells

Capability method signatures are unchanged. Tests still call `CreateContainerAsync`,
`PutObjectAsync`, and the rest.

`CallTrace` (`src/FlociLab.Core/CallTrace.cs`) is an `AsyncLocal` buffer. `Request` and
`Response` do nothing unless a comparison run opened a buffer with `Begin`, so the
integration tests are unaffected. The last request in a method wins. `OperationCanceledException`
still escapes.

Each of the 20 capability classes records a request at the start of every public async
method: the factory URL (`ServiceUrl`, `BaseUri`, or `Endpoint`) and `Type.Method`. GCP
Pub/Sub and the other factories without one of those properties record the method name
only. No capability sets `Response` yet, so that row of the cell stays empty. Each
capability also sets `SourcePath` on `ICloudCapability` (a default of null, overridden
with the repo-relative path). The GitHub link is
`https://github.com/glensouza/flocilab/blob/main/` plus that path.

`ComparisonTrace` runs the operation inside a buffer. The five comparison pages pass the
recorded request and response into `ComparisonCellView`, which shows status, elapsed time,
detail, an optional request/response block, "Open the demo", and "Source". The demo link
is the first `IServiceDemo` in the capability's assembly. `FlociLab.Comparison` still
references only Core and Shell.

`docs/BLAZOR-PLAN.md` §8 now describes that cell.

## 3. Known differences

`/differences` (`src/FlociLab.Shell/Pages/Differences.razor`) groups
`[ServiceFinding(title, detail)]` attributes by provider, in catalog order, and links each
demo. The lede says the sentence is the one already in that sample's source, and that a
missing row means nothing was recorded.

29 findings were copied onto 12 AWS demos, only from non-comment lines that already pair
"floci" with "real". A skip message about pointing a real account at floci was left out.
Most demos have no such sentence, so they have no row. That is intentional.

`tests/FlociLab.IntegrationTests/ServiceFindingTests.cs` loads the referenced sample
assemblies, finds each demo's `.cs` file, and asserts the detail is a verbatim substring.
It does not start a container.

Demos with findings: ACM, AppConfig, AppConfigData, Application Auto Scaling, Auto Scaling,
Cloud Control API, CloudFormation, CodeGuru Reviewer, Elastic Beanstalk, Lightsail, S3
Tables, S3 Vectors.

## 4. Coverage snapshot

`/coverage` used to forget its result when the circuit ended. `ICoverageSnapshotStore` is
a singleton registered from `AddFlociCore`. The page saves after a probe. `GET
/coverage.json` returns that snapshot. With no snapshot yet, the GET probes. `?refresh=true`
probes again. `refresh` defaults to false. A non-optional `bool` made a bare GET fail
with "Required parameter was not provided".

The JSON shape is `CoverageSnapshot`: `takenAt`, emulator rows, and demo rows (provider,
slug, display name, category, route, status, detail, duration in milliseconds). The DTOs
are not live `IServiceDemo` instances.

`MapCoverageSnapshot` lives on `FlociLab.Shell` and is called from all five hosts, so
every host exposes `/coverage.json`.

Checked with the emulators down: the first GET returned 4 unreachable emulators and 62
unreachable demos. The second GET returned the same snapshot in 285 ms.

## 5. Phase 4 flag and advertised hostnames

`FlociOptions.EnableContainerBackedServices` has no initializer, so it defaults to false.
The key is in `hosts/FlociLab.All.Web/appsettings.json` and
`src/FlociLab.AppHost/appsettings.json`.

`ContainerBackedServiceAttribute` marks a future Kind B demo. `DemoCatalog` hides those
types when the flag is false, which keeps them out of the nav, the coverage grid, and
comparison columns that enumerate the catalog. The route would still exist. The attribute
says `RunAsync` must also refuse when the flag is off. No Kind B demo exists yet.

The AppHost reads `Floci:EnableContainerBackedServices`. Off sets `FLOCI_HOSTNAME`,
`FLOCI_AZ_HOSTNAME`, `FLOCI_GCP_HOSTNAME`, and `FLOCI_OCI_HOSTNAME` to `127.0.0.1`. On
sets them to `floci`, `floci-az`, `floci-gcp`, and `floci-oci`. It passes
`Floci__EnableContainerBackedServices` into All.Web. The Docker socket stays. Service Bus
port 5673 stays unpublished. `*_BASE_URL` is not set.

OCI samples still ignore the reported endpoint in emulator mode and dial `ForFloci`, so
`127.0.0.1` changes the URL the emulator displays. Throwaway test containers do not set
the variable, so they still report `localhost:4599`. The remarks on
`samples/oci/queue/.../QueueClientFactory.cs` and
`samples/oci/vault/.../VaultClientFactory.cs` say that.

`docs/BLAZOR-PLAN.md` §14's hostname row describes the pin. An older row in the same
section, "floci-oci reports a queue's `messagesEndpoint` from its own config", still says
the AppHost leaves `FLOCI_OCI_HOSTNAME` unset. That sentence is stale.

## 6. lab.cs beside each sample

61 files, one at `samples/<provider>/<service>/lab.cs`, next to the demo folder. A
file-based .NET 10 app:

```text
#:sdk Microsoft.NET.Sdk.Web
#:project <DemoFolder>/<Demo>.csproj
```

The Web SDK is there so the Razor class library's shared framework resolves. The file is
a console program, so `BuildServiceProvider` is suppressed for ASP0000 with a comment.
`Directory.Build.props` treats that analyzer warning as an error.

The lab calls `AddFlociCore(FlociEnvironment.Apply)` and the sample's `Add*Demo()`, then
runs every `IServiceDemo` in a scope. API Gateway v2's lab runs both demos. It prints each
step's request, response, and error, and sets a non-zero exit code if any step failed.
`FlociEnvironment.Apply` reads the same `Floci__*` environment variables the hosts bind
and leaves unset values at the README defaults.

`docs/RCL-TEMPLATE.md` puts `lab.cs` in the file tree and adds it as checklist item 4. A
Kind B demo also carries `[ContainerBackedService]`.

These files are outside the sample `.csproj`, so `dotnet build` on the solution does not
compile them. The S3 one was compiled and run. The file-based runner did not leave a
`bin` or `obj` beside `lab.cs`.

## 7. CI

`.github/workflows/build.yml` runs on push and pull request: `dotnet build FlociLab.slnx
-warnaserror` on `ubuntu-latest` with `actions/setup-dotnet` `10.0.x`. No emulator and no
secrets.

`.github/workflows/nightly.yml` runs at 06:00 UTC and on `workflow_dispatch`:
`dotnet test tests/FlociLab.IntegrationTests`, timeout 180 minutes. Emulator-only. No
secrets.

## 8. MessagePack pin

Not one of the eight features. Aspire still pulls MessagePack 2.5.302, and NuGet audit
turns that into a warning-as-error (GHSA-qhrr-8q5h-9q3h, nested arrays allocate before the
depth limit). `Directory.Packages.props` pins 2.5.303, the patched 2.x line, next to the
existing Newtonsoft.Json pin.

## What was not done

- No merge to `main`, and no backlog checkbox.
- No browser session. Pages were checked as prerendered HTML: `/`, `/differences`,
  `/compare/object-storage`, `/coverage`, and `/coverage.json`. The sidebar filter, an
  expanded comparison cell, and "Run on all providers" were not clicked. The button is
  disabled until the interactive circuit connects.
- The four standalone provider hosts were not started. They compiled.
- Comparison cells do not show a response body until a capability calls
  `CallTrace.Response`.
- The nightly workflow has not been run.
