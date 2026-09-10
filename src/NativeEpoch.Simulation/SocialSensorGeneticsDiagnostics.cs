namespace NativeEpoch.Simulation;

public readonly record struct SocialSensorGeneticsDiagnosticResult(
    bool LegacyConstructorDefaults,
    bool FoundersHaveNoAdvancedVision,
    bool FoundersArePlantOnly,
    bool FingerprintIncludesBehaviorWeights,
    bool FingerprintIncludesAnimalFoodAffinity,
    bool FingerprintIncludesCombatAffinities,
    bool ValidationAcceptsSignedWeights,
    bool ValidationRejectsOutOfRangeValues,
    bool PointMutationIsSingleScalar,
    bool AllBehaviorScalarsMutate,
    bool AnimalFoodAffinityMutates,
    bool CombatAffinitiesMutate,
    bool RandomSensorCanCreateOrganismContrast,
    bool ExactInheritancePreservesSensor,
    int PointSamples,
    int RandomSensorSamples,
    bool Passed);

/// <summary>Bounded heredity checks for directional social sensing parameters.</summary>
public static class SocialSensorGeneticsDiagnostics
{
    public static SocialSensorGeneticsDiagnosticResult Run()
    {
        Genome ancestor = Genome.CreateAncestor();
        SensorGene legacy = new(SensorChannel.ContactPressure, 0, 0, 0.8, 2.0,
            0.25, 3.0, 0.7);
        bool legacyDefaults = legacy.ApproachWeight == 0.0 &&
            legacy.AvoidanceWeight == 0.5 && legacy.TargetMemorySeconds == 1.5;

        bool foundersHaveNoAdvancedVision = ancestor.Sensors.All(sensor =>
            sensor.Channel is not SensorChannel.DirectionalLight and not SensorChannel.OrganismContrast);
        bool foundersArePlantOnly = ancestor.Metabolism.AnimalFoodAffinity == 0.0 &&
            ancestor.Metabolism.AttackAffinity == 0.0 &&
            ancestor.Metabolism.RetaliationAffinity == 0.0 &&
            ancestor.Regions.All(region => region.DecomposerExpression == 0.0);
        DeterministicRandom founderRandom = new(0x50C1A1UL, 91);
        for (int index = 0; index < 128; index++)
        {
            Genome founder = AquaticFounderFactory.Create(founderRandom);
            foundersHaveNoAdvancedVision &= founder.Sensors.All(sensor =>
                sensor.Channel is not SensorChannel.DirectionalLight and not SensorChannel.OrganismContrast);
            foundersArePlantOnly &= founder.Metabolism.AnimalFoodAffinity == 0.0 &&
                founder.Metabolism.AttackAffinity == 0.0 &&
                founder.Metabolism.RetaliationAffinity == 0.0 &&
                founder.Regions.All(region => region.DecomposerExpression == 0.0) &&
                founder.Regions.All(region => region.PhotosyntheticExpression > 0.0 &&
                    region.FeedingExpression > 0.0 && region.DigestiveExpression > 0.0);
        }

        SensorGene social = legacy with
        {
            Channel = SensorChannel.OrganismContrast,
            ApproachWeight = 0.35,
            AvoidanceWeight = -0.45,
            TargetMemorySeconds = 3.25
        };
        Genome template = WithSingleSensor(ancestor, social, mutationRate: 0.0);
        bool fingerprintIncludes = template.Fingerprint !=
                WithSingleSensor(ancestor, social with { ApproachWeight = 0.36 }, 0.0).Fingerprint &&
            template.Fingerprint !=
                WithSingleSensor(ancestor, social with { AvoidanceWeight = -0.44 }, 0.0).Fingerprint &&
            template.Fingerprint !=
                WithSingleSensor(ancestor, social with { TargetMemorySeconds = 3.26 }, 0.0).Fingerprint;
        Genome animalAffinity = WithMetabolism(ancestor,
            ancestor.Metabolism with { AnimalFoodAffinity = 0.35 });
        bool fingerprintIncludesAffinity = ancestor.Fingerprint != animalAffinity.Fingerprint;
        bool fingerprintIncludesCombat = ancestor.Fingerprint != WithMetabolism(ancestor,
                ancestor.Metabolism with { AttackAffinity = 0.35 }).Fingerprint &&
            ancestor.Fingerprint != WithMetabolism(ancestor,
                ancestor.Metabolism with { RetaliationAffinity = 0.35 }).Fingerprint;

        bool signedWeightsAccepted = Accepts(ancestor, social with
            { ApproachWeight = -1.0, AvoidanceWeight = 1.0, TargetMemorySeconds = 0.0 }) &&
            Accepts(ancestor, social with
            { ApproachWeight = 1.0, AvoidanceWeight = -1.0, TargetMemorySeconds = 8.0 });
        bool rejectsBounds = !Accepts(ancestor, social with { ApproachWeight = -1.0001 }) &&
            !Accepts(ancestor, social with { AvoidanceWeight = 1.0001 }) &&
            !Accepts(ancestor, social with { TargetMemorySeconds = -0.0001 }) &&
            !Accepts(ancestor, social with { TargetMemorySeconds = 8.0001 }) &&
            !Accepts(ancestor, ancestor.Metabolism with { AnimalFoodAffinity = -0.0001 }) &&
            !Accepts(ancestor, ancestor.Metabolism with { AnimalFoodAffinity = 1.0001 }) &&
            !Accepts(ancestor, ancestor.Metabolism with { AttackAffinity = -0.0001 }) &&
            !Accepts(ancestor, ancestor.Metabolism with { AttackAffinity = 1.0001 }) &&
            !Accepts(ancestor, ancestor.Metabolism with { RetaliationAffinity = -0.0001 }) &&
            !Accepts(ancestor, ancestor.Metabolism with { RetaliationAffinity = 1.0001 });

        Genome pointParent = WithSingleSensor(ancestor, social, mutationRate: 0.22);
        GenomeMutator mutator = new();
        DeterministicRandom pointRandom = new(0xA7701DUL, 92);
        bool pointSparse = true;
        bool approachMutated = false, avoidanceMutated = false, memoryMutated = false;
        const int pointSamples = 512;
        for (int index = 0; index < pointSamples; index++)
        {
            MutationResult result = mutator.MutateSensorForced(
                pointParent, pointRandom, MutationKind.SensorPoint);
            SensorGene changed = result.Genome.Sensors[0];
            int differences = ScalarDifferences(social, changed);
            pointSparse &= result.Kind == MutationKind.SensorPoint && differences == 1;
            approachMutated |= changed.ApproachWeight != social.ApproachWeight;
            avoidanceMutated |= changed.AvoidanceWeight != social.AvoidanceWeight;
            memoryMutated |= changed.TargetMemorySeconds != social.TargetMemorySeconds;
            try { GenomeValidator.Validate(result.Genome); }
            catch (InvalidOperationException) { pointSparse = false; }
        }
        bool allBehaviorScalarsMutate = approachMutated && avoidanceMutated && memoryMutated;

        DeterministicRandom affinityRandom = new(0xA11FA17UL, 96);
        bool affinityMutates = false, attackMutates = false, retaliationMutates = false;
        for (int index = 0; index < 768 &&
            !(affinityMutates && attackMutates && retaliationMutates); index++)
        {
            MutationResult result = mutator.MutateForced(
                ancestor, affinityRandom, MutationKind.MetabolicPoint);
            affinityMutates |= result.Genome.Metabolism.AnimalFoodAffinity > 0.0;
            attackMutates |= result.Genome.Metabolism.AttackAffinity > 0.0;
            retaliationMutates |= result.Genome.Metabolism.RetaliationAffinity > 0.0;
        }
        bool combatAffinitiesMutate = attackMutates && retaliationMutates;

        Genome empty = new(ancestor.Regions, ancestor.MutationRate, ancestor.Metabolism,
            ancestor.ControllerNodes, sensors: []);
        DeterministicRandom addRandom = new(0xC0172A57UL, 93);
        bool createdContrast = false, randomValuesValid = true;
        const int randomSamples = 256;
        for (int index = 0; index < randomSamples; index++)
        {
            MutationResult result = mutator.MutateSensorForced(
                empty, addRandom, MutationKind.SensorReconnect);
            SensorGene added = result.Genome.Sensors.Single();
            createdContrast |= added.Channel == SensorChannel.OrganismContrast;
            randomValuesValid &= added.ApproachWeight is >= -1.0 and <= 1.0 &&
                added.AvoidanceWeight is >= -1.0 and <= 1.0 &&
                added.TargetMemorySeconds is >= 0.0 and <= 8.0;
        }

        DeterministicRandom inheritanceBody = new(0x1A11CEUL, 94);
        DeterministicRandom inheritanceController = new(0x1A11CEUL, 95);
        Genome inheritable = new(template.Regions, 0.0,
            template.Metabolism with
                { AnimalFoodAffinity = 0.42, AttackAffinity = 0.37, RetaliationAffinity = 0.61 },
            template.ControllerNodes, template.Sensors);
        MutationResult inherited = mutator.Inherit(inheritable, inheritanceBody, inheritanceController);
        bool exactInheritance = inherited.Kind == MutationKind.None &&
            inherited.Genome.Fingerprint == inheritable.Fingerprint &&
            inherited.Genome.Sensors.Single().Equals(social) &&
            inherited.Genome.Metabolism.AnimalFoodAffinity == 0.42 &&
            inherited.Genome.Metabolism.AttackAffinity == 0.37 &&
            inherited.Genome.Metabolism.RetaliationAffinity == 0.61;

        bool randomSensorCreatesContrast = createdContrast && randomValuesValid;
        bool passed = legacyDefaults && foundersHaveNoAdvancedVision && foundersArePlantOnly &&
            fingerprintIncludes && fingerprintIncludesAffinity && fingerprintIncludesCombat &&
            signedWeightsAccepted && rejectsBounds && pointSparse && allBehaviorScalarsMutate &&
            affinityMutates && combatAffinitiesMutate && randomSensorCreatesContrast && exactInheritance;
        return new(legacyDefaults, foundersHaveNoAdvancedVision, foundersArePlantOnly,
            fingerprintIncludes, fingerprintIncludesAffinity, fingerprintIncludesCombat,
            signedWeightsAccepted, rejectsBounds, pointSparse, allBehaviorScalarsMutate, affinityMutates,
            combatAffinitiesMutate,
            randomSensorCreatesContrast, exactInheritance, pointSamples, randomSamples, passed);
    }

