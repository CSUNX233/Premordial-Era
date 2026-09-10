using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct EnvironmentSample(
    double TerrainHeight,
    double WaterSurface,
    double WaterDepth,
    double SampledDepth,
    double Pressure,
    double Temperature,
    double Light,
    double Minerals,
    double Detritus,
    double MetabolicWaste,
    double Moisture,
    double DissolvedOxygenAvailability,
    double AirOxygenAvailability,
    Vector2 Flow)
{
    // Added as body properties so existing positional construction remains source compatible.
    public double LandPlantBiomass { get; init; }
    public double AlgaeBiomass { get; init; }
    public double EdibleOrganics { get; init; }
    public double ProducerBiomass => LandPlantBiomass + AlgaeBiomass;
}

public interface IEnvironmentField
{
    EnvironmentSample Sample(Vector2 position, float depth = 0f);
    EnvironmentResourceSnapshot CaptureResourceSnapshot() => EnvironmentResourceSnapshot.Empty;
}

public interface IMutableEnvironmentField : IEnvironmentField
{
    double WithdrawMatter(Vector2 position, double requestedAmount);
    IReadOnlyDictionary<ulong,MatterReservation> ReserveMatter(
        IEnumerable<MatterUptakeRequest> requests);
    void ReturnMatter(MatterReservation reservation,double unusedAmount);
    IReadOnlyDictionary<ulong, MineralReservation> ReserveMinerals(
        IEnumerable<MineralUptakeRequest> requests) => new Dictionary<ulong, MineralReservation>();
    void ReturnMinerals(MineralReservation reservation, double unusedAmount) { }
    IReadOnlyDictionary<ulong, OrganicReservation> ReserveOrganic(
        IEnumerable<OrganicUptakeRequest> requests) => new Dictionary<ulong, OrganicReservation>();
    void ReturnOrganic(OrganicReservation reservation, double unusedAmount) { }
    IReadOnlyDictionary<ulong, DetritusReservation> ReserveDetritus(
        IEnumerable<DetritusUptakeRequest> requests) => new Dictionary<ulong, DetritusReservation>();
    void ReturnDetritus(DetritusReservation reservation, double unusedAmount) { }
    void DepositMinerals(Vector2 position, double amount) { }
    void DepositDetritus(Vector2 position, double amount);
    void DepositMetabolicWaste(Vector2 position, double amount);
    double WithdrawOxygen(Vector2 position, float depth, double immersion, double requestedAmount);
    void DepositOxygen(Vector2 position, float depth, double immersion, double amount);
    void UpdateOxygen(double deltaSeconds);
    void UpdateMatterCycles(double deltaSeconds);
    ProducerStepResult UpdateProducers(
        double deltaSeconds,
        IEnumerable<MineralUptakeRequest>? competingMineralRequests = null) => default;
    ulong ComputeResourceFingerprint() => 0UL;
    IReadOnlyDictionary<ulong, double> AllocateLightEnergy(
        IEnumerable<LightEnergyRequest> requests,
        double deltaSeconds);
    double TotalMinerals { get; }
    double TotalDetritus { get; }
    double TotalMetabolicWaste { get; }
    double TotalLandPlantBiomass => 0.0;
    double TotalAlgaeBiomass => 0.0;
    double TotalVegetation => TotalLandPlantBiomass + TotalAlgaeBiomass;
    double TotalEdibleOrganics => 0.0;
    double TotalOrganicMatter => TotalVegetation + TotalEdibleOrganics + TotalDetritus + TotalMetabolicWaste;
    double TotalEnvironmentMatter => TotalMinerals + TotalOrganicMatter;
    double OceanAreaFraction => 0.0;
    double TotalOxygen { get; }
    double CumulativeExternalOxygenSupply { get; }
    bool AllFinite { get; }
    double ApplyBrush(EnvironmentBrushCommand command);
}

public sealed record EnvironmentResourceSnapshot(
    int GridSize,
    float WorldSize,
    double CellArea,
    double[] TerrainHeight,
    double[] WaterDepth,
    double[] Minerals,
    double[] LandPlants,
    double[] Algae,
    double[] EdibleOrganics,
    double[] Detritus,
    double[] MetabolicWaste)
{
    public double[] CellAreas { get; init; } = [];
    public double OceanAreaFraction { get; init; }
    public static EnvironmentResourceSnapshot Empty { get; } = new(
        0, 0f, 0.0, [], [], [], [], [], [], [], []);
}

public readonly record struct ProducerStepResult(
    double MatterGrown,
    double MatterRespired,
    double MatterSenesced,
    double LightEnergyConsumed,
    double OxygenProduced,
    double OxygenConsumed,
    double ConservationResidual);

public readonly record struct MineralUptakeRequest(
    ulong OrganismId,
    Vector2 Position,
    double RequestedMatter);

public readonly record struct MineralReservation(
    ulong OrganismId,
    int CellIndex,
    double Minerals)
{
    public double Total => Minerals;
}

public readonly record struct OrganicUptakeRequest(
    ulong OrganismId,
    Vector2 Position,
    double RequestedMatter,
    double ProducerPreference = 0.75,
    double LooseOrganicAffinity = 1.0);

public readonly record struct OrganicReservation(
    ulong OrganismId,
    int CellIndex,
    double LandPlants,
    double Algae,
    double EdibleOrganics)
{
    public double Total => LandPlants + Algae + EdibleOrganics;
}

public readonly record struct DetritusUptakeRequest(
    ulong OrganismId,
    Vector2 Position,
    double RequestedMatter);

public readonly record struct DetritusReservation(
    ulong OrganismId,
    int CellIndex,
    double Detritus)
{
    public double Total => Detritus;
}

public enum EnvironmentBrushChannel
{
    Minerals,
    Temperature
}

public readonly record struct LightEnergyRequest(
    ulong OrganismId,
    Vector2 Position,
    float Depth,
    double RequestedEnergy);

public readonly record struct MatterUptakeRequest(
    ulong OrganismId,
    Vector2 Position,
    double RequestedMatter);

public readonly record struct MatterReservation(
    ulong OrganismId,
    int CellIndex,
    double Minerals,
    double Detritus)
{
    public double Total=>Minerals+Detritus;
}

public readonly record struct EnvironmentBrushCommand(
    Vector2 Position,
    float Radius,
    double Amount,
    EnvironmentBrushChannel Channel)
{
    public void Validate(float worldSize)
    {
        if (!float.IsFinite(Position.X) || !float.IsFinite(Position.Y) ||
            Position.X < 0f || Position.X > worldSize ||
            Position.Y < 0f || Position.Y > worldSize ||
            !float.IsFinite(Radius) || Radius <= 0f ||
            !double.IsFinite(Amount) || Amount == 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(EnvironmentBrushCommand));
        }
    }
}

/// <summary>
/// Regular backing grid with continuous bilinear simulation queries. Presentation may
/// request a detached grid snapshot, but never receives the live backing arrays.
/// </summary>
public sealed class BilinearEnvironmentField : IMutableEnvironmentField
{
    // Chemical energy released after uptake is owned by physiology. Producers only
    // move conserved matter and claim incident light; they never award energy twice.
    public const double FoodWebEnergyPerMatter = 9.0;
    private const double ProducerGrowthPerSecond = 0.045;
    private const double ProducerRespirationPerSecond = 0.0012;
    private const double ProducerSenescencePerSecond = 0.00045;
    private const double DetritusDecayPerSecond = 0.00035;
    private const double EdibleOrganicDecayPerSecond = 0.00055;
    private readonly int _gridSize;
    private readonly float _worldSize;
    private readonly double[] _terrain;
    private readonly double[] _temperature;
    private readonly double[] _light;
    private readonly double[] _cellAreas;
    private readonly double[] _habitatVariation;
    private readonly double[] _minerals;
    private readonly double[] _landPlants;
    private readonly double[] _algae;
    private readonly double[] _edibleOrganics;
    private readonly double[] _producerCapacity;
    private readonly double[] _detritus;
    private readonly double[] _metabolicWaste;
    private readonly double[] _moisture;
    private readonly Vector2[] _flow;
    private readonly double[] _dissolvedOxygen;
    private readonly double[] _dissolvedOxygenCapacity;
    private readonly double[] _airOxygen;
    private readonly double[] _airOxygenCapacity;
    private readonly double[] _airOxygenEquilibrium;
    private readonly double[] _lightEnergyBudget;
    private readonly double[] _producerLightClaim;
    private readonly double[] _organismMineralDemand;
    private readonly double[] _producerMineralBudget;
    private readonly double[] _producerDispersalDelta;
    private readonly Dictionary<ulong, MatterReservation> _activeMatterReservations = [];
    private readonly Dictionary<ulong, MineralReservation> _activeMineralReservations = [];
    private readonly Dictionary<ulong, OrganicReservation> _activeOrganicReservations = [];
    private readonly Dictionary<ulong, DetritusReservation> _activeDetritusReservations = [];
    private readonly double _wasteRemineralizationPerSecond;
    private readonly double _lightEnergyFluxPerWorldAreaPerSecond;
    private readonly double _referenceCellArea;
    private double _producerDispersalAccumulator;
    private int _producerDispersalDirection;

