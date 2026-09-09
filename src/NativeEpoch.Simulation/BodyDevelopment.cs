using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct BodyRegion(
    int RegionId,
    double Matter,
    double Development,
    double Substrate = 0.0,
    double Oxygen = 0.0,
    double Water = 0.0,
    double Energy = 0.0,
    double Damage = 0.0,
    double InternalSignal = 0.0,
    double TransportAvailability = 0.0,
    double Activation = 0.0)
{
    public bool AllFinite =>
        RegionId >= 0 && double.IsFinite(Matter) && Matter > 0.0 &&
        double.IsFinite(Development) && Development is >= 0.0 and <= 1.0 &&
        double.IsFinite(Substrate) && Substrate >= 0.0 &&
        double.IsFinite(Oxygen) && Oxygen >= 0.0 &&
        double.IsFinite(Water) && Water >= 0.0 &&
        double.IsFinite(Energy) && Energy >= 0.0 &&
        double.IsFinite(Damage) && Damage is >= 0.0 and <= 1.0 &&
        double.IsFinite(InternalSignal) && InternalSignal is >= -1.0 and <= 1.0 &&
        double.IsFinite(TransportAvailability) && TransportAvailability is >= 0.0 and <= 1.0 &&
        double.IsFinite(Activation) && Activation is >= 0.0 and <= 1.0;
}

public readonly record struct BodyVisualRegion(
    int RegionId,
    Vector2 LocalCenter,
    double Angle,
    double Length,
    double Width,
    double Thickness,
    Vector3 Color);

public readonly record struct BodyFunctionalGeometry(
    int RegionId,
    Vector2 LocalCenter,
    Vector2 Direction,
    double ExposedSurface,
    double ExposureFraction,
    double SignalTransportEfficiency,
    double MatterTransportEfficiency,
    double ConnectionTransmission,
    double ExchangeDistance,
    IReadOnlyList<BodySurfaceSample> SurfaceSamples);

public readonly record struct BodySurfaceSample(
    Vector3 LocalPosition,
    Vector3 LocalNormal,
    double AreaWeight,
    bool ExternallyConnected);

public readonly record struct RegionalInventoryDelta(
    double Substrate,
    double Oxygen,
    double Water,
    double Energy);

public readonly record struct BodyCache(
    double TotalMatter,
    double PhysicalMass,
    Vector2 CenterOfMass,
    double RotationalInertia,
    double ExposedSurface,
    double Drag,
    double Buoyancy,
    double StructuralStiffness,
    double WeakestConnection,
    double LightCaptureSurface,
    double MatterUptakeSurface,
    double CatalyticSurface,
    double StorageCapacity,
    double MaintenanceEnergyPerSecond,
    double MaximumActivationEnergyPerSecond,
    double BoundingRadius,
    double MatterConnectivity,
    double SignalConnectivity)
{
    public bool AllFinite =>
        double.IsFinite(TotalMatter) &&
        double.IsFinite(PhysicalMass) &&
        float.IsFinite(CenterOfMass.X) &&
        float.IsFinite(CenterOfMass.Y) &&
        double.IsFinite(RotationalInertia) &&
        double.IsFinite(ExposedSurface) &&
        double.IsFinite(Drag) &&
        double.IsFinite(Buoyancy) &&
        double.IsFinite(StructuralStiffness) &&
        double.IsFinite(WeakestConnection) &&
        double.IsFinite(LightCaptureSurface) &&
        double.IsFinite(MatterUptakeSurface) &&
        double.IsFinite(CatalyticSurface) &&
        double.IsFinite(StorageCapacity) &&
        double.IsFinite(MaintenanceEnergyPerSecond) &&
        double.IsFinite(MaximumActivationEnergyPerSecond) &&
        double.IsFinite(BoundingRadius) &&
        double.IsFinite(MatterConnectivity) &&
        double.IsFinite(SignalConnectivity);
}

public sealed class DevelopingBody
{
    private readonly List<BodyRegion> _regions = [];

    public DevelopingBody(
        Genome genome,
        double coreMatter,
        double initialSubstrate = 0.0,
        double initialEnergy = 0.0,
        double initialOxygen = 0.0,
        double initialWater = 1.0)
    {
        RegionGene core = genome.Regions.Single(region => region.IsCore);
        double target = BodyCalculator.TargetMatter(core);
        if (!double.IsFinite(coreMatter) || coreMatter <= 0.0 || coreMatter > target)
            throw new ArgumentOutOfRangeException(nameof(coreMatter));
        double[] inventories = [initialSubstrate, initialEnergy, initialOxygen, initialWater];
        if (inventories.Any(value => !double.IsFinite(value) || value < 0.0))
            throw new ArgumentOutOfRangeException(nameof(initialSubstrate));
        _regions.Add(new BodyRegion(
            core.RegionId,
            coreMatter,
            coreMatter / target,
            initialSubstrate,
            initialOxygen,
            initialWater,
            initialEnergy));
        RebuildGeometry(genome);
    }

