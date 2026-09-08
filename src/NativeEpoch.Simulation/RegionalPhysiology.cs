using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct RegionalExchangeResult(
    double OxygenUptake,
    double OxygenReleased,
    double SubstrateUptake,
    double WaterUptake,
    double WaterLost,
    double LightEnergy,
    double AssimilationEnergySpent,
    double WaterExposedArea,
    double AirExposedArea,
    int ExposedSamples,
    int OccludedSamples)
{
    public double Immersion => WaterExposedArea + AirExposedArea > 0.0
        ? WaterExposedArea / (WaterExposedArea + AirExposedArea)
        : 0.0;
}

public readonly record struct RegionalTransportResult(
    double SubstrateTransferred,
    double OxygenTransferred,
    double WaterTransferred,
    double ConservationResidual,
    double MinimumInventory,
    int ActiveEdges);

public readonly record struct RegionalMetabolismResult(
    double SubstrateConsumed,
    double OxygenConsumed,
    double EnergyProduced,
    double WasteProduced,
    double MaintenancePaid,
    double HypoxiaShortfall);

public static class RegionalPhysiology
{
    private const double WaterOxygenPotentialScale = 0.55;
    private const double AirOxygenPotentialScale = 1.0;

    public static double OxygenCapacity(BodyRegion region, SimulationConfig config) =>
        0.04 + (region.Matter * config.InternalOxygenCapacityPerMatter);

    public static double SubstrateCapacity(BodyRegion region, RegionGene gene) =>
        0.10 + (region.Matter * (0.8 + (1.2 * gene.StorageFraction)));

    public static double WaterCapacity(BodyRegion region, MetabolicGene metabolism) =>
        0.10 + (region.Matter * (0.65 + (0.55 * metabolism.WaterRetention)));

    public static (double OxygenPermeability, double WaterLossPermeability) SurfaceTradeoff(
        RegionGene region,
        MetabolicGene metabolism,
        double hydration)
    {
        double barrier = 1.0 - (0.78 * metabolism.WaterRetention);
        double selectivity = 0.65 + (0.35 * metabolism.OxygenCatalysis);
        double drying = 0.18 + (0.82 * Math.Clamp(hydration, 0.0, 1.0));
        return (
            region.Permeability * barrier * selectivity * drying,
            region.Permeability * barrier);
    }

    public static double BodyHydration(DevelopingBody body, Genome genome)
    {
        Dictionary<int, RegionGene> genes = genome.Regions.ToDictionary(gene => gene.RegionId);
        double capacity = body.Regions.Sum(region => WaterCapacity(region, genome.Metabolism));
        return capacity > 0.0 ? Math.Clamp(body.TotalWater / capacity, 0.0, 1.0) : 0.0;
    }

