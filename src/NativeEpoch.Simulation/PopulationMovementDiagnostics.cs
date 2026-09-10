using System.Numerics;

namespace NativeEpoch.Simulation;

/// <summary>Reports actual trajectories of multiple founders, including poor swimmers.</summary>
public static class PopulationMovementDiagnostics
{
    public static object Run()
    {
        SimulationConfig config = new() { ReproductionEnergyThreshold = 100 };
        SimulationWorld world = new(config, 20260908, 24);
        world.Run(300);
        var tracks = world.Organisms.ToDictionary(o => o.Id, o => new Track(o));
        for (int step = 0; step < 1200; step++)
        {
            world.Step();
            foreach (Organism o in world.Organisms)
            {
                if (!tracks.TryGetValue(o.Id, out Track? t)) continue;
                t.Path += SphericalWorld.Distance(t.Last, o.Position, config.WorldSize);
                Vector2 heading = SphericalWorld.Transport(t.Last, o.Position,
                    new Vector2((float)Math.Cos(t.Heading), (float)Math.Sin(t.Heading)), config.WorldSize);
                double previousHeading = Math.Atan2(heading.Y, heading.X);
                t.Turn += Math.Abs(Math.IEEERemainder(o.HeadingRadians - previousHeading, Math.Tau));
                t.Last = o.Position; t.Heading = o.HeadingRadians;
                t.Energy += o.Body.TotalEnergy; t.Activity += o.ExplorationDrive;
                t.Steering += o.ControllerOutputs.LateralContraction; t.Guidance += o.ForagingMemory.Steering; t.Count++;
                t.Span = Math.Max(t.Span, SphericalWorld.Distance(t.Start, o.Position, config.WorldSize));
            }
        }
        return tracks.Select(pair => new {
            id = pair.Key, seconds = pair.Value.Count * config.FixedDeltaSeconds,
            net = Math.Round(SphericalWorld.Distance(pair.Value.Start, pair.Value.Last, config.WorldSize), 3),
            path = Math.Round(pair.Value.Path, 3), range = Math.Round(pair.Value.Span, 3),
            turns = Math.Round(pair.Value.Turn / Math.Tau, 2),
            energy = Math.Round(pair.Value.Energy / Math.Max(1, pair.Value.Count), 3),
            activity = Math.Round(pair.Value.Activity / Math.Max(1, pair.Value.Count), 3),
            steering = Math.Round(pair.Value.Steering / Math.Max(1, pair.Value.Count), 3),
            guidance = Math.Round(pair.Value.Guidance / Math.Max(1, pair.Value.Count), 3)
        }).ToArray();
    }

    private sealed class Track(Organism organism)
    {
        public Vector2 Start = organism.Position, Last = organism.Position;
        public double Heading = organism.HeadingRadians, Path, Turn, Span, Energy, Activity, Steering, Guidance;
        public int Count;
    }
}
