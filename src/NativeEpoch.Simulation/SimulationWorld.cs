using System.Numerics;

namespace NativeEpoch.Simulation;

public sealed class SimulationWorld
{
    private const int RecentBirthRecordLimit = 256;
    private const int InterventionRecordLimit = 256;
    private readonly SimulationConfig _config;
    private readonly ulong _seed;
    private readonly BilinearEnvironmentField _environment;
    private readonly DeterministicRandom _reproductionRandom;
    private readonly DeterministicRandom _mutationRandom;
    private readonly GenomeMutator _mutator = new();
    private readonly List<BirthRecord> _recentBirths = [];
    private readonly List<EnvironmentBrushCommand> _pendingEnvironmentCommands = [];
    private readonly List<EnvironmentInterventionRecord> _recentInterventions = [];
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
                Velocity = Vector2.Zero,
                HeadingRadians = streams.Placement.NextUnitDouble() * Math.Tau,
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
    public double CumulativeMovementEnergy { get; private set; }
    public double CumulativeExternalMatter { get; private set; }
    public IReadOnlyList<Organism> Organisms => _organisms;
    public IReadOnlyList<BirthRecord> RecentBirths => _recentBirths;
    public IReadOnlyList<EnvironmentInterventionRecord> RecentInterventions => _recentInterventions;
    public GenomeRegistry Genomes { get; }
    public IEnvironmentField Environment => _environment;
    public SimulationConfig Config => _config;

    public void QueueEnvironmentBrush(EnvironmentBrushCommand command)
    {
        command.Validate(_config.WorldSize);
        _pendingEnvironmentCommands.Add(command);
    }

    public int ApplyQueuedCommands()
    {
        int count = _pendingEnvironmentCommands.Count;
        foreach (EnvironmentBrushCommand command in _pendingEnvironmentCommands)
        {
            double matterDelta = _environment.ApplyBrush(command);
            CumulativeExternalMatter += matterDelta;
            if (_recentInterventions.Count == InterventionRecordLimit)
                _recentInterventions.RemoveAt(0);
            _recentInterventions.Add(new EnvironmentInterventionRecord(StepIndex, command, matterDelta));
        }
        _pendingEnvironmentCommands.Clear();
        return count;
    }

