using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct BodyRegion(int RegionId, double Matter, double Development)
{
    public bool AllFinite =>
        RegionId >= 0 && double.IsFinite(Matter) && Matter > 0.0 &&
        double.IsFinite(Development) && Development is >= 0.0 and <= 1.0;
}

public readonly record struct BodyVisualRegion(
    int RegionId,
    Vector2 LocalCenter,
    double Angle,
    double Length,
    double Width,
    double Thickness,
    Vector3 Color);

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
    Vector2 PropulsionVector,
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
        float.IsFinite(PropulsionVector.X) &&
        float.IsFinite(PropulsionVector.Y) &&
        double.IsFinite(BoundingRadius) &&
        double.IsFinite(MatterConnectivity) &&
        double.IsFinite(SignalConnectivity);
}

public sealed class DevelopingBody
{
    private readonly List<BodyRegion> _regions = [];

    public DevelopingBody(Genome genome, double coreMatter)
    {
        RegionGene core = genome.Regions.Single(region => region.IsCore);
        double target = BodyCalculator.TargetMatter(core);
        if (!double.IsFinite(coreMatter) || coreMatter <= 0.0 || coreMatter > target)
            throw new ArgumentOutOfRangeException(nameof(coreMatter));
        _regions.Add(new BodyRegion(core.RegionId, coreMatter, coreMatter / target));
        Cache = BodyCalculator.Recalculate(genome, _regions);
    }

    public IReadOnlyList<BodyRegion> Regions => _regions;
    public BodyCache Cache { get; private set; }
    public int RegionCount => _regions.Count;

    public double Grow(
        Genome genome,
        double maturity,
        ref double storedMatter,
        ref double energy,
        SimulationConfig config)
    {
        double remainingGrowth = config.GrowthMatterPerSecond * config.FixedDeltaSeconds;
        double totalGrowth = 0.0;

        foreach (RegionGene gene in genome.Regions)
        {
            if (remainingGrowth <= 0.0 || storedMatter <= 0.0 || energy <= 0.0)
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

            double affordableByEnergy = energy / config.GrowthEnergyPerMatter;
            double amount = Math.Min(needed, Math.Min(remainingGrowth, Math.Min(storedMatter, affordableByEnergy)));
            if (amount <= 0.0)
                continue;

            storedMatter -= amount;
            energy -= amount * config.GrowthEnergyPerMatter;
            remainingGrowth -= amount;
            totalGrowth += amount;
            double newMatter = currentMatter + amount;
            BodyRegion updated = new(gene.RegionId, newMatter, Math.Clamp(newMatter / BodyCalculator.TargetMatter(gene), 0.0, 1.0));
            if (bodyIndex >= 0)
                _regions[bodyIndex] = updated;
            else
                _regions.Add(updated);
        }

        if (totalGrowth > 0.0)
            Cache = BodyCalculator.Recalculate(genome, _regions);
        return totalGrowth;
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
        Vector2 propulsion = Vector2.Zero;
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
            Vector2 materialDirection = new(
                (float)Math.Cos(sample.Angle),
                (float)Math.Sin(sample.Angle));
            propulsion += materialDirection * (float)(
                sample.Body.Matter * gene.Contractility *
                (0.35 + (0.65 * gene.Rigidity)) * signalPath);
            radius = Math.Max(radius,
                Vector2.Distance(sample.Center, centerOfMass) +
                (0.5 * Math.Sqrt((sample.Length * sample.Length) + (sample.Width * sample.Width))));
        }

        int count = Math.Max(1, samples.Count);
        double propulsionDivisor = Math.Max(0.25, physicalMass + (drag * 0.08));
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
            propulsion / (float)propulsionDivisor,
            radius,
            matterLinks / (double)count,
            signalLinks / (double)count);
    }

    public static IReadOnlyList<BodyVisualRegion> BuildVisualRegions(
        Genome genome,
        IReadOnlyList<BodyRegion> bodyRegions)
    {
        Dictionary<int, RegionGene> genes = genome.Regions.ToDictionary(gene => gene.RegionId);
        Dictionary<int, Vector2> centers = [];
        Dictionary<int, double> angles = [];
        List<BodyVisualRegion> result = new(bodyRegions.Count);
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
            if (!gene.IsCore)
            {
                parentCenter = centers[gene.ParentRegionId];
                angle += angles[gene.ParentRegionId];
            }

            Vector2 direction = new((float)Math.Cos(angle), (float)Math.Sin(angle));
            Vector2 center = gene.IsCore
                ? Vector2.Zero
                : parentCenter + (direction * (float)(length * 0.5));
            centers[gene.RegionId] = center;
            angles[gene.RegionId] = angle;

            Vector3 color = new(
                (float)(0.18 + (0.68 * gene.Pigment)),
                (float)(0.20 + (0.65 * gene.LightReactivity)),
                (float)(0.22 + (0.58 * gene.Permeability)));
            result.Add(new BodyVisualRegion(
                gene.RegionId,
                center,
                angle,
                length,
                width,
                Math.Max(0.08, width * (0.30 + (0.45 * gene.Density))),
                color));
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
            Vector2.Distance(left.PropulsionVector, right.PropulsionVector),
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
