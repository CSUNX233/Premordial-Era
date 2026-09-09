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
    Vector2 Flow);

public interface IEnvironmentField
{
    EnvironmentSample Sample(Vector2 position, float depth = 0f);
}

public interface IMutableEnvironmentField : IEnvironmentField
{
    double WithdrawMatter(Vector2 position, double requestedAmount);
    IReadOnlyDictionary<ulong,MatterReservation> ReserveMatter(
        IEnumerable<MatterUptakeRequest> requests);
    void ReturnMatter(MatterReservation reservation,double unusedAmount);
    void DepositDetritus(Vector2 position, double amount);
    void DepositMetabolicWaste(Vector2 position, double amount);
    double WithdrawOxygen(Vector2 position, float depth, double immersion, double requestedAmount);
    void DepositOxygen(Vector2 position, float depth, double immersion, double amount);
    void UpdateOxygen(double deltaSeconds);
    void UpdateMatterCycles(double deltaSeconds);
    IReadOnlyDictionary<ulong, double> AllocateLightEnergy(
        IEnumerable<LightEnergyRequest> requests,
        double deltaSeconds);
    double TotalMinerals { get; }
    double TotalDetritus { get; }
    double TotalMetabolicWaste { get; }
    double TotalOxygen { get; }
    double CumulativeExternalOxygenSupply { get; }
    bool AllFinite { get; }
    double ApplyBrush(EnvironmentBrushCommand command);
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
/// Hidden regular samples with continuous bilinear queries. Simulation clients never
/// receive cell coordinates or direct access to the backing arrays.
/// </summary>
public sealed class BilinearEnvironmentField : IMutableEnvironmentField
{
    private readonly int _gridSize;
    private readonly float _worldSize;
    private readonly double[] _terrain;
    private readonly double[] _temperature;
    private readonly double[] _light;
    private readonly double[] _minerals;
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
    private readonly double _wasteRemineralizationPerSecond;
    private readonly double _lightEnergyFluxPerCell;

