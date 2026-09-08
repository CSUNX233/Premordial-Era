using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct EnvironmentSample(
    double TerrainHeight,
    double WaterSurface,
    double WaterDepth,
    double Temperature,
    double Light,
    double Minerals,
    double Detritus,
    double Moisture,
    Vector2 Flow);

public interface IEnvironmentField
{
    EnvironmentSample Sample(Vector2 position, float depth = 0f);
}

public interface IMutableEnvironmentField : IEnvironmentField
{
    double WithdrawMatter(Vector2 position, double requestedAmount);
    void DepositDetritus(Vector2 position, double amount);
    double TotalMinerals { get; }
    double TotalDetritus { get; }
    bool AllFinite { get; }
    double ApplyBrush(EnvironmentBrushCommand command);
}

public enum EnvironmentBrushChannel
{
    Minerals,
    Temperature
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
    private readonly double[] _moisture;

    public BilinearEnvironmentField(SimulationConfig config, DeterministicRandom random)
    {
        _gridSize = config.EnvironmentGridSize;
        _worldSize = config.WorldSize;
        int count = checked(_gridSize * _gridSize);
        _terrain = new double[count];
        _temperature = new double[count];
        _light = new double[count];
        _minerals = new double[count];
        _detritus = new double[count];
        _moisture = new double[count];

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

                _terrain[index] = 8.0 - (18.0 * radial) + ((variation - 0.5) * 2.0);
                _temperature[index] = 0.45 + (0.45 * (1.0 - Math.Abs(centerY))) + (variation * 0.05);
                _light[index] = 0.50 + (0.35 * (1.0 - normalizedY)) + (variation * 0.10);
                _minerals[index] = 4.0 + (variation * 4.0) + (Math.Max(0.0, 1.0 - radial) * 2.0);
                _detritus[index] = 0.0;
                _moisture[index] = Math.Clamp(1.15 - radial, 0.15, 1.0);
            }
        }
    }

    public double TotalMinerals => Sum(_minerals);
    public double TotalDetritus => Sum(_detritus);

    public bool AllFinite =>
        _terrain.All(double.IsFinite) &&
        _temperature.All(value => double.IsFinite(value) && value >= 0.0) &&
        _light.All(value => double.IsFinite(value) && value >= 0.0) &&
        _minerals.All(value => double.IsFinite(value) && value >= 0.0) &&
        _detritus.All(value => double.IsFinite(value) && value >= 0.0) &&
        _moisture.All(value => double.IsFinite(value) && value >= 0.0);

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
            Interpolate(_temperature, quad) - (legalDepth * 0.002),
            lightAtSurface * Math.Exp(-legalDepth * 0.06),
            Interpolate(_minerals, quad),
            Interpolate(_detritus, quad),
            Interpolate(_moisture, quad),
            Vector2.Zero);
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
