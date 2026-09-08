namespace NativeEpoch.Simulation;

public readonly record struct SimulationSnapshot(
    long StepIndex,
    double SimulatedSeconds,
    int Population,
    long CumulativeBirths,
    long CumulativeDeaths,
    int GenomeCount,
    int TotalBodyRegions,
    double AverageMaturity,
    double EnvironmentMinerals,
    double EnvironmentDetritus,
    double OrganismBodyMatter,
    double OrganismStoredMatter,
    double TotalMatter,
    double InitialMatter,
    double MatterError,
    double LivingEnergy,
    double CumulativeLightEnergy,
    double CumulativeDissipatedEnergy,
    double MaximumCacheError,
    bool AllFinite,
    ulong StateFingerprint);

public readonly record struct BirthRecord(
    ulong ParentId,
    ulong ChildId,
    int ParentGenomeId,
    int ChildGenomeId,
    int ParentGeneCount,
    int ChildGeneCount,
    MutationKind MutationKind,
    string MutationSummary);
