namespace NativeEpoch.Simulation;

public enum MutationKind
{
    None,
    Point,
    DuplicateRegion,
    DeleteRegion,
    ReconnectRegion,
    MetabolicPoint,
    ControllerPoint,
    ControllerAddNode,
    ControllerDeleteNode,
    ControllerReconnect,
    SensorPoint,
    SensorReconnect
}

// These are bounded local physical/chemical signals, not organs or behaviours.
public enum SensorChannel
{
    ChemicalResource,
    ContactPressure,
    InternalEnergy,
    Hydration,
    AmbientLight,
    Temperature,
    Pressure,
    DirectionalLight,
    OrganismContrast
}

public readonly record struct SensorGene(
    SensorChannel Channel,
    int SourceRegionId,
    int TargetControllerNodeIndex,
    double Gain,
    double ResponseRate,
    double DirectionOffsetRadians = 0.0,
    double Range = 2.0,
    double DirectionalSelectivity = 0.5,
    double ApproachWeight = 0.0,
    double AvoidanceWeight = 0.5,
    double TargetMemorySeconds = 1.5)
{
    public bool AllFinite => double.IsFinite(Gain) && double.IsFinite(ResponseRate) &&
        double.IsFinite(DirectionOffsetRadians) && double.IsFinite(Range) &&
        double.IsFinite(DirectionalSelectivity) && double.IsFinite(ApproachWeight) &&
        double.IsFinite(AvoidanceWeight) && double.IsFinite(TargetMemorySeconds);
}

public readonly record struct MetabolicGene(
    double OxygenUseFraction,
    double OxygenCatalysis,
    double WaterRetention,
    double OsmoticTolerance,
    double AnimalFoodAffinity = 0.0,
    double AttackAffinity = 0.0,
    double RetaliationAffinity = 0.0)
{
    public static MetabolicGene AquaticAncestor => new(0.35, 0.48, 0.20, 0.72);

    public bool AllFinite =>
        double.IsFinite(OxygenUseFraction) &&
        double.IsFinite(OxygenCatalysis) &&
        double.IsFinite(WaterRetention) &&
        double.IsFinite(OsmoticTolerance) &&
        double.IsFinite(AnimalFoodAffinity) &&
        double.IsFinite(AttackAffinity) &&
        double.IsFinite(RetaliationAffinity);
}

public readonly record struct ControllerNodeGene(
    double Bias,
    double LightWeight,
    double TemperatureWeight,
    double PressureWeight,
    double EnergyWeight,
    double MatterWeight,
    double HydrationWeight,
    double ContactWeight,
    double SelfMemoryWeight,
    int RecurrentSourceIndex,
    double RecurrentWeight,
    double ContractionOutputWeight,
    double PermeabilityOutputWeight,
    double SecretionOutputWeight,
    double VerticalContractionOutputWeight,
    double LateralContractionOutputWeight)
{
    public bool AllFinite =>
        double.IsFinite(Bias) &&
        double.IsFinite(LightWeight) &&
        double.IsFinite(TemperatureWeight) &&
        double.IsFinite(PressureWeight) &&
        double.IsFinite(EnergyWeight) &&
        double.IsFinite(MatterWeight) &&
        double.IsFinite(HydrationWeight) &&
        double.IsFinite(ContactWeight) &&
        double.IsFinite(SelfMemoryWeight) &&
        double.IsFinite(RecurrentWeight) &&
        double.IsFinite(ContractionOutputWeight) &&
        double.IsFinite(PermeabilityOutputWeight) &&
        double.IsFinite(SecretionOutputWeight) &&
        double.IsFinite(VerticalContractionOutputWeight) &&
        double.IsFinite(LateralContractionOutputWeight);
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
    double CrossSectionAspect,
    double Taper,
    double Curvature,
    double Roundness,
    double Density,
    double Rigidity,
    double Toughness,
    double Permeability,
    double LightReactivity,
    double CatalyticActivity,
    double Contractility,
    double SignalConductivity,
    double StorageFraction,
    double Pigment,
    double ExchangeExpression = 0.5,
    double BarrierExpression = 0.3,
    double ContractileExpression = 0.5,
    double StructuralExpression = 0.5,
    double SensoryExpression = 0.3,
    double CavityFraction = 0.0,
    double CavityAperture = 0.0,
    double JointRestPitch = 0.0,
    double JointMobility = 0.0,
    double PhotosyntheticExpression = 0.0,
    double FeedingExpression = 0.0,
    double DigestiveExpression = 0.0,
    double DecomposerExpression = 0.0)
{
    public bool AllFinite =>
        double.IsFinite(AppearanceMaturity) &&
        double.IsFinite(TargetLength) &&
        double.IsFinite(TargetWidth) &&
        double.IsFinite(RelativeAngle) &&
        double.IsFinite(CrossSectionAspect) &&
        double.IsFinite(Taper) &&
        double.IsFinite(Curvature) &&
        double.IsFinite(Roundness) &&
        double.IsFinite(Density) &&
        double.IsFinite(Rigidity) &&
        double.IsFinite(Toughness) &&
        double.IsFinite(Permeability) &&
        double.IsFinite(LightReactivity) &&
        double.IsFinite(CatalyticActivity) &&
        double.IsFinite(Contractility) &&
        double.IsFinite(SignalConductivity) &&
        double.IsFinite(StorageFraction) &&
        double.IsFinite(Pigment) &&
        double.IsFinite(ExchangeExpression) &&
        double.IsFinite(BarrierExpression) &&
        double.IsFinite(ContractileExpression) &&
        double.IsFinite(StructuralExpression) &&
        double.IsFinite(SensoryExpression) && double.IsFinite(CavityFraction) &&
        double.IsFinite(CavityAperture) && double.IsFinite(JointRestPitch) &&
        double.IsFinite(JointMobility) &&
        double.IsFinite(PhotosyntheticExpression) &&
        double.IsFinite(FeedingExpression) &&
        double.IsFinite(DigestiveExpression) &&
        double.IsFinite(DecomposerExpression);
}