    public DevelopingBody(Genome genome, IEnumerable<BodyRegion> regionalState)
    {
        _regions.AddRange(regionalState.OrderBy(region => region.RegionId));
        if (_regions.Count == 0 || !_regions.All(region => region.AllFinite) ||
            _regions.Select(region => region.RegionId).Distinct().Count() != _regions.Count ||
            _regions.Any(region => genome.Regions.All(gene => gene.RegionId != region.RegionId)))
        {
            throw new ArgumentException("Regional state must be finite, unique, and present in the genome.", nameof(regionalState));
        }
        RebuildGeometry(genome);
    }

    public IReadOnlyList<BodyRegion> Regions => _regions;
    public BodyCache Cache { get; private set; }
    public BodyGeometry Geometry { get; private set; } = null!;
    public IReadOnlyList<BodyFunctionalGeometry> FunctionalGeometry { get; private set; } = [];
    private BodyFunctionalGeometry[] RestFunctionalGeometry { get; set; } = [];
    private BodyFunctionalGeometry[] PosedFunctionalGeometry { get; set; } = [];
    private BodySurfaceSample[][] PosedSurfaceSamples { get; set; } = [];
    public int RegionCount => _regions.Count;
    public double TotalSubstrate => _regions.Sum(region => region.Substrate);
    public double TotalOxygen => _regions.Sum(region => region.Oxygen);
    public double TotalWater => _regions.Sum(region => region.Water);
    public double TotalEnergy => _regions.Sum(region => region.Energy);
    public double AverageDamage => _regions.Average(region => region.Damage);

    public BodyRegion GetRegion(int regionId)
    {
        int index = IndexOfRegion(regionId);
        return index >= 0 ? _regions[index] : throw new KeyNotFoundException($"Body has no region {regionId}.");
    }

    public int IndexOfRegion(int regionId)
    {
        for (int index = 0; index < _regions.Count; index++)
            if (_regions[index].RegionId == regionId)
                return index;
        return -1;
    }

    public double Grow(
        Genome genome,
        double maturity,
        SimulationConfig config)
    {
        double remainingGrowth = config.GrowthMatterPerSecond * config.FixedDeltaSeconds;
        double totalGrowth = 0.0;

        foreach (RegionGene gene in genome.Regions)
        {
            if (remainingGrowth <= 0.0)
                break;
            if (maturity < gene.AppearanceMaturity)
                continue;
            if (!gene.IsCore && !_regions.Any(region => region.RegionId == gene.ParentRegionId))
                continue;

            double localDevelopment = gene.AppearanceMaturity >= 1.0
                ? (maturity >= 1.0 ? 1.0 : 0.0)
                : Math.Clamp(
                    (maturity - gene.AppearanceMaturity) / (1.0 - gene.AppearanceMaturity),
                    0.0,
                    1.0);
            double targetMatter = BodyCalculator.TargetMatter(gene) * localDevelopment;
            int bodyIndex = _regions.FindIndex(region => region.RegionId == gene.RegionId);
            double currentMatter = bodyIndex >= 0 ? _regions[bodyIndex].Matter : 0.0;
            double needed = targetMatter - currentMatter;
            if (needed <= 1e-12)
                continue;

            int sourceIndex = gene.IsCore
                ? bodyIndex
                : _regions.FindIndex(region => region.RegionId == gene.MatterSourceRegionId);
            if (sourceIndex < 0)
                continue;
            BodyRegion source = _regions[sourceIndex];
            double affordableByEnergy = source.Energy / config.GrowthEnergyPerMatter;
            double amount = Math.Min(
                needed,
                Math.Min(remainingGrowth, Math.Min(source.Substrate, affordableByEnergy)));
            if (amount <= 0.0)
                continue;

            source = source with
            {
                Substrate = source.Substrate - amount,
                Energy = source.Energy - (amount * config.GrowthEnergyPerMatter)
            };
            _regions[sourceIndex] = source;
            remainingGrowth -= amount;
            totalGrowth += amount;
            double newMatter = currentMatter + amount;
            BodyRegion updated = new(
                gene.RegionId,
                newMatter,
                Math.Clamp(newMatter / BodyCalculator.TargetMatter(gene), 0.0, 1.0),
                bodyIndex >= 0 ? _regions[bodyIndex].Substrate : 0.0,
                bodyIndex >= 0 ? _regions[bodyIndex].Oxygen : 0.0,
                bodyIndex >= 0 ? _regions[bodyIndex].Water : 0.0,
                bodyIndex >= 0 ? _regions[bodyIndex].Energy : 0.0,
                bodyIndex >= 0 ? _regions[bodyIndex].Damage : 0.0,
                bodyIndex >= 0 ? _regions[bodyIndex].InternalSignal : 0.0,
                bodyIndex >= 0 ? _regions[bodyIndex].TransportAvailability : 0.0,
                bodyIndex >= 0 ? _regions[bodyIndex].Activation : 0.0);
            if (bodyIndex >= 0)
                _regions[bodyIndex] = updated;
            else
                _regions.Add(updated);
        }

        if (totalGrowth > 0.0)
        {
            RebuildGeometry(genome);
        }
        return totalGrowth;
    }

