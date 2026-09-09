namespace NativeEpoch.Simulation;

public sealed record SimulationConfig
{
    public float WorldSize { get; init; } = 512f;
    public int EnvironmentGridSize { get; init; } = 128;
    public double TerrainElevationOffset { get; init; }
    public double FixedDeltaSeconds { get; init; } = 0.1;
    public int MaxPopulation { get; init; } = 20_000;
    public double InitialMineralScale { get; init; } = 1.0;
    // Optional preset: changing founder count transfers matter from the same
    // initial environment budget, rather than adding ecosystem resources.
    public int? ResourceBudgetReferenceAncestors { get; init; }
    public bool RandomizeFounders { get; init; } = true;
    public CorePrecisionProfile PrecisionProfile { get; init; } = CorePrecisionProfile.Balanced;

    public double CoreInitialMatter { get; init; } = 0.25;
    public double AncestorStoredMatter { get; init; } = 1.5;
    public double AncestorEnergy { get; init; } = 14.0;
    public double NewbornEnergy { get; init; } = 5.0;
    public double NewbornSubstrate { get; init; } = 0.40;
    public double MaximumEnergy { get; init; } = 30.0;
    public double BaseStoredMatter { get; init; } = 1.0;
    public double MatterUptakePerSurfacePerSecond { get; init; } = 0.10;
    public double MatterAssimilationEnergyPerMatter { get; init; } = 9.5;
    public double WasteRemineralizationPerSecond { get; init; } = 0.008;
    public double LightEnergyPerSurfacePerSecond { get; init; } = 1.45;
    public double SurfaceLightEnergyPerWorldAreaPerSecond { get; init; } = 0.05;
    public double BaseMaintenanceEnergyPerSecond { get; init; } = 0.12;
    public double GrowthMatterPerSecond { get; init; } = 0.18;
    public double GrowthEnergyPerMatter { get; init; } = 1.5;
    public double PropulsionAccelerationScale { get; init; } = 1.6;
    public double MaximumMovementSpeed { get; init; } = 2.4;
    public double VelocityDampingPerSecond { get; init; } = 0.85;
    public double MovementEnergyPerDistance { get; init; } = 0.16;
    public double ActiveSurfaceDriveSpeed { get; init; } = 4.0;
    public double ActiveSurfaceDriveEnergyScale { get; init; } = 0.08;
    public double ForagingSenseDistance { get; init; } = 4.0;
    public double ForagingCueMemorySeconds { get; init; } = 2.5;
    public double ExplorationMemorySeconds { get; init; } = 3.5;
    public double ExplorationMemoryRadius { get; init; } = 8.0;
    public double CuriosityStrength { get; init; } = 0.55;
    public double ExplorationActivityFloor { get; init; } = 0.52;
    public double ForagingRestEnergyFraction { get; init; } = 0.16;
    public float SeparationRadius { get; init; } = 2.8f;
    public double SeparationAcceleration { get; init; } = 4.0;
    public double ActiveContestForce { get; init; } = 2.2;
    public double ContestEnergyPerForceSecond { get; init; } = 0.06;
    public double ReproductionBlockedRetrySeconds { get; init; } = 1.5;
    public double ReproductionEnergyCost { get; init; } = 9.0;
    public double ReproductionEnergyThreshold { get; init; } = 14.0;
    public double MaturityAgeSeconds { get; init; } = 22.0;
    public double ReproductionCooldownSeconds { get; init; } = 28.0;
    public double SenescenceOnsetSeconds { get; init; } = 90.0;
    public double SenescenceTimeScaleSeconds { get; init; } = 90.0;
    public double SenescenceHazardPerSecond { get; init; } = 0.003;
    public double JuvenileHazardPerSecond { get; init; } = 0.012;
    public float NewbornOffsetRadius { get; init; } = 2.5f;
    public float MinimumAquaticSpawnDepth { get; init; } = 3.0f;
    public float PreferredAquaticSpawnMaximumDepth { get; init; } = 18.0f;
    public int MaximumAquaticSpawnAttempts { get; init; } = 256;
    public double InitialAquaticDepthFraction { get; init; } = 0.28;

    public double AncestorInternalOxygen { get; init; } = 0.12;
    public double InternalOxygenCapacityPerMatter { get; init; } = 0.45;
    public double WaterOxygenTransferCoefficient { get; init; } = 0.32;
    public double AirOxygenTransferCoefficient { get; init; } = 1.15;
    public double OxygenPerAerobicSubstrate { get; init; } = 0.70;
    public double AerobicEnergyPerSubstrate { get; init; } = 9.0;
    public double AnaerobicEnergyPerSubstrate { get; init; } = 2.3;
    public double MetabolicSubstratePerSecond { get; init; } = 0.022;
    public double ControllerNodeEnergyPerSecond { get; init; } = 0.003;
    public double ControllerActivationEnergyPerSecond { get; init; } = 0.025;
    public double SensorEnergyPerSlotPerSecond { get; init; } = 0.004;
    public double LocalActuationEnergyScale { get; init; } = 0.18;
    public double SecretionMatterPerSecond { get; init; } = 0.006;
    public double SecretionEnergyPerMatter { get; init; } = 0.8;

