namespace NativeEpoch.Simulation;

public readonly record struct MutationDistributionDiagnosticResult(
    int Samples,
    double ExpectedAnyProbability,
    double ObservedAnyProbability,
    double ConditionalOneEventFraction,
    double ConditionalTwoEventFraction,
    double ConditionalThreeEventFraction,
    double LocalScalarEventFraction,
    double StructuralEventFraction,
    double RareLargeEventFraction,
    int SingleLocalSamples,
    int SingleLocalSparsePasses,
    int SensorPointSamples,
    int SensorPointSparsePasses,
    int InvalidMetadataResults,
    int UnchangedReportedMutations,
    int InvalidGenomeResults,
    bool ZeroRateExactInheritance,
    bool Passed);

/// <summary>
/// Bounded distribution assay. Every child is sampled from the same fixed
/// parent, so structural events cannot recursively grow an unbounded genome.
/// </summary>
public static class MutationDistributionDiagnostics
{
    public const int DefaultSamples = 10_000;

    public static MutationDistributionDiagnosticResult Run(int samples = DefaultSamples)
    {
        if (samples is < 5_000 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(samples), "Use 5,000-10,000 fixed-parent samples.");

        Genome parent = Genome.CreateAncestor();
        GenomeMutator mutator = new();
        DeterministicRandom bodyRandom = new(0xD15A71B0UL, 31);
        DeterministicRandom controllerRandom = new(0xC011EC7UL, 32);
        int[] counts = new int[4];
        int localEvents = 0, structuralEvents = 0, rareLargeEvents = 0;
        int singleLocalSamples = 0, singleLocalSparsePasses = 0;
        int sensorPointSamples = 0, sensorPointSparsePasses = 0;
        int invalidMetadata = 0, unchangedReported = 0, invalidGenomes = 0;

        for (int sample = 0; sample < samples; sample++)
        {
            MutationResult result = mutator.Inherit(parent, bodyRandom, controllerRandom);
            if (result.EventCount is < 0 or > 3 ||
                result.EventKinds.Count != result.EventCount ||
                (result.EventCount == 0) != (result.Kind == MutationKind.None))
                invalidMetadata++;
            else counts[result.EventCount]++;

            if (result.EventCount > 0 && result.Genome.Fingerprint == parent.Fingerprint)
                unchangedReported++;
            try { GenomeValidator.Validate(result.Genome); }
            catch (InvalidOperationException) { invalidGenomes++; }

            foreach (MutationKind kind in result.EventKinds)
            {
                if (IsLocalScalar(kind)) localEvents++;
                else structuralEvents++;
            }
            rareLargeEvents += CountOccurrences(result.Summary, "rare large");

            if (result.EventCount == 1 && IsLocalScalar(result.Kind))
            {
                singleLocalSamples++;
                if (CountChangedScalarDimensions(parent, result.Genome) == 1)
                    singleLocalSparsePasses++;
            }
            if (result.EventCount == 1 && result.Kind == MutationKind.SensorPoint)
            {
                sensorPointSamples++;
                if (CountChangedSensorScalars(parent, result.Genome) == 1 &&
                    SensorStructureEqual(parent, result.Genome))
                    sensorPointSparsePasses++;
            }
        }

        int mutated = counts[1] + counts[2] + counts[3];
        int totalEvents = localEvents + structuralEvents;
        double observedAny = mutated / (double)samples;
        double one = mutated > 0 ? counts[1] / (double)mutated : 0.0;
        double two = mutated > 0 ? counts[2] / (double)mutated : 0.0;
        double three = mutated > 0 ? counts[3] / (double)mutated : 0.0;
        double local = totalEvents > 0 ? localEvents / (double)totalEvents : 0.0;
        double structural = totalEvents > 0 ? structuralEvents / (double)totalEvents : 0.0;
        double rareLarge = totalEvents > 0 ? rareLargeEvents / (double)totalEvents : 0.0;
        double expectedAny = GenomeMutator.NaturalMutationProbability(parent.MutationRate);
        bool zeroRateExact = VerifyZeroRate(mutator, parent);

        bool passed = Math.Abs(expectedAny - 0.44088) < 1e-12 &&
            Math.Abs(observedAny - expectedAny) < 0.015 &&
            Math.Abs(one - 0.90) < 0.015 &&
            Math.Abs(two - 0.09) < 0.012 &&
            Math.Abs(three - 0.01) < 0.006 &&
            Math.Abs(local - 0.85) < 0.018 &&
            rareLarge is >= 0.008 and <= 0.024 &&
            singleLocalSamples > 0 && singleLocalSparsePasses == singleLocalSamples &&
            sensorPointSamples > 0 && sensorPointSparsePasses == sensorPointSamples &&
            invalidMetadata == 0 && unchangedReported == 0 && invalidGenomes == 0 &&
            zeroRateExact;

        return new(samples, expectedAny, observedAny, one, two, three, local,
            structural, rareLarge, singleLocalSamples, singleLocalSparsePasses,
            sensorPointSamples, sensorPointSparsePasses, invalidMetadata,
            unchangedReported, invalidGenomes, zeroRateExact, passed);
    }

