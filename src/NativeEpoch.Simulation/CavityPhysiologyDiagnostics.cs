using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct CavityPhysiologyDiagnosticResult(
    double AirReplenishment,
    double SealedInventoryChange,
    double SubmergedInventoryConsumed,
    double SubmergedEnvironmentalUptake,
    double DisconnectedTissueTransfer,
    double PoweredVentilation,
    double UnpoweredVentilation,
    double CapacityShrinkRelease,
    double DeathRelease,
    double MaximumConservationError,
    bool Passed);

/// <summary>
/// Small positive/negative fixtures for the cavity ledger.  These directly
/// exercise the formal module API and do not run an ecology simulation.
/// </summary>
public static class CavityPhysiologyDiagnostics
{
    private const double DeltaSeconds = 0.05;

    public static CavityPhysiologyDiagnosticResult Run()
    {
        Trial air = RunAirTrial(energy: 8.0, steps: 24);
        Trial unpowered = RunAirTrial(energy: 0.0, steps: 24);
        Trial sealedTrial = RunSealedTrial();
        Trial submerged = RunSubmergedConsumptionTrial();
        Trial disconnected = RunDisconnectedTrial();
        Trial shrink = RunCapacityShrinkTrial();
        Trial death = RunDeathReleaseTrial();
        double maximumError = new[]
        {
            air.ConservationError, unpowered.ConservationError,
            sealedTrial.ConservationError, submerged.ConservationError,
            disconnected.ConservationError, shrink.ConservationError,
            death.ConservationError
        }.Max();
        bool passed = air.EnvironmentUptake > 1e-5 &&
            Math.Abs(sealedTrial.CavityChange) < 1e-12 &&
            submerged.CavityChange < -1e-4 &&
            submerged.EnvironmentUptake < 1e-12 &&
            Math.Abs(disconnected.TissueTransfer) < 1e-12 &&
            air.EnvironmentUptake > unpowered.EnvironmentUptake * 1.25 + 1e-7 &&
            shrink.EnvironmentRelease > 1e-5 && death.EnvironmentRelease > 1e-5 &&
            maximumError < 1e-9;
        return new(air.EnvironmentUptake, sealedTrial.CavityChange,
            -submerged.CavityChange, submerged.EnvironmentUptake,
            disconnected.TissueTransfer, air.EnvironmentUptake,
            unpowered.EnvironmentUptake, shrink.EnvironmentRelease,
            death.EnvironmentRelease, maximumError, passed);
    }

    private static Trial RunAirTrial(double energy, int steps)
    {
        Fixture fixture = CreateFixture(energy, tissueOxygen: 0.0, transportAvailability: 0.0);
        CavityRegionInput input = fixture.Input(transportConnected: false,
            new CavityApertureContact(fixture.Position, 0.0f, true, 1.0, 0.0));
        return Run(fixture, input, steps);
    }

    private static Trial RunSealedTrial()
    {
        Fixture fixture = CreateFixture(energy: 4.0, tissueOxygen: 0.0, transportAvailability: 0.0);
        CavityRegionInput input = fixture.Input(transportConnected: false,
            new CavityApertureContact(fixture.Position, 0.0f, false, 0.0, 0.0));
        Seed(fixture, input, 0.72);
        return Run(fixture, input, 60);
    }

    private static Trial RunSubmergedConsumptionTrial()
    {
        Fixture fixture = CreateFixture(energy: 5.0, tissueOxygen: 0.0, transportAvailability: 1.0);
        CavityExpression sealedExpression = fixture.Expression with { CavityAperture = 0.0 };
        CavityRegionInput input = fixture.Input(transportConnected: true,
            new CavityApertureContact(fixture.Position, 1.0f, true, 0.0, 1.0), sealedExpression);
        Seed(fixture, input, 0.92);
        double environmentBefore = fixture.Environment.TotalOxygen;
        double cavityBefore = fixture.State.TotalOxygen;
        double tissueBefore = fixture.Body.TotalOxygen;
        double metabolicConsumption = 0.0;
        CavityStepResult total = default;
        for (int step = 0; step < 140; step++)
        {
            total += CavityPhysiology.StepRegion(fixture.State, fixture.Body,
                fixture.Environment, input, DeltaSeconds);
            // Existing metabolism is the terminal sink from tissue. Record it
            // separately in the ledger because this fixture isolates cavity flow.
            double consumed = Math.Min(fixture.Body.GetRegion(fixture.RegionId).Oxygen, 0.00055);
            fixture.Body.ApplyInventoryDelta(fixture.RegionId,
                new RegionalInventoryDelta(0.0, -consumed, 0.0, 0.0));
            metabolicConsumption += consumed;
        }
        double oxygenBefore = environmentBefore + cavityBefore + tissueBefore;
        double oxygenAfter = fixture.Environment.TotalOxygen + fixture.State.TotalOxygen +
            fixture.Body.TotalOxygen + metabolicConsumption;
        return new(total.OxygenTakenFromEnvironment, total.OxygenReturnedToEnvironment,
            fixture.State.TotalOxygen - cavityBefore,
            fixture.Body.TotalOxygen - tissueBefore,
            Math.Abs(oxygenAfter - oxygenBefore));
    }

    private static Trial RunDisconnectedTrial()
    {
        Fixture fixture = CreateFixture(energy: 4.0, tissueOxygen: 0.0, transportAvailability: 1.0);
        CavityRegionInput input = fixture.Input(transportConnected: false,
            new CavityApertureContact(fixture.Position, 0.0f, false, 0.0, 0.0));
        Seed(fixture, input, 0.85);
        return Run(fixture, input, 80);
    }