    public void UpdateFunctionalState(
        Genome genome,
        ControllerOutputs outputs,
        IReadOnlyDictionary<int, double> localSignals,
        double deltaSeconds)
    {
        Dictionary<int, BodyRegion> previous = _regions.ToDictionary(region => region.RegionId);
        Dictionary<int, RegionGene> genes = genome.Regions.ToDictionary(gene => gene.RegionId);
        for (int index = 0; index < _regions.Count; index++)
        {
            BodyRegion region = _regions[index];
            RegionGene gene = genes[region.RegionId];
            double sourceSignal = gene.IsCore || !previous.TryGetValue(gene.SignalSourceRegionId, out BodyRegion signalSource)
                ? 0.0
                : signalSource.InternalSignal * gene.SignalConductivity;
            double local = localSignals.TryGetValue(region.RegionId, out double signal) ? signal : 0.0;
            double targetSignal = Math.Tanh(local + sourceSignal);
            double signalRate = 0.8 + (3.2 * gene.SignalConductivity);
            double signalBlend = 1.0 - Math.Exp(-signalRate * deltaSeconds);
            double nextSignal = Math.Clamp(
                region.InternalSignal + ((targetSignal - region.InternalSignal) * signalBlend),
                -1.0,
                1.0);

            double sourceTransport = gene.IsCore ||
                !previous.TryGetValue(gene.MatterSourceRegionId, out BodyRegion matterSource)
                ? 1.0
                : matterSource.TransportAvailability;
            double targetTransport = sourceTransport * gene.Permeability *
                (0.25 + (0.75 * outputs.PermeabilityGate));
            double transportBlend = 1.0 - Math.Exp(-(0.5 + (2.5 * gene.Permeability)) * deltaSeconds);
            double nextTransport = Math.Clamp(
                region.TransportAvailability +
                ((targetTransport - region.TransportAvailability) * transportBlend),
                0.0,
                1.0);
            double activation = Math.Clamp(
                outputs.ContractionActivation * gene.Contractility *
                (0.35 + (0.65 * ((nextSignal + 1.0) * 0.5))) *
                (0.30 + (0.70 * gene.SignalConductivity)),
                0.0,
                1.0);
            _regions[index] = region with
            {
                InternalSignal = nextSignal,
                TransportAvailability = nextTransport,
                Activation = activation
            };
        }
    }

