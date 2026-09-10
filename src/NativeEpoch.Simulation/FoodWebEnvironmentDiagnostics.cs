using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct FoodWebEnvironmentDiagnosticResult(
    double InitialMatter,
    double InitialLandPlants,
    double InitialAlgae,
    double PrimaryProduction,
    double ClaimedLightEnergy,
    double ProducerConservationError,
    double CycleConservationError,
    double StepConsistencyRelativeError,
    double FairOrganicDifference,
    double ReversedOrderDifference,
    double ReservationReturnError,
    bool MineralReservationConserved,
    bool DetritusReservationConserved,
    bool ReservationReplayRejected,
    bool NeighborRecolonizationConserved,
    bool SnapshotDetached,
    bool FiniteAfterBoundedRun,
    bool OxygenContractIsNetZero,
    bool Passed);

/// <summary>
/// Small deterministic checks for the environment side of the trophic chain.
/// They exercise finite seeding, growth, decay, fair withdrawal and detached rendering data.
/// </summary>
public static class FoodWebEnvironmentDiagnostics
{
    public static FoodWebEnvironmentDiagnosticResult Run()
    {
        SimulationConfig config = new()
        {
            WorldSize = 96f,
            EnvironmentGridSize = 33,
            ResourceBudgetReferenceAncestors = null
        };
        const ulong seed = 0xF00D5EEDUL;
        BilinearEnvironmentField environment = Create(config, seed);
        EnvironmentResourceSnapshot initialSnapshot = environment.CaptureResourceSnapshot();
        int landCell = IndexOfMaximum(initialSnapshot.LandPlants);
        int algaeCell = IndexOfMaximum(initialSnapshot.Algae);
        Vector2 landPosition = PositionOf(landCell, initialSnapshot);
        Vector2 algaePosition = PositionOf(algaeCell, initialSnapshot);
        double initialMatter = environment.TotalEnvironmentMatter;
        double initialOxygen = environment.TotalOxygen;

        ProducerStepResult producer = environment.UpdateProducers(2.0);
        double producerError = Math.Max(
            Math.Abs(producer.ConservationResidual),
            Math.Abs(environment.TotalEnvironmentMatter - initialMatter));
        double lightError = Math.Abs(
            producer.LightEnergyConsumed -
            (producer.MatterGrown * BilinearEnvironmentField.FoodWebEnergyPerMatter));

        double beforeCycles = environment.TotalEnvironmentMatter;
        environment.UpdateMatterCycles(120.0);
        double cycleError = Math.Abs(environment.TotalEnvironmentMatter - beforeCycles);

        BilinearEnvironmentField coarse = Create(config, seed + 1);
        BilinearEnvironmentField fine = Create(config, seed + 1);
        coarse.UpdateProducers(10.0);
        for (int step = 0; step < 100; step++) fine.UpdateProducers(0.1);
        double consistency = RelativeDifference(coarse.TotalVegetation, fine.TotalVegetation);

        AllocationTrial allocation = OrganicAllocationTrial(config, seed + 2, landPosition);
        bool mineralConserved = MineralReservationTrial(config, seed + 3, landPosition);
        bool detritusConserved = DetritusReservationTrial(config, seed + 4, algaePosition);
        bool recolonized = RecolonizationTrial(config, seed + 5);

        ulong fingerprintBefore = environment.ComputeResourceFingerprint();
        EnvironmentResourceSnapshot detached = environment.CaptureResourceSnapshot();
        if (detached.Minerals.Length > 0) detached.Minerals[0] += 12345.0;
        if (detached.LandPlants.Length > 0) detached.LandPlants[0] += 12345.0;
        bool snapshotDetached = fingerprintBefore == environment.ComputeResourceFingerprint() &&
            !ReferenceEquals(detached.Minerals, environment.CaptureResourceSnapshot().Minerals);

        for (int step = 0; step < 240; step++)
        {
            environment.UpdateMatterCycles(0.5);
            environment.UpdateProducers(0.5);
        }
        bool finite = environment.AllFinite &&
            Math.Abs(environment.TotalEnvironmentMatter - initialMatter) < 1e-8;
        bool oxygenNetZero = producer.OxygenProduced == 0.0 && producer.OxygenConsumed == 0.0 &&
            Math.Abs(environment.TotalOxygen - initialOxygen) < 1e-10;

        bool passed = initialSnapshot.LandPlants[landCell] > 0.0 &&
            initialSnapshot.Algae[algaeCell] > 0.0 && producer.MatterGrown > 0.0 &&
            producerError < 1e-10 && lightError < 1e-10 && cycleError < 1e-10 &&
            consistency <= 0.10 && allocation.EqualDifference < 1e-12 &&
            allocation.ReversedDifference < 1e-12 && allocation.ReturnError < 1e-10 &&
            allocation.ReplayRejected && mineralConserved && detritusConserved &&
            recolonized && snapshotDetached && finite && oxygenNetZero;

        return new FoodWebEnvironmentDiagnosticResult(
            initialMatter,
            initialSnapshot.LandPlants.Sum(),
            initialSnapshot.Algae.Sum(),
            producer.MatterGrown,
            producer.LightEnergyConsumed,
            producerError,
            cycleError,
            consistency,
            allocation.EqualDifference,
            allocation.ReversedDifference,
            allocation.ReturnError,
            mineralConserved,
            detritusConserved,
            allocation.ReplayRejected,
            recolonized,
            snapshotDetached,
            finite,
            oxygenNetZero,
            passed);
    }