    public static RegionalExchangeResult ExchangeWithEnvironment(
        DevelopingBody body,
        Genome genome,
        IMutableEnvironmentField environment,
        Vector2 organismPosition,
        double headingRadians,
        float depth,
        SimulationConfig config,
        double deltaSeconds,
        Func<int, int, bool>? surfaceEnabled = null,
        double lightEnergyAllowance = double.PositiveInfinity)
    {
        Dictionary<int, RegionGene> genes = genome.Regions.ToDictionary(gene => gene.RegionId);
        Dictionary<int, BodyRegion> regions = body.Regions.ToDictionary(region => region.RegionId);
        Dictionary<int, RegionalInventoryDelta> deltas = [];
        double oxygenUptake = 0.0;
        double oxygenReleased = 0.0;
        double substrateUptake = 0.0;
        double waterUptake = 0.0;
        double waterLost = 0.0;
        double lightEnergy = 0.0;
        double remainingLight = lightEnergyAllowance;
        double assimilationEnergy = 0.0;
        double waterArea = 0.0;
        double airArea = 0.0;
        int exposedSamples = 0;
        int occludedSamples = 0;
        double cosine = Math.Cos(headingRadians);
        double sine = Math.Sin(headingRadians);
        EnvironmentSample centerSample = environment.Sample(organismPosition, depth);
        double centerElevation = centerSample.WaterDepth > 0.0
            ? centerSample.WaterSurface - depth
            : centerSample.TerrainHeight + Math.Max(0.08, body.Cache.BoundingRadius * 0.22);

        foreach (BodyFunctionalGeometry geometry in body.FunctionalGeometry.OrderBy(item => item.RegionId))
        {
            BodyRegion region = regions[geometry.RegionId];
            RegionGene gene = genes[geometry.RegionId];
            RegionalInventoryDelta delta = default;
            double oxygenCapacity = OxygenCapacity(region, config);
            double waterCapacity = WaterCapacity(region, genome.Metabolism);
            double substrateCapacity = SubstrateCapacity(region, gene);
            double oxygenConcentration = oxygenCapacity > 0.0 ? region.Oxygen / oxygenCapacity : 0.0;
            double barrier = 1.0 - (0.78 * genome.Metabolism.WaterRetention);

            for (int surfaceIndex = 0; surfaceIndex < geometry.SurfaceSamples.Count; surfaceIndex++)
            {
                BodySurfaceSample surface = geometry.SurfaceSamples[surfaceIndex];
                if (!surface.ExternallyConnected ||
                    (surfaceEnabled is not null && !surfaceEnabled(geometry.RegionId, surfaceIndex)))
                {
                    occludedSamples++;
                    continue;
                }
                exposedSamples++;
                double localX = (surface.LocalPosition.X * cosine) - (surface.LocalPosition.Z * sine);
                double localY = (surface.LocalPosition.X * sine) + (surface.LocalPosition.Z * cosine);
                Vector2 samplePosition = Vector2.Clamp(
                    organismPosition + new Vector2((float)localX, (float)localY),
                    Vector2.Zero,
                    new Vector2(config.WorldSize));
                double elevation = centerElevation + surface.LocalPosition.Y;
                EnvironmentSample surfaceEnvironment = environment.Sample(samplePosition, depth);
                bool waterSide = surfaceEnvironment.WaterDepth > 0.0 &&
                    elevation <= surfaceEnvironment.WaterSurface &&
                    elevation >= surfaceEnvironment.TerrainHeight;
                double sampleDepth = waterSide
                    ? Math.Clamp(surfaceEnvironment.WaterSurface - elevation, 0.0, surfaceEnvironment.WaterDepth)
                    : 0.0;
                surfaceEnvironment = environment.Sample(samplePosition, (float)sampleDepth);
                if (waterSide)
                    waterArea += surface.AreaWeight;
                else
                    airArea += surface.AreaWeight;

                double mediumCoefficient = waterSide
                    ? config.WaterOxygenTransferCoefficient
                    : config.AirOxygenTransferCoefficient;
                double outsidePotential = waterSide
                    ? surfaceEnvironment.DissolvedOxygenAvailability * WaterOxygenPotentialScale
                    : surfaceEnvironment.AirOxygenAvailability * AirOxygenPotentialScale;
                double airDryingFactor = waterSide
                    ? 1.0
                    : (0.18 + (0.82 * Math.Clamp(region.Water / waterCapacity, 0.0, 1.0)));
                double oxygenPermeability = SurfaceTradeoff(
                    gene, genome.Metabolism,
                    Math.Clamp(region.Water / waterCapacity, 0.0, 1.0)).OxygenPermeability;
                double oxygenFlux = surface.AreaWeight * oxygenPermeability * mediumCoefficient /
                    Math.Max(geometry.ExchangeDistance, 0.04) *
                    (outsidePotential - oxygenConcentration) * deltaSeconds;
                if (oxygenFlux > 0.0)
                {
                    double room = Math.Max(0.0, oxygenCapacity - (region.Oxygen + delta.Oxygen));
                    double received = environment.WithdrawOxygen(
                        samplePosition,
                        (float)sampleDepth,
                        waterSide ? 1.0 : 0.0,
                        Math.Min(room, oxygenFlux));
                    delta = delta with { Oxygen = delta.Oxygen + received };
                    oxygenUptake += received;
                }
                else if (oxygenFlux < 0.0)
                {
                    double released = Math.Min(
                        Math.Max(0.0, region.Oxygen + delta.Oxygen), -oxygenFlux);
                    delta = delta with { Oxygen = delta.Oxygen - released };
                    environment.DepositOxygen(
                        samplePosition,
                        (float)sampleDepth,
                        waterSide ? 1.0 : 0.0,
                        released);
                    oxygenReleased += released;
                }

                double lightFacing = 0.20 + (0.80 * Math.Max(0.0, surface.LocalNormal.Y));
                double requestedLight = surfaceEnvironment.Light * surface.AreaWeight *
                    gene.LightReactivity * lightFacing * config.LightEnergyPerSurfacePerSecond * deltaSeconds;
                double gainedLight = Math.Min(requestedLight, Math.Max(0.0, remainingLight));
                remainingLight -= gainedLight;
                delta = delta with { Energy = delta.Energy + gainedLight };
                lightEnergy += gainedLight;

                double matterGate = gene.Permeability * geometry.MatterTransportEfficiency *
                    (0.30 + (0.70 * region.TransportAvailability)) *
                    (1.0 / (1.0 + (0.55 * genome.Metabolism.OxygenCatalysis)));
                double matterRequest = Math.Max(0.0, substrateCapacity - (region.Substrate + delta.Substrate)) *
                    matterGate * surface.AreaWeight * config.MatterUptakePerSurfacePerSecond * deltaSeconds;
                double maintenanceReserve = ((config.BaseMaintenanceEnergyPerSecond *
                        region.Matter / Math.Max(0.05, body.Cache.TotalMatter)) +
                    (region.Matter * (0.02 + (0.025 * gene.SignalConductivity)))) * deltaSeconds;
                matterRequest = Math.Min(matterRequest,
                    Math.Max(0.0, region.Energy + delta.Energy - maintenanceReserve) /
                    config.MatterAssimilationEnergyPerMatter);
                double receivedMatter = environment.WithdrawMatter(samplePosition, matterRequest);
                double assimilationCost = receivedMatter * config.MatterAssimilationEnergyPerMatter;
                delta = delta with
                {
                    Substrate = delta.Substrate + receivedMatter,
                    Energy = delta.Energy - assimilationCost
                };
                substrateUptake += receivedMatter;
                assimilationEnergy += assimilationCost;

                if (waterSide)
                {
                    double waterRequest = Math.Max(0.0, waterCapacity - (region.Water + delta.Water)) *
                        gene.Permeability * surface.AreaWeight * 0.12 * deltaSeconds;
                    delta = delta with { Water = delta.Water + waterRequest };
                    waterUptake += waterRequest;
                }
                else
                {
                    double vaporDeficit = 1.0 - surfaceEnvironment.Moisture;
                    double evaporated = Math.Min(
                        Math.Max(0.0, region.Water + delta.Water),
                        surface.AreaWeight * gene.Permeability * barrier * vaporDeficit * 0.025 * deltaSeconds);
                    delta = delta with { Water = delta.Water - evaporated };
                    waterLost += evaporated;
                }
            }
            deltas[geometry.RegionId] = delta;
        }

        foreach ((int regionId, RegionalInventoryDelta delta) in deltas.OrderBy(pair => pair.Key))
            body.ApplyInventoryDelta(regionId, delta);

        return new RegionalExchangeResult(
            oxygenUptake, oxygenReleased, substrateUptake, waterUptake, waterLost,
            lightEnergy, assimilationEnergy, waterArea, airArea, exposedSamples, occludedSamples);
    }

