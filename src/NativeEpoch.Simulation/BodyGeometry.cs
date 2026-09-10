using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct BodyGeometryRegion(
    int RegionId,
    int ParentRegionId,
    Vector2 Start,
    Vector2 End,
    double Angle,
    double Length,
    double StartRadius,
    double EndRadius,
    double VerticalScale,
    double Curvature,
    double Roundness,
    double AnalyticVolume,
    Vector3 Color)
{
    public Vector2 Center => (Start + End) * 0.5f;
    public bool IsFinite => RegionId >= 0 &&
        float.IsFinite(Start.X) && float.IsFinite(Start.Y) &&
        float.IsFinite(End.X) && float.IsFinite(End.Y) &&
        double.IsFinite(Length) && Length >= 0 &&
        double.IsFinite(StartRadius) && StartRadius > 0 &&
        double.IsFinite(EndRadius) && EndRadius > 0 &&
        double.IsFinite(VerticalScale) && VerticalScale > 0 &&
        double.IsFinite(AnalyticVolume) && AnalyticVolume > 0;
}

public sealed record BodyGeometry(
    IReadOnlyList<BodyGeometryRegion> Regions,
    double ExpectedVolume,
    double AnalyticVolume,
    bool Connected,
    bool Finite,
    ulong GeometryKey)
{
    public double RelativeVolumeError => Math.Abs(AnalyticVolume - ExpectedVolume) /
        Math.Max(1e-12, ExpectedVolume);
}

/// <summary>
/// Pure simulation geometry shared by mechanics and rendering.  Matter and
/// material density determine volume; shape genes only determine axis ratios.
/// </summary>
public static class BodyGeometryBuilder
{
    public static BodyGeometry Build(Genome genome, IReadOnlyList<BodyRegion> bodyRegions)
    {
        Dictionary<int, RegionGene> genes = genome.Regions.ToDictionary(g => g.RegionId);
        Dictionary<int, BodyGeometryRegion> built = [];
        List<BodyRegion> pending = bodyRegions.OrderBy(r => r.RegionId).ToList();
        double expected = 0;
        ulong key = FingerprintHash.Offset;

        while (pending.Count > 0)
        {
            int index = pending.FindIndex(r => genes[r.RegionId].IsCore || built.ContainsKey(genes[r.RegionId].ParentRegionId));
            if (index < 0)
                throw new InvalidOperationException("Developed body geometry must be rooted at the core.");
            BodyRegion body = pending[index];
            pending.RemoveAt(index);
            RegionGene gene = genes[body.RegionId];

            double bulkDensity = 0.75 + (0.50 * gene.Density);
            double volume = body.Matter / bulkDensity;
            expected += volume;
            double targetAspect = Math.Max(0.12, gene.TargetLength / gene.TargetWidth);
            double verticalAspect = gene.CrossSectionAspect;
            // Ellipsoid equivalent. This normalization makes shape volume equal
            // paid matter / bulk density at every developmental size.
            double unit = (4.0 / 3.0) * Math.PI * targetAspect * verticalAspect;
            double radius = Math.Cbrt(volume / Math.Max(1e-12, unit));
            double majorSemiAxis = radius * targetAspect;
            double startRadius = radius;
            double endRadius = radius * (1.0 - (0.72 * gene.Taper));
            // Renormalize the tapered ellipsoid/frustum approximation exactly.
            double taperFactor = (1.0 + (endRadius / startRadius) + Math.Pow(endRadius / startRadius, 2.0)) / 3.0;
            double correction = Math.Cbrt(1.0 / Math.Max(0.15, taperFactor));
            majorSemiAxis *= correction;
            startRadius *= correction;
            endRadius *= correction;
            double length = Math.Max(0.0, 2.0 * (majorSemiAxis - ((startRadius + endRadius) * 0.5)));

            double angle = gene.RelativeAngle;
            Vector2 start = Vector2.Zero;
            if (!gene.IsCore)
            {
                BodyGeometryRegion parent = built[gene.ParentRegionId];
                angle += parent.Angle;
                start = parent.End;
            }
            Vector2 direction = new((float)Math.Cos(angle), (float)Math.Sin(angle));
            Vector2 bend = new(-direction.Y, direction.X);
            Vector2 end = gene.IsCore
                ? direction * (float)(length * 0.5)
                : start + direction * (float)length;
            if (gene.IsCore)
                start = -direction * (float)(length * 0.5);
            // Curvature moves only the centreline endpoint; attachment remains exact.
            end += bend * (float)(gene.Curvature * length * 0.22);
            if(Vector2.DistanceSquared(start,end)>1e-12f)
                angle=Math.Atan2(end.Y-start.Y,end.X-start.X);
            double analytic = volume;
            Vector3 color = GeneColor(gene);
            BodyGeometryRegion region = new(gene.RegionId, gene.ParentRegionId, start, end, angle,
                Vector2.Distance(start, end), startRadius, endRadius, verticalAspect,
                gene.Curvature, gene.Roundness, analytic, color);
            built.Add(gene.RegionId, region);
            FingerprintHash.Add(ref key, unchecked((ulong)gene.RegionId));
            FingerprintHash.Add(ref key, unchecked((ulong)gene.ParentRegionId));
            FingerprintHash.Add(ref key,BitConverter.DoubleToUInt64Bits(region.Angle));
            FingerprintHash.Add(ref key,BitConverter.DoubleToUInt64Bits(region.Length));
            FingerprintHash.Add(ref key,BitConverter.DoubleToUInt64Bits(region.StartRadius));
            FingerprintHash.Add(ref key,BitConverter.DoubleToUInt64Bits(region.EndRadius));
            FingerprintHash.Add(ref key,BitConverter.DoubleToUInt64Bits(region.VerticalScale));
            FingerprintHash.Add(ref key,BitConverter.DoubleToUInt64Bits(region.Curvature));
            FingerprintHash.Add(ref key,BitConverter.DoubleToUInt64Bits(region.Roundness));
            FingerprintHash.Add(ref key,BitConverter.DoubleToUInt64Bits(region.AnalyticVolume));
            FingerprintHash.Add(ref key,BitConverter.SingleToUInt32Bits(region.Start.X));
            FingerprintHash.Add(ref key,BitConverter.SingleToUInt32Bits(region.Start.Y));
            FingerprintHash.Add(ref key,BitConverter.SingleToUInt32Bits(region.End.X));
            FingerprintHash.Add(ref key,BitConverter.SingleToUInt32Bits(region.End.Y));
            FingerprintHash.Add(ref key,BitConverter.SingleToUInt32Bits(region.Color.X));
            FingerprintHash.Add(ref key,BitConverter.SingleToUInt32Bits(region.Color.Y));
            FingerprintHash.Add(ref key,BitConverter.SingleToUInt32Bits(region.Color.Z));
            // Runtime skins are topology templates. Exact growth stays in this
            // geometry and is applied through the bone pose, avoiding voxel remeshes.
        }

        BodyGeometryRegion[] regions = built.Values.OrderBy(r => r.RegionId).ToArray();
        double actual = regions.Sum(r => r.AnalyticVolume);
        bool connected = regions.All(r => r.ParentRegionId < 0 ||
            built.TryGetValue(r.ParentRegionId, out BodyGeometryRegion parent) &&
            Vector2.Distance(r.Start, parent.End) <= 1e-5f);
        return new BodyGeometry(Array.AsReadOnly(regions), expected, actual,
            connected, connected && regions.All(r => r.IsFinite), key);
    }