public sealed class Genome
{
    private readonly RegionGene[] _regions;
    private readonly IReadOnlyList<RegionGene> _readOnlyRegions;
    private readonly ControllerNodeGene[] _controllerNodes;
    private readonly IReadOnlyList<ControllerNodeGene> _readOnlyControllerNodes;
    private readonly SensorGene[] _sensors;
    private readonly IReadOnlyList<SensorGene> _readOnlySensors;

    public Genome(
        IEnumerable<RegionGene> regions,
        double mutationRate,
        MetabolicGene? metabolism = null,
        IEnumerable<ControllerNodeGene>? controllerNodes = null,
        IEnumerable<SensorGene>? sensors = null)
    {
        _regions = regions.ToArray();
        _readOnlyRegions = Array.AsReadOnly(_regions);
        _controllerNodes = (controllerNodes ?? []).ToArray();
        _readOnlyControllerNodes = Array.AsReadOnly(_controllerNodes);
        _sensors = (sensors ?? []).ToArray();
        _readOnlySensors = Array.AsReadOnly(_sensors);
        MutationRate = mutationRate;
        Metabolism = metabolism ?? MetabolicGene.AquaticAncestor;
        GenomeValidator.Validate(this);
        Fingerprint = ComputeFingerprint();
    }

    public IReadOnlyList<RegionGene> Regions => _readOnlyRegions;
    public double MutationRate { get; }
    public MetabolicGene Metabolism { get; }
    public IReadOnlyList<ControllerNodeGene> ControllerNodes => _readOnlyControllerNodes;
    public IReadOnlyList<SensorGene> Sensors => _readOnlySensors;
    public ulong Fingerprint { get; }

    public RegionGene GetRegion(int regionId)
    {
        foreach (RegionGene region in _regions)
            if (region.RegionId == regionId)
                return region;
        throw new KeyNotFoundException($"Genome has no region {regionId}.");
    }

    public static Genome CreateAncestor() => new(
    [
        new RegionGene(
            0, -1, -1, -1, true,
            0.0, 0.80, 0.65, 0.0, 0.88, 0.10, 0.06, 0.88,
            0.65, 0.50, 0.60, 0.35, 0.55,
            0.25, 0.20, 0.50, 0.55, 0.45,
            0.62, 0.32, 0.58, 0.48, 0.55) with
            { PhotosyntheticExpression = 0.76, FeedingExpression = 0.10,
                DigestiveExpression = 0.18, DecomposerExpression = 0.0 },
        new RegionGene(
            1, 0, 0, 0, false,
            0.20, 1.10, 0.35, -0.60, 0.72, 0.34, -0.18, 0.72,
            0.40, 0.30, 0.40, 0.75, 0.80,
            0.35, 0.25, 0.45, 0.30, 0.25,
            0.78, 0.18, 0.66, 0.24, 0.72) with
            { PhotosyntheticExpression = 0.84, FeedingExpression = 0.08,
                DigestiveExpression = 0.16, DecomposerExpression = 0.0 },
        new RegionGene(
            2, 0, 0, 0, false,
            0.45, 0.70, 0.50, 0.80, 0.48, 0.22, 0.24, 0.80,
            0.55, 0.65, 0.70, 0.45, 0.40,
            0.75, 0.35, 0.60, 0.65, 0.65,
            0.48, 0.55, 0.42, 0.72, 0.46) with
            { PhotosyntheticExpression = 0.68, FeedingExpression = 0.12,
                DigestiveExpression = 0.20, DecomposerExpression = 0.0 }
    ],
    mutationRate: 0.28,
    metabolism: MetabolicGene.AquaticAncestor,
    controllerNodes:
    [
        new ControllerNodeGene(
            -0.10, 0.75, -0.15, -0.35, 0.40, 0.20, 0.55, -0.25,
            0.52, 2, 0.35, 0.90, 0.30, 0.05, -0.45, 0.18),
        new ControllerNodeGene(
            0.08, -0.25, 0.30, 0.40, -0.10, 0.45, 0.20, 0.50,
            -0.42, 0, 0.65, -0.35, 0.75, 0.22, 0.58, -0.28),
        new ControllerNodeGene(
            -0.02, 0.15, -0.20, 0.65, 0.15, -0.15, -0.45, 0.35,
            0.30, 1, -0.72, 0.40, -0.12, 0.62, 0.35, 0.55)
    ],
    sensors:
    [
        new SensorGene(SensorChannel.ChemicalResource, 1, 0, 1.00, 2.2),
        new SensorGene(SensorChannel.ContactPressure, 0, 1, 0.85, 4.0),
        new SensorGene(SensorChannel.InternalEnergy, 0, 2, 0.70, 1.4),
        new SensorGene(SensorChannel.Hydration, 2, 1, 0.55, 1.8)
    ]);

