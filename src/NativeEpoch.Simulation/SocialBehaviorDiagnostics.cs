using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SocialBehaviorDiagnosticResult(
    double RightInjurySteering,
    double LeftInjurySteering,
    double CarnivoreSteering,
    double CarnivoreActivation,
    double MechanicsSpeed,
    double MechanicsAngularSpeed,
    bool InjuryTurnsAway,
    bool NoPerceptionHasNoResponse,
    bool MotorConnectionRequired,
    bool EnergyRequired,
    bool MemoryExpires,
    bool MemoryDoesNotTrackUnknownTarget,
    bool HerbivoreDoesNotApproach,
    bool HerbivoreCanAvoid,
    bool HungryCarnivoreApproaches,
    bool MechanicsExecutesDrive,
    bool Passed);

/// <summary>
/// Bounded fixtures for remembered social responses and their execution by the
/// ordinary body mechanics. These are capability checks, not evidence of evolved hunting.
/// </summary>
public static class SocialBehaviorDiagnostics
{
    public static SocialBehaviorDiagnosticResult Run()
    {
        SimulationConfig config = new();
        Vector2 origin = new(config.WorldSize * 0.5f);
        ClearEnvironment environment = new();

        Genome injuryGenome = CreateGenome(social: false, contact: true, affinity: 0.0, motor: true);
        SocialMotorResponse rightInjury = InjuryResponse(
            injuryGenome, origin, new Vector2(0f, 2f), config);
        SocialMotorResponse leftInjury = InjuryResponse(
            injuryGenome, origin, new Vector2(0f, -2f), config);
        bool injuryTurnsAway = rightInjury.Mode == SocialResponse.InjuryAvoidance &&
            leftInjury.Mode == SocialResponse.InjuryAvoidance &&
            rightInjury.Steering < 0.0 && leftInjury.Steering > 0.0;

        Genome noSensorGenome = CreateGenome(
            social: false, contact: false, affinity: 1.0, motor: true);
        DevelopingBody noSensorBody = CreateBody(noSensorGenome, 2.0);
        SocialMemory noSensorMemory = new();
        noSensorMemory.Observe(SyntheticSight(origin));
        noSensorMemory.RecordInjury(8, SphericalWorld.OffsetPosition(
            origin, new Vector2(0f, 2f), config.WorldSize), 0.04);
        SocialMotorResponse noSensor = noSensorMemory.Respond(
            noSensorBody, noSensorGenome, origin, 0.0, config, 0.1);
        bool noPerception = noSensor.Mode == SocialResponse.None &&
            noSensor.TargetId == 0 && noSensor.Steering == 0.0;

        Genome disconnectedGenome = CreateGenome(
            social: true, contact: true, affinity: 1.0, motor: false);
        DevelopingBody disconnectedBody = CreateBody(disconnectedGenome, 2.0);
        SocialMemory disconnectedMemory = new();
        disconnectedMemory.Observe(SyntheticSight(origin));
        disconnectedMemory.RecordInjury(9, SphericalWorld.OffsetPosition(
            origin, new Vector2(0f, 2f), config.WorldSize), 0.04);
        SocialMotorResponse disconnected = disconnectedMemory.Respond(
            disconnectedBody, disconnectedGenome, origin, 0.0, config, 0.1);
        bool motorRequired = disconnected.Mode == SocialResponse.None &&
            disconnected.Steering == 0.0 && disconnected.Activation == 0.0;

        Genome socialGenome = CreateGenome(social: true, contact: false, affinity: 1.0, motor: true);
        DevelopingBody emptyBody = CreateBody(socialGenome, 0.0);
        SocialMemory emptyMemory = new();
        emptyMemory.Observe(SyntheticSight(origin));
        SocialMotorResponse empty = emptyMemory.Respond(
            emptyBody, socialGenome, origin, 0.0, config, 0.1);
        bool energyRequired = empty.Mode == SocialResponse.None &&
            empty.Steering == 0.0 && empty.EnergySpent == 0.0;

        SocialMemory expiring = new();
        Vector2 remembered = SphericalWorld.OffsetPosition(
            origin, new Vector2(3f, 1f), config.WorldSize);
        expiring.Observe(SyntheticSight(origin) with
        {
            TargetPosition = remembered,
            MemorySeconds = 0.4
        });
        Vector2 unknownLivePosition = SphericalWorld.OffsetPosition(
            remembered, new Vector2(4f, -2f), config.WorldSize);
        expiring.Advance(0.2);
        bool doesNotTrack = expiring.TargetId != 0 &&
            SphericalWorld.Distance(expiring.LastSeenPosition, remembered, config.WorldSize) < 1e-6 &&
            SphericalWorld.Distance(expiring.LastSeenPosition, unknownLivePosition, config.WorldSize) > 1.0;
        expiring.Advance(0.21);
        bool memoryExpires = expiring.TargetId == 0 &&
            SphericalWorld.Distance(expiring.LastSeenPosition, remembered, config.WorldSize) < 1e-6;

        ResponseFixture herbivoreSmall = PerceivedResponse(
            affinity: 0.0, sizeRatio: 0.4f, origin, config, environment);
        bool herbivoreDoesNotApproach = herbivoreSmall.Response.Mode == SocialResponse.None &&
            herbivoreSmall.Response.Steering == 0.0;
        ResponseFixture herbivoreLarge = PerceivedResponse(
            affinity: 0.0, sizeRatio: 2.2f, origin, config, environment);
        bool herbivoreCanAvoid = herbivoreLarge.Response.Mode == SocialResponse.Avoid &&
            Math.Abs(herbivoreLarge.Response.Steering) > 1e-5;

        ResponseFixture carnivore = PerceivedResponse(
            affinity: 1.0, sizeRatio: 0.4f, origin, config, environment);
        bool carnivoreApproaches = carnivore.Sight.TargetId != 0 &&
            carnivore.Response.Mode == SocialResponse.Approach &&
            carnivore.Response.Steering > 1e-5 &&
            carnivore.Response.Activation > 0.0;
        BodyPose pose = new(carnivore.Genome, carnivore.Body);
        ControllerOutputs driven = ControllerOutputs.Basal with
        {
            LateralContraction = Math.Clamp(carnivore.Response.Steering, -1.0, 1.0),
            ContractionActivation = Math.Clamp(
                ControllerOutputs.Basal.ContractionActivation *
                (1.0 + (0.6 * carnivore.Response.Activation)), 0.0, 1.0)
        };
        carnivore.Body.UpdateFunctionalState(
            carnivore.Genome, driven, 0.8, 1.0, default, 0.1);
        BodyMechanicsResult mechanics = pose.Step(
            carnivore.Genome, carnivore.Body, driven, 1.0, 1.0, 1.0, config, 0.1);
        bool mechanicsExecutes = mechanics.MediumVelocity.Length() > 1e-7f ||
            Math.Abs(mechanics.AngularVelocity) > 1e-7;

        bool passed = injuryTurnsAway && noPerception && motorRequired && energyRequired &&
            memoryExpires && doesNotTrack && herbivoreDoesNotApproach && herbivoreCanAvoid &&
            carnivoreApproaches && mechanicsExecutes;
        return new SocialBehaviorDiagnosticResult(
            rightInjury.Steering,
            leftInjury.Steering,
            carnivore.Response.Steering,
            carnivore.Response.Activation,
            mechanics.MediumVelocity.Length(),
            Math.Abs(mechanics.AngularVelocity),
            injuryTurnsAway,
            noPerception,
            motorRequired,
            energyRequired,
            memoryExpires,
            doesNotTrack,
            herbivoreDoesNotApproach,
            herbivoreCanAvoid,
            carnivoreApproaches,
            mechanicsExecutes,
            passed);
    }