    public static RegionalTransportResult TransportAlongMatterEdges(
        DevelopingBody body,
        Genome genome,
        SimulationConfig config,
        double deltaSeconds,
        Func<int, int, bool>? edgeEnabled = null,
        bool reverseEdgeEnumeration = false)
    {
        Dictionary<int, BodyRegion> regions = body.Regions.ToDictionary(region => region.RegionId);
        Dictionary<int, RegionGene> genes = genome.Regions.ToDictionary(gene => gene.RegionId);
        Dictionary<int, BodyFunctionalGeometry> geometry =
            body.FunctionalGeometry.ToDictionary(item => item.RegionId);
        List<TransferRequest> requests = [];
        IEnumerable<RegionGene> edgeGenes = genome.Regions.Where(gene => !gene.IsCore);
        edgeGenes = reverseEdgeEnumeration
            ? edgeGenes.OrderByDescending(gene => gene.RegionId)
            : edgeGenes.OrderBy(gene => gene.RegionId);

        foreach (RegionGene targetGene in edgeGenes)
        {
            int targetId = targetGene.RegionId;
            int sourceId = targetGene.MatterSourceRegionId;
            if (!regions.ContainsKey(targetId) || !regions.ContainsKey(sourceId) ||
                (edgeEnabled is not null && !edgeEnabled(sourceId, targetId)))
            {
                continue;
            }
            BodyRegion source = regions[sourceId];
            BodyRegion target = regions[targetId];
            double length = Math.Max(
                0.08,
                Vector2.Distance(geometry[sourceId].LocalCenter, geometry[targetId].LocalCenter));
            double crossSection = Math.Max(
                0.02,
                Math.Min(source.Matter, target.Matter) * 0.35);
            double conductance = targetGene.Permeability * geometry[targetId].MatterTransportEfficiency *
                crossSection / length;
            AddBidirectionalRequest(requests, source, target, InventoryKind.Substrate,
                SubstrateCapacity(source, genes[sourceId]), SubstrateCapacity(target, targetGene),
                conductance * 0.55 * deltaSeconds);
            AddBidirectionalRequest(requests, source, target, InventoryKind.Oxygen,
                OxygenCapacity(source, config), OxygenCapacity(target, config),
                conductance * 0.85 * deltaSeconds);
            AddBidirectionalRequest(requests, source, target, InventoryKind.Water,
                WaterCapacity(source, genome.Metabolism), WaterCapacity(target, genome.Metabolism),
                conductance * 0.45 * deltaSeconds);
        }

        Dictionary<(int Donor, InventoryKind Kind), double> totals = requests
            .GroupBy(request => (request.DonorId, request.Kind))
            .ToDictionary(group => group.Key, group => group.Sum(request => request.Amount));
        Dictionary<int, RegionalInventoryDelta> deltas = [];
        double substrateTransferred = 0.0;
        double oxygenTransferred = 0.0;
        double waterTransferred = 0.0;
        foreach (TransferRequest request in requests
                     .OrderBy(request => request.DonorId)
                     .ThenBy(request => request.ReceiverId)
                     .ThenBy(request => request.Kind))
        {
            double available = Inventory(regions[request.DonorId], request.Kind);
            double scale = totals[(request.DonorId, request.Kind)] > available
                ? available / totals[(request.DonorId, request.Kind)]
                : 1.0;
            double amount = request.Amount * scale;
            AddTransferDelta(deltas, request.DonorId, request.Kind, -amount);
            AddTransferDelta(deltas, request.ReceiverId, request.Kind, amount);
            switch (request.Kind)
            {
                case InventoryKind.Substrate: substrateTransferred += amount; break;
                case InventoryKind.Oxygen: oxygenTransferred += amount; break;
                case InventoryKind.Water: waterTransferred += amount; break;
            }
        }

        double before = body.TotalSubstrate + body.TotalOxygen + body.TotalWater;
        foreach ((int regionId, RegionalInventoryDelta delta) in deltas.OrderBy(pair => pair.Key))
            body.ApplyInventoryDelta(regionId, delta);
        double after = body.TotalSubstrate + body.TotalOxygen + body.TotalWater;
        double minimum = body.Regions.Min(region =>
            Math.Min(region.Substrate, Math.Min(region.Oxygen, region.Water)));
        return new RegionalTransportResult(
            substrateTransferred, oxygenTransferred, waterTransferred,
            after - before, minimum, requests.Select(request => (request.DonorId, request.ReceiverId)).Distinct().Count());
    }

