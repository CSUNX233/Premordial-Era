namespace NativeEpoch.Simulation;

/// <summary>
/// Fixed numerical integration budget. Balanced is the shipping profile;
/// Reference retains the previous surface quadrature for repeatable comparisons.
/// </summary>
public readonly record struct CorePrecisionProfile(
    int SurfaceSamplesPerRegion,
    int PoseGeometryIntervalSteps,
    int GrowthGeometryRefreshIntervalSteps)
{
    public static CorePrecisionProfile Balanced => new(14,2,2);
    public static CorePrecisionProfile Reference => new(16,1,1);

    public void Validate()
    {
        if(SurfaceSamplesPerRegion is <8 or >32)
            throw new ArgumentOutOfRangeException(nameof(SurfaceSamplesPerRegion));
        if(PoseGeometryIntervalSteps is <1 or >4)
            throw new ArgumentOutOfRangeException(nameof(PoseGeometryIntervalSteps));
        if(GrowthGeometryRefreshIntervalSteps is <1 or >4)
            throw new ArgumentOutOfRangeException(nameof(GrowthGeometryRefreshIntervalSteps));
    }
}
