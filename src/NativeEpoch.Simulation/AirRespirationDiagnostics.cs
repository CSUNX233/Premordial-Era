using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct AirRespirationDiagnosticResult(
    double WaterControlDamage,
    double WaterControlOxygenUptake,
    double NoAirExchangeDamage,
    double NoAirExchangeOxygenUptake,
    double NoAirExchangeLethalSeconds,
    double AirExchangeDamage,
    double AirExchangeOxygenUptake,
    double DamageBeforeWaterRecovery,
    double DamageAfterWaterRecovery,
    double SubstrateFreeHypoxia,
    double SubstrateFreeOxygenConsumed,
    double OxygenRichSubstrateFreeDamage,
    double OxygenRichSubstrateFreeHypoxia,
    bool AquaticAncestorHasNoAirExchange,
    bool AirTraitChangesFingerprint,
    bool CavityRoutePassed,
    double MaximumOxygenConservationError,
    bool Passed);

/// <summary>
/// Bounded fixtures for medium-specific exchange and oxygen-dependent damage.
/// They call the production exchange, transport, metabolism, and cavity APIs.
/// </summary>
public static class AirRespirationDiagnostics
{
    private const double DeltaSeconds = 0.1;

    public static AirRespirationDiagnosticResult Run()
    {
        SimulationConfig config = new()
        {
            WorldSize = 32,
            EnvironmentGridSize = 8,
            FixedDeltaSeconds = DeltaSeconds
        };
        Genome aquatic = CreateGenome(0.0);
        Genome airBreathing = CreateGenome(0.90);
        DevelopingBody equilibrated = CreateBody(aquatic, oxygen: 0.0, substrate: 1.0, energy: 0.25);
        Trial water = Run(equilibrated, aquatic, config, inWater: true, seconds: 30.0);

        BodyRegion[] sharedState = water.Body.Regions.ToArray();
        Trial noAir = Run(new DevelopingBody(aquatic, sharedState), aquatic,
            config, inWater: false, seconds: 90.0);
        Trial air = Run(WithAirPhenotype(sharedState, airBreathing), airBreathing,
            config, inWater: false, seconds: 70.0);

        DevelopingBody recoveryBody = new(aquatic, sharedState);
        Trial deprived = Run(recoveryBody, aquatic, config, inWater: false, seconds: 55.0);
        double damageBeforeRecovery = deprived.Body.AverageDamage;
        Trial recovered = Run(deprived.Body, aquatic, config, inWater: true, seconds: 20.0);

        DevelopingBody substrateFree = CreateBody(aquatic, oxygen: 0.0, substrate: 0.0, energy: 2.0);
        LedgerEnvironment substrateFreeEnvironment = new(inWater: false);
        RegionalMetabolismResult substrateFreeResult = RegionalPhysiology.ReactAndMaintain(
            substrateFree, aquatic, substrateFreeEnvironment, Vector2.Zero, config, 10.0);
        DevelopingBody oxygenRichSubstrateFree = CreateBody(
            aquatic, oxygen: 0.2, substrate: 0.0, energy: 2.0);
        RegionalMetabolismResult oxygenRichSubstrateFreeResult = RegionalPhysiology.ReactAndMaintain(
            oxygenRichSubstrateFree, aquatic, substrateFreeEnvironment, Vector2.Zero, config, 10.0);

        CavityPhysiologyDiagnosticResult cavity = CavityPhysiologyDiagnostics.Run();
        bool ancestorHasNoAir = Genome.CreateAncestor().Regions.All(
            gene => gene.AirExchangeAffinity == 0.0);
        bool fingerprintChanged = aquatic.Fingerprint != airBreathing.Fingerprint;
        double maximumConservationError = new[]
        {
            water.OxygenConservationError, noAir.OxygenConservationError,
            air.OxygenConservationError, deprived.OxygenConservationError,
            recovered.OxygenConservationError
        }.Max();
        bool passed = water.Body.AverageDamage < 0.02 && water.OxygenUptake > 1e-5 &&
            noAir.Body.AverageDamage > 0.25 && noAir.OxygenUptake < 1e-12 &&
            noAir.LethalAtSeconds is >= 30.0 and <= 90.0 &&
            air.Body.AverageDamage < noAir.Body.AverageDamage * 0.25 &&
            air.OxygenUptake > 1e-4 &&
            damageBeforeRecovery > water.Body.AverageDamage + 0.02 &&
            recovered.Body.AverageDamage <= damageBeforeRecovery + 1e-6 &&
            substrateFreeResult.HypoxiaShortfall > 0.0 &&
            substrateFreeResult.OxygenConsumed < 1e-12 &&
            oxygenRichSubstrateFree.AverageDamage > 0.0 &&
            oxygenRichSubstrateFreeResult.HypoxiaShortfall < 1e-12 &&
            oxygenRichSubstrateFreeResult.OxygenConsumed < 1e-12 &&
            ancestorHasNoAir && fingerprintChanged && cavity.Passed &&
            maximumConservationError < 1e-9;
        return new(water.Body.AverageDamage, water.OxygenUptake,
            noAir.Body.AverageDamage, noAir.OxygenUptake,
            noAir.LethalAtSeconds,
            air.Body.AverageDamage, air.OxygenUptake,
            damageBeforeRecovery, recovered.Body.AverageDamage,
            substrateFreeResult.HypoxiaShortfall, substrateFreeResult.OxygenConsumed,
            oxygenRichSubstrateFree.AverageDamage,
            oxygenRichSubstrateFreeResult.HypoxiaShortfall,
            ancestorHasNoAir, fingerprintChanged, cavity.Passed,
            maximumConservationError, passed);
    }

