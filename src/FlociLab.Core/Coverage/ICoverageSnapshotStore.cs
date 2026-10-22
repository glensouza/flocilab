namespace FlociLab.Core.Coverage;

/// <summary>
/// Holds the latest <see cref="CoverageSnapshot"/> for this process. Singleton, because the
/// circuit that probed and the <c>GET /coverage.json</c> that reads it are different scopes.
/// </summary>
public interface ICoverageSnapshotStore
{
    CoverageSnapshot? Latest { get; }

    CoverageSnapshot Save(IReadOnlyList<EmulatorHealth> health, IReadOnlyList<DemoCoverage> coverage);
}