    public BilinearEnvironmentField(SimulationConfig config, DeterministicRandom random)
    {
        _gridSize = config.EnvironmentGridSize;
        _worldSize = config.WorldSize;
        _wasteRemineralizationPerSecond = config.WasteRemineralizationPerSecond;
        double cellLength = config.WorldSize / (config.EnvironmentGridSize - 1);
        _lightEnergyFluxPerCell = config.SurfaceLightEnergyPerWorldAreaPerSecond * cellLength * cellLength;
        int count = checked(_gridSize * _gridSize);
        _terrain = new double[count];
        _temperature = new double[count];
        _light = new double[count];
        _minerals = new double[count];
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

        for (int y = 0; y < _gridSize; y++)
        {
            double normalizedY = y / (double)(_gridSize - 1);
            for (int x = 0; x < _gridSize; x++)
            {
                double normalizedX = x / (double)(_gridSize - 1);
                double centerX = (normalizedX * 2.0) - 1.0;
                double centerY = (normalizedY * 2.0) - 1.0;
                double radial = Math.Sqrt((centerX * centerX) + (centerY * centerY));
                double variation = random.NextUnitDouble();
                int index = Index(x, y);

                // Continuous shelf-to-basin profile: readable 0-6 shallows, a broad
                // continental slope, then deep ocean. This is also the simulation height.
                _terrain[index] = 8.0 - (62.0 * Math.Pow(radial, 1.6)) +
                    ((variation - 0.5) * (1.2 + (1.3 * radial))) +
                    config.TerrainElevationOffset;
                _temperature[index] = 0.45 + (0.45 * (1.0 - Math.Abs(centerY))) + (variation * 0.05);
                _light[index] = 0.50 + (0.35 * (1.0 - normalizedY)) + (variation * 0.10);
                _minerals[index] = config.InitialMineralScale * (0.10 + (variation * 0.14) +
                    (Math.Max(0.0, 1.0 - radial) * 0.08));
                _detritus[index] = 0.0;
                _metabolicWaste[index] = 0.0;
                _moisture[index] = Math.Clamp(1.15 - radial, 0.15, 1.0);
                _flow[index] = new Vector2(
                    (float)(-centerY * (0.06 + (0.08 * variation))),
                    (float)(centerX * (0.06 + (0.08 * variation))));

                _airOxygenCapacity[index] = 4.0;
                double airAvailability = Math.Clamp(0.30 + ((variation - 0.5) * 0.08), 0.22, 0.38);
                _airOxygen[index] = _airOxygenCapacity[index] * airAvailability;
                _airOxygenEquilibrium[index] = _airOxygen[index];
                double waterDepth = Math.Max(0.0, -_terrain[index]);
                if (waterDepth > 0.0)
                {
                    _dissolvedOxygenCapacity[index] = 0.85 + (0.025 * waterDepth);
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
    }

    public double TotalMinerals => Sum(_minerals);
    internal void SetInitialMineralBudget(double budget)
    {
        if(!double.IsFinite(budget)||budget<0.0)
            throw new InvalidOperationException("The initial resource budget cannot fund this many founders.");
        double current=TotalMinerals;
        double scale=current>0.0?budget/current:0.0;
        for(int index=0;index<_minerals.Length;index++)_minerals[index]*=scale;
    }
    public double TotalDetritus => Sum(_detritus);
    public double TotalMetabolicWaste => Sum(_metabolicWaste);
    public double TotalOxygen => Sum(_dissolvedOxygen) + Sum(_airOxygen);
    public double CumulativeExternalOxygenSupply { get; private set; }

    public bool AllFinite =>
        _terrain.All(double.IsFinite) &&
        _temperature.All(value => double.IsFinite(value) && value >= 0.0) &&
        _light.All(value => double.IsFinite(value) && value >= 0.0) &&
        _minerals.All(value => double.IsFinite(value) && value >= 0.0) &&
        _detritus.All(value => double.IsFinite(value) && value >= 0.0) &&
        _metabolicWaste.All(value => double.IsFinite(value) && value >= 0.0) &&
        _moisture.All(value => double.IsFinite(value) && value >= 0.0) &&
        _dissolvedOxygen.All(value => double.IsFinite(value) && value >= 0.0) &&
        _airOxygen.All(value => double.IsFinite(value) && value >= 0.0) &&
        _lightEnergyBudget.All(value => double.IsFinite(value) && value >= 0.0) &&
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
            Interpolate(_flow, quad));
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
                double amount=request.RequestedMatter*fraction;
                MatterReservation reservation=new(request.OrganismId,group.Key,
                    amount*mineralShare,amount*(1.0-mineralShare));
                reservations[request.OrganismId]=reservation;
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
        if(unusedAmount<=0.0||reservation.Total<=0.0)return;
        double fraction=Math.Min(1.0,unusedAmount/reservation.Total);
        Add(_minerals,reservation.CellIndex,reservation.Minerals*fraction);
        Add(_detritus,reservation.CellIndex,reservation.Detritus*fraction);
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
        double fraction = 1.0 - Math.Exp(-_wasteRemineralizationPerSecond * deltaSeconds);
        for (int index = 0; index < _metabolicWaste.Length; index++)
        {
            double recycled = _metabolicWaste[index] * fraction;
            _metabolicWaste[index] -= recycled;
            _minerals[index] += recycled;
        }
    }

    public IReadOnlyDictionary<ulong, double> AllocateLightEnergy(
        IEnumerable<LightEnergyRequest> requests,
        double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        for (int index = 0; index < _lightEnergyBudget.Length; index++)
            _lightEnergyBudget[index] = _light[index] * _lightEnergyFluxPerCell * deltaSeconds;

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
        double spacing = _worldSize / (_gridSize - 1);
        int minimumX = Math.Max(0, (int)Math.Floor((command.Position.X - command.Radius) / spacing));
        int maximumX = Math.Min(_gridSize - 1, (int)Math.Ceiling((command.Position.X + command.Radius) / spacing));
        int minimumY = Math.Max(0, (int)Math.Floor((command.Position.Y - command.Radius) / spacing));
        int maximumY = Math.Min(_gridSize - 1, (int)Math.Ceiling((command.Position.Y + command.Radius) / spacing));

        for (int y = minimumY; y <= maximumY; y++)
        {
            double worldY = y * spacing;
            for (int x = minimumX; x <= maximumX; x++)
            {
                double worldX = x * spacing;
                double distance = Math.Sqrt(
                    Math.Pow(worldX - command.Position.X, 2.0) +
                    Math.Pow(worldY - command.Position.Y, 2.0));
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

    private int Index(int x, int y) => (y * _gridSize) + x;
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

        double scaledX = Math.Clamp(position.X, 0f, _worldSize) * (_gridSize - 1) / _worldSize;
        double scaledY = Math.Clamp(position.Y, 0f, _worldSize) * (_gridSize - 1) / _worldSize;
        int x0 = (int)Math.Floor(scaledX);
        int y0 = (int)Math.Floor(scaledY);
        int x1 = Math.Min(x0 + 1, _gridSize - 1);
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

    private readonly record struct CellQuad(
        int I00, int I10, int I01, int I11,
        double W00, double W10, double W01, double W11);
}