    public BilinearEnvironmentField(SimulationConfig config, DeterministicRandom random)
    {
        _gridSize = config.EnvironmentGridSize;
        _worldSize = config.WorldSize;
        _wasteRemineralizationPerSecond = config.WasteRemineralizationPerSecond;
        _lightEnergyFluxPerWorldAreaPerSecond = config.SurfaceLightEnergyPerWorldAreaPerSecond;
        _referenceCellArea = Math.Pow(config.WorldSize / (config.EnvironmentGridSize - 1), 2.0);
        int count = checked(_gridSize * _gridSize);
        _terrain = new double[count];
        _temperature = new double[count];
        _light = new double[count];
        _cellAreas = new double[count];
        _habitatVariation = new double[count];
        _minerals = new double[count];
        _landPlants = new double[count];
        _algae = new double[count];
        _edibleOrganics = new double[count];
        _producerCapacity = new double[count];
        _detritus = new double[count];
        _metabolicWaste = new double[count];
        _moisture = new double[count];
        _flow = new Vector2[count];
        _dissolvedOxygen = new double[count];
        _dissolvedOxygenCapacity = new double[count];
        _airOxygen = new double[count];
        _airOxygenCapacity = new double[count];
        _airOxygenEquilibrium = new double[count];
        _lightEnergyBudget = new double[count];
        _producerLightClaim = new double[count];
        _organismMineralDemand = new double[count];
        _producerMineralBudget = new double[count];
        _producerDispersalDelta = new double[count];

        double targetOceanAreaFraction = 0.70 + (0.10 * random.NextUnitDouble());
        Span<Vector3> noiseDirections = stackalloc Vector3[6];
        Span<double> noisePhases = stackalloc double[6];
        ReadOnlySpan<double> noiseFrequencies = [1.15, 1.85, 2.7, 3.9, 5.4, 7.2];
        ReadOnlySpan<double> noiseWeights = [0.34, 0.24, 0.17, 0.12, 0.08, 0.05];
        for (int term = 0; term < noiseDirections.Length; term++)
        {
            double z = (random.NextUnitDouble() * 2.0) - 1.0;
            double angle = random.NextUnitDouble() * Math.Tau;
            double radial = Math.Sqrt(Math.Max(0.0, 1.0 - (z * z)));
            noiseDirections[term] = new Vector3(
                (float)(radial * Math.Cos(angle)), (float)z, (float)(radial * Math.Sin(angle)));
            noisePhases[term] = random.NextUnitDouble() * Math.Tau;
        }
        double radius = SphericalWorld.Radius(_worldSize);
        double thetaStep = Math.PI / (_gridSize - 1);
        double longitudeStep = Math.Tau / _gridSize;

        for (int y = 0; y < _gridSize; y++)
        {
            double normalizedY = y / (double)(_gridSize - 1);
            double theta = normalizedY * Math.PI;
            double northEdge = Math.Max(0.0, theta - (thetaStep * 0.5));
            double southEdge = Math.Min(Math.PI, theta + (thetaStep * 0.5));
            double cellArea = radius * radius * longitudeStep *
                (Math.Cos(northEdge) - Math.Cos(southEdge));
            for (int x = 0; x < _gridSize; x++)
            {
                double normalizedX = x / (double)_gridSize;
                int index = Index(x, y);
                Vector2 mapPosition = new(
                    (float)(normalizedX * _worldSize), (float)(normalizedY * _worldSize));
                Vector3 unit = SphericalWorld.ToUnit(mapPosition, _worldSize);
                double broadNoise = SphericalNoise(
                    unit, noiseDirections, noisePhases, noiseFrequencies, noiseWeights);
                double detailNoise = 0.5 + (0.5 * Math.Sin(
                    10.7 * Vector3.Dot(unit, noiseDirections[5]) + noisePhases[4]));
                double variation = Math.Clamp(0.5 + (0.36 * broadNoise) +
                    (0.14 * (detailNoise - 0.5)), 0.0, 1.0);
                _habitatVariation[index] = variation;
                double areaScale = cellArea / _referenceCellArea;
                _cellAreas[index] = cellArea;

                // Low-frequency noise is evaluated in 3D direction space. Therefore
                // longitude wraps exactly and every representation of a pole agrees.
                double terrain = -7.0 + (35.0 * broadNoise) +
                    (4.0 * ((detailNoise * 2.0) - 1.0)) + config.TerrainElevationOffset;
                _terrain[index] = Math.Clamp(terrain, -0.85 * radius, 0.35 * radius);
                double equatorialWarmth = Math.Sin(theta);
                _temperature[index] = 0.42 + (0.46 * equatorialWarmth) + (variation * 0.05);
                _light[index] = 0.46 + (0.38 * equatorialWarmth) + (variation * 0.08);
                // Finite terrain-bound stores. Deep water starts nutrient-poor,
                // the shelf provides a readable approach gradient, and inland
                // terrain contains substantially richer mineral and old organic
                // deposits. Nothing below replenishes these initial inventories.
                double inland = SmoothStep(-1.5, 6.0, _terrain[index]);
                double coast = Math.Exp(-Math.Abs(_terrain[index]) / 3.5);
                double oceanFloor = 0.020 + (variation * 0.035);
                _minerals[index] = config.InitialMineralScale * areaScale *
                    (oceanFloor + (0.11 * coast) + (2.10 * inland));
                _detritus[index] = config.InitialMineralScale * areaScale *
                    ((0.018 * coast) + (0.16 * inland * (0.75 + (0.25 * variation))));
                _metabolicWaste[index] = 0.0;
                _moisture[index] = Math.Clamp(
                    0.24 + (0.60 * equatorialWarmth) + (0.20 * broadNoise), 0.08, 1.0);
                _flow[index] = new Vector2(
                    (float)(equatorialWarmth * (0.055 + (0.075 * variation))),
                    (float)(equatorialWarmth * 0.035 *
                        Math.Sin((normalizedX * Math.Tau * 2.0) + noisePhases[0])));

                _airOxygenCapacity[index] = 4.0 * areaScale;
                double airAvailability = Math.Clamp(0.30 + ((variation - 0.5) * 0.08), 0.22, 0.38);
                _airOxygen[index] = _airOxygenCapacity[index] * airAvailability;
                _airOxygenEquilibrium[index] = _airOxygen[index];
                double waterDepth = Math.Max(0.0, -_terrain[index]);
                if (waterDepth > 0.0)
                {
                    _dissolvedOxygenCapacity[index] =
                        (0.85 + (0.025 * waterDepth)) * areaScale;
                    double mixing = Math.Clamp(_flow[index].Length() * 4.0, 0.0, 0.55);
                    double dissolvedAvailability = Math.Clamp(
                        0.18 + (0.16 * mixing) + ((variation - 0.5) * 0.05),
                        0.08,
                        0.36);
                    _dissolvedOxygen[index] =
                        _dissolvedOxygenCapacity[index] * dissolvedAvailability;
                }

            }
        }
        if (Math.Abs(config.TerrainElevationOffset) <= 1e-12)
            CalibrateSeaLevel(targetOceanAreaFraction, radius);
        InitializeInventories(config.InitialMineralScale);
        OceanAreaFraction = CalculateOceanAreaFraction();
        SeedProducers(config.InitialMineralScale);
    }