    private static SocialMotorResponse InjuryResponse(
        Genome genome,
        Vector2 origin,
        Vector2 sourceOffset,
        SimulationConfig config)
    {
        DevelopingBody body = CreateBody(genome, 2.0);
        SocialMemory memory = new();
        memory.RecordInjury(70, SphericalWorld.OffsetPosition(
            origin, sourceOffset, config.WorldSize), 0.04);
        return memory.Respond(body, genome, origin, 0.0, config, 0.1);
    }

    private static ResponseFixture PerceivedResponse(
        double affinity,
        float sizeRatio,
        Vector2 origin,
        SimulationConfig config,
        IEnvironmentField environment)
    {
        Genome genome = CreateGenome(social: true, contact: false, affinity, motor: true);
        DevelopingBody body = CreateBody(genome, 2.0);
        float ownRadius = (float)Math.Max(0.04, body.Cache.BoundingRadius);
        SocialCandidate candidate = new(
            55,
            SphericalWorld.OffsetPosition(origin, new Vector2(4f, 2f), config.WorldSize),
            0f,
            ownRadius * sizeRatio,
            0.2f);
        SocialPerceptionResult sight = SocialPerception.Evaluate(
            body, genome, environment, origin, 0f, 0.0, [candidate], config, 0.1);
        SocialMemory memory = new();
        memory.Observe(sight);
        SocialMotorResponse response = memory.Respond(
            body, genome, origin, 0.0, config, 0.1);
        return new(genome, body, sight, response);
    }

