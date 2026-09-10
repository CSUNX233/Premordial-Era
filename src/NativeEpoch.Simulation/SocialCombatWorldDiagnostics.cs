using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SocialCombatWorldDiagnosticResult(
    double RetaliationDamage,
    double RetaliationEnergySpent,
    double MatterError,
    bool DefenderIsPlantOnlyRetaliator,
    bool HerbivoreRetaliatedAfterDamage,
    bool ContactWithoutDamageDidNotRetaliate,
    bool SeparationPreventedRetaliation,
    bool ZeroRetaliationTraitDidNotRetaliate,
    bool NoOrganicAssimilation,
    bool RetaliationPaidEnergy,
    bool MatterConserved,
    bool Finite,
    bool Passed);

/// <summary>Short world-step checks for inherited, damage-gated retaliation.</summary>
public static class SocialCombatWorldDiagnostics
{
    public static SocialCombatWorldDiagnosticResult Run()
    {
        Trial active = RunTrial(attackerAffinity: 1.0, retaliationAffinity: 1.0, touching: true);
        Trial noDamage = RunTrial(attackerAffinity: 0.0, retaliationAffinity: 1.0, touching: true);
        Trial separated = RunTrial(attackerAffinity: 1.0, retaliationAffinity: 1.0, touching: false);
        Trial zeroTrait = RunTrial(attackerAffinity: 1.0, retaliationAffinity: 0.0, touching: true);

        bool plantOnlyRetaliator = active.DefenderPlantOnlyRetaliator;
        bool retaliated = active.DefenderDamage > 0.0 && active.AttackerDamage > 0.0 &&
            active.DefenderPredationEnergy > 0.0;
        bool noDamageGate = noDamage.DefenderDamage == 0.0 && noDamage.AttackerDamage == 0.0 &&
            noDamage.DefenderPredationEnergy == 0.0;
        bool contactGate = separated.DefenderDamage == 0.0 && separated.AttackerDamage == 0.0 &&
            separated.DefenderPredationEnergy == 0.0;
        bool zeroTraitGate = zeroTrait.DefenderDamage > 0.0 && zeroTrait.AttackerDamage == 0.0 &&
            zeroTrait.DefenderPredationEnergy == 0.0;
        bool noAssimilation = active.PredationAssimilated == 0.0 &&
            noDamage.PredationAssimilated == 0.0 && separated.PredationAssimilated == 0.0 &&
            zeroTrait.PredationAssimilated == 0.0;
        bool energyPaid = active.DefenderEnergyAfter < active.DefenderEnergyBefore &&
            active.DefenderPredationEnergy > 0.0;
        double matterError = Math.Max(
            Math.Max(active.MatterError, noDamage.MatterError),
            Math.Max(separated.MatterError, zeroTrait.MatterError));
        bool conserved = matterError < 1e-9;
        bool finite = active.Finite && noDamage.Finite && separated.Finite && zeroTrait.Finite;
        bool passed = plantOnlyRetaliator && retaliated && noDamageGate && contactGate && zeroTraitGate &&
            noAssimilation && energyPaid && conserved && finite;
        return new SocialCombatWorldDiagnosticResult(
            active.AttackerDamage,
            active.DefenderPredationEnergy,
            matterError,
            plantOnlyRetaliator,
            retaliated,
            noDamageGate,
            contactGate,
            zeroTraitGate,
            noAssimilation,
            energyPaid,
            conserved,
            finite,
            passed);
    }

    private static Trial RunTrial(
        double attackerAffinity,
        double retaliationAffinity,
        bool touching)
    {
        Genome attackerGenome = CombatGenome(attackerAffinity, retaliationAffinity: 0.0);
        Genome defenderGenome = CombatGenome(attackerAffinity: 0.0, retaliationAffinity);
        bool defenderPlantOnlyRetaliator =
            defenderGenome.Metabolism.AnimalFoodAffinity == 0.0 &&
            defenderGenome.Metabolism.AttackAffinity == 0.0 &&
            defenderGenome.Metabolism.RetaliationAffinity == retaliationAffinity &&
            defenderGenome.Regions.All(region => region.DecomposerExpression == 0.0);
        SimulationConfig config = new()
        {
            RandomizeFounders = false,
            AncestorStoredMatter = 0.30,
            AncestorEnergy = 20.0,
            MaximumEnergy = 25.0,
            ReproductionEnergyThreshold = 100.0
        };
        SimulationWorld world = new(
            config, 0xC0B447UL, 2, attackerGenome, mutationsEnabled: false);
        SetGenome(world, organismIndex: 1, defenderGenome);

        ulong attackerId = world.Organisms[0].Id;
        ulong defenderId = world.Organisms[1].Id;
        Vector2 attackerPosition = world.Organisms[0].Position;
        float attackerDepth = world.Organisms[0].Depth;
        Vector2 defenderPosition = touching
            ? attackerPosition
            : SphericalWorld.OffsetPosition(attackerPosition, new Vector2(20f, 0f), config.WorldSize);
        world.RelocateForMediumDiagnostic(defenderId, defenderPosition, attackerDepth, Math.PI);

        double defenderEnergyBefore = Find(world, defenderId).Body.TotalEnergy;
        double attackerDamageBefore = Find(world, attackerId).Body.AverageDamage;
        double defenderDamageBefore = Find(world, defenderId).Body.AverageDamage;
        world.Step();
        Organism attacker = Find(world, attackerId);
        Organism defender = Find(world, defenderId);
        SimulationSnapshot snapshot = world.CaptureSnapshot();
        return new Trial(
            attacker.Body.AverageDamage - attackerDamageBefore,
            defender.Body.AverageDamage - defenderDamageBefore,
            defenderEnergyBefore,
            defender.Body.TotalEnergy,
            defender.PredationEnergyLastStep,
            world.CumulativePredationOrganic,
            Math.Abs(snapshot.MatterError),
            defenderPlantOnlyRetaliator,
            snapshot.AllFinite);
    }

    private static Genome CombatGenome(double attackerAffinity, double retaliationAffinity)
    {
        Genome basis = Genome.CreateAncestor();
        return new Genome(
            basis.Regions.Select(region => region with
            {
                FeedingExpression = 1.0,
                DigestiveExpression = 1.0,
                DecomposerExpression = 0.0
            }),
            basis.MutationRate,
            basis.Metabolism with
            {
                AnimalFoodAffinity = 0.0,
                AttackAffinity = attackerAffinity,
                RetaliationAffinity = retaliationAffinity
            },
            basis.ControllerNodes,
            basis.Sensors);
    }

    private static void SetGenome(SimulationWorld world, int organismIndex, Genome genome)
    {
        // SimulationWorld deliberately exposes organisms as a read-only view. This
        // fixture changes one value-type entry so two inherited combat strategies
        // can meet in the same deterministic world without adding a production API.
        List<Organism> organisms = (List<Organism>)world.Organisms;
        Organism organism = organisms[organismIndex];
        organism.GenomeId = world.Genomes.Register(genome);
        organisms[organismIndex] = organism;
    }

    private static Organism Find(SimulationWorld world, ulong organismId) =>
        world.Organisms.First(organism => organism.Id == organismId);

    private readonly record struct Trial(
        double AttackerDamage,
        double DefenderDamage,
        double DefenderEnergyBefore,
        double DefenderEnergyAfter,
        double DefenderPredationEnergy,
        double PredationAssimilated,
        double MatterError,
        bool DefenderPlantOnlyRetaliator,
        bool Finite);
}