    public void Step()
    {
        ApplyQueuedCommands();
        double delta = _config.FixedDeltaSeconds;
        int reproductionSlots = Math.Max(0, _config.MaxPopulation - _organisms.Count);
        Vector2[] separationAccelerations = ComputeSeparationAccelerations();
        _nextOrganisms.Clear();
        _birthBuffer.Clear();

        for (int organismIndex = 0; organismIndex < _organisms.Count; organismIndex++)
        {
            Organism current = _organisms[organismIndex];
            Organism organism = current;
            Genome genome = Genomes.Get(organism.GenomeId);
            organism.AgeSeconds += delta;
            organism.Maturity = Math.Clamp(organism.AgeSeconds / _config.MaturityAgeSeconds, 0.0, 1.0);
            organism.ReproductionCooldownSeconds =
                Math.Max(0.0, organism.ReproductionCooldownSeconds - delta);

            MoveOrganism(
                ref organism,
                organism.Body.Cache,
                separationAccelerations[organismIndex],
                delta);

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
                    organism.Position,
                    organism.HeadingRadians);
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

    public WorldPresentationSnapshot CapturePresentationSnapshot()
    {
        OrganismPresentationState[] organisms = new OrganismPresentationState[_organisms.Count];
        for (int index = 0; index < _organisms.Count; index++)
        {
            Organism organism = _organisms[index];
            Genome genome = Genomes.Get(organism.GenomeId);
            organisms[index] = new OrganismPresentationState(
                organism.Id,
                organism.ParentId,
                organism.GenomeId,
                genome.Fingerprint,
                genome.Regions.Count,
                organism.Position,
                organism.Velocity,
                organism.HeadingRadians,
                organism.AgeSeconds,
                organism.Maturity,
                organism.Energy,
                organism.StoredMatter,
                organism.ReproductionCooldownSeconds,
                organism.Body.DevelopmentCompletion(genome),
                _environment.Sample(organism.Position),
                organism.Body.Cache,
                BodyCalculator.BuildVisualRegions(genome, organism.Body.Regions));
        }

        return new WorldPresentationSnapshot(CaptureSnapshot(), organisms);
    }

    public SimulationSnapshot CaptureSnapshot()
    {
        double bodyMatter = 0.0;
        double storedMatter = 0.0;
        double livingEnergy = 0.0;
        double maturity = 0.0;
        int bodyRegions = 0;
        double totalSpeed = 0.0;
        bool organismsFinite = true;

        foreach (Organism organism in _organisms)
        {
            Genome genome = Genomes.Get(organism.GenomeId);
            bodyMatter += organism.Body.Cache.TotalMatter;
            storedMatter += organism.StoredMatter;
            livingEnergy += organism.Energy;
            maturity += organism.Maturity;
            bodyRegions += organism.Body.RegionCount;
            totalSpeed += organism.Velocity.Length();
            organismsFinite &= organism.AllFinite &&
                organism.Body.AllFinite(genome) &&
                organism.StoredMatter >= 0.0 &&
                organism.Energy >= 0.0;
        }

        bool cachesValid = ValidateBodyCaches(out double maximumCacheError);
        double minerals = _environment.TotalMinerals;
        double detritus = _environment.TotalDetritus;
        double totalMatter = minerals + detritus + bodyMatter + storedMatter;
        double matterError = totalMatter - (_initialMatter + CumulativeExternalMatter);
        bool totalsFinite =
            double.IsFinite(totalMatter) &&
            double.IsFinite(matterError) &&
            double.IsFinite(livingEnergy) &&
            double.IsFinite(CumulativeLightEnergy) &&
            double.IsFinite(CumulativeDissipatedEnergy) &&
            double.IsFinite(CumulativeMovementEnergy) &&
            double.IsFinite(totalSpeed) &&
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
            CumulativeExternalMatter,
            matterError,
            livingEnergy,
            CumulativeLightEnergy,
            CumulativeDissipatedEnergy,
            _organisms.Count > 0 ? totalSpeed / _organisms.Count : 0.0,
            CumulativeMovementEnergy,
            maximumCacheError,
            organismsFinite && _environment.AllFinite && totalsFinite && cachesValid,
            ComputeFingerprint());
    }

