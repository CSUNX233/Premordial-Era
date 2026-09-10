using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct ContactPredationDiagnosticResult(
    double Assimilated, double PreyLoss, double DetritusReturned, double MatterError,
    bool ContactRequired, bool DepthContactRequired, bool FeedingRequired,
    bool DigestionRequired, bool EnergyRequired, bool UnarmedHerbivoreCannotAttack,
    bool CarnivoreYieldCapped, bool StructureNotDuplicated, bool Passed)
{
    public double WorldAssimilated { get; init; }
    public double WorldMatterError { get; init; }
    public bool WorldContactProducesPredation { get; init; }
    public bool WorldSeparationPreventsPredation { get; init; }
    public bool AttackAffinityRequired { get; init; }
    public bool AggressiveHerbivoreDamagesWithoutAssimilation { get; init; }
    public bool RetaliationAffinityProducesCounterattack { get; init; }
}

public static class ContactPredationDiagnostics
{
    public static ContactPredationDiagnosticResult Run()
    {
        Trial active = RunTrial(true, true, 20, 0.1f, 0);
        Trial far = RunTrial(true, true, 20, 50, 0);
        Trial deep = RunTrial(true, true, 20, 0.1f, 50);
        Trial noFeed = RunTrial(false, true, 20, 0.1f, 0);
        Trial noDigest = RunTrial(true, false, 20, 0.1f, 0);
        Trial noEnergy = RunTrial(true, true, 0, 0.1f, 0);
        Trial herbivore = RunTrial(true, true, 20, 0.1f, 0,
            animalFoodAffinity: 0.0, attackAffinity: 0.0);
        Trial noAttack = RunTrial(true, true, 20, 0.1f, 0,
            animalFoodAffinity: 1.0, attackAffinity: 0.0);
        Trial aggressiveHerbivore = RunTrial(true, true, 20, 0.1f, 0,
            animalFoodAffinity: 0.0, attackAffinity: 1.0);
        Trial retaliation = RunTrial(true, true, 20, 0.1f, 0,
            animalFoodAffinity: 0.0, attackAffinity: 0.0,
            retaliationAffinity: 1.0, retaliation: true);
        bool contact = far.Result.AssimilatedOrganic == 0 && far.PreyLoss == 0;
        bool depth = deep.Result.AssimilatedOrganic == 0 && deep.PreyLoss == 0;
        bool feeding = noFeed.Result.AssimilatedOrganic == 0;
        bool digestion = noDigest.Result.AssimilatedOrganic == 0;
        bool energy = noEnergy.Result.AssimilatedOrganic == 0 && noEnergy.PreyLoss == 0;
        bool unarmedHerbivoreCannotAttack = herbivore.Result.AssimilatedOrganic == 0 &&
            herbivore.PreyLoss == 0 && herbivore.Result.EnergySpent == 0 &&
            herbivore.Result.Damage == 0;
        bool attackRequired = noAttack.Result.AssimilatedOrganic == 0 &&
            noAttack.PreyLoss == 0 && noAttack.Result.EnergySpent == 0 &&
            noAttack.Result.Damage == 0;
        bool herbivoreDamage = aggressiveHerbivore.Result.AssimilatedOrganic == 0 &&
            aggressiveHerbivore.PreyLoss == 0 && aggressiveHerbivore.Result.EnergySpent > 0 &&
            aggressiveHerbivore.Result.Damage > 0;
        bool retaliationWorks = retaliation.Result.AssimilatedOrganic == 0 &&
            retaliation.PreyLoss == 0 && retaliation.Result.EnergySpent > 0 &&
            retaliation.Result.Damage > 0;
        bool yieldCapped = active.Result.RemovedOrganic > 0 &&
            active.Result.AssimilatedOrganic <= active.Result.RemovedOrganic * 0.20 + 1e-12;
        WorldTrial world = RunWorldTrial();
        bool passed = active.Result.AssimilatedOrganic > 0 && active.PreyLoss > 0 &&
            Math.Abs(active.MatterError) < 1e-9 && active.StructureUnchanged &&
            contact && depth && feeding && digestion && energy && unarmedHerbivoreCannotAttack &&
            attackRequired && herbivoreDamage && retaliationWorks &&
            yieldCapped && world.ContactAssimilated > 0.0 &&
            world.SeparatedAssimilated == 0.0 && Math.Abs(world.MatterError) < 1e-9;
        return new(active.Result.AssimilatedOrganic, active.PreyLoss, active.Result.ReturnedDetritus,
            active.MatterError, contact, depth, feeding, digestion, energy, unarmedHerbivoreCannotAttack,
            yieldCapped, active.StructureUnchanged, passed)
        {
            WorldAssimilated = world.ContactAssimilated,
            WorldMatterError = world.MatterError,
            WorldContactProducesPredation = world.ContactAssimilated > 0.0,
            WorldSeparationPreventsPredation = world.SeparatedAssimilated == 0.0,
            AttackAffinityRequired = attackRequired,
            AggressiveHerbivoreDamagesWithoutAssimilation = herbivoreDamage,
            RetaliationAffinityProducesCounterattack = retaliationWorks
        };
    }

