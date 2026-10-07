# Integration tests

- One test class per sample, using `Testcontainers.Floci` with the image pinned explicitly
  (`new FlociBuilder("floci/floci:latest")` — the module still defaults to an older tag, and its
  parameterless constructor is obsolete).
- If the emulator returns `501`, **assert `ProbeStatus.NotImplemented` explicitly** rather than
  skipping. The test then becomes the tripwire that tells you when upstream ships it.
- Also assert the `Unreachable` classification — a stopped emulator must not read as a broken sample.
- Prove re-runs are idempotent by running the round-trip twice.