    private static Vector3 GeneColor(RegionGene gene)
    {
        // Continuous genetic colour: nearby pigment/material genes stay related,
        // while independently inherited founders remain visually distinguishable.
        double hue=Fraction((0.71*gene.Pigment)+(0.17*gene.LightReactivity)+
            (0.09*gene.Permeability)+(0.037*gene.RegionId));
        double saturation=Math.Clamp(0.48+(0.32*gene.Pigment)+
            (0.12*gene.BarrierExpression),0.42,0.90);
        double value=Math.Clamp(0.56+(0.25*gene.LightReactivity)+
            (0.10*(1.0-gene.Density)),0.48,0.92);
        return HsvToRgb(hue,saturation,value);
    }

    private static Vector3 HsvToRgb(double hue,double saturation,double value)
    {
        double scaled=Fraction(hue)*6.0;
        int sector=(int)Math.Floor(scaled);
        double fraction=scaled-sector;
        double p=value*(1.0-saturation);
        double q=value*(1.0-(saturation*fraction));
        double t=value*(1.0-(saturation*(1.0-fraction)));
        (double r,double g,double b)=sector switch
        {
            0=>(value,t,p),1=>(q,value,p),2=>(p,value,t),
            3=>(p,q,value),4=>(t,p,value),_=>(value,p,q)
        };
        return new Vector3((float)r,(float)g,(float)b);
    }

    private static double Fraction(double value)=>value-Math.Floor(value);
}

public static class MorphologyGenomeFactory
{
    public static IReadOnlyList<(string Name, Genome Genome)> BuildSixDiagnostics()
    {
        Genome ancestor = Genome.CreateAncestor();
        RegionGene template = ancestor.Regions[0];
        Genome One(double length, double width, double aspect, double taper, double curve, double round) =>
            new([template with { TargetLength = length, TargetWidth = width, CrossSectionAspect = aspect,
                Taper = taper, Curvature = curve, Roundness = round }], 0.0,
                ancestor.Metabolism, ancestor.ControllerNodes);
        Genome Branch() => new([
            template with { TargetLength = 1.5, TargetWidth = 0.75, CrossSectionAspect = 0.75, Taper = 0.12 },
            ancestor.Regions[1] with { ParentRegionId = 0, MatterSourceRegionId = 0, SignalSourceRegionId = 0,
                TargetLength = 1.5, TargetWidth = 0.42, RelativeAngle = 0.0, Taper = 0.60 },
            ancestor.Regions[2] with { ParentRegionId = 0, MatterSourceRegionId = 0, SignalSourceRegionId = 0,
                TargetLength = 1.35, TargetWidth = 0.40, RelativeAngle = 1.05, Taper = 0.62 },
            ancestor.Regions[2] with { RegionId = 3, ParentRegionId = 0, MatterSourceRegionId = 0, SignalSourceRegionId = 0,
                TargetLength = 1.30, TargetWidth = 0.38, RelativeAngle = -1.05, Taper = 0.65 }
        ], 0.0, ancestor.Metabolism, ancestor.ControllerNodes);
        return [
            ("球形", One(0.65, 0.65, 1.0, 0.0, 0.0, 1.0)),
            ("卵圆", One(1.25, 0.75, 0.88, 0.08, 0.0, 0.92)),
            ("渐细杆", One(2.8, 0.48, 0.82, 0.72, 0.0, 0.78)),
            ("弯曲丝", One(3.0, 0.38, 0.70, 0.25, 0.72, 0.68)),
            ("扁平体", One(1.45, 1.05, 0.20, 0.10, 0.0, 0.76)),
            ("三分枝", Branch())
        ];
    }

    public static DevelopingBody FullyDeveloped(Genome genome)
    {
        BodyRegion[] regions = genome.Regions.Select(g => new BodyRegion(
            g.RegionId, BodyCalculator.TargetMatter(g), 1.0)).ToArray();
        return new DevelopingBody(genome, regions);
    }
}
