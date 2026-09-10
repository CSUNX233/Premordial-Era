using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SocialPerceptionDiagnosticResult(
    double ForwardSignal,
    double LateralSignal,
    double EnergySpent,
    double SizeRatio,
    bool ForwardTargetDetected,
    bool RearTargetRejected,
    bool OutOfRangeRejected,
    bool TerrainOcclusionRejected,
    bool DarkWaterRejected,
    bool ExpressionRequired,
    bool EnergyRequired,
    bool MotorConnectionRequired,
    bool NegativeGainPreserved,
    bool Passed);

/// <summary>Small positive and negative checks for paid local outline perception.</summary>
public static class SocialPerceptionDiagnostics
{
    public static SocialPerceptionDiagnosticResult Run()
    {
        SimulationConfig config = new();
        Vector2 origin = new(config.WorldSize * 0.5f);
        SocialCandidate ahead = Candidate(
            42, origin, new Vector2(4f, 0f), config, radius: 0.8f);
        SocialCandidate behind = Candidate(
            43, origin, new Vector2(-4f, 0f), config, radius: 0.8f);
        SocialCandidate side = Candidate(
            44, origin, new Vector2(3f, 2f), config, radius: 0.5f);
        SocialCandidate far = Candidate(
            45, origin, new Vector2(9f, 0f), config, radius: 0.8f);

        SocialPerceptionResult forward = Evaluate(
            config, origin, [ahead, side], new OutlineEnvironment(origin));
        SocialPerceptionResult rear = Evaluate(
            config, origin, [behind], new OutlineEnvironment(origin));
        SocialPerceptionResult distant = Evaluate(
            config, origin, [far], new OutlineEnvironment(origin));
        SocialPerceptionResult occluded = Evaluate(
            config, origin, [ahead], new OutlineEnvironment(origin, ridge: true));
        SocialPerceptionResult dark = Evaluate(
            config, origin, [ahead], new OutlineEnvironment(origin, darkWater: true), depth: 8f);
        SocialPerceptionResult noExpression = Evaluate(
            config, origin, [ahead], new OutlineEnvironment(origin), sensoryExpression: 0.0);
        SocialPerceptionResult noEnergy = Evaluate(
            config, origin, [ahead], new OutlineEnvironment(origin), energy: 0.0);
        SocialPerceptionResult disconnected = Evaluate(
            config, origin, [ahead], new OutlineEnvironment(origin), connectedMotor: false);
        SocialPerceptionResult inverted = Evaluate(
            config, origin, [ahead], new OutlineEnvironment(origin), gain: -1.0);

        bool forwardDetected = forward.TargetId == ahead.Id && forward.ForwardSignal > 0.0 &&
            forward.EnergySpent > 0.0 && forward.ActiveSensors == 1 &&
            forward.ApproachGain > 0.0 && forward.ActivationGain > 0.0 &&
            forward.SizeRatio > 1.0 && forward.MemorySeconds > 0.0;
        bool rearRejected = rear.TargetId == 0 && rear.ForwardSignal == 0.0;
        bool rangeRejected = distant.TargetId == 0;
        bool terrainRejected = occluded.TargetId == 0;
        bool darkRejected = dark.TargetId == 0;
        bool expressionRequired = noExpression.TargetId == 0 && noExpression.EnergySpent == 0.0;
        bool energyRequired = noEnergy.TargetId == 0 && noEnergy.EnergySpent == 0.0;
        bool motorRequired = disconnected.TargetId == 0 && disconnected.EnergySpent == 0.0;
        bool negativePreserved = inverted.TargetId == ahead.Id &&
            inverted.ForwardSignal < 0.0 && inverted.ApproachGain < 0.0 &&
            inverted.ActivationGain < 0.0;
        bool passed = forwardDetected && rearRejected && rangeRejected && terrainRejected &&
            darkRejected && expressionRequired && energyRequired && motorRequired && negativePreserved;
        return new SocialPerceptionDiagnosticResult(
            forward.ForwardSignal,
            forward.LateralSignal,
            forward.EnergySpent,
            forward.SizeRatio,
            forwardDetected,
            rearRejected,
            rangeRejected,
            terrainRejected,
            darkRejected,
            expressionRequired,
            energyRequired,
            motorRequired,
            negativePreserved,
            passed);
    }

    private static SocialPerceptionResult Evaluate(
        SimulationConfig config,
        Vector2 origin,
        IReadOnlyList<SocialCandidate> candidates,
        IEnvironmentField environment,
        double sensoryExpression = 0.9,
        double energy = 1.0,
        bool connectedMotor = true,
        double gain = 1.0,
        float depth = 0f)
    {
        Genome basis = Genome.CreateAncestor();
        RegionGene core = basis.Regions.Single(region => region.IsCore) with
        {
            SensoryExpression = sensoryExpression,
            LightReactivity = 0.9
        };
        ControllerNodeGene controller = basis.ControllerNodes[0] with
        {
            RecurrentSourceIndex = -1,
            ContractionOutputWeight = connectedMotor ? 0.9 : 0.0,
            LateralContractionOutputWeight = connectedMotor ? 0.8 : 0.0,
            VerticalContractionOutputWeight = 0.0
        };
        SensorGene sensor = new(
            SensorChannel.OrganismContrast,
            core.RegionId,
            0,
            gain,
            8.0,
            DirectionOffsetRadians: 0.0,
            Range: 6.0,
            DirectionalSelectivity: 0.9,
            ApproachWeight: 0.8,
            AvoidanceWeight: 0.3,
            TargetMemorySeconds: 2.0);
        Genome genome = new([core], 0.0, basis.Metabolism, [controller], [sensor]);
        double targetMatter = BodyCalculator.TargetMatter(core);
        DevelopingBody body = new(
            genome, targetMatter, 0.0, energy, 0.0, 1.0, CorePrecisionProfile.Reference);
        return SocialPerception.Evaluate(
            body, genome, environment, origin, depth, 0.0, candidates, config, 0.1);
    }

    private static SocialCandidate Candidate(
        ulong id,
        Vector2 origin,
        Vector2 offset,
        SimulationConfig config,
        float radius) =>
        new(
            id,
            SphericalWorld.OffsetPosition(origin, offset, config.WorldSize),
            0f,
            radius,
            0.20f);

    private sealed class OutlineEnvironment(
        Vector2 origin,
        bool ridge = false,
        bool darkWater = false) : IEnvironmentField
    {
        public EnvironmentSample Sample(Vector2 position, float depth = 0f)
        {
            double distance = SphericalWorld.Distance(origin, position, 512f);
            double terrain = ridge && distance is > 1.4 and < 3.2 ? 2.0 : darkWater ? -20.0 : 0.0;
            double waterDepth = darkWater ? 20.0 : 0.0;
            return new EnvironmentSample(
                terrain,
                0.0,
                waterDepth,
                depth,
                1.0,
                0.8,
                darkWater ? 0.0 : 0.9,
                0.0,
                0.0,
                0.0,
                0.7,
                1.0,
                1.0,
                Vector2.Zero);
        }
    }
}
