using FlociLab.Core;

namespace FlociLab.Core.Coverage;

/// <summary>
/// The last <c>/coverage</c> probe, in a shape that serialises. The live grid forgets its result
/// when the circuit goes away; this is what <c>/coverage.json</c> returns, so a Floci release is
/// a diff of two files.
/// </summary>
public sealed record CoverageSnapshot(
    DateTimeOffset TakenAt,
    IReadOnlyList<EmulatorSnapshot> Emulators,
    IReadOnlyList<DemoSnapshot> Demos)
{
    public static CoverageSnapshot From(IReadOnlyList<EmulatorHealth> health, IReadOnlyList<DemoCoverage> coverage)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(coverage);

        return new CoverageSnapshot(
            DateTimeOffset.UtcNow,
            [.. health.Select(emulator => new EmulatorSnapshot(
                emulator.Provider,
                emulator.Endpoint,
                emulator.Result.Status.ToString(),
                emulator.Result.Detail,
                Milliseconds(emulator.Result)))],
            [.. coverage.Select(cell => new DemoSnapshot(
                cell.Demo.Provider,
                cell.Demo.Slug,
                cell.Demo.DisplayName,
                cell.Demo.Category,
                cell.Demo.Route,
                cell.Result.Status.ToString(),
                cell.Result.Detail,
                Milliseconds(cell.Result)))]);
    }

    private static long? Milliseconds(ProbeResult result)
        => result.Duration is { } duration ? (long)duration.TotalMilliseconds : null;
}

/// <summary>One emulator row of a <see cref="CoverageSnapshot"/>.</summary>
public sealed record EmulatorSnapshot(string Provider, string Endpoint, string Status, string? Detail, long? DurationMs);

/// <summary>One demo row of a <see cref="CoverageSnapshot"/>.</summary>
public sealed record DemoSnapshot(
    string Provider,
    string Slug,
    string DisplayName,
    string Category,
    string Route,
    string Status,
    string? Detail,
    long? DurationMs);