    private static Trial RunCapacityShrinkTrial()
    {
        Fixture fixture = CreateFixture(energy: 3.0, tissueOxygen: 0.0, transportAvailability: 0.0);
        CavityApertureContact closed = new(fixture.Position, 0.0f, false, 0.0, 0.0);
        CavityRegionInput large = fixture.Input(transportConnected: false, closed);
        Seed(fixture, large, 0.90);
        CavityRegionInput small = large with { RegionAnalyticVolume = large.RegionAnalyticVolume * 0.22 };
        return Run(fixture, small, 1);
    }

    private static Trial RunDeathReleaseTrial()
    {
        Fixture fixture = CreateFixture(energy: 3.0, tissueOxygen: 0.0, transportAvailability: 0.0);
        CavityRegionInput input = fixture.Input(transportConnected: false,
            new CavityApertureContact(fixture.Position, 0.0f, false, 0.0, 0.0));
        Seed(fixture, input, 0.66);
        double environmentBefore = fixture.Environment.TotalOxygen;
        double cavityBefore = fixture.State.TotalOxygen;
        double released = CavityPhysiology.ReleaseAll(fixture.State, fixture.Environment,
            fixture.Position, 0.0f, 0.0);
        double error = Math.Abs((fixture.Environment.TotalOxygen - environmentBefore) - cavityBefore);
        return new(0.0, released, -cavityBefore, 0.0, error);
    }

    private static Trial Run(Fixture fixture, CavityRegionInput input, int steps)
    {
        double environmentBefore = fixture.Environment.TotalOxygen;
        double cavityBefore = fixture.State.TotalOxygen;
        double tissueBefore = fixture.Body.TotalOxygen;
        CavityStepResult total = default;
        for (int step = 0; step < steps; step++)
            total += CavityPhysiology.StepRegion(fixture.State, fixture.Body,
                fixture.Environment, input, DeltaSeconds);
        double oxygenBefore = environmentBefore + cavityBefore + tissueBefore;
        double oxygenAfter = fixture.Environment.TotalOxygen + fixture.State.TotalOxygen +
            fixture.Body.TotalOxygen;
        return new(total.OxygenTakenFromEnvironment, total.OxygenReturnedToEnvironment,
            fixture.State.TotalOxygen - cavityBefore,
            fixture.Body.TotalOxygen - tissueBefore,
            Math.Abs(oxygenAfter - oxygenBefore));
    }

    private static void Seed(Fixture fixture, CavityRegionInput input, double capacityFraction)
    {
        CavityPhysiology.StepRegion(fixture.State, fixture.Body, fixture.Environment,
            input, 0.0);
        CavityRegionState empty = fixture.State.GetRegion(fixture.RegionId);
        fixture.State = new CavitySystemState([
            empty with { Oxygen = empty.OxygenCapacity * capacityFraction }
        ]);
    }

    private static Fixture CreateFixture(double energy, double tissueOxygen,
        double transportAvailability)
    {
        SimulationConfig config = new() { WorldSize = 24, EnvironmentGridSize = 8 };
        Genome genome = Genome.CreateAncestor();
        int regionId = genome.Regions.Single(gene => gene.IsCore).RegionId;
        BodyRegion[] regions = genome.Regions.Select(gene => new BodyRegion(
            gene.RegionId, BodyCalculator.TargetMatter(gene), 1.0,
            Oxygen: gene.RegionId == regionId ? tissueOxygen : 0.0,
            Energy: gene.RegionId == regionId ? energy : 0.0,
            TransportAvailability: gene.RegionId == regionId ? transportAvailability : 0.0,
            Activation: gene.RegionId == regionId ? 1.0 : 0.0)).ToArray();
        DevelopingBody body = new(genome, regions);
        BilinearEnvironmentField environment = new(config, new DeterministicRandom(38191, 7));
        double volume = body.Geometry.Regions.Single(region => region.RegionId == regionId).AnalyticVolume;
        CavityExpression expression = new(0.88, 0.72, 0.90, 0.94, 0.95, 0.92);
        return new(body, environment, new CavitySystemState(), regionId,
            new Vector2(12.0f, 12.0f), volume, expression, config);
    }

    private sealed class Fixture(
        DevelopingBody body,
        BilinearEnvironmentField environment,
        CavitySystemState state,
        int regionId,
        Vector2 position,
        double volume,
        CavityExpression expression,
        SimulationConfig config)
    {
        public DevelopingBody Body { get; } = body;
        public BilinearEnvironmentField Environment { get; } = environment;
        public CavitySystemState State { get; set; } = state;
        public int RegionId { get; } = regionId;
        public Vector2 Position { get; } = position;
        public CavityExpression Expression { get; } = expression;

        public CavityRegionInput Input(bool transportConnected, CavityApertureContact contact,
            CavityExpression? overrideExpression = null) => new(
                RegionId, volume, RegionalPhysiology.OxygenCapacity(Body.GetRegion(RegionId), config),
                transportConnected, contact.WaterExposure, overrideExpression ?? Expression, contact);
    }

    private readonly record struct Trial(
        double EnvironmentUptake,
        double EnvironmentRelease,
        double CavityChange,
        double TissueTransfer,
        double ConservationError);
}
