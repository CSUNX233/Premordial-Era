using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct ShoreFounderTrack(
    ulong Id,
    double StartCoastDistance,
    double LastCoastDistance,
    double MinimumCoastDistance,
    double StartWaterDepth,
    double LastWaterDepth,
    bool InitiallyNearShore,
    bool EverNearShore,
    bool EverOnLand,
    bool Alive,
    double? FirstNearShoreSeconds,
    double? FirstLandSeconds,
    double SecondsObserved,
    double PathLength,
    double NetDisplacement,
    double MeanChemicalAccess,
    int ChemicalAccessPositiveSteps)
{
    public double CoastApproach => StartCoastDistance - LastCoastDistance;
    public double MaximumCoastApproach => StartCoastDistance - MinimumCoastDistance;
}

public readonly record struct ShoreTrialSummary(
    string Label,
    int Founders,
    int InitiallyNearShore,
    int EverNearShore,
    int EverOnLand,
    int Surviving,
    int FinalNearShoreAmongSurvivors,
    int EverChemicalAccessPositive,
    double MeanStartCoastDistance,
    double MeanLastCoastDistanceAllFounders,
    double MeanLastCoastDistanceSurvivors,
    double MeanCoastApproachAllFounders,
    double MeanCoastApproachSurvivors,
    int MovedCloserByOneUnit,
    int MovedFartherByOneUnit,
    IReadOnlyList<ShoreFounderTrack> Tracks);

public readonly record struct ShoreCausalityDiagnosticResult(
    ulong Seed,
    int FounderCount,
    double SimulatedSeconds,
    double NearShoreDistance,
    bool MatchedInitialState,
    bool MatchedGenomesExceptChemicalGain,
    ShoreTrialSummary Sensed,
    ShoreTrialSummary ChemicalGainZero,
    double PairedMeanExtraApproachWithSensing,
    int PairsSensedApproachedMoreByOneUnit,
    int PairsBlindApproachedMoreByOneUnit,
    bool ZeroAccessIgnoredExtremeCueGradient,
    double ZeroAccessMaximumDecisionDifference,
    double PositiveAccessSteeringDifference);

/// <summary>
/// A bounded paired diagnosis of the report that founders all move to shore.
/// This is deliberately outside production behavior: both trials disable births,
/// share the default randomized founder genes/placements, and differ only by
/// setting ChemicalResource sensor Gain to zero in the second trial.
/// </summary>
public static class ShoreCausalityDiagnostics
{
    private const ulong DefaultSeed = 20260908;
    private const int DefaultFounders = 24;
    private const double NearShoreDistance = 8.0;

