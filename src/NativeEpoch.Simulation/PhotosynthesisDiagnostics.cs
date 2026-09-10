using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct PhotosynthesisDiagnosticResult(
    bool ZeroExpressionStrongLightNoOrganicStorage,
    bool SensoryMaterialCannotSynthesizeOrganic,
    bool IndependentPigmentStoresOrganicChemicalEnergy,
    bool ContinuousExpressionBenefitAndCost,
    bool OcclusionReducesProduction,
    bool SurfaceDirectionChangesProduction,
    bool LocalLightChangesProduction,
    bool ExpressionBudgetIsShared,
    bool ConstructionAndMaintenanceArePaid,
    double EnergyBalanceResidual,
    bool Passed);

/// <summary>Bounded checks for inherited, developed photosynthetic pigment and its local heat cost.</summary>
public static class PhotosynthesisDiagnostics
{
    public static PhotosynthesisDiagnosticResult Run()
    {
        SimulationConfig config = new();
        RegionalExchangeResult zero = Exchange(0.0, 1.0, 1.0, config);
        RegionalExchangeResult sensoryOnly = Exchange(0.0, 1.0, 1.0, config);
        RegionalExchangeResult independent = Exchange(0.75, 0.0, 1.0, config);
        RegionalExchangeResult low = Exchange(0.20, 0.0, 1.0, config);
        RegionalExchangeResult high = Exchange(0.80, 0.0, 1.0, config);
        RegionalExchangeResult occluded = Exchange(0.80, 0.0, 1.0, config,
            (_, sampleIndex, sampleCount) => sampleIndex % 2 == 0);
        RegionalExchangeResult upward = Exchange(0.80, 0.0, 1.0, config,
            (_, sampleIndex, sampleCount) => sampleIndex < sampleCount / 2);
        RegionalExchangeResult downward = Exchange(0.80, 0.0, 1.0, config,
            (_, sampleIndex, sampleCount) => sampleIndex >= sampleCount / 2);
        RegionalExchangeResult dim = Exchange(0.80, 0.0, 0.20, config);

        bool zeroGate = NearZero(zero.AbsorbedLight) && NearZero(zero.LightEnergy);
        bool sensoryGate = NearZero(sensoryOnly.AbsorbedLight) && NearZero(sensoryOnly.LightEnergy);
        bool independentGate = independent.AbsorbedLight > 0.0 && independent.LightEnergy > 0.0;
        bool continuous = high.AbsorbedLight > low.AbsorbedLight &&
            high.LightEnergy > low.LightEnergy &&
            high.PhotosyntheticHeat > low.PhotosyntheticHeat &&
            high.PhotosyntheticMaintenance > low.PhotosyntheticMaintenance;
        bool shade = occluded.ExposedSamples < high.ExposedSamples &&
            occluded.AbsorbedLight > 0.0 && occluded.AbsorbedLight < high.AbsorbedLight;
        bool direction = upward.AbsorbedLight > downward.AbsorbedLight;
        bool localLight = high.AbsorbedLight > dim.AbsorbedLight * 4.5;

        RegionGene zeroGene = TestGene(0.0, 0.0) with
        {
            ExchangeExpression = 0.80,
            BarrierExpression = 0.50,
            StructuralExpression = 0.55,
            SensoryExpression = 0.70
        };
        RegionGene costlyGene = zeroGene with { PhotosyntheticExpression = 0.90 };
        DevelopingBody zeroBody = FullBody(new Genome([zeroGene], 0.0));
        DevelopingBody costlyBody = FullBody(new Genome([costlyGene], 0.0));
        bool sharedBudget = costlyBody.GetRegion(0).ExchangeExpression <
            zeroBody.GetRegion(0).ExchangeExpression;
        bool paidCosts = BodyCalculator.TargetMatter(costlyGene) > BodyCalculator.TargetMatter(zeroGene) &&
            costlyBody.Cache.MaintenanceEnergyPerSecond > zeroBody.Cache.MaintenanceEnergyPerSecond &&
            costlyBody.Cache.PhotosyntheticSurface > 0.0;

        double residual = Math.Abs(high.AbsorbedLight -
            (high.LightEnergy + high.PhotosyntheticHeat));
        bool passed = zeroGate && sensoryGate && independentGate && continuous && shade && direction &&
            localLight && sharedBudget && paidCosts && residual <= 1e-10;
        return new(zeroGate, sensoryGate, independentGate, continuous, shade, direction,
            localLight, sharedBudget, paidCosts, residual, passed);
    }

