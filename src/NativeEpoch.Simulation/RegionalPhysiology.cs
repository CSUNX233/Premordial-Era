using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct RegionalExchangeResult(
    double OxygenUptake,
    double OxygenReleased,
    double SubstrateUptake,
    double SubstrateDemand,
    double WaterUptake,
    double WaterLost,
    double LightEnergy,
    double AssimilationEnergySpent,
    double WaterExposedArea,
    double AirExposedArea,
    int ExposedSamples,
    int OccludedSamples,
    double AbsorbedLight = 0.0,
    double PhotosyntheticHeat = 0.0,
    double PhotosyntheticMaintenance = 0.0,
    double PhotosynthesizedSubstrate = 0.0,
    double MineralConsumed = 0.0,
    double OrganicConsumed = 0.0,
    double OrganicAssimilated = 0.0,
    double DetritusDecomposed = 0.0,
    double MineralsReturned = 0.0,
    double FoodDemand = 0.0,
    double FoodConsumed = 0.0)
{
    public double Immersion => WaterExposedArea + AirExposedArea > 0.0
        ? WaterExposedArea / (WaterExposedArea + AirExposedArea)
        : 0.0;
}

public readonly record struct RegionalTransportResult(
    double SubstrateTransferred,
    double OxygenTransferred,
    double WaterTransferred,
    double EnergyTransferred,
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

public readonly record struct OrganicDigestionResult(
    double OfferedOrganic,
    double ProcessedOrganic,
    double AssimilatedSubstrate,
    double UnprocessedOrganic,
    double DigestionWaste,
    double EnergySpent);

public readonly record struct DecompositionResult(
    double OfferedDetritus,
    double AssimilatedSubstrate,
    double ReturnedMinerals,
    double RejectedDetritus,
    double EnergySpent);

public static class RegionalPhysiology
{
    private const double OngoingEnergyCostScale = 1.0 / 30.0;
    private const double WaterOxygenPotentialScale = 0.55;
    private const double AirOxygenPotentialScale = 1.0;
    private const double OrganicDigestionEnergyPerMatter = 0.18;
    private const double OrganicFeedingRateScale = 10.0;
    private const double DigestiveThroughputPerCapacity = 1.5;

    public static double FeedingCapacity(DevelopingBody body, Genome genome) =>
        body.Cache.FeedingSurface;

    public static double DecompositionCapacity(DevelopingBody body, Genome genome) =>
        body.Cache.DecomposerSurface;

    public static OrganicDigestionResult DigestOrganicToSubstrate(
        DevelopingBody body,
        Genome genome,
        double offeredOrganic,
        double deltaSeconds,
        double maximumAssimilationEfficiency = 0.85)
    {
        if (!double.IsFinite(offeredOrganic) || offeredOrganic < 0.0)
            throw new ArgumentOutOfRangeException(nameof(offeredOrganic));
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (!double.IsFinite(maximumAssimilationEfficiency) ||
            maximumAssimilationEfficiency is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(maximumAssimilationEfficiency));
        double processLimit = Math.Min(offeredOrganic,
            body.Cache.DigestiveCapacity * DigestiveThroughputPerCapacity * deltaSeconds);
        double remaining = processLimit;
        double processedTotal = 0.0;
        double assimilated = 0.0;
        double energySpent = 0.0;
        for (int index = 0; index < body.Regions.Count && remaining > 1e-12; index++)
        {
            BodyRegion region = body.Regions[index];
            RegionGene gene = genome.GetRegion(region.RegionId);
            double machinery = region.DigestiveExpression *
                (0.25 + (0.75 * gene.CatalyticActivity)) *
                (0.25 + (0.75 * region.TransportAvailability));
            if (machinery <= 1e-12) continue;
            // Expression limits throughput. Once machinery exists, material quality
            // controls yield so low expression is slow rather than intrinsically futile.
            double efficiency = Math.Clamp(0.35 +
                (0.45 * gene.CatalyticActivity * (0.25 + (0.75 * region.TransportAvailability))),
                0.35, 0.85);
            efficiency = Math.Min(efficiency, maximumAssimilationEfficiency);
            double room = Math.Max(0.0, SubstrateCapacity(region, gene) - region.Substrate);
            double requested = Math.Min(remaining, Math.Min(
                region.Matter * machinery * DigestiveThroughputPerCapacity * deltaSeconds,
                room / Math.Max(1e-12, efficiency)));
            double requestedEnergy = requested * OrganicDigestionEnergyPerMatter;
            double paid = body.ConsumeRegionEnergy(region.RegionId, requestedEnergy);
            double execution = requestedEnergy > 1e-12 ? paid / requestedEnergy : 0.0;
            double processed = requested * Math.Clamp(execution, 0.0, 1.0);
            double stored = processed * efficiency;
            if (stored > 0.0)
                body.ApplyInventoryDelta(region.RegionId, new RegionalInventoryDelta(stored, 0.0, 0.0, 0.0));
            remaining -= processed;
            processedTotal += processed;
            assimilated += stored;
            energySpent += paid;
        }
        return new(offeredOrganic, processedTotal, assimilated,
            Math.Max(0.0, offeredOrganic - processedTotal),
            Math.Max(0.0, processedTotal - assimilated), energySpent);
    }

    public static DecompositionResult DecomposeDetritus(
        DevelopingBody body,
        Genome genome,
        double offeredDetritus,
        double deltaSeconds)
    {
        if (!double.IsFinite(offeredDetritus) || offeredDetritus < 0.0)
            throw new ArgumentOutOfRangeException(nameof(offeredDetritus));
        double gate = Math.Clamp(DecompositionCapacity(body, genome), 0.0, double.MaxValue);
        double admitted = Math.Min(offeredDetritus, gate * 0.20 * deltaSeconds);
        OrganicDigestionResult digestion = DigestOrganicToSubstrate(body, genome, admitted, deltaSeconds);
        double mineralized = Math.Max(0.0, digestion.ProcessedOrganic - digestion.AssimilatedSubstrate);
        return new(offeredDetritus, digestion.AssimilatedSubstrate, mineralized,
            Math.Max(0.0, offeredDetritus - digestion.ProcessedOrganic), digestion.EnergySpent);
    }

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
        double capacity = body.Regions.Sum(region => WaterCapacity(region, genome.Metabolism));
        return capacity > 0.0 ? Math.Clamp(body.TotalWater / capacity, 0.0, 1.0) : 0.0;
    }

    public static double EstimateMatterDemand(
        DevelopingBody body,Genome genome,double lightEnergyAllowance,
        SimulationConfig config,double deltaSeconds)
        => 0.0;

    public static double EstimateMineralDemand(
        DevelopingBody body, Genome genome, double lightAbsorptionAllowance,
        SimulationConfig config, double deltaSeconds)
    {
        if (body.Cache.PhotosyntheticSurface <= 0.0 || lightAbsorptionAllowance <= 0.0)
            return 0.0;
        double room = 0.0;
        foreach (BodyRegion region in body.Regions)
            room += Math.Max(0.0, SubstrateCapacity(region, genome.GetRegion(region.RegionId)) - region.Substrate);
        return Math.Min(room, Math.Max(0.0, lightAbsorptionAllowance) * 0.96 /
            config.MatterAssimilationEnergyPerMatter);
    }

    public static double EstimateOrganicDemand(
        DevelopingBody body, Genome genome, SimulationConfig config, double deltaSeconds)
    {
        double room = 0.0;
        foreach (BodyRegion region in body.Regions)
            room += Math.Max(0.0, SubstrateCapacity(region, genome.GetRegion(region.RegionId)) - region.Substrate);
        return Math.Min(room, FeedingCapacity(body, genome) *
            config.MatterUptakePerSurfacePerSecond * OrganicFeedingRateScale * deltaSeconds);
    }

    public static double EstimateDetritusDemand(
        DevelopingBody body, Genome genome, double deltaSeconds) =>
        DecompositionCapacity(body, genome) * 0.20 * deltaSeconds;

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
        double lightEnergyAllowance = double.PositiveInfinity,
        MatterReservation? matterReservation = null,
        double matterDemand = double.NaN,
        MineralReservation? mineralReservation = null,
        OrganicReservation? organicReservation = null,
        DetritusReservation? detritusReservation = null)
    {
        double oxygenUptake = 0.0;
        double oxygenReleased = 0.0;
        double substrateUptake = 0.0;
        double waterUptake = 0.0;
        double waterLost = 0.0;
        double lightEnergy = 0.0;
        double absorbedLight = 0.0;
        double photosyntheticHeat = 0.0;
        double photosyntheticMaintenance = 0.0;
        double photosynthesizedSubstrate = 0.0;
        double mineralConsumed = 0.0;
        double remainingMinerals = mineralReservation?.Total ?? 0.0;
        double remainingLight = lightEnergyAllowance;
        if(!double.IsFinite(matterDemand)) matterDemand=0.0;
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

        foreach (BodyFunctionalGeometry geometry in body.FunctionalGeometry)
        {
            BodyRegion region = body.GetRegion(geometry.RegionId);
            RegionGene gene = genome.GetRegion(geometry.RegionId);
            RegionalInventoryDelta delta = default;
            double oxygenCapacity = OxygenCapacity(region, config);
            double waterCapacity = WaterCapacity(region, genome.Metabolism);
            double substrateCapacity = SubstrateCapacity(region, gene);
            double oxygenConcentration = oxygenCapacity > 0.0 ? region.Oxygen / oxygenCapacity : 0.0;
            double barrier = 1.0 - (0.78 * genome.Metabolism.WaterRetention);
            double regionalHeatMaintenanceDemand = 0.0;

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
                Vector2 samplePosition = SphericalWorld.OffsetPosition(
                    organismPosition, new Vector2((float)localX, (float)localY), config.WorldSize);
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
                double expressionGate=EffectiveExchange(region);
                double oxygenPermeability = SurfaceTradeoff(
                    gene, genome.Metabolism,
                    Math.Clamp(region.Water / waterCapacity, 0.0, 1.0)).OxygenPermeability*
                    expressionGate;
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
                double requestedAbsorption = surfaceEnvironment.Light * surface.AreaWeight *
                    region.PhotosyntheticExpression * lightFacing *
                    config.LightEnergyPerSurfacePerSecond * deltaSeconds;
                double absorbed = Math.Min(requestedAbsorption, Math.Max(0.0, remainingLight));
                remainingLight -= absorbed;
                double conversionEfficiency = 0.72 + (0.24 * gene.CatalyticActivity);
                double potentialChemicalEnergy = absorbed * conversionEfficiency;
                double synthesisRoom = Math.Max(0.0,
                    substrateCapacity - (region.Substrate + delta.Substrate));
                double synthesized = Math.Min(remainingMinerals, Math.Min(synthesisRoom,
                    potentialChemicalEnergy / config.MatterAssimilationEnergyPerMatter));
                double storedChemicalEnergy = synthesized * BilinearEnvironmentField.FoodWebEnergyPerMatter;
                remainingMinerals -= synthesized;
                double wasteHeat = absorbed - storedChemicalEnergy;
                double thermalRetention = waterSide ? 0.18 : 0.62;
                double ambientHeatStress = Math.Clamp((surfaceEnvironment.Temperature - 0.70) / 0.50, 0.0, 1.0);
                regionalHeatMaintenanceDemand += wasteHeat * OngoingEnergyCostScale *
                    (0.08 + (0.18 * thermalRetention) + (0.14 * ambientHeatStress));
                delta = delta with { Substrate = delta.Substrate + synthesized };
                absorbedLight += absorbed;
                photosyntheticHeat += wasteHeat;
                lightEnergy += storedChemicalEnergy;
                photosynthesizedSubstrate += synthesized;
                mineralConsumed += synthesized;
                substrateUptake += synthesized;

                if (waterSide)
                {
                    double waterRequest = Math.Max(0.0, waterCapacity - (region.Water + delta.Water)) *
                        gene.Permeability * Math.Clamp(region.ExchangeExpression/0.50,0.0,1.25) *
                        surface.AreaWeight * 0.12 * deltaSeconds;
                    delta = delta with { Water = delta.Water + waterRequest };
                    waterUptake += waterRequest;
                }
                else
                {
                    double vaporDeficit = 1.0 - surfaceEnvironment.Moisture;
                    double evaporated = Math.Min(
                        Math.Max(0.0, region.Water + delta.Water),
                        surface.AreaWeight * gene.Permeability * barrier *
                        (1.0-0.82*region.BarrierExpression) * vaporDeficit * 0.025 * deltaSeconds);
                    delta = delta with { Water = delta.Water - evaporated };
                    waterLost += evaporated;
                }
            }
            double heatMaintenancePaid = Math.Min(
                Math.Max(0.0, region.Energy + delta.Energy), regionalHeatMaintenanceDemand);
            delta = delta with { Energy = delta.Energy - heatMaintenancePaid };
            photosyntheticMaintenance += heatMaintenancePaid;
            body.ApplyInventoryDelta(geometry.RegionId, delta);
            double heatMaintenanceShortfall = regionalHeatMaintenanceDemand - heatMaintenancePaid;
            if (heatMaintenanceShortfall > 1e-12)
                body.AddDamage(geometry.RegionId,
                    Math.Clamp(heatMaintenanceShortfall / Math.Max(0.05, region.Matter) * 0.02, 0.0, 1.0));
        }

        if(matterReservation.HasValue)
            environment.ReturnMatter(matterReservation.Value,matterReservation.Value.Total);
        if(mineralReservation.HasValue&&remainingMinerals>0.0)
            environment.ReturnMinerals(mineralReservation.Value,remainingMinerals);

        double organicConsumed = 0.0;
        double organicAssimilated = 0.0;
        if(organicReservation.HasValue)
        {
            OrganicDigestionResult digestion=DigestOrganicToSubstrate(
                body,genome,organicReservation.Value.Total,deltaSeconds);
            organicConsumed=digestion.ProcessedOrganic;
            organicAssimilated=digestion.AssimilatedSubstrate;
            assimilationEnergy+=digestion.EnergySpent;
            substrateUptake+=digestion.AssimilatedSubstrate;
            double unprocessed=organicReservation.Value.Total-digestion.ProcessedOrganic;
            if(unprocessed>0.0)environment.ReturnOrganic(organicReservation.Value,unprocessed);
            double waste=digestion.ProcessedOrganic-digestion.AssimilatedSubstrate;
            if(waste>0.0)environment.DepositMetabolicWaste(organismPosition,waste);
        }
        double detritusDecomposed = 0.0;
        double mineralsReturned = 0.0;
        if(detritusReservation.HasValue)
        {
            DecompositionResult decomposition=DecomposeDetritus(
                body,genome,detritusReservation.Value.Total,deltaSeconds);
            detritusDecomposed=decomposition.OfferedDetritus-decomposition.RejectedDetritus;
            mineralsReturned=decomposition.ReturnedMinerals;
            substrateUptake+=decomposition.AssimilatedSubstrate;
            assimilationEnergy+=decomposition.EnergySpent;
            if(decomposition.RejectedDetritus>0.0)
                environment.ReturnDetritus(detritusReservation.Value,decomposition.RejectedDetritus);
            if(decomposition.ReturnedMinerals>0.0)
                environment.DepositMinerals(organismPosition,decomposition.ReturnedMinerals);
        }

        return new RegionalExchangeResult(
            oxygenUptake, oxygenReleased, substrateUptake,
            matterDemand, waterUptake, waterLost,
            lightEnergy, assimilationEnergy, waterArea, airArea, exposedSamples, occludedSamples,
            absorbedLight, photosyntheticHeat, photosyntheticMaintenance,
            photosynthesizedSubstrate,mineralConsumed,organicConsumed,organicAssimilated,
            detritusDecomposed,mineralsReturned,matterDemand,
            mineralConsumed+organicConsumed+detritusDecomposed);
    }

    private static double EffectiveExchange(BodyRegion region) =>
        Math.Clamp(region.ExchangeExpression/0.50,0.0,1.25)*
        (1.0-0.30*region.BarrierExpression);

    public static RegionalTransportResult TransportAlongMatterEdges(
        DevelopingBody body,
        Genome genome,
        SimulationConfig config,
        double deltaSeconds,
        Func<int, int, bool>? edgeEnabled = null,
        bool reverseEdgeEnumeration = false)
    {
        Span<TransferRequest> requests = stackalloc TransferRequest[GenomeValidator.MaximumRegions * 4];
        int requestCount = 0;
        int start = reverseEdgeEnumeration ? genome.Regions.Count - 1 : 0;
        int end = reverseEdgeEnumeration ? -1 : genome.Regions.Count;
        int direction = reverseEdgeEnumeration ? -1 : 1;
        for (int geneIndex = start; geneIndex != end; geneIndex += direction)
        {
            RegionGene targetGene = genome.Regions[geneIndex];
            if (targetGene.IsCore) continue;
            int targetId = targetGene.RegionId;
            int sourceId = targetGene.MatterSourceRegionId;
            int targetIndex = body.IndexOfRegion(targetId);
            int sourceIndex = body.IndexOfRegion(sourceId);
            if (targetIndex < 0 || sourceIndex < 0 ||
                (edgeEnabled is not null && !edgeEnabled(sourceId, targetId)))
                continue;
            BodyRegion source = body.Regions[sourceIndex];
            BodyRegion target = body.Regions[targetIndex];
            double length = Math.Max(
                0.08,
                Vector2.Distance(body.FunctionalGeometry[sourceIndex].LocalCenter,
                    body.FunctionalGeometry[targetIndex].LocalCenter));
            double crossSection = Math.Max(
                0.02,
                Math.Min(source.Matter, target.Matter) * 0.35);
            double conductance = targetGene.Permeability * body.FunctionalGeometry[targetIndex].MatterTransportEfficiency *
                crossSection / length;
            AddBidirectionalRequest(requests, ref requestCount, source, target, InventoryKind.Substrate,
                SubstrateCapacity(source, genome.GetRegion(sourceId)), SubstrateCapacity(target, targetGene),
                conductance * 0.55 * deltaSeconds);
            AddBidirectionalRequest(requests, ref requestCount, source, target, InventoryKind.Oxygen,
                OxygenCapacity(source, config), OxygenCapacity(target, config),
                conductance * 0.85 * deltaSeconds);
            AddBidirectionalRequest(requests, ref requestCount, source, target, InventoryKind.Water,
                WaterCapacity(source, genome.Metabolism), WaterCapacity(target, genome.Metabolism),
                conductance * 0.45 * deltaSeconds);
            double sourceEnergyCapacity=config.MaximumEnergy*source.Matter/
                Math.Max(0.05,body.Cache.TotalMatter);
            double targetEnergyCapacity=config.MaximumEnergy*target.Matter/
                Math.Max(0.05,body.Cache.TotalMatter);
            double energyConductance=(0.15+(0.85*targetGene.SignalConductivity))*
                body.FunctionalGeometry[targetIndex].ConnectionTransmission*crossSection/length;
            AddBidirectionalRequest(requests,ref requestCount,source,target,InventoryKind.Energy,
                sourceEnergyCapacity,targetEnergyCapacity,energyConductance*0.70*deltaSeconds);
        }

        for (int index = 1; index < requestCount; index++)
        {
            TransferRequest value = requests[index];
            int insert = index - 1;
            while (insert >= 0 && Compare(requests[insert], value) > 0)
            { requests[insert + 1] = requests[insert]; insert--; }
            requests[insert + 1] = value;
        }
        Span<double> totals = stackalloc double[GenomeValidator.MaximumRegions * 4];
        Span<RegionalInventoryDelta> deltas = stackalloc RegionalInventoryDelta[GenomeValidator.MaximumRegions];
        Span<bool> activePairs = stackalloc bool[GenomeValidator.MaximumRegions * GenomeValidator.MaximumRegions];
        for (int index = 0; index < requestCount; index++)
        {
            TransferRequest request = requests[index];
            int donorIndex = body.IndexOfRegion(request.DonorId);
            totals[(donorIndex * 4) + (int)request.Kind] += request.Amount;
        }
        double substrateTransferred = 0.0;
        double oxygenTransferred = 0.0;
        double waterTransferred = 0.0;
        double energyTransferred = 0.0;
        int activeEdges = 0;
        for (int index = 0; index < requestCount; index++)
        {
            TransferRequest request = requests[index];
            int donorIndex = body.IndexOfRegion(request.DonorId);
            int receiverIndex = body.IndexOfRegion(request.ReceiverId);
            double total = totals[(donorIndex * 4) + (int)request.Kind];
            double available = Inventory(body.Regions[donorIndex], request.Kind);
            double scale = total > available
                ? available / total
                : 1.0;
            double amount = request.Amount * scale;
            deltas[donorIndex] = AddTransferDelta(deltas[donorIndex], request.Kind, -amount);
            deltas[receiverIndex] = AddTransferDelta(deltas[receiverIndex], request.Kind, amount);
            int pair = donorIndex * GenomeValidator.MaximumRegions + receiverIndex;
            if (!activePairs[pair]) { activePairs[pair] = true; activeEdges++; }
            switch (request.Kind)
            {
                case InventoryKind.Substrate: substrateTransferred += amount; break;
                case InventoryKind.Oxygen: oxygenTransferred += amount; break;
                case InventoryKind.Water: waterTransferred += amount; break;
                case InventoryKind.Energy: energyTransferred += amount; break;
            }
        }

        double before = body.TotalSubstrate + body.TotalOxygen + body.TotalWater + body.TotalEnergy;
        for (int index = 0; index < body.RegionCount; index++)
            body.ApplyInventoryDelta(body.Regions[index].RegionId, deltas[index]);
        double after = body.TotalSubstrate + body.TotalOxygen + body.TotalWater + body.TotalEnergy;
        double minimum = body.Regions.Min(region =>
            Math.Min(Math.Min(region.Substrate,region.Energy), Math.Min(region.Oxygen, region.Water)));
        return new RegionalTransportResult(
            substrateTransferred, oxygenTransferred, waterTransferred,energyTransferred,
            after - before, minimum, activeEdges);
    }

    public static RegionalMetabolismResult ReactAndMaintain(
        DevelopingBody body,
        Genome genome,
        IMutableEnvironmentField environment,
        Vector2 position,
        SimulationConfig config,
        double deltaSeconds)
    {
        double substrateConsumed = 0.0;
        double oxygenConsumed = 0.0;
        double energyProduced = 0.0;
        double waste = 0.0;
        double maintenancePaid = 0.0;
        double hypoxia = 0.0;
        int regionCount = body.RegionCount;
        for (int regionIndex = 0; regionIndex < regionCount; regionIndex++)
        {
            BodyRegion snapshot = body.Regions[regionIndex];
            RegionGene gene = genome.GetRegion(snapshot.RegionId);
            double regionalEnergyCapacity = Math.Max(
                0.2, config.MaximumEnergy * snapshot.Matter / Math.Max(0.05, body.Cache.TotalMatter));
            // Respiration follows local demand. Do not burn building material
            // at a fixed rate while photosynthesis has already filled the reserve.
            double energyTarget = regionalEnergyCapacity * 0.65;
            double energyRoom = Math.Max(0.0, energyTarget - snapshot.Energy);
            // Keep respiration available through the reproduction reserve. Taper
            // only near the target so a viable metabolism can actually recharge,
            // while still stopping substrate burn when local energy is full.
            double demand = Math.Clamp(energyRoom / Math.Max(0.05, energyTarget * 0.25), 0.0, 1.0);
            double catalyticRate = config.MetabolicSubstratePerSecond *
                (0.20 + (0.80 * gene.CatalyticActivity)) * snapshot.Matter * deltaSeconds * demand;
            double aerobicDemand = Math.Min(snapshot.Substrate, catalyticRate) *
                genome.Metabolism.OxygenUseFraction;
            double aerobicByOxygen = snapshot.Oxygen / config.OxygenPerAerobicSubstrate;
            double aerobic = Math.Min(aerobicDemand, aerobicByOxygen);
            hypoxia += Math.Max(0.0, aerobicDemand - aerobic);
            double anaerobicCapacity = Math.Min(
                snapshot.Substrate - aerobic,
                catalyticRate * (1.0 - genome.Metabolism.OxygenUseFraction));
            double anaerobic = Math.Max(0.0, anaerobicCapacity);
            double producedEnergy =
                (aerobic * config.AerobicEnergyPerSubstrate *
                    (0.30 + (0.70 * genome.Metabolism.OxygenCatalysis))) +
                (anaerobic * config.AnaerobicEnergyPerSubstrate *
                    (1.0 - (0.35 * genome.Metabolism.OxygenCatalysis)));
            double execution = producedEnergy > 0.0 ? Math.Min(1.0, energyRoom / producedEnergy) : 0.0;
            aerobic *= execution;
            anaerobic *= execution;
            producedEnergy *= execution;
            double usedSubstrate = aerobic + anaerobic;
            double usedOxygen = aerobic * config.OxygenPerAerobicSubstrate;
            body.ApplyInventoryDelta(snapshot.RegionId, new RegionalInventoryDelta(
                -usedSubstrate,
                -usedOxygen,
                0.0,
                producedEnergy));
            substrateConsumed += usedSubstrate;
            oxygenConsumed += usedOxygen;
            energyProduced += producedEnergy;
            waste += usedSubstrate;

            BodyRegion updated = body.GetRegion(snapshot.RegionId);
            double maintenance = ((config.BaseMaintenanceEnergyPerSecond * updated.Matter) +
                (updated.Matter * (0.00067 + (0.00083 * gene.SignalConductivity) +
                    0.0006*(updated.ExchangeExpression+updated.BarrierExpression+
                        updated.ContractileExpression+updated.StructuralExpression+
                        updated.SensoryExpression+updated.PhotosyntheticExpression+
                        updated.FeedingExpression+updated.DigestiveExpression+
                        updated.DecomposerExpression)))) * deltaSeconds;
            double paid = Math.Min(updated.Energy, maintenance);
            body.ApplyInventoryDelta(updated.RegionId, new RegionalInventoryDelta(0.0, 0.0, 0.0, -paid));
            maintenancePaid += paid;
            if (paid + 1e-12 < maintenance)
            {
                // Damage reflects the fraction of essential upkeep not supplied,
                // rather than an absolute energy debt that makes tiny bodies immortal.
                double deficitFraction = Math.Clamp((maintenance - paid) / maintenance, 0.0, 1.0);
                body.AddDamage(updated.RegionId, deficitFraction * deltaSeconds / config.MaintenanceFailureSeconds);
            }
        }
        environment.DepositMetabolicWaste(position, waste);
        return new RegionalMetabolismResult(
            substrateConsumed, oxygenConsumed, energyProduced, waste,
            maintenancePaid, hypoxia);
    }

    private static void AddBidirectionalRequest(
        Span<TransferRequest> requests,
        ref int requestCount,
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
        requests[requestCount++] = signed > 0.0
            ? new TransferRequest(first.RegionId, second.RegionId, kind, signed)
            : new TransferRequest(second.RegionId, first.RegionId, kind, -signed);
    }

    private static double Inventory(BodyRegion region, InventoryKind kind) => kind switch
    {
        InventoryKind.Substrate => region.Substrate,
        InventoryKind.Oxygen => region.Oxygen,
        InventoryKind.Water => region.Water,
        _ => region.Energy
    };

    private static RegionalInventoryDelta AddTransferDelta(
        RegionalInventoryDelta delta, InventoryKind kind, double amount) => kind switch
        {
            InventoryKind.Substrate => delta with { Substrate = delta.Substrate + amount },
            InventoryKind.Oxygen => delta with { Oxygen = delta.Oxygen + amount },
            InventoryKind.Water => delta with { Water = delta.Water + amount },
            _ => delta with { Energy = delta.Energy + amount }
        };

    private static int Compare(TransferRequest left, TransferRequest right)
    {
        int donor = left.DonorId.CompareTo(right.DonorId);
        if (donor != 0) return donor;
        int receiver = left.ReceiverId.CompareTo(right.ReceiverId);
        return receiver != 0 ? receiver : left.Kind.CompareTo(right.Kind);
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