    public static ShoreCausalityDiagnosticResult Run(double seconds = 180.0)
    {
        SimulationConfig config = new()
        {
            ReproductionEnergyThreshold = 100.0
        };
        SimulationWorld sensed = new(config, DefaultSeed, DefaultFounders, mutationsEnabled: false);
        SimulationWorld blind = new(config, DefaultSeed, DefaultFounders, mutationsEnabled: false);

        IReadOnlyList<Organism> sensedInitial = sensed.Organisms.ToArray();
        IReadOnlyList<Organism> blindInitial = blind.Organisms.ToArray();
        bool matchedInitialState = sensedInitial.Count == blindInitial.Count &&
            sensedInitial.Zip(blindInitial).All(pair =>
                pair.First.Id == pair.Second.Id &&
                pair.First.Position == pair.Second.Position &&
                pair.First.Depth == pair.Second.Depth &&
                pair.First.HeadingRadians.Equals(pair.Second.HeadingRadians));

        // Organism is a struct and the exposed list is the world's actual List<T>.
        // Repoint each blind founder to a registered genome with exactly one change.
        List<Organism> blindOrganisms = (List<Organism>)blind.Organisms;
        bool matchedGenomesExceptChemicalGain = true;
        for (int index = 0; index < blindOrganisms.Count; index++)
        {
            Organism organism = blindOrganisms[index];
            Genome source = blind.Genomes.Get(organism.GenomeId);
            Genome disabled = DisableChemicalGain(source);
            matchedGenomesExceptChemicalGain &= OnlyChemicalGainChanged(source, disabled);
            organism.GenomeId = blind.Genomes.Register(disabled);
            blindOrganisms[index] = organism;
        }

        ShorelineMap shoreline = ShorelineMap.Build(sensed.Environment, config.WorldSize);
        ShoreTrialSummary sensedSummary = Track("sensed", sensed, shoreline, seconds);
        ShoreTrialSummary blindSummary = Track("chemical_gain_zero", blind, shoreline, seconds);

        Dictionary<ulong, ShoreFounderTrack> blindById = blindSummary.Tracks.ToDictionary(track => track.Id);
        double[] pairedExtraApproach = sensedSummary.Tracks
            .Select(track => track.CoastApproach - blindById[track.Id].CoastApproach)
            .ToArray();
        (bool ignored, double zeroDifference, double positiveDifference) = DirectCueIsolation(config);

        return new(
            DefaultSeed,
            DefaultFounders,
            seconds,
            NearShoreDistance,
            matchedInitialState,
            matchedGenomesExceptChemicalGain,
            sensedSummary,
            blindSummary,
            pairedExtraApproach.Average(),
            pairedExtraApproach.Count(value => value >= 1.0),
            pairedExtraApproach.Count(value => value <= -1.0),
            ignored,
            zeroDifference,
            positiveDifference);
    }

    private static Genome DisableChemicalGain(Genome source) => new(
        source.Regions,
        source.MutationRate,
        source.Metabolism,
        source.ControllerNodes,
        source.Sensors.Select(sensor => sensor.Channel == SensorChannel.ChemicalResource
            ? sensor with { Gain = 0.0 }
            : sensor));

    private static bool OnlyChemicalGainChanged(Genome source, Genome disabled)
    {
        if (!source.Regions.SequenceEqual(disabled.Regions) ||
            !source.ControllerNodes.SequenceEqual(disabled.ControllerNodes) ||
            !source.Metabolism.Equals(disabled.Metabolism) ||
            !source.MutationRate.Equals(disabled.MutationRate) ||
            source.Sensors.Count != disabled.Sensors.Count)
            return false;
        for (int index = 0; index < source.Sensors.Count; index++)
        {
            SensorGene expected = source.Sensors[index].Channel == SensorChannel.ChemicalResource
                ? source.Sensors[index] with { Gain = 0.0 }
                : source.Sensors[index];
            if (!expected.Equals(disabled.Sensors[index])) return false;
        }
        return true;
    }

    private static ShoreTrialSummary Track(
        string label,
        SimulationWorld world,
        ShorelineMap shoreline,
        double seconds)
    {
        SimulationConfig config = world.Config;
        int steps = (int)Math.Round(seconds / config.FixedDeltaSeconds);
        Dictionary<ulong, MutableTrack> tracks = world.Organisms.ToDictionary(
            organism => organism.Id,
            organism => new MutableTrack(organism, world.Environment, shoreline));

        for (int step = 1; step <= steps; step++)
        {
            world.Step();
            HashSet<ulong> alive = [];
            foreach (Organism organism in world.Organisms)
            {
                if (!tracks.TryGetValue(organism.Id, out MutableTrack? track)) continue;
                alive.Add(organism.Id);
                track.Observe(organism, world.Environment, shoreline,
                    step * config.FixedDeltaSeconds, step % 5 == 0);
            }
            foreach (MutableTrack track in tracks.Values)
                if (!alive.Contains(track.Id)) track.Alive = false;
        }

        ShoreFounderTrack[] completed = tracks.Values
            .OrderBy(track => track.Id)
            .Select(track => track.Complete(config.WorldSize))
            .ToArray();
        ShoreFounderTrack[] survivors = completed.Where(track => track.Alive).ToArray();
        return new(
            label,
            completed.Length,
            completed.Count(track => track.InitiallyNearShore),
            completed.Count(track => track.EverNearShore),
            completed.Count(track => track.EverOnLand),
            survivors.Length,
            survivors.Count(track => track.LastCoastDistance <= NearShoreDistance),
            completed.Count(track => track.ChemicalAccessPositiveSteps > 0),
            completed.Average(track => track.StartCoastDistance),
            completed.Average(track => track.LastCoastDistance),
            survivors.Length > 0 ? survivors.Average(track => track.LastCoastDistance) : double.NaN,
            completed.Average(track => track.CoastApproach),
            survivors.Length > 0 ? survivors.Average(track => track.CoastApproach) : double.NaN,
            completed.Count(track => track.CoastApproach >= 1.0),
            completed.Count(track => track.CoastApproach <= -1.0),
            completed);
    }

