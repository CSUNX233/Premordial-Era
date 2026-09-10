using System.Numerics;

namespace NativeEpoch.Simulation;

public struct Organism
{
    public ulong Id;
    public ulong ParentId;
    public int Generation;
    public int GenomeId;
    public int BirthMutationCount;
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
    public double HypoxiaShortfallLastStep;
    public double MetabolicEnergyLastStep;
    public double LightEnergyLastStep;
    public double OrganicFeedingLastStep;
    public double DecompositionLastStep;
    public double PrimaryProductionLastStep;
    public double PredationLastStep;
    public double AttackDamageLastStep;
    public double RetaliationDamageLastStep;
    public double PredationEnergyLastStep;
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
    public double[] SensorState;
    public double[] TissueControllerInputs;
    public int ActiveSensorCount;
    public double ChemicalSensorSignal;
    public double ContactSensorSignal;
    public int ActiveVisualSensorCount;
    public double VisionSignal;
    public double ChemicalSenseAccess;
    public double SensingEnergyLastStep;
    public ForagingMemory ForagingMemory;
    public SocialMemory SocialMemory;
    public SocialResponse SocialResponse;
    public ulong SocialTargetId;
    public int ActiveIndividualSensors;
    public ControllerInputs ControllerInputs;
    public ControllerOutputs ControllerOutputs;
    public double ForagingCue;
    public double ForagingTrend;
    public double ExplorationDrive;
    public double SteeringDrive;
    public SurvivalReflexMemory SurvivalMemory;
    public double SurvivalStress;
    public bool SurvivalReflexActive;
    public SurvivalReflexMode SurvivalReflexMode;
    public Vector2 LocalActuationForce;
    public double ActuationTorque;
    public double AgeSeconds;
    public double Maturity;
    public double ReproductionCooldownSeconds;
    public DevelopingBody Body;
    public BodyPose Pose;
    public AppendageMechanics AppendageMechanics;
    public IReadOnlyList<AppendageRegionPose> AppendageRegions;
    public int AppendageContactCount;
    public double AppendageSupport;
    public Vector2 AppendageGroundVelocity;
    public double AppendageEnergyLastStep;
    public CavitySystemState CavityState;
    public double CavityVentilationLastStep;
    public double CavityTissueOxygenLastStep;
    public double CavityEnergyLastStep;

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
        double.IsFinite(HypoxiaShortfallLastStep) && HypoxiaShortfallLastStep >= 0.0 &&
        double.IsFinite(OxygenConsumedLastStep) && OxygenConsumedLastStep >= 0.0 &&
        double.IsFinite(MetabolicEnergyLastStep) && MetabolicEnergyLastStep >= 0.0 &&
        double.IsFinite(LightEnergyLastStep) && LightEnergyLastStep >= 0.0 &&
        double.IsFinite(OrganicFeedingLastStep) && OrganicFeedingLastStep >= 0 &&
        double.IsFinite(DecompositionLastStep) && DecompositionLastStep >= 0 &&
        double.IsFinite(PrimaryProductionLastStep) && PrimaryProductionLastStep >= 0 &&
        double.IsFinite(AttackDamageLastStep) && AttackDamageLastStep>=0 &&
        double.IsFinite(RetaliationDamageLastStep) && RetaliationDamageLastStep>=0 &&
        double.IsFinite(PredationLastStep) && PredationLastStep >= 0 &&
        double.IsFinite(PredationEnergyLastStep) && PredationEnergyLastStep >= 0 &&
        double.IsFinite(DehydrationCostLastStep) && DehydrationCostLastStep >= 0.0 &&
        double.IsFinite(ContactPressure) && ContactPressure >= 0.0 &&
        ContactNeighborCount>=0&&
        double.IsFinite(InteractionIntensity)&&InteractionIntensity is >=0.0 and <=1.0&&
        double.IsFinite(InteractionEnergyLastStep)&&InteractionEnergyLastStep>=0.0&&
        float.IsFinite(InteractionDirection.X)&&float.IsFinite(InteractionDirection.Y)&&
        double.IsFinite(ResourceDemandLastStep)&&ResourceDemandLastStep>=0.0&&
        double.IsFinite(ResourceSatisfaction)&&ResourceSatisfaction is >=0.0 and <=1.0&&
        ControllerState is not null && ControllerState.All(double.IsFinite) &&
        SensorState is not null && SensorState.All(double.IsFinite) &&
        TissueControllerInputs is not null && TissueControllerInputs.All(double.IsFinite) &&
        ActiveSensorCount>=0 && double.IsFinite(ChemicalSensorSignal) &&
        double.IsFinite(ContactSensorSignal) && ActiveVisualSensorCount>=0 &&
        double.IsFinite(VisionSignal) && double.IsFinite(ChemicalSenseAccess) &&
        ChemicalSenseAccess is >=0.0 and <=1.0 &&
        double.IsFinite(SensingEnergyLastStep) && SensingEnergyLastStep>=0.0 &&
        ForagingMemory is not null && ForagingMemory.AllFinite &&
        SocialMemory is not null && SocialMemory.AllFinite &&
        ControllerInputs.AllFinite &&
        ControllerOutputs.AllFinite &&
        double.IsFinite(ForagingCue) && double.IsFinite(ForagingTrend) &&
        double.IsFinite(ExplorationDrive) && double.IsFinite(SteeringDrive) &&
        SurvivalMemory.AllFinite && double.IsFinite(SurvivalStress) &&
        SurvivalStress is >= 0.0 and <= 1.0 && Enum.IsDefined(SurvivalReflexMode) &&
        float.IsFinite(LocalActuationForce.X) && float.IsFinite(LocalActuationForce.Y) &&
        double.IsFinite(ActuationTorque) &&
        double.IsFinite(AgeSeconds) &&
        double.IsFinite(Maturity) &&
        double.IsFinite(ReproductionCooldownSeconds) &&
        Maturity is >= 0.0 and <= 1.0 &&
        Body is not null && Pose is not null && AppendageMechanics is not null &&
        AppendageRegions is not null && AppendageRegions.All(region=>region.AllFinite) &&
        AppendageContactCount>=0 && double.IsFinite(AppendageSupport) &&
        AppendageSupport is >=0.0 and <=1.0 &&
        float.IsFinite(AppendageGroundVelocity.X)&&float.IsFinite(AppendageGroundVelocity.Y)&&
        double.IsFinite(AppendageEnergyLastStep)&&AppendageEnergyLastStep>=0.0&&
        CavityState is not null&&CavityState.AllFinite&&
        double.IsFinite(CavityVentilationLastStep)&&double.IsFinite(CavityTissueOxygenLastStep)&&
        double.IsFinite(CavityEnergyLastStep)&&CavityEnergyLastStep>=0.0;
}
