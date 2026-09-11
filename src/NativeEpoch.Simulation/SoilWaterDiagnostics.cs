using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SoilWaterDiagnosticResult(
    double ContactUptake, double SuspendedUptake, double DisabledExchangeUptake,
    double DepletedUptake, double WaterBalanceError, bool FiniteCompetition, bool Passed);

public static class SoilWaterDiagnostics
{
    public static SoilWaterDiagnosticResult Run()
    {
        SimulationConfig config = new() { WorldSize = 128, EnvironmentGridSize = 32 };
        BilinearEnvironmentField MakeEnvironment() => new(config, new DeterministicRandom(20260908, 81));
        BilinearEnvironmentField wet = MakeEnvironment();
        EnvironmentResourceSnapshot resources = wet.CaptureResourceSnapshot();
        Vector2 position = default;
        bool found = false;
        for (int index = 0; index < resources.SoilWater.Length; index++)
        {
            if (resources.TerrainHeight[index] <= 0.1 || resources.SoilWaterCapacity[index] <= 0.0 ||
                resources.SoilWater[index] / resources.SoilWaterCapacity[index] < 0.85) continue;
            position = new Vector2((index % resources.GridSize) * resources.WorldSize / resources.GridSize,
                (index / resources.GridSize) * resources.WorldSize / (resources.GridSize - 1f));
            if (wet.Sample(position).SoilWaterAvailability > 0.80) { found = true; break; }
        }
        if (!found) throw new InvalidOperationException("Seeded wet-soil fixture has no wet land.");

        Genome ancestor = Genome.CreateAncestor();
        RegionGene core = ancestor.Regions.Single(gene => gene.IsCore) with
        { ExchangeExpression = 0.9, Permeability = 0.8, PhotosyntheticExpression = 0.0 };
        Genome genome = new([core], 0.0, ancestor.Metabolism, controllerNodes: [], sensors: []);
        DevelopingBody Body(double exchange) => new(genome, [new BodyRegion(core.RegionId,
            BodyCalculator.TargetMatter(core), 1.0, Water: 0.01, Energy: 1.0,
            ExchangeExpression: exchange, TransportAvailability: 1.0)]);

        (double Uptake, double Error) Trial(BilinearEnvironmentField environment,
            double exchange, bool contact)
        {
            DevelopingBody body = Body(exchange);
            double before = environment.TotalSoilWater + body.TotalWater;
            double uptake = 0.0, lost = 0.0;
            // Explicit planted-foot input uses the same exchange entry point as World;
            // raising the body without a contact must prevent absorption.
            SoilWaterContact[] contacts = [new(core.RegionId, position, 0.12)];
            for (int step = 0; step < 50; step++)
            {
                RegionalExchangeResult result = RegionalPhysiology.ExchangeWithEnvironment(body,
                    genome, environment, position, 0.0, 0f, config, 0.1,
                    lightEnergyAllowance: 0.0, matterDemand: 0.0,
                    bodyCenterElevationOverride: environment.Sample(position).TerrainHeight + 4.0,
                    soilWaterContacts: contact ? contacts : null);
                uptake += result.WaterUptake;
                lost += result.WaterLost;
            }
            return (uptake, Math.Abs(before - environment.TotalSoilWater - body.TotalWater - lost));
        }

        var touching = Trial(wet, 0.9, true);
        var suspended = Trial(MakeEnvironment(), 0.9, false);
        var disabled = Trial(MakeEnvironment(), 0.0, true);
        BilinearEnvironmentField depleted = MakeEnvironment();
        double initial = depleted.TotalSoilWater;
        double withdrawn = 0.0;
        for (int count = 0; count < 128; count++) withdrawn += depleted.WithdrawSoilWater(position, 1e6);
        double secondCompetitor = depleted.WithdrawSoilWater(position, 1e6);
        bool competition = secondCompetitor < 1e-8 && withdrawn > 0.0 &&
            Math.Abs(initial - depleted.TotalSoilWater - withdrawn - secondCompetitor) < 1e-8;
        var dry = Trial(depleted, 0.9, true);
        double error = Math.Max(Math.Max(touching.Error, suspended.Error), Math.Max(disabled.Error, dry.Error));
        bool passed = touching.Uptake > 1e-5 && suspended.Uptake < 1e-12 &&
            disabled.Uptake < 1e-12 && dry.Uptake < 1e-8 && competition && error < 1e-8 &&
            wet.AllFinite && depleted.AllFinite;
        return new(touching.Uptake, suspended.Uptake, disabled.Uptake, dry.Uptake, error, competition, passed);
    }
}
