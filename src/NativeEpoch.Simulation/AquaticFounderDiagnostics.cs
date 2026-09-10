namespace NativeEpoch.Simulation;

public readonly record struct AquaticFounderDiagnosticResult(
    bool SameSeedInitialFingerprint,
    bool DifferentSeedFounderGenetics,
    int DistinctFounderGenomes,
    int DistinctCoreGeometries,
    int DistinctGenomeRegionCounts,
    bool AllInitialAquaticDepthsLegal,
    bool AllInitialAgeAndMaturityZero,
    bool NoAdvancedFounderOrgans,
    bool InitialPhotosyntheticProducers,
    bool ExplicitFounderStayedUniform,
    double SharedMatterBudgetDifference,
    long ObservationSteps,
    long BirthsObserved,
    int Survivors,
    bool FrozenInheritanceExact,
    bool Passed);

/// <summary>
/// Bounded checks for seeded primitive founder diversity. Initial diversity is not counted as mutation.
/// </summary>
public static class AquaticFounderDiagnostics
{
    private const ulong PrimarySeed = 20260909;
    private const int FounderCount = 24;
    private const int MaximumObservationSteps = 1_200;

    public static AquaticFounderDiagnosticResult Run()
    {
        SimulationConfig config = new() { ResourceBudgetReferenceAncestors = FounderCount };
        SimulationWorld first = new(config, PrimarySeed, FounderCount);
        SimulationWorld repeat = new(config, PrimarySeed, FounderCount);
        SimulationWorld otherSeed = new(config, PrimarySeed + 1, FounderCount);

        ulong[] firstGenes = FounderFingerprints(first);
        ulong[] repeatGenes = FounderFingerprints(repeat);
        ulong[] otherGenes = FounderFingerprints(otherSeed);
        bool sameSeed = first.CaptureSnapshot().StateFingerprint ==
                repeat.CaptureSnapshot().StateFingerprint &&
            firstGenes.SequenceEqual(repeatGenes) &&
            InitialPlacementSignature(first).SequenceEqual(InitialPlacementSignature(repeat));
        bool differentSeed = !firstGenes.SequenceEqual(otherGenes);

        int distinctGenomes = firstGenes.Distinct().Count();
        int distinctCoreGeometries = first.Organisms.Select(organism =>
        {
            BodyGeometryRegion core = organism.Body.Geometry.Regions.Single(region =>
                first.Genomes.Get(organism.GenomeId).GetRegion(region.RegionId).IsCore);
            return (core.Length, core.StartRadius, core.EndRadius, core.VerticalScale,
                core.AnalyticVolume);
        }).Distinct().Count();
        int distinctRegionCounts = first.Organisms.Select(organism =>
            first.Genomes.Get(organism.GenomeId).Regions.Count).Distinct().Count();

        bool legalDepths = first.Organisms.All(organism =>
        {
            EnvironmentSample sample = first.Environment.Sample(organism.Position, organism.Depth);
            double halfThickness = Math.Max(0.08,
                organism.Body.Geometry.Regions.Max(region =>
                    Math.Max(region.StartRadius, region.EndRadius) * region.VerticalScale));
            return sample.WaterDepth > 0.0 && organism.Immersion == 1.0 &&
                organism.Depth >= halfThickness - 1e-5 &&
                organism.Depth <= sample.WaterDepth - halfThickness + 1e-5;
        });
        bool initialLifeState = first.Organisms.All(organism =>
            organism.ParentId == 0 && organism.Generation == 0 &&
            organism.AgeSeconds == 0.0 && organism.Maturity == 0.0);
        bool noAdvancedOrgans = first.Organisms.All(organism =>
        {
            Genome genome = first.Genomes.Get(organism.GenomeId);
            return genome.Regions.All(region =>
                    region.CavityFraction == 0.0 && region.CavityAperture == 0.0 &&
                    region.JointRestPitch == 0.0 && region.JointMobility == 0.0) &&
                genome.Sensors.All(sensor => sensor.Channel != SensorChannel.DirectionalLight);
        });
        bool photosyntheticProducers = first.Organisms.All(organism =>
            first.Genomes.Get(organism.GenomeId).Regions.All(region =>
                region.PhotosyntheticExpression > 0.0));

        Genome explicitFounder = Genome.CreateAncestor();
        SimulationWorld explicitWorld = new(config, PrimarySeed, FounderCount, explicitFounder);
        bool explicitUniform = explicitWorld.Genomes.Count == 1 &&
            explicitWorld.Organisms.All(organism =>
                explicitWorld.Genomes.Get(organism.GenomeId).Fingerprint == explicitFounder.Fingerprint);

        SimulationWorld smaller = new(config, PrimarySeed, 12);
        SimulationWorld larger = new(config, PrimarySeed, 48);
        double matterDifference = Math.Abs(
            smaller.CaptureSnapshot().InitialMatter - larger.CaptureSnapshot().InitialMatter);
        double matterTolerance = Math.Max(1e-8,
            Math.Abs(smaller.CaptureSnapshot().InitialMatter) * 1e-10);

        SimulationWorld inheritance = new(config, PrimarySeed, FounderCount,
            founderGenome: null, mutationsEnabled: false);
        for (int step = 0; step < MaximumObservationSteps; step++)
            inheritance.Step();
        bool inheritanceExact = inheritance.CumulativeBirths > 0 &&
            inheritance.RecentBirths.All(record =>
                record.MutationKind == MutationKind.None &&
                record.ParentGenomeId == record.ChildGenomeId &&
                record.ParentGeneCount == record.ChildGeneCount &&
                ReferenceEquals(inheritance.Genomes.Get(record.ParentGenomeId),
                    inheritance.Genomes.Get(record.ChildGenomeId)));
        SimulationSnapshot observed = inheritance.CaptureSnapshot();

        bool passed = sameSeed && differentSeed &&
            distinctGenomes > 1 && distinctCoreGeometries > 1 && distinctRegionCounts > 1 &&
            legalDepths && initialLifeState && noAdvancedOrgans && photosyntheticProducers && explicitUniform &&
            matterDifference <= matterTolerance && inheritanceExact && observed.Population > 0 &&
            observed.AllFinite && observed.StepIndex == MaximumObservationSteps;
        return new(sameSeed, differentSeed, distinctGenomes, distinctCoreGeometries,
            distinctRegionCounts, legalDepths, initialLifeState, noAdvancedOrgans, photosyntheticProducers, explicitUniform,
            matterDifference, observed.StepIndex, observed.CumulativeBirths, observed.Population,
            inheritanceExact, passed);
    }

    private static ulong[] FounderFingerprints(SimulationWorld world) =>
        world.Organisms.Select(organism => world.Genomes.Get(organism.GenomeId).Fingerprint).ToArray();

    private static IEnumerable<(ulong Id, int GenomeId, float X, float Y, float Depth, double Heading)>
        InitialPlacementSignature(SimulationWorld world) => world.Organisms.Select(organism =>
            (organism.Id, organism.GenomeId, organism.Position.X, organism.Position.Y,
                organism.Depth, organism.HeadingRadians));
}