    public double TotalMinerals => Sum(_minerals);
    internal void SetInitialMineralBudget(double budget)
    {
        if(!double.IsFinite(budget))
            throw new InvalidOperationException("The initial resource budget cannot fund this many founders.");
        if (budget < 0.0)
        {
            // Founders are funded from the same finite world inventory. If their
            // allocation exceeds free minerals, draw proportionally from every
            // environmental pool instead of deleting all minerals or creating food.
            double organic = TotalOrganicMatter;
            double targetTotal = organic + budget;
            double currentTotal = TotalMinerals + organic;
            if (targetTotal < -1e-10 || currentTotal <= 0.0)
                throw new InvalidOperationException("The initial resource budget cannot fund this many founders.");
            double resourceScale = Math.Max(0.0, targetTotal) / currentTotal;
            Scale(_minerals, resourceScale);
            Scale(_landPlants, resourceScale);
            Scale(_algae, resourceScale);
            Scale(_edibleOrganics, resourceScale);
            Scale(_detritus, resourceScale);
            Scale(_metabolicWaste, resourceScale);
            return;
        }
        double current=TotalMinerals;
        double scale=current>0.0?budget/current:0.0;
        Scale(_minerals,scale);
    }
    public double TotalDetritus => Sum(_detritus);
    public double TotalMetabolicWaste => Sum(_metabolicWaste);
    public double TotalLandPlantBiomass => Sum(_landPlants);
    public double TotalAlgaeBiomass => Sum(_algae);
    public double TotalVegetation => TotalLandPlantBiomass + TotalAlgaeBiomass;
    public double TotalEdibleOrganics => Sum(_edibleOrganics);
    public double TotalOrganicMatter => TotalVegetation + TotalEdibleOrganics + TotalDetritus + TotalMetabolicWaste;
    public double TotalEnvironmentMatter => TotalMinerals + TotalOrganicMatter;
    public double OceanAreaFraction { get; private set; }
    public double TotalOxygen => Sum(_dissolvedOxygen) + Sum(_airOxygen);
    public double CumulativeExternalOxygenSupply { get; private set; }

    public bool AllFinite =>
        _terrain.All(double.IsFinite) &&
        _temperature.All(value => double.IsFinite(value) && value >= 0.0) &&
        _light.All(value => double.IsFinite(value) && value >= 0.0) &&
        _cellAreas.All(value => double.IsFinite(value) && value > 0.0) &&
        _habitatVariation.All(value => double.IsFinite(value) && value >= 0.0 && value <= 1.0) &&
        _minerals.All(value => double.IsFinite(value) && value >= 0.0) &&
        _landPlants.All(value => double.IsFinite(value) && value >= 0.0) &&
        _algae.All(value => double.IsFinite(value) && value >= 0.0) &&
        _edibleOrganics.All(value => double.IsFinite(value) && value >= 0.0) &&
        _producerCapacity.All(value => double.IsFinite(value) && value >= 0.0) &&
        _detritus.All(value => double.IsFinite(value) && value >= 0.0) &&
        _metabolicWaste.All(value => double.IsFinite(value) && value >= 0.0) &&
        _moisture.All(value => double.IsFinite(value) && value >= 0.0) &&
        _dissolvedOxygen.All(value => double.IsFinite(value) && value >= 0.0) &&
        _airOxygen.All(value => double.IsFinite(value) && value >= 0.0) &&
        _lightEnergyBudget.All(value => double.IsFinite(value) && value >= 0.0) &&
        _producerLightClaim.All(value => double.IsFinite(value) && value >= 0.0) &&
        _producerDispersalDelta.All(double.IsFinite) &&
        double.IsFinite(CumulativeExternalOxygenSupply);

    public EnvironmentSample Sample(Vector2 position, float depth = 0f)
    {
        CellQuad quad = Locate(position);
        double terrain = Interpolate(_terrain, quad);
        const double waterSurface = 0.0;
        double seabedDepth = Math.Max(0.0, waterSurface - terrain);
        double legalDepth = Math.Clamp(depth, 0.0, seabedDepth);
        double lightAtSurface = Interpolate(_light, quad);

        return new EnvironmentSample(
            terrain,
            waterSurface,
            seabedDepth,
            legalDepth,
            1.0 + (legalDepth * 0.045),
            Interpolate(_temperature, quad) - (legalDepth * 0.002),
            lightAtSurface * Math.Exp(-legalDepth * 0.06),
            Interpolate(_minerals, quad),
            Interpolate(_detritus, quad),
            Interpolate(_metabolicWaste, quad),
            Interpolate(_moisture, quad),
            Availability(_dissolvedOxygen, _dissolvedOxygenCapacity, quad),
            Availability(_airOxygen, _airOxygenCapacity, quad),
            Interpolate(_flow, quad))
        {
            LandPlantBiomass = Interpolate(_landPlants, quad),
            AlgaeBiomass = Interpolate(_algae, quad),
            EdibleOrganics = Interpolate(_edibleOrganics, quad)
        };
    }

    public EnvironmentResourceSnapshot CaptureResourceSnapshot()
    {
        double averageCellArea = 4.0 * Math.PI * Math.Pow(SphericalWorld.Radius(_worldSize), 2.0) /
            _terrain.Length;
        double[] waterDepth = new double[_terrain.Length];
        for (int index = 0; index < waterDepth.Length; index++)
            waterDepth[index] = Math.Max(0.0, -_terrain[index]);
        return new EnvironmentResourceSnapshot(
            _gridSize,
            _worldSize,
            averageCellArea,
            (double[])_terrain.Clone(),
            waterDepth,
            (double[])_minerals.Clone(),
            (double[])_landPlants.Clone(),
            (double[])_algae.Clone(),
            (double[])_edibleOrganics.Clone(),
            (double[])_detritus.Clone(),
            (double[])_metabolicWaste.Clone())
        {
            CellAreas = (double[])_cellAreas.Clone(),
            OceanAreaFraction = OceanAreaFraction
        };
    }

    public double WithdrawMatter(Vector2 position, double requestedAmount)
    {
        if (!double.IsFinite(requestedAmount) || requestedAmount < 0.0)
            throw new ArgumentOutOfRangeException(nameof(requestedAmount));
        if (requestedAmount == 0.0)
            return 0.0;

        CellQuad quad = Locate(position);
        double mineralSignal = Interpolate(_minerals, quad);
        double detritusSignal = Interpolate(_detritus, quad);
        double totalSignal = mineralSignal + detritusSignal;
        if (totalSignal <= 0.0)
            return 0.0;

        double mineralRequest = requestedAmount * (mineralSignal / totalSignal);
        double detritusRequest = requestedAmount - mineralRequest;
        return Withdraw(_minerals, quad, mineralRequest) + Withdraw(_detritus, quad, detritusRequest);
    }

    public IReadOnlyDictionary<ulong,MatterReservation> ReserveMatter(
        IEnumerable<MatterUptakeRequest> requests)
    {
        // Any unreturned portion from the preceding allocation was consumed.
        // A reservation token is valid for one allocation cycle and one return.
        _activeMatterReservations.Clear();
        Dictionary<ulong,MatterReservation> reservations=[];
        foreach(IGrouping<int,MatterUptakeRequest> group in requests
                    .Where(request=>request.RequestedMatter>0.0)
                    .GroupBy(request=>DominantCell(Locate(request.Position))))
        {
            MatterUptakeRequest[] ordered=group.OrderBy(request=>request.OrganismId).ToArray();
            double totalRequest=ordered.Sum(request=>request.RequestedMatter);
            double minerals=_minerals[group.Key],detritus=_detritus[group.Key];
            double available=minerals+detritus;
            double fraction=totalRequest>0.0?Math.Min(1.0,available/totalRequest):0.0;
            double mineralShare=available>0.0?minerals/available:0.0;
            double totalMinerals=0.0,totalDetritus=0.0;
            foreach(MatterUptakeRequest request in ordered)
            {
                if (reservations.ContainsKey(request.OrganismId))
                    throw new InvalidOperationException("Each organism may submit one matter request per allocation cycle.");
                double amount=request.RequestedMatter*fraction;
                MatterReservation reservation=new(request.OrganismId,group.Key,
                    amount*mineralShare,amount*(1.0-mineralShare));
                reservations[request.OrganismId]=reservation;
                _activeMatterReservations[request.OrganismId]=reservation;
                totalMinerals+=reservation.Minerals;totalDetritus+=reservation.Detritus;
            }
            Remove(_minerals,group.Key,Math.Min(minerals,totalMinerals));
            Remove(_detritus,group.Key,Math.Min(detritus,totalDetritus));
        }
        return reservations;
    }