    private static bool VerifyZeroRate(GenomeMutator mutator, Genome template)
    {
        Genome zero = new(template.Regions, 0.0, template.Metabolism,
            template.ControllerNodes, template.Sensors);
        DeterministicRandom body = new(1984, 41);
        DeterministicRandom controller = new(1984, 42);
        for (int index = 0; index < 512; index++)
        {
            MutationResult result = mutator.Inherit(zero, body, controller);
            if (result.EventCount != 0 || result.EventKinds.Count != 0 ||
                result.Kind != MutationKind.None || result.Genome.Fingerprint != zero.Fingerprint)
                return false;
        }
        return true;
    }

    private static bool IsLocalScalar(MutationKind kind) => kind is
        MutationKind.Point or MutationKind.MetabolicPoint or
        MutationKind.ControllerPoint or MutationKind.SensorPoint;

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static int CountChangedScalarDimensions(Genome left, Genome right)
    {
        if (left.Regions.Count != right.Regions.Count ||
            left.ControllerNodes.Count != right.ControllerNodes.Count ||
            left.Sensors.Count != right.Sensors.Count)
            return int.MaxValue;
        int changed = 0;
        for (int index = 0; index < left.Regions.Count; index++)
        {
            RegionGene a = left.Regions[index], b = right.Regions[index];
            if (a.RegionId != b.RegionId || a.ParentRegionId != b.ParentRegionId ||
                a.MatterSourceRegionId != b.MatterSourceRegionId ||
                a.SignalSourceRegionId != b.SignalSourceRegionId || a.IsCore != b.IsCore)
                return int.MaxValue;
            changed += CountDifferences(RegionScalars(a), RegionScalars(b));
        }
        changed += CountDifferences(
            [left.Metabolism.OxygenUseFraction, left.Metabolism.OxygenCatalysis,
             left.Metabolism.WaterRetention, left.Metabolism.OsmoticTolerance],
            [right.Metabolism.OxygenUseFraction, right.Metabolism.OxygenCatalysis,
             right.Metabolism.WaterRetention, right.Metabolism.OsmoticTolerance]);
        for (int index = 0; index < left.ControllerNodes.Count; index++)
        {
            ControllerNodeGene a = left.ControllerNodes[index], b = right.ControllerNodes[index];
            if (a.RecurrentSourceIndex != b.RecurrentSourceIndex) return int.MaxValue;
            changed += CountDifferences(ControllerScalars(a), ControllerScalars(b));
        }
        for (int index = 0; index < left.Sensors.Count; index++)
        {
            SensorGene a = left.Sensors[index], b = right.Sensors[index];
            if (a.Channel != b.Channel || a.SourceRegionId != b.SourceRegionId ||
                a.TargetControllerNodeIndex != b.TargetControllerNodeIndex)
                return int.MaxValue;
            changed += CountDifferences(SensorScalars(a), SensorScalars(b));
        }
        return changed;
    }

    private static int CountChangedSensorScalars(Genome left, Genome right)
    {
        if (left.Sensors.Count != right.Sensors.Count) return int.MaxValue;
        int changed = 0;
        for (int index = 0; index < left.Sensors.Count; index++)
            changed += CountDifferences(SensorScalars(left.Sensors[index]), SensorScalars(right.Sensors[index]));
        return changed;
    }

    private static bool SensorStructureEqual(Genome left, Genome right)
    {
        if (left.Sensors.Count != right.Sensors.Count) return false;
        for (int index = 0; index < left.Sensors.Count; index++)
        {
            SensorGene a = left.Sensors[index], b = right.Sensors[index];
            if (a.Channel != b.Channel || a.SourceRegionId != b.SourceRegionId ||
                a.TargetControllerNodeIndex != b.TargetControllerNodeIndex)
                return false;
        }
        return true;
    }

    private static int CountDifferences(double[] left, double[] right)
    {
        int changed = 0;
        for (int index = 0; index < left.Length; index++)
            if (!left[index].Equals(right[index])) changed++;
        return changed;
    }

    private static double[] RegionScalars(RegionGene g) =>
    [
        g.AppearanceMaturity, g.TargetLength, g.TargetWidth, g.RelativeAngle,
        g.CrossSectionAspect, g.Taper, g.Curvature, g.Roundness, g.Density,
        g.Rigidity, g.Toughness, g.Permeability, g.LightReactivity,
        g.CatalyticActivity, g.Contractility, g.SignalConductivity,
        g.StorageFraction, g.Pigment, g.ExchangeExpression, g.BarrierExpression,
        g.ContractileExpression, g.StructuralExpression, g.SensoryExpression,
        g.CavityFraction, g.CavityAperture, g.JointRestPitch, g.JointMobility
    ];

    private static double[] ControllerScalars(ControllerNodeGene g) =>
    [
        g.Bias, g.LightWeight, g.TemperatureWeight, g.PressureWeight,
        g.EnergyWeight, g.MatterWeight, g.HydrationWeight, g.ContactWeight,
        g.SelfMemoryWeight, g.RecurrentWeight, g.ContractionOutputWeight,
        g.PermeabilityOutputWeight, g.SecretionOutputWeight,
        g.VerticalContractionOutputWeight, g.LateralContractionOutputWeight
    ];

    private static double[] SensorScalars(SensorGene g) =>
    [g.Gain, g.ResponseRate, g.DirectionOffsetRadians, g.Range, g.DirectionalSelectivity];
}