    public double VerticalContractionAcceleration { get; init; } = 1.0;
    public double BuoyancyAccelerationScale { get; init; } = 0.75;
    public double WaterVerticalDampingPerSecond { get; init; } = 1.4;
    public double LandFrictionMultiplier { get; init; } = 2.8;
    public double DryingRatePerSecond { get; init; } = 0.055;
    public double RehydrationRatePerSecond { get; init; } = 0.40;
    public double DehydrationEnergyCostPerSecond { get; init; } = 0.75;
    public double PressureMismatchEnergyCostPerSecond { get; init; } = 0.05;

    public void Validate(int ancestorCount)
    {
        PrecisionProfile.Validate();
        if (!float.IsFinite(WorldSize) || WorldSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(WorldSize));
        if (EnvironmentGridSize < 2)
            throw new ArgumentOutOfRangeException(nameof(EnvironmentGridSize));
        if (!double.IsFinite(TerrainElevationOffset))
            throw new ArgumentOutOfRangeException(nameof(TerrainElevationOffset));
        if (!double.IsFinite(FixedDeltaSeconds) || FixedDeltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(FixedDeltaSeconds));
        if (MaxPopulation < 1 || ancestorCount < 1 || ancestorCount > MaxPopulation)
            throw new ArgumentOutOfRangeException(nameof(ancestorCount));
        if (ResourceBudgetReferenceAncestors is < 0)
            throw new ArgumentOutOfRangeException(nameof(ResourceBudgetReferenceAncestors));
        if (MaximumAquaticSpawnAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumAquaticSpawnAttempts));
        if (!float.IsFinite(PreferredAquaticSpawnMaximumDepth) ||
            PreferredAquaticSpawnMaximumDepth <= MinimumAquaticSpawnDepth)
            throw new ArgumentOutOfRangeException(nameof(PreferredAquaticSpawnMaximumDepth));
        if (!double.IsFinite(InitialAquaticDepthFraction) || InitialAquaticDepthFraction is <= 0.0 or >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(InitialAquaticDepthFraction));
        if (CoreInitialMatter <= 0.0)
            throw new InvalidOperationException("A newborn core must contain positive matter.");
        if (ReproductionEnergyCost < NewbornEnergy)
            throw new InvalidOperationException("Reproduction must pay at least the newborn's starting energy.");
        if (MatterAssimilationEnergyPerMatter < AerobicEnergyPerSubstrate)
            throw new InvalidOperationException("Mineral assimilation must cost at least the maximum substrate energy yield.");
        if (ExplorationActivityFloor is > 1.0 || ForagingRestEnergyFraction is >= 1.0)
            throw new InvalidOperationException("Foraging activity and rest fractions must stay below one.");

        double[] finitePositive =
        [
            InitialMineralScale, CoreInitialMatter, AncestorStoredMatter, AncestorEnergy, NewbornEnergy, NewbornSubstrate,
            MaximumEnergy, BaseStoredMatter, MatterUptakePerSurfacePerSecond,
            MatterAssimilationEnergyPerMatter, WasteRemineralizationPerSecond, LightEnergyPerSurfacePerSecond,
            SurfaceLightEnergyPerWorldAreaPerSecond,
            BaseMaintenanceEnergyPerSecond, GrowthMatterPerSecond, GrowthEnergyPerMatter,
            PropulsionAccelerationScale, MaximumMovementSpeed, VelocityDampingPerSecond,
            MovementEnergyPerDistance, ActiveSurfaceDriveSpeed, ActiveSurfaceDriveEnergyScale,
            ForagingSenseDistance, ForagingCueMemorySeconds,
            ExplorationMemorySeconds, ExplorationMemoryRadius, CuriosityStrength,
            ExplorationActivityFloor, ForagingRestEnergyFraction,
            SeparationRadius, SeparationAcceleration,
            ActiveContestForce,ContestEnergyPerForceSecond,ReproductionBlockedRetrySeconds,
            ReproductionEnergyCost, ReproductionEnergyThreshold, MaturityAgeSeconds,
            ReproductionCooldownSeconds, SenescenceOnsetSeconds, SenescenceTimeScaleSeconds,
            SenescenceHazardPerSecond, JuvenileHazardPerSecond, NewbornOffsetRadius,
            MinimumAquaticSpawnDepth, AncestorInternalOxygen, InternalOxygenCapacityPerMatter,
            WaterOxygenTransferCoefficient, AirOxygenTransferCoefficient,
            OxygenPerAerobicSubstrate, AerobicEnergyPerSubstrate, AnaerobicEnergyPerSubstrate,
            MetabolicSubstratePerSecond, ControllerNodeEnergyPerSecond,
            ControllerActivationEnergyPerSecond, SensorEnergyPerSlotPerSecond,
            LocalActuationEnergyScale, SecretionMatterPerSecond,
            SecretionEnergyPerMatter, VerticalContractionAcceleration,
            BuoyancyAccelerationScale, WaterVerticalDampingPerSecond, LandFrictionMultiplier,
            DryingRatePerSecond, RehydrationRatePerSecond, DehydrationEnergyCostPerSecond,
            PressureMismatchEnergyCostPerSecond
        ];

        if (finitePositive.Any(value => !double.IsFinite(value) || value <= 0.0))
            throw new InvalidOperationException("All phase 0 life parameters must be finite and positive.");
    }
}
