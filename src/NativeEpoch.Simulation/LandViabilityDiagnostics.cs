using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct LandViabilityTrial(
    ulong GenomeFingerprint,
    double SimulatedSeconds,
    double LandSeconds,
    double FirstWaterEntrySeconds,
    double NetDisplacement,
    double PathLength,
    double PathBodyLengths,
    double AppendageDrivenPath,
    double AppendageEnergySpent,
    double MaximumBodyLift,
    int MaximumPlantedContacts,
    double OxygenUptake,
    double WaterLost,
    double WaterUptake,
    double SoilWaterTransferDuringFootContact,
    double InitialHydration,
    double MinimumHydration,
    double FinalHydration,
    double HydrationAfterTwentySeconds,
    double SecondsUntilHalfHydration,
    double HypoxiaAfterTenSeconds,
    double MaximumMaturity,
    double MaximumDescendantMaturity,
    double MaximumLandBornMaturityOnLand,
    double MaximumLandBornMaturityInWater,
    double FirstLandBirthHydration,
    long Births,
    long LandBirths,
    int LivingDescendants,
    int MatureDescendants,
    int LandBornMaturedOnLand,
    int LandBornMaturedInWater,
    long BirthsByLandBornDescendants,
    long LandBirthsByLandBornDescendants,
    int MaximumGeneration,
    bool OffspringInheritedFixtureGenome,
    double InitialFiniteMatter,
    double FinalFiniteMatter,
    double ExternalMatterAdded,
    double WaterBalanceError,
    bool AllFinite);

public readonly record struct LandViabilityDiagnosticResult(
    LandViabilityTrial Viable,
    LandViabilityTrial JointDisabled,
    LandViabilityTrial AirExchangeDisabled,
    LandViabilityTrial WaterRetentionDisabled,
    bool FixtureIsArtificial,
    bool FunctionalAppendagesMoveOnLand,
    bool ZeroEnergyCannotWalk,
    bool AirExchangeIsCausal,
    bool WaterRetentionIsCausal,
    bool LandBirthAndInheritanceObserved,
    bool Passed);

/// <summary>
/// End-to-end, deterministic mechanism assay in a real <see cref="SimulationWorld"/>.
/// The constructed genome is an explicit artificial fixture, not evidence that a natural run
/// evolved a terrestrial lineage. Its targeted phenotype knockouts retain the rest of the
/// fixture's genetic background.
/// </summary>
public static class LandViabilityDiagnostics
{
    public const ulong DiagnosticSeed = 0x1A4D_A11FUL;
    private const int TrialSteps = 4_800;