    private static SocialPerceptionResult SyntheticSight(Vector2 origin) =>
        new(
            0.0,
            1,
            77,
            SphericalWorld.OffsetPosition(origin, new Vector2(3f, 1f), 512f),
            0f,
            0.5,
            0.2,
            0.5,
            0.7,
            0.2,
            1.0)
        {
            ActivationGain = 0.5
        };

    private static Genome CreateGenome(
        bool social,
        bool contact,
        double affinity,
        bool motor)
    {
        Genome basis = Genome.CreateAncestor();
        RegionGene[] regions = basis.Regions.Select(gene => gene with
        {
            SensoryExpression = 0.9,
            LightReactivity = 0.9,
            FeedingExpression = 0.85,
            DigestiveExpression = 0.85,
            ContractileExpression = 0.85
        }).ToArray();
        ControllerNodeGene[] nodes = basis.ControllerNodes.ToArray();
        nodes[0] = nodes[0] with
        {
            ContractionOutputWeight = motor ? 0.9 : 0.0,
            LateralContractionOutputWeight = motor ? 0.8 : 0.0
        };
        List<SensorGene> sensors = [];
        if (social)
            sensors.Add(new SensorGene(
                SensorChannel.OrganismContrast,
                regions[0].RegionId,
                0,
                1.0,
                8.0,
                DirectionOffsetRadians: 0.0,
                Range: 8.0,
                DirectionalSelectivity: 0.55,
                ApproachWeight: 1.0,
                AvoidanceWeight: 0.8,
                TargetMemorySeconds: 1.5));
        if (contact)
            sensors.Add(new SensorGene(
                SensorChannel.ContactPressure,
                regions[0].RegionId,
                0,
                1.0,
                8.0,
                ApproachWeight: 0.0,
                AvoidanceWeight: 1.0,
                TargetMemorySeconds: 1.5));
        return new Genome(
            regions,
            0.0,
            basis.Metabolism with { AnimalFoodAffinity = affinity },
            nodes,
            sensors);
    }

    private static DevelopingBody CreateBody(Genome genome, double totalEnergy)
    {
        double energyPerRegion = totalEnergy / genome.Regions.Count;
        return new DevelopingBody(
            genome,
            genome.Regions.Select(gene => new BodyRegion(
                gene.RegionId,
                BodyCalculator.TargetMatter(gene),
                1.0,
                Substrate: 0.0,
                Oxygen: 1.0,
                Water: 1.0,
                Energy: energyPerRegion,
                TransportAvailability: 1.0,
                ExchangeExpression: gene.ExchangeExpression,
                BarrierExpression: gene.BarrierExpression,
                ContractileExpression: gene.ContractileExpression,
                StructuralExpression: gene.StructuralExpression,
                SensoryExpression: gene.SensoryExpression,
                PhotosyntheticExpression: gene.PhotosyntheticExpression,
                FeedingExpression: gene.FeedingExpression,
                DigestiveExpression: gene.DigestiveExpression,
                DecomposerExpression: gene.DecomposerExpression)),
            CorePrecisionProfile.Reference);
    }

    private readonly record struct ResponseFixture(
        Genome Genome,
        DevelopingBody Body,
        SocialPerceptionResult Sight,
        SocialMotorResponse Response);

    private sealed class ClearEnvironment : IEnvironmentField
    {
        public EnvironmentSample Sample(Vector2 position, float depth = 0f) => new(
            0.0, 0.0, 0.0, depth, 1.0, 0.8, 0.9,
            0.0, 0.0, 0.0, 0.7, 1.0, 1.0, Vector2.Zero);
    }
}
