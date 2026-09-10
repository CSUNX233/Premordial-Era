using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct LandResourceDiagnosticResult(
    int InlandSamples,
    int CoastalSamples,
    int DeepOceanSamples,
    double InlandMeanResource,
    double CoastalMeanResource,
    double DeepOceanMeanResource,
    double InitialFiniteMatter,
    double ExhaustedMatter,
    double SpontaneousRefill,
    double WasteCycleConservationError,
        double EqualRequestDifference,
    double ReversedOrderDifference,
    double ReservationConservationError,
    bool ReservationReplayRejected,
    bool CenterIsResourceRichLand,
    bool CenterRelocationRemainsOnLand,
    bool ThousandFounderBudgetConstructs,
    bool Passed);

/// <summary>
/// Bounded checks for finite terrain-bound resources, shoreline gradients,
/// reservation fairness, and absence of an implicit matter source.
/// </summary>
public static class LandResourceDiagnostics
{
    public static LandResourceDiagnosticResult Run()
    {
        SimulationConfig config = new()
        {
            WorldSize = 96,
            EnvironmentGridSize = 49,
            ResourceBudgetReferenceAncestors = 24
        };
        const ulong seed = 0x1A4D5EEDUL;
        BilinearEnvironmentField environment = new(config, new DeterministicRandom(seed, 1));
        ResourceBands bands = MeasureBands(environment, config);
        Vector2 center = new(config.WorldSize * 0.5f, config.WorldSize * 0.5f);
        EnvironmentSample centerSample = environment.Sample(center);
        bool centerRichLand = centerSample.TerrainHeight > 3.0 &&
            centerSample.Minerals + centerSample.Detritus > bands.CoastalMean * 2.0;

        double initial = environment.TotalMinerals + environment.TotalDetritus +
            environment.TotalMetabolicWaste;
        double withdrawn = ExhaustAtGridNodes(environment, config);
        double exhausted = environment.TotalMinerals + environment.TotalDetritus;
        for (int step = 0; step < 16; step++) environment.UpdateMatterCycles(0.1);
        double spontaneousRefill = environment.TotalMinerals + environment.TotalDetritus;

        environment.DepositMetabolicWaste(center, 1.0);
        double cycleBefore = environment.TotalMinerals + environment.TotalDetritus +
            environment.TotalMetabolicWaste;
        environment.UpdateMatterCycles(0.5);
        double cycleAfter = environment.TotalMinerals + environment.TotalDetritus +
            environment.TotalMetabolicWaste;
        double cycleError = Math.Abs(cycleAfter - cycleBefore);

        AllocationTrial allocation = RunAllocationTrial(config, seed + 1, center);
        bool centerRelocation = VerifyCenterRelocation(config, seed + 2, center);
        bool thousandFounderBudget = VerifyThousandFounderBudget(seed + 3);
        bool passed = bands.InlandCount > 0 && bands.CoastalCount > 0 && bands.DeepCount > 0 &&
            bands.InlandMean > bands.CoastalMean * 3.0 &&
            bands.CoastalMean > bands.DeepMean * 2.0 && centerRichLand &&
            initial > 0.0 && Math.Abs(initial - withdrawn) < 1e-8 &&
            exhausted < 1e-10 && spontaneousRefill < 1e-10 && cycleError < 1e-12 &&
            allocation.EqualDifference < 1e-12 && allocation.ReversedDifference < 1e-12 &&
            allocation.ConservationError < 1e-12 && allocation.ReplayRejected && centerRelocation &&
            thousandFounderBudget;

        return new(bands.InlandCount, bands.CoastalCount, bands.DeepCount,
            bands.InlandMean, bands.CoastalMean, bands.DeepMean, initial, exhausted,
            spontaneousRefill, cycleError, allocation.EqualDifference,
            allocation.ReversedDifference, allocation.ConservationError, allocation.ReplayRejected,
            centerRichLand, centerRelocation, thousandFounderBudget, passed);
    }

