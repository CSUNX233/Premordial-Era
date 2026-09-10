using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct ContactPredationDiagnosticResult(
    double Assimilated, double PreyLoss, double DetritusReturned, double MatterError,
    bool ContactRequired, bool DepthContactRequired, bool FeedingRequired,
    bool DigestionRequired, bool EnergyRequired, bool StructureNotDuplicated, bool Passed);

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
        bool contact = far.Result.AssimilatedOrganic == 0 && far.PreyLoss == 0;
        bool depth = deep.Result.AssimilatedOrganic == 0 && deep.PreyLoss == 0;
        bool feeding = noFeed.Result.AssimilatedOrganic == 0;
        bool digestion = noDigest.Result.AssimilatedOrganic == 0;
        bool energy = noEnergy.Result.AssimilatedOrganic == 0 && noEnergy.PreyLoss == 0;
        bool passed = active.Result.AssimilatedOrganic > 0 && active.PreyLoss > 0 &&
            Math.Abs(active.MatterError) < 1e-9 && active.StructureUnchanged &&
            contact && depth && feeding && digestion && energy;
        return new(active.Result.AssimilatedOrganic, active.PreyLoss, active.Result.ReturnedDetritus,
            active.MatterError, contact, depth, feeding, digestion, energy, active.StructureUnchanged, passed);
    }

    private static Trial RunTrial(bool feeding, bool digestion, double energy, float separation, float depth)
    {
        Genome basis = Genome.CreateAncestor();
        Genome genome = new(basis.Regions.Select(g => g with
        {
            FeedingExpression = feeding ? 1 : 0,
            DigestiveExpression = digestion ? 1 : 0,
            DecomposerExpression = 0
        }), basis.MutationRate, basis.Metabolism, basis.ControllerNodes, basis.Sensors);
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
            prey, genome, new Vector2(40 + separation, 40), 1 + depth, environment, 0.1);
        return new(result, preyBefore - prey.TotalSubstrate, Matter() - before,
            Math.Abs(prey.Cache.TotalMatter - structure) < 1e-12);
    }

    private readonly record struct Trial(PredationResult Result, double PreyLoss, double MatterError,
        bool StructureUnchanged);
}
