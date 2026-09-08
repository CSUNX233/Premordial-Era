using System.Collections.ObjectModel;
using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct OrganismPresentationState(
    ulong Id,
    ulong ParentId,
    int GenomeId,
    ulong GenomeFingerprint,
    int GenomeRegionCount,
    Vector2 Position,
    Vector2 Velocity,
    double HeadingRadians,
    double AgeSeconds,
    double Maturity,
    double Energy,
    double StoredMatter,
    double ReproductionCooldownSeconds,
    double DevelopmentCompletion,
    EnvironmentSample Environment,
    BodyCache Body,
    IReadOnlyList<BodyVisualRegion> Regions);

public sealed class WorldPresentationSnapshot
{
    public WorldPresentationSnapshot(
        SimulationSnapshot statistics,
        IEnumerable<OrganismPresentationState> organisms)
    {
        Statistics = statistics;
        Organisms = new ReadOnlyCollection<OrganismPresentationState>(organisms.ToArray());
    }

    public SimulationSnapshot Statistics { get; }
    public IReadOnlyList<OrganismPresentationState> Organisms { get; }
}