    private static (bool Ignored, double ZeroDifference, double PositiveDifference)
        DirectCueIsolation(SimulationConfig config)
    {
        ForagingObservation flat = new(
            new Vector2(10, 10), new Vector2(14, 10), new Vector2(10, 6), new Vector2(10, 14),
            0.1, 0.1, 0.1, 0.1, 0, 0, 0, 0);
        ForagingObservation extreme = flat with
        {
            AheadCue = 1.0,
            LeftCue = 0.0,
            RightCue = 1.0
        };
        ForagingDecision flatZero = RepeatedDecision(flat with { ChemicalAccess = 0.0 }, config);
        ForagingDecision extremeZero = RepeatedDecision(extreme with { ChemicalAccess = 0.0 }, config);
        ForagingDecision flatPositive = RepeatedDecision(flat with { ChemicalAccess = 1.0 }, config);
        ForagingDecision extremePositive = RepeatedDecision(extreme with { ChemicalAccess = 1.0 }, config);
        double zeroDifference = MaximumDifference(flatZero, extremeZero);
        double positiveDifference = Math.Abs(flatPositive.Steering - extremePositive.Steering);
        return (zeroDifference <= 1e-12, zeroDifference, positiveDifference);
    }

    private static ForagingDecision RepeatedDecision(
        ForagingObservation observation,
        SimulationConfig config)
    {
        ForagingMemory memory = new();
        ForagingDecision decision = default;
        for (int step = 0; step < 30; step++)
            decision = BehaviorController.UpdateForaging(
                memory, observation, 0.8, 0.6, config, config.FixedDeltaSeconds);
        return decision;
    }

    private static double MaximumDifference(ForagingDecision first, ForagingDecision second)
    {
        double[] differences =
        [
            Math.Abs(first.ResourceGradient - second.ResourceGradient),
            Math.Abs(first.ResourceLateral - second.ResourceLateral),
            Math.Abs(first.ResourceTrend - second.ResourceTrend),
            Math.Abs(first.Novelty - second.Novelty),
            Math.Abs(first.Danger - second.Danger),
            Math.Abs(first.ExplorationSignal - second.ExplorationSignal),
            Math.Abs(first.Activity - second.Activity),
            Math.Abs(first.Steering - second.Steering)
        ];
        return differences.Max();
    }

    private sealed class MutableTrack
    {
        private Vector2 _startPosition;
        private Vector2 _lastPosition;
        private double _chemicalAccessTotal;
        private int _observations;

        public MutableTrack(Organism organism, IEnvironmentField environment, ShorelineMap shoreline)
        {
            Id = organism.Id;
            _startPosition = organism.Position;
            _lastPosition = organism.Position;
            StartCoastDistance = shoreline.Distance(organism.Position);
            LastCoastDistance = StartCoastDistance;
            MinimumCoastDistance = StartCoastDistance;
            StartWaterDepth = environment.Sample(organism.Position).WaterDepth;
            LastWaterDepth = StartWaterDepth;
            InitiallyNearShore = StartCoastDistance <= NearShoreDistance;
            EverNearShore = InitiallyNearShore;
        }

