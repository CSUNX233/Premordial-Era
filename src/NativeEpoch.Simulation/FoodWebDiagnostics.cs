using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct FoodWebDiagnosticResult(
    bool MineralCannotFuelRespiration,
    bool PhotosynthesisRequiresMineral,
    bool PhotosynthesisStoresOrganicWithoutDirectEnergy,
    bool FeedingAndDigestionBothRequired,
    bool OrganicDigestionIsPaidAndConservative,
    bool ZeroEnergyDisablesDigestion,
    bool StoredOrganicRestartsZeroEnergyMetabolism,
    bool DecompositionReturnsMineralsConservatively,
    bool ZeroDecomposerHasNoBenefit,
    bool ContinuousCapacities,
    bool RichOrganicFoodSustainsConsumer,
    double RichFoodInitialReserve,
    double RichFoodMidpointReserve,
    double RichFoodFinalReserve,
    bool ExpressionBudgetAndCostsApplied,
    bool PredationExtractsOnlyStoredOrganic,
    double MaximumMatterResidual,
    bool Passed);

/// <summary>Short positive and negative checks for composable trophic tissue programs.</summary>
public static class FoodWebDiagnostics
{
    public static FoodWebDiagnosticResult Run()
    {
        SimulationConfig config = new();
        ExchangeProbe mineralOnly = ProbeExchange(TestGene(photo: 0.0), config,
            light: 1.0, minerals: 1.0);
        ExchangeProbe noMineral = ProbeExchange(TestGene(photo: 0.85), config,
            light: 1.0, minerals: 0.0);
        ExchangeProbe producer = ProbeExchange(TestGene(photo: 0.85), config,
            light: 1.0, minerals: 1.0);
        bool mineralCannotFuel = NearZero(mineralOnly.Result.PhotosynthesizedSubstrate) &&
            NearZero(mineralOnly.Result.LightEnergy) &&
            mineralOnly.EnergyAfter <= mineralOnly.EnergyBefore + 1e-12;
        bool needsMineral = noMineral.Result.AbsorbedLight > 0.0 &&
            NearZero(noMineral.Result.PhotosynthesizedSubstrate) && NearZero(noMineral.Result.LightEnergy);
        bool storesOrganic = producer.Result.PhotosynthesizedSubstrate > 0.0 &&
            Near(producer.Result.PhotosynthesizedSubstrate, producer.Result.MineralConsumed) &&
            producer.SubstrateAfter > producer.SubstrateBefore &&
            producer.EnergyAfter <= producer.EnergyBefore + 1e-12;

        DevelopingBody feedOnly = FullBody(new Genome([TestGene(feed: 0.8, digest: 0.0)], 0.0), 2.0);
        DevelopingBody digestOnly = FullBody(new Genome([TestGene(feed: 0.0, digest: 0.8)], 0.0), 2.0);
        bool bothRequired = NearZero(RegionalPhysiology.FeedingCapacity(feedOnly,
                new Genome([TestGene(feed: 0.8, digest: 0.0)], 0.0))) &&
            NearZero(RegionalPhysiology.FeedingCapacity(digestOnly,
                new Genome([TestGene(feed: 0.0, digest: 0.8)], 0.0)));

        Genome consumerGenome = new([TestGene(feed: 0.8, digest: 0.8)], 0.0);
        DevelopingBody consumer = FullBody(consumerGenome, 2.0);
        OrganicDigestionResult digestion = RegionalPhysiology.DigestOrganicToSubstrate(
            consumer, consumerGenome, 0.10, 1.0);
        double organicResidual = Math.Abs(digestion.OfferedOrganic -
            (digestion.AssimilatedSubstrate + digestion.UnprocessedOrganic +
             digestion.DigestionWaste));
        bool paidDigestion = digestion.ProcessedOrganic > 0.0 &&
            digestion.AssimilatedSubstrate > 0.0 && digestion.EnergySpent > 0.0 &&
            organicResidual <= 1e-12;
        DevelopingBody starved = FullBody(consumerGenome, 0.0);
        OrganicDigestionResult starvedDigestion = RegionalPhysiology.DigestOrganicToSubstrate(
            starved, consumerGenome, 0.10, 1.0);
        bool zeroEnergy = NearZero(starvedDigestion.ProcessedOrganic) &&
            NearZero(starvedDigestion.AssimilatedSubstrate);
        DevelopingBody dormant = FullBody(consumerGenome, 0.0, substrate: 0.20, oxygen: 2.0);
        RegionalMetabolismResult restarted = RegionalPhysiology.ReactAndMaintain(
            dormant, consumerGenome, new FixedEnvironment(0.0), Vector2.Zero, config, 0.1);
        bool storedOrganicRestarts = restarted.SubstrateConsumed > 0.0 &&
            restarted.EnergyProduced > 0.0 && dormant.TotalEnergy > 0.0;

        Genome decomposerGenome = new([TestGene(digest: 0.85, decompose: 0.85)], 0.0);
        DevelopingBody decomposer = FullBody(decomposerGenome, 2.0);
        DecompositionResult decomposition = RegionalPhysiology.DecomposeDetritus(
            decomposer, decomposerGenome, 0.12, 1.0);
        double detritusResidual = Math.Abs(decomposition.OfferedDetritus -
            (decomposition.AssimilatedSubstrate + decomposition.ReturnedMinerals +
             decomposition.RejectedDetritus));
        bool decompositionConserved = decomposition.ReturnedMinerals > 0.0 &&
            decomposition.AssimilatedSubstrate > 0.0 && detritusResidual <= 1e-12;
        Genome noDecomposerGenome = new([TestGene(digest: 0.85, decompose: 0.0)], 0.0);
        DevelopingBody noDecomposer = FullBody(noDecomposerGenome, 2.0);
        DecompositionResult noDecomposition = RegionalPhysiology.DecomposeDetritus(
            noDecomposer, noDecomposerGenome, 0.12, 1.0);
        bool noDecomposerBenefit = NearZero(noDecomposition.AssimilatedSubstrate) &&
            NearZero(noDecomposition.ReturnedMinerals);

        Genome lowGenome = new([TestGene(feed: 0.20, digest: 0.50, decompose: 0.20)], 0.0);
        Genome highGenome = new([TestGene(feed: 0.80, digest: 0.80, decompose: 0.80)], 0.0);
        DevelopingBody low = FullBody(lowGenome, 2.0), high = FullBody(highGenome, 2.0);
        bool continuous = RegionalPhysiology.FeedingCapacity(high, highGenome) >
                RegionalPhysiology.FeedingCapacity(low, lowGenome) &&
            RegionalPhysiology.DecompositionCapacity(high, highGenome) >
                RegionalPhysiology.DecompositionCapacity(low, lowGenome);
        RichFoodTrajectory fed = RichFoodTrajectoryAfter(consumerGenome, config, true);
        RichFoodTrajectory unfed = RichFoodTrajectoryAfter(consumerGenome, config, false);
        bool richFoodSustains = fed.Final >= fed.Initial - 1e-9 &&
            fed.Final >= fed.Midpoint - 1e-9 &&
            fed.Final > unfed.Final + 1.0;

        RegionGene cheapGene = TestGene() with { ExchangeExpression = 0.80 };
        RegionGene costlyGene = cheapGene with
            { FeedingExpression = 0.80, DigestiveExpression = 0.80, DecomposerExpression = 0.80 };
        Genome cheapGenome = new([cheapGene], 0.0), costlyGenome = new([costlyGene], 0.0);
        DevelopingBody cheap = FullBody(cheapGenome, 2.0), costly = FullBody(costlyGenome, 2.0);
        bool costs = BodyCalculator.TargetMatter(costlyGene) > BodyCalculator.TargetMatter(cheapGene) &&
            costly.Cache.MaintenanceEnergyPerSecond > cheap.Cache.MaintenanceEnergyPerSecond &&
            costly.GetRegion(0).ExchangeExpression < cheap.GetRegion(0).ExchangeExpression;

        DevelopingBody prey = FullBody(cheapGenome, 0.0, substrate: 0.20);
        double structuralBefore = prey.Cache.TotalMatter;
        double extracted = prey.ExtractEdibleSubstrate(0.08);
        bool reserveOnly = Near(extracted, 0.08) && Near(prey.Cache.TotalMatter, structuralBefore) &&
            Near(prey.TotalSubstrate, 0.12);
        double maxResidual = Math.Max(organicResidual, detritusResidual);
        bool passed = mineralCannotFuel && needsMineral && storesOrganic && bothRequired && paidDigestion &&
            zeroEnergy && storedOrganicRestarts && decompositionConserved && noDecomposerBenefit && continuous &&
            richFoodSustains && costs &&
            reserveOnly && maxResidual <= 1e-12;
        return new(mineralCannotFuel, needsMineral, storesOrganic, bothRequired, paidDigestion,
            zeroEnergy, storedOrganicRestarts, decompositionConserved, noDecomposerBenefit,
            continuous, richFoodSustains, fed.Initial, fed.Midpoint, fed.Final, costs, reserveOnly,
            maxResidual, passed);
    }