    private static Genome WithSingleSensor(
        Genome ancestor, SensorGene sensor, double mutationRate) =>
        new(ancestor.Regions, mutationRate, ancestor.Metabolism, ancestor.ControllerNodes, [sensor]);

    private static Genome WithMetabolism(Genome ancestor, MetabolicGene metabolism) =>
        new(ancestor.Regions, ancestor.MutationRate, metabolism,
            ancestor.ControllerNodes, ancestor.Sensors);

    private static bool Accepts(Genome ancestor, SensorGene sensor)
    {
        try
        {
            _ = WithSingleSensor(ancestor, sensor, ancestor.MutationRate);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Accepts(Genome ancestor, MetabolicGene metabolism)
    {
        try
        {
            _ = WithMetabolism(ancestor, metabolism);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int ScalarDifferences(SensorGene left, SensorGene right)
    {
        double[] a =
        [
            left.Gain, left.ResponseRate, left.DirectionOffsetRadians, left.Range,
            left.DirectionalSelectivity, left.ApproachWeight, left.AvoidanceWeight,
            left.TargetMemorySeconds
        ];
        double[] b =
        [
            right.Gain, right.ResponseRate, right.DirectionOffsetRadians, right.Range,
            right.DirectionalSelectivity, right.ApproachWeight, right.AvoidanceWeight,
            right.TargetMemorySeconds
        ];
        int changed = 0;
        for (int index = 0; index < a.Length; index++)
            if (a[index] != b[index]) changed++;
        return changed;
    }
}