    public static LandViabilityDiagnosticResult Run()
    {
        Genome viableGenome = CreateArtificialLandFixtureGenome();
        Genome jointDisabledGenome = ReplaceRegions(viableGenome, gene => gene.IsCore
            ? gene
            : gene with { JointMobility = 0.0, JointRestPitch = 0.0 });
        Genome airDisabledGenome = ReplaceRegions(viableGenome,
            gene => gene with
            {
                AirExchangeAffinity = 0.0,
                CavityAperture = 0.0
            });
        Genome retentionDisabledRegions = ReplaceRegions(viableGenome,
            gene=>gene with { BarrierExpression=0.0 });
        Genome retentionDisabledGenome=new(retentionDisabledRegions.Regions,
            retentionDisabledRegions.MutationRate,
            retentionDisabledRegions.Metabolism with { WaterRetention=0.0 },
            retentionDisabledRegions.ControllerNodes,retentionDisabledRegions.Sensors);

        LandViabilityTrial viable = RunTrial(viableGenome, DiagnosticSeed, TrialSteps);
        LandViabilityTrial jointDisabled = RunTrial(jointDisabledGenome, DiagnosticSeed, TrialSteps);
        LandViabilityTrial airDisabled = RunTrial(airDisabledGenome, DiagnosticSeed, TrialSteps);
        LandViabilityTrial retentionDisabled = RunTrial(retentionDisabledGenome, DiagnosticSeed, TrialSteps);
        AppendageMechanicsDiagnosticResult appendage = AppendageMechanicsDiagnostics.Run();

        bool walking = viable.MaximumPlantedContacts >= 1 &&
            viable.AppendageDrivenPath > jointDisabled.AppendageDrivenPath + 0.05 &&
            viable.AppendageEnergySpent > jointDisabled.AppendageEnergySpent + 1e-6 &&
            viable.PathBodyLengths >= 2.0;
        bool airCausal = viable.OxygenUptake > 1e-4 &&
            airDisabled.OxygenUptake < viable.OxygenUptake * 0.01;
        bool retentionCausal = viable.FirstWaterEntrySeconds>0.0&&
            retentionDisabled.FirstWaterEntrySeconds>0.0&&
            viable.FirstWaterEntrySeconds>retentionDisabled.FirstWaterEntrySeconds+5.0||
            viable.WaterLost/Math.Max(0.1,viable.LandSeconds)<
            retentionDisabled.WaterLost/Math.Max(0.1,retentionDisabled.LandSeconds)*0.75;
        bool inheritance = viable.LandBirths > 0 &&
            viable.MaximumDescendantMaturity > 0.20 && viable.MaximumGeneration >= 1 &&
            viable.OffspringInheritedFixtureGenome;
        bool finiteResources = viable.ExternalMatterAdded == 0.0 &&
            viable.InitialFiniteMatter > 0.0 && viable.FinalFiniteMatter >= 0.0&&
            viable.WaterBalanceError<1e-8;
        bool finite = viable.AllFinite && jointDisabled.AllFinite &&
            airDisabled.AllFinite && retentionDisabled.AllFinite;
        bool passed = finite && walking && viable.SoilWaterTransferDuringFootContact>1e-5&&
            appendage.ZeroEnergyDisplacement < 1e-9 &&
            airCausal && retentionCausal && inheritance && finiteResources;

        return new(viable, jointDisabled, airDisabled, retentionDisabled,
            FixtureIsArtificial: true,
            FunctionalAppendagesMoveOnLand: walking,
            ZeroEnergyCannotWalk: appendage.ZeroEnergyDisplacement < 1e-9,
            AirExchangeIsCausal: airCausal,
            WaterRetentionIsCausal: retentionCausal,
            LandBirthAndInheritanceObserved: inheritance,
            Passed: passed);
    }

    /// <summary>
    /// Creates the same real, finite-resource world used by the viable assay and pre-runs only
    /// enough paid growth for the artificial fixture's appendages to be visible and functional.
    /// This world must never be merged into natural-lineage evidence or persistent discoveries.
    /// </summary>
    public static SimulationWorld CreateDemonstrationWorld()
    {
        SimulationWorld world = CreateWorld(CreateArtificialLandFixtureGenome(), DiagnosticSeed);
        int aquaticGrowthSteps=(int)Math.Ceiling(60.0/world.Config.FixedDeltaSeconds);
        for(int step=0;step<aquaticGrowthSteps&&world.Organisms.Count>0;step++)world.Step();
        if(world.Organisms.Count==0)
            throw new InvalidOperationException("Artificial land fixture died during paid aquatic development.");
        if(world.Organisms.Count!=1||world.Organisms[0].Generation!=0)
            throw new InvalidOperationException("Artificial land fixture reproduced before its land exposure.");
        PlaceFounderOnInlandLand(world);
        for(int step=0;step<8&&world.Organisms.Count>0;step++)world.Step();
        return world;
    }