    public void ReturnMatter(MatterReservation reservation,double unusedAmount)
    {
        if(!double.IsFinite(unusedAmount)||unusedAmount<0.0||unusedAmount>reservation.Total+1e-10)
            throw new ArgumentOutOfRangeException(nameof(unusedAmount));
        // No-demand organisms receive the default zero reservation in World.
        // A zero return cannot add material and needs no active allocation.
        if (reservation.Total <= 0.0 && unusedAmount == 0.0) return;
        if (!_activeMatterReservations.TryGetValue(reservation.OrganismId, out MatterReservation active) ||
            active != reservation)
            throw new InvalidOperationException("A matter reservation may be returned only once in its allocation cycle.");
        _activeMatterReservations.Remove(reservation.OrganismId);
        if(unusedAmount<=0.0||reservation.Total<=0.0)return;
        double fraction=Math.Min(1.0,unusedAmount/reservation.Total);
        Add(_minerals,reservation.CellIndex,reservation.Minerals*fraction);
        Add(_detritus,reservation.CellIndex,reservation.Detritus*fraction);
    }

    public IReadOnlyDictionary<ulong, MineralReservation> ReserveMinerals(
        IEnumerable<MineralUptakeRequest> requests)
    {
        _activeMineralReservations.Clear();
        Dictionary<ulong, MineralReservation> reservations = [];
        foreach (IGrouping<int, MineralUptakeRequest> group in requests
                     .Where(request => request.RequestedMatter > 0.0)
                     .GroupBy(request => DominantCell(Locate(request.Position))))
        {
            MineralUptakeRequest[] ordered = group.OrderBy(request => request.OrganismId).ToArray();
            double requested = ordered.Sum(request => request.RequestedMatter);
            double fraction = requested > 0.0 ? Math.Min(1.0, _minerals[group.Key] / requested) : 0.0;
            double reserved = 0.0;
            foreach (MineralUptakeRequest request in ordered)
            {
                if (!double.IsFinite(request.RequestedMatter) || request.RequestedMatter < 0.0 ||
                    reservations.ContainsKey(request.OrganismId))
                    throw new InvalidOperationException("Each organism may submit one finite mineral request per allocation cycle.");
                MineralReservation reservation = new(
                    request.OrganismId, group.Key, request.RequestedMatter * fraction);
                reservations.Add(request.OrganismId, reservation);
                _activeMineralReservations.Add(request.OrganismId, reservation);
                reserved += reservation.Minerals;
            }
            Remove(_minerals, group.Key, Math.Min(_minerals[group.Key], reserved));
        }
        return reservations;
    }

    public void ReturnMinerals(MineralReservation reservation, double unusedAmount)
    {
        ValidateUnused(unusedAmount, reservation.Total);
        if (reservation.Total <= 0.0 && unusedAmount == 0.0) return;
        if (!_activeMineralReservations.TryGetValue(reservation.OrganismId, out MineralReservation active) ||
            active != reservation)
            throw new InvalidOperationException("A mineral reservation may be returned only once in its allocation cycle.");
        _activeMineralReservations.Remove(reservation.OrganismId);
        if (unusedAmount > 0.0)
            Add(_minerals, reservation.CellIndex, unusedAmount);
    }

    public IReadOnlyDictionary<ulong, OrganicReservation> ReserveOrganic(
        IEnumerable<OrganicUptakeRequest> requests)
    {
        _activeOrganicReservations.Clear();
        Dictionary<ulong, OrganicReservation> reservations = [];
        foreach (IGrouping<int, OrganicUptakeRequest> group in requests
                     .Where(request => request.RequestedMatter > 0.0)
                     .GroupBy(request => DominantCell(Locate(request.Position))))
        {
            OrganicUptakeRequest[] ordered = group.OrderBy(request => request.OrganismId).ToArray();
            double requested = ordered.Sum(request => request.RequestedMatter);
            double land = _landPlants[group.Key];
            double algae = _algae[group.Key];
            double loose = _edibleOrganics[group.Key];
            double available = land + algae + loose;
            foreach (OrganicUptakeRequest request in ordered)
                if (!double.IsFinite(request.RequestedMatter) || request.RequestedMatter < 0.0 ||
                    !double.IsFinite(request.ProducerPreference) || request.ProducerPreference is < 0.0 or > 1.0 ||
                    !double.IsFinite(request.LooseOrganicAffinity) || request.LooseOrganicAffinity is < 0.0 or > 1.0)
                    throw new InvalidOperationException("Organic requests must be finite and preferences and affinities must be in [0,1].");

            if (ordered.All(request => request.LooseOrganicAffinity == 1.0))
            {
                ReserveUnrestrictedOrganic(
                    group.Key, ordered, requested, land, algae, loose, available, reservations);
                continue;
            }

            ReserveDietAwareOrganic(group.Key, ordered, land, algae, loose, reservations);
        }
        return reservations;
    }

    private void ReserveUnrestrictedOrganic(
        int cellIndex,
        OrganicUptakeRequest[] ordered,
        double requested,
        double land,
        double algae,
        double loose,
        double available,
        Dictionary<ulong, OrganicReservation> reservations)
    {
        double fraction = requested > 0.0 ? Math.Min(1.0, available / requested) : 0.0;
        double allocated = requested * fraction;
        double producerAvailable = land + algae;
        double preference = requested > 0.0
            ? ordered.Sum(request => request.RequestedMatter * request.ProducerPreference) / requested
            : 0.0;
        double producerTake = Math.Min(producerAvailable, allocated * preference);
        double looseTake = Math.Min(loose, allocated - producerTake);
        producerTake += Math.Min(
            producerAvailable - producerTake,
            Math.Max(0.0, allocated - producerTake - looseTake));
        looseTake += Math.Min(
            loose - looseTake,
            Math.Max(0.0, allocated - producerTake - looseTake));
        double landTake = producerAvailable > 0.0 ? producerTake * land / producerAvailable : 0.0;
        double algaeTake = producerTake - landTake;
        double totalLand = 0.0, totalAlgae = 0.0, totalLoose = 0.0;
        foreach (OrganicUptakeRequest request in ordered)
        {
            if (reservations.ContainsKey(request.OrganismId))
                throw new InvalidOperationException("Each organism may submit one finite organic request per allocation cycle.");

            double amount = request.RequestedMatter * fraction;
            double allocationShare = allocated > 0.0 ? amount / allocated : 0.0;
            OrganicReservation reservation = new(
                request.OrganismId,
                cellIndex,
                landTake * allocationShare,
                algaeTake * allocationShare,
                looseTake * allocationShare);
            reservations.Add(request.OrganismId, reservation);
            _activeOrganicReservations.Add(request.OrganismId, reservation);
            totalLand += reservation.LandPlants;
            totalAlgae += reservation.Algae;
            totalLoose += reservation.EdibleOrganics;
        }
        Remove(_landPlants, cellIndex, Math.Min(land, totalLand));
        Remove(_algae, cellIndex, Math.Min(algae, totalAlgae));
        Remove(_edibleOrganics, cellIndex, Math.Min(loose, totalLoose));
    }

