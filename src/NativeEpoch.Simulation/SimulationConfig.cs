namespace NativeEpoch.Simulation;

public sealed record SimulationConfig
{
    public float WorldSize { get; init; } = 512f;
    public int EnvironmentGridSize { get; init; } = 128;
    public double FixedDeltaSeconds { get; init; } = 0.1;
    public int MaxPopulation { get; init; } = 20_000;

    public double CoreInitialMatter { get; init; } = 0.25;
    public double AncestorStoredMatter { get; init; } = 1.5;
    public double AncestorEnergy { get; init; } = 14.0;
    public double NewbornEnergy { get; init; } = 5.0;
    public double MaximumEnergy { get; init; } = 30.0;
    public double BaseStoredMatter { get; init; } = 1.0;
    public double MatterUptakePerSurfacePerSecond { get; init; } = 0.10;
    public double LightEnergyPerSurfacePerSecond { get; init; } = 1.45;
    public double BaseMaintenanceEnergyPerSecond { get; init; } = 0.12;
    public double GrowthMatterPerSecond { get; init; } = 0.18;
    public double GrowthEnergyPerMatter { get; init; } = 1.5;
    public double PropulsionAccelerationScale { get; init; } = 1.6;
    public double MaximumMovementSpeed { get; init; } = 2.4;
    public double VelocityDampingPerSecond { get; init; } = 0.85;
    public double MovementEnergyPerDistance { get; init; } = 0.16;
    public float SeparationRadius { get; init; } = 2.8f;
    public double SeparationAcceleration { get; init; } = 4.0;
    public double ReproductionEnergyCost { get; init; } = 12.0;
    public double ReproductionEnergyThreshold { get; init; } = 16.0;
    public double MaturityAgeSeconds { get; init; } = 6.0;
    public double ReproductionCooldownSeconds { get; init; } = 6.0;
    public double MaximumAgeSeconds { get; init; } = 35.0;
    public float NewbornOffsetRadius { get; init; } = 2.5f;

    public void Validate(int ancestorCount)
    {
        if (!float.IsFinite(WorldSize) || WorldSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(WorldSize));
        if (EnvironmentGridSize < 2)
            throw new ArgumentOutOfRangeException(nameof(EnvironmentGridSize));
        if (!double.IsFinite(FixedDeltaSeconds) || FixedDeltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(FixedDeltaSeconds));
        if (MaxPopulation < 1 || ancestorCount < 1 || ancestorCount > MaxPopulation)
            throw new ArgumentOutOfRangeException(nameof(ancestorCount));
        if (CoreInitialMatter <= 0.0)
            throw new InvalidOperationException("A newborn core must contain positive matter.");
        if (ReproductionEnergyCost < NewbornEnergy)
            throw new InvalidOperationException("Reproduction must pay at least the newborn's starting energy.");

        double[] finitePositive =
        [
            CoreInitialMatter, AncestorStoredMatter, AncestorEnergy, NewbornEnergy, MaximumEnergy,
            BaseStoredMatter, MatterUptakePerSurfacePerSecond, LightEnergyPerSurfacePerSecond,
            BaseMaintenanceEnergyPerSecond, GrowthMatterPerSecond, GrowthEnergyPerMatter,
            PropulsionAccelerationScale, MaximumMovementSpeed, VelocityDampingPerSecond,
            MovementEnergyPerDistance, SeparationRadius, SeparationAcceleration,
            ReproductionEnergyCost, ReproductionEnergyThreshold, MaturityAgeSeconds,
            ReproductionCooldownSeconds, MaximumAgeSeconds, NewbornOffsetRadius
        ];

        if (finitePositive.Any(value => !double.IsFinite(value) || value <= 0.0))
            throw new InvalidOperationException("All phase 0 life parameters must be finite and positive.");
    }
}
