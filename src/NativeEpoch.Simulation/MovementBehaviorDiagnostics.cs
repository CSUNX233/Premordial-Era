using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct MovementBehaviorDiagnosticResult(
    double NegativeSteeringHeading,
    double NeutralSteeringHeading,
    double PositiveSteeringHeading,
    double HealthyNetDisplacement,
    double HealthyPathLength,
    double HealthyBodyLengths,
    int VisitedResourceCells,
    Vector2 BoundsSpan,
    double LowEnergyDisplacement,
    double LowEnergyMeanActivity,
    bool Passed);

/// <summary>
/// Bounded checks for steering authority, persistent exploration, and the
/// low-energy rest response. This diagnostic does not alter the simulated rules.
/// </summary>
public static class MovementBehaviorDiagnostics
{
    public static MovementBehaviorDiagnosticResult Run()
    {
        double negative = FixedSteeringHeading(-0.5);
        double neutral = FixedSteeringHeading(0.0);
        double positive = FixedSteeringHeading(0.5);
        MovementTrack healthy = TrackHealthy();
        MovementTrack resting = TrackLowEnergy();
        bool passed = negative < 0.0 && positive > 0.0 &&
            negative < neutral && neutral < positive &&
            healthy.Net > 10.0 && healthy.Path > 10.0 &&
            healthy.Net / healthy.Path > 0.40 && healthy.Cells >= 6 && healthy.Samples == 1200 &&
            resting.Net < 0.20 && resting.MeanActivity < 0.05 && resting.Samples == 100;
        return new(negative, neutral, positive, healthy.Net, healthy.Path,
            healthy.BodyLengths, healthy.Cells, healthy.Span, resting.Net,
            resting.MeanActivity, passed);
    }

    private static double FixedSteeringHeading(double steering)
    {
        Genome genome = Genome.CreateAncestor();
        BodyRegion[] regions = genome.Regions.Select(gene => new BodyRegion(
            gene.RegionId, BodyCalculator.TargetMatter(gene), 1.0,
            Substrate: 1.0, Oxygen: 1.0, Water: 1.0, Energy: 120.0)).ToArray();
        DevelopingBody body = new(genome, regions);
        BodyPose pose = new(genome, body);
        SimulationConfig config = new();
        ControllerOutputs output = ControllerOutputs.Basal with
            { ContractionActivation = 1.0, LateralContraction = steering };
        double heading = 0.0;
        IReadOnlyDictionary<int, double> noLocalSignal =
            body.Regions.ToDictionary(region => region.RegionId, _ => 0.0);
        for (int step = 0; step < 100; step++)
        {
            body.UpdateFunctionalState(genome, output, noLocalSignal, config.FixedDeltaSeconds);
            BodyMechanicsResult result = pose.Step(genome, body, output,
                step * config.FixedDeltaSeconds, 1.0, 1.0, config,
                config.FixedDeltaSeconds, groundSupported: true, reciprocalDiagnostic: false);
            heading += result.AngularVelocity * config.FixedDeltaSeconds;
        }
        return heading;
    }

    private static MovementTrack TrackHealthy()
    {
        SimulationConfig config = new() { ReproductionEnergyThreshold = 100.0 };
        SimulationWorld world = new(config, 20260908, 1);
        for (int step = 0; step < 300; step++) world.Step();
        return Track(world, world.Organisms[0].Id, 1200, config);
    }

    private static MovementTrack TrackLowEnergy()
    {
        SimulationConfig config = new()
        {
            ReproductionEnergyThreshold = 100.0,
            AncestorEnergy = 0.05,
            LightEnergyPerSurfacePerSecond = 0.0001,
            SurfaceLightEnergyPerWorldAreaPerSecond = 0.0001
        };
        SimulationWorld world = new(config, 20260908, 1);
        return Track(world, world.Organisms[0].Id, 100, config);
    }

    private static MovementTrack Track(
        SimulationWorld world, ulong organismId, int steps, SimulationConfig config)
    {
        Organism startOrganism = world.Organisms.Single(organism => organism.Id == organismId);
        Vector2 start = startOrganism.Position;
        Vector2 previous = start;
        float minX = start.X, maxX = start.X, minY = start.Y, maxY = start.Y;
        double path = 0.0, activity = 0.0;
        int samples = 0;
        HashSet<(int X, int Y)> cells = [];
        double cellSize = config.WorldSize / (config.EnvironmentGridSize - 1.0);
        for (int step = 0; step < steps; step++)
        {
            world.Step();
            int index = -1;
            for (int candidate = 0; candidate < world.Organisms.Count; candidate++)
                if (world.Organisms[candidate].Id == organismId) { index = candidate; break; }
            if (index < 0) break;
            Organism current = world.Organisms[index];
            path += Vector2.Distance(previous, current.Position);
            previous = current.Position;
            minX = Math.Min(minX, previous.X); maxX = Math.Max(maxX, previous.X);
            minY = Math.Min(minY, previous.Y); maxY = Math.Max(maxY, previous.Y);
            cells.Add(((int)(previous.X / cellSize), (int)(previous.Y / cellSize)));
            activity += current.ExplorationDrive;
            samples++;
        }
        double net = Vector2.Distance(start, previous);
        double diameter = Math.Max(0.1, startOrganism.Body.Cache.BoundingRadius * 2.0);
        return new(net, path, net / diameter, cells.Count,
            new Vector2(maxX - minX, maxY - minY), samples > 0 ? activity / samples : 0.0, samples);
    }

    private readonly record struct MovementTrack(
        double Net, double Path, double BodyLengths, int Cells, Vector2 Span,
        double MeanActivity, int Samples);
}
