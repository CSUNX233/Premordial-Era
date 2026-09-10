using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct DietOrganicAllocationDiagnosticResult(
    bool StrictHerbivoreExcludedFromLoose,
    bool OmnivoreReceivedLoose,
    double EqualShareDifference,
    double ReversedOrderDifference,
    double ReservationLedgerError,
    double FullReturnError,
    double UnusedReturnError,
    bool WorldFoundersPlantOnly,
    bool WorldPredationStayedZero,
    double WorldMatterError,
    bool WorldFinite,
    bool Finite,
    bool Passed);

/// <summary>
/// Deterministic checks for diet-constrained competition over producer and loose organic pools.
/// </summary>
public static class DietOrganicAllocationDiagnostics
{
    public static DietOrganicAllocationDiagnosticResult Run()
    {
        SimulationConfig config = new()
        {
            WorldSize = 96f,
            EnvironmentGridSize = 33,
            ResourceBudgetReferenceAncestors = null
        };
        const ulong seed = 0xD1E7A110CUL;

        BilinearEnvironmentField forward = CreatePrimed(config, seed);
        EnvironmentResourceSnapshot snapshot = forward.CaptureResourceSnapshot();
        int cell = FindMixedFoodCell(snapshot);
        Vector2 position = PositionOf(cell, snapshot);
        double cellFood = snapshot.LandPlants[cell] + snapshot.Algae[cell] +
            snapshot.EdibleOrganics[cell];
        double request = Math.Max(1.0, cellFood * 2.0);
        OrganicUptakeRequest herbivore = new(1, position, request, 1.0, 0.0);
        OrganicUptakeRequest omnivore = new(2, position, request, 0.25, 1.0);

        double before = forward.TotalEnvironmentMatter;
        IReadOnlyDictionary<ulong, OrganicReservation> reservations =
            forward.ReserveOrganic([herbivore, omnivore]);
        double reserved = reservations.Values.Sum(value => value.Total);
        double ledgerError = Math.Abs(forward.TotalEnvironmentMatter + reserved - before);
        double equalDifference = Math.Abs(reservations[1].Total - reservations[2].Total);
        bool herbivoreExcluded = reservations[1].EdibleOrganics == 0.0;
        bool omnivoreFedFromLoose = reservations[2].EdibleOrganics > 0.0;

        foreach (OrganicReservation reservation in reservations.Values)
            forward.ReturnOrganic(reservation, reservation.Total);
        double fullReturnError = Math.Abs(forward.TotalEnvironmentMatter - before);

        BilinearEnvironmentField reverse = CreatePrimed(config, seed);
        IReadOnlyDictionary<ulong, OrganicReservation> reversed =
            reverse.ReserveOrganic([omnivore, herbivore]);
        double reversedDifference = Math.Max(
            ReservationDifference(reservations[1], reversed[1]),
            ReservationDifference(reservations[2], reversed[2]));

        double reverseBefore = before;
        double unusedFirst = reversed[1].Total * 0.4;
        double unusedSecond = reversed[2].Total * 0.6;
        reverse.ReturnOrganic(reversed[1], unusedFirst);
        reverse.ReturnOrganic(reversed[2], unusedSecond);
        double expectedAfterReturn = reverseBefore -
            ((reversed[1].Total - unusedFirst) + (reversed[2].Total - unusedSecond));
        double unusedReturnError = Math.Abs(
            reverse.TotalEnvironmentMatter - expectedAfterReturn);

        SimulationWorld world = new(new SimulationConfig(), seed + 1, 24);
        bool foundersPlantOnly = world.Organisms.All(organism =>
        {
            Genome genome = world.Genomes.Get(organism.GenomeId);
            return genome.Metabolism.AnimalFoodAffinity == 0.0 &&
                genome.Regions.All(region => region.DecomposerExpression == 0.0);
        });
        world.Run(32);
        SimulationSnapshot worldSnapshot = world.CaptureSnapshot();
        bool predationStayedZero = worldSnapshot.CumulativePredationOrganic == 0.0;
        double worldMatterError = Math.Abs(worldSnapshot.MatterError);
        bool worldFinite = worldSnapshot.AllFinite &&
            worldMatterError <= Math.Max(1e-8, Math.Abs(worldSnapshot.InitialMatter) * 1e-10);

        bool finite = forward.AllFinite && reverse.AllFinite &&
            reservations.Values.All(IsFinite) && reversed.Values.All(IsFinite);
        bool passed = herbivoreExcluded && omnivoreFedFromLoose &&
            equalDifference < 1e-12 && reversedDifference < 1e-12 &&
            ledgerError < 1e-12 && fullReturnError < 1e-12 &&
            unusedReturnError < 1e-12 && foundersPlantOnly && predationStayedZero &&
            worldFinite && finite;
        return new DietOrganicAllocationDiagnosticResult(
            herbivoreExcluded,
            omnivoreFedFromLoose,
            equalDifference,
            reversedDifference,
            ledgerError,
            fullReturnError,
            unusedReturnError,
            foundersPlantOnly,
            predationStayedZero,
            worldMatterError,
            worldFinite,
            finite,
            passed);
    }

    private static BilinearEnvironmentField CreatePrimed(SimulationConfig config, ulong seed)
    {
        BilinearEnvironmentField environment = new(config, new DeterministicRandom(seed, 29));
        environment.UpdateProducers(2.0);
        return environment;
    }

    private static int FindMixedFoodCell(EnvironmentResourceSnapshot snapshot)
    {
        int best = -1;
        for (int index = 0; index < snapshot.EdibleOrganics.Length; index++)
        {
            double producer = snapshot.LandPlants[index] + snapshot.Algae[index];
            double loose = snapshot.EdibleOrganics[index];
            if (loose <= 0.0 || producer < loose) continue;
            if (best < 0 || loose > snapshot.EdibleOrganics[best]) best = index;
        }
        if (best < 0)
            throw new InvalidOperationException("Diagnostic map contains no cell with both producer and loose organic food.");
        return best;
    }

    private static Vector2 PositionOf(int index, EnvironmentResourceSnapshot snapshot) => new(
        (index % snapshot.GridSize) * snapshot.WorldSize / snapshot.GridSize,
        (index / snapshot.GridSize) * snapshot.WorldSize / (snapshot.GridSize - 1f));

    private static double ReservationDifference(OrganicReservation a, OrganicReservation b) =>
        Math.Max(
            Math.Max(Math.Abs(a.LandPlants - b.LandPlants), Math.Abs(a.Algae - b.Algae)),
            Math.Abs(a.EdibleOrganics - b.EdibleOrganics));

    private static bool IsFinite(OrganicReservation reservation) =>
        double.IsFinite(reservation.LandPlants) && reservation.LandPlants >= 0.0 &&
        double.IsFinite(reservation.Algae) && reservation.Algae >= 0.0 &&
        double.IsFinite(reservation.EdibleOrganics) && reservation.EdibleOrganics >= 0.0;
}