    internal bool ContentEquals(Genome other) =>
        MutationRate.Equals(other.MutationRate) &&
        Metabolism.Equals(other.Metabolism) &&
        _regions.SequenceEqual(other._regions) &&
        _controllerNodes.SequenceEqual(other._controllerNodes) &&
        _sensors.SequenceEqual(other._sensors);

    private ulong ComputeFingerprint()
    {
        ulong hash = FingerprintHash.Offset;
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(MutationRate)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(Metabolism.OxygenUseFraction)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(Metabolism.OxygenCatalysis)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(Metabolism.WaterRetention)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(Metabolism.OsmoticTolerance)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(Metabolism.AnimalFoodAffinity)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(Metabolism.AttackAffinity)));
        FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(Metabolism.RetaliationAffinity)));
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
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.CrossSectionAspect)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.Taper)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.Curvature)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.Roundness)));
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
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.ExchangeExpression)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.BarrierExpression)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.ContractileExpression)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.StructuralExpression)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.SensoryExpression)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.CavityFraction)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.CavityAperture)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.JointRestPitch)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.JointMobility)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.PhotosyntheticExpression)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.FeedingExpression)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.DigestiveExpression)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(region.DecomposerExpression)));
        }

        FingerprintHash.Add(ref hash, unchecked((ulong)_sensors.Length));
        foreach (SensorGene sensor in _sensors)
        {
            FingerprintHash.Add(ref hash, unchecked((ulong)sensor.Channel));
            FingerprintHash.Add(ref hash, unchecked((ulong)sensor.SourceRegionId));
            FingerprintHash.Add(ref hash, unchecked((ulong)sensor.TargetControllerNodeIndex));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sensor.Gain)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sensor.ResponseRate)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sensor.DirectionOffsetRadians)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sensor.Range)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sensor.DirectionalSelectivity)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sensor.ApproachWeight)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sensor.AvoidanceWeight)));
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(sensor.TargetMemorySeconds)));
        }

        FingerprintHash.Add(ref hash, unchecked((ulong)_controllerNodes.Length));
        foreach (ControllerNodeGene node in _controllerNodes)
        {
            FingerprintHash.Add(ref hash, unchecked((ulong)node.RecurrentSourceIndex));
            double[] values =
            [
                node.Bias, node.LightWeight, node.TemperatureWeight, node.PressureWeight,
                node.EnergyWeight, node.MatterWeight, node.HydrationWeight, node.ContactWeight,
                node.SelfMemoryWeight, node.RecurrentWeight, node.ContractionOutputWeight,
                node.PermeabilityOutputWeight, node.SecretionOutputWeight,
                node.VerticalContractionOutputWeight, node.LateralContractionOutputWeight
            ];
            foreach (double value in values)
                FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
        }

        return hash;
    }
}

public static class GenomeValidator
{
    public const int MaximumRegions = 32;
    public const int MaximumControllerNodes = 12;
    public const int MaximumSensors = 8;

    public static void Validate(Genome genome)
    {
        IReadOnlyList<RegionGene> regions = genome.Regions;
        if (regions.Count is < 1 or > MaximumRegions)
            throw new InvalidOperationException($"A genome must contain 1-{MaximumRegions} regions.");
        if (!double.IsFinite(genome.MutationRate) || genome.MutationRate is < 0.0 or > 0.5)
            throw new InvalidOperationException("Mutation rate must be finite and within 0-0.5.");
        if (!genome.Metabolism.AllFinite ||
            genome.Metabolism.OxygenUseFraction is < 0.0 or > 1.0 ||
            genome.Metabolism.OxygenCatalysis is < 0.0 or > 1.0 ||
            genome.Metabolism.WaterRetention is < 0.0 or > 1.0 ||
            genome.Metabolism.OsmoticTolerance is < 0.0 or > 1.0 ||
            genome.Metabolism.AnimalFoodAffinity is < 0.0 or > 1.0 ||
            genome.Metabolism.AttackAffinity is < 0.0 or > 1.0 ||
            genome.Metabolism.RetaliationAffinity is < 0.0 or > 1.0)
        {
            throw new InvalidOperationException("Metabolic trade-off genes must be finite within 0-1.");
        }
        if (genome.ControllerNodes.Count > MaximumControllerNodes)
            throw new InvalidOperationException($"A controller may contain 0-{MaximumControllerNodes} nodes.");
        for (int index = 0; index < genome.ControllerNodes.Count; index++)
        {
            ControllerNodeGene node = genome.ControllerNodes[index];
            if (!node.AllFinite ||
                node.RecurrentSourceIndex < -1 ||
                node.RecurrentSourceIndex >= genome.ControllerNodes.Count ||
                node.RecurrentSourceIndex == index)
            {
                throw new InvalidOperationException($"Controller node {index} has an invalid value or connection.");
            }
            double[] weights =
            [
                node.Bias, node.LightWeight, node.TemperatureWeight, node.PressureWeight,
                node.EnergyWeight, node.MatterWeight, node.HydrationWeight, node.ContactWeight,
                node.SelfMemoryWeight, node.RecurrentWeight, node.ContractionOutputWeight,
                node.PermeabilityOutputWeight, node.SecretionOutputWeight,
                node.VerticalContractionOutputWeight, node.LateralContractionOutputWeight
            ];
            if (weights.Any(value => value is < -3.0 or > 3.0))
                throw new InvalidOperationException($"Controller node {index} has a weight outside -3 to 3.");
        }

        if (genome.Sensors.Count > MaximumSensors)
            throw new InvalidOperationException($"A genome may contain 0-{MaximumSensors} sensor slots.");
        foreach (SensorGene sensor in genome.Sensors)
        {
            if (!Enum.IsDefined(sensor.Channel) || !sensor.AllFinite ||
                sensor.Gain is < -3.0 or > 3.0 || sensor.ResponseRate is < 0.1 or > 8.0 ||
                sensor.DirectionOffsetRadians < -Math.PI || sensor.DirectionOffsetRadians > Math.PI ||
                sensor.Range is < 0.25 or > 12.0 || sensor.DirectionalSelectivity is < 0.0 or > 1.0 ||
                sensor.ApproachWeight is < -1.0 or > 1.0 ||
                sensor.AvoidanceWeight is < -1.0 or > 1.0 ||
                sensor.TargetMemorySeconds is < 0.0 or > 8.0 ||
                !genome.Regions.Any(region => region.RegionId == sensor.SourceRegionId) ||
                sensor.TargetControllerNodeIndex < 0 ||
                sensor.TargetControllerNodeIndex >= genome.ControllerNodes.Count)
                throw new InvalidOperationException("A sensor slot has an invalid channel, source, target, gain, or rate.");
        }

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
            region.RelativeAngle < -Math.PI || region.RelativeAngle > Math.PI ||
            region.CrossSectionAspect is < 0.15 or > 1.0 ||
            region.Taper is < 0.0 or > 0.85 ||
            region.Curvature is < -1.0 or > 1.0 ||
            region.Roundness is < 0.2 or > 1.0)
        {
            throw new InvalidOperationException($"Region {region.RegionId} has unsafe geometry or timing values.");
        }

