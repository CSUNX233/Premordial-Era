using System.Numerics;

namespace NativeEpoch.Simulation;

public sealed class SimulationWorld
{
    private const int RecordLimit = 256;
    private readonly SimulationConfig _config;
    private readonly ulong _seed;
    private readonly BilinearEnvironmentField _environment;
    private readonly DeterministicRandom _reproductionRandom;
    private readonly DeterministicRandom _mutationRandom;
    private readonly DeterministicRandom _controllerMutationRandom;
    private readonly DeterministicRandom _mortalityRandom;
    private readonly GenomeMutator _mutator = new();
    private readonly List<BirthRecord> _recentBirths = [];
    private readonly List<DeathRecord> _recentDeaths = [];
    private readonly List<EnvironmentBrushCommand> _pendingEnvironmentCommands = [];
    private readonly List<EnvironmentInterventionRecord> _recentInterventions = [];
    private List<Organism> _organisms;
    private List<Organism> _nextOrganisms;
    private readonly List<Organism> _birthBuffer = [];
    private ulong _nextOrganismId = 1;
    private readonly double _initialMatter;
    private readonly double _initialOxygen;

    public SimulationWorld(SimulationConfig config, ulong seed, int ancestorCount)
    {
        config.Validate(ancestorCount);
        _config = config;
        _seed = seed;
        RandomStreams streams = new(seed);
        _environment = new BilinearEnvironmentField(config, streams.Environment);
        _reproductionRandom = streams.Reproduction;
        _mutationRandom = streams.Mutation;
        _controllerMutationRandom = streams.ControllerMutation;
        _mortalityRandom = streams.Mortality;
        _organisms = new(Math.Min(config.MaxPopulation, ancestorCount * 2));
        _nextOrganisms = new(Math.Min(config.MaxPopulation, ancestorCount * 2));
        Genomes = new GenomeRegistry();
        Genome ancestorGenome = Genome.CreateAncestor();
        int ancestorGenomeId = Genomes.Register(ancestorGenome);

        for (int index = 0; index < ancestorCount; index++)
        {
            DevelopingBody body = new(
                ancestorGenome, config.CoreInitialMatter, config.AncestorStoredMatter,
                config.AncestorEnergy, config.AncestorInternalOxygen, config.CoreInitialMatter);
            (Vector2 position, float depth) = FindAquaticSpawn(streams.Placement, body);
            _organisms.Add(new Organism
            {
                Id = _nextOrganismId++, ParentId = 0, GenomeId = ancestorGenomeId,
                Position = position, Velocity = Vector2.Zero, Depth = depth,
                HeadingRadians = streams.Placement.NextUnitDouble() * Math.Tau,
                Hydration = RegionalPhysiology.BodyHydration(body, ancestorGenome), Immersion = 1.0,
                ControllerState = new double[ancestorGenome.ControllerNodes.Count], Body = body
            });
        }
        _initialMatter = CalculateTotalMatter();
        _initialOxygen = CalculateTotalOxygen();
    }