    private static ResourceBands MeasureBands(
        BilinearEnvironmentField environment, SimulationConfig config)
    {
        double spacing = config.WorldSize / (config.EnvironmentGridSize - 1);
        double inland = 0.0, coastal = 0.0, deep = 0.0;
        int inlandCount = 0, coastalCount = 0, deepCount = 0;
        for (int y = 0; y < config.EnvironmentGridSize; y++)
        for (int x = 0; x < config.EnvironmentGridSize; x++)
        {
            EnvironmentSample sample = environment.Sample(
                new Vector2((float)(x * spacing), (float)(y * spacing)));
            double resource = sample.Minerals + sample.Detritus;
            if (sample.TerrainHeight >= 3.0)
            {
                inland += resource;
                inlandCount++;
            }
            else if (sample.WaterDepth is > 0.0 and <= 3.0)
            {
                coastal += resource;
                coastalCount++;
            }
            else if (sample.WaterDepth >= 20.0)
            {
                deep += resource;
                deepCount++;
            }
        }
        return new(inlandCount, coastalCount, deepCount,
            inlandCount > 0 ? inland / inlandCount : 0.0,
            coastalCount > 0 ? coastal / coastalCount : 0.0,
            deepCount > 0 ? deep / deepCount : 0.0);
    }

    private static double ExhaustAtGridNodes(
        BilinearEnvironmentField environment, SimulationConfig config)
    {
        double spacing = config.WorldSize / (config.EnvironmentGridSize - 1);
        double withdrawn = 0.0;
        for (int y = 0; y < config.EnvironmentGridSize; y++)
        for (int x = 0; x < config.EnvironmentGridSize; x++)
            withdrawn += environment.WithdrawMatter(
                new Vector2((float)(x * spacing), (float)(y * spacing)), double.MaxValue);
        return withdrawn;
    }

    private static AllocationTrial RunAllocationTrial(
        SimulationConfig config, ulong seed, Vector2 position)
    {
        MatterUptakeRequest first = new(1, position, 10.0);
        MatterUptakeRequest second = new(2, position, 10.0);
        BilinearEnvironmentField forward = new(config, new DeterministicRandom(seed, 2));
        double before = forward.TotalMinerals + forward.TotalDetritus;
        IReadOnlyDictionary<ulong, MatterReservation> a = forward.ReserveMatter([first, second]);
        double equal = Math.Abs(a[1].Total - a[2].Total);
        foreach (MatterReservation reservation in a.Values)
            forward.ReturnMatter(reservation, reservation.Total);
        double conservation = Math.Abs(before - (forward.TotalMinerals + forward.TotalDetritus));
        bool replayRejected = false;
        try { forward.ReturnMatter(a[1], a[1].Total); }
        catch (InvalidOperationException) { replayRejected = true; }

        BilinearEnvironmentField reverse = new(config, new DeterministicRandom(seed, 2));
        IReadOnlyDictionary<ulong, MatterReservation> b = reverse.ReserveMatter([second, first]);
        double reversed = Math.Max(Math.Abs(a[1].Total - b[1].Total),
            Math.Abs(a[2].Total - b[2].Total));
        return new(equal, reversed, conservation, replayRejected);
    }

    private static bool VerifyCenterRelocation(
        SimulationConfig config, ulong seed, Vector2 center)
    {
        SimulationWorld world = new(config, seed, 1, mutationsEnabled: false);
        ulong id = world.Organisms[0].Id;
        if (!world.RelocateForMediumDiagnostic(id, center, 0.0f, 0.0)) return false;
        world.Step();
        if (!world.TryGetOrganism(id, out Organism organism)) return false;
        EnvironmentSample sample = world.Environment.Sample(organism.Position, organism.Depth);
        return sample.TerrainHeight > 0.0 && sample.WaterDepth == 0.0 &&
            Vector2.Distance(center, organism.Position) < 1.0f;
    }

    private static bool VerifyThousandFounderBudget(ulong seed)
    {
        SimulationConfig config = new()
        {
            ResourceBudgetReferenceAncestors = 24,
            RandomizeFounders = false
        };
        SimulationWorld world = new(config, seed, 1_000, mutationsEnabled: false);
        SimulationSnapshot snapshot = world.CaptureSnapshot();
        return snapshot.Population == 1_000 && snapshot.EnvironmentMinerals > 0.0 &&
            snapshot.AllFinite && Math.Abs(snapshot.MatterError) < 1e-10;
    }

    private readonly record struct ResourceBands(
        int InlandCount, int CoastalCount, int DeepCount,
        double InlandMean, double CoastalMean, double DeepMean);
    private readonly record struct AllocationTrial(
        double EqualDifference, double ReversedDifference, double ConservationError,
        bool ReplayRejected);
}
