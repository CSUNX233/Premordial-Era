namespace NativeEpoch.Simulation;

public readonly record struct SimulationSnapshot(
    long StepIndex,
    double SimulatedSeconds,
    int Population,
    long CumulativeBirths,
    long CumulativeDeaths,
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
    bool AllFinite,
    ulong StateFingerprint);