    public static RegionalMetabolismResult ReactAndMaintain(
        DevelopingBody body,
        Genome genome,
        IMutableEnvironmentField environment,
        Vector2 position,
        SimulationConfig config,
        double deltaSeconds)
    {
        Dictionary<int, RegionGene> genes = genome.Regions.ToDictionary(gene => gene.RegionId);
        double substrateConsumed = 0.0;
        double oxygenConsumed = 0.0;
        double energyProduced = 0.0;
        double waste = 0.0;
        double maintenancePaid = 0.0;
        double hypoxia = 0.0;
        foreach (BodyRegion snapshot in body.Regions.OrderBy(region => region.RegionId).ToArray())
        {
            RegionGene gene = genes[snapshot.RegionId];
            double catalyticRate = config.MetabolicSubstratePerSecond *
                (0.20 + (0.80 * gene.CatalyticActivity)) * snapshot.Matter * deltaSeconds;
            double aerobicDemand = Math.Min(snapshot.Substrate, catalyticRate) *
                genome.Metabolism.OxygenUseFraction;
            double aerobicByOxygen = snapshot.Oxygen / config.OxygenPerAerobicSubstrate;
            double aerobic = Math.Min(aerobicDemand, aerobicByOxygen);
            double anaerobicCapacity = Math.Min(
                snapshot.Substrate - aerobic,
                catalyticRate * (1.0 - genome.Metabolism.OxygenUseFraction));
            double anaerobic = Math.Max(0.0, anaerobicCapacity);
            double usedSubstrate = aerobic + anaerobic;
            double usedOxygen = aerobic * config.OxygenPerAerobicSubstrate;
            double producedEnergy =
                (aerobic * config.AerobicEnergyPerSubstrate *
                    (0.30 + (0.70 * genome.Metabolism.OxygenCatalysis))) +
                (anaerobic * config.AnaerobicEnergyPerSubstrate *
                    (1.0 - (0.35 * genome.Metabolism.OxygenCatalysis)));
            double regionalEnergyCapacity = Math.Max(
                0.2,
                config.MaximumEnergy * snapshot.Matter / Math.Max(0.05, body.Cache.TotalMatter));
            producedEnergy = Math.Min(producedEnergy, Math.Max(0.0, regionalEnergyCapacity - snapshot.Energy));
            body.ApplyInventoryDelta(snapshot.RegionId, new RegionalInventoryDelta(
                -usedSubstrate,
                -usedOxygen,
                0.0,
                producedEnergy));
            substrateConsumed += usedSubstrate;
            oxygenConsumed += usedOxygen;
            energyProduced += producedEnergy;
            waste += usedSubstrate;
            hypoxia += Math.Max(0.0, aerobicDemand - aerobic);

            BodyRegion updated = body.Regions.Single(region => region.RegionId == snapshot.RegionId);
            double maintenance = ((config.BaseMaintenanceEnergyPerSecond *
                    updated.Matter / Math.Max(0.05, body.Cache.TotalMatter)) +
                (updated.Matter * (0.02 + (0.025 * gene.SignalConductivity)))) * deltaSeconds;
            double paid = Math.Min(updated.Energy, maintenance);
            body.ApplyInventoryDelta(updated.RegionId, new RegionalInventoryDelta(0.0, 0.0, 0.0, -paid));
            maintenancePaid += paid;
            if (paid + 1e-12 < maintenance)
                body.AddDamage(updated.RegionId, (maintenance - paid) * 0.02);
        }
        environment.DepositMetabolicWaste(position, waste);
        return new RegionalMetabolismResult(
            substrateConsumed, oxygenConsumed, energyProduced, waste,
            maintenancePaid, hypoxia);
    }