    public void ApplyInventoryDelta(int regionId, RegionalInventoryDelta delta)
    {
        int index = _regions.FindIndex(region => region.RegionId == regionId);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(regionId));
        BodyRegion region = _regions[index];
        double substrate = region.Substrate + delta.Substrate;
        double oxygen = region.Oxygen + delta.Oxygen;
        double water = region.Water + delta.Water;
        double energy = region.Energy + delta.Energy;
        if (!double.IsFinite(substrate) || !double.IsFinite(oxygen) ||
            !double.IsFinite(water) || !double.IsFinite(energy) ||
            substrate < -1e-10 || oxygen < -1e-10 || water < -1e-10 || energy < -1e-10)
        {
            throw new InvalidOperationException("A regional inventory update would create an invalid or negative pool.");
        }
        _regions[index] = region with
        {
            Substrate = Math.Max(0.0, substrate),
            Oxygen = Math.Max(0.0, oxygen),
            Water = Math.Max(0.0, water),
            Energy = Math.Max(0.0, energy)
        };
    }

    public double ConsumeEnergy(double requested) => ConsumeInventory(requested, InventoryKind.Energy);
    public double ConsumeSubstrate(double requested) => ConsumeInventory(requested, InventoryKind.Substrate);
    public double ConsumeOxygen(double requested) => ConsumeInventory(requested, InventoryKind.Oxygen);
    public double ConsumeWater(double requested) => ConsumeInventory(requested, InventoryKind.Water);

    public double ConsumeRegionEnergy(int regionId, double requested)
    {
        if (!double.IsFinite(requested) || requested < 0) throw new ArgumentOutOfRangeException(nameof(requested));
        int index = _regions.FindIndex(region => region.RegionId == regionId);
        if (index < 0) return 0;
        BodyRegion region = _regions[index];
        double paid = Math.Min(region.Energy, requested);
        _regions[index] = region with { Energy = region.Energy - paid };
        return paid;
    }

    public void UpdateFunctionalState(
        Genome genome,
        ControllerOutputs outputs,
        double localLight,
        double localPressure,
        ForagingDecision foraging,
        double deltaSeconds)
    {
        Span<BodyRegion> previous = stackalloc BodyRegion[GenomeValidator.MaximumRegions];
        for (int index = 0; index < _regions.Count; index++) previous[index] = _regions[index];
        for (int index = 0; index < _regions.Count; index++)
        {
            BodyRegion region = _regions[index];
            RegionGene gene = genome.GetRegion(region.RegionId);
            BodyRegion signalSource = default;
            bool hasSource = false;
            if (!gene.IsCore)
                for (int sourceIndex = 0; sourceIndex < _regions.Count; sourceIndex++)
                    if (previous[sourceIndex].RegionId == gene.SignalSourceRegionId)
                    { signalSource = previous[sourceIndex]; hasSource = true; break; }
            double sourceSignal = hasSource ? signalSource.InternalSignal * gene.SignalConductivity : 0.0;
            BodyFunctionalGeometry functional = FunctionalGeometry[index];
            double local = Math.Clamp(
                (localLight * Vector2.Dot(functional.Direction, Vector2.UnitX)) +
                (foraging.ResourceGradient*0.70*functional.Direction.X)+
                ((foraging.ResourceLateral+(0.35*foraging.ExplorationSignal))*0.90*functional.Direction.Y)-
                (0.08 * localPressure), -1.0, 1.0);
            double targetSignal = Math.Tanh(local + sourceSignal);
            double signalRate = 0.8 + (3.2 * gene.SignalConductivity);
            double signalBlend = 1.0 - Math.Exp(-signalRate * deltaSeconds);
            double nextSignal = Math.Clamp(region.InternalSignal +
                ((targetSignal - region.InternalSignal) * signalBlend), -1.0, 1.0);

            BodyRegion matterSource = default;
            bool hasMatterSource = false;
            if (!gene.IsCore)
                for (int sourceIndex = 0; sourceIndex < _regions.Count; sourceIndex++)
                    if (previous[sourceIndex].RegionId == gene.MatterSourceRegionId)
                    { matterSource = previous[sourceIndex]; hasMatterSource = true; break; }
            double sourceTransport = hasMatterSource ? matterSource.TransportAvailability : 1.0;
            double targetTransport = sourceTransport * gene.Permeability *
                (0.25 + (0.75 * outputs.PermeabilityGate));
            double transportBlend = 1.0 - Math.Exp(-(0.5 + (2.5 * gene.Permeability)) * deltaSeconds);
            double nextTransport = Math.Clamp(region.TransportAvailability +
                ((targetTransport - region.TransportAvailability) * transportBlend), 0.0, 1.0);
            double activation = Math.Clamp(outputs.ContractionActivation * gene.Contractility *
                (0.65 + (0.35 * ((nextSignal + 1.0) * 0.5))) *
                (0.60 + (0.40 * gene.SignalConductivity)), 0.0, 1.0);
            _regions[index] = region with
                { InternalSignal = nextSignal, TransportAvailability = nextTransport, Activation = activation };
        }
    }

    public void ApplyPoseGeometry(Genome genome, BodyPose pose)
    {
        for (int index = 0; index < RestFunctionalGeometry.Length; index++)
        {
            BodyFunctionalGeometry functional = RestFunctionalGeometry[index];
            BodyGeometryRegion old = Geometry.Regions[index];
            if (!pose.TryGetRegion(functional.RegionId, out BodyPoseRegion current))
                current = new BodyPoseRegion(old.RegionId, old.Center, old.Angle, old.Length,
                    old.StartRadius + old.EndRadius,
                    (old.StartRadius + old.EndRadius) * old.VerticalScale, 0.0);
            double delta = current.Angle - old.Angle;
            double c = Math.Cos(delta), s = Math.Sin(delta);
            BodySurfaceSample[] posedSamples = PosedSurfaceSamples[index];
            for (int sampleIndex = 0; sampleIndex < functional.SurfaceSamples.Count; sampleIndex++)
            {
                BodySurfaceSample sample = functional.SurfaceSamples[sampleIndex];
                Vector2 local = new(sample.LocalPosition.X - old.Center.X, sample.LocalPosition.Z - old.Center.Y);
                Vector2 rotated = new((float)(local.X*c-local.Y*s), (float)(local.X*s+local.Y*c));
                double lengthScale = current.Length / Math.Max(1e-8, old.Length);
                Vector3 p = new(current.LocalCenter.X + rotated.X*(float)lengthScale,
                    sample.LocalPosition.Y * (float)(current.Thickness/Math.Max(1e-8,
                        (old.StartRadius + old.EndRadius) * old.VerticalScale)),
                    current.LocalCenter.Y + rotated.Y*(float)lengthScale);
                Vector3 n = new((float)(sample.LocalNormal.X*c-sample.LocalNormal.Z*s), sample.LocalNormal.Y,
                    (float)(sample.LocalNormal.X*s+sample.LocalNormal.Z*c));
                posedSamples[sampleIndex] = sample with
                    { LocalPosition = p, LocalNormal = Vector3.Normalize(n) };
            }
            PosedFunctionalGeometry[index] = functional with
            {
                LocalCenter = current.LocalCenter - Cache.CenterOfMass,
                Direction = new Vector2((float)Math.Cos(current.Angle), (float)Math.Sin(current.Angle)),
                SurfaceSamples = posedSamples
            };
        }
    }

    private void RebuildGeometry(Genome genome)
    {
        Cache = BodyCalculator.Recalculate(genome, _regions);
        Geometry = BodyGeometryBuilder.Build(genome, _regions);
        RestFunctionalGeometry = BodyCalculator.BuildFunctionalGeometry(
            genome, _regions, Cache.CenterOfMass, Geometry).ToArray();
        PosedFunctionalGeometry = new BodyFunctionalGeometry[RestFunctionalGeometry.Length];
        PosedSurfaceSamples = new BodySurfaceSample[RestFunctionalGeometry.Length][];
        for (int index = 0; index < RestFunctionalGeometry.Length; index++)
        {
            BodySurfaceSample[] samples = RestFunctionalGeometry[index].SurfaceSamples.ToArray();
            PosedSurfaceSamples[index] = samples;
            PosedFunctionalGeometry[index] = RestFunctionalGeometry[index] with { SurfaceSamples = samples };
        }
        FunctionalGeometry = PosedFunctionalGeometry;
    }

    public double LimitTotalEnergy(double maximum)
    {
        if (!double.IsFinite(maximum) || maximum <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        double excess = Math.Max(0.0, TotalEnergy - maximum);
        return ConsumeEnergy(excess);
    }

    public void AddDamage(int regionId, double amount)
    {
        if (!double.IsFinite(amount) || amount < 0.0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        int index = _regions.FindIndex(region => region.RegionId == regionId);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(regionId));
        BodyRegion region = _regions[index];
        _regions[index] = region with { Damage = Math.Clamp(region.Damage + amount, 0.0, 1.0) };
    }

    private double ConsumeInventory(double requested, InventoryKind kind)
    {
        if (!double.IsFinite(requested) || requested < 0.0)
            throw new ArgumentOutOfRangeException(nameof(requested));
        double remaining = requested;
        foreach (int index in Enumerable.Range(0, _regions.Count)
                     .OrderBy(index => _regions[index].RegionId))
        {
            if (remaining <= 0.0)
                break;
            BodyRegion region = _regions[index];
            double available = kind switch
            {
                InventoryKind.Substrate => region.Substrate,
                InventoryKind.Oxygen => region.Oxygen,
                InventoryKind.Water => region.Water,
                _ => region.Energy
            };
            double amount = Math.Min(available, remaining);
            remaining -= amount;
            _regions[index] = kind switch
            {
                InventoryKind.Substrate => region with { Substrate = region.Substrate - amount },
                InventoryKind.Oxygen => region with { Oxygen = region.Oxygen - amount },
                InventoryKind.Water => region with { Water = region.Water - amount },
                _ => region with { Energy = region.Energy - amount }
            };
        }
        return requested - remaining;
    }

    private enum InventoryKind
    {
        Substrate,
        Oxygen,
        Water,
        Energy
    }

    public BodyCache Recalculate(Genome genome) => BodyCalculator.Recalculate(genome, _regions);

    public double DevelopmentCompletion(Genome genome)
    {
        double target = genome.Regions.Sum(BodyCalculator.TargetMatter);
        return target > 0.0 ? Math.Clamp(Cache.TotalMatter / target, 0.0, 1.0) : 0.0;
    }

    public bool AllFinite(Genome genome) =>
        _regions.Count is >= 1 and <= GenomeValidator.MaximumRegions &&
        _regions.All(region => region.AllFinite) &&
        _regions.Select(region => region.RegionId).Distinct().Count() == _regions.Count &&
        _regions.All(region => genome.Regions.Any(gene => gene.RegionId == region.RegionId)) &&
        Cache.AllFinite;
}

