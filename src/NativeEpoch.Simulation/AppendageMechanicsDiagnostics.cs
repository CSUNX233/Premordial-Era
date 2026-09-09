using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct AppendageMechanicsDiagnosticResult(
    double PoweredDisplacement,
    double ZeroEnergyDisplacement,
    double SuspendedDisplacement,
    double DisconnectedDisplacement,
    double PassiveSupport,
    double PoweredEnergySpent,
    int MaximumObservedContacts,
    double TerrainSampleSpan,
    double MaximumSpeed,
    double TranslationFeedbackError,
    bool AllFinite,
    bool Passed);

/// <summary>Short deterministic causal checks; no ecology run and no Godot dependency.</summary>
public static class AppendageMechanicsDiagnostics
{
    public static AppendageMechanicsDiagnosticResult Run()
    {
        Genome poweredGenome = AppendageGenome();
        TrialOutcome powered = Trial(poweredGenome, initialEnergy: 20.0, bodyElevation: 0.48, active: true);
        TrialOutcome zeroEnergy = Trial(poweredGenome, initialEnergy: 0.0, bodyElevation: 0.48, active: true);
        TrialOutcome suspended = Trial(poweredGenome, initialEnergy: 20.0, bodyElevation: 4.0, active: true);
        Genome disconnectedGenome = ReplaceAppendages(poweredGenome,
            gene => gene with { Rigidity = 0.0 });
        TrialOutcome disconnected = Trial(disconnectedGenome, initialEnergy: 20.0, bodyElevation: 0.48, active: true);
        TrialOutcome passive = Trial(poweredGenome, initialEnergy: 0.0, bodyElevation: 0.48, active: false);
        TrialOutcome crowded = Trial(ManyFeetGenome(), initialEnergy: 20.0, bodyElevation: 0.48, active: true, steps: 4);
        double translationFeedbackError = TranslationFeedbackError(poweredGenome);

        bool finite = powered.Finite && zeroEnergy.Finite && suspended.Finite &&
            disconnected.Finite && passive.Finite && crowded.Finite;
        bool passed = finite &&
            powered.Displacement > 1e-5 && powered.EnergySpent > 1e-6 && powered.ContactCount > 0 &&
            zeroEnergy.Displacement < 1e-9 && zeroEnergy.EnergySpent < 1e-12 &&
            suspended.Displacement < 1e-9 && suspended.ContactCount == 0 &&
            disconnected.Displacement < 1e-9 && disconnected.ContactCount == 0 &&
            passive.Displacement < 1e-9 && passive.Support > 0.0 &&
            crowded.ContactCount == AppendageMechanics.MaximumContacts &&
            powered.TerrainSampleSpan > 0.05 &&
            powered.MaximumSpeed <= new SimulationConfig().MaximumMovementSpeed + 1e-9 &&
            translationFeedbackError < 1e-6;
        return new(powered.Displacement, zeroEnergy.Displacement, suspended.Displacement,
            disconnected.Displacement, passive.Support, powered.EnergySpent,
            crowded.ContactCount, powered.TerrainSampleSpan, powered.MaximumSpeed,
            translationFeedbackError, finite, passed);
    }

    private static TrialOutcome Trial(Genome genome, double initialEnergy, double bodyElevation,
        bool active, int steps = 96)
    {
        BodyRegion[] state = genome.Regions.Select(gene => new BodyRegion(
            gene.RegionId,
            BodyCalculator.TargetMatter(gene),
            1.0,
            Water: 1.0,
            Energy: initialEnergy,
            InternalSignal: Math.Sin(gene.RegionId * 1.7),
            TransportAvailability: 1.0,
            Activation: active ? 1.0 : 0.0,
            ContractileExpression: active ? 1.0 : 0.0,
            StructuralExpression: 1.0)).ToArray();
        DevelopingBody body = new(genome, state);
        BodyPose pose = new(genome, body);
        AppendageMechanics mechanics = new();
        SimulationConfig config = new();
        SlopeEnvironment terrain = new();
        ControllerOutputs output = ControllerOutputs.Basal with
        {
            ContractionActivation = active ? 1.0 : 0.0,
            LateralContraction = 0.62,
            VerticalContraction = -0.18
        };
        Vector2 start = new(4.0f, 4.0f);
        Vector2 position = start;
        double heading = 0.0;
        double energySpent = 0.0;
        double support = 0.0;
        int maximumContacts = 0;
        double maximumSpeed = 0.0;
        bool finite = true;
        for (int step = 0; step < steps; step++)
        {
            AppendageMechanicsResult result = mechanics.Step(genome, body, pose, output,
                position, heading, bodyElevation, terrain, config,
                step * config.FixedDeltaSeconds, config.FixedDeltaSeconds, enableGround: true);
            position += result.GroundVelocity * (float)config.FixedDeltaSeconds;
            maximumSpeed = Math.Max(maximumSpeed, result.GroundVelocity.Length());
            heading += result.AngularVelocity * config.FixedDeltaSeconds;
            energySpent += result.EnergySpent;
            support = Math.Max(support, result.SupportFraction);
            int planted = 0;
            foreach (AppendageContact contact in result.Contacts)
                if (contact.Planted) planted++;
            maximumContacts = Math.Max(maximumContacts, planted);
            finite &= result.AllFinite && float.IsFinite(position.X) && float.IsFinite(position.Y) &&
                double.IsFinite(heading);
        }
        return new(Vector2.Distance(start, position), energySpent, support, maximumContacts,
            terrain.SampleSpan, maximumSpeed, finite);
    }