        double[] materialValues =
        [
            region.Density, region.Rigidity, region.Toughness, region.Permeability,
            region.LightReactivity, region.CatalyticActivity, region.Contractility,
            region.SignalConductivity, region.StorageFraction, region.Pigment
            , region.ExchangeExpression, region.BarrierExpression, region.ContractileExpression,
            region.StructuralExpression, region.SensoryExpression, region.CavityFraction,
            region.CavityAperture, region.JointMobility, region.PhotosyntheticExpression,
            region.FeedingExpression, region.DigestiveExpression, region.DecomposerExpression
        ];
        if (materialValues.Any(value => value is < 0.0 or > 1.0))
            throw new InvalidOperationException($"Region {region.RegionId} has a material value outside 0-1.");
        if (region.JointRestPitch is < -1.0 or > 1.0)
            throw new InvalidOperationException($"Region {region.RegionId} has joint rest pitch outside -1 to 1.");
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

public readonly record struct MutationResult
{
    public MutationResult(Genome genome, MutationKind kind, string summary)
        : this(genome, kind, summary, kind == MutationKind.None ? 0 : 1,
            kind == MutationKind.None ? Array.Empty<MutationKind>() : new[] { kind })
    {
    }

    public MutationResult(
        Genome genome,
        MutationKind kind,
        string summary,
        int eventCount,
        IReadOnlyList<MutationKind>? eventKinds)
    {
        if (eventCount < 0) throw new ArgumentOutOfRangeException(nameof(eventCount));
        Genome = genome;
        Kind = kind;
        Summary = summary;
        _eventKinds = eventKinds?.ToArray() ?? Array.Empty<MutationKind>();
        if (EventKinds.Count != eventCount)
            throw new ArgumentException("Mutation event metadata count must match its kind list.", nameof(eventKinds));
        EventCount = eventCount;
    }

    public Genome Genome { get; }
    public MutationKind Kind { get; }
    public string Summary { get; }
    public int EventCount { get; }
    private readonly IReadOnlyList<MutationKind>? _eventKinds;
    public IReadOnlyList<MutationKind> EventKinds => _eventKinds ?? Array.Empty<MutationKind>();
}

public sealed class GenomeMutator
{
    public static double NaturalMutationProbability(double mutationRate)
    {
        if (!double.IsFinite(mutationRate) || mutationRate is < 0.0 or > 0.5)
            throw new ArgumentOutOfRangeException(nameof(mutationRate));
        if (mutationRate == 0.0) return 0.0;
        double oldProbability = 1.0 - ((1.0 - mutationRate) * (1.0 - (0.55 * mutationRate)));
        return Math.Min(0.95, oldProbability + 0.05);
    }

