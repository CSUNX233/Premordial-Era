namespace NativeEpoch.Simulation;

public enum MutationKind
{
    None,
    Point,
    DuplicateRegion,
    DeleteRegion,
    ReconnectRegion
}

public readonly record struct RegionGene(
    int RegionId,
    int ParentRegionId,
    int MatterSourceRegionId,
    int SignalSourceRegionId,
    bool IsCore,
    double AppearanceMaturity,
    double TargetLength,
    double TargetWidth,
    double RelativeAngle,
    double Density,
    double Rigidity,
    double Toughness,
    double Permeability,
    double LightReactivity,
    double CatalyticActivity,
    double Contractility,
    double SignalConductivity,
    double StorageFraction,
    double Pigment)
{
    public bool AllFinite =>
        double.IsFinite(AppearanceMaturity) &&
        double.IsFinite(TargetLength) &&
        double.IsFinite(TargetWidth) &&
        double.IsFinite(RelativeAngle) &&
        double.IsFinite(Density) &&
        double.IsFinite(Rigidity) &&
        double.IsFinite(Toughness) &&
        double.IsFinite(Permeability) &&
        double.IsFinite(LightReactivity) &&
        double.IsFinite(CatalyticActivity) &&
        double.IsFinite(Contractility) &&
        double.IsFinite(SignalConductivity) &&
        double.IsFinite(StorageFraction) &&
        double.IsFinite(Pigment);
}

public sealed class Genome
{
    private readonly RegionGene[] _regions;
    private readonly IReadOnlyList<RegionGene> _readOnlyRegions;

    public Genome(IEnumerable<RegionGene> regions, double mutationRate)
    {
        _regions = regions.ToArray();
        _readOnlyRegions = Array.AsReadOnly(_regions);
        MutationRate = mutationRate;
        GenomeValidator.Validate(this);
        Fingerprint = ComputeFingerprint();
    }

    public IReadOnlyList<RegionGene> Regions => _readOnlyRegions;
    public double MutationRate { get; }
    public ulong Fingerprint { get; }

    public static Genome CreateAncestor() => new(
    [
        new RegionGene(
            0, -1, -1, -1, true,
            0.0, 0.80, 0.65, 0.0,
            0.65, 0.50, 0.60, 0.35, 0.55,
            0.25, 0.20, 0.50, 0.55, 0.45),
        new RegionGene(
            1, 0, 0, 0, false,
            0.20, 1.10, 0.35, -0.60,
            0.40, 0.30, 0.40, 0.75, 0.80,
            0.35, 0.25, 0.45, 0.30, 0.25),
        new RegionGene(
            2, 0, 0, 0, false,
            0.45, 0.70, 0.50, 0.80,
            0.55, 0.65, 0.70, 0.45, 0.40,
            0.75, 0.35, 0.60, 0.65, 0.65)
    ],
    mutationRate: 0.18);

    internal bool ContentEquals(Genome other) =>
        MutationRate.Equals(other.MutationRate) && _regions.SequenceEqual(other._regions);

    private ulong ComputeFingerprint()
    {
        ulong hash = FingerprintHash.Offset;
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(MutationRate)));
        foreach (RegionGene region in _regions)
        {
            FingerprintHash.Add(ref hash, unchecked((ulong)region.RegionId));
            FingerprintHash.Add(ref hash, unchecked((ulong)region.ParentRegionId));
            FingerprintHash.Add(ref hash, unchecked((ulong)region.MatterSourceRegionId));
            FingerprintHash.Add(ref hash, unchecked((ulong)region.SignalSourceRegionId));
            FingerprintHash.Add(ref hash, region.IsCore ? 1UL : 0UL);
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.AppearanceMaturity)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.TargetLength)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.TargetWidth)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.RelativeAngle)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.Density)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.Rigidity)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.Toughness)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.Permeability)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.LightReactivity)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.CatalyticActivity)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.Contractility)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.SignalConductivity)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.StorageFraction)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.Pigment)));
        }

        return hash;
    }
}

public static class GenomeValidator
{
    public const int MaximumRegions = 32;

