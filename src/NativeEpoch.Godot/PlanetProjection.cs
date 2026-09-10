using Godot;
using NativeEpoch.Simulation;
using MapVector2 = System.Numerics.Vector2;
using SimVector3 = System.Numerics.Vector3;

namespace NativeEpoch.Godot;

/// <summary>
/// Shared equirectangular map projection for presentation and input. Map X is
/// longitude (eastward), map Y is colatitude (southward), and elevation is
/// measured outward from the sea-level sphere.
/// </summary>
public static class PlanetProjection
{
    public static float Radius(float worldSize) => (float)SphericalWorld.Radius(worldSize);

    public static Vector3 UnitNormal(MapVector2 mapPosition, float worldSize)
    {
        SimVector3 unit=SphericalWorld.ToUnit(mapPosition,worldSize);
        return new Vector3(unit.X,unit.Y,unit.Z);
    }

    public static Vector3 MapToWorld(MapVector2 mapPosition, float elevation, float worldSize) =>
        UnitNormal(mapPosition, worldSize) * (Radius(worldSize) + elevation);

    public static Basis BasisAt(MapVector2 mapPosition, float worldSize)
    {
        SphericalBasis source=SphericalWorld.BasisAt(mapPosition,worldSize);
        Vector3 normal=new(source.Up.X,source.Up.Y,source.Up.Z);
        Vector3 east=new(source.East.X,source.East.Y,source.East.Z);
        Vector3 south=new(source.South.X,source.South.Y,source.South.Z);
        return new Basis(east.Normalized(), normal.Normalized(), south.Normalized()).Orthonormalized();
    }

    public static Basis HeadingBasis(MapVector2 mapPosition, float headingRadians, float worldSize) =>
        BasisAt(mapPosition, worldSize) * new Basis(Vector3.Up, -headingRadians);

    public static Vector3 MapDirectionToWorld(
        MapVector2 mapPosition, MapVector2 mapDirection, float worldSize)
    {
        Basis basis = BasisAt(mapPosition, worldSize);
        Vector3 tangent = (basis.X * mapDirection.X) + (basis.Z * mapDirection.Y);
        return tangent.LengthSquared() > 1e-12f ? tangent.Normalized() : basis.Z;
    }

    public static MapVector2 WorldToMap(Vector3 worldPosition, float worldSize)
    {
        if (worldSize <= 0f || worldPosition.LengthSquared() <= 1e-12f)
            return new MapVector2(worldSize * 0.5f, worldSize * 0.5f);
        return SphericalWorld.FromUnit(
            new SimVector3(worldPosition.X,worldPosition.Y,worldPosition.Z),worldSize);
    }

    public static float SurfaceElevation(Vector3 worldPosition, float worldSize) =>
        worldPosition.Length() - Radius(worldSize);

    public static bool IsSurfaceVisible(
        MapVector2 mapPosition, Vector3 cameraPosition, float worldSize, float horizonMargin = 0.035f)
    {
        float cameraDistance = cameraPosition.Length();
        float radius = Radius(worldSize);
        // Below sea level the camera is inside the occluding sea sphere; the
        // screen/depth tests are the stable culling authority in this case.
        if (cameraDistance <= radius + 0.5f) return true;
        float threshold = Math.Clamp(radius / cameraDistance, 0f, 1f) - horizonMargin;
        return UnitNormal(mapPosition, worldSize).Dot(cameraPosition / cameraDistance) >= threshold;
    }

    public static bool TryIntersectSeaLevelRay(
        Vector3 rayOrigin, Vector3 rayDirection, float worldSize, out Vector3 hit)
    {
        hit = default;
        if (rayDirection.LengthSquared() <= 1e-12f) return false;
        Vector3 direction = rayDirection.Normalized();
        float radius = Radius(worldSize);
        float b = rayOrigin.Dot(direction);
        float c = rayOrigin.LengthSquared() - (radius * radius);
        float discriminant = (b * b) - c;
        if (discriminant < 0f) return false;
        float root = MathF.Sqrt(discriminant);
        float distance = -b - root;
        if (distance < 0f) distance = -b + root;
        if (distance < 0f) return false;
        hit = rayOrigin + (direction * distance);
        return true;
    }

    public static MapVector2 InterpolateMapPosition(
        MapVector2 from, MapVector2 to, float alpha, float worldSize)
    {
        if (worldSize <= 0f) return MapVector2.Lerp(from, to, alpha);
        Vector3 first=UnitNormal(from,worldSize);
        Vector3 second=UnitNormal(to,worldSize);
        Vector3 blended=first.Lerp(second,alpha);
        if(blended.LengthSquared()<=1e-10f)
            return alpha<0.5f
                ?SphericalWorld.Normalize(from,worldSize)
                :SphericalWorld.Normalize(to,worldSize);
        return WorldToMap(blended.Normalized(),worldSize);
    }
}