    private static WorldTrial RunWorldTrial()
    {
        Genome genome = PredatoryGenome();
        SimulationConfig config = new()
        {
            RandomizeFounders = false,
            AncestorStoredMatter = 0.30,
            AncestorEnergy = 20.0,
            MaximumEnergy = 25.0,
            ReproductionEnergyThreshold = 100.0
        };
        SimulationWorld touching = new(config, 0xB17EUL, 2, genome, mutationsEnabled: false);
        Vector2 contactPosition = touching.Organisms[0].Position;
        float contactDepth = touching.Organisms[0].Depth;
        touching.RelocateForMediumDiagnostic(
            touching.Organisms[1].Id, contactPosition, contactDepth, Math.PI);
        touching.Step();
        double touchingAssimilated = touching.CumulativePredationOrganic;
        double matterError = touching.CaptureSnapshot().MatterError;

        SimulationWorld separated = new(config, 0xB17EUL, 2, genome, mutationsEnabled: false);
        Vector2 firstPosition = separated.Organisms[0].Position;
        float firstDepth = separated.Organisms[0].Depth;
        Vector2 farPosition = SphericalWorld.OffsetPosition(
            firstPosition, new Vector2(20f, 0f), config.WorldSize);
        separated.RelocateForMediumDiagnostic(
            separated.Organisms[1].Id, farPosition, firstDepth, Math.PI);
        separated.Step();
        return new(touchingAssimilated, separated.CumulativePredationOrganic, matterError);
    }

    private static Genome PredatoryGenome()
    {
        Genome basis = Genome.CreateAncestor();
        return new Genome(basis.Regions.Select(g => g with
        {
            FeedingExpression = 1.0,
            DigestiveExpression = 1.0,
            DecomposerExpression = 0.0
        }), basis.MutationRate, basis.Metabolism with
            { AnimalFoodAffinity = 1.0, AttackAffinity = 1.0 },
            basis.ControllerNodes, basis.Sensors);
    }

    private static Trial RunTrial(bool feeding, bool digestion, double energy, float separation, float depth,
        double animalFoodAffinity = 1.0, double attackAffinity = 1.0,
        double retaliationAffinity = 0.0, bool retaliation = false)
    {
        Genome basis = Genome.CreateAncestor();
        Genome genome = new(basis.Regions.Select(g => g with
        {
            FeedingExpression = feeding ? 1 : 0,
            DigestiveExpression = digestion ? 1 : 0,
            DecomposerExpression = 0
        }), basis.MutationRate, basis.Metabolism with
            {
                AnimalFoodAffinity = animalFoodAffinity,
                AttackAffinity = attackAffinity,
                RetaliationAffinity = retaliationAffinity
            },
            basis.ControllerNodes, basis.Sensors);
        DevelopingBody Make(double stored, double availableEnergy) => new(genome,
            genome.Regions.Select(g => new BodyRegion(g.RegionId, BodyCalculator.TargetMatter(g), 1,
                Substrate: stored, Oxygen: 1, Water: 1, Energy: availableEnergy,
                TransportAvailability: 1, FeedingExpression: feeding ? 1 : 0,
                DigestiveExpression: digestion ? 1 : 0)).ToArray());
        DevelopingBody predator = Make(0, energy), prey = Make(0.3, 20);
        BilinearEnvironmentField environment = new(new SimulationConfig(), new DeterministicRandom(9123, 1));
        double Matter() => environment.TotalEnvironmentMatter + predator.Cache.TotalMatter +
            prey.Cache.TotalMatter + predator.TotalSubstrate + prey.TotalSubstrate;
        double before = Matter(), preyBefore = prey.TotalSubstrate, structure = prey.Cache.TotalMatter;
        PredationResult result = ContactPredation.Attempt(predator, genome, new Vector2(40), 1,
            prey, genome, new Vector2(40 + separation, 40), 1 + depth, environment, 0.1,
            retaliation: retaliation);
        return new(result, preyBefore - prey.TotalSubstrate, Matter() - before,
            Math.Abs(prey.Cache.TotalMatter - structure) < 1e-12);
    }

    private readonly record struct Trial(PredationResult Result, double PreyLoss, double MatterError,
        bool StructureUnchanged);
    private readonly record struct WorldTrial(
        double ContactAssimilated, double SeparatedAssimilated, double MatterError);
}