    public static Genome CreateArtificialLandFixtureGenome()
    {
        Genome ancestor = Genome.CreateAncestor();
        RegionGene[] regions = ancestor.Regions.Select(gene => gene with
        {
            AppearanceMaturity = gene.IsCore ? 0.0 : 0.02,
            TargetLength = gene.IsCore ? 0.56 : 0.76,
            TargetWidth = gene.IsCore ? 0.46 : 0.17,
            Rigidity = Math.Max(gene.Rigidity, 0.86),
            Toughness = Math.Max(gene.Toughness, 0.88),
            Permeability = 0.30,
            CatalyticActivity = Math.Max(gene.CatalyticActivity, 0.82),
            Contractility = Math.Max(gene.Contractility, 0.90),
            SignalConductivity = Math.Max(gene.SignalConductivity, 0.76),
            StorageFraction = Math.Max(gene.StorageFraction, 0.72),
            ExchangeExpression = 0.52,
            BarrierExpression = 0.98,
            ContractileExpression = gene.IsCore ? 0.34 : 0.82,
            StructuralExpression = gene.IsCore ? 0.62 : 0.88,
            SensoryExpression = 0.12,
            CavityFraction = gene.IsCore?0.88:0.0,
            CavityAperture = gene.IsCore?0.82:0.0,
            PhotosyntheticExpression = 0.62,
            FeedingExpression = 0.0,
            DigestiveExpression = 0.0,
            AirExchangeAffinity = 0.98,
            JointRestPitch = gene.IsCore ? 0.0 : -0.88,
            JointMobility = gene.IsCore ? 0.0 : 0.94,
            RelativeAngle = gene.IsCore ? 0.0 : gene.RegionId == 1 ? -0.72 : 0.72,
            Pigment = gene.IsCore ? 0.32 : gene.RegionId == 1 ? 0.18 : 0.68
        }).ToArray();
        MetabolicGene metabolism = ancestor.Metabolism with
        {
            OxygenUseFraction = 0.46,
            OxygenCatalysis = 0.86,
            WaterRetention = 0.95,
            OsmoticTolerance = 0.72
        };
        return new Genome(regions, ancestor.MutationRate, metabolism,
            ancestor.ControllerNodes, ancestor.Sensors);
    }

    private static SimulationWorld CreateWorld(Genome genome, ulong seed)
    {
        SimulationConfig config = new()
        {
            WorldSize = 128,
            EnvironmentGridSize = 49,
            RandomizeFounders = false,
            MaxPopulation = 48,
            ResourceBudgetReferenceAncestors = 8,
            FounderReproductionCooldownSeconds=65.0
        };
        return new SimulationWorld(config, seed, 1, genome, mutationsEnabled: false);
    }