    public MutationResult Inherit(
        Genome parent,
        DeterministicRandom bodyRandom,
        DeterministicRandom controllerRandom)
    {
        double mutationProbability = NaturalMutationProbability(parent.MutationRate);
        if (bodyRandom.NextUnitDouble() >= mutationProbability)
            return new MutationResult(parent, MutationKind.None, "exact inheritance");

        double countChoice = bodyRandom.NextUnitDouble();
        int requestedEvents = countChoice < 0.90 ? 1 : countChoice < 0.99 ? 2 : 3;
        Genome result = parent;
        List<MutationKind> eventKinds = new(requestedEvents);
        List<string> summaries = new(requestedEvents);
        for (int eventIndex = 0; eventIndex < requestedEvents; eventIndex++)
        {
            double choice = bodyRandom.NextUnitDouble();
            MutationKind selectedKind = choice switch
            {
                < 0.55 => MutationKind.Point,
                < 0.65 => MutationKind.MetabolicPoint,
                < 0.80 => MutationKind.ControllerPoint,
                < 0.85 => MutationKind.SensorPoint,
                < 0.88 => MutationKind.DuplicateRegion,
                < 0.90 => MutationKind.DeleteRegion,
                < 0.93 => MutationKind.ReconnectRegion,
                < 0.945 => MutationKind.ControllerAddNode,
                < 0.955 => MutationKind.ControllerDeleteNode,
                < 0.970 => MutationKind.ControllerReconnect,
                _ => MutationKind.SensorReconnect
            };
            MutationResult eventResult = selectedKind switch
            {
                MutationKind.ControllerPoint or MutationKind.ControllerAddNode or
                    MutationKind.ControllerDeleteNode or MutationKind.ControllerReconnect =>
                    MutateControllerForced(result, controllerRandom, selectedKind),
                MutationKind.SensorPoint or MutationKind.SensorReconnect =>
                    MutateSensorForced(result, controllerRandom, selectedKind),
                _ => MutateForced(result, bodyRandom, selectedKind)
            };
            if (eventResult.Genome.Fingerprint == result.Fingerprint)
                continue;
            result = eventResult.Genome;
            eventKinds.Add(eventResult.Kind);
            summaries.Add(eventResult.Summary);
        }

        if (eventKinds.Count == 0 || result.Fingerprint == parent.Fingerprint)
            return new MutationResult(parent, MutationKind.None, "exact inheritance; attempted changes had no effect");
        MutationKind kind = eventKinds[^1];
        return new MutationResult(result, kind, string.Join("; ", summaries),
            eventKinds.Count, eventKinds.ToArray());
    }

    public MutationResult MutateForced(Genome parent, DeterministicRandom random, MutationKind kind)
    {
        if (kind == MutationKind.None)
            return new MutationResult(parent, MutationKind.None, "exact inheritance");

        List<RegionGene> regions = parent.Regions.ToList();
        List<SensorGene> sensors = parent.Sensors.ToList();
        MetabolicGene metabolism = parent.Metabolism;
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
                summary = DeleteRegion(regions, sensors, random);
                break;
            case MutationKind.ReconnectRegion when regions.Count > 2:
                string? reconnect = ReconnectRegion(regions, random);
                if (reconnect is null)
                {
                    kind = MutationKind.Point;
                    summary = PointMutation(regions, random) + " (no safe alternate connection)";
                }
                else summary = reconnect;
                break;
            case MutationKind.MetabolicPoint:
                (metabolism, summary) = MutateMetabolism(metabolism, random);
                break;
            default:
                kind = MutationKind.Point;
                summary = PointMutation(regions, random) + " (structural mutation unavailable)";
                break;
        }

        double mutationRate = parent.MutationRate;
        Genome child = new(regions, mutationRate, metabolism, parent.ControllerNodes, sensors);
        if (child.Fingerprint == parent.Fingerprint)
            return new MutationResult(parent, MutationKind.None, "attempted body mutation had no effect");
        return new MutationResult(child, kind, summary);
    }

    public MutationResult MutateControllerForced(
        Genome parent,
        DeterministicRandom random,
        MutationKind kind)
    {
        List<ControllerNodeGene> nodes = parent.ControllerNodes.ToList();
        List<SensorGene> sensors = parent.Sensors.ToList();
        string summary;
        switch (kind)
        {
            case MutationKind.ControllerPoint when nodes.Count > 0:
                summary = MutateControllerPoint(nodes, random);
                break;
            case MutationKind.ControllerAddNode when nodes.Count < GenomeValidator.MaximumControllerNodes:
                summary = AddControllerNode(nodes, random);
                break;
            case MutationKind.ControllerDeleteNode when nodes.Count > 0:
                summary = DeleteControllerNode(nodes, sensors, random);
                break;
            case MutationKind.ControllerReconnect when nodes.Count > 1:
                summary = ReconnectControllerNode(nodes, random);
                break;
            default:
                kind = nodes.Count == 0 ? MutationKind.ControllerAddNode : MutationKind.ControllerPoint;
                summary = nodes.Count == 0
                    ? AddControllerNode(nodes, random)
                    : MutateControllerPoint(nodes, random) + " (requested controller change unavailable)";
                break;
        }

        Genome child = new(parent.Regions, parent.MutationRate, parent.Metabolism, nodes, sensors);
        if (child.Fingerprint == parent.Fingerprint)
            return new MutationResult(parent, MutationKind.None, "attempted controller mutation had no effect");
        return new MutationResult(child, kind, summary);
    }

