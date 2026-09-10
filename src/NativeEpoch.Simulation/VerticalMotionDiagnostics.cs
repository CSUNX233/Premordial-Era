namespace NativeEpoch.Simulation;

public readonly record struct VerticalMotionDiagnosticResult(
    double UpwardDepth,
    double NeutralDepth,
    double DownwardDepth,
    double UnpoweredDepth,
    double UpwardEnergy,
    double DownwardEnergy,
    bool SurfaceVelocityCleared,
    bool BottomVelocityCleared,
    bool Passed);

public static class VerticalMotionDiagnostics
{
    public static VerticalMotionDiagnosticResult Run()
    {
        TrialResult upward = Trial(0.8, 1.0);
        TrialResult neutral = Trial(0.0, 1.0);
        TrialResult downward = Trial(-0.8, 1.0);
        TrialResult unpowered = Trial(0.8, 0.0);
        bool surfaceCleared = BoundaryVelocityCleared(surface: true);
        bool bottomCleared = BoundaryVelocityCleared(surface: false);
        bool passed = upward.Depth < neutral.Depth - 1e-3 &&
            downward.Depth > neutral.Depth + 1e-3 &&
            Math.Abs(unpowered.Depth - neutral.Depth) < 1e-6 &&
            upward.Energy > 0.0 && downward.Energy > 0.0 &&
            surfaceCleared && bottomCleared;
        return new(upward.Depth, neutral.Depth, downward.Depth, unpowered.Depth,
            upward.Energy, downward.Energy, surfaceCleared, bottomCleared, passed);
    }

    private static TrialResult Trial(double command, double regionalEnergy)
    {
        Genome genome = NeutralGenome();
        DevelopingBody body = DevelopedBody(genome, regionalEnergy);
        SimulationConfig config = new();
        ControllerOutputs outputs = ControllerOutputs.Basal with
            { ContractionActivation = 1.0, VerticalContraction = command };
        IReadOnlyDictionary<int, double> noSignals =
            body.Regions.ToDictionary(region => region.RegionId, _ => 0.0);
        float depth = 5f, velocity = 0f;
        double energy = 0.0;
        for (int step = 0; step < 20; step++)
        {
            body.UpdateFunctionalState(genome, outputs, noSignals, config.FixedDeltaSeconds);
            VerticalMotionResult result = VerticalMotionMechanics.Step(body, genome, outputs,
                ref depth, ref velocity, 10.0, 0.2f, 1.0, 1.0, config,
                config.FixedDeltaSeconds);
            energy += result.EnergySpent;
        }
        return new TrialResult(depth, energy);
    }

    private static bool BoundaryVelocityCleared(bool surface)
    {
        Genome genome = NeutralGenome();
        DevelopingBody body = DevelopedBody(genome, 1.0);
        SimulationConfig config = new();
        float depth = surface ? 0.2f : 9.8f;
        float velocity = surface ? 1f : -1f;
        ControllerOutputs outputs = ControllerOutputs.Basal with
        {
            ContractionActivation = 1.0,
            VerticalContraction = surface ? 1.0 : -1.0
        };
        VerticalMotionResult result = VerticalMotionMechanics.Step(body, genome, outputs,
            ref depth, ref velocity, 10.0, 0.2f, 1.0, 1.0, config,
            config.FixedDeltaSeconds);
        return Math.Abs(velocity) < 1e-7 && result.EnergySpent == 0.0;
    }

    private static Genome NeutralGenome()
    {
        Genome source = Genome.CreateAncestor();
        return new Genome(source.Regions.Select(region => region with { Density = 0.5 }),
            source.MutationRate, source.Metabolism, source.ControllerNodes, source.Sensors);
    }

    private static DevelopingBody DevelopedBody(Genome genome, double regionalEnergy) => new(
        genome,
        genome.Regions.Select(gene => new BodyRegion(gene.RegionId,
            BodyCalculator.TargetMatter(gene), 1.0, Substrate: 1.0, Oxygen: 1.0,
            Water: 1.0, Energy: regionalEnergy)));

    private readonly record struct TrialResult(double Depth, double Energy);
}