    private static RegionalExchangeResult Exchange(
        double photosyntheticExpression,
        double lightReactivity,
        double localLight,
        SimulationConfig config,
        Func<int, int, int, bool>? enabled = null)
    {
        RegionGene gene = TestGene(photosyntheticExpression, lightReactivity);
        Genome genome = new([gene], 0.0);
        DevelopingBody body = FullBody(genome);
        BodyFunctionalGeometry geometry = body.FunctionalGeometry[0];
        return RegionalPhysiology.ExchangeWithEnvironment(
            body, genome, new FixedEnvironment(localLight),
            new Vector2(config.WorldSize * 0.5f), 0.0, 0.0f, config, 1.0,
            enabled is null ? null : (regionId, sampleIndex) =>
                enabled(regionId, sampleIndex, geometry.SurfaceSamples.Count),
            double.PositiveInfinity, matterDemand: 0.0,
            mineralReservation: new MineralReservation(1, 0, 10.0));
    }

    private static DevelopingBody FullBody(Genome genome)
    {
        double matter = BodyCalculator.TargetMatter(genome.Regions[0]);
        return new DevelopingBody(genome, matter, initialEnergy: 1.0, initialWater: matter,
            precision: CorePrecisionProfile.Reference);
    }

    private static RegionGene TestGene(double photosyntheticExpression, double lightReactivity) =>
        Genome.CreateAncestor().Regions[0] with
        {
            LightReactivity = lightReactivity,
            CatalyticActivity = 0.55,
            Permeability = 0.0,
            ExchangeExpression = 0.0,
            BarrierExpression = 0.0,
            ContractileExpression = 0.0,
            StructuralExpression = 0.0,
            SensoryExpression = 0.0,
            PhotosyntheticExpression = photosyntheticExpression,
            FeedingExpression = 0.0,
            DigestiveExpression = 0.0,
            DecomposerExpression = 0.0
        };

    private static bool NearZero(double value) => Math.Abs(value) <= 1e-12;

    private sealed class FixedEnvironment(double light) : IMutableEnvironmentField
    {
        public EnvironmentSample Sample(Vector2 position, float depth = 0f) => new(
            0.0, 0.0, 0.0, depth, 0.0, 0.80, light, 0.0, 0.0, 0.0,
            0.70, 0.0, 0.0, Vector2.Zero);

        public double WithdrawMatter(Vector2 position, double requestedAmount) => 0.0;
        public IReadOnlyDictionary<ulong, MatterReservation> ReserveMatter(
            IEnumerable<MatterUptakeRequest> requests) => new Dictionary<ulong, MatterReservation>();
        public void ReturnMatter(MatterReservation reservation, double unusedAmount) { }
        public void DepositDetritus(Vector2 position, double amount) { }
        public void DepositMetabolicWaste(Vector2 position, double amount) { }
        public double WithdrawOxygen(Vector2 position, float depth, double immersion,
            double requestedAmount) => 0.0;
        public void DepositOxygen(Vector2 position, float depth, double immersion, double amount) { }
        public void UpdateOxygen(double deltaSeconds) { }
        public void UpdateMatterCycles(double deltaSeconds) { }
        public IReadOnlyDictionary<ulong, double> AllocateLightEnergy(
            IEnumerable<LightEnergyRequest> requests, double deltaSeconds) =>
            requests.ToDictionary(request => request.OrganismId, request => request.RequestedEnergy);
        public double TotalMinerals => 0.0;
        public double TotalDetritus => 0.0;
        public double TotalMetabolicWaste => 0.0;
        public double TotalOxygen => 0.0;
        public double CumulativeExternalOxygenSupply => 0.0;
        public bool AllFinite => true;
        public double ApplyBrush(EnvironmentBrushCommand command) => 0.0;
    }
}