    public MutationResult MutateSensorForced(Genome parent, DeterministicRandom random, MutationKind kind)
    {
        List<SensorGene> sensors = parent.Sensors.ToList();
        string summary;
        if (sensors.Count == 0)
        {
            if (parent.ControllerNodes.Count == 0)
                return new MutationResult(parent, MutationKind.None, "sensor mutation unavailable without controller nodes");
            RegionGene source = parent.Regions[random.NextInt(parent.Regions.Count)];
            sensors.Add(new SensorGene((SensorChannel)random.NextInt(Enum.GetValues<SensorChannel>().Length), source.RegionId,
                random.NextInt(parent.ControllerNodes.Count), 0.25 + random.NextUnitDouble(),
                0.5 + (3.0 * random.NextUnitDouble()),
                DirectionOffsetRadians: -Math.PI + (Math.Tau * random.NextUnitDouble()),
                Range: 0.25 + (11.75 * random.NextUnitDouble()),
                DirectionalSelectivity: random.NextUnitDouble(),
                ApproachWeight: -1.0 + (2.0 * random.NextUnitDouble()),
                AvoidanceWeight: -1.0 + (2.0 * random.NextUnitDouble()),
                TargetMemorySeconds: 8.0 * random.NextUnitDouble()));
            summary = "sensor slot 0 added";
            kind = MutationKind.SensorReconnect;
        }
        else if (kind == MutationKind.SensorReconnect)
        {
            int index = random.NextInt(sensors.Count);
            SensorGene old = sensors[index];
            int choice = random.NextInt(3);
            if (choice == 0 && parent.Regions.Count > 1)
            {
                RegionGene[] alternatives = parent.Regions
                    .Where(region => region.RegionId != old.SourceRegionId).ToArray();
                int source = alternatives[random.NextInt(alternatives.Length)].RegionId;
                sensors[index] = old with { SourceRegionId = source };
                summary = $"sensor {index} source region {old.SourceRegionId}->{source}";
            }
            else if (choice == 1 && parent.ControllerNodes.Count > 1)
            {
                int target = random.NextInt(parent.ControllerNodes.Count - 1);
                if (target >= old.TargetControllerNodeIndex) target++;
                sensors[index] = old with { TargetControllerNodeIndex = target };
                summary = $"sensor {index} target node {old.TargetControllerNodeIndex}->{target}";
            }
            else
            {
                int channel = random.NextInt(Enum.GetValues<SensorChannel>().Length - 1);
                if (channel >= (int)old.Channel) channel++;
                sensors[index] = old with { Channel = (SensorChannel)channel };
                summary = $"sensor {index} channel {old.Channel}->{sensors[index].Channel}";
            }
        }
        else
        {
            int index = random.NextInt(sensors.Count);
            SensorGene old = sensors[index];
            int property = random.NextInt(8);
            MutationStep step = SampleScalarStep(random);
            sensors[index] = property switch
            {
                0 => old with { Gain = ReflectRange(old.Gain + step.SignedFraction, -3.0, 3.0) },
                1 => old with { ResponseRate = ReflectRange(
                    old.ResponseRate * Math.Exp(step.SignedFraction), 0.1, 8.0) },
                2 => old with { DirectionOffsetRadians = ReflectRange(
                    old.DirectionOffsetRadians + (step.SignedFraction * Math.PI), -Math.PI, Math.PI) },
                3 => old with { Range = ReflectRange(old.Range * Math.Exp(step.SignedFraction), 0.25, 12.0) },
                4 => old with { DirectionalSelectivity = ReflectRange(
                    old.DirectionalSelectivity + step.SignedFraction, 0.0, 1.0) },
                5 => old with { ApproachWeight = ReflectRange(
                    old.ApproachWeight + step.SignedFraction, -1.0, 1.0) },
                6 => old with { AvoidanceWeight = ReflectRange(
                    old.AvoidanceWeight + step.SignedFraction, -1.0, 1.0) },
                _ => old with { TargetMemorySeconds = ReflectRange(
                    old.TargetMemorySeconds + (2.0 * step.SignedFraction), 0.0, 8.0) }
            };
            summary = $"sensor {index} scalar {property}, {(step.Large ? "rare large" : "small")} inherited step";
            kind = MutationKind.SensorPoint;
        }
        Genome child = new(parent.Regions, parent.MutationRate, parent.Metabolism,
            parent.ControllerNodes, sensors);
        if (child.Fingerprint == parent.Fingerprint)
            return new MutationResult(parent, MutationKind.None, "attempted sensor mutation had no effect");
        return new MutationResult(child, kind, summary);
    }

