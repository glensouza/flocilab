namespace FlociLab.Core.Coverage;

internal sealed class CoverageSnapshotStore : ICoverageSnapshotStore
{
    private readonly Lock gate = new();

    private CoverageSnapshot? latest;

    public CoverageSnapshot? Latest
    {
        get
        {
            lock (this.gate)
            {
                return this.latest;
            }
        }
    }

    public CoverageSnapshot Save(IReadOnlyList<EmulatorHealth> health, IReadOnlyList<DemoCoverage> coverage)
    {
        CoverageSnapshot snapshot = CoverageSnapshot.From(health, coverage);

        lock (this.gate)
        {
            this.latest = snapshot;
        }

        return snapshot;
    }
}