    public static void Validate(Genome genome)
    {
        IReadOnlyList<RegionGene> regions = genome.Regions;
        if (regions.Count is < 1 or > MaximumRegions)
            throw new InvalidOperationException($"A genome must contain 1-{MaximumRegions} regions.");
        if (!double.IsFinite(genome.MutationRate) || genome.MutationRate is < 0.0 or > 0.5)
            throw new InvalidOperationException("Mutation rate must be finite and within 0-0.5.");

        HashSet<int> ids = [];
        foreach (RegionGene region in regions)
        {
            if (region.RegionId < 0 || !ids.Add(region.RegionId))
                throw new InvalidOperationException("Region IDs must be unique non-negative values.");
            ValidateValues(region);
        }

        RegionGene[] cores = regions.Where(region => region.IsCore).ToArray();
        if (cores.Length != 1)
            throw new InvalidOperationException("A genome must retain exactly one core region.");
        RegionGene core = cores[0];
        if (core.ParentRegionId != -1 || core.MatterSourceRegionId != -1 || core.SignalSourceRegionId != -1)
            throw new InvalidOperationException("The core region must be the root of all connection graphs.");

        foreach (RegionGene region in regions.Where(region => !region.IsCore))
        {
            ValidateReference(ids, region.RegionId, region.ParentRegionId, "geometric");
            ValidateReference(ids, region.RegionId, region.MatterSourceRegionId, "matter");
            ValidateReference(ids, region.RegionId, region.SignalSourceRegionId, "signal");
        }

        EnsureRootedAcyclic(regions, core.RegionId, region => region.ParentRegionId, "geometric");
        EnsureRootedAcyclic(regions, core.RegionId, region => region.MatterSourceRegionId, "matter");
        EnsureRootedAcyclic(regions, core.RegionId, region => region.SignalSourceRegionId, "signal");
    }

    private static void ValidateValues(RegionGene region)
    {
        if (!region.AllFinite ||
            region.AppearanceMaturity is < 0.0 or > 1.0 ||
            region.TargetLength is < 0.1 or > 3.0 ||
            region.TargetWidth is < 0.1 or > 2.0 ||
            region.RelativeAngle < -Math.PI || region.RelativeAngle > Math.PI)
        {
            throw new InvalidOperationException($"Region {region.RegionId} has unsafe geometry or timing values.");
        }

        double[] materialValues =
        [
            region.Density, region.Rigidity, region.Toughness, region.Permeability,
            region.LightReactivity, region.CatalyticActivity, region.Contractility,
            region.SignalConductivity, region.StorageFraction, region.Pigment
        ];
        if (materialValues.Any(value => value is < 0.0 or > 1.0))
            throw new InvalidOperationException($"Region {region.RegionId} has a material value outside 0-1.");
    }

    private static void ValidateReference(HashSet<int> ids, int source, int target, string kind)
    {
        if (target == source || !ids.Contains(target))
            throw new InvalidOperationException($"Region {source} has an invalid {kind} connection.");
    }

    private static void EnsureRootedAcyclic(
        IReadOnlyList<RegionGene> regions,
        int coreId,
        Func<RegionGene, int> selectParent,
        string kind)
    {
        Dictionary<int, RegionGene> byId = regions.ToDictionary(region => region.RegionId);
        foreach (RegionGene start in regions)
        {
            HashSet<int> path = [];
            RegionGene current = start;
            while (current.RegionId != coreId)
            {
                if (!path.Add(current.RegionId))
                    throw new InvalidOperationException($"The {kind} connection graph contains a cycle.");
                int parentId = selectParent(current);
                if (!byId.TryGetValue(parentId, out current))
                    throw new InvalidOperationException($"The {kind} connection graph is not rooted at the core.");
            }
        }
    }
}

public sealed class GenomeRegistry
{
    private readonly List<Genome> _genomes = [];
    private readonly Dictionary<ulong, List<int>> _idsByFingerprint = [];

    public int Count => _genomes.Count;

