namespace NativeEpoch.Simulation;

public readonly record struct SimulationSnapshot(
    long StepIndex,
    double SimulatedSeconds,
    int Population,
    long CumulativeBirths,
    long CumulativeDeaths,
    long DamageDeaths,
    long JuvenileDeaths,
    long SenescenceDeaths,
    int GenomeCount,
    int TotalBodyRegions,
    double AverageMaturity,
    double EnvironmentMinerals,
    double EnvironmentDetritus,
    double EnvironmentMetabolicWaste,
    double OrganismBodyMatter,
    double OrganismStoredMatter,
    double TotalMatter,
    double InitialMatter,
    double CumulativeExternalMatter,
    double MatterError,
    double LivingEnergy,
    double CumulativeLightEnergy,
    double CumulativeDissipatedEnergy,
    double AverageSpeed,
    double CumulativeMovementEnergy,
    int AquaticPopulation,
    int ShorePopulation,
    int LandPopulation,
    double AverageDepth,
    double AverageHydration,
    double EnvironmentOxygen,
    double OrganismOxygen,
    double InitialOxygen,
    double CumulativeExternalOxygenSupply,
    double CumulativeOxygenUptake,
    double CumulativeOxygenConsumed,
    double OxygenError,
    double CumulativeWaterUptake,
    double CumulativeWaterLoss,
    double MaximumCacheError,
    bool AllFinite,
    ulong StateFingerprint)
{
    public double EnvironmentVegetation { get; init; }
    public double EnvironmentEdibleOrganics { get; init; }
    public double CumulativePrimaryProduction { get; init; }
    public double CumulativeOrganicFeeding { get; init; }
    public double CumulativeDecomposition { get; init; }
    public double CumulativePredationOrganic { get; init; }
}

public readonly record struct EnvironmentInterventionRecord(
    long StepIndex,
    EnvironmentBrushCommand Command,
    double AppliedMatterDelta);

public readonly record struct BirthRecord(
    ulong ParentId,
    ulong ChildId,
    int ParentGenomeId,
    int ChildGenomeId,
    int ParentGeneCount,
    int ChildGeneCount,
    MutationKind MutationKind,
    string MutationSummary)
{
    public int MutationEventCount { get; init; }
    public IReadOnlyList<MutationKind> MutationEventKinds { get; init; } = Array.Empty<MutationKind>();
}

public enum DeathCause
{
    AccumulatedDamage,
    JuvenileFailure,
    Senescence
}

public readonly record struct DeathRecord(
    long StepIndex,
    ulong OrganismId,
    ulong ParentId,
    DeathCause Cause,
    double AgeSeconds,
    double Energy,
    double Substrate,
    double Oxygen,
    double Hydration,
    double DevelopmentCompletion);