    private static Trial Run(DevelopingBody body, Genome genome, SimulationConfig config,
        bool inWater, double seconds)
    {
        LedgerEnvironment environment = new(inWater);
        double oxygenBefore = environment.TotalOxygen + body.TotalOxygen;
        double consumed = 0.0;
        double uptake = 0.0;
        double lethalAtSeconds = -1.0;
        int steps = (int)Math.Round(seconds / DeltaSeconds);
        for (int step = 0; step < steps; step++)
        {
            RegionalExchangeResult exchange = RegionalPhysiology.ExchangeWithEnvironment(
                body, genome, environment, new Vector2(16, 16), 0.0,
                inWater ? 1.0f : 0.0f, config, DeltaSeconds,
                lightEnergyAllowance: 0.0, matterDemand: 0.0);
            RegionalPhysiology.TransportAlongMatterEdges(body, genome, config, DeltaSeconds);
            RegionalMetabolismResult metabolism = RegionalPhysiology.ReactAndMaintain(
                body, genome, environment, new Vector2(16, 16), config, DeltaSeconds);
            uptake += exchange.OxygenUptake;
            consumed += metabolism.OxygenConsumed;
            if (lethalAtSeconds < 0.0 && body.AverageDamage >= 1.0)
                lethalAtSeconds = (step + 1) * DeltaSeconds;
        }
        double oxygenAfter = environment.TotalOxygen + body.TotalOxygen + consumed;
        return new(body, uptake, Math.Abs(oxygenAfter - oxygenBefore), lethalAtSeconds);
    }

    private static Genome CreateGenome(double airAffinity)
    {
        Genome ancestor = Genome.CreateAncestor();
        RegionGene core = ancestor.Regions.Single(gene => gene.IsCore) with
        {
            ExchangeExpression = 0.82,
            AirExchangeAffinity = airAffinity,
            CatalyticActivity = 0.58,
            StorageFraction = 0.75,
            PhotosyntheticExpression = 0.0,
            FeedingExpression = 0.0,
            DigestiveExpression = 0.0,
            DecomposerExpression = 0.0
        };
        return new Genome([core], 0.0,
            ancestor.Metabolism with { OxygenUseFraction = 0.42 },
            controllerNodes: [], sensors: []);
    }