    public int Register(Genome genome)
    {
        if (_idsByFingerprint.TryGetValue(genome.Fingerprint, out List<int>? candidates))
        {
            foreach (int candidate in candidates)
            {
                if (_genomes[candidate].ContentEquals(genome))
                    return candidate;
            }
        }
        else
        {
            candidates = [];
            _idsByFingerprint.Add(genome.Fingerprint, candidates);
        }

        int id = _genomes.Count;
        _genomes.Add(genome);
        candidates.Add(id);
        return id;
    }

    public Genome Get(int genomeId) =>
        genomeId >= 0 && genomeId < _genomes.Count
            ? _genomes[genomeId]
            : throw new ArgumentOutOfRangeException(nameof(genomeId));
}

public readonly record struct MutationResult(Genome Genome, MutationKind Kind, string Summary);

public sealed class GenomeMutator
{
    public MutationResult Inherit(Genome parent, DeterministicRandom random)
    {
        if (random.NextUnitDouble() >= parent.MutationRate)
            return new MutationResult(parent, MutationKind.None, "exact inheritance");

        double choice = random.NextUnitDouble();
        MutationKind kind = choice switch
        {
            < 0.70 => MutationKind.Point,
            < 0.80 => MutationKind.DuplicateRegion,
            < 0.90 => MutationKind.DeleteRegion,
            _ => MutationKind.ReconnectRegion
        };
        return MutateForced(parent, random, kind);
    }

    public MutationResult MutateForced(Genome parent, DeterministicRandom random, MutationKind kind)
    {
        if (kind == MutationKind.None)
            return new MutationResult(parent, MutationKind.None, "exact inheritance");

        List<RegionGene> regions = parent.Regions.ToList();
        string summary;
        switch (kind)
        {
            case MutationKind.Point:
                summary = PointMutation(regions, random);
                break;
            case MutationKind.DuplicateRegion when regions.Count < GenomeValidator.MaximumRegions:
                summary = DuplicateRegion(regions, random);
                break;
            case MutationKind.DeleteRegion when regions.Count > 1:
                summary = DeleteRegion(regions, random);
                break;
            case MutationKind.ReconnectRegion when regions.Count > 2:
                summary = ReconnectRegion(regions, random);
                break;
            default:
                kind = MutationKind.Point;
                summary = PointMutation(regions, random) + " (structural mutation unavailable)";
                break;
        }

        double mutationRate = parent.MutationRate;
        Genome child = new(regions, mutationRate);
        return new MutationResult(child, kind, summary);
    }

    private static string PointMutation(List<RegionGene> regions, DeterministicRandom random)
    {
        int index = random.NextInt(regions.Count);
        RegionGene gene = regions[index];
        int property = random.NextInt(8);
        double delta = (random.NextUnitDouble() - 0.5) * 0.18;
        RegionGene changed = property switch
        {
            0 => gene with { TargetLength = Math.Clamp(gene.TargetLength + delta, 0.1, 3.0) },
            1 => gene with { TargetWidth = Math.Clamp(gene.TargetWidth + delta, 0.1, 2.0) },
            2 => gene with { RelativeAngle = Math.Clamp(gene.RelativeAngle + delta, -Math.PI, Math.PI) },
            3 => gene with { Density = ClampUnit(gene.Density + delta) },
            4 => gene with { Permeability = ClampUnit(gene.Permeability + delta) },
            5 => gene with { LightReactivity = ClampUnit(gene.LightReactivity + delta) },
            6 => gene with { CatalyticActivity = ClampUnit(gene.CatalyticActivity + delta) },
            _ => gene with { StorageFraction = ClampUnit(gene.StorageFraction + delta) }
        };
        regions[index] = changed;
        return $"region {gene.RegionId} continuous property {property} changed by {delta:+0.000;-0.000}";
    }