        public ulong Id { get; }
        public double StartCoastDistance { get; }
        public double LastCoastDistance { get; private set; }
        public double MinimumCoastDistance { get; private set; }
        public double StartWaterDepth { get; }
        public double LastWaterDepth { get; private set; }
        public bool InitiallyNearShore { get; }
        public bool EverNearShore { get; private set; }
        public bool EverOnLand { get; private set; }
        public bool Alive { get; set; } = true;
        public double? FirstNearShoreSeconds { get; private set; }
        public double? FirstLandSeconds { get; private set; }
        public double SecondsObserved { get; private set; }
        public double PathLength { get; private set; }
        public int ChemicalAccessPositiveSteps { get; private set; }

        public void Observe(
            Organism organism,
            IEnvironmentField environment,
            ShorelineMap shoreline,
            double seconds,
            bool updateCoastDistance)
        {
            PathLength += SphericalWorld.Distance(_lastPosition, organism.Position, shoreline.WorldSize);
            _lastPosition = organism.Position;
            SecondsObserved = seconds;
            _chemicalAccessTotal += organism.ChemicalSenseAccess;
            _observations++;
            if (organism.ChemicalSenseAccess > 1e-6) ChemicalAccessPositiveSteps++;

            EnvironmentSample sample = environment.Sample(organism.Position);
            LastWaterDepth = sample.WaterDepth;
            if (sample.WaterDepth <= 0.0)
            {
                EverOnLand = true;
                FirstLandSeconds ??= seconds;
            }
            if (!updateCoastDistance && sample.WaterDepth > 0.0) return;
            LastCoastDistance = shoreline.Distance(organism.Position);
            MinimumCoastDistance = Math.Min(MinimumCoastDistance, LastCoastDistance);
            if (LastCoastDistance <= NearShoreDistance)
            {
                EverNearShore = true;
                FirstNearShoreSeconds ??= seconds;
            }
        }

        public ShoreFounderTrack Complete(float worldSize) => new(
            Id,
            StartCoastDistance,
            LastCoastDistance,
            MinimumCoastDistance,
            StartWaterDepth,
            LastWaterDepth,
            InitiallyNearShore,
            EverNearShore,
            EverOnLand,
            Alive,
            FirstNearShoreSeconds,
            FirstLandSeconds,
            SecondsObserved,
            PathLength,
            SphericalWorld.Distance(_startPosition, _lastPosition, worldSize),
            _observations > 0 ? _chemicalAccessTotal / _observations : 0.0,
            ChemicalAccessPositiveSteps);
    }

    private sealed class ShorelineMap(float worldSize, Vector2[] points)
    {
        public float WorldSize { get; } = worldSize;

        public double Distance(Vector2 position)
        {
            double minimum = double.PositiveInfinity;
            foreach (Vector2 point in points)
                minimum = Math.Min(minimum, SphericalWorld.Distance(position, point, WorldSize));
            return minimum;
        }

        public static ShorelineMap Build(IEnvironmentField environment, float worldSize)
        {
            const int columns = 256;
            const int rows = 129;
            bool[,] land = new bool[columns, rows];
            for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                Vector2 position = new(
                    x * worldSize / columns,
                    y * worldSize / (rows - 1));
                land[x, y] = environment.Sample(position).WaterDepth <= 0.0;
            }

            List<Vector2> points = [];
            for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int left = (x + columns - 1) % columns;
                int right = (x + 1) % columns;
                bool boundary = land[x, y] != land[left, y] || land[x, y] != land[right, y];
                if (y > 0) boundary |= land[x, y] != land[x, y - 1];
                if (y + 1 < rows) boundary |= land[x, y] != land[x, y + 1];
                if (boundary)
                    points.Add(new Vector2(
                        x * worldSize / columns,
                        y * worldSize / (rows - 1)));
            }
            if (points.Count == 0)
                throw new InvalidOperationException("Seeded world has no sampled shoreline.");
            return new ShorelineMap(worldSize, points.ToArray());
        }
    }
}
