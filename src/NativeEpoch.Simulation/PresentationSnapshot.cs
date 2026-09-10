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
    public int BirthMutationCount { get; init; }
    public double OffspringMutationProbability { get; init; }
    public double LightEnergyLastStep { get; init; }
    public double OrganicFeedingLastStep { get; init; }
    public double DecompositionLastStep { get; init; }
    public double PrimaryProductionLastStep { get; init; }
    public double PredationLastStep { get; init; }
    public double ExplorationDrive { get; init; }
    public double ForagingTrend { get; init; }
    public int ContactNeighborCount { get; init; }
    public ulong InteractionOpponentId { get; init; }
    public InteractionState InteractionState { get; init; }
    public double InteractionIntensity { get; init; }
    public Vector2 InteractionDirection { get; init; }
    public double ResourceSatisfaction { get; init; }
    public double ResourceDemandLastStep { get; init; }
    public double MeanTissueExpression { get; init; }
    public int ActiveSensorCount { get; init; }
    public double ChemicalSensorSignal { get; init; }
    public double ChemicalSenseAccess { get; init; }
    public double ContactSensorSignal { get; init; }
    public double SensingEnergyLastStep { get; init; }
    public int ActiveVisualSensorCount { get; init; }
    public double VisionSignal { get; init; }
    public SocialResponse SocialResponse { get; init; }
    public ulong SocialTargetId { get; init; }
    public int ActiveIndividualSensors { get; init; }
    public double AnimalFoodAffinity { get; init; }
    public double AttackAffinity { get; init; }
    public double RetaliationAffinity { get; init; }
    public double AttackDamageLastStep { get; init; }
    public double RetaliationDamageLastStep { get; init; }
    public double BodyCenterElevation { get; init; } = double.NaN;
    public IReadOnlyList<AppendageRegionPose> AppendageRegions { get; init; } = Array.Empty<AppendageRegionPose>();
    public int AppendageContactCount { get; init; }
    public double AppendageSupport { get; init; }
    public Vector2 AppendageGroundVelocity { get; init; }
    public double AppendageEnergyLastStep { get; init; }
    public double CavityOxygen { get; init; }
    public double CavityOxygenCapacity { get; init; }
    public double CavityVentilationLastStep { get; init; }
    public double CavityTissueOxygenLastStep { get; init; }
    public double CavityEnergyLastStep { get; init; }
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
    public EnvironmentResourceSnapshot? Resources { get; init; }
}