    private static LandViabilityTrial RunTrial(Genome genome, ulong seed, int steps)
    {
        SimulationWorld world = CreateWorld(genome, seed);
        // Let the founder hydrate and pay for early development in its normal aquatic spawn.
        // The measured land exposure still begins from one fixed, dry coordinate.
        int aquaticPreparationSteps=(int)Math.Ceiling(60.0/world.Config.FixedDeltaSeconds);
        for (int step = 0; step < aquaticPreparationSteps&&world.Organisms.Count>0; step++) world.Step();
        if(world.Organisms.Count==0)
            throw new InvalidOperationException("Artificial land fixture died during paid aquatic development.");
        if(world.Organisms.Count!=1||world.Organisms[0].Generation!=0)
            throw new InvalidOperationException("Artificial land fixture reproduced before its land exposure.");
        Vector2 start = PlaceFounderOnInlandLand(world);
        ulong founderId = world.Organisms[0].Id;
        SimulationSnapshot initial = world.CaptureSnapshot();
        BilinearEnvironmentField environment=(BilinearEnvironmentField)world.Environment;
        double initialSoilWater=environment.TotalSoilWater;
        double initialBodyWater=world.Organisms.Sum(organism=>organism.Body.TotalWater);
        double initialWaterUptake=world.CumulativeWaterUptake;
        double initialSoilWaterUptake=environment.CumulativeSoilWaterUptake;
        double initialWaterLoss=world.CumulativeWaterLoss;
        double initialHydration=world.Organisms[0].Hydration;
        Vector2 previous = start;
        Vector2 lastLandPosition=start;
        double bodyLength=Math.Max(0.1,world.Organisms[0].Body.Cache.BoundingRadius*2.0);
        double path = 0.0, appendagePath = 0.0, landSeconds = 0.0;
        double appendageEnergy=0.0;
        double maximumBodyLift=0.0;
        double landOxygenUptake=0.0,landWaterLoss=0.0,landWaterUptake=0.0,footWaterUptake=0.0;
        double previousWorldWaterLoss=world.CumulativeWaterLoss;
        double previousWorldWaterUptake=world.CumulativeWaterUptake;
        double previousSoilWaterUptake=environment.CumulativeSoilWaterUptake;
        double firstWater = double.PositiveInfinity, minimumHydration = initialHydration;
        double halfHydrationSeconds=double.PositiveInfinity;
        double hypoxiaAtTen=double.NaN;
        double hydrationAtTwenty = double.NaN,maximumMaturity=0.0,maximumDescendantMaturity=0.0;
        double maximumLandBornMaturityOnLand=0.0,maximumLandBornMaturityInWater=0.0;
        int maximumContacts = 0;
        int maximumLandGeneration=0;
        long landBirths=0,birthsByLandBorn=0,landBirthsByLandBorn=0;
        double firstLandBirthHydration=double.NaN;
        HashSet<ulong> observedBirths=[];
        HashSet<ulong> landBornIds=[];
        HashSet<ulong> maturationRecorded=[];
        HashSet<ulong> maturedOnLand=[];
        HashSet<ulong> maturedInWater=[];
        bool finite = initial.AllFinite;
        for (int step = 0; step < steps && world.Organisms.Count > 0; step++)
        {
            bool hadPlantedFootContact=world.TryGetOrganism(founderId,out Organism beforeStepFounder)&&
                beforeStepFounder.SoilWaterContacts.Count>0;
            world.Step();
            double worldWaterLoss=world.CumulativeWaterLoss;
            double stepWaterLoss=Math.Max(0.0,worldWaterLoss-previousWorldWaterLoss);
            previousWorldWaterLoss=worldWaterLoss;
            double worldWaterUptake=world.CumulativeWaterUptake;
            double stepWaterUptake=Math.Max(0.0,worldWaterUptake-previousWorldWaterUptake);
            previousWorldWaterUptake=worldWaterUptake;
            double soilWaterUptake=environment.CumulativeSoilWaterUptake;
            double stepSoilWaterUptake=Math.Max(0.0,soilWaterUptake-previousSoilWaterUptake);
            previousSoilWaterUptake=soilWaterUptake;
            finite &= world.CaptureSnapshot().AllFinite;
            foreach(Organism descendant in world.Organisms)
                if(landBornIds.Contains(descendant.Id))
                {
                    maximumDescendantMaturity=Math.Max(maximumDescendantMaturity,descendant.Maturity);
                    EnvironmentSample descendantSample=world.Environment.Sample(
                        descendant.Position,descendant.Depth);
                    bool descendantOnLand=descendantSample.WaterDepth<=1e-6&&descendant.Immersion<0.05;
                    if(descendantOnLand)
                    {
                        maximumLandBornMaturityOnLand=Math.Max(
                            maximumLandBornMaturityOnLand,descendant.Maturity);
                    }
                    else
                    {
                        maximumLandBornMaturityInWater=Math.Max(
                            maximumLandBornMaturityInWater,descendant.Maturity);
                    }
                    if(descendant.Maturity>=0.95&&maturationRecorded.Add(descendant.Id))
                    {
                        if(descendantOnLand)maturedOnLand.Add(descendant.Id);
                        else maturedInWater.Add(descendant.Id);
                    }
                }
            foreach(BirthRecord birth in world.RecentBirths)
            {
                if(!observedBirths.Add(birth.ChildId)||
                    !world.TryGetOrganism(birth.ChildId,out Organism newborn))continue;
                EnvironmentSample birthSample=world.Environment.Sample(newborn.Position,newborn.Depth);
                bool newbornOnLand=birthSample.WaterDepth<=1e-6&&newborn.Immersion<0.05;
                if(landBornIds.Contains(birth.ParentId))
                {
                    birthsByLandBorn++;
                    if(newbornOnLand)landBirthsByLandBorn++;
                }
                if(newbornOnLand)
                {
                    landBirths++;
                    landBornIds.Add(newborn.Id);
                    if(double.IsNaN(firstLandBirthHydration))firstLandBirthHydration=newborn.Hydration;
                    maximumLandGeneration=Math.Max(maximumLandGeneration,newborn.Generation);
                }
            }
            if (world.TryGetOrganism(founderId, out Organism founder))
            {
                maximumMaturity=Math.Max(maximumMaturity,founder.Maturity);
                double distance = SphericalWorld.Distance(previous, founder.Position, world.Config.WorldSize);
                previous = founder.Position;
                maximumContacts = Math.Max(maximumContacts, founder.AppendageContactCount);
                maximumBodyLift=Math.Max(maximumBodyLift,founder.AppendageBodyLift);
                minimumHydration = Math.Min(minimumHydration, founder.Hydration);
                EnvironmentSample sample = world.Environment.Sample(founder.Position, founder.Depth);
                bool onLand = sample.WaterDepth <= 1e-6 && founder.Immersion < 0.05;
                if (onLand)
                {
                    path += distance;
                    lastLandPosition=founder.Position;
                    appendagePath += founder.AppendageGroundVelocity.Length() * world.Config.FixedDeltaSeconds;
                    appendageEnergy+=founder.AppendageEnergyLastStep;
                    landSeconds += world.Config.FixedDeltaSeconds;
                    landOxygenUptake+=founder.OxygenUptakeLastStep+founder.CavityVentilationLastStep;
                    landWaterLoss+=stepWaterLoss;
                    landWaterUptake+=stepWaterUptake;
                    // This is the world's conserved soil-water transfer during a step in which
                    // the founder had a loaded foot. The isolated soil diagnostic provides the
                    // exact per-contact attribution and suspended/disabled controls.
                    if(hadPlantedFootContact)footWaterUptake+=stepSoilWaterUptake;
                }
                else if (double.IsPositiveInfinity(firstWater))
                    firstWater = world.SimulatedSeconds-initial.SimulatedSeconds;
                double elapsed=world.SimulatedSeconds-initial.SimulatedSeconds;
                if(double.IsPositiveInfinity(halfHydrationSeconds)&&founder.Hydration<0.50)
                    halfHydrationSeconds=elapsed;
                if(double.IsNaN(hypoxiaAtTen)&&elapsed>=10.0)
                    hypoxiaAtTen=founder.HypoxiaShortfallLastStep;
                if(double.IsNaN(hydrationAtTwenty)&&elapsed>=20.0)hydrationAtTwenty=founder.Hydration;
            }
        }

        SimulationSnapshot final = world.CaptureSnapshot();
        double externalWater=(world.CumulativeWaterUptake-initialWaterUptake)-
            (environment.CumulativeSoilWaterUptake-initialSoilWaterUptake);
        double waterBalanceError=Math.Abs(initialSoilWater+initialBodyWater+externalWater-
            environment.TotalSoilWater-world.Organisms.Sum(organism=>organism.Body.TotalWater)-
            (world.CumulativeWaterLoss-initialWaterLoss));
        Organism? founderAtEnd = world.TryGetOrganism(founderId, out Organism livingFounder)
            ? livingFounder
            : null;
        Genome fixture = genome;
        Organism[] descendants = world.Organisms.Where(organism => landBornIds.Contains(organism.Id)).ToArray();
        bool inherited = landBornIds.Count>0&&world.RecentBirths
            .Where(record=>landBornIds.Contains(record.ChildId))
            .All(record => world.Genomes.Get(record.ChildGenomeId).Fingerprint == fixture.Fingerprint &&
                record.MutationKind == MutationKind.None);
        double finalHydration = founderAtEnd?.Hydration ?? 0.0;
        double net=SphericalWorld.Distance(start,lastLandPosition,world.Config.WorldSize);
        return new(fixture.Fingerprint, final.SimulatedSeconds-initial.SimulatedSeconds, landSeconds,
            double.IsPositiveInfinity(firstWater) ? -1.0 : firstWater,
            net, path, path/bodyLength,appendagePath,appendageEnergy,maximumBodyLift,maximumContacts,
            landOxygenUptake,landWaterLoss,landWaterUptake,footWaterUptake,
            initialHydration,minimumHydration, finalHydration,
            double.IsNaN(hydrationAtTwenty)?finalHydration:hydrationAtTwenty,
            double.IsPositiveInfinity(halfHydrationSeconds)?final.SimulatedSeconds-initial.SimulatedSeconds:halfHydrationSeconds,
            double.IsNaN(hypoxiaAtTen)?0.0:hypoxiaAtTen,
            maximumMaturity,maximumDescendantMaturity,
            maximumLandBornMaturityOnLand,maximumLandBornMaturityInWater,
            double.IsNaN(firstLandBirthHydration)?-1.0:firstLandBirthHydration,
            final.CumulativeBirths-initial.CumulativeBirths,
            landBirths,
            descendants.Length, descendants.Count(organism => organism.Maturity >= 0.95),
            maturedOnLand.Count,maturedInWater.Count,birthsByLandBorn,landBirthsByLandBorn,
            maximumLandGeneration,
            inherited, initial.TotalMatter, final.TotalMatter,
            final.CumulativeExternalMatter-initial.CumulativeExternalMatter,
            waterBalanceError,
            finite && final.AllFinite);
    }

