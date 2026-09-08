using System.Globalization;
using NativeEpoch.Simulation;

return HeadlessProgram.Run(args);

internal static class HeadlessProgram
{
    public static int Run(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            if (options.DiagnoseMutations)
                return DiagnoseMutations(options);
            return options.Verify ? Verify(options) : Simulate(options);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            Console.Error.WriteLine("Use --help to list supported options.");
            return 2;
        }
    }

    private static int Simulate(Options options)
    {
        SimulationWorld world = CreateWorld(options);
        PrintHeader(options);
        PrintSnapshot(world.CaptureSnapshot());

        for (int index = 0; index < options.Steps; index++)
        {
            world.Step();
            bool isFinal = index + 1 == options.Steps;
            if ((world.StepIndex % options.ReportEvery == 0) || isFinal)
                PrintSnapshot(world.CaptureSnapshot());
        }

        SimulationSnapshot final = world.CaptureSnapshot();
        if (options.InspectLineage > 0)
            PrintLineage(world, options.InspectLineage);
        double tolerance = MatterTolerance(final.InitialMatter);
        if (!final.AllFinite || Math.Abs(final.MatterError) > tolerance)
        {
            Console.Error.WriteLine(
                $"FAILED finite={final.AllFinite} matter_error={final.MatterError:R} tolerance={tolerance:R}");
            return 1;
        }

        return 0;
    }

    private static int Verify(Options options)
    {
        SimulationWorld first = CreateWorld(options);
        SimulationWorld second = CreateWorld(options);
        first.Run(options.Steps);
        second.Run(options.Steps);
        SimulationSnapshot a = first.CaptureSnapshot();
        SimulationSnapshot b = second.CaptureSnapshot();

        bool deterministic = a.StateFingerprint == b.StateFingerprint;
        bool lifecycle = a.CumulativeBirths > 0 && a.CumulativeDeaths > 0;
        bool developedBodies = first.Organisms.Any(organism => organism.Body.RegionCount > 1);
        bool exactInheritance = first.RecentBirths.Any(record =>
            record.MutationKind == MutationKind.None && record.ParentGenomeId == record.ChildGenomeId);
        bool naturalVariation = first.RecentBirths.Any(record =>
            record.MutationKind != MutationKind.None && record.ParentGenomeId != record.ChildGenomeId);
        bool cacheConsistent = first.ValidateBodyCaches(out double cacheError);
        bool growthPaid = VerifyPaidGrowth();
        bool forcedMutationsSafe = CheckForcedMutations(options.Seed, print: false);
        double tolerance = MatterTolerance(a.InitialMatter);
        bool matterConserved = Math.Abs(a.MatterError) <= tolerance;
        bool passed = deterministic && lifecycle && developedBodies && exactInheritance && naturalVariation &&
            cacheConsistent && growthPaid && forcedMutationsSafe &&
            a.AllFinite && b.AllFinite && matterConserved;

        Console.WriteLine(
            $"verify seed={options.Seed} steps={options.Steps} ancestors={options.Ancestors}");
        Console.WriteLine(
            $"deterministic={deterministic} fingerprint={a.StateFingerprint:X16} repeat={b.StateFingerprint:X16}");
        Console.WriteLine(
            $"lifecycle={lifecycle} population={a.Population} births={a.CumulativeBirths} deaths={a.CumulativeDeaths}");
        Console.WriteLine(
            $"genetics exact_inheritance={exactInheritance} natural_variation={naturalVariation} " +
            $"genomes={a.GenomeCount} body_regions={a.TotalBodyRegions}");
        Console.WriteLine(
            $"development={developedBodies} growth_paid={growthPaid} " +
            $"cache_consistent={cacheConsistent} cache_error={cacheError:E3}");
        Console.WriteLine($"forced_mutation_topology={forcedMutationsSafe} kinds=point,copy,delete,reconnect");
        Console.WriteLine(
            $"finite={a.AllFinite && b.AllFinite} matter_error={a.MatterError:E6} tolerance={tolerance:E6}");
        Console.WriteLine(passed ? "VERIFY PASS" : "VERIFY FAIL");
        return passed ? 0 : 1;
    }

    private static int DiagnoseMutations(Options options)
    {
        Console.WriteLine("FORCED MUTATION DIAGNOSTIC (test mode; does not represent natural event rates)");
        bool passed = CheckForcedMutations(options.Seed, print: true);
        Console.WriteLine(passed ? "DIAGNOSTIC PASS" : "DIAGNOSTIC FAIL");
        return passed ? 0 : 1;
    }

    private static bool CheckForcedMutations(ulong seed, bool print)
    {
        Genome parent = Genome.CreateAncestor();
        GenomeMutator mutator = new();
        MutationKind[] kinds =
        [
            MutationKind.Point,
            MutationKind.DuplicateRegion,
            MutationKind.DeleteRegion,
            MutationKind.ReconnectRegion
        ];
        bool passed = true;

        for (int index = 0; index < kinds.Length; index++)
        {
            DeterministicRandom random = new(seed ^ (ulong)(index + 1), (ulong)(20 + index));
            MutationResult result = mutator.MutateForced(parent, random, kinds[index]);
            bool changed = result.Genome.Fingerprint != parent.Fingerprint;
            bool safe = result.Genome.Regions.Count is >= 1 and <= GenomeValidator.MaximumRegions &&
                result.Genome.Regions.Count(region => region.IsCore) == 1;

            SimulationConfig config = new();
            DevelopingBody body = new(result.Genome, config.CoreInitialMatter);
            double stored = 10.0;
            double energy = 100.0;
            for (int step = 0; step < 120; step++)
                body.Grow(result.Genome, 1.0, ref stored, ref energy, config);
            BodyCache fresh = body.Recalculate(result.Genome);
            double cacheError = BodyCalculator.MaximumDifference(body.Cache, fresh);
            bool bodyValid = body.AllFinite(result.Genome) && cacheError <= 1e-12;
            passed &= changed && safe && bodyValid;

            if (print)
            {
                Console.WriteLine(
                    $"kind={result.Kind,-17} valid={safe && bodyValid} changed={changed} " +
                    $"genes={result.Genome.Regions.Count,2} body_regions={body.RegionCount,2} " +
                    $"body_matter={body.Cache.TotalMatter:F6} light={body.Cache.LightCaptureSurface:F6} " +
                    $"uptake={body.Cache.MatterUptakeSurface:F6} maintenance={body.Cache.MaintenanceEnergyPerSecond:F6} " +
                    $"cache_error={cacheError:E3}");
                Console.WriteLine($"  {result.Summary}");
            }
        }

        Genome expanded = parent;
        int stream = 100;
        while (expanded.Regions.Count < GenomeValidator.MaximumRegions)
        {
            DeterministicRandom random = new(seed ^ (ulong)stream, (ulong)stream);
            expanded = mutator.MutateForced(
                expanded, random, MutationKind.DuplicateRegion).Genome;
            stream++;
        }
        Genome capped = mutator.MutateForced(
            expanded,
            new DeterministicRandom(seed ^ 0xCAFEUL, 200),
            MutationKind.DuplicateRegion).Genome;

        Genome reduced = parent;
        while (reduced.Regions.Count > 1)
        {
            DeterministicRandom random = new(seed ^ (ulong)stream, (ulong)stream);
            reduced = mutator.MutateForced(reduced, random, MutationKind.DeleteRegion).Genome;
            stream++;
        }
        Genome floored = mutator.MutateForced(
            reduced,
            new DeterministicRandom(seed ^ 0xFACEUL, 201),
            MutationKind.DeleteRegion).Genome;
        bool boundsSafe =
            expanded.Regions.Count == GenomeValidator.MaximumRegions &&
            capped.Regions.Count == GenomeValidator.MaximumRegions &&
            reduced.Regions.Count == 1 &&
            floored.Regions.Count == 1;
        passed &= boundsSafe;
        if (print)
            Console.WriteLine($"region_bounds={boundsSafe} floor={reduced.Regions.Count} ceiling={expanded.Regions.Count}");

        return passed;
    }

    private static bool VerifyPaidGrowth()
    {
        SimulationConfig config = new();
        Genome genome = Genome.CreateAncestor();
        DevelopingBody body = new(genome, config.CoreInitialMatter);
        double stored = 2.0;
        double energy = 10.0;
        double bodyBefore = body.Cache.TotalMatter;
        double storedBefore = stored;
        double energyBefore = energy;
        double grown = body.Grow(genome, 1.0, ref stored, ref energy, config);
        return grown > 0.0 &&
            Math.Abs((body.Cache.TotalMatter - bodyBefore) - grown) <= 1e-12 &&
            Math.Abs((storedBefore - stored) - grown) <= 1e-12 &&
            Math.Abs((energyBefore - energy) - (grown * config.GrowthEnergyPerMatter)) <= 1e-12;
    }

    private static SimulationWorld CreateWorld(Options options)
    {
        SimulationConfig config = new()
        {
            WorldSize = options.WorldSize,
            EnvironmentGridSize = options.GridSize,
            MaxPopulation = options.MaxPopulation
        };
        return new SimulationWorld(config, options.Seed, options.Ancestors);
    }

    private static double MatterTolerance(double initialMatter) =>
        Math.Max(1e-8, Math.Abs(initialMatter) * 1e-10);

    private static void PrintHeader(Options options) => Console.WriteLine(
        $"NativeEpoch phase1 seed={options.Seed} steps={options.Steps} ancestors={options.Ancestors} " +
        $"world={options.WorldSize.ToString("G9", CultureInfo.InvariantCulture)} grid={options.GridSize} " +
        $"max_population={options.MaxPopulation}");

    private static void PrintSnapshot(SimulationSnapshot snapshot) => Console.WriteLine(
        $"step={snapshot.StepIndex,6} time={snapshot.SimulatedSeconds,7:F1}s " +
        $"alive={snapshot.Population,6} births={snapshot.CumulativeBirths,6} deaths={snapshot.CumulativeDeaths,6} " +
        $"genomes={snapshot.GenomeCount,4} regions={snapshot.TotalBodyRegions,6} maturity={snapshot.AverageMaturity:F3} " +
        $"minerals={snapshot.EnvironmentMinerals,12:F6} detritus={snapshot.EnvironmentDetritus,10:F6} " +
        $"body={snapshot.OrganismBodyMatter,10:F6} stored={snapshot.OrganismStoredMatter,10:F6} " +
        $"matter_error={snapshot.MatterError:E3} fingerprint={snapshot.StateFingerprint:X16}");

    private static void PrintLineage(SimulationWorld world, int count)
    {
        Console.WriteLine($"lineage inspection (up to {count}, mutated births first)");
        BirthRecord[] recent = world.RecentBirths.Reverse().ToArray();
        int mutatedCount = recent.Count(record => record.MutationKind != MutationKind.None);
        int exactCount = recent.Length - mutatedCount;
        Console.WriteLine($"recent inheritance exact={exactCount} mutated={mutatedCount}");
        int mutatedLimit = count > 1 ? count - 1 : count;
        IEnumerable<BirthRecord> selected = recent
            .Where(record => record.MutationKind != MutationKind.None)
            .Take(mutatedLimit)
            .Concat(recent.Where(record => record.MutationKind == MutationKind.None).Take(count > 1 ? 1 : 0))
            .Concat(recent.Where(record => record.MutationKind == MutationKind.None))
            .Distinct()
            .Take(count);
        foreach (BirthRecord record in selected)
        {
            string bodyText = "child no longer alive";
            if (world.TryGetOrganism(record.ChildId, out Organism child))
            {
                BodyCache cache = child.Body.Cache;
                bodyText =
                    $"age={child.AgeSeconds:F1}s maturity={child.Maturity:F3} " +
                    $"body_regions={child.Body.RegionCount} body_matter={cache.TotalMatter:F6} " +
                    $"mass={cache.PhysicalMass:F6} radius={cache.BoundingRadius:F6} " +
                    $"light={cache.LightCaptureSurface:F6} uptake={cache.MatterUptakeSurface:F6} " +
                    $"maintenance={cache.MaintenanceEnergyPerSecond:F6}";
            }

            Console.WriteLine(
                $"parent={record.ParentId} child={record.ChildId} mutation={record.MutationKind} " +
                $"genome={record.ParentGenomeId}->{record.ChildGenomeId} " +
                $"genes={record.ParentGeneCount}->{record.ChildGeneCount} {bodyText}");
            Console.WriteLine($"  {record.MutationSummary}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("NativeEpoch phase 1 headless genetics and development simulation");
        Console.WriteLine("  --verify                 deterministic lifecycle and phase 1 invariants");
        Console.WriteLine("  --diagnose-mutations     force four mutation kinds in an explicit test mode");
        Console.WriteLine("  --inspect-lineage <int>  print recent parent-child genome/body differences");
        Console.WriteLine("  --seed <uint64>          world seed (default 20260908)");
        Console.WriteLine("  --steps <int>            fixed 0.1 s steps (default 600)");
        Console.WriteLine("  --ancestors <int>        initial organisms (default 4)");
        Console.WriteLine("  --report-every <int>     output interval (default 100)");
        Console.WriteLine("  --grid <int>             hidden field samples per axis (default 128)");
        Console.WriteLine("  --world-size <float>     continuous world extent (default 512)");
        Console.WriteLine("  --max-population <int>   reproduction safety ceiling (default 20000)");
    }

    private sealed record Options(
        bool Verify,
        bool DiagnoseMutations,
        bool ShowHelp,
        ulong Seed,
        int Steps,
        int Ancestors,
        int ReportEvery,
        int GridSize,
        float WorldSize,
        int MaxPopulation,
        int InspectLineage)
    {
        public static Options Parse(string[] args)
        {
            bool verify = false;
            bool diagnoseMutations = false;
            bool showHelp = false;
            ulong seed = 20260908;
            int steps = 600;
            int ancestors = 4;
            int reportEvery = 100;
            int gridSize = 128;
            float worldSize = 512f;
            int maxPopulation = 20_000;
            int inspectLineage = 0;

            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                switch (option)
                {
                    case "--verify":
                        verify = true;
                        break;
                    case "--diagnose-mutations":
                        diagnoseMutations = true;
                        break;
                    case "--inspect-lineage":
                        inspectLineage = ParsePositiveInt(ReadValue(args, ref index, option), option);
                        break;
                    case "--help" or "-h":
                        showHelp = true;
                        break;
                    case "--seed":
                        seed = ParseUlong(ReadValue(args, ref index, option), option);
                        break;
                    case "--steps":
                        steps = ParseNonNegativeInt(ReadValue(args, ref index, option), option);
                        break;
                    case "--ancestors":
                        ancestors = ParsePositiveInt(ReadValue(args, ref index, option), option);
                        break;
                    case "--report-every":
                        reportEvery = ParsePositiveInt(ReadValue(args, ref index, option), option);
                        break;
                    case "--grid":
                        gridSize = ParsePositiveInt(ReadValue(args, ref index, option), option);
                        break;
                    case "--world-size":
                        worldSize = ParsePositiveFloat(ReadValue(args, ref index, option), option);
                        break;
                    case "--max-population":
                        maxPopulation = ParsePositiveInt(ReadValue(args, ref index, option), option);
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{option}'.");
                }
            }

            if (steps == 0 && verify)
                throw new ArgumentException("--verify requires --steps greater than zero.");

            return new Options(
                verify, diagnoseMutations, showHelp, seed, steps, ancestors, reportEvery,
                gridSize, worldSize, maxPopulation, inspectLineage);
        }

        private static string ReadValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length)
                throw new ArgumentException($"Missing value for {option}.");
            return args[index];
        }

        private static ulong ParseUlong(string value, string option) =>
            ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed)
                ? parsed
                : throw new ArgumentException($"Invalid value '{value}' for {option}.");

        private static int ParsePositiveInt(string value, string option)
        {
            int parsed = ParseNonNegativeInt(value, option);
            return parsed > 0
                ? parsed
                : throw new ArgumentException($"{option} must be greater than zero.");
        }

        private static int ParseNonNegativeInt(string value, string option) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0
                ? parsed
                : throw new ArgumentException($"Invalid value '{value}' for {option}.");

        private static float ParsePositiveFloat(string value, string option) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) &&
            float.IsFinite(parsed) && parsed > 0f
                ? parsed
                : throw new ArgumentException($"Invalid value '{value}' for {option}.");
    }
}
