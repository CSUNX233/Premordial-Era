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
    bool Passed)
{
    public bool FoundersAquatic { get; init; }
    public bool InlandBirthAllowed { get; init; }
    public bool LandNewbornIsJuvenile { get; init; }
    public double LandBirthMatterError { get; init; }
}

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
        Vector2 center = FindRichInlandPosition(environment.CaptureResourceSnapshot());
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
        bool centerRelocation = VerifyCenterRelocation(config, seed, center);
        bool thousandFounderBudget = VerifyThousandFounderBudget(seed + 3);
        var birth = VerifyInlandBirth();
        bool passed = bands.InlandCount > 0 && bands.CoastalCount > 0 && bands.DeepCount > 0 &&
            bands.InlandMean > bands.CoastalMean * 3.0 &&
            bands.CoastalMean > bands.DeepMean * 2.0 && centerRichLand &&
            initial > 0.0 && Math.Abs(initial - withdrawn) / initial < 1e-6 &&
            exhausted / initial < 1e-6 && spontaneousRefill <= exhausted + 1e-10 &&
            cycleError < 1e-12 &&
            allocation.EqualDifference < 1e-12 && allocation.ReversedDifference < 1e-12 &&
            allocation.ConservationError < 1e-12 && allocation.ReplayRejected && centerRelocation &&
            thousandFounderBudget && birth.Aquatic && birth.Born && birth.Juvenile && Math.Abs(birth.MatterError) < 1e-8;

        return new(bands.InlandCount, bands.CoastalCount, bands.DeepCount,
            bands.InlandMean, bands.CoastalMean, bands.DeepMean, initial, exhausted,
            spontaneousRefill, cycleError, allocation.EqualDifference,
            allocation.ReversedDifference, allocation.ConservationError, allocation.ReplayRejected,
            centerRichLand, centerRelocation, thousandFounderBudget, passed)
        {
            FoundersAquatic = birth.Aquatic, InlandBirthAllowed = birth.Born,
            LandNewbornIsJuvenile = birth.Juvenile, LandBirthMatterError = birth.MatterError
        };
    }

    private static (bool Aquatic, bool Born, bool Juvenile, double MatterError) VerifyInlandBirth()
    {
        // A supplied, accelerated parent isolates birthplace legality from
        // natural adaptation. Relocation is diagnostic only; birth uses Step.
        SimulationConfig config = new()
        {
            EnvironmentGridSize = 32, RandomizeFounders = false, MaxPopulation = 8,
            AncestorStoredMatter = 10, AncestorEnergy = 10, MaximumEnergy = 20,
            MaturityAgeSeconds = 1, GrowthMatterPerSecond = 4,
            ReproductionCooldownSeconds = 0.1, NewbornOffsetRadius = 0.2f
        };
        SimulationWorld world = new(config, 20260908, 1, mutationsEnabled: false);
        bool aquatic = world.Organisms.All(o => o.Generation == 0 && o.Immersion > 0.95 && o.Depth > 0);
        Vector2 inland = FindRichInlandPosition(world.CapturePresentationSnapshot().Resources!);
        world.RelocateForMediumDiagnostic(world.Organisms[0].Id, inland, 0);
        bool born = false, juvenile = false;
        for (int step = 0; step < 200 && !born && world.Organisms.Count > 0; step++)
        {
            world.Step();
            foreach (Organism child in world.Organisms)
            {
                if (child.Generation <= 0 || child.Depth != 0 || child.Immersion != 0) continue;
                born = true;
                juvenile = child.AgeSeconds == 0 && child.Maturity < 0.95 && child.ParentId != 0;
                break;
            }
        }
        return (aquatic, born, juvenile, world.CaptureSnapshot().MatterError);
    }

    private static ResourceBands MeasureBands(
        BilinearEnvironmentField environment, SimulationConfig config)
    {
        double inland = 0.0, coastal = 0.0, deep = 0.0;
        int inlandCount = 0, coastalCount = 0, deepCount = 0;
        EnvironmentResourceSnapshot snapshot = environment.CaptureResourceSnapshot();
        for (int y = 0; y < config.EnvironmentGridSize; y++)
        for (int x = 0; x < config.EnvironmentGridSize; x++)
        {
            int index = (y * config.EnvironmentGridSize) + x;
            double resource = (snapshot.Minerals[index] + snapshot.Detritus[index]) /
                snapshot.CellAreas[index];
            if (snapshot.TerrainHeight[index] >= 3.0)
            {
                inland += resource;
                inlandCount++;
            }
            else if (snapshot.WaterDepth[index] is > 0.0 and <= 3.0)
            {
                coastal += resource;
                coastalCount++;
            }
            else if (snapshot.WaterDepth[index] >= 20.0)
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
        double longitudeSpacing = config.WorldSize / config.EnvironmentGridSize;
        double colatitudeSpacing = config.WorldSize / (config.EnvironmentGridSize - 1);
        double withdrawn = 0.0;
        for (int y = 0; y < config.EnvironmentGridSize; y++)
        for (int x = 0; x < config.EnvironmentGridSize; x++)
            withdrawn += environment.WithdrawMatter(
                new Vector2((float)(x * longitudeSpacing), (float)(y * colatitudeSpacing)),
                double.MaxValue);
        return withdrawn;
    }

    private static Vector2 FindRichInlandPosition(EnvironmentResourceSnapshot snapshot)
    {
        int best = -1;
        double bestDensity = double.NegativeInfinity;
        for (int index = 0; index < snapshot.TerrainHeight.Length; index++)
        {
            if (snapshot.TerrainHeight[index] < 3.0) continue;
            double density = (snapshot.Minerals[index] + snapshot.Detritus[index]) /
                snapshot.CellAreas[index];
            if (density <= bestDensity) continue;
            best = index;
            bestDensity = density;
        }
        if (best < 0) throw new InvalidOperationException("Seeded globe has no inland diagnostic cell.");
        return new Vector2(
            (best % snapshot.GridSize) * snapshot.WorldSize / snapshot.GridSize,
            (best / snapshot.GridSize) * snapshot.WorldSize / (snapshot.GridSize - 1f));
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
            SphericalWorld.Distance(center, organism.Position, config.WorldSize) < 1.0;
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
