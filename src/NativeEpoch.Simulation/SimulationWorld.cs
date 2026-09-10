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
    private readonly bool _mutationsEnabled;
    private readonly GenomeMutator _mutator = new();
    private readonly List<BirthRecord> _recentBirths = [];
    private readonly List<DeathRecord> _recentDeaths = [];
    private readonly List<EnvironmentBrushCommand> _pendingEnvironmentCommands = [];
    private readonly List<EnvironmentInterventionRecord> _recentInterventions = [];
    private readonly Dictionary<int, BodyGeometry> _visualGeometryTemplates = [];
    private readonly Dictionary<ulong, double> _mineralDemands = [];
    private readonly Dictionary<ulong, double> _organicDemands = [];
    private readonly Dictionary<ulong, double> _detritusDemands = [];
    private readonly List<LightEnergyRequest> _lightRequests = [];
    private readonly List<MineralUptakeRequest> _producerMineralCompetition = [];
    private EnvironmentResourceSnapshot? _resourcePresentation;
    private long _resourcePresentationStep = -100;
    private List<Organism> _organisms;
    private List<Organism> _nextOrganisms;
    private readonly List<Organism> _birthBuffer = [];
    private ulong _nextOrganismId = 1;
    private readonly double _initialMatter;
    private readonly double _initialOxygen;
    public bool CollectPerformanceMetrics { get; set; }
    public SimulationPerformanceMetrics PerformanceMetrics { get; } = new();

    public SimulationWorld(SimulationConfig config, ulong seed, int ancestorCount,
        Genome? founderGenome = null, bool mutationsEnabled = true)
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
        _mutationsEnabled = mutationsEnabled;
        _organisms = new(Math.Min(config.MaxPopulation, ancestorCount * 2));
        _nextOrganisms = new(Math.Min(config.MaxPopulation, ancestorCount * 2));
        Genomes = new GenomeRegistry();
        Genome? sharedFounder = founderGenome ?? (config.RandomizeFounders ? null : Genome.CreateAncestor());
        SpatialOccupancyIndex initialOccupancy=new(_config.SeparationRadius,_config.WorldSize);

        for (int index = 0; index < ancestorCount; index++)
        {
            Genome ancestorGenome = sharedFounder ?? AquaticFounderFactory.Create(streams.Founders);
            int ancestorGenomeId = Genomes.Register(ancestorGenome);
            DevelopingBody body = new(
                ancestorGenome, config.CoreInitialMatter, config.AncestorStoredMatter,
                config.AncestorEnergy, config.AncestorInternalOxygen, config.CoreInitialMatter, config.PrecisionProfile);
            (Vector2 position, float depth) = FindAquaticSpawn(streams.Placement, body,initialOccupancy);
            Organism ancestor=new()
            {
                Id = _nextOrganismId++, ParentId = 0, GenomeId = ancestorGenomeId,
                Position = position, Velocity = Vector2.Zero, Depth = depth,
                HeadingRadians = streams.Placement.NextUnitDouble() * Math.Tau,
                Hydration = RegionalPhysiology.BodyHydration(body, ancestorGenome), Immersion = 1.0,
                ControllerState = new double[ancestorGenome.ControllerNodes.Count],
                SensorState = new double[ancestorGenome.Sensors.Count],
                TissueControllerInputs = new double[ancestorGenome.ControllerNodes.Count],
                ForagingMemory = new ForagingMemory(), SocialMemory = new SocialMemory(), Body = body,
                Pose = new BodyPose(ancestorGenome, body), Generation = 0,
                AppendageMechanics=new AppendageMechanics(),AppendageRegions=[],
                CavityState=new CavitySystemState(),
                ResourceSatisfaction=1.0
            };
            _organisms.Add(ancestor);
            initialOccupancy.Upsert(new SpatialOccupant(ancestor.Id,position,depth,
                OccupancyShape.FromBody(body)));
        }
        if(config.ResourceBudgetReferenceAncestors is int referenceAncestors)
        {
            double founderMatter=config.CoreInitialMatter+config.AncestorStoredMatter;
            _environment.SetInitialMineralBudget(_environment.TotalMinerals+
                (referenceAncestors-ancestorCount)*founderMatter);
        }
        _initialMatter = CalculateTotalMatter();
        _initialOxygen = CalculateTotalOxygen();
    }

    public long StepIndex { get; private set; }
    public double SimulatedSeconds => StepIndex * _config.FixedDeltaSeconds;
    public long CumulativeBirths { get; private set; }
    public long CumulativeDeaths { get; private set; }
    public long CumulativeBlockedBirthAttempts { get; private set; }
    public double MaximumBlockedBirthResourceLoss { get; private set; }
    public long CumulativeContactPairs { get; private set; }
    public long DamageDeaths { get; private set; }
    public long JuvenileDeaths { get; private set; }
    public long SenescenceDeaths { get; private set; }
    public double CumulativeLightEnergy { get; private set; }
    public double CumulativePredationOrganic { get; private set; }
    public double CumulativeOrganicFeeding { get; private set; }
    public double CumulativeDecomposition { get; private set; }
    public double CumulativePrimaryProduction { get; private set; }
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
        if (count > 0) _resourcePresentation = null;
        return count;
    }

    public void Step()
    {
        long measuredAt = CollectPerformanceMetrics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        ApplyQueuedCommands();
        double dt = _config.FixedDeltaSeconds;
        _environment.UpdateOxygen(dt);
        _environment.UpdateMatterCycles(dt);
        _lightRequests.Clear();
        _producerMineralCompetition.Clear();
        foreach (Organism organism in _organisms)
        {
            Genome genome=Genomes.Get(organism.GenomeId);
            EnvironmentSample sample = _environment.Sample(organism.Position, organism.Depth);
            double lightRequest = sample.Light * organism.Body.Cache.PhotosyntheticSurface *
                _config.LightEnergyPerSurfacePerSecond * dt;
            _lightRequests.Add(new LightEnergyRequest(
                organism.Id, organism.Position, organism.Depth, lightRequest));
            double mineralRequest=RegionalPhysiology.EstimateMineralDemand(
                organism.Body,genome,lightRequest,_config,dt);
            if(mineralRequest>0.0)
                _producerMineralCompetition.Add(new MineralUptakeRequest(
                    organism.Id,organism.Position,mineralRequest));
        }
        ProducerStepResult producers = _environment.UpdateProducers(
            dt,_producerMineralCompetition);
        CumulativeLightEnergy += producers.LightEnergyConsumed;
        CumulativeDissipatedEnergy += producers.MatterRespired * BilinearEnvironmentField.FoodWebEnergyPerMatter;
        CumulativePrimaryProduction += producers.MatterGrown;
        IReadOnlyDictionary<ulong, double> lightAllocations = _environment.AllocateLightEnergy(
            _lightRequests, dt);
        Dictionary<ulong,double> mineralDemands=_mineralDemands;
        Dictionary<ulong,double> organicDemands=_organicDemands;
        Dictionary<ulong,double> detritusDemands=_detritusDemands;
        mineralDemands.Clear();
        organicDemands.Clear();
        detritusDemands.Clear();
        foreach (Organism organism in _organisms)
        {
            Genome genome=Genomes.Get(organism.GenomeId);
            mineralDemands[organism.Id]=RegionalPhysiology.EstimateMineralDemand(
                organism.Body,genome,lightAllocations.GetValueOrDefault(organism.Id),_config,dt);
            organicDemands[organism.Id]=RegionalPhysiology.EstimateOrganicDemand(organism.Body,genome,_config,dt);
            detritusDemands[organism.Id]=RegionalPhysiology.EstimateDetritusDemand(organism.Body,genome,dt);
        }
        var mineralReservations=_environment.ReserveMinerals(_organisms.Select(organism=>
            new MineralUptakeRequest(organism.Id,organism.Position,mineralDemands[organism.Id])));
        var organicReservations=_environment.ReserveOrganic(_organisms.Select(organism=>
            new OrganicUptakeRequest(organism.Id,organism.Position,organicDemands[organism.Id],
                1.0-0.75*Genomes.Get(organism.GenomeId).Metabolism.AnimalFoodAffinity,
                Genomes.Get(organism.GenomeId).Metabolism.AnimalFoodAffinity)));
        var detritusReservations=_environment.ReserveDetritus(_organisms.Select(organism=>
            new DetritusUptakeRequest(organism.Id,organism.Position,detritusDemands[organism.Id])));
        if (CollectPerformanceMetrics)
        {
            PerformanceMetrics.EnvironmentMilliseconds += ElapsedMilliseconds(measuredAt);
            measuredAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        SpatialInteractionFrame interactions = ComputeSpatialInteractions(dt);
        SpatialOccupancyIndex occupancy=interactions.Occupancy;
        if (CollectPerformanceMetrics)
        {
            PerformanceMetrics.SeparationMilliseconds += ElapsedMilliseconds(measuredAt);
            measuredAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        int reproductionSlots = Math.Max(0, _config.MaxPopulation - _organisms.Count);
        _nextOrganisms.Clear();
        _birthBuffer.Clear();

        for (int index = 0; index < _organisms.Count; index++)
        {
            Organism organism = _organisms[index];
            Genome genome = Genomes.Get(organism.GenomeId);
            SpatialInteractionState interaction=interactions.States[index];
            organism.ContactPressure=interaction.ContactPressure;
            organism.ContactNeighborCount=interaction.NeighborCount;
            organism.InteractionOpponentId=interaction.OpponentId;
            organism.InteractionState=interaction.State;
            organism.InteractionIntensity=interaction.Intensity;
            organism.InteractionEnergyLastStep=interaction.EnergySpent;
            organism.InteractionDirection=interaction.Direction;
            organism.AgeSeconds += dt;
            double growthStage = Math.Clamp(organism.AgeSeconds / _config.MaturityAgeSeconds, 0.0, 1.0);
            organism.Maturity = Math.Min(growthStage, organism.Body.DevelopmentCompletion(genome));
            organism.ReproductionCooldownSeconds = Math.Max(0.0, organism.ReproductionCooldownSeconds - dt);

            RegionalExchangeResult exchange = RegionalPhysiology.ExchangeWithEnvironment(
                organism.Body, genome, _environment, organism.Position, organism.HeadingRadians,
                organism.Depth, _config, dt, lightEnergyAllowance:
                    lightAllocations.GetValueOrDefault(organism.Id),
                matterDemand: mineralDemands[organism.Id]+organicDemands[organism.Id]+detritusDemands[organism.Id],
                mineralReservation: mineralReservations.GetValueOrDefault(organism.Id),
                organicReservation: organicReservations.GetValueOrDefault(organism.Id),
                detritusReservation: detritusReservations.GetValueOrDefault(organism.Id));
            organism.ResourceDemandLastStep=exchange.FoodDemand;
            organism.PrimaryProductionLastStep=exchange.PhotosynthesizedSubstrate;
            organism.OrganicFeedingLastStep=exchange.OrganicAssimilated;
            organism.DecompositionLastStep=exchange.DetritusDecomposed;
            CumulativePrimaryProduction+=exchange.PhotosynthesizedSubstrate;
            CumulativeOrganicFeeding+=exchange.OrganicAssimilated;
            CumulativeDecomposition+=exchange.DetritusDecomposed;
            organism.ResourceSatisfaction=exchange.FoodDemand>1e-12
                ?Math.Clamp(exchange.FoodConsumed/exchange.FoodDemand,0.0,1.0):1.0;
            if (CollectPerformanceMetrics)
            {
                PerformanceMetrics.ExchangeMilliseconds += ElapsedMilliseconds(measuredAt);
                measuredAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            organism.WaterExposedArea = exchange.WaterExposedArea;
            organism.AirExposedArea = exchange.AirExposedArea;
            organism.ExposedSurfaceSamples = exchange.ExposedSamples;
            organism.OccludedSurfaceSamples = exchange.OccludedSamples;
            organism.Immersion = exchange.Immersion;
            organism.OxygenUptakeLastStep = exchange.OxygenUptake;
            CumulativeOxygenUptake += exchange.OxygenUptake;
            CumulativeWaterUptake += exchange.WaterUptake;
            CumulativeWaterLoss += exchange.WaterLost;
            CumulativeLightEnergy += exchange.AbsorbedLight;
            CumulativeDissipatedEnergy += exchange.PhotosyntheticHeat + exchange.PhotosyntheticMaintenance;
            organism.LightEnergyLastStep = exchange.LightEnergy;
            CumulativeDissipatedEnergy += exchange.AssimilationEnergySpent;

            RegionalPhysiology.TransportAlongMatterEdges(organism.Body, genome, _config, dt);
            EnvironmentSample controllerEnvironment = _environment.Sample(organism.Position, organism.Depth);
            TissueSensingResult sensing=TissueSensing.Evaluate(organism.Body,genome,_environment,
                organism.Position,organism.HeadingRadians,organism.Depth,organism.ContactPressure,
                _config,dt,ref organism.SensorState,ref organism.TissueControllerInputs);
            organism.ActiveSensorCount=sensing.ActiveSensorCount;
            organism.ChemicalSensorSignal=sensing.ChemicalSignal;
            organism.ContactSensorSignal=sensing.ContactSignal;
            organism.ActiveVisualSensorCount=sensing.ActiveVisualSensorCount;
            organism.VisionSignal=sensing.VisionSignal;
            organism.ChemicalSenseAccess=sensing.ChemicalAccess;
            organism.SensingEnergyLastStep=sensing.EnergySpent;
            CumulativeDissipatedEnergy+=sensing.EnergySpent;
            SocialMotorResponse social=UpdateSocial(ref organism,genome,occupancy,dt);
            ForagingObservation foragingObservation=SenseForaging(organism,genome,sensing);
            double energyFraction=Math.Clamp(organism.Body.TotalEnergy/_config.MaximumEnergy,0,1);
            double meanConductivity=genome.Regions.Average(region=>region.SignalConductivity);
            ForagingDecision foraging=BehaviorController.UpdateForaging(organism.ForagingMemory,
                foragingObservation,energyFraction,meanConductivity,_config,dt,spherical:true);
            organism.ForagingCue=foragingObservation.CenterCue;
            organism.ForagingTrend=foraging.ResourceTrend;
            organism.ExplorationDrive=foraging.Activity;
            organism.SteeringDrive=foraging.Steering;
            organism.ControllerInputs = new ControllerInputs(
                sensing.AmbientLightSignal,
                sensing.TemperatureSignal,
                sensing.PressureSignal,
                Math.Clamp(organism.Body.TotalEnergy / _config.MaximumEnergy, 0, 1),
                Math.Clamp(organism.Body.TotalSubstrate / Math.Max(0.1, organism.Body.Cache.TotalMatter), 0, 1),
                organism.Hydration,
                sensing.ContactSignal,
                foraging.ResourceGradient,
                foraging.ResourceLateral,
                foraging.ResourceTrend,
                foraging.Novelty,foraging.Danger*sensing.ChemicalAccess,foraging.ExplorationSignal);
            ControllerEvaluation control = BehaviorController.Evaluate(
                genome, organism.ControllerState, organism.ControllerInputs,
                organism.TissueControllerInputs);
            double controllerCost = genome.ControllerNodes.Count * _config.ControllerNodeEnergyPerSecond * dt;
            double controllerPaid = organism.Body.ConsumeEnergy(controllerCost);
            organism.ControllerState = control.State;
            organism.ControllerOutputs = controllerPaid + 1e-12 >= controllerCost
                ? ApplySocialDrive(ApplyForagingDrive(control.Outputs,foraging),social) : ControllerOutputs.Basal;
            CumulativeDissipatedEnergy += controllerPaid;
            organism.Body.UpdateFunctionalState(genome, organism.ControllerOutputs,
                controllerEnvironment.Light, controllerEnvironment.Pressure, foraging, dt);
            if (CollectPerformanceMetrics)
            {
                PerformanceMetrics.ControlTransportMilliseconds += ElapsedMilliseconds(measuredAt);
                measuredAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            MoveOrganism(ref organism, genome, interaction.Acceleration, dt);
            StepCavities(ref organism,genome,dt);
            ApplyMediumStress(ref organism, genome, dt);
            if (CollectPerformanceMetrics)
            {
                PerformanceMetrics.MechanicsMilliseconds += ElapsedMilliseconds(measuredAt);
                measuredAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }
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
            if (grown > 0.0)
            {
                organism.Pose.Synchronize(genome, organism.Body);
                organism.Body.ApplyPoseGeometry(genome, organism.Pose);
            }
            organism.Maturity = Math.Min(growthStage, organism.Body.DevelopmentCompletion(genome));
            ResolveOccupancy(ref organism,occupancy);
            occupancy.Upsert(new SpatialOccupant(organism.Id,organism.Position,organism.Depth,
                OccupancyShape.FromBody(organism.Body)));

            bool canReproduce = reproductionSlots > 0 && organism.Maturity >= 0.95 &&
                organism.Body.DevelopmentCompletion(genome) >= 0.95 &&
                organism.ReproductionCooldownSeconds <= 0.0 &&
                organism.Body.TotalEnergy >= _config.ReproductionEnergyThreshold &&
                organism.Body.TotalEnergy >= _config.ReproductionEnergyCost &&
                organism.Body.TotalSubstrate >= _config.CoreInitialMatter + _config.NewbornSubstrate;
            if (canReproduce)
            {
                double energyBeforePlacement=organism.Body.TotalEnergy;
                double substrateBeforePlacement=organism.Body.TotalSubstrate;
                MutationResult inheritance = _mutationsEnabled
                    ? _mutator.Inherit(genome, _mutationRandom, _controllerMutationRandom)
                    : new MutationResult(genome, MutationKind.None, "mutation frozen for paired assay");
                double childCoreMatter=NewbornCoreMatter(inheritance.Genome);
                DevelopingBody provisional=new(inheritance.Genome,childCoreMatter,
                    _config.NewbornSubstrate,_config.NewbornEnergy,0.0,childCoreMatter*0.5,_config.PrecisionProfile);
                if(TryFindNewbornPlacement(organism,provisional,occupancy,out Vector2 childPosition,out float childDepth))
                {
                    organism.Body.ConsumeEnergy(_config.ReproductionEnergyCost);
                    organism.Body.ConsumeSubstrate(childCoreMatter + _config.NewbornSubstrate);
                    organism.ReproductionCooldownSeconds = _config.ReproductionCooldownSeconds;
                    CumulativeDissipatedEnergy += _config.ReproductionEnergyCost - _config.NewbornEnergy;
                    int childGenomeId = Genomes.Register(inheritance.Genome);
                    ulong childId = _nextOrganismId++;
                    Organism child = CreateNewborn(childId, ref organism, childGenomeId,
                        inheritance.Genome,childPosition,childDepth);
                    child.BirthMutationCount=inheritance.EventCount;
                    _birthBuffer.Add(child);
                    occupancy.Upsert(new SpatialOccupant(child.Id,child.Position,child.Depth,
                        OccupancyShape.FromBody(child.Body)));
                    AddBirthRecord(new(organism.Id, childId, organism.GenomeId, childGenomeId,
                        genome.Regions.Count, inheritance.Genome.Regions.Count,
                        inheritance.Kind, inheritance.Summary)
                    {
                        MutationEventCount=inheritance.EventCount,
                        MutationEventKinds=inheritance.EventKinds
                    });
                    reproductionSlots--;
                    CumulativeBirths++;
                }
                else
                {
                    MaximumBlockedBirthResourceLoss=Math.Max(MaximumBlockedBirthResourceLoss,
                        Math.Abs(energyBeforePlacement-organism.Body.TotalEnergy)+
                        Math.Abs(substrateBeforePlacement-organism.Body.TotalSubstrate));
                    organism.ReproductionCooldownSeconds=_config.ReproductionBlockedRetrySeconds;
                    CumulativeBlockedBirthAttempts++;
                }
            }
            if (CollectPerformanceMetrics)
            {
                PerformanceMetrics.MetabolismGrowthMilliseconds += ElapsedMilliseconds(measuredAt);
                measuredAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            DeathCause? deathCause = organism.Body.AverageDamage >= 1.0
                ? DeathCause.AccumulatedDamage
                : SampleMortality(organism, genome, dt);
            if (deathCause is not null)
            {
                occupancy.Remove(organism.Id);
                RecordDeath(organism, genome, deathCause.Value);
                CumulativeDissipatedEnergy += organism.Body.TotalEnergy;
                _environment.DepositDetritus(
                    organism.Position, organism.Body.Cache.TotalMatter + organism.Body.TotalSubstrate);
                CavityPhysiology.ReleaseAll(organism.CavityState,_environment,
                    organism.Position,organism.Depth,organism.Immersion);
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
        if (CollectPerformanceMetrics)
            PerformanceMetrics.FinalizeMilliseconds += ElapsedMilliseconds(measuredAt);
    }

    private static double ElapsedMilliseconds(long started) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

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
                o.Body.Geometry, VisualTemplateGeometry(o.GenomeId,g),
                o.Pose.Regions.Select(region => new BodyVisualRegion(region.RegionId, region.LocalCenter,
                    region.Angle, region.Length, region.Width, region.Thickness,
                    o.Body.Geometry.Regions.Single(r => r.RegionId == region.RegionId).Color)).ToArray(),
                o.Body.Regions.ToArray())
            {
                Generation=o.Generation,ExplorationDrive=o.ExplorationDrive,
                BirthMutationCount=o.BirthMutationCount,
                LightEnergyLastStep=o.LightEnergyLastStep,
                OrganicFeedingLastStep=o.OrganicFeedingLastStep,
                DecompositionLastStep=o.DecompositionLastStep,
                PrimaryProductionLastStep=o.PrimaryProductionLastStep,
                PredationLastStep=o.PredationLastStep,
                AttackDamageLastStep=o.AttackDamageLastStep,RetaliationDamageLastStep=o.RetaliationDamageLastStep,
                AttackAffinity=Genomes.Get(o.GenomeId).Metabolism.AttackAffinity,
                RetaliationAffinity=Genomes.Get(o.GenomeId).Metabolism.RetaliationAffinity,
                OffspringMutationProbability=_mutationsEnabled?GenomeMutator.NaturalMutationProbability(g.MutationRate):0,
                ForagingTrend=o.ForagingTrend,ContactNeighborCount=o.ContactNeighborCount,
                InteractionOpponentId=o.InteractionOpponentId,InteractionState=o.InteractionState,
                InteractionIntensity=o.InteractionIntensity,InteractionDirection=o.InteractionDirection,
                ResourceSatisfaction=o.ResourceSatisfaction,ResourceDemandLastStep=o.ResourceDemandLastStep,
                MeanTissueExpression=o.Body.Regions.Count>0?o.Body.Regions.Average(region=>(
                    region.ExchangeExpression+region.BarrierExpression+region.ContractileExpression+
                    region.StructuralExpression+region.SensoryExpression+region.PhotosyntheticExpression+
                    region.FeedingExpression+region.DigestiveExpression+region.DecomposerExpression)/9.0):0.0,
                ActiveSensorCount=o.ActiveSensorCount,ChemicalSensorSignal=o.ChemicalSensorSignal,ChemicalSenseAccess=o.ChemicalSenseAccess,
                ContactSensorSignal=o.ContactSensorSignal,SensingEnergyLastStep=o.SensingEnergyLastStep,
                ActiveVisualSensorCount=o.ActiveVisualSensorCount,VisionSignal=o.VisionSignal,
                SocialResponse=o.SocialResponse,SocialTargetId=o.SocialTargetId,
                ActiveIndividualSensors=o.ActiveIndividualSensors,
                AnimalFoodAffinity=Genomes.Get(o.GenomeId).Metabolism.AnimalFoodAffinity,
                BodyCenterElevation=BodyCenterElevation(o),AppendageRegions=o.AppendageRegions.ToArray(),
                AppendageContactCount=o.AppendageContactCount,AppendageSupport=o.AppendageSupport,
                AppendageGroundVelocity=o.AppendageGroundVelocity,AppendageEnergyLastStep=o.AppendageEnergyLastStep,
                CavityOxygen=o.CavityState.TotalOxygen,
                CavityOxygenCapacity=o.CavityState.Regions.Sum(region=>region.OxygenCapacity),
                CavityVentilationLastStep=o.CavityVentilationLastStep,
                CavityTissueOxygenLastStep=o.CavityTissueOxygenLastStep,
                CavityEnergyLastStep=o.CavityEnergyLastStep
            };
        }
        if (_resourcePresentation is null || StepIndex - _resourcePresentationStep >= 10)
        {
            _resourcePresentation = _environment.CaptureResourceSnapshot();
            _resourcePresentationStep = StepIndex;
        }
        return new(CaptureSnapshot(fullValidation: false), result) { Resources = _resourcePresentation };
    }

    private BodyGeometry VisualTemplateGeometry(int genomeId, Genome genome)
    {
        if (_visualGeometryTemplates.TryGetValue(genomeId,out BodyGeometry? geometry)) return geometry;
        BodyRegion[] mature = genome.Regions.Select(gene => new BodyRegion(
            gene.RegionId,BodyCalculator.TargetMatter(gene),1.0)).ToArray();
        geometry=BodyGeometryBuilder.Build(genome,mature);
        _visualGeometryTemplates[genomeId]=geometry;
        return geometry;
    }

    private double NewbornCoreMatter(Genome genome)
    {
        // Small inherited bodies remain valid juveniles, with exactly this
        // amount debited from the parent's substrate before the birth.
        RegionGene core=genome.Regions.First(region=>region.IsCore);
        return Math.Min(_config.CoreInitialMatter,BodyCalculator.TargetMatter(core)*0.8);
    }

    private double BodyCenterElevation(Organism organism)
    {
        EnvironmentSample sample=_environment.Sample(organism.Position,organism.Depth);
        return sample.WaterDepth>0.0
            ?sample.WaterSurface-organism.Depth
            :sample.TerrainHeight+OccupancyShape.FromBody(organism.Body).VerticalHalfExtent;
    }

    public SimulationSnapshot CaptureSnapshot() => CaptureSnapshot(fullValidation: true);

    private SimulationSnapshot CaptureSnapshot(bool fullValidation)
    {
        double bodyMatter = 0, stored = 0, energy = 0, maturity = 0, speed = 0;
        double organismOxygen = 0, hydration = 0, depth = 0;
        int regions = 0, aquatic = 0, shore = 0, land = 0;
        bool finite = true;
        foreach (Organism o in _organisms)
        {
            Genome g = Genomes.Get(o.GenomeId);
            bodyMatter += o.Body.Cache.TotalMatter; stored += o.Body.TotalSubstrate;
            energy += o.Body.TotalEnergy; organismOxygen += o.Body.TotalOxygen+o.CavityState.TotalOxygen;
            maturity += o.Maturity; speed += o.Velocity.Length(); depth += o.Depth;
            hydration += o.Hydration; regions += o.Body.RegionCount;
            if (o.Immersion >= 0.8) aquatic++; else if (o.Immersion > 0.05) shore++; else land++;
            finite &= o.AllFinite && o.Body.AllFinite(g);
        }
        bool cacheValid = true;
        double cacheError = 0.0;
        if (fullValidation)
            cacheValid = ValidateBodyCaches(out cacheError);
        double minerals = _environment.TotalMinerals;
        double detritus = _environment.TotalDetritus;
        double waste = _environment.TotalMetabolicWaste;
        double totalMatter = _environment.TotalEnvironmentMatter + bodyMatter + stored;
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
            finite && cacheValid, fullValidation ? ComputeFingerprint() : 0UL)
        {
            EnvironmentVegetation = _environment.TotalVegetation,
            EnvironmentEdibleOrganics = _environment.TotalEdibleOrganics,
            CumulativePrimaryProduction = CumulativePrimaryProduction,
            CumulativeOrganicFeeding = CumulativeOrganicFeeding,
            CumulativeDecomposition = CumulativeDecomposition,
            CumulativePredationOrganic = CumulativePredationOrganic
        };
    }

    public static bool AreWithinContactRange(
        Vector2 firstPosition, float firstDepth, Vector2 secondPosition, float secondDepth, float radius,
        float worldSize=512f)
    {
        float vertical = firstDepth - secondDepth;
        double surface = SphericalWorld.Distance(firstPosition, secondPosition, worldSize);
        return (surface * surface) + (vertical * vertical) < radius * radius;
    }

    public bool RelocateForMediumDiagnostic(ulong organismId, Vector2 position, float depth,
        double? headingRadians=null)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            position.X < 0 || position.Y < 0 || position.X > _config.WorldSize || position.Y > _config.WorldSize)
            throw new ArgumentOutOfRangeException(nameof(position));
        int index = _organisms.FindIndex(organism => organism.Id == organismId);
        if (index < 0) return false;
        Organism organism = _organisms[index];
        position=SphericalWorld.Normalize(position,_config.WorldSize);
        EnvironmentSample sample = _environment.Sample(position);
        organism.Position = position;
        organism.Velocity = Vector2.Zero;
        organism.VerticalVelocity = 0;
        if(headingRadians.HasValue)organism.HeadingRadians=NormalizeAngle(headingRadians.Value);
        organism.Depth = sample.WaterDepth > 0.0
            ? Math.Clamp(depth, 0f, (float)sample.WaterDepth)
            : 0f;
        organism.Immersion = sample.WaterDepth > 0.0 && organism.Depth > 0f ? 1.0 : 0.0;
        _organisms[index] = organism;
        return true;
    }

    private (Vector2 Position, float Depth) FindAquaticSpawn(
        DeterministicRandom random, DevelopingBody body,SpatialOccupancyIndex? occupancy=null)
    {
        float halfThickness = (float)Math.Max(0.08, body.Cache.BoundingRadius * 0.22);
        float horizontalRadius=OccupancyShape.FromBody(body).HorizontalRadius;
        if(horizontalRadius*2.0f>=_config.WorldSize)
            throw new InvalidOperationException("Body is wider than the simulated world.");
        float minimum = Math.Max(_config.MinimumAquaticSpawnDepth, halfThickness * 2.2f);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int attempt = 0; attempt < _config.MaximumAquaticSpawnAttempts; attempt++)
            {
                double cosineTheta=1.0-(2.0*random.NextUnitDouble());
                Vector2 position = new(
                    random.NextFloat(0f, _config.WorldSize),
                    (float)(Math.Acos(cosineTheta)*_config.WorldSize/Math.PI));
                EnvironmentSample sample = _environment.Sample(position);
                bool preferredPhoticShelf = sample.WaterDepth <= _config.PreferredAquaticSpawnMaximumDepth;
                if (sample.WaterDepth < minimum || (pass == 0 && !preferredPhoticShelf))
                    continue;
                float depth = (float)Math.Clamp(sample.WaterDepth * _config.InitialAquaticDepthFraction,
                    halfThickness, Math.Min(sample.WaterDepth - halfThickness, 6.0));
                if(occupancy is not null&&!occupancy.CanPlace(position,depth,OccupancyShape.FromBody(body)))
                    continue;
                return (position, depth);
            }
        }
        throw new InvalidOperationException(
            $"No unoccupied aquatic spawn found after {_config.MaximumAquaticSpawnAttempts * 2} bounded attempts.");
    }

    private bool TryFindNewbornPlacement(Organism parent,DevelopingBody child,
        SpatialOccupancyIndex occupancy,out Vector2 position,out float depth)
    {
        OccupancyShape parentShape=OccupancyShape.FromBody(parent.Body);
        OccupancyShape childShape=OccupancyShape.FromBody(child);
        float minimumDistance=(parentShape.HorizontalRadius+childShape.HorizontalRadius)*1.04f;
        float maximumDistance=minimumDistance+_config.NewbornOffsetRadius;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            double angle = _reproductionRandom.NextUnitDouble() * Math.Tau;
            float distance = _reproductionRandom.NextFloat(minimumDistance,maximumDistance);
            Vector2 candidate=SphericalWorld.OffsetPosition(parent.Position,new Vector2(
                (float)Math.Cos(angle)*distance,(float)Math.Sin(angle)*distance),_config.WorldSize);
            EnvironmentSample sample = _environment.Sample(candidate);
            // Only founders require an aquatic spawn. Descendants may occupy
            // land or shallow water; physiology decides whether they survive.
            float candidateDepth = 0f;
            if (sample.WaterDepth > 0.0)
            {
                float half = Math.Min(childShape.VerticalHalfExtent, (float)sample.WaterDepth * 0.5f);
                float verticalRange = parentShape.VerticalHalfExtent + childShape.VerticalHalfExtent + 0.15f;
                candidateDepth = (float)Math.Clamp(parent.Depth +
                    ((_reproductionRandom.NextUnitDouble() - 0.5) * 2.0 * verticalRange),
                    half, Math.Max(half, sample.WaterDepth - half));
            }
            if(!occupancy.CanPlace(candidate,candidateDepth,childShape))continue;
            position=candidate;depth=candidateDepth;return true;
        }
        position=default;depth=0;return false;
    }

    private Organism CreateNewborn(ulong id, ref Organism parent, int genomeId, Genome genome,
        Vector2 position,float depth)
    {
        double transferredOxygen = Math.Min(parent.Body.TotalOxygen * 0.18, _config.AncestorInternalOxygen * 0.5);
        double transferredWater = Math.Min(parent.Body.TotalWater * 0.12, _config.CoreInitialMatter * 0.5);
        parent.Body.ConsumeOxygen(transferredOxygen);
        parent.Body.ConsumeWater(transferredWater);
        DevelopingBody body = new(genome, NewbornCoreMatter(genome), _config.NewbornSubstrate, _config.NewbornEnergy,
            transferredOxygen, transferredWater, _config.PrecisionProfile);
        return new Organism
        {
            Id = id, ParentId = parent.Id, GenomeId = genomeId, Position = position,
            Depth = depth, HeadingRadians = NormalizeAngle(parent.HeadingRadians +
                ((_reproductionRandom.NextUnitDouble() - 0.5) * 0.35)),
            Hydration = RegionalPhysiology.BodyHydration(body, genome),
            Immersion = _environment.Sample(position).WaterDepth > 0.0 && depth > 0f ? 1.0 : 0.0,
            ReproductionCooldownSeconds = _config.ReproductionCooldownSeconds,
            ControllerState = new double[genome.ControllerNodes.Count],
            SensorState = new double[genome.Sensors.Count],
            TissueControllerInputs = new double[genome.ControllerNodes.Count],
            ForagingMemory = new ForagingMemory(), SocialMemory = new SocialMemory(), Body = body,
            Pose = new BodyPose(genome, body), Generation = parent.Generation + 1,
            AppendageMechanics=new AppendageMechanics(),AppendageRegions=[],
            CavityState=new CavitySystemState(),
            ResourceSatisfaction=1.0
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
        double energyNeed = Math.Max(1e-6,
            _config.BaseMaintenanceEnergyPerSecond * organism.Body.Cache.TotalMatter +
            organism.Body.Cache.MaintenanceEnergyPerSecond);
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

    private ForagingObservation SenseForaging(Organism organism,Genome genome,TissueSensingResult sensing)
    {
        // Probe geometry also supplies self-motion/novelty memory, even without receptors.
        float distance=(float)Math.Max(0.1,Math.Min(_config.ForagingSenseDistance,
            sensing.ChemicalRange>0.0?sensing.ChemicalRange:_config.ForagingSenseDistance));
        Vector2 forward=new((float)Math.Cos(organism.HeadingRadians),(float)Math.Sin(organism.HeadingRadians));
        Vector2 right=new(-forward.Y,forward.X);
        Vector2 center=organism.Position;
        Vector2 ahead=SphericalWorld.OffsetPosition(center,forward*distance,_config.WorldSize);
        Vector2 left=SphericalWorld.OffsetPosition(center,-right*distance,_config.WorldSize);
        Vector2 rightPosition=SphericalWorld.OffsetPosition(center,right*distance,_config.WorldSize);
        EnvironmentSample centerSample=_environment.Sample(center,organism.Depth);
        double growthRemaining=Math.Max(0.0,genome.Regions.Sum(BodyCalculator.TargetMatter)-organism.Body.Cache.TotalMatter);
        double substrateTarget=Math.Max(organism.Body.Cache.TotalMatter*0.35,
            _config.CoreInitialMatter+_config.NewbornSubstrate+growthRemaining);
        double reserve=Math.Clamp(organism.Body.TotalSubstrate/Math.Max(0.10,substrateTarget),0,1);
        double area=Math.Max(0.01,organism.Body.Cache.ExposedSurface);
        double production=organism.Body.Cache.PhotosyntheticSurface/area;
        double feeding=RegionalPhysiology.FeedingCapacity(organism.Body,genome)/area;
        double decomposition=RegionalPhysiology.DecompositionCapacity(organism.Body,genome)/area;
        double Cue(EnvironmentSample sample)
        {
            double available=Math.Max(0.0,sample.Minerals*production+
                (sample.ProducerBiomass+sample.EdibleOrganics*genome.Metabolism.AnimalFoodAffinity)*feeding+sample.Detritus*decomposition);
            return (available/(0.12+available))*(0.35+0.65*(1.0-reserve));
        }
        double cue=Cue(centerSample);
        double aheadCue=cue,leftCue=cue,rightCue=cue;
        if(sensing.ChemicalAccess>1e-6 && sensing.ChemicalRange>0.0)
        {
            aheadCue=Cue(_environment.Sample(ahead,organism.Depth));
            leftCue=Cue(_environment.Sample(left,organism.Depth));
            rightCue=Cue(_environment.Sample(rightPosition,organism.Depth));
        }
        // Drying/pressure discomfort is local. Smell does not reveal remote light,
        // oxygen or terrain hazards. Directional light arrives through paid receptors.
        double danger=EnvironmentalDanger(centerSample,organism,genome);
        double lightBenefit=Math.Clamp(organism.Body.Cache.PhotosyntheticSurface/
            Math.Max(0.01,organism.Body.Cache.TotalMatter),0.0,1.0);
        return new(center,ahead,left,rightPosition,cue,aheadCue,leftCue,rightCue,
            danger,danger,danger,danger)
        {
            ChemicalAccess=sensing.ChemicalAccess,
            VisualForwardSignal=sensing.VisualForwardSignal*lightBenefit,
            VisualLateralSignal=sensing.VisualLateralSignal*lightBenefit
        };
    }

    private static double EnvironmentalDanger(EnvironmentSample sample,Organism organism,Genome genome)
    {
        double dry=sample.WaterDepth<=0
            ? (1.0-genome.Metabolism.WaterRetention)*(0.5+(0.5*(1.0-organism.Hydration))) : 0;
        double pressure=Math.Clamp((sample.Pressure-(1.0+genome.Metabolism.OsmoticTolerance))/8.0,0,1);
        double oxygen=sample.WaterDepth>0?sample.DissolvedOxygenAvailability:sample.AirOxygenAvailability;
        return Math.Clamp(dry+(0.45*pressure)+(0.35*Math.Max(0,0.25-oxygen)),0,1);
    }

    private readonly List<SocialCandidate> _socialCandidates = new(8);

    private SocialMotorResponse UpdateSocial(ref Organism organism, Genome genome,
        SpatialOccupancyIndex occupancy, double dt)
    {
        organism.SocialMemory.Advance(dt);
        if (organism.Body.TotalEnergy <= _config.MaximumEnergy * _config.ForagingRestEnergyFraction)
            organism.ActiveIndividualSensors = 0;
        if ((StepIndex + (long)organism.Id) % 5 == 0 &&
            organism.Body.TotalEnergy > _config.MaximumEnergy * _config.ForagingRestEnergyFraction)
        {
            organism.ActiveIndividualSensors = 0;
            double range = 0;
            foreach (SensorGene sensor in genome.Sensors)
            {
                if (sensor.Channel != SensorChannel.OrganismContrast) continue;
                int index = organism.Body.IndexOfRegion(sensor.SourceRegionId);
                if(index >= 0 && organism.Body.Regions[index].SensoryExpression > 1e-6)
                    range = Math.Max(range, Math.Min(12, sensor.Range));
            }
            if (range > 0)
            {
                _socialCandidates.Clear();
                int inspected = 0;
                foreach (SpatialOccupant other in occupancy.Query(organism.Position,(float)range))
                {
                    if(other.Id == organism.Id) continue;
                    if(++inspected > 48) break;
                    double distance=SphericalWorld.Distance(organism.Position,other.Position,_config.WorldSize);
                    if(distance>range) continue;
                    EnvironmentSample sample=_environment.Sample(other.Position,other.Depth);
                    float elevation=(float)(sample.WaterDepth>0 ? sample.WaterSurface-other.Depth :
                        sample.TerrainHeight+other.Shape.VerticalHalfExtent);
                    SocialCandidate candidate=new(other.Id,other.Position,other.Depth,
                        other.Shape.HorizontalRadius,elevation);
                    int slot=0;
                    while(slot<_socialCandidates.Count && SphericalWorld.Distance(organism.Position,
                        _socialCandidates[slot].Position,_config.WorldSize)<=distance) slot++;
                    if(slot>=8) continue;
                    _socialCandidates.Insert(slot,candidate);
                    if(_socialCandidates.Count>8) _socialCandidates.RemoveAt(8);
                }
                SocialPerceptionResult sight=SocialPerception.Evaluate(organism.Body,genome,_environment,
                    organism.Position,organism.Depth,organism.HeadingRadians,_socialCandidates,_config,dt*5);
                organism.ActiveIndividualSensors=sight.ActiveSensors;
                organism.SocialMemory.Observe(sight);
                organism.SensingEnergyLastStep+=sight.EnergySpent;
                CumulativeDissipatedEnergy+=sight.EnergySpent;
            }
        }
        SocialMotorResponse response=organism.SocialMemory.Respond(organism.Body,genome,
            organism.Position,organism.HeadingRadians,_config,dt);
        organism.SocialResponse=response.Mode;
        organism.SocialTargetId=response.TargetId;
        organism.SensingEnergyLastStep+=response.EnergySpent;
        CumulativeDissipatedEnergy+=response.EnergySpent;
        return response;
    }

    private static ControllerOutputs ApplySocialDrive(ControllerOutputs output, SocialMotorResponse social)
        => output with
        {
            LateralContraction=Math.Clamp(output.LateralContraction+social.Steering,-1,1),
            ContractionActivation=Math.Clamp(output.ContractionActivation*(1+0.6*social.Activation),0,1)
        };

    private static ControllerOutputs ApplyForagingDrive(ControllerOutputs output,ForagingDecision foraging)
        =>output with
        {
            ContractionActivation=Math.Clamp((0.55+(0.45*output.ContractionActivation))*foraging.Activity,0,1),
            LateralContraction=Math.Clamp((0.40*output.LateralContraction)+(0.85*foraging.Steering),-1,1),
            VerticalContraction=Math.Clamp(output.VerticalContraction*(0.35+(0.65*foraging.Activity)),-1,1)
        };

    private void MoveOrganism(ref Organism organism, Genome genome, Vector2 separation, double dt)
    {
        BodyCache body = organism.Body.Cache;
        OccupancyShape occupancyShape=OccupancyShape.FromBody(organism.Body);
        EnvironmentSample preMoveEnvironment = _environment.Sample(organism.Position, organism.Depth);
        float supportHalfThickness=occupancyShape.VerticalHalfExtent;
        bool groundSupported = HasGroundSupport(
            preMoveEnvironment.WaterDepth, organism.Depth, supportHalfThickness);
        BodyMechanicsResult mechanics = organism.Pose.Step(genome, organism.Body,
            organism.ControllerOutputs, organism.AgeSeconds, organism.Immersion,
            organism.Hydration, _config, dt, groundSupported, false);
        organism.Body.ApplyPoseGeometry(genome, organism.Pose);
        double centerElevation=preMoveEnvironment.WaterDepth>0.0
            ?preMoveEnvironment.WaterSurface-organism.Depth
            :preMoveEnvironment.TerrainHeight+supportHalfThickness;
        AppendageMechanicsResult appendage=organism.AppendageMechanics.Step(genome,organism.Body,
            organism.Pose,organism.ControllerOutputs,organism.Position,organism.HeadingRadians,
            centerElevation,_environment,_config,organism.AgeSeconds,dt,true);
        organism.AppendageRegions=appendage.Regions;
        int plantedContacts=0;
        for(int contactIndex=0;contactIndex<appendage.Contacts.Count;contactIndex++)
            if(appendage.Contacts[contactIndex].Planted)plantedContacts++;
        organism.AppendageContactCount=plantedContacts;
        organism.AppendageSupport=appendage.SupportFraction;
        organism.AppendageGroundVelocity=appendage.GroundVelocity;
        organism.AppendageEnergyLastStep=appendage.EnergySpent;
        double c = Math.Cos(organism.HeadingRadians), s = Math.Sin(organism.HeadingRadians);
        Vector2 local = mechanics.MediumVelocity;
        Vector2 reactionVelocity = appendage.GroundVelocity+
            new Vector2((float)(local.X * c - local.Y * s), (float)(local.X * s + local.Y * c));
        double mobility = MediumMobility(organism.Immersion, organism.Hydration, genome.Metabolism.WaterRetention);
        // The mechanics solve returns a velocity from the force/torque balance,
        // rather than an acceleration.  Separation is the only acceleration here.
        organism.Velocity = reactionVelocity + separation * (float)dt;
        double angularVelocity=appendage.AngularVelocity+mechanics.AngularVelocity;
        organism.LocalActuationForce = mechanics.NetExternalForce;
        organism.ActuationTorque = mechanics.NetExternalTorque;
        float maxSpeed = (float)(_config.MaximumMovementSpeed * mobility /
            (1 + 0.08 * body.Drag / Math.Max(0.1, body.PhysicalMass)));
        if (organism.Velocity.Length() > maxSpeed && maxSpeed > 0)
        {
            float scale = maxSpeed / organism.Velocity.Length();
            organism.Velocity *= scale;
            // Preserve the solved trajectory curvature when bounding locomotion.
            angularVelocity *= scale;
        }
        organism.HeadingRadians = NormalizeAngle(organism.HeadingRadians + angularVelocity * dt);
        Vector2 displacement = organism.Velocity * (float)dt;
        CumulativeDissipatedEnergy += mechanics.EnergySpent+appendage.EnergySpent;
        CumulativeMovementEnergy += mechanics.EnergySpent+appendage.EnergySpent;
        Vector2 previousPosition=organism.Position;
        Vector2 nextPosition=SphericalWorld.Advance(
            previousPosition,organism.Velocity,dt,_config.WorldSize);
        Vector2 headingVector=new((float)Math.Cos(organism.HeadingRadians),
            (float)Math.Sin(organism.HeadingRadians));
        organism.Velocity=SphericalWorld.Transport(
            previousPosition,nextPosition,organism.Velocity,_config.WorldSize);
        Vector2 transportedHeading=SphericalWorld.Transport(
            previousPosition,nextPosition,headingVector,_config.WorldSize);
        if(transportedHeading.LengthSquared()>1e-12f)
            organism.HeadingRadians=NormalizeAngle(Math.Atan2(transportedHeading.Y,transportedHeading.X));
        organism.Position=nextPosition;

        EnvironmentSample sample = _environment.Sample(organism.Position, organism.Depth);
        if (sample.WaterDepth > 0)
        {
            VerticalMotionResult vertical=VerticalMotionMechanics.Step(organism.Body,genome,
                organism.ControllerOutputs,ref organism.Depth,ref organism.VerticalVelocity,
                sample.WaterDepth,occupancyShape.VerticalHalfExtent,organism.Immersion,
                organism.Hydration,_config,dt);
            CumulativeDissipatedEnergy+=vertical.EnergySpent;
            CumulativeMovementEnergy+=vertical.EnergySpent;
        }
        else { organism.Depth = 0; organism.VerticalVelocity = 0; }
    }

    private void StepCavities(ref Organism organism,Genome genome,double dt)
    {
        Span<CavityRegionInput> inputs=stackalloc CavityRegionInput[GenomeValidator.MaximumRegions];
        int count=0;
        double c=Math.Cos(organism.HeadingRadians),s=Math.Sin(organism.HeadingRadians);
        double centerElevation=BodyCenterElevation(organism);
        foreach(BodyRegion region in organism.Body.Regions)
        {
            RegionGene gene=genome.GetRegion(region.RegionId);
            int bodyIndex=organism.Body.IndexOfRegion(region.RegionId);
            BodyFunctionalGeometry functional=organism.Body.FunctionalGeometry[bodyIndex];
            BodyGeometryRegion geometry=organism.Body.Geometry.Regions[bodyIndex];
            BodySurfaceSample apertureSample=default;
            bool connected=false;
            int sampleCount=functional.SurfaceSamples.Count;
            int start=sampleCount>0?Math.Abs(region.RegionId)%sampleCount:0;
            if(sampleCount>0)
            {apertureSample=functional.SurfaceSamples[start];connected=apertureSample.ExternallyConnected;}
            double liftedY=0.0;
            foreach(AppendageRegionPose pose in organism.AppendageRegions)
                if(pose.RegionId==region.RegionId){liftedY=(pose.LocalStart.Y+pose.LocalEnd.Y)*0.5;break;}
            Vector2 samplePosition=organism.Position;
            float sampleDepth=organism.Depth;
            double air=0,water=0;
            if(connected)
            {
                double localX=apertureSample.LocalPosition.X*c-apertureSample.LocalPosition.Z*s;
                double localY=apertureSample.LocalPosition.X*s+apertureSample.LocalPosition.Z*c;
                samplePosition=SphericalWorld.OffsetPosition(
                    organism.Position,new Vector2((float)localX,(float)localY),_config.WorldSize);
                EnvironmentSample surface=_environment.Sample(samplePosition);
                double elevation=centerElevation+apertureSample.LocalPosition.Y+liftedY;
                bool insideTerrain=elevation<surface.TerrainHeight;
                bool waterSide=!insideTerrain&&surface.WaterDepth>0&&elevation<=surface.WaterSurface;
                sampleDepth=waterSide?(float)Math.Clamp(surface.WaterSurface-elevation,0,surface.WaterDepth):0;
                if(insideTerrain)connected=false;
                else if(waterSide)water=1;else air=1;
            }
            CavityExpression expression=new(gene.CavityFraction,gene.CavityAperture,
                region.ExchangeExpression,region.BarrierExpression,region.ContractileExpression,
                region.StructuralExpression);
            bool transportConnected=gene.IsCore||
                (region.TransportAvailability>0.02&&functional.MatterTransportEfficiency>0.01);
            inputs[count++]=new CavityRegionInput(region.RegionId,geometry.AnalyticVolume,
                RegionalPhysiology.OxygenCapacity(region,_config),transportConnected,water,
                expression,new CavityApertureContact(samplePosition,sampleDepth,connected,air,water));
        }
        CavityStepResult result=CavityPhysiology.StepBody(organism.CavityState,organism.Body,
            _environment,inputs[..count],dt);
        organism.CavityVentilationLastStep=result.OxygenTakenFromEnvironment;
        organism.CavityTissueOxygenLastStep=result.OxygenDeliveredToTissue;
        organism.CavityEnergyLastStep=result.EnergySpent;
        CumulativeOxygenUptake+=result.OxygenTakenFromEnvironment;
        CumulativeDissipatedEnergy+=result.EnergySpent;
    }

    public static double MediumMobility(double immersion, double hydration, double retention) =>
        Math.Clamp(immersion + ((1.0 - immersion) * 0.14 * hydration * (0.35 + 0.65 * retention)), 0.02, 1.0);

    internal static bool HasGroundSupport(double waterDepth,float centerDepth,float halfThickness) =>
        waterDepth<=0.0
            ?centerDepth<=halfThickness+1e-5f
            :centerDepth+halfThickness>=waterDepth-1e-5;

    private void ClampHorizontal(ref Organism organism)
    {
        organism.Position=SphericalWorld.Normalize(organism.Position,_config.WorldSize);
    }

    private SpatialInteractionFrame ComputeSpatialInteractions(double dt)
    {
        int count=_organisms.Count;
        SpatialInteractionState[] states=new SpatialInteractionState[count];
        SpatialOccupancyIndex occupancy=new(_config.SeparationRadius,_config.WorldSize);
        Dictionary<ulong,int> byId=new(count);
        OccupancyShape[] shapes=new OccupancyShape[count];
        double[] masses=new double[count];
        for(int index=0;index<count;index++)
        {
            Organism organism=_organisms[index];
            shapes[index]=OccupancyShape.FromBody(organism.Body);
            masses[index]=Math.Max(0.05,organism.Body.Cache.PhysicalMass);
            byId[organism.Id]=index;
            occupancy.Upsert(new SpatialOccupant(organism.Id,organism.Position,organism.Depth,shapes[index]));
        }
        List<ContactPair> pairs=[];
        double[] pressure=new double[count],overlapSum=new double[count],strongestOverlap=new double[count];
        int[] neighbors=new int[count],opponents=new int[count];
        Array.Fill(opponents,-1);
        for(int a=0;a<count;a++)
        {
            Organism first=_organisms[a];
            foreach(SpatialOccupant candidate in occupancy.Query(first.Position,shapes[a].HorizontalRadius))
            {
                int b=byId[candidate.Id];
                if(b<=a)continue;
                Organism second=_organisms[b];
                float horizontal=shapes[a].HorizontalRadius+shapes[b].HorizontalRadius;
                float vertical=shapes[a].VerticalHalfExtent+shapes[b].VerticalHalfExtent;
                Vector2 difference=-SphericalWorld.Delta(
                    first.Position,second.Position,_config.WorldSize);
                double normalizedSquared=difference.LengthSquared()/(horizontal*horizontal)+
                    Math.Pow((first.Depth-second.Depth)/vertical,2.0);
                double normalizedDistance=Math.Sqrt(Math.Max(0.0,normalizedSquared));
                const double contactShell=0.04;
                if(normalizedDistance>=1.0+contactShell)continue;
                double penetration=Math.Max(0.0,1.0-normalizedDistance);
                double contact=Math.Max(penetration,
                    0.08*Math.Clamp((1.0+contactShell-normalizedDistance)/contactShell,0.0,1.0));
                Vector2 direction;
                float distance=difference.Length();
                if(distance<1e-5f)
                {
                    double angle=((first.Id*2654435761UL)^second.Id)%65536*Math.Tau/65536;
                    direction=new((float)Math.Cos(angle),(float)Math.Sin(angle));
                }
                else direction=difference/distance;
                pairs.Add(new ContactPair(a,b,direction,contact,penetration));
                neighbors[a]++;neighbors[b]++;
                pressure[a]+=contact*(masses[b]/(masses[a]+masses[b]));
                pressure[b]+=contact*(masses[a]/(masses[a]+masses[b]));
                overlapSum[a]+=contact;overlapSum[b]+=contact;
                if(contact>strongestOverlap[a]){strongestOverlap[a]=contact;opponents[a]=b;}
                if(contact>strongestOverlap[b]){strongestOverlap[b]=contact;opponents[b]=a;}
            }
        }
        CumulativeContactPairs+=pairs.Count;
        ApplyContactPredation(opponents, dt);
        double[] effort=new double[count],energySpent=new double[count];
        Vector2[] acceleration=new Vector2[count],interactionDirection=new Vector2[count];
        for(int index=0;index<count;index++)
        {
            pressure[index]=1.0-Math.Exp(-pressure[index]);
            if(neighbors[index]==0)continue;
            Organism organism=_organisms[index];
            Genome genome=Genomes.Get(organism.GenomeId);
            double energyGate=Math.Clamp((organism.Body.TotalEnergy/_config.MaximumEnergy-
                    _config.ForagingRestEnergyFraction)/
                Math.Max(0.05,1.0-_config.ForagingRestEnergyFraction),0.0,1.0);
            double inheritedDrive=Math.Clamp(organism.ControllerOutputs.ContractionActivation,0.0,1.0)*
                (0.35+0.65*genome.Regions.Average(region=>region.Contractility));
            double desired=Math.Clamp(pressure[index]*energyGate*inheritedDrive,0.0,1.0);
            double requested=desired*_config.ActiveContestForce*
                _config.ContestEnergyPerForceSecond*masses[index]*dt;
            double paid=organism.Body.ConsumeEnergy(requested);
            effort[index]=desired*(requested>1e-12?paid/requested:0.0);
            energySpent[index]=paid;
            CumulativeDissipatedEnergy+=paid;CumulativeMovementEnergy+=paid;
        }
        foreach(ContactPair pair in pairs)
        {
            double reducedMass=2.0*masses[pair.First]*masses[pair.Second]/
                (masses[pair.First]+masses[pair.Second]);
            double activeBudget=_config.ActiveContestForce*(
                effort[pair.First]*pair.Contact/Math.Max(1e-8,overlapSum[pair.First])+
                effort[pair.Second]*pair.Contact/Math.Max(1e-8,overlapSum[pair.Second]));
            double force=reducedMass*(pair.Penetration*_config.SeparationAcceleration+activeBudget);
            acceleration[pair.First]+=pair.Direction*(float)(force/masses[pair.First]);
            acceleration[pair.Second]-=pair.Direction*(float)(force/masses[pair.Second]);
            interactionDirection[pair.First]+=pair.Direction*(float)pair.Contact;
            interactionDirection[pair.Second]-=pair.Direction*(float)pair.Contact;
        }
        for(int index=0;index<count;index++)
        {
            ulong opponent=opponents[index]>=0?_organisms[opponents[index]].Id:0;
            InteractionState state=neighbors[index]==0?InteractionState.None:
                effort[index]>0.002?InteractionState.Contesting:InteractionState.Yielding;
            Vector2 direction=interactionDirection[index].LengthSquared()>1e-10f
                ?Vector2.Normalize(interactionDirection[index]):Vector2.Zero;
            states[index]=new(acceleration[index],pressure[index],neighbors[index],opponent,
                state,effort[index],energySpent[index],direction);
        }
        return new(states,occupancy,pairs.Count);
    }

    private readonly HashSet<ulong> _retaliatedThisStep = [];

    private void ApplyContactPredation(int[] opponents, double dt)
    {
        _retaliatedThisStep.Clear();
        for (int index = 0; index < _organisms.Count; index++)
        {
            Organism organism = _organisms[index];
            organism.PredationLastStep = 0;
            organism.AttackDamageLastStep = 0;
            organism.RetaliationDamageLastStep = 0;
            organism.PredationEnergyLastStep = 0;
            _organisms[index] = organism;
        }
        // Reuse the strongest actual contact. Alternating iteration removes a
        // permanent low-ID first-access advantage without introducing a new RNG.
        for (int offset = 0; offset < _organisms.Count; offset++)
        {
            int index = StepIndex % 2 == 0 ? offset : _organisms.Count - 1 - offset;
            int target = opponents[index];
            if (target < 0) continue;
            Organism predator = _organisms[index], prey = _organisms[target];
            PredationResult result = ContactPredation.Attempt(
                predator.Body, Genomes.Get(predator.GenomeId), predator.Position, predator.Depth,
                prey.Body, Genomes.Get(prey.GenomeId), prey.Position, prey.Depth, _environment, dt,
                _config.WorldSize);
            if(result.Damage>0) prey.SocialMemory.RecordInjury(predator.Id,predator.Position,result.Damage);
            predator.AttackDamageLastStep += result.Damage;
            predator.PredationLastStep += result.AssimilatedOrganic;
            predator.PredationEnergyLastStep += result.EnergySpent;
            if(result.Damage>0 && _retaliatedThisStep.Add(prey.Id))
            {
                PredationResult counter=ContactPredation.Attempt(
                    prey.Body,Genomes.Get(prey.GenomeId),prey.Position,prey.Depth,
                    predator.Body,Genomes.Get(predator.GenomeId),predator.Position,predator.Depth,
                    _environment,dt,_config.WorldSize,retaliation:true);
                if(counter.Damage>0)
                    predator.SocialMemory.RecordInjury(prey.Id,prey.Position,counter.Damage);
                prey.RetaliationDamageLastStep+=counter.Damage;
                prey.PredationLastStep+=counter.AssimilatedOrganic;
                prey.PredationEnergyLastStep+=counter.EnergySpent;
                _organisms[target]=prey;
                CumulativePredationOrganic+=counter.AssimilatedOrganic;
                CumulativeDissipatedEnergy+=counter.EnergySpent;
            }
            _organisms[index] = predator;
            CumulativePredationOrganic += result.AssimilatedOrganic;
            CumulativeDissipatedEnergy += result.EnergySpent;
        }
    }

    private void ResolveOccupancy(ref Organism organism,SpatialOccupancyIndex occupancy)
    {
        OccupancyShape shape=OccupancyShape.FromBody(organism.Body);
        occupancy.Remove(organism.Id);
        for(int pass=0;pass<2;pass++)
        {
            bool corrected=false;
            foreach(SpatialOccupant other in occupancy.Query(organism.Position,shape.HorizontalRadius).ToArray())
            {
                float vertical=shape.VerticalHalfExtent+other.Shape.VerticalHalfExtent;
                double verticalRatio=Math.Abs(organism.Depth-other.Depth)/vertical;
                if(verticalRatio>=1.0)continue;
                float requiredHorizontal=(shape.HorizontalRadius+other.Shape.HorizontalRadius)*
                    (float)Math.Sqrt(Math.Max(0.0,1.0-verticalRatio*verticalRatio));
                Vector2 difference=-SphericalWorld.Delta(
                    organism.Position,other.Position,_config.WorldSize);
                float distance=difference.Length();
                if(distance>=requiredHorizontal)continue;
                Vector2 direction;
                if(distance<1e-5f)
                {
                    double angle=((organism.Id*2654435761UL)^other.Id)%65536*Math.Tau/65536;
                    direction=new((float)Math.Cos(angle),(float)Math.Sin(angle));
                }
                else direction=difference/distance;
                organism.Position=SphericalWorld.OffsetPosition(organism.Position,
                    direction*(requiredHorizontal-distance+0.001f),_config.WorldSize);
                corrected=true;
            }
            if(!corrected)break;
        }
        // Contact projection can cross a shoreline or move onto a shallower
        // column after the movement step has already constrained depth.
        EnvironmentSample habitat=_environment.Sample(organism.Position,organism.Depth);
        if(habitat.WaterDepth>0)
        {
            float half=Math.Min(shape.VerticalHalfExtent,(float)habitat.WaterDepth*0.5f);
            organism.Depth=Math.Clamp(organism.Depth,half,(float)habitat.WaterDepth-half);
        }
        else
        {
            organism.Depth=0;
            organism.VerticalVelocity=0;
        }
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
            organism.Body.TotalOxygen+organism.CavityState.TotalOxygen, organism.Hydration,
            organism.Body.DevelopmentCompletion(genome)));
        switch (cause)
        {
            case DeathCause.AccumulatedDamage: DamageDeaths++; break;
            case DeathCause.JuvenileFailure: JuvenileDeaths++; break;
            case DeathCause.Senescence: SenescenceDeaths++; break;
        }
    }

    private double CalculateTotalMatter() => _environment.TotalEnvironmentMatter +
        _organisms.Sum(o => o.Body.Cache.TotalMatter + o.Body.TotalSubstrate);
    private double CalculateTotalOxygen() => _environment.TotalOxygen +
        _organisms.Sum(o => o.Body.TotalOxygen+o.CavityState.TotalOxygen);

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
        FingerprintHash.Add(ref hash, _environment.ComputeResourceFingerprint());
        foreach (Organism o in _organisms)
        {
            FingerprintHash.Add(ref hash, o.Id); FingerprintHash.Add(ref hash, o.ParentId);
            o.SocialMemory.AddFingerprint(ref hash);
            FingerprintHash.Add(ref hash, unchecked((ulong)o.GenomeId));
            FingerprintHash.Add(ref hash, unchecked((ulong)o.BirthMutationCount));
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
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(o.HeadingRadians)));
            foreach (BodyRegion r in o.Body.Regions.OrderBy(r => r.RegionId))
            {
                FingerprintHash.Add(ref hash, unchecked((ulong)r.RegionId));
                foreach (double value in new[] { r.Matter, r.Substrate, r.Oxygen, r.Water, r.Energy, r.Damage,
                             r.InternalSignal,r.TransportAvailability,r.Activation,r.ExchangeExpression,
                             r.BarrierExpression,r.ContractileExpression,r.StructuralExpression,r.SensoryExpression,
                             r.PhotosyntheticExpression,r.FeedingExpression,r.DigestiveExpression,
                             r.DecomposerExpression })
                    FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
            }
            foreach(double value in o.SensorState)
                FingerprintHash.Add(ref hash,unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
            foreach(AppendageRegionPose pose in o.AppendageRegions.OrderBy(region=>region.RegionId))
            {
                FingerprintHash.Add(ref hash,unchecked((ulong)pose.RegionId));
                FingerprintHash.Add(ref hash,unchecked((ulong)BitConverter.DoubleToInt64Bits(pose.JointPitch)));
            }
            foreach(CavityRegionState cavity in o.CavityState.Regions.OrderBy(region=>region.RegionId))
            {
                FingerprintHash.Add(ref hash,unchecked((ulong)cavity.RegionId));
                FingerprintHash.Add(ref hash,unchecked((ulong)BitConverter.DoubleToInt64Bits(cavity.Oxygen)));
                FingerprintHash.Add(ref hash,unchecked((ulong)BitConverter.DoubleToInt64Bits(cavity.FloodedFraction)));
            }
        }
        return hash;
    }

    private static double NormalizeAngle(double angle)
    { angle %= Math.Tau; return angle < 0 ? angle + Math.Tau : angle; }
}

internal readonly record struct ContactPair(
    int First,int Second,Vector2 Direction,double Contact,double Penetration);

internal readonly record struct SpatialInteractionState(
    Vector2 Acceleration,double ContactPressure,int NeighborCount,ulong OpponentId,
    InteractionState State,double Intensity,double EnergySpent,Vector2 Direction);

internal sealed record SpatialInteractionFrame(
    SpatialInteractionState[] States,SpatialOccupancyIndex Occupancy,int PairCount);

public sealed class SimulationPerformanceMetrics
{
    public double EnvironmentMilliseconds { get; set; }
    public double SeparationMilliseconds { get; set; }
    public double ExchangeMilliseconds { get; set; }
    public double ControlTransportMilliseconds { get; set; }
    public double MechanicsMilliseconds { get; set; }
    public double MetabolismGrowthMilliseconds { get; set; }
    public double FinalizeMilliseconds { get; set; }

    public void Reset()
    {
        EnvironmentMilliseconds=0;SeparationMilliseconds=0;ExchangeMilliseconds=0;
        ControlTransportMilliseconds=0;MechanicsMilliseconds=0;
        MetabolismGrowthMilliseconds=0;FinalizeMilliseconds=0;
    }
}