public static class BodyCalculator
{
    public static double TargetMatter(RegionGene gene)
    {
        double propertyBudget =
            gene.Density + gene.Rigidity + gene.Toughness + gene.Permeability +
            gene.LightReactivity + gene.CatalyticActivity + gene.Contractility +
            gene.SignalConductivity + gene.StorageFraction + gene.Pigment;
        return 0.12 +
            (gene.TargetLength * gene.TargetWidth) *
            (0.35 + (0.65 * gene.Density)) *
            (0.75 + (0.025 * propertyBudget));
    }

    public static BodyCache Recalculate(Genome genome, IReadOnlyList<BodyRegion> bodyRegions)
    {
        Dictionary<int, RegionGene> genes = genome.Regions.ToDictionary(gene => gene.RegionId);
        Dictionary<int, BodyRegion> bodies = bodyRegions.ToDictionary(region => region.RegionId);
        Dictionary<int, Vector2> centers = [];
        Dictionary<int, double> angles = [];
        List<ShapeSample> samples = [];

        List<BodyRegion> pending = bodyRegions.ToList();
        while (pending.Count > 0)
        {
            int pendingIndex = pending.FindIndex(body =>
            {
                RegionGene candidate = genes[body.RegionId];
                return candidate.IsCore || centers.ContainsKey(candidate.ParentRegionId);
            });
            if (pendingIndex < 0)
                throw new InvalidOperationException("Developed geometry is not rooted at the core.");

            BodyRegion body = pending[pendingIndex];
            pending.RemoveAt(pendingIndex);
            RegionGene gene = genes[body.RegionId];
            double scale = Math.Sqrt(Math.Clamp(body.Matter / TargetMatter(gene), 0.0, 1.0));
            double length = gene.TargetLength * scale;
            double width = gene.TargetWidth * scale;
            double angle = gene.RelativeAngle;
            Vector2 parentCenter = Vector2.Zero;
            if (!gene.IsCore && centers.TryGetValue(gene.ParentRegionId, out Vector2 foundCenter))
            {
                parentCenter = foundCenter;
                angle += angles[gene.ParentRegionId];
            }

            Vector2 direction = new((float)Math.Cos(angle), (float)Math.Sin(angle));
            Vector2 center = gene.IsCore
                ? Vector2.Zero
                : parentCenter + (direction * (float)(length * 0.5));
            centers[gene.RegionId] = center;
            angles[gene.RegionId] = angle;
            samples.Add(new ShapeSample(gene, body, center, angle, length, width));
        }

        double totalMatter = bodyRegions.Sum(region => region.Matter);
        double physicalMass = samples.Sum(sample => sample.Body.Matter * (0.75 + (0.5 * sample.Gene.Density)));
        Vector2 centerOfMass = Vector2.Zero;
        if (physicalMass > 0.0)
        {
            foreach (ShapeSample sample in samples)
            {
                double mass = sample.Body.Matter * (0.75 + (0.5 * sample.Gene.Density));
                centerOfMass += sample.Center * (float)(mass / physicalMass);
            }
        }

        double inertia = 0.0;
        double exposedSurface = 0.0;
        double drag = 0.0;
        double buoyancy = 0.0;
        double stiffness = 0.0;
        double weakest = 1.0;
        double light = 0.0;
        double uptake = 0.0;
        double catalysis = 0.0;
        double storage = 0.0;
        double maintenance = 0.0;
        double activation = 0.0;
        double radius = 0.0;
        int matterLinks = 0;
        int signalLinks = 0;

        foreach (ShapeSample sample in samples)
        {
            RegionGene gene = sample.Gene;
            double mass = sample.Body.Matter * (0.75 + (0.5 * gene.Density));
            double surface = 2.0 * (sample.Length + sample.Width);
            double distanceSquared = Vector2.DistanceSquared(sample.Center, centerOfMass);
            inertia += mass * (distanceSquared + ((sample.Length * sample.Length + sample.Width * sample.Width) / 12.0));
            exposedSurface += surface;
            drag += surface * (0.45 + (0.55 * gene.Density));
            buoyancy += sample.Body.Matter * (1.25 - (0.5 * gene.Density));
            stiffness += sample.Body.Matter * gene.Rigidity * (0.5 + (0.5 * gene.Toughness));
            if (!gene.IsCore)
                weakest = Math.Min(weakest, gene.Toughness * (0.5 + (0.5 * gene.Rigidity)));

            bool hasMatterLink = gene.IsCore || bodies.ContainsKey(gene.MatterSourceRegionId);
            bool hasSignalLink = gene.IsCore || bodies.ContainsKey(gene.SignalSourceRegionId);
            if (hasMatterLink)
                matterLinks++;
            if (hasSignalLink)
                signalLinks++;
            double matterPath = hasMatterLink ? 1.0 : 0.25;
            double signalPath = hasSignalLink ? 1.0 : 0.25;
            light += surface * gene.LightReactivity * (1.0 - (0.35 * gene.Pigment));
            uptake += surface * gene.Permeability * (0.5 + (0.5 * gene.CatalyticActivity)) * matterPath;
            catalysis += surface * gene.CatalyticActivity * gene.Permeability;
            storage += sample.Body.Matter * gene.StorageFraction * 1.5 * matterPath;
            maintenance += sample.Body.Matter *
                (0.10 + (0.08 * gene.Rigidity) + (0.08 * gene.Contractility) +
                 (0.05 * gene.SignalConductivity) + (0.04 * gene.CatalyticActivity));
            activation += sample.Body.Matter *
                ((0.55 * gene.Contractility) + (0.20 * gene.SignalConductivity) +
                 (0.18 * gene.CatalyticActivity)) * signalPath;
            radius = Math.Max(radius,
                Vector2.Distance(sample.Center, centerOfMass) +
                (0.5 * Math.Sqrt((sample.Length * sample.Length) + (sample.Width * sample.Width))));
        }

        int count = Math.Max(1, samples.Count);
        return new BodyCache(
            totalMatter,
            physicalMass,
            centerOfMass,
            inertia,
            exposedSurface,
            drag,
            buoyancy,
            physicalMass > 0.0 ? stiffness / physicalMass : 0.0,
            samples.Count > 1 ? weakest : 1.0,
            light,
            uptake,
            catalysis,
            storage,
            maintenance,
            activation,
            radius,
            matterLinks / (double)count,
            signalLinks / (double)count);
    }

