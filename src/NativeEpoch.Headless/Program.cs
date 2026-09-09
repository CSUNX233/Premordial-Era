using System.Globalization;
using NativeEpoch.Simulation;

return HeadlessProgram.Run(args);

internal static class HeadlessProgram
{
    public static int Run(string[] args)
    {
        try
        {
            bool diagnoseEcology = args.Contains("--diagnose-ecology");
            bool diagnoseMovement = args.Contains("--diagnose-movement");
            bool diagnoseCompetition = args.Contains("--diagnose-competition");
            Options options = Options.Parse(args.Where(a => a != "--diagnose-ecology" && a != "--diagnose-movement" && a != "--diagnose-competition").ToArray());
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            if (diagnoseEcology)
                return EcologyDiagnostics.Run(CreateWorld(options), options.Steps, options.ReportEvery);
            if (diagnoseCompetition)
            {
                SpatialCompetitionDiagnosticResult result = SpatialCompetitionDiagnostics.Run();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
                Console.WriteLine(result.Passed ? "COMPETITION PASS" : "COMPETITION FAIL");
                return result.Passed ? 0 : 1;
            }
            if (diagnoseMovement)
            {
                MovementBehaviorDiagnosticResult movement = MovementBehaviorDiagnostics.Run();
                Console.WriteLine($"steering_heading(-0.5/0/+0.5)={movement.NegativeSteeringHeading:F3}/{movement.NeutralSteeringHeading:F3}/{movement.PositiveSteeringHeading:F3}");
                Console.WriteLine($"healthy_120s net={movement.HealthyNetDisplacement:F3} path={movement.HealthyPathLength:F3} body_lengths={movement.HealthyBodyLengths:F2} visited_cells={movement.VisitedResourceCells} span={movement.BoundsSpan}");
                Console.WriteLine($"low_energy_10s displacement={movement.LowEnergyDisplacement:F6} activity={movement.LowEnergyMeanActivity:F6}");
                Console.WriteLine(movement.Passed ? "MOVEMENT PASS" : "MOVEMENT FAIL");
                return movement.Passed ? 0 : 1;
            }

            if (options.DiagnoseMutations)
                return DiagnoseMutations(options);
            if (options.ExperimentAdaptation)
                return RunAdaptationExperiment();
            if (options.ExperimentSupplement)
                return RunAdaptationSupplement();
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

    private static int RunAdaptationExperiment()
    {
        Console.WriteLine("phase2C bounded natural-lineage paired assays (fresh-state common garden; mutations frozen only during replay)");
        IReadOnlyList<LineageExperimentResult> results = AdaptationExperiment.RunThreeSeeds();
        foreach (LineageExperimentResult result in results)
        {
            static string F(FitnessAssay f) => $"repro={f.ReproductiveIndex:F2},pop={f.FinalPopulation},birth={f.Births},death={f.Deaths},mature_child/grand={f.MatureDescendants}/{f.MatureGrandchildren},gen={f.MaximumGeneration},energy={f.LivingEnergy:F2},hydration={f.MeanHydration:F2}";
            Console.WriteLine($"seed={result.Seed} candidate={result.CandidateFound} id={result.CandidateId} parent={result.CandidateParentId} generation={result.CandidateGeneration} lineage={result.LineagePath} genome={result.AncestorFingerprint:X8}->{result.CandidateFingerprint:X8} revert={result.ReversionKind} common_garden={result.CommonGardenGenerationCompleted}");
            if (!result.CandidateFound)
            {
                Console.WriteLine($"  ancestor baselines water[{F(result.AncestorWater)}] land[{F(result.AncestorLand)}]");
                Console.WriteLine($"  conclusion={result.Conclusion}");
                continue;
            }
            Console.WriteLine($"  water ancestor[{F(result.AncestorWater)}] candidate[{F(result.CandidateWater)}] reverted[{F(result.RevertedWater)}]");
            Console.WriteLine($"  land  ancestor[{F(result.AncestorLand)}] candidate[{F(result.CandidateLand)}] reverted[{F(result.RevertedLand)}]");
            Console.WriteLine($"  source_advantage={result.SourceHabitatAdvantage} reversion_support={result.ReversionSupportsChange} land_adaptation={result.LandAdaptationObserved} conclusion={result.Conclusion}");
        }
        Console.WriteLine($"PHASE2C COMPLETE candidates={results.Count(r=>r.CandidateFound)}/3 supported={results.Count(r=>r.SourceHabitatAdvantage&&r.ReversionSupportsChange)}/3 land={results.Count(r=>r.LandAdaptationObserved)}/3");
        return 0;
    }

    private static int RunAdaptationSupplement()
    {
        LineageExperimentResult result=AdaptationExperiment.Run(20261011,3000,6500,true);
        static string F(FitnessAssay f)=>$"repro={f.ReproductiveIndex:F2},birth={f.Births},death={f.Deaths},mature_child/grand={f.MatureDescendants}/{f.MatureGrandchildren},gen={f.MaximumGeneration}";
        Console.WriteLine("phase2C fixed finite-resource common-garden supplement: identical +4 mineral brush, radius 8, per founder site; externally accounted");
        Console.WriteLine($"seed={result.Seed} candidate={result.CandidateFound} id={result.CandidateId} parent={result.CandidateParentId} lineage={result.LineagePath} common_garden={result.CommonGardenGenerationCompleted} revert={result.ReversionKind}");
        Console.WriteLine($"water ancestor[{F(result.AncestorWater)}] candidate[{F(result.CandidateWater)}] reverted[{F(result.RevertedWater)}]");
        Console.WriteLine($"land ancestor[{F(result.AncestorLand)}] candidate[{F(result.CandidateLand)}] reverted[{F(result.RevertedLand)}]");
        Console.WriteLine($"source_advantage={result.SourceHabitatAdvantage} reversion_support={result.ReversionSupportsChange} land_adaptation={result.LandAdaptationObserved} conclusion={result.Conclusion}");
        return 0;
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
        PrintDeathSummary(world);
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

    private static void PrintDeathSummary(SimulationWorld world)
    {
        foreach (IGrouping<DeathCause, DeathRecord> group in world.RecentDeaths.GroupBy(record => record.Cause))
        {
            Console.WriteLine(
                $"death_cause={group.Key} count={group.Count()} " +
                $"avg_development={group.Average(record => record.DevelopmentCompletion):F3} " +
                $"avg_energy={group.Average(record => record.Energy):F3} " +
                $"avg_substrate={group.Average(record => record.Substrate):F5} " +
                $"avg_oxygen={group.Average(record => record.Oxygen):F5} " +
                $"avg_hydration={group.Average(record => record.Hydration):F3}");
        }
    }

    private static int Verify(Options options)
    {
        SimulationWorld first = CreateWorld(options);
        SimulationWorld second = CreateWorld(options);
        bool initiallyAquatic = VerifyInitiallyAquatic(first) && VerifyInitiallyAquatic(second);
        first.Run(options.Steps);
        second.Run(options.Steps);
        SimulationSnapshot a = first.CaptureSnapshot();
        SimulationSnapshot b = second.CaptureSnapshot();

        bool deterministic = a.StateFingerprint == b.StateFingerprint;
        bool lifecycle = a.CumulativeBirths > 0;
        bool developedBodies = first.Organisms.Any(organism => organism.Body.RegionCount > 1);
        bool exactInheritance = first.RecentBirths.Any(record =>
            record.MutationKind == MutationKind.None && record.ParentGenomeId == record.ChildGenomeId);
        bool naturalVariation = first.RecentBirths.Any(record =>
            record.MutationKind != MutationKind.None && record.ParentGenomeId != record.ChildGenomeId);
        bool cacheConsistent = first.ValidateBodyCaches(out double cacheError);
        bool growthPaid = VerifyPaidGrowth();
        bool forcedMutationsSafe = CheckForcedMutations(options.Seed, print: false);
        bool movementPaid = a.AverageSpeed > 0.0 && a.CumulativeMovementEnergy > 0.0;
        bool depthLegal = first.Organisms.All(organism =>
        {
            EnvironmentSample sample = first.Environment.Sample(organism.Position, organism.Depth);
            return organism.Depth >= 0f && organism.Depth <= sample.WaterDepth + 1e-6;
        });
        bool noWaterRejected = VerifyNoWaterRejected(options.Seed);
        bool depthAwareContact = !SimulationWorld.AreWithinContactRange(
            new System.Numerics.Vector2(1, 1), 1, new System.Numerics.Vector2(1, 1), 8, 2);
        bool mediumDifference = SimulationWorld.MediumMobility(1, 1, 0.2) >
            SimulationWorld.MediumMobility(0, 1, 0.2) * 4;
        bool landDiagnostic = VerifyLandConstraint(options.Seed);
        bool sealedSurface = VerifySealedSurface(options.Seed);
        bool transport = VerifyRegionalTransport(out bool orderInvariant, out bool brokenEdgeBlocks);
        bool exchangeTradeoff = VerifyExchangeTradeoff();
        bool oxygenLimited = VerifyOxygenLimitedMetabolism(options.Seed);
        bool demandMetabolism = VerifyDemandLimitedMetabolism(options.Seed);
        bool finiteLightCompetition = VerifyFiniteLightCompetition(options.Seed);
        bool sharedFounderBudget = VerifySharedFounderBudget(options.Seed);
        MechanicsDiagnosticResult mechanics = MechanicsDiagnostics.Run();
        bool geometryContract = MorphologyGenomeFactory.BuildSixDiagnostics().All(sample =>
        {
            DevelopingBody body = MorphologyGenomeFactory.FullyDeveloped(sample.Genome);
            BodyGeometry geometry = BodyGeometryBuilder.Build(sample.Genome, body.Regions);
            return geometry.Finite && geometry.Connected && geometry.RelativeVolumeError < 1e-10;
        });
        (double minimumTerrain, double maximumTerrain, double maximumWaterDepth) = SampleTerrain(first);
        double tolerance = MatterTolerance(a.InitialMatter);
        bool matterConserved = Math.Abs(a.MatterError) <= tolerance;
        double oxygenTolerance = Math.Max(1e-8, Math.Abs(a.InitialOxygen) * 1e-10);
        bool oxygenConserved = Math.Abs(a.OxygenError) <= oxygenTolerance;
        bool passed = deterministic && lifecycle && developedBodies && exactInheritance &&
            cacheConsistent && growthPaid && forcedMutationsSafe && movementPaid &&
            a.AllFinite && b.AllFinite && matterConserved && initiallyAquatic && depthLegal &&
            noWaterRejected && depthAwareContact && mediumDifference && sealedSurface && transport &&
            landDiagnostic && orderInvariant && brokenEdgeBlocks && exchangeTradeoff && oxygenLimited && demandMetabolism &&
            finiteLightCompetition && sharedFounderBudget && oxygenConserved && mechanics.Passed && geometryContract;

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
            $"movement={movementPaid} average_speed={a.AverageSpeed:F6} " +
            $"movement_energy={a.CumulativeMovementEnergy:F6}");
        Console.WriteLine(
            $"phase2b={mechanics.Passed} geometry={geometryContract} " +
            $"shape_only_single={mechanics.SingleRegionDisplacement:E3} zero_activation={mechanics.ZeroActuationDisplacement:E3} shape_only_reciprocal={mechanics.ReciprocalDisplacement:E3} " +
            $"shape_only_multi={mechanics.MultiRegionDisplacement:E3} ground={mechanics.GroundSupportedDisplacement:E3} unsupported={mechanics.UnsupportedDisplacement:E3} weak_pose_delta={mechanics.WeakLinkPoseDifference:E3} " +
            $"work={mechanics.EnergySpent:E3} connection_load={mechanics.ConnectionLoad:E3} " +
            $"internal_residual={mechanics.MaximumInternalResidual:E3} balance_residual={mechanics.MaximumBalanceResidual:E3}");
        Console.WriteLine($"active_surface_sphere={mechanics.ActiveSurfaceSphereDisplacement:F6} no_energy={mechanics.UnpoweredSurfaceDisplacement:E3} sphere_sections_positive={mechanics.PositiveSphereSections}");
        Console.WriteLine($"demand_limited_metabolism={demandMetabolism}");
        Console.WriteLine($"shared_founder_matter_budget={sharedFounderBudget}");
        Console.WriteLine(
            $"aquatic_initial={initiallyAquatic} depth_legal={depthLegal} no_water_rejected={noWaterRejected} " +
            $"depth_contact={depthAwareContact} water_land_mobility={mediumDifference} " +
            $"land_dehydrates={landDiagnostic}");
        Console.WriteLine(
            $"regional_surface_seal={sealedSurface} transport={transport} " +
            $"shared_donor_order={orderInvariant} broken_edge_blocks={brokenEdgeBlocks} " +
            $"exchange_water_tradeoff={exchangeTradeoff}");
        Console.WriteLine(
            $"oxygen_limited={oxygenLimited} oxygen_nonnegative={a.EnvironmentOxygen >= 0 && a.OrganismOxygen >= 0} " +
            $"finite_light_competition={finiteLightCompetition} " +
            $"oxygen_error={a.OxygenError:E6} oxygen_tolerance={oxygenTolerance:E6} " +
            $"external_supply={a.CumulativeExternalOxygenSupply:F6}");
        Console.WriteLine(
            $"terrain_min={minimumTerrain:F2} terrain_max={maximumTerrain:F2} max_water_depth={maximumWaterDepth:F2}");
        Console.WriteLine(
            $"finite={a.AllFinite && b.AllFinite} matter_error={a.MatterError:E6} tolerance={tolerance:E6}");
        Console.WriteLine(passed ? "VERIFY PASS" : "VERIFY FAIL");
        return passed ? 0 : 1;
    }

    private static bool VerifyInitiallyAquatic(SimulationWorld world) => world.Organisms.All(organism =>
    {
        EnvironmentSample sample = world.Environment.Sample(organism.Position, organism.Depth);
        return sample.WaterDepth >= world.Config.MinimumAquaticSpawnDepth &&
            organism.Depth > 0f && organism.Depth < sample.WaterDepth;
    });

    private static bool VerifyNoWaterRejected(ulong seed)
    {
        try
        {
            _ = new SimulationWorld(new SimulationConfig
            {
                TerrainElevationOffset = 100,
                MaximumAquaticSpawnAttempts = 8,
                EnvironmentGridSize = 16
            }, seed, 1);
            return false;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message.Contains("bounded attempts", StringComparison.Ordinal);
        }
    }

    private static bool VerifyLandConstraint(ulong seed)
    {
        SimulationWorld world = new(new SimulationConfig { EnvironmentGridSize = 32 }, seed, 1);
        Organism before = world.Organisms[0];
        System.Numerics.Vector2? land = null;
        for (int y = 0; y <= 32 && land is null; y++) for (int x = 0; x <= 32; x++)
        {
            System.Numerics.Vector2 candidate = new(world.Config.WorldSize * x / 32f,
                world.Config.WorldSize * y / 32f);
            if (world.Environment.Sample(candidate).WaterDepth == 0) { land = candidate; break; }
        }
        if (land is null || !world.RelocateForMediumDiagnostic(before.Id, land.Value, 0)) return false;
        world.Run(20);
        return world.TryGetOrganism(before.Id, out Organism after) &&
            after.Depth == 0 && after.Immersion < 0.05 && after.Hydration < before.Hydration &&
            after.DehydrationCostLastStep > 0;
    }

    private static bool VerifySealedSurface(ulong seed)
    {
        SimulationConfig config = new() { EnvironmentGridSize = 16 };
        Genome genome = Genome.CreateAncestor();
        DevelopingBody open = new(genome, config.CoreInitialMatter, 0, 0, 0, 0.1);
        DevelopingBody sealedBody = new(genome, config.CoreInitialMatter, 0, 0, 0, 0.1);
        BilinearEnvironmentField openEnvironment = new(config, new DeterministicRandom(seed, 51));
        BilinearEnvironmentField sealedEnvironment = new(config, new DeterministicRandom(seed, 51));
        System.Numerics.Vector2 position = FindDiagnosticWater(openEnvironment, config.WorldSize);
        EnvironmentSample sample = openEnvironment.Sample(position);
        float depth = (float)Math.Min(2, sample.WaterDepth * 0.5);
        RegionalExchangeResult exposed = RegionalPhysiology.ExchangeWithEnvironment(
            open, genome, openEnvironment, position, 0, depth, config, config.FixedDeltaSeconds);
        RegionalExchangeResult blocked = RegionalPhysiology.ExchangeWithEnvironment(
            sealedBody, genome, sealedEnvironment, position, 0, depth, config,
            config.FixedDeltaSeconds, (_, _) => false);
        DevelopingBody developed = CloneDevelopedBody(genome, config);
        bool hasBuriedSamples = developed.FunctionalGeometry
            .SelectMany(geometry => geometry.SurfaceSamples)
            .Any(surface => !surface.ExternallyConnected);
        return exposed.ExposedSamples > 0 && exposed.OxygenUptake > 0 &&
            blocked.ExposedSamples == 0 && blocked.OxygenUptake == 0 &&
            blocked.SubstrateUptake == 0 && blocked.LightEnergy == 0 && hasBuriedSamples;
    }

    private static System.Numerics.Vector2 FindDiagnosticWater(IEnvironmentField environment, float worldSize)
    {
        for (int y = 0; y <= 16; y++) for (int x = 0; x <= 16; x++)
        {
            System.Numerics.Vector2 position = new(worldSize * x / 16f, worldSize * y / 16f);
            if (environment.Sample(position).WaterDepth > 4) return position;
        }
        throw new InvalidOperationException("Diagnostic map unexpectedly contains no water.");
    }

    private static bool VerifyRegionalTransport(out bool orderInvariant, out bool brokenEdgeBlocks)
    {
        SimulationConfig config = new();
        Genome ancestor = Genome.CreateAncestor();
        DevelopingBody template = new(ancestor, config.CoreInitialMatter, 20, 100, 0.5, 1);
        for (int step = 0; step < 120; step++) template.Grow(ancestor, 1, config);
        RegionGene region2 = ancestor.Regions.Single(region => region.RegionId == 2) with
            { MatterSourceRegionId = 1 };
        Genome chain = new(ancestor.Regions.Select(region => region.RegionId == 2 ? region2 : region),
            ancestor.MutationRate, ancestor.Metabolism, ancestor.ControllerNodes);

        DevelopingBody Forward() => CloneDevelopedBody(chain, config);
        DevelopingBody forward = Forward();
        DevelopingBody reverse = Forward();
        RegionalPhysiology.TransportAlongMatterEdges(forward, chain, config, 0.5);
        RegionalPhysiology.TransportAlongMatterEdges(reverse, chain, config, 0.5,
            reverseEdgeEnumeration: true);
        orderInvariant = forward.Regions.OrderBy(region => region.RegionId)
            .Zip(reverse.Regions.OrderBy(region => region.RegionId))
            .All(pair => Math.Abs(pair.First.Substrate - pair.Second.Substrate) < 1e-12 &&
                         Math.Abs(pair.First.Oxygen - pair.Second.Oxygen) < 1e-12 &&
                         pair.First.Substrate >= 0 && pair.First.Oxygen >= 0);

        DevelopingBody intact = Forward();
        DevelopingBody broken = Forward();
        for (int step = 0; step < 2; step++)
        {
            RegionalPhysiology.TransportAlongMatterEdges(intact, chain, config, 0.5);
            RegionalPhysiology.TransportAlongMatterEdges(broken, chain, config, 0.5,
                (source, target) => !(source == 1 && target == 2));
        }
        double intactDownstream = intact.Regions.Single(region => region.RegionId == 2).Substrate;
        double brokenDownstream = broken.Regions.Single(region => region.RegionId == 2).Substrate;
        brokenEdgeBlocks = intactDownstream > brokenDownstream + 1e-12 && brokenDownstream <= 1e-12;
        double before = forward.TotalSubstrate + forward.TotalOxygen + forward.TotalWater;
        RegionalTransportResult result = RegionalPhysiology.TransportAlongMatterEdges(forward, chain, config, 3);
        double after = forward.TotalSubstrate + forward.TotalOxygen + forward.TotalWater;
        return Math.Abs(before - after) < 1e-10 && Math.Abs(result.ConservationResidual) < 1e-10 &&
            result.MinimumInventory >= 0;
    }

    private static DevelopingBody CloneDevelopedBody(Genome genome, SimulationConfig config)
    {
        return new DevelopingBody(genome, genome.Regions.Select(gene => new BodyRegion(
            gene.RegionId, BodyCalculator.TargetMatter(gene), 1,
            gene.IsCore ? 20 : 0, gene.IsCore ? 0.5 : 0,
            gene.IsCore ? 1 : 0, gene.IsCore ? 100 : 0)));
    }

    private static bool VerifyExchangeTradeoff()
    {
        RegionGene region = Genome.CreateAncestor().Regions[0];
        MetabolicGene low = MetabolicGene.AquaticAncestor with { WaterRetention = 0.05 };
        MetabolicGene high = MetabolicGene.AquaticAncestor with { WaterRetention = 0.90 };
        var exposed = RegionalPhysiology.SurfaceTradeoff(region, low, 0.8);
        var retained = RegionalPhysiology.SurfaceTradeoff(region, high, 0.8);
        return retained.OxygenPermeability < exposed.OxygenPermeability &&
            retained.WaterLossPermeability < exposed.WaterLossPermeability;
    }

    private static bool VerifyOxygenLimitedMetabolism(ulong seed)
    {
        SimulationConfig config = new() { EnvironmentGridSize = 16 };
        Genome genome = Genome.CreateAncestor();
        DevelopingBody oxygenated = new(genome, config.CoreInitialMatter, 1, 0, 0.2, 0.2);
        DevelopingBody depleted = new(genome, config.CoreInitialMatter, 1, 0, 0, 0.2);
        BilinearEnvironmentField first = new(config, new DeterministicRandom(seed, 72));
        BilinearEnvironmentField second = new(config, new DeterministicRandom(seed, 72));
        RegionalMetabolismResult high = RegionalPhysiology.ReactAndMaintain(
            oxygenated, genome, first, System.Numerics.Vector2.Zero, config, 1);
        RegionalMetabolismResult low = RegionalPhysiology.ReactAndMaintain(
            depleted, genome, second, System.Numerics.Vector2.Zero, config, 1);
        return high.EnergyProduced > low.EnergyProduced && high.OxygenConsumed > 0 &&
            low.OxygenConsumed == 0 && low.SubstrateConsumed > 0;
    }

    private static bool VerifyDemandLimitedMetabolism(ulong seed)
    {
        SimulationConfig config = new() { EnvironmentGridSize = 16 };
        Genome genome = Genome.CreateAncestor();
        BilinearEnvironmentField environment = new(config, new DeterministicRandom(seed, 94));
        DevelopingBody full = new(genome, config.CoreInitialMatter, 1, config.MaximumEnergy, 0.2, 0.2);
        DevelopingBody hungry = new(genome, config.CoreInitialMatter, 1, 0, 0.2, 0.2);
        RegionalMetabolismResult resting = RegionalPhysiology.ReactAndMaintain(full, genome, environment,
            System.Numerics.Vector2.Zero, config, 1);
        RegionalMetabolismResult working = RegionalPhysiology.ReactAndMaintain(hungry, genome, environment,
            System.Numerics.Vector2.Zero, config, 1);
        return resting.SubstrateConsumed == 0 && resting.OxygenConsumed == 0 &&
            working.SubstrateConsumed > 0 && working.EnergyProduced > 0 &&
            Math.Abs(resting.SubstrateConsumed + working.SubstrateConsumed - environment.TotalMetabolicWaste) < 1e-10;
    }

    private static bool VerifyFiniteLightCompetition(ulong seed)
    {
        SimulationConfig config = new() { EnvironmentGridSize = 16 };
        BilinearEnvironmentField environment = new(config, new DeterministicRandom(seed, 83));
        System.Numerics.Vector2 position = new(config.WorldSize * 0.5f);
        IReadOnlyDictionary<ulong, double> allocation = environment.AllocateLightEnergy(
        [
            new LightEnergyRequest(1, position, 1, 10),
            new LightEnergyRequest(2, position, 1, 10),
            new LightEnergyRequest(3, position, 9, 10)
        ], config.FixedDeltaSeconds);
        return allocation.TryGetValue(1, out double first) &&
            allocation.TryGetValue(2, out double second) &&
            Math.Abs(first - second) < 1e-12 && first > 0 && first < 10 &&
            allocation.GetValueOrDefault(3UL) == 0;
    }

    private static (double Minimum, double Maximum, double MaximumDepth) SampleTerrain(SimulationWorld world)
    {
        double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity, maximumDepth = 0;
        for (int y = 0; y <= 64; y++) for (int x = 0; x <= 64; x++)
        {
            EnvironmentSample sample = world.Environment.Sample(new System.Numerics.Vector2(
                world.Config.WorldSize * x / 64f, world.Config.WorldSize * y / 64f));
            minimum = Math.Min(minimum, sample.TerrainHeight);
            maximum = Math.Max(maximum, sample.TerrainHeight);
            maximumDepth = Math.Max(maximumDepth, sample.WaterDepth);
        }
        return (minimum, maximum, maximumDepth);
    }

    private static int DiagnoseMutations(Options options)
    {
        Console.WriteLine("FORCED MUTATION DIAGNOSTIC (test mode; does not represent natural event rates)");
        bool passed = CheckForcedMutations(options.Seed, print: true);
        passed &= CheckInheritedMorphology(options.Seed);
        Console.WriteLine(passed ? "DIAGNOSTIC PASS" : "DIAGNOSTIC FAIL");
        return passed ? 0 : 1;
    }

    private static bool CheckInheritedMorphology(ulong seed)
    {
        // Samples the ordinary inheritance path, not a survival/adaptation assay.
        // Adult geometry shows inherited potential; real offspring still grow it.
        Genome parent = Genome.CreateAncestor();
        Genome frozen = new(parent.Regions, 0, parent.Metabolism, parent.ControllerNodes);
        GenomeMutator mutator = new();
        DeterministicRandom bodyRandom = new(seed, 301), controllerRandom = new(seed, 302);
        DevelopingBody ancestorBody = MorphologyGenomeFactory.FullyDeveloped(parent);
        BodyGeometry original = ancestorBody.Geometry;
        int exact = 0, visual = 0, shape = 0, substantial = 0, topology = 0, capacityChanged = 0;
        bool valid = true, freezeExact = true;
        for (int index = 0; index < 2000; index++)
        {
            Genome child = mutator.Inherit(parent, bodyRandom, controllerRandom).Genome;
            DevelopingBody developed = MorphologyGenomeFactory.FullyDeveloped(child);
            BodyGeometry geometry = developed.Geometry;
            BodyCache a = ancestorBody.Cache, b = developed.Cache;
            if (Math.Abs(a.MatterUptakeSurface-b.MatterUptakeSurface)>1e-8 ||
                Math.Abs(a.LightCaptureSurface-b.LightCaptureSurface)>1e-8 ||
                Math.Abs(a.CatalyticSurface-b.CatalyticSurface)>1e-8 ||
                Math.Abs(a.StorageCapacity-b.StorageCapacity)>1e-8 ||
                Math.Abs(a.MaintenanceEnergyPerSecond-b.MaintenanceEnergyPerSecond)>1e-8 ||
                Math.Abs(a.StructuralStiffness-b.StructuralStiffness)>1e-8 ||
                Math.Abs(a.MaximumActivationEnergyPerSecond-b.MaximumActivationEnergyPerSecond)>1e-8)
                capacityChanged++;
            valid &= geometry.Finite && geometry.Connected;
            if (child.Fingerprint == parent.Fingerprint) exact++;
            if (geometry.GeometryKey != original.GeometryKey) visual++;
            bool structural = child.Regions.Count != parent.Regions.Count ||
                child.Regions.Any(g => parent.Regions.FirstOrDefault(p => p.RegionId == g.RegionId).ParentRegionId != g.ParentRegionId);
            bool different = structural, noticeable = structural;
            foreach (BodyGeometryRegion region in geometry.Regions)
            {
                BodyGeometryRegion source = original.Regions.FirstOrDefault(r => r.RegionId == region.RegionId);
                if (source.StartRadius <= 0) continue;
                different |= region with { Color = source.Color } != source;
                noticeable |= Math.Abs(region.Length - source.Length) > 0.10 * (source.Length + 2 * source.StartRadius) ||
                    Math.Abs(region.StartRadius / source.StartRadius - 1) > 0.10 ||
                    Math.Abs(region.EndRadius / source.EndRadius - 1) > 0.10 ||
                    Math.Abs(region.VerticalScale - source.VerticalScale) > 0.10 ||
                    Math.Abs(region.Curvature - source.Curvature) > 0.10 ||
                    Math.Abs(region.Angle - source.Angle) > 0.15;
            }
            if (different) shape++;
            if (noticeable) substantial++;
            if (structural) topology++;
            freezeExact &= mutator.Inherit(frozen, bodyRandom, controllerRandom).Genome.Fingerprint == frozen.Fingerprint;
        }
        Console.WriteLine($"INHERITANCE SAMPLE (2000 independent births; adult potential, not natural selection): exact={exact} visual_keys={visual} shape={shape} substantial_shape={substantial} topology={topology} body_capacity_changes={capacityChanged} finite={valid} mutation_zero_exact={freezeExact}");
        return valid && freezeExact && exact > 0 && shape > 0 && substantial > 0 && topology > 0 && capacityChanged > 0;
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
            DevelopingBody body = new(result.Genome, config.CoreInitialMatter, 10.0, 100.0);
            for (int step = 0; step < 120; step++)
                body.Grow(result.Genome, 1.0, config);
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
        DevelopingBody body = new(genome, config.CoreInitialMatter, 2.0, 10.0);
        double bodyBefore = body.Cache.TotalMatter;
        double storedBefore = body.TotalSubstrate;
        double energyBefore = body.TotalEnergy;
        double grown = body.Grow(genome, 1.0, config);
        return grown > 0.0 &&
            Math.Abs((body.Cache.TotalMatter - bodyBefore) - grown) <= 1e-12 &&
            Math.Abs((storedBefore - body.TotalSubstrate) - grown) <= 1e-12 &&
            Math.Abs((energyBefore - body.TotalEnergy) - (grown * config.GrowthEnergyPerMatter)) <= 1e-12;
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

    private static bool VerifySharedFounderBudget(ulong seed)
    {
        SimulationConfig config=new(){ResourceBudgetReferenceAncestors=24};
        SimulationWorld small=new(config,seed,24),observation=new(config,seed,300);
        SimulationSnapshot a=small.CaptureSnapshot(),b=observation.CaptureSnapshot();
        double transferred=(300-24)*(config.CoreInitialMatter+config.AncestorStoredMatter);
        return a.AllFinite&&b.AllFinite&&Math.Abs(a.InitialMatter-b.InitialMatter)<1e-8&&
            Math.Abs(a.EnvironmentMinerals-b.EnvironmentMinerals-transferred)<1e-8&&
            Math.Abs(a.MatterError)<1e-8&&Math.Abs(b.MatterError)<1e-8;
    }

    private static double MatterTolerance(double initialMatter) =>
        Math.Max(1e-8, Math.Abs(initialMatter) * 1e-10);

    private static void PrintHeader(Options options) => Console.WriteLine(
        $"NativeEpoch phase2A seed={options.Seed} steps={options.Steps} ancestors={options.Ancestors} " +
        $"world={options.WorldSize.ToString("G9", CultureInfo.InvariantCulture)} grid={options.GridSize} " +
        $"max_population={options.MaxPopulation}");

    private static void PrintSnapshot(SimulationSnapshot snapshot) => Console.WriteLine(
        $"step={snapshot.StepIndex,6} time={snapshot.SimulatedSeconds,7:F1}s " +
            $"alive={snapshot.Population,6} births={snapshot.CumulativeBirths,6} deaths={snapshot.CumulativeDeaths,6} " +
        $"death(damage/juvenile/senescence)={snapshot.DamageDeaths}/{snapshot.JuvenileDeaths}/{snapshot.SenescenceDeaths} " +
        $"genomes={snapshot.GenomeCount,4} regions={snapshot.TotalBodyRegions,6} maturity={snapshot.AverageMaturity:F3} " +
        $"minerals={snapshot.EnvironmentMinerals,12:F6} detritus={snapshot.EnvironmentDetritus,10:F6} " +
        $"body={snapshot.OrganismBodyMatter,10:F6} stored={snapshot.OrganismStoredMatter,10:F6} " +
        $"energy={snapshot.LivingEnergy,10:F4} " +
        $"speed={snapshot.AverageSpeed:F4} movement_energy={snapshot.CumulativeMovementEnergy:F4} " +
        $"water/shore/land={snapshot.AquaticPopulation}/{snapshot.ShorePopulation}/{snapshot.LandPopulation} " +
        $"depth={snapshot.AverageDepth:F2} hydration={snapshot.AverageHydration:F3} " +
        $"oxygen_error={snapshot.OxygenError:E3} " +
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
        Console.WriteLine("NativeEpoch phase 2A regional exchange and transport simulation");
        Console.WriteLine("  --verify                 deterministic lifecycle and phase 2A mechanism invariants");
        Console.WriteLine("  --diagnose-mutations     force four mutation kinds in an explicit test mode");
        Console.WriteLine("  --diagnose-ecology       track actual generations, reproductive bottlenecks and survival");
        Console.WriteLine("  --diagnose-movement      check steering authority, 120 s exploration and low-energy rest");
        Console.WriteLine("  --diagnose-competition   check occupied space, paid contact and finite food sharing");
        Console.WriteLine("  --experiment-adaptation run three bounded natural-lineage paired assays");
        Console.WriteLine("  --experiment-supplement run one fixed finite-resource common-garden supplement");
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
        bool ExperimentAdaptation,
        bool ExperimentSupplement,
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
            bool experimentAdaptation = false;
            bool experimentSupplement = false;
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
                    case "--experiment-adaptation":
                        experimentAdaptation = true;
                        break;
                    case "--experiment-supplement":
                        experimentSupplement = true;
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
                verify, diagnoseMutations, experimentAdaptation, experimentSupplement, showHelp, seed, steps, ancestors, reportEvery,
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