    private void ReserveDietAwareOrganic(
        int cellIndex,
        OrganicUptakeRequest[] ordered,
        double land,
        double algae,
        double loose,
        Dictionary<ulong, OrganicReservation> reservations)
    {
        int count = ordered.Length;
        double producerAvailable = land + algae;
        double[] targets = new double[count];
        double[] looseCaps = new double[count];
        double totalRequested = 0.0;
        for (int index = 0; index < count; index++)
        {
            totalRequested += ordered[index].RequestedMatter;
            looseCaps[index] = ordered[index].RequestedMatter * ordered[index].LooseOrganicAffinity;
        }

        // Give every competitor the same fraction of its request that the two
        // finite pools can jointly support. A consumer's loose cap is individual,
        // so a flexible neighbor can never make carrion eligible for a herbivore.
        double low = 0.0, high = 1.0;
        for (int iteration = 0; iteration < 64; iteration++)
        {
            double fraction = (low + high) * 0.5;
            double targetTotal = totalRequested * fraction;
            double eligibleLoose = 0.0;
            for (int index = 0; index < count; index++)
                eligibleLoose += Math.Min(
                    ordered[index].RequestedMatter * fraction,
                    looseCaps[index]);
            double producerNeeded = targetTotal - Math.Min(loose, eligibleLoose);
            if (targetTotal <= producerAvailable + loose && producerNeeded <= producerAvailable)
                low = fraction;
            else
                high = fraction;
        }

        double targetSum = 0.0;
        double[] desiredLoose = new double[count];
        double[] looseAllocations = new double[count];
        for (int index = 0; index < count; index++)
        {
            targets[index] = ordered[index].RequestedMatter * low;
            targetSum += targets[index];
            looseCaps[index] = Math.Min(looseCaps[index], targets[index]);
            desiredLoose[index] = Math.Min(
                looseCaps[index],
                targets[index] * (1.0 - ordered[index].ProducerPreference));
        }

        double maximumLoose = Math.Min(loose, looseCaps.Sum());
        double requiredLoose = Math.Min(maximumLoose, Math.Max(0.0, targetSum - producerAvailable));
        double preferredLoose = Math.Min(maximumLoose, desiredLoose.Sum());
        double looseTarget = Math.Max(requiredLoose, preferredLoose);
        double looseTaken = AllocateProportionally(looseTarget, desiredLoose, looseAllocations);

        if (looseTaken < looseTarget)
        {
            double[] remainingCaps = new double[count];
            for (int index = 0; index < count; index++)
                remainingCaps[index] = Math.Max(0.0, looseCaps[index] - looseAllocations[index]);
            looseTaken += AllocateProportionally(
                looseTarget - looseTaken, remainingCaps, looseAllocations);
        }

        double[] producerAllocations = new double[count];
        double producerTaken = 0.0;
        for (int index = 0; index < count; index++)
        {
            producerAllocations[index] = Math.Max(0.0, targets[index] - looseAllocations[index]);
            producerTaken += producerAllocations[index];
        }

        // Once a restricted diet fixes the common fair share, a compatible pool
        // may remain. Distribute it among eligible unmet requests in bounded passes.
        double[] unmet = new double[count];
        double[] extraLooseCaps = new double[count];
        for (int index = 0; index < count; index++)
        {
            unmet[index] = Math.Max(0.0, ordered[index].RequestedMatter - targets[index]);
            extraLooseCaps[index] = Math.Min(
                unmet[index],
                Math.Max(0.0,
                    ordered[index].RequestedMatter * ordered[index].LooseOrganicAffinity -
                    looseAllocations[index]));
        }
        looseTaken += AllocateProportionally(
            Math.Max(0.0, loose - looseTaken), extraLooseCaps, looseAllocations);
        for (int index = 0; index < count; index++)
            unmet[index] = Math.Max(
                0.0,
                ordered[index].RequestedMatter - producerAllocations[index] - looseAllocations[index]);
        producerTaken += AllocateProportionally(
            Math.Max(0.0, producerAvailable - producerTaken), unmet, producerAllocations);

        double landFraction = producerAvailable > 0.0 ? land / producerAvailable : 0.0;
        double totalLand = 0.0, totalAlgae = 0.0, totalLoose = 0.0;
        for (int index = 0; index < count; index++)
        {
            OrganicUptakeRequest request = ordered[index];
            if (reservations.ContainsKey(request.OrganismId))
                throw new InvalidOperationException("Each organism may submit one finite organic request per allocation cycle.");
            double landTake = producerAllocations[index] * landFraction;
            OrganicReservation reservation = new(
                request.OrganismId,
                cellIndex,
                landTake,
                producerAllocations[index] - landTake,
                looseAllocations[index]);
            reservations.Add(request.OrganismId, reservation);
            _activeOrganicReservations.Add(request.OrganismId, reservation);
            totalLand += reservation.LandPlants;
            totalAlgae += reservation.Algae;
            totalLoose += reservation.EdibleOrganics;
        }
        Remove(_landPlants, cellIndex, Math.Min(land, totalLand));
        Remove(_algae, cellIndex, Math.Min(algae, totalAlgae));
        Remove(_edibleOrganics, cellIndex, Math.Min(loose, totalLoose));
    }

    private static double AllocateProportionally(
        double available,
        IReadOnlyList<double> capacities,
        double[] allocations)
    {
        double capacity = 0.0;
        for (int index = 0; index < capacities.Count; index++)
            capacity += capacities[index];
        if (available <= 0.0 || capacity <= 0.0) return 0.0;

        double target = Math.Min(available, capacity);
        double remaining = target;
        double allocated = 0.0;
        int lastEligible = -1;
        for (int index = capacities.Count - 1; index >= 0; index--)
            if (capacities[index] > 0.0)
            {
                lastEligible = index;
                break;
            }
        for (int index = 0; index < capacities.Count; index++)
        {
            double amount = index == lastEligible
                ? Math.Min(capacities[index], remaining)
                : Math.Min(remaining, capacities[index] * target / capacity);
            allocations[index] += amount;
            allocated += amount;
            remaining -= amount;
        }
        return allocated;
    }

    public void ReturnOrganic(OrganicReservation reservation, double unusedAmount)
    {
        ValidateUnused(unusedAmount, reservation.Total);
        if (reservation.Total <= 0.0 && unusedAmount == 0.0) return;
        if (!_activeOrganicReservations.TryGetValue(reservation.OrganismId, out OrganicReservation active) ||
            active != reservation)
            throw new InvalidOperationException("An organic reservation may be returned only once in its allocation cycle.");
        _activeOrganicReservations.Remove(reservation.OrganismId);
        if (unusedAmount <= 0.0 || reservation.Total <= 0.0) return;
        double fraction = unusedAmount / reservation.Total;
        Add(_landPlants, reservation.CellIndex, reservation.LandPlants * fraction);
        Add(_algae, reservation.CellIndex, reservation.Algae * fraction);
        Add(_edibleOrganics, reservation.CellIndex, reservation.EdibleOrganics * fraction);
    }

    public IReadOnlyDictionary<ulong, DetritusReservation> ReserveDetritus(
        IEnumerable<DetritusUptakeRequest> requests)
    {
        _activeDetritusReservations.Clear();
        Dictionary<ulong, DetritusReservation> reservations = [];
        foreach (IGrouping<int, DetritusUptakeRequest> group in requests
                     .Where(request => request.RequestedMatter > 0.0)
                     .GroupBy(request => DominantCell(Locate(request.Position))))
        {
            DetritusUptakeRequest[] ordered = group.OrderBy(request => request.OrganismId).ToArray();
            double requested = ordered.Sum(request => request.RequestedMatter);
            double fraction = requested > 0.0 ? Math.Min(1.0, _detritus[group.Key] / requested) : 0.0;
            double reserved = 0.0;
            foreach (DetritusUptakeRequest request in ordered)
            {
                if (!double.IsFinite(request.RequestedMatter) || request.RequestedMatter < 0.0 ||
                    reservations.ContainsKey(request.OrganismId))
                    throw new InvalidOperationException("Each organism may submit one finite detritus request per allocation cycle.");
                DetritusReservation reservation = new(
                    request.OrganismId, group.Key, request.RequestedMatter * fraction);
                reservations.Add(request.OrganismId, reservation);
                _activeDetritusReservations.Add(request.OrganismId, reservation);
                reserved += reservation.Detritus;
            }
            Remove(_detritus, group.Key, Math.Min(_detritus[group.Key], reserved));
        }
        return reservations;
    }

    public void ReturnDetritus(DetritusReservation reservation, double unusedAmount)
    {
        ValidateUnused(unusedAmount, reservation.Total);
        if (reservation.Total <= 0.0 && unusedAmount == 0.0) return;
        if (!_activeDetritusReservations.TryGetValue(reservation.OrganismId, out DetritusReservation active) ||
            active != reservation)
            throw new InvalidOperationException("A detritus reservation may be returned only once in its allocation cycle.");
        _activeDetritusReservations.Remove(reservation.OrganismId);
        if (unusedAmount > 0.0)
            Add(_detritus, reservation.CellIndex, unusedAmount);
    }

    public void DepositMinerals(Vector2 position, double amount)
    {
        ValidateDeposit(amount);
        if (amount > 0.0)
            Deposit(_minerals, Locate(position), amount);
    }

    public void DepositDetritus(Vector2 position, double amount)
    {
        if (!double.IsFinite(amount) || amount < 0.0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount == 0.0)
            return;

        CellQuad quad = Locate(position);
        Add(_detritus, quad.I00, amount * quad.W00);
        Add(_detritus, quad.I10, amount * quad.W10);
        Add(_detritus, quad.I01, amount * quad.W01);
        Add(_detritus, quad.I11, amount * quad.W11);
    }

    public void DepositMetabolicWaste(Vector2 position, double amount)
    {
        if (!double.IsFinite(amount) || amount < 0.0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount == 0.0)
            return;
        Deposit(_metabolicWaste, Locate(position), amount);
    }