    private Organism CreateNewborn(
        ulong id,
        ulong parentId,
        int genomeId,
        Genome genome,
        Vector2 parentPosition,
        double parentHeading)
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
            Velocity = Vector2.Zero,
            HeadingRadians = NormalizeAngle(
                parentHeading + ((_reproductionRandom.NextUnitDouble() - 0.5) * 0.35)),
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
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(CumulativeExternalMatter)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(CumulativeMovementEnergy)));
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
            FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(organism.Velocity.X)));
            FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(organism.Velocity.Y)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(organism.HeadingRadians)));
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

    private void MoveOrganism(
        ref Organism organism,
        BodyCache body,
        Vector2 separationAcceleration,
        double delta)
    {
        double cosine = Math.Cos(organism.HeadingRadians);
        double sine = Math.Sin(organism.HeadingRadians);
        Vector2 localPropulsion = body.PropulsionVector;
        Vector2 worldPropulsion = new(
            (float)((localPropulsion.X * cosine) - (localPropulsion.Y * sine)),
            (float)((localPropulsion.X * sine) + (localPropulsion.Y * cosine)));
        Vector2 acceleration =
            (worldPropulsion * (float)_config.PropulsionAccelerationScale) +
            separationAcceleration;
        organism.Velocity += acceleration * (float)delta;
        organism.Velocity *= (float)Math.Exp(-_config.VelocityDampingPerSecond * delta);

        double dragRatio = body.Drag / Math.Max(0.1, body.PhysicalMass);
        float maximumSpeed = (float)(_config.MaximumMovementSpeed / (1.0 + (0.08 * dragRatio)));
        float speed = organism.Velocity.Length();
        if (speed > maximumSpeed)
            organism.Velocity *= maximumSpeed / speed;

        Vector2 displacement = organism.Velocity * (float)delta;
        double movementCost = displacement.Length() * _config.MovementEnergyPerDistance *
            (1.0 + body.MaximumActivationEnergyPerSecond);
        if (movementCost > organism.Energy && movementCost > 0.0)
        {
            float affordable = (float)(organism.Energy / movementCost);
            displacement *= affordable;
            organism.Velocity *= affordable;
            movementCost = organism.Energy;
        }
        organism.Energy -= movementCost;
        CumulativeDissipatedEnergy += movementCost;
        CumulativeMovementEnergy += movementCost;
        organism.Position += displacement;

        float worldMaximum = _config.WorldSize;
        if (organism.Position.X < 0f || organism.Position.X > worldMaximum)
        {
            organism.Position.X = Math.Clamp(organism.Position.X, 0f, worldMaximum);
            organism.Velocity.X *= -0.45f;
            organism.HeadingRadians = NormalizeAngle(Math.PI - organism.HeadingRadians);
        }
        if (organism.Position.Y < 0f || organism.Position.Y > worldMaximum)
        {
            organism.Position.Y = Math.Clamp(organism.Position.Y, 0f, worldMaximum);
            organism.Velocity.Y *= -0.45f;
            organism.HeadingRadians = NormalizeAngle(-organism.HeadingRadians);
        }
    }

    private Vector2[] ComputeSeparationAccelerations()
    {
        Vector2[] accelerations = new Vector2[_organisms.Count];
        float radius = _config.SeparationRadius;
        Dictionary<(int X, int Y), List<int>> spatialHash = [];
        for (int index = 0; index < _organisms.Count; index++)
        {
            Vector2 position = _organisms[index].Position;
            (int X, int Y) key = ((int)MathF.Floor(position.X / radius), (int)MathF.Floor(position.Y / radius));
            if (!spatialHash.TryGetValue(key, out List<int>? bucket))
            {
                bucket = [];
                spatialHash.Add(key, bucket);
            }
            bucket.Add(index);
        }

        float radiusSquared = radius * radius;
        for (int first = 0; first < _organisms.Count; first++)
        {
            Vector2 firstPosition = _organisms[first].Position;
            int cellX = (int)MathF.Floor(firstPosition.X / radius);
            int cellY = (int)MathF.Floor(firstPosition.Y / radius);
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (!spatialHash.TryGetValue((cellX + offsetX, cellY + offsetY), out List<int>? bucket))
                        continue;
                    foreach (int second in bucket)
                    {
                        if (second <= first)
                            continue;
                        Vector2 difference = firstPosition - _organisms[second].Position;
                        float distanceSquared = difference.LengthSquared();
                        if (distanceSquared >= radiusSquared)
                            continue;

                        Vector2 direction;
                        float distance;
                        if (distanceSquared <= 1e-10f)
                        {
                            ulong pairHash = _organisms[first].Id * 0x9E3779B185EBCA87UL ^ _organisms[second].Id;
                            double angle = (pairHash & 0xFFFFUL) * (Math.Tau / 65536.0);
                            direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                            distance = 0f;
                        }
                        else
                        {
                            distance = MathF.Sqrt(distanceSquared);
                            direction = difference / distance;
                        }

                        float overlap = 1f - (distance / radius);
                        Vector2 push = direction * (float)(_config.SeparationAcceleration * overlap);
                        accelerations[first] += push;
                        accelerations[second] -= push;
                    }
                }
            }
        }
        return accelerations;
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= Math.Tau;
        return angle < 0.0 ? angle + Math.Tau : angle;
    }
}
