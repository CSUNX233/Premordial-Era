using System.Numerics;

namespace NativeEpoch.Simulation;

public struct Organism
{
    public ulong Id;
    public ulong ParentId;
    public int Generation;
    public int GenomeId;
    public Vector2 Position;
    public Vector2 Velocity;
    public float Depth;
    public float VerticalVelocity;
    public double HeadingRadians;
    public double Hydration;
    public double Immersion;
    public double WaterExposedArea;
    public double AirExposedArea;
    public int ExposedSurfaceSamples;
    public int OccludedSurfaceSamples;
    public double OxygenUptakeLastStep;
    public double OxygenConsumedLastStep;
    public double MetabolicEnergyLastStep;
    public double DehydrationCostLastStep;
    public double ContactPressure;
    public int ContactNeighborCount;
    public ulong InteractionOpponentId;
    public InteractionState InteractionState;
    public double InteractionIntensity;
    public double InteractionEnergyLastStep;
    public Vector2 InteractionDirection;
    public double ResourceDemandLastStep;
    public double ResourceSatisfaction;
    public double[] ControllerState;
    public ForagingMemory ForagingMemory;
    public ControllerInputs ControllerInputs;
    public ControllerOutputs ControllerOutputs;
    public double ForagingCue;
    public double ForagingTrend;
    public double ExplorationDrive;
    public double SteeringDrive;
    public Vector2 LocalActuationForce;
    public double ActuationTorque;
    public double AgeSeconds;
    public double Maturity;
    public double ReproductionCooldownSeconds;
    public DevelopingBody Body;
    public BodyPose Pose;

    public readonly bool AllFinite =>
        float.IsFinite(Position.X) &&
        float.IsFinite(Position.Y) &&
        float.IsFinite(Velocity.X) &&
        float.IsFinite(Velocity.Y) &&
        float.IsFinite(Depth) && Depth >= 0f &&
        float.IsFinite(VerticalVelocity) &&
        double.IsFinite(HeadingRadians) &&
        double.IsFinite(Hydration) && Hydration is >= 0.0 and <= 1.0 &&
        double.IsFinite(Immersion) && Immersion is >= 0.0 and <= 1.0 &&
        double.IsFinite(WaterExposedArea) && WaterExposedArea >= 0.0 &&
        double.IsFinite(AirExposedArea) && AirExposedArea >= 0.0 &&
        ExposedSurfaceSamples >= 0 && OccludedSurfaceSamples >= 0 &&
        double.IsFinite(OxygenUptakeLastStep) && OxygenUptakeLastStep >= 0.0 &&
        double.IsFinite(OxygenConsumedLastStep) && OxygenConsumedLastStep >= 0.0 &&
        double.IsFinite(MetabolicEnergyLastStep) && MetabolicEnergyLastStep >= 0.0 &&
        double.IsFinite(DehydrationCostLastStep) && DehydrationCostLastStep >= 0.0 &&
        double.IsFinite(ContactPressure) && ContactPressure >= 0.0 &&
        ContactNeighborCount>=0&&
        double.IsFinite(InteractionIntensity)&&InteractionIntensity is >=0.0 and <=1.0&&
        double.IsFinite(InteractionEnergyLastStep)&&InteractionEnergyLastStep>=0.0&&
        float.IsFinite(InteractionDirection.X)&&float.IsFinite(InteractionDirection.Y)&&
        double.IsFinite(ResourceDemandLastStep)&&ResourceDemandLastStep>=0.0&&
        double.IsFinite(ResourceSatisfaction)&&ResourceSatisfaction is >=0.0 and <=1.0&&
        ControllerState is not null && ControllerState.All(double.IsFinite) &&
        ForagingMemory is not null && ForagingMemory.AllFinite &&
        ControllerInputs.AllFinite &&
        ControllerOutputs.AllFinite &&
        double.IsFinite(ForagingCue) && double.IsFinite(ForagingTrend) &&
        double.IsFinite(ExplorationDrive) && double.IsFinite(SteeringDrive) &&
        float.IsFinite(LocalActuationForce.X) && float.IsFinite(LocalActuationForce.Y) &&
        double.IsFinite(ActuationTorque) &&
        double.IsFinite(AgeSeconds) &&
        double.IsFinite(Maturity) &&
        double.IsFinite(ReproductionCooldownSeconds) &&
        Maturity is >= 0.0 and <= 1.0 &&
        Body is not null && Pose is not null;
}
