# Contributing

The design is [`docs/BLAZOR-PLAN.md`](docs/BLAZOR-PLAN.md); the coding rules and the constraints
every sample keeps are in [`CLAUDE.md`](CLAUDE.md); how to build a new sample is
[`docs/RCL-TEMPLATE.md`](docs/RCL-TEMPLATE.md).

## When a sample is done

- Its Razor Class Library builds with `dotnet build -warnaserror`.
- Its integration test passes (a `501` from the emulator is a documented outcome, asserted, not
  skipped).
- It is registered in its provider host and in `FlociLab.All.Web`.
- It implements the capability interface its service calls for, and no other.
- It references exactly one official cloud SDK package.
- It has been reviewed.

One service per PR.

## When Floci releases

The emulators ship together about every two weeks, and a release can fix a gap a test pins, change
behaviour a sample relies on, or add services. Re-run the full suite on the new images, flip the
tripwire tests upstream fixed, fix the samples whose behaviour moved, and record it in
[`docs/BLAZOR-PLAN.md`](docs/BLAZOR-PLAN.md) §14. The AppHost picks up new images on its next start
(§14, "Emulator `latest` tags shift under you").

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
