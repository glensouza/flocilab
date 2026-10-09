namespace FlociLab.Core.Coverage;

/// <summary>
/// Calls <see cref="IServiceDemo.ProbeAsync"/> on every registered demo in parallel. This is how
/// the service checklists stay honest — the app reports what the emulators
/// actually do, rather than what the plan hoped they would.
/// </summary>
public interface ICoverageMatrix
{
    Task<IReadOnlyList<DemoCoverage>> ProbeAllAsync(CancellationToken ct);
}