    private static RichFoodTrajectory RichFoodTrajectoryAfter(
        Genome genome, SimulationConfig config, bool fed)
    {
        // Begin below the regulatory target, with enough stored organic matter to
        // restart respiration. This prevents the initial energy grant from making
        // a continuously loss-making consumer look sustainable.
        DevelopingBody body = FullBody(genome, 0.0, substrate: 0.20, oxygen: 20.0);
        FixedEnvironment environment = new(0.0);
        const double dt = 0.1;
        double initial = ChemicalReserve(body);
        double midpoint = initial;
        for (int step = 0; step < 1_200; step++)
        {
            if (fed)
            {
                double offered = RegionalPhysiology.EstimateOrganicDemand(body, genome, config, dt);
                RegionalPhysiology.ExchangeWithEnvironment(body, genome, environment,
                    Vector2.Zero, 0.0, 0.2f, config, dt,
                    matterDemand: offered,
                    organicReservation: new OrganicReservation(1, 0, 0.0, offered, 0.0));
            }
            else
                RegionalPhysiology.ExchangeWithEnvironment(body, genome, environment,
                    Vector2.Zero, 0.0, 0.2f, config, dt);
            RegionalPhysiology.ReactAndMaintain(body, genome, environment,
                new Vector2(config.WorldSize * 0.5f), config, dt);
            if (step == 599) midpoint = ChemicalReserve(body);
        }
        return new RichFoodTrajectory(initial, midpoint, ChemicalReserve(body));
    }

