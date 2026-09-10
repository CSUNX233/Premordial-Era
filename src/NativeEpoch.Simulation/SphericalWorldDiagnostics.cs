using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SphericalWorldDiagnosticResult(
    double SeamAdvanceDistance,
    double PoleAdvanceDistance,
    double SeamContactDistance,
    double PoleContactDistance,
    double CellAreaRelativeError,
    double OceanAreaFraction,
    double EnvironmentConservationError,
    double SeamTerrainDifference,
    double PoleTerrainDifference,
    bool CenterMappingCorrect,
    bool RightHandedBasis,
    bool SameSeedReproduces,
    bool DifferentSeedChangesTerrain,
    bool ExplicitAllWater,
    bool ExplicitAllLand,
    bool SeamBroadPhaseConnects,
    bool PoleBroadPhaseConnects,
    bool Passed);

/// <summary>Bounded checks for globe topology, seeded terrain and finite spherical resources.</summary>
public static class SphericalWorldDiagnostics
{
    public static SphericalWorldDiagnosticResult Run()
    {
        const float size = 512f;
        const double centimetre = 0.01;
        Vector2 center = new(size * 0.5f, size * 0.5f);
        Vector3 centerUnit = SphericalWorld.ToUnit(center, size);
        SphericalBasis basis = SphericalWorld.BasisAt(center, size);
        bool centerCorrect = Vector3.Distance(centerUnit, Vector3.UnitZ) < 1e-6f;
        bool rightHanded = Vector3.Dot(Vector3.Cross(basis.East, basis.Up), basis.South) > 0.999999f;

        Vector2 seamStart = new(size - 0.002f, size * 0.5f);
        Vector2 seamEnd = SphericalWorld.Advance(
            seamStart, new Vector2((float)centimetre, 0f), 1.0, size);
        double seamAdvance = SphericalWorld.Distance(seamStart, seamEnd, size);
        Vector2 poleStart = new(size * 0.31f, 0.002f);
        Vector2 poleEnd = SphericalWorld.Advance(
            poleStart, new Vector2(0f, (float)-centimetre), 1.0, size);
        double poleAdvance = SphericalWorld.Distance(poleStart, poleEnd, size);

        Vector2 seamA = new(0.0025f, size * 0.5f);
        Vector2 seamB = new(size - 0.0025f, size * 0.5f);
        Vector2 poleA = new(0f, 0.005f);
        Vector2 poleB = new(size * 0.5f, 0.005f);
        double seamContactDistance = SphericalWorld.Distance(seamA, seamB, size);
        double poleContactDistance = SphericalWorld.Distance(poleA, poleB, size);
        OccupancyShape tiny = new(0.02f, 0.02f);
        SpatialOccupancyIndex seamIndex = new(0.25f, size);
        seamIndex.Upsert(new SpatialOccupant(1, seamA, 1f, tiny));
        bool seamConnected = !seamIndex.CanPlace(seamB, 1f, tiny);
        SpatialOccupancyIndex poleIndex = new(0.25f, size);
        poleIndex.Upsert(new SpatialOccupant(1, poleA, 1f, tiny));
        bool poleConnected = !poleIndex.CanPlace(poleB, 1f, tiny);

        SimulationConfig config = new()
        {
            WorldSize = size,
            EnvironmentGridSize = 65,
            ResourceBudgetReferenceAncestors = null
        };
        const ulong seed = 0x5F4E_5245UL;
        BilinearEnvironmentField first = Create(config, seed);
        BilinearEnvironmentField replay = Create(config, seed);
        BilinearEnvironmentField changed = Create(config, seed + 1);
        EnvironmentResourceSnapshot snapshot = first.CaptureResourceSnapshot();
        EnvironmentResourceSnapshot replaySnapshot = replay.CaptureResourceSnapshot();
        EnvironmentResourceSnapshot changedSnapshot = changed.CaptureResourceSnapshot();
        bool sameSeed = snapshot.TerrainHeight.SequenceEqual(replaySnapshot.TerrainHeight) &&
            first.ComputeResourceFingerprint() == replay.ComputeResourceFingerprint();
        bool differentSeed = !snapshot.TerrainHeight.SequenceEqual(changedSnapshot.TerrainHeight);

        double expectedArea = 4.0 * Math.PI * Math.Pow(SphericalWorld.Radius(size), 2.0);
        double areaError = Math.Abs(snapshot.CellAreas.Sum() - expectedArea) / expectedArea;
        EnvironmentSample seamZero = first.Sample(new Vector2(0f, size * 0.47f));
        EnvironmentSample seamSize = first.Sample(new Vector2(size, size * 0.47f));
        double seamTerrainDifference = Math.Abs(seamZero.TerrainHeight - seamSize.TerrainHeight);
        EnvironmentSample poleZero = first.Sample(Vector2.Zero);
        EnvironmentSample poleTurned = first.Sample(new Vector2(size * 0.37f, 0f));
        double poleTerrainDifference = Math.Abs(poleZero.TerrainHeight - poleTurned.TerrainHeight);

        double beforeMatter = first.TotalEnvironmentMatter;
        for (int step = 0; step < 10; step++)
        {
            first.UpdateMatterCycles(0.2);
            first.UpdateProducers(0.2);
        }
        double conservationError = Math.Abs(first.TotalEnvironmentMatter - beforeMatter);

        BilinearEnvironmentField allWater = Create(
            config with { TerrainElevationOffset = -10_000.0 }, seed);
        BilinearEnvironmentField allLand = Create(
            config with { TerrainElevationOffset = 10_000.0 }, seed);
        EnvironmentResourceSnapshot waterSnapshot = allWater.CaptureResourceSnapshot();
        EnvironmentResourceSnapshot landSnapshot = allLand.CaptureResourceSnapshot();
        bool explicitAllWater = waterSnapshot.TerrainHeight.All(value => value < 0.0);
        bool explicitAllLand = landSnapshot.TerrainHeight.All(value => value > 0.0);

        bool passed = centerCorrect && rightHanded &&
            Math.Abs(seamAdvance - centimetre) < 2e-5 &&
            Math.Abs(poleAdvance - centimetre) < 2e-5 &&
            seamContactDistance < 0.02 && poleContactDistance < 0.02 &&
            seamConnected && poleConnected && areaError < 1e-12 &&
            snapshot.OceanAreaFraction is >= 0.70 and <= 0.80 &&
            sameSeed && differentSeed && seamTerrainDifference < 1e-12 &&
            poleTerrainDifference < 1e-10 && conservationError < 1e-8 &&
            first.AllFinite && explicitAllWater && explicitAllLand;

        return new SphericalWorldDiagnosticResult(
            seamAdvance, poleAdvance, seamContactDistance, poleContactDistance,
            areaError, snapshot.OceanAreaFraction, conservationError,
            seamTerrainDifference, poleTerrainDifference, centerCorrect, rightHanded,
            sameSeed, differentSeed, explicitAllWater, explicitAllLand,
            seamConnected, poleConnected, passed);
    }

    private static BilinearEnvironmentField Create(SimulationConfig config, ulong seed) =>
        new(config, new DeterministicRandom(seed, 31));
}