    public static IReadOnlyList<BodyVisualRegion> BuildVisualRegions(
        Genome genome,
        IReadOnlyList<BodyRegion> bodyRegions)
        => BuildVisualRegions(BodyGeometryBuilder.Build(genome, bodyRegions));

    public static IReadOnlyList<BodyVisualRegion> BuildVisualRegions(BodyGeometry geometry)
        => Array.AsReadOnly(geometry.Regions.Select(region => new BodyVisualRegion(
            region.RegionId,
            region.Center,
            region.Angle,
            region.Length,
            region.StartRadius + region.EndRadius,
            (region.StartRadius + region.EndRadius) * region.VerticalScale,
            region.Color)).ToArray());

    public static IReadOnlyList<BodyFunctionalGeometry> BuildFunctionalGeometry(
        Genome genome,
        IReadOnlyList<BodyRegion> bodyRegions,
        Vector2 centerOfMass)
        => BuildFunctionalGeometry(genome, bodyRegions, centerOfMass,
            BodyGeometryBuilder.Build(genome, bodyRegions));

    public static IReadOnlyList<BodyFunctionalGeometry> BuildFunctionalGeometry(
        Genome genome,
        IReadOnlyList<BodyRegion> bodyRegions,
        Vector2 centerOfMass,
        BodyGeometry geometry)
    {
        IReadOnlyList<BodyVisualRegion> visuals = BuildVisualRegions(geometry);
        Dictionary<int, RegionGene> genes = genome.Regions.ToDictionary(gene => gene.RegionId);
        Dictionary<int, BodyVisualRegion> byId = visuals.ToDictionary(region => region.RegionId);
        Dictionary<int, double> signalPaths = [];
        Dictionary<int, double> matterPaths = [];

        double PathEfficiency(
            int regionId,
            Func<RegionGene, int> source,
            Func<RegionGene, double> conductivity,
            Dictionary<int, double> cache)
        {
            if (cache.TryGetValue(regionId, out double value))
                return value;
            RegionGene gene = genes[regionId];
            value = gene.IsCore
                ? 1.0
                : PathEfficiency(source(gene), source, conductivity, cache) * conductivity(gene);
            cache[regionId] = value;
            return value;
        }

        List<BodyFunctionalGeometry> result = new(visuals.Count);
        foreach (BodyVisualRegion visual in visuals)
        {
            RegionGene gene = genes[visual.RegionId];
            const int sampleCount = 16;
            double perimeterSurface = 2.0 * (visual.Length + visual.Width);
            double thickness = Math.Max(0.08, visual.Thickness);
            Vector2 forward = new((float)Math.Cos(visual.Angle), (float)Math.Sin(visual.Angle));
            Vector2 side = new(-forward.Y, forward.X);
            List<BodySurfaceSample> surfaceSamples = new(sampleCount);
            int exposedSamples = 0;
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                double vertical = 1.0 - (2.0 * (sampleIndex + 0.5) / sampleCount);
                double radial = Math.Sqrt(Math.Max(0.0, 1.0 - (vertical * vertical)));
                double azimuth = sampleIndex * Math.PI * (3.0 - Math.Sqrt(5.0));
                double along = Math.Cos(azimuth) * radial;
                double across = Math.Sin(azimuth) * radial;
                Vector2 planar = visual.LocalCenter +
                    (forward * (float)(along * visual.Length * 0.5)) +
                    (side * (float)(across * visual.Width * 0.5));
                Vector3 samplePosition = new(planar.X, (float)(vertical * thickness * 0.5), planar.Y);
                bool occluded = false;
                foreach (BodyVisualRegion other in visuals)
                {
                    if (other.RegionId == visual.RegionId)
                        continue;
                    Vector2 otherForward = new((float)Math.Cos(other.Angle), (float)Math.Sin(other.Angle));
                    Vector2 otherSide = new(-otherForward.Y, otherForward.X);
                    Vector2 delta = planar - other.LocalCenter;
                    double normalized =
                        Math.Pow(Vector2.Dot(delta, otherForward) / Math.Max(0.04, other.Length * 0.5), 2.0) +
                        Math.Pow(Vector2.Dot(delta, otherSide) / Math.Max(0.04, other.Width * 0.5), 2.0) +
                        Math.Pow(samplePosition.Y / Math.Max(0.04, other.Thickness * 0.5), 2.0);
                    if (normalized < 0.94)
                    {
                        occluded = true;
                        break;
                    }
                }
                if (!occluded)
                    exposedSamples++;
                Vector3 normal = new(
                    (float)((forward.X * along) + (side.X * across)),
                    (float)vertical,
                    (float)((forward.Y * along) + (side.Y * across)));
                surfaceSamples.Add(new BodySurfaceSample(
                    samplePosition,
                    Vector3.Normalize(normal),
                    perimeterSurface / sampleCount,
                    !occluded));
            }
            double exposureFraction = exposedSamples / (double)sampleCount;
            double connectionTransmission = gene.IsCore
                ? 1.0
                : Math.Clamp(
                    gene.Rigidity * (0.35 + (0.65 * gene.Toughness)) *
                    PathEfficiency(gene.ParentRegionId, g => g.ParentRegionId,
                        g => 0.35 + (0.65 * g.Rigidity), new Dictionary<int, double>()),
                    0.0,
                    1.0);
            result.Add(new BodyFunctionalGeometry(
                visual.RegionId,
                visual.LocalCenter - centerOfMass,
                new Vector2((float)Math.Cos(visual.Angle), (float)Math.Sin(visual.Angle)),
                perimeterSurface * exposureFraction,
                exposureFraction,
                PathEfficiency(visual.RegionId, g => g.SignalSourceRegionId,
                    g => g.SignalConductivity, signalPaths),
                PathEfficiency(visual.RegionId, g => g.MatterSourceRegionId,
                    g => g.Permeability, matterPaths),
                connectionTransmission,
                Math.Max(0.08, visual.Thickness * 0.5),
                surfaceSamples.AsReadOnly()));
        }
        return result.AsReadOnly();
    }

    public static double MaximumDifference(BodyCache left, BodyCache right)
    {
        double[] differences =
        [
            Math.Abs(left.TotalMatter - right.TotalMatter),
            Math.Abs(left.PhysicalMass - right.PhysicalMass),
            Vector2.Distance(left.CenterOfMass, right.CenterOfMass),
            Math.Abs(left.RotationalInertia - right.RotationalInertia),
            Math.Abs(left.ExposedSurface - right.ExposedSurface),
            Math.Abs(left.Drag - right.Drag),
            Math.Abs(left.Buoyancy - right.Buoyancy),
            Math.Abs(left.StructuralStiffness - right.StructuralStiffness),
            Math.Abs(left.WeakestConnection - right.WeakestConnection),
            Math.Abs(left.LightCaptureSurface - right.LightCaptureSurface),
            Math.Abs(left.MatterUptakeSurface - right.MatterUptakeSurface),
            Math.Abs(left.CatalyticSurface - right.CatalyticSurface),
            Math.Abs(left.StorageCapacity - right.StorageCapacity),
            Math.Abs(left.MaintenanceEnergyPerSecond - right.MaintenanceEnergyPerSecond),
            Math.Abs(left.MaximumActivationEnergyPerSecond - right.MaximumActivationEnergyPerSecond),
            Math.Abs(left.BoundingRadius - right.BoundingRadius),
            Math.Abs(left.MatterConnectivity - right.MatterConnectivity),
            Math.Abs(left.SignalConnectivity - right.SignalConnectivity)
        ];
        return differences.Max();
    }

    private readonly record struct ShapeSample(
        RegionGene Gene,
        BodyRegion Body,
        Vector2 Center,
        double Angle,
        double Length,
        double Width);
}