    private static BilinearEnvironmentField Create(SimulationConfig config, ulong seed) =>
        new(config, new DeterministicRandom(seed, 19));

    private static AllocationTrial OrganicAllocationTrial(
        SimulationConfig config, ulong seed, Vector2 position)
    {
        OrganicUptakeRequest first = new(1, position, 100.0);
        OrganicUptakeRequest second = new(2, position, 100.0);
        BilinearEnvironmentField forward = Create(config, seed);
        double before = forward.TotalEnvironmentMatter;
        IReadOnlyDictionary<ulong, OrganicReservation> a = forward.ReserveOrganic([first, second]);
        double equal = Math.Abs(a[1].Total - a[2].Total);
        foreach (OrganicReservation reservation in a.Values)
            forward.ReturnOrganic(reservation, reservation.Total);
        double returnError = Math.Abs(forward.TotalEnvironmentMatter - before);
        bool replayRejected = false;
        try { forward.ReturnOrganic(a[1], a[1].Total); }
        catch (InvalidOperationException) { replayRejected = true; }

        BilinearEnvironmentField reverse = Create(config, seed);
        IReadOnlyDictionary<ulong, OrganicReservation> b = reverse.ReserveOrganic([second, first]);
        double reversed = Math.Max(
            Math.Abs(a[1].Total - b[1].Total),
            Math.Abs(a[2].Total - b[2].Total));
        return new(equal, reversed, returnError, replayRejected);
    }

    private static bool MineralReservationTrial(
        SimulationConfig config, ulong seed, Vector2 position)
    {
        BilinearEnvironmentField environment = Create(config, seed);
        double before = environment.TotalEnvironmentMatter;
        IReadOnlyDictionary<ulong, MineralReservation> reservations = environment.ReserveMinerals(
            [new MineralUptakeRequest(1, position, 1.0)]);
        MineralReservation reservation = reservations[1];
        environment.ReturnMinerals(reservation, reservation.Total);
        return Math.Abs(environment.TotalEnvironmentMatter - before) < 1e-12;
    }

    private static bool DetritusReservationTrial(
        SimulationConfig config, ulong seed, Vector2 position)
    {
        BilinearEnvironmentField environment = Create(config, seed);
        environment.DepositDetritus(position, 1.0);
        double before = environment.TotalEnvironmentMatter;
        IReadOnlyDictionary<ulong, DetritusReservation> reservations = environment.ReserveDetritus(
            [new DetritusUptakeRequest(1, position, 1.0)]);
        DetritusReservation reservation = reservations[1];
        environment.ReturnDetritus(reservation, reservation.Total);
        return Math.Abs(environment.TotalEnvironmentMatter - before) < 1e-12;
    }

    private static bool RecolonizationTrial(SimulationConfig config, ulong seed)
    {
        BilinearEnvironmentField environment = Create(config, seed);
        EnvironmentResourceSnapshot snapshot = environment.CaptureResourceSnapshot();
        int target = -1;
        for (int y = 1; y < snapshot.GridSize - 1 && target < 0; y++)
        for (int x = 1; x < snapshot.GridSize - 1; x++)
        {
            int index = (y * snapshot.GridSize) + x;
            if (snapshot.Algae[index] <= 0.0) continue;
            if (snapshot.Algae[index - 1] > 0.0 && snapshot.Algae[index + 1] > 0.0 &&
                snapshot.Algae[index - snapshot.GridSize] > 0.0 &&
                snapshot.Algae[index + snapshot.GridSize] > 0.0)
            {
                target = index;
                break;
            }
        }
        if (target < 0) return false;
        Vector2 position = PositionOf(target, snapshot);
        IReadOnlyDictionary<ulong, OrganicReservation> reservations = environment.ReserveOrganic(
            [new OrganicUptakeRequest(1, position, 100.0, 1.0)]);
        if (!reservations.TryGetValue(1, out OrganicReservation removed) || removed.Algae <= 0.0)
            return false;
        double before = environment.TotalEnvironmentMatter;
        for (int step = 0; step < 4; step++) environment.UpdateProducers(0.5);
        EnvironmentResourceSnapshot after = environment.CaptureResourceSnapshot();
        return after.Algae[target] > 0.0 &&
            Math.Abs(environment.TotalEnvironmentMatter - before) < 1e-10;
    }

    private static int IndexOfMaximum(double[] values)
    {
        int maximumIndex = 0;
        for (int index = 1; index < values.Length; index++)
            if (values[index] > values[maximumIndex]) maximumIndex = index;
        return maximumIndex;
    }

    private static Vector2 PositionOf(int index, EnvironmentResourceSnapshot snapshot)
    {
        float longitudeSpacing = snapshot.WorldSize / snapshot.GridSize;
        float colatitudeSpacing = snapshot.WorldSize / (snapshot.GridSize - 1f);
        return new Vector2(
            (index % snapshot.GridSize) * longitudeSpacing,
            (index / snapshot.GridSize) * colatitudeSpacing);
    }

    private static double RelativeDifference(double a, double b) =>
        Math.Abs(a - b) / Math.Max(1e-12, Math.Max(Math.Abs(a), Math.Abs(b)));

    private readonly record struct AllocationTrial(
        double EqualDifference,
        double ReversedDifference,
        double ReturnError,
        bool ReplayRejected);
}