    private static void AddBidirectionalRequest(
        List<TransferRequest> requests,
        BodyRegion first,
        BodyRegion second,
        InventoryKind kind,
        double firstCapacity,
        double secondCapacity,
        double coefficient)
    {
        double firstConcentration = Inventory(first, kind) / Math.Max(1e-8, firstCapacity);
        double secondConcentration = Inventory(second, kind) / Math.Max(1e-8, secondCapacity);
        double signed = coefficient * (firstConcentration - secondConcentration);
        if (Math.Abs(signed) <= 1e-15)
            return;
        requests.Add(signed > 0.0
            ? new TransferRequest(first.RegionId, second.RegionId, kind, signed)
            : new TransferRequest(second.RegionId, first.RegionId, kind, -signed));
    }

    private static double Inventory(BodyRegion region, InventoryKind kind) => kind switch
    {
        InventoryKind.Substrate => region.Substrate,
        InventoryKind.Oxygen => region.Oxygen,
        InventoryKind.Water => region.Water,
        _ => region.Energy
    };

    private static void AddTransferDelta(
        Dictionary<int, RegionalInventoryDelta> deltas,
        int regionId,
        InventoryKind kind,
        double amount)
    {
        deltas.TryGetValue(regionId, out RegionalInventoryDelta delta);
        delta = kind switch
        {
            InventoryKind.Substrate => delta with { Substrate = delta.Substrate + amount },
            InventoryKind.Oxygen => delta with { Oxygen = delta.Oxygen + amount },
            InventoryKind.Water => delta with { Water = delta.Water + amount },
            _ => delta with { Energy = delta.Energy + amount }
        };
        deltas[regionId] = delta;
    }

    private readonly record struct TransferRequest(
        int DonorId,
        int ReceiverId,
        InventoryKind Kind,
        double Amount);

    private enum InventoryKind
    {
        Substrate,
        Oxygen,
        Water,
        Energy
    }
}
