using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SocialWorldDiagnosticResult(
    bool VisibleTargetSelected,
    bool ApproachResponseProduced,
    bool SocialControllerOutputDiffers,
    bool BlindControlHasNoTarget,
    bool ContactPredationOccurred,
    bool PredationInjuryRecorded,
    bool WorldRemainsFinite,
    ulong VisibleTargetId,
    double SocialLateralOutput,
    double BlindLateralOutput,
    double PredationAssimilated,
    double InjuryStrength,
    bool Passed);

/// <summary>
/// Short end-to-end checks through SimulationWorld.Step for inherited outline
/// sensing, social motor drive, and injury memory written by paid predation.
/// </summary>
public static class SocialWorldDiagnostics
{
    private const ulong Seed = 0x50C1A1UL;

    public static SocialWorldDiagnosticResult Run()
    {
        SimulationConfig config = TestConfig();
        Genome socialGenome = TestGenome(includeOrganismSensor: true);
        Genome blindGenome = TestGenome(includeOrganismSensor: false);

        SimulationWorld social = CreateSeparatedPair(config, socialGenome);
        SimulationWorld blind = CreateSeparatedPair(config, blindGenome);
        ulong observerId = social.Organisms[0].Id;
        ulong expectedTargetId = social.Organisms[1].Id;

        Organism socialObserver = social.Organisms[0];
        Organism blindObserver = blind.Organisms[0];
        bool visibleTargetSelected = false;
        bool approachProduced = false;
        bool controllerDiffers = false;
        for (int step = 0; step < 10; step++)
        {
            social.Step();
            blind.Step();
            if (!social.TryGetOrganism(observerId, out socialObserver) ||
                !blind.TryGetOrganism(observerId, out blindObserver))
                break;
            visibleTargetSelected |= socialObserver.SocialTargetId == expectedTargetId &&
                socialObserver.ActiveIndividualSensors > 0;
            approachProduced |= socialObserver.SocialResponse == SocialResponse.Approach;
            controllerDiffers |= Math.Abs(socialObserver.ControllerOutputs.LateralContraction -
                blindObserver.ControllerOutputs.LateralContraction) > 1e-5;
        }
        bool blindHasNoTarget = blindObserver.SocialTargetId == 0 &&
            blindObserver.ActiveIndividualSensors == 0;

        SimulationWorld contact = CreateContactPair(config, socialGenome);
        contact.Step();
        double strongestInjury = 0.0;
        bool injuryRecorded = false;
        foreach (Organism organism in contact.Organisms)
        {
            strongestInjury = Math.Max(strongestInjury, organism.SocialMemory.InjuryStrength);
            injuryRecorded |= organism.SocialMemory.InjurySourceId != 0 &&
                organism.SocialMemory.InjuryStrength > 0.0;
        }
        bool predationOccurred = contact.CumulativePredationOrganic > 0.0;
        bool finite = social.CaptureSnapshot().AllFinite && blind.CaptureSnapshot().AllFinite &&
            contact.CaptureSnapshot().AllFinite;
        bool passed = visibleTargetSelected && approachProduced && controllerDiffers &&
            blindHasNoTarget && predationOccurred && injuryRecorded && finite;
        return new(visibleTargetSelected, approachProduced, controllerDiffers,
            blindHasNoTarget, predationOccurred, injuryRecorded, finite,
            socialObserver.SocialTargetId, socialObserver.ControllerOutputs.LateralContraction,
            blindObserver.ControllerOutputs.LateralContraction,
            contact.CumulativePredationOrganic, strongestInjury, passed);
    }

    private static SimulationWorld CreateSeparatedPair(SimulationConfig config, Genome genome)
    {
        SimulationWorld world = new(config, Seed, 2, genome, mutationsEnabled: false);
        Organism observer = world.Organisms[0];
        Vector2 target = SphericalWorld.OffsetPosition(
            observer.Position, new Vector2(1.8f, 1.2f), config.WorldSize);
        world.RelocateForMediumDiagnostic(observer.Id, observer.Position, observer.Depth, 0.0);
        world.RelocateForMediumDiagnostic(world.Organisms[1].Id, target, observer.Depth, Math.PI);
        return world;
    }

    private static SimulationWorld CreateContactPair(SimulationConfig config, Genome genome)
    {
        SimulationWorld world = new(config, Seed + 1, 2, genome, mutationsEnabled: false);
        Organism predator = world.Organisms[0];
        Vector2 contact = SphericalWorld.OffsetPosition(
            predator.Position, new Vector2(0.02f, 0.015f), config.WorldSize);
        world.RelocateForMediumDiagnostic(predator.Id, predator.Position, predator.Depth, 0.0);
        world.RelocateForMediumDiagnostic(world.Organisms[1].Id, contact, predator.Depth, Math.PI);
        return world;
    }

    private static SimulationConfig TestConfig() => new()
    {
        WorldSize = 96f,
        EnvironmentGridSize = 25,
        InitialMineralScale = 0.2,
        RandomizeFounders = false,
        AncestorStoredMatter = 0.05,
        AncestorEnergy = 2.5,
        MaximumEnergy = 3.0,
        ReproductionEnergyThreshold = 100.0,
        MaxPopulation = 8
    };

    private static Genome TestGenome(bool includeOrganismSensor)
    {
        Genome basis = Genome.CreateAncestor();
        RegionGene core = basis.Regions.Single(region => region.IsCore) with
        {
            Contractility = 0.9,
            LightReactivity = 1.0,
            SensoryExpression = 1.0,
            FeedingExpression = 1.0,
            DigestiveExpression = 1.0,
            DecomposerExpression = 0.0
        };
        ControllerNodeGene controller = basis.ControllerNodes[0] with
        {
            Bias = 0.35,
            RecurrentSourceIndex = -1,
            RecurrentWeight = 0.0,
            ContractionOutputWeight = 1.0,
            LateralContractionOutputWeight = 1.0,
            VerticalContractionOutputWeight = 0.0
        };
        SensorGene contact = new(
            SensorChannel.ContactPressure, core.RegionId, 0, 1.0, 8.0,
            DirectionOffsetRadians: 0.0, Range: 2.0, DirectionalSelectivity: 0.0,
            ApproachWeight: 0.0, AvoidanceWeight: 1.0, TargetMemorySeconds: 2.0);
        SensorGene organism = new(
            SensorChannel.OrganismContrast, core.RegionId, 0, 1.0, 8.0,
            DirectionOffsetRadians: 0.0, Range: 6.0, DirectionalSelectivity: 0.25,
            ApproachWeight: 1.0, AvoidanceWeight: 0.0, TargetMemorySeconds: 2.0);
        SensorGene[] sensors = includeOrganismSensor ? [contact, organism] : [contact];
        return new Genome([core], 0.0,
            basis.Metabolism with
                { AnimalFoodAffinity = 1.0, AttackAffinity = 1.0, RetaliationAffinity = 1.0 },
            [controller], sensors);
    }
}
