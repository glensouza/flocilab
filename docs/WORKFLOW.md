# Workflow

How work moves through this repo. The plan, [`BLAZOR-PLAN.md`](BLAZOR-PLAN.md), says *what* to
build; this page says how a piece of it gets built and counted as done.

## The loop

```
build the next item  →  code review  →  fix findings  →  mark ☑  →  commit
     (leaves ☐)                                          └── only after review passes
```

Work is driven by Claude Code skills (`/next` to build, `/ship` to review and record) that live in
a private companion repo alongside the video scripts. You don't need them to build or run anything
here; the rules below are what they enforce.

**The one rule:** nothing is marked ☑ in the plan until it has been reviewed. A ☑ is read
downstream as "shipped, safe to make content about", so a premature tick publishes unreviewed
work. The order is the guarantee.

## When a service is done

- Its Razor Class Library builds with `dotnet build -warnaserror`.
- Its integration test passes (a `501` from the emulator is a documented outcome, asserted, not
  skipped).
- It is registered in its provider host and in `FlociLab.All.Web`.
- It implements the capability interface its plan row names, and no other.
- It references exactly one official cloud SDK package (see [`CLAUDE.md`](../CLAUDE.md)).
- It has been reviewed, and only then ticked in §13.

One service per PR, or one category per PR in Phase 3.

## When Floci releases

The emulators ship together about every two weeks, and a release can fix a gap a test pins, change
behaviour a sample relies on, or add services. Re-run the full suite on the new images, flip the
tripwire tests upstream fixed, fix the samples whose behaviour moved, and record it in §14. The
AppHost picks up new images on its next start (§14, "Emulator `latest` tags shift under you").

## Setup on a new machine

| Need | For |
| :--- | :--- |
| .NET SDK 10.0.3xx | Everything |
| Docker Desktop (or Docker + Compose) | The emulators |
| `git` | Everything |

```bash
git clone https://github.com/glensouza/flocilab.git
cd flocilab
dotnet run --project src/FlociLab.AppHost      # four emulators + the console + the web app
dotnet test tests/FlociLab.IntegrationTests    # throwaway containers, no running stack needed
```

`.claude/settings.json` is committed: it allows the toolchain this repo needs and denies the
destructive cases (`rm -rf`, force pushes, `git reset --hard`, `docker volume rm`, secrets files).
Put machine-specific overrides in `.claude/settings.local.json`, which is gitignored.

Model choice and cost habits are in [`BLAZOR-PLAN.md` §11](BLAZOR-PLAN.md#11-model-selection-and-cost-strategy).