    private static double TranslationFeedbackError(Genome genome)
    {
        DevelopingBody movingBody = CreateBody(genome, 20.0);
        DevelopingBody fixedBody = CreateBody(genome, 20.0);
        BodyPose movingPose = new(genome, movingBody), fixedPose = new(genome, fixedBody);
        AppendageMechanics moving = new(), fixedSolver = new();
        SimulationConfig config = new();
        FlatEnvironment terrain = new();
        ControllerOutputs output = ControllerOutputs.Basal with
            { ContractionActivation = 1.0, LateralContraction = 0.62, VerticalContraction = -0.18 };
        Vector2 movingPosition = new(4, 4), fixedPosition = movingPosition;
        double maximumError = 0.0;
        for (int step = 0; step < 48; step++)
        {
            double age = step * config.FixedDeltaSeconds;
            AppendageMechanicsResult movingResult = moving.Step(genome, movingBody, movingPose, output,
                movingPosition, 0.0, 0.48, terrain, config, age, config.FixedDeltaSeconds, true);
            AppendageMechanicsResult fixedResult = fixedSolver.Step(genome, fixedBody, fixedPose, output,
                fixedPosition, 0.0, 0.48, terrain, config, age, config.FixedDeltaSeconds, true);
            maximumError = Math.Max(maximumError,
                Vector2.Distance(movingResult.GroundVelocity, fixedResult.GroundVelocity));
            movingPosition += movingResult.GroundVelocity * (float)config.FixedDeltaSeconds;
        }
        return maximumError;
    }

    private static DevelopingBody CreateBody(Genome genome, double energy)
    {
        BodyRegion[] state = genome.Regions.Select(gene => new BodyRegion(
            gene.RegionId, BodyCalculator.TargetMatter(gene), 1.0, Water: 1.0, Energy: energy,
            InternalSignal: Math.Sin(gene.RegionId * 1.7), TransportAvailability: 1.0,
            Activation: 1.0, ContractileExpression: 1.0, StructuralExpression: 1.0)).ToArray();
        return new DevelopingBody(genome, state);
    }

    private static Genome AppendageGenome()
    {
        Genome ancestor = Genome.CreateAncestor();
        RegionGene[] regions = ancestor.Regions.Select(gene => gene.IsCore
            ? gene
            : gene with
            {
                AppearanceMaturity = 0.0,
                TargetLength = 1.35,
                TargetWidth = 0.22,
                Rigidity = 0.86,
                Toughness = 0.90,
                Contractility = 0.92,
                SignalConductivity = 0.72,
                ContractileExpression = 0.92,
                StructuralExpression = 0.95,
                JointRestPitch = -0.88,
                JointMobility = 0.92
            }).ToArray();
        return new Genome(regions, ancestor.MutationRate, ancestor.Metabolism,
            ancestor.ControllerNodes, ancestor.Sensors);
    }

    private static Genome ReplaceAppendages(Genome source, Func<RegionGene, RegionGene> replace)
    {
        RegionGene[] regions = source.Regions.Select(gene => gene.IsCore ? gene : replace(gene)).ToArray();
        return new Genome(regions, source.MutationRate, source.Metabolism,
            source.ControllerNodes, source.Sensors);
    }

    private static Genome ManyFeetGenome()
    {
        Genome source = AppendageGenome();
        RegionGene core = source.Regions.Single(gene => gene.IsCore);
        RegionGene template = source.Regions.First(gene => !gene.IsCore);
        List<RegionGene> regions = [core];
        for (int index = 0; index < 12; index++)
        {
            double angle = -Math.PI + (index * Math.Tau / 12.0);
            regions.Add(template with
            {
                RegionId = index + 1,
                ParentRegionId = core.RegionId,
                MatterSourceRegionId = core.RegionId,
                SignalSourceRegionId = core.RegionId,
                RelativeAngle = angle,
                Pigment = index / 12.0
            });
        }
        return new Genome(regions, source.MutationRate, source.Metabolism,
            source.ControllerNodes, source.Sensors);
    }

    private readonly record struct TrialOutcome(
        double Displacement,
        double EnergySpent,
        double Support,
        int ContactCount,
        double TerrainSampleSpan,
        double MaximumSpeed,
        bool Finite);

    private sealed class SlopeEnvironment : IEnvironmentField
    {
        private float _minimumX = float.PositiveInfinity;
        private float _maximumX = float.NegativeInfinity;
        public double SampleSpan => float.IsFinite(_minimumX) && float.IsFinite(_maximumX)
            ? _maximumX - _minimumX
            : 0.0;

        public EnvironmentSample Sample(Vector2 position, float depth = 0f)
        {
            _minimumX = Math.Min(_minimumX, position.X);
            _maximumX = Math.Max(_maximumX, position.X);
            double height = (0.08 * position.X) + (0.025 * position.Y) - 0.42;
            return new EnvironmentSample(height, height, 0.0, depth, 1.0, 1.0,
                1.0, 0.0, 0.0, 0.0, 0.7, 0.0, 1.0, Vector2.Zero);
        }
    }

    private sealed class FlatEnvironment : IEnvironmentField
    {
        public EnvironmentSample Sample(Vector2 position, float depth = 0f) =>
            new(0.0, 0.0, 0.0, depth, 1.0, 1.0, 1.0,
                0.0, 0.0, 0.0, 0.7, 0.0, 1.0, Vector2.Zero);
    }
}