    private static Vector2 PlaceFounderOnInlandLand(SimulationWorld world)
    {
        float scan=world.Config.WorldSize/128f;
        Vector2[] directions=[Vector2.UnitX,-Vector2.UnitX,Vector2.UnitY,-Vector2.UnitY];
        Vector2 position=default,landDirection=default;
        bool found=false;
        double bestMoisture=double.NegativeInfinity;
        for(float y=scan;y<world.Config.WorldSize-scan;y+=scan)
        for(float x=0;x<world.Config.WorldSize;x+=scan)
        {
            Vector2 water=new(x,y);
            EnvironmentSample wet=world.Environment.Sample(water);
            if(wet.WaterDepth is <0.3 or >3.0||wet.DissolvedOxygenAvailability<0.20)continue;
            foreach(Vector2 direction in directions)
            {
                Vector2 dry=SphericalWorld.OffsetPosition(water,direction*scan,world.Config.WorldSize);
                if(world.Environment.Sample(dry).WaterDepth>0.0)continue;
                float low=0f,high=scan;
                for(int iteration=0;iteration<18;iteration++)
                {
                    float middle=(low+high)*0.5f;
                    Vector2 candidate=SphericalWorld.OffsetPosition(water,direction*middle,world.Config.WorldSize);
                    if(world.Environment.Sample(candidate).WaterDepth>0.0)low=middle;else high=middle;
                }
                float clearance=(float)Math.Max(0.25,
                    world.Organisms[0].Body.Cache.BoundingRadius*1.15);
                Vector2 candidatePosition=SphericalWorld.OffsetPosition(
                    water,direction*(high+clearance),world.Config.WorldSize);
                EnvironmentSample drySample=world.Environment.Sample(candidatePosition);
                if(drySample.WaterDepth>0.0||drySample.SoilWaterAvailability<=bestMoisture)continue;
                position=candidatePosition;landDirection=direction;
                bestMoisture=drySample.SoilWaterAvailability;found=true;
            }
        }
        if(!found)throw new InvalidOperationException("Seeded fixture world has no dry coastal land start.");
        ulong id = world.Organisms[0].Id;
        double heading=Math.Atan2(landDirection.Y,landDirection.X);
        if (!world.RelocateForMediumDiagnostic(id, position, 0.0f, heading))
            throw new InvalidOperationException("Artificial land fixture founder was not found.");
        EnvironmentSample placed = world.Environment.Sample(position);
        if (placed.WaterDepth > 1e-6)
            throw new InvalidOperationException("Artificial land fixture was not placed on land.");
        return position;
    }

    private static Genome ReplaceRegions(Genome source, Func<RegionGene, RegionGene> replace) =>
        new(source.Regions.Select(replace), source.MutationRate, source.Metabolism,
            source.ControllerNodes, source.Sensors);
}