    private static string DuplicateRegion(List<RegionGene> regions, DeterministicRandom random)
    {
        RegionGene source = regions[random.NextInt(regions.Count)];
        int newId = regions.Max(region => region.RegionId) + 1;
        int parentId = source.IsCore ? source.RegionId : source.ParentRegionId;
        RegionGene duplicate = source with
        {
            RegionId = newId,
            ParentRegionId = parentId,
            MatterSourceRegionId = source.IsCore ? source.RegionId : source.MatterSourceRegionId,
            SignalSourceRegionId = source.IsCore ? source.RegionId : source.SignalSourceRegionId,
            IsCore = false,
            AppearanceMaturity = Math.Clamp(source.AppearanceMaturity + 0.05, 0.0, 0.95),
            RelativeAngle = Math.Clamp(-source.RelativeAngle + 0.08, -Math.PI, Math.PI)
        };
        regions.Add(duplicate);
        return $"region {source.RegionId} copied to {newId}";
    }

    private static string DeleteRegion(List<RegionGene> regions, DeterministicRandom random)
    {
        RegionGene[] removable = regions.Where(region => !region.IsCore).ToArray();
        RegionGene removed = removable[random.NextInt(removable.Length)];
        RegionGene replacement = regions.Single(region => region.RegionId == removed.ParentRegionId);
        regions.RemoveAll(region => region.RegionId == removed.RegionId);

        for (int index = 0; index < regions.Count; index++)
        {
            RegionGene region = regions[index];
            regions[index] = region with
            {
                ParentRegionId = region.ParentRegionId == removed.RegionId
                    ? replacement.RegionId : region.ParentRegionId,
                MatterSourceRegionId = region.MatterSourceRegionId == removed.RegionId
                    ? removed.MatterSourceRegionId : region.MatterSourceRegionId,
                SignalSourceRegionId = region.SignalSourceRegionId == removed.RegionId
                    ? removed.SignalSourceRegionId : region.SignalSourceRegionId
            };
        }

        return $"non-core region {removed.RegionId} deleted";
    }

    private static string ReconnectRegion(List<RegionGene> regions, DeterministicRandom random)
    {
        RegionGene[] nonCore = regions.Where(region => !region.IsCore).ToArray();
        RegionGene source = nonCore[random.NextInt(nonCore.Length)];
        int sourceIndex = regions.FindIndex(region => region.RegionId == source.RegionId);
        int graph = random.NextInt(3);
        Func<RegionGene, int> selector = graph switch
        {
            0 => region => region.ParentRegionId,
            1 => region => region.MatterSourceRegionId,
            _ => region => region.SignalSourceRegionId
        };
        int oldTarget = selector(source);
        HashSet<int> descendants = FindDescendants(regions, source.RegionId, selector);
        int[] targets = regions
            .Select(region => region.RegionId)
            .Where(id => id != source.RegionId && id != oldTarget && !descendants.Contains(id))
            .ToArray();
        if (targets.Length == 0)
            return PointMutation(regions, random) + " (no safe alternate connection)";

        int newTarget = targets[random.NextInt(targets.Length)];
        regions[sourceIndex] = graph switch
        {
            0 => source with { ParentRegionId = newTarget },
            1 => source with { MatterSourceRegionId = newTarget },
            _ => source with { SignalSourceRegionId = newTarget }
        };
        string graphName = graph switch { 0 => "geometric", 1 => "matter", _ => "signal" };
        return $"region {source.RegionId} {graphName} connection {oldTarget}->{newTarget}";
    }

    private static HashSet<int> FindDescendants(
        List<RegionGene> regions,
        int sourceId,
        Func<RegionGene, int> selector)
    {
        HashSet<int> descendants = [];
        bool changed;
        do
        {
            changed = false;
            foreach (RegionGene region in regions)
            {
                if ((selector(region) == sourceId || descendants.Contains(selector(region))) &&
                    descendants.Add(region.RegionId))
                {
                    changed = true;
                }
            }
        }
        while (changed);
        return descendants;
    }

    private static double ClampUnit(double value) => Math.Clamp(value, 0.0, 1.0);
}

internal static class FingerprintHash
{
    public const ulong Offset = 14695981039346656037UL;

    public static void Add(ref ulong hash, ulong value)
    {
        for (int index = 0; index < sizeof(ulong); index++)
        {
            hash ^= (byte)value;
            hash *= 1099511628211UL;
            value >>= 8;
        }
    }
}
