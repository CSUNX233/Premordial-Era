using System.Numerics;

namespace NativeEpoch.Simulation;

public sealed class SimulationWorld
{
    private readonly SimulationConfig _config;
    private readonly ulong _seed;
    private readonly BilinearEnvironmentField _environment;
    private readonly DeterministicRandom _reproductionRandom;
    private List<Organism> _organisms;
    private List<Organism> _nextOrganisms;
    private readonly List<Organism> _birthBuffer = [];
    private ulong _nextOrganismId = 1;
    private readonly double _initialMatter;

    public SimulationWorld(SimulationConfig config, ulong seed, int ancestorCount)
    {
        config.Validate(ancestorCount);
        _config = config;
        _seed = seed;

        RandomStreams streams = new(seed);
        _environment = new BilinearEnvironmentField(config, streams.Environment);
        _reproductionRandom = streams.Reproduction;
        _organisms = new List<Organism>(Math.Min(config.MaxPopulation, ancestorCount * 2));
        _nextOrganisms = new List<Organism>(Math.Min(config.MaxPopulation, ancestorCount * 2));

        for (int index = 0; index < ancestorCount; index++)
        {
            _organisms.Add(new Organism
            {
                Id = _nextOrganismId++,
                ParentId = 0,
                Position = new Vector2(
                    streams.Placement.NextFloat(0f, config.WorldSize),
                    streams.Placement.NextFloat(0f, config.WorldSize)),
                AgeSeconds = 0.0,
                Energy = config.AncestorEnergy,
                BodyMatter = config.BodyMatter,
                StoredMatter = config.AncestorStoredMatter,
                ReproductionCooldownSeconds = 0.0
            });
        }

        _initialMatter = CalculateTotalMatter();
    }

    public long StepIndex { get; private set; }
    public double SimulatedSeconds => StepIndex * _config.FixedDeltaSeconds;
    public long CumulativeBirths { get; private set; }
    public long CumulativeDeaths { get; private set; }
    public double CumulativeLightEnergy { get; private set; }
    public double CumulativeDissipatedEnergy { get; private set; }
    public IReadOnlyList<Organism> Organisms => _organisms;
    public IEnvironmentField Environment => _environment;

    public void Step()
    {
        double delta = _config.FixedDeltaSeconds;
        int reproductionSlots = Math.Max(0, _config.MaxPopulation - _organisms.Count);
        _nextOrganisms.Clear();
        _birthBuffer.Clear();

        foreach (Organism current in _organisms)
        {
            Organism organism = current;
            organism.AgeSeconds += delta;
            organism.ReproductionCooldownSeconds =
                Math.Max(0.0, organism.ReproductionCooldownSeconds - delta);

            EnvironmentSample sample = _environment.Sample(organism.Position);
            double lightInput = sample.Light * _config.LightEnergyPerSecond * delta;
            CumulativeLightEnergy += lightInput;
            organism.Energy += lightInput;
            if (organism.Energy > _config.MaximumEnergy)
            {
                CumulativeDissipatedEnergy += organism.Energy - _config.MaximumEnergy;
                organism.Energy = _config.MaximumEnergy;
            }

            double storageCapacity = Math.Max(0.0, _config.MaximumStoredMatter - organism.StoredMatter);
            double requestedMatter = Math.Min(storageCapacity, _config.MatterUptakePerSecond * delta);
            organism.StoredMatter += _environment.WithdrawMatter(organism.Position, requestedMatter);

            double maintenanceCost = _config.MaintenanceEnergyPerSecond * delta;
            double paidMaintenance = Math.Min(organism.Energy, maintenanceCost);
            organism.Energy -= paidMaintenance;
            CumulativeDissipatedEnergy += paidMaintenance;

            bool canReproduce =
                reproductionSlots > 0 &&
                organism.AgeSeconds >= _config.MaturityAgeSeconds &&
                organism.ReproductionCooldownSeconds <= 0.0 &&
                organism.Energy >= _config.ReproductionEnergyThreshold &&
                organism.Energy >= _config.ReproductionEnergyCost &&
                organism.StoredMatter >= _config.ReproductionMatterCost;

            if (canReproduce)
            {
                organism.Energy -= _config.ReproductionEnergyCost;
                organism.StoredMatter -= _config.ReproductionMatterCost;
                organism.ReproductionCooldownSeconds = _config.ReproductionCooldownSeconds;
                CumulativeDissipatedEnergy += _config.ReproductionEnergyCost - _config.NewbornEnergy;

                double angle = _reproductionRandom.NextUnitDouble() * Math.Tau;
                float distance = _reproductionRandom.NextFloat(0f, _config.NewbornOffsetRadius);
                Vector2 offset = new(
                    (float)(Math.Cos(angle) * distance),
                    (float)(Math.Sin(angle) * distance));
                Vector2 newbornPosition = Vector2.Clamp(
                    organism.Position + offset,
                    Vector2.Zero,
                    new Vector2(_config.WorldSize));

                _birthBuffer.Add(new Organism
                {
                    Id = _nextOrganismId++,
                    ParentId = organism.Id,
                    Position = newbornPosition,
                    AgeSeconds = 0.0,
                    Energy = _config.NewbornEnergy,
                    BodyMatter = _config.BodyMatter,
                    StoredMatter = 0.0,
                    ReproductionCooldownSeconds = _config.ReproductionCooldownSeconds
                });
                reproductionSlots--;
                CumulativeBirths++;
            }

            bool died = organism.Energy <= 0.0 || organism.AgeSeconds >= _config.MaximumAgeSeconds;
            if (died)
            {
                CumulativeDissipatedEnergy += organism.Energy;
                _environment.DepositDetritus(
                    organism.Position,
                    organism.BodyMatter + organism.StoredMatter);
                CumulativeDeaths++;
            }
            else
            {
                _nextOrganisms.Add(organism);
            }
        }

        // Newborns enter after all current organisms have completed this fixed step.
        _nextOrganisms.AddRange(_birthBuffer);
        (_organisms, _nextOrganisms) = (_nextOrganisms, _organisms);
        StepIndex++;
    }