    public long StepIndex { get; private set; }
    public double SimulatedSeconds => StepIndex * _config.FixedDeltaSeconds;
    public long CumulativeBirths { get; private set; }
    public long CumulativeDeaths { get; private set; }
    public long DamageDeaths { get; private set; }
    public long JuvenileDeaths { get; private set; }
    public long SenescenceDeaths { get; private set; }
    public double CumulativeLightEnergy { get; private set; }
    public double CumulativeDissipatedEnergy { get; private set; }
    public double CumulativeMovementEnergy { get; private set; }
    public double CumulativeExternalMatter { get; private set; }
    public double CumulativeOxygenUptake { get; private set; }
    public double CumulativeOxygenConsumed { get; private set; }
    public double CumulativeWaterUptake { get; private set; }
    public double CumulativeWaterLoss { get; private set; }
    public IReadOnlyList<Organism> Organisms => _organisms;
    public IReadOnlyList<BirthRecord> RecentBirths => _recentBirths;
    public IReadOnlyList<DeathRecord> RecentDeaths => _recentDeaths;
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
            if (_recentInterventions.Count == RecordLimit) _recentInterventions.RemoveAt(0);
            _recentInterventions.Add(new(StepIndex, command, matterDelta));
        }
        _pendingEnvironmentCommands.Clear();
        return count;
    }

    public void Step()
    {
        ApplyQueuedCommands();
        double dt = _config.FixedDeltaSeconds;
        _environment.UpdateOxygen(dt);
        _environment.UpdateMatterCycles(dt);
        IReadOnlyDictionary<ulong, double> lightAllocations = _environment.AllocateLightEnergy(
            _organisms.Select(organism =>
            {
                EnvironmentSample sample = _environment.Sample(organism.Position, organism.Depth);
                double request = sample.Light * organism.Body.Cache.LightCaptureSurface *
                    _config.LightEnergyPerSurfacePerSecond * dt;
                return new LightEnergyRequest(organism.Id, organism.Position, organism.Depth, request);
            }), dt);
        Vector2[] separation = ComputeSeparationAccelerations();
        int reproductionSlots = Math.Max(0, _config.MaxPopulation - _organisms.Count);
        _nextOrganisms.Clear();
        _birthBuffer.Clear();

        for (int index = 0; index < _organisms.Count; index++)
        {
            Organism organism = _organisms[index];
            Genome genome = Genomes.Get(organism.GenomeId);
            organism.AgeSeconds += dt;
            double growthStage = Math.Clamp(organism.AgeSeconds / _config.MaturityAgeSeconds, 0.0, 1.0);
            organism.Maturity = Math.Min(growthStage, organism.Body.DevelopmentCompletion(genome));
            organism.ReproductionCooldownSeconds = Math.Max(0.0, organism.ReproductionCooldownSeconds - dt);

            RegionalExchangeResult exchange = RegionalPhysiology.ExchangeWithEnvironment(
                organism.Body, genome, _environment, organism.Position, organism.HeadingRadians,
                organism.Depth, _config, dt, lightEnergyAllowance:
                    lightAllocations.GetValueOrDefault(organism.Id));
            organism.WaterExposedArea = exchange.WaterExposedArea;
            organism.AirExposedArea = exchange.AirExposedArea;
            organism.ExposedSurfaceSamples = exchange.ExposedSamples;
            organism.OccludedSurfaceSamples = exchange.OccludedSamples;
            organism.Immersion = exchange.Immersion;
            organism.OxygenUptakeLastStep = exchange.OxygenUptake;
            CumulativeOxygenUptake += exchange.OxygenUptake;
            CumulativeWaterUptake += exchange.WaterUptake;
            CumulativeWaterLoss += exchange.WaterLost;
            CumulativeLightEnergy += exchange.LightEnergy;
            CumulativeDissipatedEnergy += exchange.AssimilationEnergySpent;

            RegionalPhysiology.TransportAlongMatterEdges(organism.Body, genome, _config, dt);
            organism.Body.UpdateFunctionalState(
                genome, ControllerOutputs.Basal,
                organism.Body.Regions.ToDictionary(region => region.RegionId, _ => 0.0), dt);

            MoveOrganism(ref organism, genome, separation[index], dt);
            ApplyMediumStress(ref organism, genome, dt);
            RegionalMetabolismResult metabolism = RegionalPhysiology.ReactAndMaintain(
                organism.Body, genome, _environment, organism.Position, _config, dt);
            organism.OxygenConsumedLastStep = metabolism.OxygenConsumed;
            organism.MetabolicEnergyLastStep = metabolism.EnergyProduced;
            CumulativeOxygenConsumed += metabolism.OxygenConsumed;
            CumulativeDissipatedEnergy += metabolism.MaintenancePaid;
            organism.Hydration = RegionalPhysiology.BodyHydration(organism.Body, genome);
            double excess = organism.Body.LimitTotalEnergy(_config.MaximumEnergy);
            CumulativeDissipatedEnergy += excess;
            double grown = organism.Body.Grow(genome, growthStage, _config);
            CumulativeDissipatedEnergy += grown * _config.GrowthEnergyPerMatter;
            organism.Maturity = Math.Min(growthStage, organism.Body.DevelopmentCompletion(genome));

            bool canReproduce = reproductionSlots > 0 && organism.Maturity >= 0.95 &&
                organism.Body.DevelopmentCompletion(genome) >= 0.95 &&
                organism.ReproductionCooldownSeconds <= 0.0 &&
                organism.Body.TotalEnergy >= _config.ReproductionEnergyThreshold &&
                organism.Body.TotalEnergy >= _config.ReproductionEnergyCost &&
                organism.Body.TotalSubstrate >= _config.CoreInitialMatter + _config.NewbornSubstrate;
            if (canReproduce)
            {
                organism.Body.ConsumeEnergy(_config.ReproductionEnergyCost);
                organism.Body.ConsumeSubstrate(_config.CoreInitialMatter + _config.NewbornSubstrate);
                organism.ReproductionCooldownSeconds = _config.ReproductionCooldownSeconds;
                CumulativeDissipatedEnergy += _config.ReproductionEnergyCost - _config.NewbornEnergy;
                MutationResult inheritance = _mutator.Inherit(genome, _mutationRandom, _controllerMutationRandom);
                int childGenomeId = Genomes.Register(inheritance.Genome);
                ulong childId = _nextOrganismId++;
                Organism child = CreateNewborn(childId, organism, childGenomeId, inheritance.Genome);
                _birthBuffer.Add(child);
                AddBirthRecord(new(organism.Id, childId, organism.GenomeId, childGenomeId,
                    genome.Regions.Count, inheritance.Genome.Regions.Count,
                    inheritance.Kind, inheritance.Summary));
                reproductionSlots--;
                CumulativeBirths++;
            }

            DeathCause? deathCause = organism.Body.AverageDamage >= 1.0
                ? DeathCause.AccumulatedDamage
                : SampleMortality(organism, genome, dt);
            if (deathCause is not null)
            {
                RecordDeath(organism, genome, deathCause.Value);
                CumulativeDissipatedEnergy += organism.Body.TotalEnergy;
                _environment.DepositDetritus(
                    organism.Position, organism.Body.Cache.TotalMatter + organism.Body.TotalSubstrate);
                _environment.DepositOxygen(organism.Position, organism.Depth, organism.Immersion,
                    organism.Body.TotalOxygen);
                CumulativeWaterLoss += organism.Body.TotalWater;
                CumulativeDeaths++;
            }
            else _nextOrganisms.Add(organism);
        }
        _nextOrganisms.AddRange(_birthBuffer);
        (_organisms, _nextOrganisms) = (_nextOrganisms, _organisms);
        StepIndex++;
    }

    public void Run(int steps)
    {
        if (steps < 0) throw new ArgumentOutOfRangeException(nameof(steps));
        for (int index = 0; index < steps; index++) Step();
    }

    public bool TryGetOrganism(ulong id, out Organism organism)
    {
        foreach (Organism candidate in _organisms)
            if (candidate.Id == id) { organism = candidate; return true; }
        organism = default;
        return false;
    }

    public bool ValidateBodyCaches(out double maximumDifference)
    {
        maximumDifference = 0.0;
        foreach (Organism organism in _organisms)
        {
            BodyCache recalculated = organism.Body.Recalculate(Genomes.Get(organism.GenomeId));
            maximumDifference = Math.Max(maximumDifference,
                BodyCalculator.MaximumDifference(organism.Body.Cache, recalculated));
        }
        return maximumDifference <= 1e-12;
    }

    public WorldPresentationSnapshot CapturePresentationSnapshot()
    {
        OrganismPresentationState[] result = new OrganismPresentationState[_organisms.Count];
        for (int index = 0; index < _organisms.Count; index++)
        {
            Organism o = _organisms[index];
            Genome g = Genomes.Get(o.GenomeId);
            result[index] = new(o.Id, o.ParentId, o.GenomeId, g.Fingerprint, g.Regions.Count,
                o.Position, o.Velocity, o.Depth, o.VerticalVelocity, o.Immersion,
                o.HeadingRadians, o.Hydration, o.Body.TotalOxygen,
                o.Body.Regions.Sum(region => RegionalPhysiology.OxygenCapacity(region, _config)),
                o.OxygenUptakeLastStep, o.OxygenConsumedLastStep, o.MetabolicEnergyLastStep,
                o.DehydrationCostLastStep, o.ContactPressure, o.WaterExposedArea, o.AirExposedArea,
                o.ExposedSurfaceSamples, o.OccludedSurfaceSamples, o.ControllerInputs, o.ControllerOutputs,
                o.LocalActuationForce, o.ActuationTorque, o.AgeSeconds, o.Maturity,
                o.Body.TotalEnergy, o.Body.TotalSubstrate, o.ReproductionCooldownSeconds,
                o.Body.DevelopmentCompletion(g), _environment.Sample(o.Position, o.Depth), o.Body.Cache,
                BodyCalculator.BuildVisualRegions(g, o.Body.Regions), o.Body.Regions.ToArray());
        }
        return new(CaptureSnapshot(), result);
    }

    public SimulationSnapshot CaptureSnapshot()
    {
        double bodyMatter = 0, stored = 0, energy = 0, maturity = 0, speed = 0;
        double organismOxygen = 0, hydration = 0, depth = 0;
        int regions = 0, aquatic = 0, shore = 0, land = 0;
        bool finite = true;
        foreach (Organism o in _organisms)
        {
            Genome g = Genomes.Get(o.GenomeId);
            bodyMatter += o.Body.Cache.TotalMatter; stored += o.Body.TotalSubstrate;
            energy += o.Body.TotalEnergy; organismOxygen += o.Body.TotalOxygen;
            maturity += o.Maturity; speed += o.Velocity.Length(); depth += o.Depth;
            hydration += o.Hydration; regions += o.Body.RegionCount;
            if (o.Immersion >= 0.8) aquatic++; else if (o.Immersion > 0.05) shore++; else land++;
            finite &= o.AllFinite && o.Body.AllFinite(g);
        }
        bool cacheValid = ValidateBodyCaches(out double cacheError);
        double minerals = _environment.TotalMinerals;
        double detritus = _environment.TotalDetritus;
        double waste = _environment.TotalMetabolicWaste;
        double totalMatter = minerals + detritus + waste + bodyMatter + stored;
        double matterError = totalMatter - (_initialMatter + CumulativeExternalMatter);
        double environmentOxygen = _environment.TotalOxygen;
        double oxygenError = environmentOxygen + organismOxygen + CumulativeOxygenConsumed -
            (_initialOxygen + _environment.CumulativeExternalOxygenSupply);
        int count = _organisms.Count;
        finite &= _environment.AllFinite && double.IsFinite(matterError) &&
            double.IsFinite(oxygenError) && Math.Abs(cacheError) <= 1e-12;
        return new(StepIndex, SimulatedSeconds, count, CumulativeBirths, CumulativeDeaths,
            DamageDeaths, JuvenileDeaths, SenescenceDeaths,
            Genomes.Count, regions, count > 0 ? maturity / count : 0, minerals, detritus, waste,
            bodyMatter, stored, totalMatter, _initialMatter, CumulativeExternalMatter, matterError,
            energy, CumulativeLightEnergy, CumulativeDissipatedEnergy,
            count > 0 ? speed / count : 0, CumulativeMovementEnergy,
            aquatic, shore, land, count > 0 ? depth / count : 0,
            count > 0 ? hydration / count : 0, environmentOxygen, organismOxygen,
            _initialOxygen, _environment.CumulativeExternalOxygenSupply,
            CumulativeOxygenUptake, CumulativeOxygenConsumed, oxygenError,
            CumulativeWaterUptake, CumulativeWaterLoss, cacheError,
            finite && cacheValid, ComputeFingerprint());
    }

    public static bool AreWithinContactRange(
        Vector2 firstPosition, float firstDepth, Vector2 secondPosition, float secondDepth, float radius)
    {
        Vector2 planar = firstPosition - secondPosition;
        float vertical = firstDepth - secondDepth;
        return planar.LengthSquared() + vertical * vertical < radius * radius;
    }

    public bool RelocateForMediumDiagnostic(ulong organismId, Vector2 position, float depth)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            position.X < 0 || position.Y < 0 || position.X > _config.WorldSize || position.Y > _config.WorldSize)
            throw new ArgumentOutOfRangeException(nameof(position));
        int index = _organisms.FindIndex(organism => organism.Id == organismId);
        if (index < 0) return false;
        Organism organism = _organisms[index];
        EnvironmentSample sample = _environment.Sample(position);
        organism.Position = position;
        organism.Velocity = Vector2.Zero;
        organism.VerticalVelocity = 0;
        organism.Depth = sample.WaterDepth > 0.0
            ? Math.Clamp(depth, 0f, (float)sample.WaterDepth)
            : 0f;
        organism.Immersion = sample.WaterDepth > 0.0 && organism.Depth > 0f ? 1.0 : 0.0;
        _organisms[index] = organism;
        return true;
    }

    private (Vector2 Position, float Depth) FindAquaticSpawn(
        DeterministicRandom random, DevelopingBody body)
    {
        float halfThickness = (float)Math.Max(0.08, body.Cache.BoundingRadius * 0.22);
        float minimum = Math.Max(_config.MinimumAquaticSpawnDepth, halfThickness * 2.2f);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int attempt = 0; attempt < _config.MaximumAquaticSpawnAttempts; attempt++)
            {
                Vector2 position = new(random.NextFloat(0, _config.WorldSize), random.NextFloat(0, _config.WorldSize));
                EnvironmentSample sample = _environment.Sample(position);
                bool preferredPhoticShelf = sample.WaterDepth <= _config.PreferredAquaticSpawnMaximumDepth;
                if (sample.WaterDepth < minimum || (pass == 0 && !preferredPhoticShelf))
                    continue;
                float depth = (float)Math.Clamp(sample.WaterDepth * _config.InitialAquaticDepthFraction,
                    halfThickness, Math.Min(sample.WaterDepth - halfThickness, 6.0));
                return (position, depth);
            }
        }
        throw new InvalidOperationException(
            $"No aquatic spawn found after {_config.MaximumAquaticSpawnAttempts * 2} bounded attempts; map has no sufficiently deep water.");
    }

    private Organism CreateNewborn(ulong id, Organism parent, int genomeId, Genome genome)
    {
        Vector2 position = parent.Position;
        float depth = parent.Depth;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            double angle = _reproductionRandom.NextUnitDouble() * Math.Tau;
            float distance = _reproductionRandom.NextFloat(0, _config.NewbornOffsetRadius);
            Vector2 candidate = Vector2.Clamp(parent.Position + new Vector2(
                (float)Math.Cos(angle) * distance, (float)Math.Sin(angle) * distance),
                Vector2.Zero, new Vector2(_config.WorldSize));
            EnvironmentSample sample = _environment.Sample(candidate);
            if (sample.WaterDepth <= 0) continue;
            position = candidate;
            depth = Math.Clamp(parent.Depth, 0.08f, (float)Math.Max(0.08, sample.WaterDepth - 0.08));
            break;
        }
        double transferredOxygen = Math.Min(parent.Body.TotalOxygen * 0.18, _config.AncestorInternalOxygen * 0.5);
        double transferredWater = Math.Min(parent.Body.TotalWater * 0.12, _config.CoreInitialMatter * 0.5);
        parent.Body.ConsumeOxygen(transferredOxygen);
        parent.Body.ConsumeWater(transferredWater);
        DevelopingBody body = new(genome, _config.CoreInitialMatter, _config.NewbornSubstrate, _config.NewbornEnergy,
            transferredOxygen, transferredWater);
        return new Organism
        {
            Id = id, ParentId = parent.Id, GenomeId = genomeId, Position = position,
            Depth = depth, HeadingRadians = NormalizeAngle(parent.HeadingRadians +
                ((_reproductionRandom.NextUnitDouble() - 0.5) * 0.35)),
            Hydration = RegionalPhysiology.BodyHydration(body, genome), Immersion = 1,
            ReproductionCooldownSeconds = _config.ReproductionCooldownSeconds,
            ControllerState = new double[genome.ControllerNodes.Count], Body = body
        };
    }

    private void ApplyMediumStress(ref Organism organism, Genome genome, double dt)
    {
        organism.Hydration = RegionalPhysiology.BodyHydration(organism.Body, genome);
        double dryness = 1.0 - organism.Immersion;
        double dehydration = dryness * (1.0 - genome.Metabolism.WaterRetention) *
            (1.0 - organism.Hydration) * _config.DehydrationEnergyCostPerSecond * dt;
        double pressureMismatch = organism.Immersion * Math.Max(0.0,
            _environment.Sample(organism.Position, organism.Depth).Pressure -
            (1.0 + genome.Metabolism.OsmoticTolerance)) * _config.PressureMismatchEnergyCostPerSecond * dt;
        double requested = dehydration + pressureMismatch;
        double paid = organism.Body.ConsumeEnergy(requested);
        organism.DehydrationCostLastStep = requested;
        CumulativeDissipatedEnergy += paid;
        if (paid + 1e-12 < requested)
            foreach (BodyRegion region in organism.Body.Regions.ToArray())
                organism.Body.AddDamage(region.RegionId, (requested - paid) * 0.012 / organism.Body.RegionCount);
    }

    private DeathCause? SampleMortality(Organism organism, Genome genome, double dt)
    {
        double development = organism.Body.DevelopmentCompletion(genome);
        double energyNeed = Math.Max(0.1,
            _config.BaseMaintenanceEnergyPerSecond + organism.Body.Cache.MaintenanceEnergyPerSecond);
        double energyReserve = Math.Clamp(organism.Body.TotalEnergy / (energyNeed * 5.0), 0.0, 1.0);
        double substrateReserve = Math.Clamp(
            organism.Body.TotalSubstrate / Math.Max(0.12, organism.Body.Cache.TotalMatter * 0.35), 0.0, 1.0);
        double juvenileVulnerability = Math.Pow(Math.Max(0.0, 1.0 - development), 1.4);
        double juvenileStress =
            (0.55 * (1.0 - substrateReserve)) +
            (0.70 * (1.0 - organism.Hydration)) +
            (0.65 * (1.0 - energyReserve)) +
            (2.0 * organism.Body.AverageDamage);
        double juvenileHazard = _config.JuvenileHazardPerSecond * juvenileVulnerability *
            Math.Pow(Math.Max(0.0, juvenileStress - 0.18), 1.35);

        double meanToughness = genome.Regions.Average(region => region.Toughness);
        double onset = _config.SenescenceOnsetSeconds * (0.75 + (0.50 * meanToughness));
        double ageBeyondOnset = Math.Max(0.0, organism.AgeSeconds - onset);
        double ageScale = ageBeyondOnset / _config.SenescenceTimeScaleSeconds;
        double senescenceHazard = _config.SenescenceHazardPerSecond * ageScale * ageScale *
            (1.0 + (2.0 * organism.Body.AverageDamage) + (0.6 * (1.0 - energyReserve)));
        double totalHazard = juvenileHazard + senescenceHazard;
        if (totalHazard <= 0.0)
            return null;
        double deathProbability = 1.0 - Math.Exp(-totalHazard * dt);
        double roll = _mortalityRandom.NextUnitDouble();
        if (roll >= deathProbability)
            return null;
        return roll / deathProbability < juvenileHazard / totalHazard
            ? DeathCause.JuvenileFailure
            : DeathCause.Senescence;
    }

    private void MoveOrganism(ref Organism organism, Genome genome, Vector2 separation, double dt)
    {
        BodyCache body = organism.Body.Cache;
        double c = Math.Cos(organism.HeadingRadians), s = Math.Sin(organism.HeadingRadians);
        Vector2 local = body.PropulsionVector;
        Vector2 propulsion = new((float)(local.X * c - local.Y * s), (float)(local.X * s + local.Y * c));
        double mobility = MediumMobility(organism.Immersion, organism.Hydration, genome.Metabolism.WaterRetention);
        organism.Velocity += (propulsion * (float)(_config.PropulsionAccelerationScale * mobility) + separation) * (float)dt;
        double damping = _config.VelocityDampingPerSecond *
            (organism.Immersion > 0.05 ? 1.0 : _config.LandFrictionMultiplier);
        organism.Velocity *= (float)Math.Exp(-damping * dt);
        float maxSpeed = (float)(_config.MaximumMovementSpeed * mobility /
            (1 + 0.08 * body.Drag / Math.Max(0.1, body.PhysicalMass)));
        if (organism.Velocity.Length() > maxSpeed && maxSpeed > 0)
            organism.Velocity = Vector2.Normalize(organism.Velocity) * maxSpeed;
        Vector2 displacement = organism.Velocity * (float)dt;
        double requested = displacement.Length() * _config.MovementEnergyPerDistance *
            (1 + body.MaximumActivationEnergyPerSecond);
        double paid = organism.Body.ConsumeEnergy(requested);
        if (requested > 0 && paid < requested)
        {
            float fraction = (float)(paid / requested);
            displacement *= fraction; organism.Velocity *= fraction;
        }
        CumulativeDissipatedEnergy += paid; CumulativeMovementEnergy += paid;
        organism.Position += displacement;
        ClampHorizontal(ref organism);

        EnvironmentSample sample = _environment.Sample(organism.Position, organism.Depth);
        if (sample.WaterDepth > 0)
        {
            float half = (float)Math.Max(0.08, body.BoundingRadius * 0.22);
            double buoyancyRatio = body.Buoyancy / Math.Max(0.1, body.PhysicalMass);
            organism.VerticalVelocity += (float)((buoyancyRatio - 1.0) *
                _config.BuoyancyAccelerationScale * dt);
            organism.VerticalVelocity *= (float)Math.Exp(-_config.WaterVerticalDampingPerSecond * dt);
            organism.Depth = Math.Clamp(organism.Depth - organism.VerticalVelocity * (float)dt,
                half, (float)Math.Max(half, sample.WaterDepth - half));
        }
        else { organism.Depth = 0; organism.VerticalVelocity = 0; }
    }

    public static double MediumMobility(double immersion, double hydration, double retention) =>
        Math.Clamp(immersion + ((1.0 - immersion) * 0.14 * hydration * (0.35 + 0.65 * retention)), 0.02, 1.0);

    private void ClampHorizontal(ref Organism organism)
    {
        float max = _config.WorldSize;
        if (organism.Position.X < 0 || organism.Position.X > max)
        { organism.Position.X = Math.Clamp(organism.Position.X, 0, max); organism.Velocity.X *= -0.45f; }
        if (organism.Position.Y < 0 || organism.Position.Y > max)
        { organism.Position.Y = Math.Clamp(organism.Position.Y, 0, max); organism.Velocity.Y *= -0.45f; }
    }

    private Vector2[] ComputeSeparationAccelerations()
    {
        Vector2[] result = new Vector2[_organisms.Count];
        float radius = _config.SeparationRadius;
        Dictionary<(int X, int Y), List<int>> hash = [];
        for (int i = 0; i < _organisms.Count; i++)
        {
            Vector2 p = _organisms[i].Position;
            var key = ((int)MathF.Floor(p.X / radius), (int)MathF.Floor(p.Y / radius));
            if (!hash.TryGetValue(key, out List<int>? bucket)) hash[key] = bucket = [];
            bucket.Add(i);
        }
        for (int a = 0; a < _organisms.Count; a++)
        {
            Vector2 pa = _organisms[a].Position;
            int cx = (int)MathF.Floor(pa.X / radius), cy = (int)MathF.Floor(pa.Y / radius);
            for (int oy = -1; oy <= 1; oy++) for (int ox = -1; ox <= 1; ox++)
            {
                if (!hash.TryGetValue((cx + ox, cy + oy), out List<int>? bucket)) continue;
                foreach (int b in bucket)
                {
                    if (b <= a || !AreWithinContactRange(pa, _organisms[a].Depth,
                            _organisms[b].Position, _organisms[b].Depth, radius)) continue;
                    Vector2 difference = pa - _organisms[b].Position;
                    Vector2 direction;
                    float distance = difference.Length();
                    if (distance < 1e-5f)
                    {
                        double angle = ((_organisms[a].Id * 2654435761UL) ^ _organisms[b].Id) % 65536 * Math.Tau / 65536;
                        direction = new((float)Math.Cos(angle), (float)Math.Sin(angle));
                    }
                    else direction = difference / distance;
                    float threeDistance = MathF.Sqrt(distance * distance +
                        MathF.Pow(_organisms[a].Depth - _organisms[b].Depth, 2));
                    Vector2 push = direction * (float)(_config.SeparationAcceleration * (1 - threeDistance / radius));
                    result[a] += push; result[b] -= push;
                }
            }
        }
        return result;
    }

    private void AddBirthRecord(BirthRecord record)
    {
        if (_recentBirths.Count == RecordLimit) _recentBirths.RemoveAt(0);
        _recentBirths.Add(record);
    }

    private void RecordDeath(Organism organism, Genome genome, DeathCause cause)
    {
        if (_recentDeaths.Count == RecordLimit) _recentDeaths.RemoveAt(0);
        _recentDeaths.Add(new DeathRecord(StepIndex, organism.Id, organism.ParentId, cause,
            organism.AgeSeconds, organism.Body.TotalEnergy, organism.Body.TotalSubstrate,
            organism.Body.TotalOxygen, organism.Hydration,
            organism.Body.DevelopmentCompletion(genome)));
        switch (cause)
        {
            case DeathCause.AccumulatedDamage: DamageDeaths++; break;
            case DeathCause.JuvenileFailure: JuvenileDeaths++; break;
            case DeathCause.Senescence: SenescenceDeaths++; break;
        }
    }

    private double CalculateTotalMatter() => _environment.TotalMinerals + _environment.TotalDetritus +
        _environment.TotalMetabolicWaste + _organisms.Sum(o => o.Body.Cache.TotalMatter + o.Body.TotalSubstrate);
    private double CalculateTotalOxygen() => _environment.TotalOxygen + _organisms.Sum(o => o.Body.TotalOxygen);

    private ulong ComputeFingerprint()
    {
        ulong hash = FingerprintHash.Offset;
        FingerprintHash.Add(ref hash, _seed);
        FingerprintHash.Add(ref hash, unchecked((ulong)StepIndex));
        FingerprintHash.Add(ref hash, unchecked((ulong)CumulativeBirths));
        FingerprintHash.Add(ref hash, unchecked((ulong)CumulativeDeaths));
        FingerprintHash.Add(ref hash, unchecked((ulong)DamageDeaths));
        FingerprintHash.Add(ref hash, unchecked((ulong)JuvenileDeaths));
        FingerprintHash.Add(ref hash, unchecked((ulong)SenescenceDeaths));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(_environment.TotalMinerals)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(_environment.TotalDetritus)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(_environment.TotalMetabolicWaste)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(_environment.TotalOxygen)));
        foreach (Organism o in _organisms)
        {
            FingerprintHash.Add(ref hash, o.Id); FingerprintHash.Add(ref hash, o.ParentId);
            FingerprintHash.Add(ref hash, unchecked((ulong)o.GenomeId));
            FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(o.Position.X)));
            FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(o.Position.Y)));
            FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(o.Depth)));
            FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(o.Velocity.X)));
            FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(o.Velocity.Y)));
            FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(o.VerticalVelocity)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(o.AgeSeconds)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(o.Maturity)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(o.Hydration)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(o.Immersion)));
            foreach (BodyRegion r in o.Body.Regions.OrderBy(r => r.RegionId))
            {
                FingerprintHash.Add(ref hash, unchecked((ulong)r.RegionId));
                foreach (double value in new[] { r.Matter, r.Substrate, r.Oxygen, r.Water, r.Energy, r.Damage })
                    FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
            }
        }
        return hash;
    }

    private static double NormalizeAngle(double angle)
    { angle %= Math.Tau; return angle < 0 ? angle + Math.Tau : angle; }
}