    private static string PointMutation(List<RegionGene> regions, DeterministicRandom random)
    {
        int index = random.NextInt(regions.Count);
        RegionGene gene = regions[index];
        // Keep a morphology budget independent of the number of physiological
        // traits. Mostly local changes, with occasional larger inherited steps;
        // the resulting body still has to pay for its growth and maintenance.
        int property = random.NextUnitDouble() < 0.55
            ? random.NextInt(8) : 8 + random.NextInt(23);
        MutationStep step = SampleScalarStep(random);
        double delta = step.SignedFraction;
        double sizeFactor = Math.Exp(step.SignedFraction);
        double angleDelta = step.SignedFraction * Math.PI;
        RegionGene changed = property switch
        {
            0 => gene with { TargetLength = ReflectRange(gene.TargetLength * sizeFactor, 0.1, 3.0) },
            1 => gene with { TargetWidth = ReflectRange(gene.TargetWidth * sizeFactor, 0.1, 2.0) },
            2 => gene with { RelativeAngle = ReflectRange(gene.RelativeAngle + angleDelta, -Math.PI, Math.PI) },
            3 => gene with { CrossSectionAspect = ReflectRange(gene.CrossSectionAspect + delta, 0.15, 1.0) },
            4 => gene with { Taper = ReflectRange(gene.Taper + delta, 0.0, 0.85) },
            5 => gene with { Curvature = ReflectRange(gene.Curvature + delta, -1.0, 1.0) },
            6 => gene with { Roundness = ReflectRange(gene.Roundness + delta, 0.2, 1.0) },
            7 => gene with { Pigment = ReflectRange(gene.Pigment + delta, 0.0, 1.0) },
            8 => gene with { Density = ReflectRange(gene.Density + delta, 0.0, 1.0) },
            9 => gene with { Rigidity = ReflectRange(gene.Rigidity + delta, 0.0, 1.0) },
            10 => gene with { Toughness = ReflectRange(gene.Toughness + delta, 0.0, 1.0) },
            11 => gene with { Permeability = ReflectRange(gene.Permeability + delta, 0.0, 1.0) },
            12 => gene with { LightReactivity = ReflectRange(gene.LightReactivity + delta, 0.0, 1.0) },
            13 => gene with { CatalyticActivity = ReflectRange(gene.CatalyticActivity + delta, 0.0, 1.0) },
            14 => gene with { Contractility = ReflectRange(gene.Contractility + delta, 0.0, 1.0) },
            15 => gene with { SignalConductivity = ReflectRange(gene.SignalConductivity + delta, 0.0, 1.0) },
            16 => gene with { StorageFraction = ReflectRange(gene.StorageFraction + delta, 0.0, 1.0) },
            17 => gene with { ExchangeExpression = ReflectRange(gene.ExchangeExpression + delta, 0.0, 1.0) },
            18 => gene with { BarrierExpression = ReflectRange(gene.BarrierExpression + delta, 0.0, 1.0) },
            19 => gene with { ContractileExpression = ReflectRange(gene.ContractileExpression + delta, 0.0, 1.0) },
            20 => gene with { StructuralExpression = ReflectRange(gene.StructuralExpression + delta, 0.0, 1.0) },
            21 => gene with { SensoryExpression = ReflectRange(gene.SensoryExpression + delta, 0.0, 1.0) },
            22 => gene with { CavityFraction = ReflectRange(gene.CavityFraction + delta, 0.0, 1.0) },
            23 => gene with { CavityAperture = ReflectRange(gene.CavityAperture + delta, 0.0, 1.0) },
            24 => gene with { JointRestPitch = ReflectRange(gene.JointRestPitch + delta, -1.0, 1.0) },
            25 => gene with { JointMobility = ReflectRange(gene.JointMobility + delta, 0.0, 1.0) },
            26 => gene with { PhotosyntheticExpression = ReflectRange(gene.PhotosyntheticExpression + delta, 0.0, 1.0) },
            27 => gene with { FeedingExpression = ReflectRange(gene.FeedingExpression + delta, 0.0, 1.0) },
            28 => gene with { DigestiveExpression = ReflectRange(gene.DigestiveExpression + delta, 0.0, 1.0) },
            29 => gene with { DecomposerExpression = ReflectRange(gene.DecomposerExpression + delta, 0.0, 1.0) },
            _ when !gene.IsCore => gene with { AppearanceMaturity = ReflectRange(gene.AppearanceMaturity + delta, 0.0, 0.95) },
            _ => gene with { SensoryExpression = ReflectRange(gene.SensoryExpression + delta, 0.0, 1.0) }
        };
        regions[index] = changed;
        return $"region {gene.RegionId} continuous property {property}, {(step.Large ? "rare large" : "small")} inherited step";
    }

    private static MutationStep SampleScalarStep(DeterministicRandom random)
    {
        bool large = random.NextUnitDouble() < 0.015;
        double magnitude = large
            ? 0.12 + (0.28 * random.NextUnitDouble())
            : 0.01 + (0.03 * random.NextUnitDouble());
        double sign = random.NextUnitDouble() < 0.5 ? -1.0 : 1.0;
        return new MutationStep(large, sign * magnitude);
    }

    private readonly record struct MutationStep(bool Large, double SignedFraction);