    public void Run(int steps)
    {
        if (steps < 0)
            throw new ArgumentOutOfRangeException(nameof(steps));

        for (int index = 0; index < steps; index++)
            Step();
    }

    public SimulationSnapshot CaptureSnapshot()
    {
        double bodyMatter = 0.0;
        double storedMatter = 0.0;
        double livingEnergy = 0.0;
        bool organismsFinite = true;

        foreach (Organism organism in _organisms)
        {
            bodyMatter += organism.BodyMatter;
            storedMatter += organism.StoredMatter;
            livingEnergy += organism.Energy;
            organismsFinite &= organism.AllFinite &&
                organism.BodyMatter >= 0.0 &&
                organism.StoredMatter >= 0.0 &&
                organism.Energy >= 0.0;
        }

        double minerals = _environment.TotalMinerals;
        double detritus = _environment.TotalDetritus;
        double totalMatter = minerals + detritus + bodyMatter + storedMatter;
        double matterError = totalMatter - _initialMatter;
        bool totalsFinite =
            double.IsFinite(totalMatter) &&
            double.IsFinite(matterError) &&
            double.IsFinite(livingEnergy) &&
            double.IsFinite(CumulativeLightEnergy) &&
            double.IsFinite(CumulativeDissipatedEnergy);

        return new SimulationSnapshot(
            StepIndex,
            SimulatedSeconds,
            _organisms.Count,
            CumulativeBirths,
            CumulativeDeaths,
            minerals,
            detritus,
            bodyMatter,
            storedMatter,
            totalMatter,
            _initialMatter,
            matterError,
            livingEnergy,
            CumulativeLightEnergy,
            CumulativeDissipatedEnergy,
            organismsFinite && _environment.AllFinite && totalsFinite,
            ComputeFingerprint());
    }

    private double CalculateTotalMatter()
    {
        double organismMatter = 0.0;
        foreach (Organism organism in _organisms)
            organismMatter += organism.BodyMatter + organism.StoredMatter;

        return _environment.TotalMinerals + _environment.TotalDetritus + organismMatter;
    }

    private ulong ComputeFingerprint()
    {
        ulong hash = 14695981039346656037UL;
        Hash(ref hash, _seed);
        Hash(ref hash, unchecked((ulong)StepIndex));
        Hash(ref hash, unchecked((ulong)CumulativeBirths));
        Hash(ref hash, unchecked((ulong)CumulativeDeaths));
        Hash(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(_environment.TotalMinerals)));
        Hash(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(_environment.TotalDetritus)));

        foreach (Organism organism in _organisms)
        {
            Hash(ref hash, organism.Id);
            Hash(ref hash, organism.ParentId);
            Hash(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(organism.Position.X)));
            Hash(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(organism.Position.Y)));
            Hash(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.AgeSeconds)));
            Hash(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.Energy)));
            Hash(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.BodyMatter)));
            Hash(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.StoredMatter)));
            Hash(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.ReproductionCooldownSeconds)));
        }

        return hash;
    }

    private static void Hash(ref ulong hash, ulong value)
    {
        for (int index = 0; index < sizeof(ulong); index++)
        {
            hash ^= (byte)value;
            hash *= 1099511628211UL;
            value >>= 8;
        }
    }
}
