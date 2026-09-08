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
    IReadOnlyList<BodyVisualRegion> Regions,
    IReadOnlyList<BodyRegion> RegionInventories);

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