    private static double ReflectRange(double value, double minimum, double maximum)
    {
        double width = maximum - minimum;
        double offset = ((value - minimum) % (2.0 * width) + 2.0 * width) % (2.0 * width);
        return minimum + (offset <= width ? offset : 2.0 * width - offset);
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

    private static string DeleteRegion(List<RegionGene> regions, List<SensorGene> sensors, DeterministicRandom random)
    {
        RegionGene[] removable = regions.Where(region => !region.IsCore).ToArray();
        RegionGene removed = removable[random.NextInt(removable.Length)];
        RegionGene replacement = regions.Single(region => region.RegionId == removed.ParentRegionId);
        regions.RemoveAll(region => region.RegionId == removed.RegionId);
        for (int index = 0; index < sensors.Count; index++)
            if (sensors[index].SourceRegionId == removed.RegionId)
                sensors[index] = sensors[index] with { SourceRegionId = replacement.RegionId };

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

    private static string? ReconnectRegion(List<RegionGene> regions, DeterministicRandom random)
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
            return null;

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

    private static (MetabolicGene Gene, string Summary) MutateMetabolism(
        MetabolicGene gene,
        DeterministicRandom random)
    {
        int property = random.NextInt(7);
        MutationStep step = SampleScalarStep(random);
        double delta = step.SignedFraction;
        MetabolicGene changed = property switch
        {
            0 => gene with { OxygenUseFraction = ClampUnit(gene.OxygenUseFraction + delta) },
            1 => gene with { OxygenCatalysis = ClampUnit(gene.OxygenCatalysis + delta) },
            2 => gene with { WaterRetention = ClampUnit(gene.WaterRetention + delta) },
            3 => gene with { OsmoticTolerance = ClampUnit(gene.OsmoticTolerance + delta) },
            4 => gene with { AnimalFoodAffinity = ClampUnit(gene.AnimalFoodAffinity + delta) },
            5 => gene with { AttackAffinity = ClampUnit(gene.AttackAffinity + delta) },
            _ => gene with { RetaliationAffinity = ClampUnit(gene.RetaliationAffinity + delta) }
        };
        return (changed, $"metabolic scalar {property}, {(step.Large ? "rare large" : "small")} step {delta:+0.000;-0.000}");
    }

    private static string MutateControllerPoint(
        List<ControllerNodeGene> nodes,
        DeterministicRandom random)
    {
        int index = random.NextInt(nodes.Count);
        ControllerNodeGene node = nodes[index];
        int property = random.NextInt(15);
        MutationStep step = SampleScalarStep(random);
        double delta = step.SignedFraction;
        double Adjust(double value) => ReflectRange(value + delta, -3.0, 3.0);
        nodes[index] = property switch
        {
            0 => node with { Bias = Adjust(node.Bias) },
            1 => node with { LightWeight = Adjust(node.LightWeight) },
            2 => node with { TemperatureWeight = Adjust(node.TemperatureWeight) },
            3 => node with { PressureWeight = Adjust(node.PressureWeight) },
            4 => node with { EnergyWeight = Adjust(node.EnergyWeight) },
            5 => node with { MatterWeight = Adjust(node.MatterWeight) },
            6 => node with { HydrationWeight = Adjust(node.HydrationWeight) },
            7 => node with { ContactWeight = Adjust(node.ContactWeight) },
            8 => node with { SelfMemoryWeight = Adjust(node.SelfMemoryWeight) },
            9 => node with { RecurrentWeight = Adjust(node.RecurrentWeight) },
            10 => node with { ContractionOutputWeight = Adjust(node.ContractionOutputWeight) },
            11 => node with { PermeabilityOutputWeight = Adjust(node.PermeabilityOutputWeight) },
            12 => node with { SecretionOutputWeight = Adjust(node.SecretionOutputWeight) },
            13 => node with { VerticalContractionOutputWeight = Adjust(node.VerticalContractionOutputWeight) },
            _ => node with { LateralContractionOutputWeight = Adjust(node.LateralContractionOutputWeight) }
        };
        return $"controller node {index} scalar {property}, {(step.Large ? "rare large" : "small")} step {delta:+0.000;-0.000}";
    }

    private static string AddControllerNode(
        List<ControllerNodeGene> nodes,
        DeterministicRandom random)
    {
        double Weight(double scale = 1.0) => (random.NextUnitDouble() - 0.5) * 2.0 * scale;
        int source = nodes.Count == 0 ? -1 : random.NextInt(nodes.Count);
        ControllerNodeGene node = new(
            Weight(0.35), Weight(), Weight(), Weight(), Weight(), Weight(), Weight(), Weight(),
            Weight(0.65), source, Weight(0.8), Weight(), Weight(), Weight(0.6), Weight(), Weight());
        nodes.Add(node);
        return $"controller node {nodes.Count - 1} added with recurrent source {source}";
    }

    private static string DeleteControllerNode(
        List<ControllerNodeGene> nodes,
        List<SensorGene> sensors,
        DeterministicRandom random)
    {
        int removed = random.NextInt(nodes.Count);
        nodes.RemoveAt(removed);
        for (int index = 0; index < nodes.Count; index++)
        {
            ControllerNodeGene node = nodes[index];
            int source = node.RecurrentSourceIndex;
            if (source == removed)
                source = -1;
            else if (source > removed)
                source--;
            if (source == index)
                source = -1;
            nodes[index] = node with { RecurrentSourceIndex = source };
        }
        for (int index = sensors.Count - 1; index >= 0; index--)
        {
            int target = sensors[index].TargetControllerNodeIndex;
            if (target == removed)
                sensors.RemoveAt(index);
            else if (target > removed)
                sensors[index] = sensors[index] with { TargetControllerNodeIndex = target - 1 };
        }
        return $"controller node {removed} deleted and connections repaired";
    }

    private static string ReconnectControllerNode(
        List<ControllerNodeGene> nodes,
        DeterministicRandom random)
    {
        int index = random.NextInt(nodes.Count);
        int[] candidates = Enumerable.Range(-1, nodes.Count + 1)
            .Where(candidate => candidate != index && candidate != nodes[index].RecurrentSourceIndex)
            .ToArray();
        int source = candidates[random.NextInt(candidates.Length)];
        int oldSource = nodes[index].RecurrentSourceIndex;
        nodes[index] = nodes[index] with { RecurrentSourceIndex = source };
        return $"controller node {index} recurrent source {oldSource}->{source}";
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