    public double WithdrawOxygen(
        Vector2 position,
        float depth,
        double immersion,
        double requestedAmount)
    {
        if (!double.IsFinite(requestedAmount) || requestedAmount < 0.0 ||
            !double.IsFinite(immersion) || immersion is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedAmount));
        }
        if (requestedAmount == 0.0)
            return 0.0;

        CellQuad quad = Locate(position);
        double waterRequest = requestedAmount * immersion;
        double airRequest = requestedAmount - waterRequest;
        return Withdraw(_dissolvedOxygen, quad, waterRequest) +
            Withdraw(_airOxygen, quad, airRequest);
    }

    public void DepositOxygen(
        Vector2 position,
        float depth,
        double immersion,
        double amount)
    {
        _ = depth;
        if (!double.IsFinite(amount) || amount < 0.0 ||
            !double.IsFinite(immersion) || immersion is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }
        if (amount == 0.0)
            return;
        CellQuad quad = Locate(position);
        Deposit(_dissolvedOxygen, quad, amount * immersion);
        Deposit(_airOxygen, quad, amount * (1.0 - immersion));
    }

    public void UpdateOxygen(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));

        for (int index = 0; index < _airOxygen.Length; index++)
        {
            double missingAir = Math.Max(0.0, _airOxygenEquilibrium[index] - _airOxygen[index]);
            double externalSupply = Math.Min(
                missingAir,
                _airOxygenCapacity[index] * 0.0025 * deltaSeconds);
            _airOxygen[index] += externalSupply;
            CumulativeExternalOxygenSupply += externalSupply;

            double waterCapacity = _dissolvedOxygenCapacity[index];
            if (waterCapacity <= 0.0)
                continue;
            double airAvailability = _airOxygen[index] / _airOxygenCapacity[index];
            double dissolvedAvailability = _dissolvedOxygen[index] / waterCapacity;
            double temperatureSolubility = Math.Clamp(
                1.08 - (0.42 * _temperature[index]),
                0.58,
                0.95);
            double mixing = Math.Clamp(_flow[index].Length() * 4.0, 0.0, 0.55);
            double equilibrium = airAvailability * temperatureSolubility * (0.72 + mixing);
            double exchange = (equilibrium - dissolvedAvailability) *
                waterCapacity * (0.025 + (0.035 * mixing)) * deltaSeconds;
            if (exchange > 0.0)
            {
                exchange = Math.Min(exchange, _airOxygen[index]);
                _airOxygen[index] -= exchange;
                _dissolvedOxygen[index] += exchange;
            }
            else if (exchange < 0.0)
            {
                double outgas = Math.Min(-exchange, _dissolvedOxygen[index]);
                _dissolvedOxygen[index] -= outgas;
                _airOxygen[index] += outgas;
            }
        }
    }

    public void UpdateMatterCycles(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        double wasteFraction = 1.0 - Math.Exp(-_wasteRemineralizationPerSecond * deltaSeconds);
        double detritusFraction = 1.0 - Math.Exp(-DetritusDecayPerSecond * deltaSeconds);
        double organicFraction = 1.0 - Math.Exp(-EdibleOrganicDecayPerSecond * deltaSeconds);
        for (int index = 0; index < _metabolicWaste.Length; index++)
        {
            // Background decomposition only moves matter down the chain. It emits
            // no harvestable energy, so repeatedly cycling a parcel cannot mint fuel.
            double spoiled = _edibleOrganics[index] * organicFraction;
            _edibleOrganics[index] -= spoiled;
            _detritus[index] += spoiled;
            double decomposed = _detritus[index] * detritusFraction;
            _detritus[index] -= decomposed;
            _metabolicWaste[index] += decomposed;
            double recycled = _metabolicWaste[index] * wasteFraction;
            _metabolicWaste[index] -= recycled;
            _minerals[index] += recycled;
        }
    }

    public ProducerStepResult UpdateProducers(
        double deltaSeconds,
        IEnumerable<MineralUptakeRequest>? competingMineralRequests = null)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));

        double grown = 0.0, respired = 0.0, senesced = 0.0, lightConsumed = 0.0;
        double residual = 0.0;
        Array.Clear(_producerLightClaim);
        Array.Clear(_organismMineralDemand);
        if (competingMineralRequests is not null)
            foreach (MineralUptakeRequest request in competingMineralRequests)
            {
                if (!double.IsFinite(request.RequestedMatter) || request.RequestedMatter < 0.0)
                    throw new InvalidOperationException("Competing mineral requests must be finite and non-negative.");
                if (request.RequestedMatter <= 0.0) continue;
                int cellIndex=DominantCell(Locate(request.Position));
                _organismMineralDemand[cellIndex] += request.RequestedMatter;
                if(!double.IsFinite(_organismMineralDemand[cellIndex]))
                    throw new InvalidOperationException("Competing mineral demand overflowed its cell budget.");
            }
        double wholeStepGrowthFraction = 1.0 - Math.Exp(-ProducerGrowthPerSecond * deltaSeconds);
        Array.Fill(_producerMineralBudget,double.PositiveInfinity);
        for(int index=0;index<_minerals.Length;index++)
        {
            double organismDemand=_organismMineralDemand[index];
            if(organismDemand<=0.0)continue;
            double producer=_landPlants[index]+_algae[index];
            double capacity=_producerCapacity[index];
            double room=Math.Max(0.0,capacity-producer);
            double densityLimit=capacity>0.0?room/capacity:0.0;
            double usableLightFactor=_terrain[index]>=0.0?_light[index]:NearSurfaceAlgaeLight(index);
            double usableLight=usableLightFactor*_lightEnergyFluxPerWorldAreaPerSecond*
                _cellAreas[index]*deltaSeconds;
            double producerPotential=Math.Min(usableLight/FoodWebEnergyPerMatter,
                producer*wholeStepGrowthFraction*densityLimit);
            _producerMineralBudget[index]=producerPotential+organismDemand>0.0
                ?_minerals[index]*producerPotential/(producerPotential+organismDemand)
                :0.0;
        }
        int substeps = Math.Min(64, Math.Max(1, (int)Math.Ceiling(deltaSeconds / 0.5)));
        double stepSeconds = deltaSeconds / substeps;
        double growthFraction = 1.0 - Math.Exp(-ProducerGrowthPerSecond * stepSeconds);
        double respirationFraction = 1.0 - Math.Exp(-ProducerRespirationPerSecond * stepSeconds);
        double senescenceFraction = 1.0 - Math.Exp(-ProducerSenescencePerSecond * stepSeconds);

        for (int substep = 0; substep < substeps; substep++)
        for (int index = 0; index < _minerals.Length; index++)
        {
            double producer = _landPlants[index] + _algae[index];
            double capacity = _producerCapacity[index];
            if (producer <= 0.0 || capacity <= 0.0)
                continue;
            double cellBefore = _minerals[index] + _landPlants[index] + _algae[index] +
                _edibleOrganics[index] + _detritus[index] + _metabolicWaste[index];

            double usableLightFactor = _terrain[index] >= 0.0
                ? _light[index]
                : NearSurfaceAlgaeLight(index);
            double usableLight = usableLightFactor * _lightEnergyFluxPerWorldAreaPerSecond *
                _cellAreas[index] * stepSeconds;
            double room = Math.Max(0.0, capacity - producer);
            double densityLimit = capacity > 0.0 ? room / capacity : 0.0;
            double producerPotential = Math.Min(
                usableLight / FoodWebEnergyPerMatter,
                producer * growthFraction * densityLimit);
            double growth = Math.Min(_minerals[index],
                Math.Min(producerPotential, _producerMineralBudget[index]));
            if (growth > 0.0)
            {
                _minerals[index] -= growth;
                _producerMineralBudget[index]-=growth;
                if (_terrain[index] >= 0.0) _landPlants[index] += growth;
                else _algae[index] += growth;
                double light = growth * FoodWebEnergyPerMatter;
                _producerLightClaim[index] += light;
                grown += growth;
                lightConsumed += light;
            }

            producer += growth;
            double respiration = producer * respirationFraction;
            double afterRespiration = producer - respiration;
            double senescence = afterRespiration * senescenceFraction;
            double removed = respiration + senescence;
            if (removed > 0.0)
            {
                double fraction = removed / producer;
                _landPlants[index] -= _landPlants[index] * fraction;
                _algae[index] -= _algae[index] * fraction;
                _metabolicWaste[index] += respiration;
                _detritus[index] += senescence * 0.85;
                _edibleOrganics[index] += senescence * 0.15;
                respired += respiration;
                senesced += senescence;
            }
            double cellAfter = _minerals[index] + _landPlants[index] + _algae[index] +
                _edibleOrganics[index] + _detritus[index] + _metabolicWaste[index];
            residual += cellAfter - cellBefore;
        }
        DisperseProducers(deltaSeconds);

        // Oxygen is deliberately net-zero in this first material ledger: CO2 and
        // water are not represented, so emitting O2 would create untracked atoms.
        return new ProducerStepResult(grown, respired, senesced, lightConsumed, 0.0, 0.0, residual);
    }

    private void DisperseProducers(double deltaSeconds)
    {
        _producerDispersalAccumulator += deltaSeconds;
        int events = Math.Min(64, (int)Math.Floor(_producerDispersalAccumulator / 0.5));
        if (events <= 0) return;
        _producerDispersalAccumulator -= events * 0.5;
        ReadOnlySpan<int> dx = [1, 0, -1, 0];
        ReadOnlySpan<int> dy = [0, 1, 0, -1];
        for (int dispersal = 0; dispersal < events; dispersal++)
        {
            Array.Clear(_producerDispersalDelta);
            int offsetX = dx[_producerDispersalDirection];
            int offsetY = dy[_producerDispersalDirection];
            _producerDispersalDirection = (_producerDispersalDirection + 1) & 3;
            for (int y = 0; y < _gridSize; y++)
            for (int x = 0; x < _gridSize; x++)
            {
                int neighborX = x + offsetX, neighborY = y + offsetY;
                if (neighborY < 0)
                {
                    neighborY = -neighborY;
                    neighborX += _gridSize / 2;
                }
                else if (neighborY >= _gridSize)
                {
                    neighborY = (2 * (_gridSize - 1)) - neighborY;
                    neighborX += _gridSize / 2;
                }
                neighborX %= _gridSize;
                if (neighborX < 0) neighborX += _gridSize;
                int sourceIndex = Index(x, y), targetIndex = Index(neighborX, neighborY);
                bool land = _terrain[sourceIndex] >= 0.0;
                if ((_terrain[targetIndex] >= 0.0) != land || _producerCapacity[targetIndex] <= 0.0)
                    continue;
                double[] field = land ? _landPlants : _algae;
                double source = field[sourceIndex];
                double room = Math.Max(0.0, _producerCapacity[targetIndex] - field[targetIndex]);
                double transfer = Math.Min(source * (land ? 0.004 : 0.02), room * 0.02);
                if (transfer <= 0.0) continue;
                _producerDispersalDelta[sourceIndex] -= transfer;
                _producerDispersalDelta[targetIndex] += transfer;
            }
            for (int index = 0; index < _producerDispersalDelta.Length; index++)
            {
                if (_terrain[index] >= 0.0) _landPlants[index] += _producerDispersalDelta[index];
                else _algae[index] += _producerDispersalDelta[index];
            }
        }
    }

    public IReadOnlyDictionary<ulong, double> AllocateLightEnergy(
        IEnumerable<LightEnergyRequest> requests,
        double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        for (int index = 0; index < _lightEnergyBudget.Length; index++)
            _lightEnergyBudget[index] = Math.Max(
                0.0,
                (_light[index] * _lightEnergyFluxPerWorldAreaPerSecond * _cellAreas[index] * deltaSeconds) -
                _producerLightClaim[index]);

        Dictionary<ulong, double> allocations = [];
        var grouped = requests.GroupBy(request =>
        {
            CellQuad quad = Locate(request.Position);
            return new[]
            {
                (Index: quad.I00, Weight: quad.W00), (Index: quad.I10, Weight: quad.W10),
                (Index: quad.I01, Weight: quad.W01), (Index: quad.I11, Weight: quad.W11)
            }.MaxBy(item => item.Weight).Index;
        });
        foreach (IGrouping<int, LightEnergyRequest> column in grouped)
        {
            double remaining = _lightEnergyBudget[column.Key];
            foreach (IGrouping<int, LightEnergyRequest> layer in column
                         .GroupBy(request => (int)Math.Floor(request.Depth / 4.0f))
                         .OrderBy(layer => layer.Key))
            {
                double totalRequest = layer.Sum(request => Math.Max(0.0, request.RequestedEnergy));
                double fraction = totalRequest > 0.0 ? Math.Min(1.0, remaining / totalRequest) : 0.0;
                foreach (LightEnergyRequest request in layer.OrderBy(request => request.OrganismId))
                    allocations[request.OrganismId] = request.RequestedEnergy * fraction;
                remaining -= totalRequest * fraction;
                if (remaining <= 1e-15) break;
            }
            _lightEnergyBudget[column.Key] = Math.Max(0.0, remaining);
        }
        return allocations;
    }

    public double ApplyBrush(EnvironmentBrushCommand command)
    {
        command.Validate(_worldSize);
        double totalMatterDelta = 0.0;
        Vector2 brushCenter = SphericalWorld.Normalize(command.Position, _worldSize);
        for (int y = 0; y < _gridSize; y++)
        {
            float worldY = y * _worldSize / (_gridSize - 1f);
            for (int x = 0; x < _gridSize; x++)
            {
                float worldX = x * _worldSize / _gridSize;
                double distance = SphericalWorld.Distance(
                    brushCenter, new Vector2(worldX, worldY), _worldSize);
                if (distance >= command.Radius)
                    continue;

                double normalized = 1.0 - (distance / command.Radius);
                double falloff = normalized * normalized * (3.0 - (2.0 * normalized));
                int index = Index(x, y);
                if (command.Channel == EnvironmentBrushChannel.Minerals)
                {
                    double before = _minerals[index];
                    _minerals[index] = Math.Max(0.0, before + (command.Amount * falloff));
                    totalMatterDelta += _minerals[index] - before;
                }
                else
                {
                    _temperature[index] = Math.Clamp(
                        _temperature[index] + (command.Amount * falloff),
                        0.0,
                        2.0);
                }
            }
        }

        return totalMatterDelta;
    }

    public ulong ComputeResourceFingerprint()
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        Append(_minerals); Append(_landPlants); Append(_algae); Append(_edibleOrganics);
        Append(_detritus); Append(_metabolicWaste);
        return hash;

        void Append(double[] values)
        {
            foreach (double value in values)
            {
                hash ^= unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
                hash *= prime;
            }
        }
    }

    private int Index(int x, int y) => (y * _gridSize) + x;

    private void CalibrateSeaLevel(double targetOceanFraction, double radius)
    {
        int[] order = Enumerable.Range(0, _terrain.Length)
            .OrderBy(index => _terrain[index])
            .ToArray();
        double targetArea = Sum(_cellAreas) * targetOceanFraction;
        double accumulated = 0.0;
        double seaLevel = _terrain[order[^1]];
        foreach (int index in order)
        {
            accumulated += _cellAreas[index];
            seaLevel = _terrain[index];
            if (accumulated >= targetArea) break;
        }
        for (int index = 0; index < _terrain.Length; index++)
            _terrain[index] = Math.Clamp(_terrain[index] - seaLevel, -0.85 * radius, 0.35 * radius);
    }

    private void InitializeInventories(double initialMineralScale)
    {
        Array.Clear(_minerals);
        Array.Clear(_detritus);
        Array.Clear(_metabolicWaste);
        Array.Clear(_dissolvedOxygen);
        Array.Clear(_dissolvedOxygenCapacity);
        for (int index = 0; index < _terrain.Length; index++)
        {
            double variation = _habitatVariation[index];
            double areaScale = _cellAreas[index] / _referenceCellArea;
            double inland = SmoothStep(-1.5, 6.0, _terrain[index]);
            double coast = Math.Exp(-Math.Abs(_terrain[index]) / 3.5);
            double oceanFloor = 0.020 + (variation * 0.035);
            _minerals[index] = initialMineralScale * areaScale *
                (oceanFloor + (0.11 * coast) + (2.10 * inland));
            _detritus[index] = initialMineralScale * areaScale *
                ((0.018 * coast) + (0.16 * inland * (0.75 + (0.25 * variation))));

            _airOxygenCapacity[index] = 4.0 * areaScale;
            double airAvailability = Math.Clamp(0.30 + ((variation - 0.5) * 0.08), 0.22, 0.38);
            _airOxygen[index] = _airOxygenCapacity[index] * airAvailability;
            _airOxygenEquilibrium[index] = _airOxygen[index];
            double waterDepth = Math.Max(0.0, -_terrain[index]);
            if (waterDepth <= 0.0) continue;
            _dissolvedOxygenCapacity[index] = (0.85 + (0.025 * waterDepth)) * areaScale;
            double mixing = Math.Clamp(_flow[index].Length() * 4.0, 0.0, 0.55);
            double dissolvedAvailability = Math.Clamp(
                0.18 + (0.16 * mixing) + ((variation - 0.5) * 0.05), 0.08, 0.36);
            _dissolvedOxygen[index] = _dissolvedOxygenCapacity[index] * dissolvedAvailability;
        }
    }

    private double CalculateOceanAreaFraction()
    {
        double ocean = 0.0, total = 0.0;
        for (int index = 0; index < _terrain.Length; index++)
        {
            total += _cellAreas[index];
            if (_terrain[index] < 0.0) ocean += _cellAreas[index];
        }
        return total > 0.0 ? ocean / total : 0.0;
    }

    private void SeedProducers(double initialMineralScale)
    {
        // Shift a bounded one-time fraction of the rich interior store into the
        // ocean before seeding. This fixes the simulation scale mismatch where a
        // water cell held far less food than one small organism, without adding matter.
        double landMinerals = 0.0, waterLightWeight = 0.0;
        for (int index = 0; index < _minerals.Length; index++)
        {
            if (_terrain[index] >= 0.0) landMinerals += _minerals[index];
            else waterLightWeight += NearSurfaceAlgaeLight(index) * _cellAreas[index];
        }
        const double inlandTransferFraction = 0.12;
        double transfer = landMinerals * inlandTransferFraction;
        for (int index = 0; index < _minerals.Length; index++)
        {
            if (_terrain[index] >= 0.0)
                _minerals[index] *= 1.0 - inlandTransferFraction;
            else if (waterLightWeight > 0.0)
                _minerals[index] += transfer * NearSurfaceAlgaeLight(index) * _cellAreas[index] /
                    waterLightWeight;
        }

        for (int index = 0; index < _minerals.Length; index++)
        {
            double areaScale = _cellAreas[index] / _referenceCellArea;
            if (_terrain[index] >= 0.0)
            {
                double landSuitability = Math.Clamp(
                    (0.25 + (0.75 * _moisture[index])) * SmoothStep(0.0, 3.5, _terrain[index]),
                    0.0, 1.0);
                _producerCapacity[index] = initialMineralScale * areaScale * 2.2 * landSuitability;
                double seed = Math.Min(_minerals[index] * 0.18, _producerCapacity[index] * 0.055);
                _minerals[index] -= seed;
                _landPlants[index] = seed;
            }
            else
            {
                double algaeLight = NearSurfaceAlgaeLight(index);
                _producerCapacity[index] = initialMineralScale * areaScale * 0.60 * algaeLight;
                double seed = Math.Min(_minerals[index] * 0.62, _producerCapacity[index] * 0.15);
                _minerals[index] -= seed;
                _algae[index] = seed;
            }
        }
    }

    private double NearSurfaceAlgaeLight(int index)
    {
        double waterDepth = Math.Max(0.0, -_terrain[index]);
        if (waterDepth <= 0.0) return 0.0;
        // The algae pool represents plankton in the lit upper water column, rather
        // than a mat fixed to a potentially very deep seabed.
        double representativeDepth = Math.Min(1.5, Math.Max(0.15, waterDepth * 0.08));
        return Math.Clamp(_light[index] * Math.Exp(-representativeDepth * 0.065), 0.0, 1.0);
    }

    private static double SmoothStep(double minimum, double maximum, double value)
    {
        double t = Math.Clamp((value - minimum) / (maximum - minimum), 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    private static double SphericalNoise(
        Vector3 unit,
        ReadOnlySpan<Vector3> directions,
        ReadOnlySpan<double> phases,
        ReadOnlySpan<double> frequencies,
        ReadOnlySpan<double> weights)
    {
        double value = 0.0;
        for (int index = 0; index < directions.Length; index++)
            value += weights[index] * Math.Sin(
                frequencies[index] * Math.PI * Vector3.Dot(unit, directions[index]) + phases[index]);
        return Math.Clamp(value, -1.0, 1.0);
    }

    private static int DominantCell(CellQuad quad)
    {
        int index=quad.I00;double weight=quad.W00;
        if(quad.W10>weight){index=quad.I10;weight=quad.W10;}
        if(quad.W01>weight){index=quad.I01;weight=quad.W01;}
        if(quad.W11>weight)index=quad.I11;
        return index;
    }

    private CellQuad Locate(Vector2 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
            throw new ArgumentOutOfRangeException(nameof(position));

        Vector2 normalized = SphericalWorld.Normalize(position, _worldSize);
        double scaledX = normalized.X * _gridSize / _worldSize;
        double scaledY = normalized.Y * (_gridSize - 1) / _worldSize;
        int x0 = (int)Math.Floor(scaledX) % _gridSize;
        int y0 = (int)Math.Floor(scaledY);
        int x1 = (x0 + 1) % _gridSize;
        int y1 = Math.Min(y0 + 1, _gridSize - 1);
        double tx = scaledX - x0;
        double ty = scaledY - y0;
        double left = 1.0 - tx;
        double top = 1.0 - ty;

        return new CellQuad(
            Index(x0, y0), Index(x1, y0), Index(x0, y1), Index(x1, y1),
            left * top, tx * top, left * ty, tx * ty);
    }

    private static double Interpolate(double[] field, CellQuad quad) =>
        (field[quad.I00] * quad.W00) +
        (field[quad.I10] * quad.W10) +
        (field[quad.I01] * quad.W01) +
        (field[quad.I11] * quad.W11);

    private static Vector2 Interpolate(Vector2[] field, CellQuad quad) =>
        (field[quad.I00] * (float)quad.W00) +
        (field[quad.I10] * (float)quad.W10) +
        (field[quad.I01] * (float)quad.W01) +
        (field[quad.I11] * (float)quad.W11);

    private static double Availability(double[] inventory, double[] capacity, CellQuad quad)
    {
        double localCapacity = Interpolate(capacity, quad);
        return localCapacity > 1e-12
            ? Math.Clamp(Interpolate(inventory, quad) / localCapacity, 0.0, 1.0)
            : 0.0;
    }

    private static double Withdraw(double[] field, CellQuad quad, double requested)
    {
        double v00 = field[quad.I00];
        double v10 = field[quad.I10];
        double v01 = field[quad.I01];
        double v11 = field[quad.I11];
        double signal =
            (v00 * quad.W00) + (v10 * quad.W10) +
            (v01 * quad.W01) + (v11 * quad.W11);
        double amount = Math.Min(requested, signal);
        if (amount <= 0.0)
            return 0.0;

        Remove(field, quad.I00, amount * v00 * quad.W00 / signal);
        Remove(field, quad.I10, amount * v10 * quad.W10 / signal);
        Remove(field, quad.I01, amount * v01 * quad.W01 / signal);
        Remove(field, quad.I11, amount * v11 * quad.W11 / signal);
        return amount;
    }

    private static void Remove(double[] field, int index, double amount)
    {
        field[index] -= amount;
        if (field[index] < 0.0 && field[index] > -1e-12)
            field[index] = 0.0;
    }

    private static void Add(double[] field, int index, double amount) => field[index] += amount;

    private static void Deposit(double[] field, CellQuad quad, double amount)
    {
        Add(field, quad.I00, amount * quad.W00);
        Add(field, quad.I10, amount * quad.W10);
        Add(field, quad.I01, amount * quad.W01);
        Add(field, quad.I11, amount * quad.W11);
    }

    private static double Sum(double[] values)
    {
        double total = 0.0;
        foreach (double value in values)
            total += value;
        return total;
    }

    private static void Scale(double[] values, double scale)
    {
        for (int index = 0; index < values.Length; index++) values[index] *= scale;
    }

    private static void ValidateUnused(double unusedAmount, double reservedAmount)
    {
        if (!double.IsFinite(unusedAmount) || unusedAmount < 0.0 ||
            unusedAmount > reservedAmount + 1e-10)
            throw new ArgumentOutOfRangeException(nameof(unusedAmount));
    }

    private static void ValidateDeposit(double amount)
    {
        if (!double.IsFinite(amount) || amount < 0.0)
            throw new ArgumentOutOfRangeException(nameof(amount));
    }

    private readonly record struct CellQuad(
        int I00, int I10, int I01, int I11,
        double W00, double W10, double W01, double W11);
}
