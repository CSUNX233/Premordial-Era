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
    float Depth,
    float VerticalVelocity,
    double Immersion,
    double HeadingRadians,
    double Hydration,
    double InternalOxygen,
    double OxygenCapacity,
    double OxygenUptakeLastStep,
    double OxygenConsumedLastStep,
    double MetabolicEnergyLastStep,
    double DehydrationCostLastStep,
    double ContactPressure,
    double WaterExposedArea,
    double AirExposedArea,
    int ExposedSurfaceSamples,
    int OccludedSurfaceSamples,
    ControllerInputs ControllerInputs,
    ControllerOutputs ControllerOutputs,
    Vector2 LocalActuationForce,
    double ActuationTorque,
    double AgeSeconds,
    double Maturity,
    double Energy,
    double StoredMatter,
    double ReproductionCooldownSeconds,
    double DevelopmentCompletion,
    EnvironmentSample Environment,
    BodyCache Body,
    BodyGeometry Geometry,
    BodyGeometry VisualTemplateGeometry,
    IReadOnlyList<BodyVisualRegion> Regions,
    IReadOnlyList<BodyRegion> RegionInventories)
{
    public int Generation { get; init; }
    public double ExplorationDrive { get; init; }
    public double ForagingTrend { get; init; }
    public int ContactNeighborCount { get; init; }
    public ulong InteractionOpponentId { get; init; }
    public InteractionState InteractionState { get; init; }
    public double InteractionIntensity { get; init; }
    public Vector2 InteractionDirection { get; init; }
    public double ResourceSatisfaction { get; init; }
    public double ResourceDemandLastStep { get; init; }
}

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
