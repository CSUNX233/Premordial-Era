using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SphericalBasis(Vector3 East, Vector3 Up, Vector3 South)
{
    public Vector3 ToWorld(Vector2 eastSouth) =>
        (East * eastSouth.X) + (South * eastSouth.Y);

    public Vector2 ToLocal(Vector3 tangent) =>
        new(Vector3.Dot(tangent, East), Vector3.Dot(tangent, South));
}

/// <summary>
/// Converts the simulation's compact longitude/colatitude map coordinates to a sphere.
/// Position X spans 2π longitude; Position Y spans π colatitude and increases southward.
/// Local Vector2 values are always physical metres east and south.
/// </summary>
public static class SphericalWorld
{
    private const double PositionEpsilon = 1e-10;

    public static double Radius(float worldSize)
    {
        ValidateSize(worldSize);
        return worldSize / Math.PI;
    }

    public static Vector2 Normalize(Vector2 position, float worldSize)
    {
        ValidateSize(worldSize);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
            throw new ArgumentOutOfRangeException(nameof(position));

        double size = worldSize;
        double x = position.X;
        double y = position.Y % (2.0 * size);
        if (y < 0.0) y += 2.0 * size;
        if (y > size)
        {
            y = (2.0 * size) - y;
            x += size * 0.5;
        }
        x %= size;
        if (x < 0.0) x += size;
        return new Vector2((float)x, (float)Math.Clamp(y, 0.0, size));
    }

    public static Vector3 ToUnit(Vector2 position, float worldSize)
    {
        Double3 unit = UnitDouble(position, worldSize);
        return new Vector3((float)unit.X, (float)unit.Y, (float)unit.Z);
    }

