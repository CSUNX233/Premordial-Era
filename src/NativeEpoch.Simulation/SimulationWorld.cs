using System.Numerics;

namespace NativeEpoch.Simulation;

public sealed class SimulationWorld
{
    private const int RecentBirthRecordLimit = 256;
    private readonly SimulationConfig _config;
    private readonly ulong _seed;
    private readonly BilinearEnvironmentField _environment;
    private readonly DeterministicRandom _reproductionRandom;
    private readonly DeterministicRandom _mutationRandom;
    private readonly GenomeMutator _mutator = new();
    private readonly List<BirthRecord> _recentBirths = [];
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
        _mutationRandom = streams.Mutation;
        _organisms = new List<Organism>(Math.Min(config.MaxPopulation, ancestorCount * 2));
        _nextOrganisms = new List<Organism>(Math.Min(config.MaxPopulation, ancestorCount * 2));

        Genomes = new GenomeRegistry();
        int ancestorGenomeId = Genomes.Register(Genome.CreateAncestor());
        Genome ancestorGenome = Genomes.Get(ancestorGenomeId);

        for (int index = 0; index < ancestorCount; index++)
        {
            _organisms.Add(new Organism
            {
                Id = _nextOrganismId++,
                ParentId = 0,
                GenomeId = ancestorGenomeId,
                Position = new Vector2(
                    streams.Placement.NextFloat(0f, config.WorldSize),
                    streams.Placement.NextFloat(0f, config.WorldSize)),
                AgeSeconds = 0.0,
                Maturity = 0.0,
                Energy = config.AncestorEnergy,
                StoredMatter = config.AncestorStoredMatter,
                ReproductionCooldownSeconds = 0.0,
                Body = new DevelopingBody(ancestorGenome, config.CoreInitialMatter)
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
    public IReadOnlyList<BirthRecord> RecentBirths => _recentBirths;
    public GenomeRegistry Genomes { get; }
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
            Genome genome = Genomes.Get(organism.GenomeId);
            organism.AgeSeconds += delta;
            organism.Maturity = Math.Clamp(organism.AgeSeconds / _config.MaturityAgeSeconds, 0.0, 1.0);
            organism.ReproductionCooldownSeconds =
                Math.Max(0.0, organism.ReproductionCooldownSeconds - delta);

            EnvironmentSample sample = _environment.Sample(organism.Position);
            BodyCache cache = organism.Body.Cache;
            double lightInput = sample.Light * cache.LightCaptureSurface *
                _config.LightEnergyPerSurfacePerSecond * delta;
            CumulativeLightEnergy += lightInput;
            organism.Energy += lightInput;
            if (organism.Energy > _config.MaximumEnergy)
            {
                CumulativeDissipatedEnergy += organism.Energy - _config.MaximumEnergy;
                organism.Energy = _config.MaximumEnergy;
            }

            double storageCapacity = _config.BaseStoredMatter + cache.StorageCapacity;
            double availableStorage = Math.Max(0.0, storageCapacity - organism.StoredMatter);
            double requestedMatter = Math.Min(
                availableStorage,
                cache.MatterUptakeSurface * _config.MatterUptakePerSurfacePerSecond * delta);
            organism.StoredMatter += _environment.WithdrawMatter(organism.Position, requestedMatter);

            double grownMatter = organism.Body.Grow(
                genome,
                organism.Maturity,
                ref organism.StoredMatter,
                ref organism.Energy,
                _config);
            CumulativeDissipatedEnergy += grownMatter * _config.GrowthEnergyPerMatter;
            cache = organism.Body.Cache;

            double maintenanceCost =
                (_config.BaseMaintenanceEnergyPerSecond + cache.MaintenanceEnergyPerSecond) * delta;
            double paidMaintenance = Math.Min(organism.Energy, maintenanceCost);
            organism.Energy -= paidMaintenance;
            CumulativeDissipatedEnergy += paidMaintenance;

            bool canReproduce =
                reproductionSlots > 0 &&
                organism.Maturity >= 1.0 &&
                organism.Body.DevelopmentCompletion(genome) >= 0.95 &&
                organism.ReproductionCooldownSeconds <= 0.0 &&
                organism.Energy >= _config.ReproductionEnergyThreshold &&
                organism.Energy >= _config.ReproductionEnergyCost &&
                organism.StoredMatter >= _config.CoreInitialMatter;

            if (canReproduce)
            {
                organism.Energy -= _config.ReproductionEnergyCost;
                organism.StoredMatter -= _config.CoreInitialMatter;
                organism.ReproductionCooldownSeconds = _config.ReproductionCooldownSeconds;
                CumulativeDissipatedEnergy += _config.ReproductionEnergyCost - _config.NewbornEnergy;

                MutationResult inheritance = _mutator.Inherit(genome, _mutationRandom);
                int childGenomeId = Genomes.Register(inheritance.Genome);
                ulong childId = _nextOrganismId++;
                Organism newborn = CreateNewborn(
                    childId,
                    organism.Id,
                    childGenomeId,
                    inheritance.Genome,
                    organism.Position);
                _birthBuffer.Add(newborn);
                AddBirthRecord(new BirthRecord(
                    organism.Id,
                    childId,
                    organism.GenomeId,
                    childGenomeId,
                    genome.Regions.Count,
                    inheritance.Genome.Regions.Count,
                    inheritance.Kind,
                    inheritance.Summary));
                reproductionSlots--;
                CumulativeBirths++;
            }

            bool died = organism.Energy <= 0.0 || organism.AgeSeconds >= _config.MaximumAgeSeconds;
            if (died)
            {
                CumulativeDissipatedEnergy += organism.Energy;
                _environment.DepositDetritus(
                    organism.Position,
                    organism.Body.Cache.TotalMatter + organism.StoredMatter);
                CumulativeDeaths++;
            }
            else
            {
                _nextOrganisms.Add(organism);
            }
        }

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

    public bool TryGetOrganism(ulong organismId, out Organism organism)
    {
        foreach (Organism candidate in _organisms)
        {
            if (candidate.Id == organismId)
            {
                organism = candidate;
                return true;
            }
        }

        organism = default;
        return false;
    }

    public bool ValidateBodyCaches(out double maximumDifference)
    {
        maximumDifference = 0.0;
        foreach (Organism organism in _organisms)
        {
            Genome genome = Genomes.Get(organism.GenomeId);
            BodyCache recalculated = organism.Body.Recalculate(genome);
            maximumDifference = Math.Max(
                maximumDifference,
                BodyCalculator.MaximumDifference(organism.Body.Cache, recalculated));
        }
        return maximumDifference <= 1e-12;
    }

    public SimulationSnapshot CaptureSnapshot()
    {
        double bodyMatter = 0.0;
        double storedMatter = 0.0;
        double livingEnergy = 0.0;
        double maturity = 0.0;
        int bodyRegions = 0;
        bool organismsFinite = true;

        foreach (Organism organism in _organisms)
        {
            Genome genome = Genomes.Get(organism.GenomeId);
            bodyMatter += organism.Body.Cache.TotalMatter;
            storedMatter += organism.StoredMatter;
            livingEnergy += organism.Energy;
            maturity += organism.Maturity;
            bodyRegions += organism.Body.RegionCount;
            organismsFinite &= organism.AllFinite &&
                organism.Body.AllFinite(genome) &&
                organism.StoredMatter >= 0.0 &&
                organism.Energy >= 0.0;
        }

        bool cachesValid = ValidateBodyCaches(out double maximumCacheError);
        double minerals = _environment.TotalMinerals;
        double detritus = _environment.TotalDetritus;
        double totalMatter = minerals + detritus + bodyMatter + storedMatter;
        double matterError = totalMatter - _initialMatter;
        bool totalsFinite =
            double.IsFinite(totalMatter) &&
            double.IsFinite(matterError) &&
            double.IsFinite(livingEnergy) &&
            double.IsFinite(CumulativeLightEnergy) &&
            double.IsFinite(CumulativeDissipatedEnergy) &&
            double.IsFinite(maximumCacheError);

        return new SimulationSnapshot(
            StepIndex,
            SimulatedSeconds,
            _organisms.Count,
            CumulativeBirths,
            CumulativeDeaths,
            Genomes.Count,
            bodyRegions,
            _organisms.Count > 0 ? maturity / _organisms.Count : 0.0,
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
            maximumCacheError,
            organismsFinite && _environment.AllFinite && totalsFinite && cachesValid,
            ComputeFingerprint());
    }

    private Organism CreateNewborn(
        ulong id,
        ulong parentId,
        int genomeId,
        Genome genome,
        Vector2 parentPosition)
    {
        double angle = _reproductionRandom.NextUnitDouble() * Math.Tau;
        float distance = _reproductionRandom.NextFloat(0f, _config.NewbornOffsetRadius);
        Vector2 offset = new(
            (float)(Math.Cos(angle) * distance),
            (float)(Math.Sin(angle) * distance));
        Vector2 position = Vector2.Clamp(
            parentPosition + offset,
            Vector2.Zero,
            new Vector2(_config.WorldSize));

        return new Organism
        {
            Id = id,
            ParentId = parentId,
            GenomeId = genomeId,
            Position = position,
            AgeSeconds = 0.0,
            Maturity = 0.0,
            Energy = _config.NewbornEnergy,
            StoredMatter = 0.0,
            ReproductionCooldownSeconds = _config.ReproductionCooldownSeconds,
            Body = new DevelopingBody(genome, _config.CoreInitialMatter)
        };
    }

    private void AddBirthRecord(BirthRecord record)
    {
        if (_recentBirths.Count == RecentBirthRecordLimit)
            _recentBirths.RemoveAt(0);
        _recentBirths.Add(record);
    }

    private double CalculateTotalMatter()
    {
        double organismMatter = 0.0;
        foreach (Organism organism in _organisms)
            organismMatter += organism.Body.Cache.TotalMatter + organism.StoredMatter;
        return _environment.TotalMinerals + _environment.TotalDetritus + organismMatter;
    }

    private ulong ComputeFingerprint()
    {
        ulong hash = FingerprintHash.Offset;
        FingerprintHash.Add(ref hash, _seed);
        FingerprintHash.Add(ref hash, unchecked((ulong)StepIndex));
        FingerprintHash.Add(ref hash, unchecked((ulong)CumulativeBirths));
        FingerprintHash.Add(ref hash, unchecked((ulong)CumulativeDeaths));
        FingerprintHash.Add(ref hash, unchecked((ulong)Genomes.Count));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(_environment.TotalMinerals)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(_environment.TotalDetritus)));

        foreach (Organism organism in _organisms)
        {
            FingerprintHash.Add(ref hash, organism.Id);
            FingerprintHash.Add(ref hash, organism.ParentId);
            FingerprintHash.Add(ref hash, unchecked((ulong)organism.GenomeId));
            FingerprintHash.Add(ref hash, Genomes.Get(organism.GenomeId).Fingerprint);
            FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(organism.Position.X)));
            FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(organism.Position.Y)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.AgeSeconds)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.Maturity)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.Energy)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.StoredMatter)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.ReproductionCooldownSeconds)));
            foreach (BodyRegion bodyRegion in organism.Body.Regions)
            {
                FingerprintHash.Add(ref hash, unchecked((ulong)bodyRegion.RegionId));
                FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(bodyRegion.Matter)));
                FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(bodyRegion.Development)));
            }
        }

        return hash;
    }
}