    private static DevelopingBody CreateBody(Genome genome, double oxygen,
        double substrate, double energy)
    {
        RegionGene gene = genome.Regions.Single();
        BodyRegion state = new(gene.RegionId, BodyCalculator.TargetMatter(gene), 1.0,
            Substrate: substrate, Oxygen: oxygen, Water: 1.0, Energy: energy,
            TransportAvailability: 1.0, Activation: 0.5,
            ExchangeExpression: gene.ExchangeExpression,
            BarrierExpression: gene.BarrierExpression,
            ContractileExpression: gene.ContractileExpression,
            StructuralExpression: gene.StructuralExpression,
            SensoryExpression: gene.SensoryExpression,
            AirExchangeExpression: gene.AirExchangeAffinity);
        return new DevelopingBody(genome, [state]);
    }

    private static DevelopingBody WithAirPhenotype(
        IReadOnlyList<BodyRegion> source, Genome genome) =>
        new(genome, source.Select(region => region with
        {
            AirExchangeExpression = genome.GetRegion(region.RegionId).AirExchangeAffinity
        }));

    private readonly record struct Trial(
        DevelopingBody Body,
        double OxygenUptake,
        double OxygenConservationError,
        double LethalAtSeconds);

    private sealed class LedgerEnvironment(bool inWater) : IMutableEnvironmentField
    {
        private double _waterOxygen = 1000.0;
        private double _airOxygen = 1000.0;

        public EnvironmentSample Sample(Vector2 position, float depth = 0.0f) => inWater
            ? new(-8.0, 0.0, 8.0, depth, 1.0, 0.7, 0.0, 0.0, 0.0, 0.0,
                1.0, 0.72, 1.0, Vector2.Zero)
            : new(0.0, 0.0, 0.0, 0.0, 1.0, 0.7, 0.0, 0.0, 0.0, 0.0,
                1.0, 0.72, 1.0, Vector2.Zero);

        public double WithdrawOxygen(Vector2 position, float depth, double immersion,
            double requestedAmount)
        {
            ref double inventory = ref immersion >= 0.5 ? ref _waterOxygen : ref _airOxygen;
            double taken = Math.Min(inventory, Math.Max(0.0, requestedAmount));
            inventory -= taken;
            return taken;
        }

        public void DepositOxygen(Vector2 position, float depth, double immersion, double amount)
        {
            if (immersion >= 0.5) _waterOxygen += amount;
            else _airOxygen += amount;
        }

        public double WithdrawMatter(Vector2 position, double requestedAmount) => 0.0;
        public IReadOnlyDictionary<ulong, MatterReservation> ReserveMatter(
            IEnumerable<MatterUptakeRequest> requests) => new Dictionary<ulong, MatterReservation>();
        public void ReturnMatter(MatterReservation reservation, double unusedAmount) { }
        public void DepositDetritus(Vector2 position, double amount) { }
        public void DepositMetabolicWaste(Vector2 position, double amount) { }
        public void UpdateOxygen(double deltaSeconds) { }
        public void UpdateMatterCycles(double deltaSeconds) { }
        public IReadOnlyDictionary<ulong, double> AllocateLightEnergy(
            IEnumerable<LightEnergyRequest> requests, double deltaSeconds) =>
            new Dictionary<ulong, double>();
        public double TotalMinerals => 0.0;
        public double TotalDetritus => 0.0;
        public double TotalMetabolicWaste => 0.0;
        public double TotalOxygen => _waterOxygen + _airOxygen;
        public double CumulativeExternalOxygenSupply => 0.0;
        public bool AllFinite => double.IsFinite(_waterOxygen) && double.IsFinite(_airOxygen);
        public double ApplyBrush(EnvironmentBrushCommand command) => 0.0;
    }
}