    public static Vector2 FromUnit(Vector3 direction, float worldSize)
    {
        ValidateSize(worldSize);
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) ||
            !float.IsFinite(direction.Z) || direction.LengthSquared() <= 1e-20f)
            throw new ArgumentOutOfRangeException(nameof(direction));
        double length = Math.Sqrt(
            ((double)direction.X * direction.X) + ((double)direction.Y * direction.Y) +
            ((double)direction.Z * direction.Z));
        double x = direction.X / length, y = direction.Y / length, z = direction.Z / length;
        double horizontal = Math.Sqrt((x * x) + (z * z));
        double theta = Math.Atan2(horizontal, y);
        if (theta <= PositionEpsilon || Math.PI - theta <= PositionEpsilon)
            return new Vector2(0f, theta <= PositionEpsilon ? 0f : worldSize);
        double longitude = Math.Atan2(x, z);
        double mapX = (longitude + Math.PI) * worldSize / Math.Tau;
        if (mapX >= worldSize) mapX -= worldSize;
        return new Vector2(
            (float)mapX,
            (float)(theta * worldSize / Math.PI));
    }

    public static SphericalBasis BasisAt(Vector2 position, float worldSize)
    {
        Vector2 normalized = Normalize(position, worldSize);
        double longitude = (normalized.X * Math.Tau / worldSize) - Math.PI;
        Vector3 up = ToUnit(normalized, worldSize);
        Vector3 east = Vector3.Normalize(new Vector3(
            (float)Math.Cos(longitude), 0f, (float)-Math.Sin(longitude)));
        Vector3 south = Vector3.Normalize(Vector3.Cross(east, up));
        return new SphericalBasis(east, up, south);
    }

    public static Vector2 Advance(
        Vector2 position,
        Vector2 eastSouthVelocity,
        double deltaSeconds,
        float worldSize)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0 ||
            !float.IsFinite(eastSouthVelocity.X) || !float.IsFinite(eastSouthVelocity.Y))
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        Vector2 start = Normalize(position, worldSize);
        double distance = eastSouthVelocity.Length() * deltaSeconds;
        if (Math.Abs(distance) <= 1e-12) return start;
        Double3 up = UnitDouble(start, worldSize);
        (Double3 east, Double3 south) = BasisDouble(start, worldSize);
        double speed = eastSouthVelocity.Length();
        Double3 tangent = ((east * eastSouthVelocity.X) + (south * eastSouthVelocity.Y)) / speed;
        double angle = distance / Radius(worldSize);
        Double3 destination = (up * Math.Cos(angle)) + (tangent * Math.Sin(angle));
        return FromDoubleUnit(destination, worldSize);
    }

    public static Vector2 OffsetPosition(Vector2 position, Vector2 eastSouthMetres, float worldSize) =>
        Advance(position, eastSouthMetres, 1.0, worldSize);

    public static Vector2 Delta(Vector2 from, Vector2 to, float worldSize)
    {
        Double3 first = UnitDouble(from, worldSize);
        Double3 second = UnitDouble(to, worldSize);
        double cosine = Math.Clamp(Double3.Dot(first, second), -1.0, 1.0);
        double sine = Double3.Cross(first, second).Length;
        double angle = Math.Atan2(sine, cosine);
        if (angle <= 1e-9) return Vector2.Zero;
        (Double3 east, Double3 south) = BasisDouble(from, worldSize);
        Double3 tangent = sine <= 1e-14 ? east : (second - (first * cosine)) / sine;
        double distance = Radius(worldSize) * angle;
        return new Vector2(
            (float)(Double3.Dot(tangent, east) * distance),
            (float)(Double3.Dot(tangent, south) * distance));
    }

    public static double Distance(Vector2 first, Vector2 second, float worldSize)
    {
        Double3 firstUnit = UnitDouble(first, worldSize);
        Double3 secondUnit = UnitDouble(second, worldSize);
        double dot = Math.Clamp(Double3.Dot(firstUnit, secondUnit), -1.0, 1.0);
        double cross = Double3.Cross(firstUnit, secondUnit).Length;
        return Radius(worldSize) * Math.Atan2(cross, dot);
    }

    public static Vector2 Transport(
        Vector2 from,
        Vector2 to,
        Vector2 eastSouthVector,
        float worldSize)
    {
        if (eastSouthVector.LengthSquared() <= 1e-20f) return Vector2.Zero;
        Double3 first = UnitDouble(from, worldSize);
        Double3 second = UnitDouble(to, worldSize);
        (Double3 east, Double3 south) = BasisDouble(from, worldSize);
        Double3 worldVector = (east * eastSouthVector.X) + (south * eastSouthVector.Y);
        Double3 axis = Double3.Cross(first, second);
        double sine = axis.Length;
        double cosine = Math.Clamp(Double3.Dot(first, second), -1.0, 1.0);
        if (sine > 1e-12)
        {
            axis /= sine;
            double angle = Math.Atan2(sine, cosine);
            worldVector = RotateAroundAxis(worldVector, axis, angle);
        }
        worldVector -= second * Double3.Dot(worldVector, second);
        (Double3 targetEast, Double3 targetSouth) = BasisDouble(to, worldSize);
        return new Vector2(
            (float)Double3.Dot(worldVector, targetEast),
            (float)Double3.Dot(worldVector, targetSouth));
    }

    public static Vector3 SurfacePoint(Vector2 position, double elevation, float worldSize) =>
        ToUnit(position, worldSize) * (float)(Radius(worldSize) + elevation);

    private static Double3 RotateAroundAxis(Double3 vector, Double3 axis, double angle) =>
        (vector * Math.Cos(angle)) +
        (Double3.Cross(axis, vector) * Math.Sin(angle)) +
        (axis * Double3.Dot(axis, vector) * (1.0 - Math.Cos(angle)));

    private static Double3 UnitDouble(Vector2 position, float worldSize)
    {
        Vector2 normalized = Normalize(position, worldSize);
        double theta = normalized.Y * Math.PI / worldSize;
        double longitude = (normalized.X * Math.Tau / worldSize) - Math.PI;
        double sinTheta = Math.Sin(theta);
        if (Math.Abs(sinTheta) <= 1e-15)
            return new Double3(0.0, Math.Cos(theta) >= 0.0 ? 1.0 : -1.0, 0.0);
        return new Double3(
            Math.Sin(longitude) * sinTheta,
            Math.Cos(theta),
            Math.Cos(longitude) * sinTheta);
    }

    private static (Double3 East, Double3 South) BasisDouble(Vector2 position, float worldSize)
    {
        Vector2 normalized = Normalize(position, worldSize);
        double longitude = (normalized.X * Math.Tau / worldSize) - Math.PI;
        Double3 east = new(Math.Cos(longitude), 0.0, -Math.Sin(longitude));
        return (east, Double3.Cross(east, UnitDouble(normalized, worldSize)));
    }

    private static Vector2 FromDoubleUnit(Double3 direction, float worldSize)
    {
        Double3 unit = direction / direction.Length;
        double theta = Math.Atan2(Math.Sqrt((unit.X * unit.X) + (unit.Z * unit.Z)), unit.Y);
        if (theta <= PositionEpsilon || Math.PI - theta <= PositionEpsilon)
            return new Vector2(0f, theta <= PositionEpsilon ? 0f : worldSize);
        double longitude = Math.Atan2(unit.X, unit.Z);
        double mapX = (longitude + Math.PI) * worldSize / Math.Tau;
        if (mapX >= worldSize) mapX -= worldSize;
        return new Vector2((float)mapX, (float)(theta * worldSize / Math.PI));
    }

    private readonly record struct Double3(double X, double Y, double Z)
    {
        public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));
        public static double Dot(Double3 a, Double3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
        public static Double3 Cross(Double3 a, Double3 b) => new(
            (a.Y * b.Z) - (a.Z * b.Y),
            (a.Z * b.X) - (a.X * b.Z),
            (a.X * b.Y) - (a.Y * b.X));
        public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Double3 operator *(Double3 value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
        public static Double3 operator /(Double3 value, double scale) => new(value.X / scale, value.Y / scale, value.Z / scale);
    }

    private static void ValidateSize(float worldSize)
    {
        if (!float.IsFinite(worldSize) || worldSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(worldSize));
    }
}
