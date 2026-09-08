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
        double tolerance = MatterTolerance(a.InitialMatter);
        bool matterConserved = Math.Abs(a.MatterError) <= tolerance;
        bool passed = deterministic && lifecycle && a.AllFinite && b.AllFinite && matterConserved;

        Console.WriteLine(
            $"verify seed={options.Seed} steps={options.Steps} ancestors={options.Ancestors}");
        Console.WriteLine(
            $"deterministic={deterministic} fingerprint={a.StateFingerprint:X16} repeat={b.StateFingerprint:X16}");
        Console.WriteLine(
            $"lifecycle={lifecycle} population={a.Population} births={a.CumulativeBirths} deaths={a.CumulativeDeaths}");
        Console.WriteLine(
            $"finite={a.AllFinite && b.AllFinite} matter_error={a.MatterError:E6} tolerance={tolerance:E6}");
        Console.WriteLine(passed ? "VERIFY PASS" : "VERIFY FAIL");
        return passed ? 0 : 1;
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
        $"NativeEpoch phase0 seed={options.Seed} steps={options.Steps} ancestors={options.Ancestors} " +
        $"world={options.WorldSize.ToString("G9", CultureInfo.InvariantCulture)} grid={options.GridSize} " +
        $"max_population={options.MaxPopulation}");

    private static void PrintSnapshot(SimulationSnapshot snapshot) => Console.WriteLine(
        $"step={snapshot.StepIndex,6} time={snapshot.SimulatedSeconds,7:F1}s " +
        $"alive={snapshot.Population,6} births={snapshot.CumulativeBirths,6} deaths={snapshot.CumulativeDeaths,6} " +
        $"minerals={snapshot.EnvironmentMinerals,12:F6} detritus={snapshot.EnvironmentDetritus,10:F6} " +
        $"body={snapshot.OrganismBodyMatter,10:F6} stored={snapshot.OrganismStoredMatter,10:F6} " +
        $"matter_error={snapshot.MatterError:E3} fingerprint={snapshot.StateFingerprint:X16}");

    private static void PrintHelp()
    {
        Console.WriteLine("NativeEpoch phase 0 headless simulation");
        Console.WriteLine("  --verify                 run the same seed twice and check invariants");
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
        bool ShowHelp,
        ulong Seed,
        int Steps,
        int Ancestors,
        int ReportEvery,
        int GridSize,
        float WorldSize,
        int MaxPopulation)
    {
        public static Options Parse(string[] args)
        {
            bool verify = false;
            bool showHelp = false;
            ulong seed = 20260908;
            int steps = 600;
            int ancestors = 4;
            int reportEvery = 100;
            int gridSize = 128;
            float worldSize = 512f;
            int maxPopulation = 20_000;

            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                switch (option)
                {
                    case "--verify":
                        verify = true;
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
                verify, showHelp, seed, steps, ancestors, reportEvery,
                gridSize, worldSize, maxPopulation);
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