    private static double ChemicalReserve(DevelopingBody body) => body.TotalEnergy +
        (body.TotalSubstrate * BilinearEnvironmentField.FoodWebEnergyPerMatter);

    private static ExchangeProbe ProbeExchange(
        RegionGene gene, SimulationConfig config, double light, double minerals)
    {
        Genome genome = new([gene], 0.0);
        DevelopingBody body = FullBody(genome, 1.0);
        double energyBefore = body.TotalEnergy, substrateBefore = body.TotalSubstrate;
        RegionalExchangeResult result = RegionalPhysiology.ExchangeWithEnvironment(
            body, genome, new FixedEnvironment(light), new Vector2(config.WorldSize * 0.5f),
            0.0, 0.0f, config, 1.0, lightEnergyAllowance: double.PositiveInfinity,
            matterDemand: 0.0, mineralReservation: new MineralReservation(1, 0, minerals));
        return new(result, energyBefore, body.TotalEnergy, substrateBefore, body.TotalSubstrate);
    }

    private static DevelopingBody FullBody(
        Genome genome, double energy, double substrate = 0.0, double oxygen = 0.0)
    {
        double matter = BodyCalculator.TargetMatter(genome.Regions[0]);
        return new DevelopingBody(genome, matter, substrate, energy, oxygen, matter,
            CorePrecisionProfile.Reference);
    }

    private static RegionGene TestGene(
        double photo = 0.0, double feed = 0.0, double digest = 0.0, double decompose = 0.0) =>
        Genome.CreateAncestor().Regions[0] with
        {
            Permeability = 0.70,
            CatalyticActivity = 0.70,
            ExchangeExpression = 0.0,
            BarrierExpression = 0.0,
            ContractileExpression = 0.0,
            StructuralExpression = 0.0,
            SensoryExpression = 0.0,
            PhotosyntheticExpression = photo,
            FeedingExpression = feed,
            DigestiveExpression = digest,
            DecomposerExpression = decompose
        };

    private static bool NearZero(double value) => Math.Abs(value) <= 1e-12;
    private static bool Near(double left, double right) => Math.Abs(left - right) <= 1e-10;

    private readonly record struct ExchangeProbe(
        RegionalExchangeResult Result, double EnergyBefore, double EnergyAfter,
        double SubstrateBefore, double SubstrateAfter);

    private readonly record struct RichFoodTrajectory(
        double Initial, double Midpoint, double Final);

    private sealed class FixedEnvironment(double light) : IMutableEnvironmentField
    {
        public EnvironmentSample Sample(Vector2 position, float depth = 0f) => new(
            0.0, 1.0, 1.0, depth, 1.0, 0.80, light, 0.0, 0.0, 0.0,
            0.70, 1.0, 1.0, Vector2.Zero);
        public double WithdrawMatter(Vector2 position, double requestedAmount) => 0.0;
        public IReadOnlyDictionary<ulong,MatterReservation> ReserveMatter(
            IEnumerable<MatterUptakeRequest> requests) => new Dictionary<ulong,MatterReservation>();
        public void ReturnMatter(MatterReservation reservation,double unusedAmount) { }
        public void DepositDetritus(Vector2 position,double amount) { }
        public void DepositMetabolicWaste(Vector2 position,double amount) { }
        public double WithdrawOxygen(Vector2 position,float depth,double immersion,double requestedAmount)=>requestedAmount;
        public void DepositOxygen(Vector2 position,float depth,double immersion,double amount) { }
        public void UpdateOxygen(double deltaSeconds) { }
        public void UpdateMatterCycles(double deltaSeconds) { }
        public IReadOnlyDictionary<ulong,double> AllocateLightEnergy(
            IEnumerable<LightEnergyRequest> requests,double deltaSeconds)=>
            requests.ToDictionary(request=>request.OrganismId,request=>request.RequestedEnergy);
        public double TotalMinerals=>0.0;
        public double TotalDetritus=>0.0;
        public double TotalMetabolicWaste=>0.0;
        public double TotalOxygen=>0.0;
        public double CumulativeExternalOxygenSupply=>0.0;
        public bool AllFinite=>true;
        public double ApplyBrush(EnvironmentBrushCommand command)=>0.0;
    }
}
