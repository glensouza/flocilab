# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

FlociLab is a multi-cloud sample gallery built on the [Floci](https://floci.io) emulator suite: four
emulators (AWS, Azure, GCP, OCI) orchestrated by .NET Aspire, with one Blazor page per emulated
service showing a real round-trip against the real cloud SDK. The repo doubles as the source for a
YouTube/blog series — `../floci-content` holds the backlog (`docs/BACKLOG.md`) and reads its ☑ to
decide what is safe to publish.

`README.md` documents the Docker/Portainer lab itself. `docs/BLAZOR-PLAN.md` is the design (§1-10)
and the risk register (§14); `../floci-content/docs/BACKLOG.md` is the single source of truth for
what is done and what is next (§11-13, same numbers). `CONTRIBUTING.md` is the public summary.
Read both before starting work — do not re-derive them.

## Commands

```bash
# Build (warnings are errors — keep it clean)
dotnet build -warnaserror

# Run everything: 4 emulators + floci-ui + FlociLab.All.Web, one F5
dotnet run --project src/FlociLab.AppHost

# Run the unified web app alone against an already-running emulator stack
dotnet run --project hosts/FlociLab.All.Web --launch-profile http   # http://localhost:5115

# Run one provider's standalone host (the AppHost does not start these — it runs All.Web only)
dotnet run --project hosts/FlociLab.Aws.Web --launch-profile http   # 5120, then 5122/5124/5126

# Tests — throwaway emulator per test class, no running stack needed
dotnet test tests/FlociLab.IntegrationTests
dotnet test tests/FlociLab.IntegrationTests --filter "FullyQualifiedName~S3"

# Emulator health (paths are NOT uniform — gcp and oci namespace theirs)
curl -fsS http://127.0.0.1:4566/_floci/health        # aws
curl -fsS http://127.0.0.1:4577/_floci/health        # azure
curl -fsS http://127.0.0.1:4588/_floci-gcp/health    # gcp
curl -fsS http://127.0.0.1:4599/_floci-oci/health    # oci

# Confirm a sample did not leak a second cloud SDK
dotnet list samples/aws/s3/FlociLab.Aws.S3.Demo package --include-transitive
```

## Architecture

### Projects

- `src/FlociLab.{Provider}.Endpoints` references only that provider's `*.Core` package, which every
  sample already pulls in transitively — so it never counts as a second SDK.
- `hosts/FlociLab.{Provider}.Web` holds Core, `FlociLab.Shell` (the shared chrome; Core-only) and
  that provider's RCLs and nothing else; that is what makes a sample clonable on its own. A host's
  own files are `Home.razor`, `_Imports.razor` and `Program.cs` — fix chrome in the shell.

### Key files to read first

- `../floci-content/docs/BACKLOG.md` — §12 phases and §13 checklists say what is done and what is
  next
- `docs/BLAZOR-PLAN.md` — §7 is the per-provider endpoint story, which is where most of the real
  difficulty lives
- `src/FlociLab.Core/IServiceDemo.cs` — the contract every sample implements
- `src/FlociLab.Core/ProbeResult.cs` — the four outcomes (`Ok` / `NotImplemented` / `Unreachable` /
  `Error`) the coverage matrix depends on; `FromException` handles only transport-level cases, so a
  sample classifies its own SDK's 501 first
- `src/FlociLab.Core/Configuration/FlociOptions.cs` — endpoints, ports and credentials, with the
  defaults being the README's host-side ports so the app runs with no configuration at all
- `src/FlociLab.AppHost/AppHost.cs` — the shared `floci` network, the Docker socket as a runtime arg
  rather than a bind mount, and why `FLOCI_HOSTNAME` is deliberately unset
- `samples/aws/s3/FlociLab.Aws.S3.Demo/` — the reference Kind A sample; copy its shape
- `docs/RCL-TEMPLATE.md` — the file-by-file skeleton behind every Kind A sample, extracted from the
  four Phase 1 samples; read this before `/next` on a new service so the shape doesn't have to be
  re-derived from a sample file by file
- `hosts/FlociLab.All.Web/Program.cs` — `AddFlociCore` then one `Add<Provider><Service>Demo()` per sample

### How a sample reaches the UI

A host adds a `ProjectReference` and one `Add<Provider><Service>Demo()` line. Everything else is derived:

- `DemoCatalog` enumerates the registered `IServiceDemo`s, which drives the nav and `/coverage`
- `IDemoCatalog.PageAssemblies` yields the assemblies owning routable pages: the registered
  demos' own, plus any declared with `AddPageAssembly()`, which is how the page-only
  `FlociLab.Comparison` RCL gets routed. Both
  `MapRazorComponents<App>().AddAdditionalAssemblies(...)` (the startup endpoint route table) **and**
  the `Router` component's `AdditionalAssemblies` (routing inside the interactive circuit) need it —
  a page wired into only one of the two 404s on a fresh request or dead-ends on an in-app link.
- The comparison pages enumerate the capability interfaces

A sample registers its demo and capability by concrete type *and* forwards the interface
registrations to that instance with `TryAddEnumerable`, so the page can inject the concrete type
while the catalog still sees one shared instance.

## Non-negotiable constraints

Breaking one of these breaks the design; they are not judgement calls.

1. **One official cloud SDK package per sample `.csproj`.** This is what makes each sample
   standalone-clonable and blog-ready. A service that would need a second package is an architecture
   question — stop and ask.
2. **Use the official SDK, never a wrapper.** The only difference from production code is the
   endpoint. A sample that needed a Floci-specific library would be worthless as a teaching artifact.
3. **`FlociLab.Core` stays dependency-free.** Never add a cloud package to it.
4. **The RCL owns its page**, its styles (scoped `.razor.css`) and its DI. Hosts reference RCLs and
   call one `Add*Demo()`.
5. **Versions live in `Directory.Packages.props`.** Never a `Version=` attribute in a sample csproj.
6. **Never invent emulator behaviour.** `curl` the running emulator or read the upstream README
   before writing code. A `501` is a documented outcome — record it and assert it in a test, do not
   work around it.
7. **`/next` never ticks a checkbox in `../floci-content/docs/BACKLOG.md`.** Only `/ship` does, after review —
   `../floci-content` reads ☑ as "safe to make a video about".

## Coding Rules

### Hard requirements

- **Always use explicit types; never use `var`** — assignments, `foreach`, out-vars, everywhere.
  This is a hard requirement, checked in review.
- **Never write `localhost` for an emulator endpoint — use `127.0.0.1`.** In config, in docs, in
  `curl` examples and on the demo pages. `localhost` resolves to both `::1` and `127.0.0.1`, and a
  Docker-published port binds IPv4 only, so every new connection burns the OS connect timeout on a
  dead IPv6 attempt first — ~2 s per pool on Windows (plan §14). The one exception is a Kestrel
  `applicationUrl` in `launchSettings.json`: that is the app's own listen address, and `localhost`
  there binds *both* stacks. Pinning it to `127.0.0.1` would recreate the problem in the browser.
- Use C# 14 / .NET 10 idioms: file-scoped namespaces, `ArgumentNullException.ThrowIfNull`, `Async`
  suffix on async methods. Do not change `TargetFramework` or `LangVersion`.
- Prefer least visibility: `private`/`internal` before `public`. Do not add public interfaces unless
  DI or testing requires them.
- Async methods accept and thread a `CancellationToken` where appropriate.
- No silent catches — log and rethrow, or return the error explicitly. A deliberately swallowed
  exception uses a brace body with a comment saying what is swallowed and why
  (`catch (OperationCanceledException) { /* navigated away mid-probe */ }`); never `catch { }` on
  one line.
- The build runs with `TreatWarningsAsErrors`. Fix the warning; do not suppress it. A suppression
  needs a comment giving the reason (see `Directory.Packages.props` on the Newtonsoft.Json pin).
- Keep diffs minimal and focused; no unrelated reformatting.

### Style rules — apply on every edit

- **`this.` prefix** on all instance field and method access (`this.steps`, `this.LoadApiKey()`).
  Primary-constructor parameters are not fields and are used bare (`endpoints.ServiceUrl`).
- **No underscore prefix** on private fields (`_logger` → `logger`, `_lock` → `@lock`).
  `static readonly` and `const` use **PascalCase** (`TargetVersionCacheDuration`), not `_camelCase`
  and not `ALL_CAPS`.
- **Acronym casing**: acronyms longer than two letters are Pascal-style (`OBSService` →
  `ObsService`, `NDI` → `Ndi`). Two-letter acronyms (`Id`, `Db`, `S3`) stay as-is.
- **Primary constructors** when no validation is needed; a classical constructor when you need
  `ArgumentNullException.ThrowIfNull`. Captured parameters still get no underscore.
- **`init` accessors** for properties set only during construction.
- **Computed / getter-only properties use `PascalCase`.**
- **No redundant initializers** — drop `= false`, `= null`, `= 0`.
- **Collection expressions `[]`** instead of `new List<T>()`, `new()`, `Enumerable.Empty<T>()` or
  `new[] { ... }`.
- **`coll.Any()` → `coll.Count != 0`** when `Count` is available.
- **Always brace** `if`/`else`/`foreach`/`using` — no single-line bodies, no single-line early
  returns.
- **Switch expressions** over if-else chains that return a value; use property patterns
  (`result is { Status: ProbeStatus.Ok, Duration: not null }`).
- **Invert conditions to reduce nesting** — early return / `continue`.
- **`await using`** for anything `IAsyncDisposable`.
- **`System.Threading.Lock`** for lock targets, not `object`. Name it `@lock` or `<thing>Lock`.
- **One top-level type per file.**
- **Collapse multi-line method signatures** onto one line, even if long. (A primary constructor with
  many parameters may wrap.)
- **Unused lambda parameters use `_`** (`(s, e) =>` → `(_, e) =>`).
- **No fully-qualified type names** where a `using` directive would do.
- **Razor `@using` directives** belong in `_Imports.razor`, not per page.

### Comments

Comments explain **why**, not what — the non-obvious constraint, the emulator quirk, the reason a
line that looks wrong is right. `//` prose over `///` XML docs for internal reasoning; XML docs on
the contracts in `FlociLab.Core` and on a sample's public surface. Never narrate the code.

Where an emulator behaves unlike the real cloud, say so at the point of the workaround and, if it
changes the plan, add it to `docs/BLAZOR-PLAN.md` §14.

## Testing

- A demo is not ticked in the backlog's §13 until its integration test passes. How to write that test lives in
  `tests/FlociLab.IntegrationTests/CLAUDE.md`.
- Demo runs clean up in a `finally` and use a unique per-run resource name, so re-runs are
  idempotent. Test that by running the round-trip twice.

## Commits and PRs

- **Conventional commits**, lowercase subject, imperative: `feat:`, `fix:`, `docs:`, `chore:`.
  Scope where it helps (`fix(hooks):`).
- Subject says the outcome, not the mechanism: `fix: ticker times use configured SystemTimeZone, not
  the container's tz`.
- Body is hard-wrapped prose explaining **why** — what was wrong, what changed, and anything the
  reader has to do about it (e.g. "rows written before this fix have wrong stored instants"). Skip
  the body only for genuinely trivial changes.
- One logical change per commit. Commit or push only when asked.
- PR checklist: `dotnet build -warnaserror` passes · `dotnet test` passes · no `var` · sample
  references exactly one cloud SDK · registered in **both** `All.Web` and its per-provider host.

## The working loop

```
/next   →  picks the next unchecked item in ../floci-content/docs/BACKLOG.md and builds it (leaves ☐)
/ship   →  code review → apply findings → tick ☑ → commit → sync to ../floci-content → episode
/floci-release → a new Floci image: full suite, flip fixed tripwires, fix moved samples, new rows,
                 then re-verify affected episodes and labs (recorded/published ones: stop and ask)
```

The skills live in the private `../floci-content` repo, and sessions normally start there with this
repo as an additional directory, so this file still loads. A session started here has the rules
but not the skills.

## Working efficiently here

- **Probe before you code.** `curl` the running emulator for the operation you are about to wrap.
  It is faster than guessing and it catches an unimplemented operation immediately.
- **Copy the nearest sample rather than re-deriving.** Provider consistency beats cross-provider
  consistency — the Azure endpoint story is nothing like AWS's. `samples/aws/s3/` is the reference.
- **A green build is not a working page.** Razor route and DI mistakes compile fine and fail at
  runtime (a missing `AddAdditionalAssemblies` 404s; injecting a type registered only by interface
  500s). Run the host and hit the route before calling a sample done.
- **Let the compiler and the integration test verify** — not another model pass, and not a re-read
  of a file you just wrote.
- **Batch independent tool calls** into one message; run the build and the emulator probe together.
- **Do not spawn subagents.** Each one starts cold and re-derives context the session already has.
  `/next` and `/ship` are designed for inline execution.
- **Model choice** (backlog §11): scaffolding on Haiku 4.5, a service sample on Sonnet 5, escalate to
  Opus 5 only after two genuine failed attempts — typically a GCP transport or Azure ARM shape
  problem. Never start a service on Opus. Batch 3–5 services per session, then start fresh.
- **Keep this file lean.** It loads into every session; detail belongs in `docs/BLAZOR-PLAN.md`, the
  backlog or a skill. Past ~300 lines it is costing more than it earns.
