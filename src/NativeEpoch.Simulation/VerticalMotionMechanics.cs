namespace NativeEpoch.Simulation;

internal readonly record struct VerticalMotionResult(
    double EnergySpent,
    double RequestedSurfaceSpeed,
    double PoweredSurfaceSpeed,
    double ActiveAcceleration);

/// <summary>
/// Resolves the radial component of the same paid, exposed-surface actuator used
/// by planar swimming. Positive velocity moves toward the water surface.
/// </summary>
internal static class VerticalMotionMechanics
{
    public static VerticalMotionResult Step(
        DevelopingBody body,
        Genome genome,
        ControllerOutputs outputs,
        ref float depth,
        ref float verticalVelocity,
        double waterDepth,
        float verticalHalfExtent,
        double immersion,
        double hydration,
        SimulationConfig config,
        double deltaSeconds)
    {
        if (!double.IsFinite(waterDepth) || waterDepth <= 0.0 ||
            !float.IsFinite(verticalHalfExtent) || verticalHalfExtent < 0f ||
            !double.IsFinite(immersion) || !double.IsFinite(hydration) ||
            !double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(waterDepth));

        float half = Math.Min(verticalHalfExtent, (float)waterDepth * 0.5f);
        float minimumDepth = half;
        float maximumDepth = (float)Math.Max(half, waterDepth - half);
        const float boundaryTolerance = 1e-5f;

        if (depth <= minimumDepth + boundaryTolerance && verticalVelocity > 0f)
            verticalVelocity = 0f;
        if (depth >= maximumDepth - boundaryTolerance && verticalVelocity < 0f)
            verticalVelocity = 0f;

        double command = Math.Clamp(outputs.VerticalContraction, -1.0, 1.0);
        if (maximumDepth - minimumDepth <= boundaryTolerance ||
            (command > 0.0 && depth <= minimumDepth + boundaryTolerance) ||
            (command < 0.0 && depth >= maximumDepth - boundaryTolerance))
            command = 0.0;

        double requestedSpeedSum = 0.0;
        double poweredSpeedSum = 0.0;
        double dragWeightSum = 0.0;
        double energySpent = 0.0;
        double mediumCoupling = Math.Clamp(immersion, 0.0, 1.0);
        if (Math.Abs(command) > 1e-12 && body.TotalEnergy > 1e-12)
        {
            for (int index = 0; index < body.Regions.Count; index++)
            {
                BodyRegion region = body.Regions[index];
                RegionGene gene = genome.GetRegion(region.RegionId);
                BodyFunctionalGeometry functional = body.FunctionalGeometry[index];
                double dragWeight = Math.Max(0.05, functional.ExposedSurface) *
                    (0.45 + (0.55 * gene.Density));
                dragWeightSum += dragWeight;

                double requestedSurfaceSpeed = config.ActiveSurfaceDriveSpeed *
                    Math.Sqrt(Math.Clamp(region.Activation, 0.0, 1.0)) *
                    (0.25 + (0.75 * gene.Permeability)) *
                    Math.Sqrt(Math.Clamp(functional.ExposureFraction, 0.0, 1.0)) *
                    mediumCoupling * command;
                double requestedWork = config.ActiveSurfaceDriveEnergyScale *
                    requestedSurfaceSpeed * requestedSurfaceSpeed *
                    Math.Max(0.05, functional.ExposedSurface) * deltaSeconds;
                double paid = requestedWork > 0.0
                    ? body.ConsumeRegionEnergy(region.RegionId, requestedWork)
                    : 0.0;
                double paidFraction = requestedWork > 1e-12
                    ? Math.Sqrt(Math.Clamp(paid / requestedWork, 0.0, 1.0))
                    : 1.0;
                requestedSpeedSum += requestedSurfaceSpeed * dragWeight;
                poweredSpeedSum += requestedSurfaceSpeed * paidFraction * dragWeight;
                energySpent += paid;
            }
        }

        double requestedSurface = dragWeightSum > 1e-12
            ? requestedSpeedSum / dragWeightSum
            : 0.0;
        double poweredSurface = dragWeightSum > 1e-12
            ? poweredSpeedSum / dragWeightSum
            : 0.0;
        double mobility = SimulationWorld.MediumMobility(immersion, hydration,
            genome.Metabolism.WaterRetention);
        double maximumSpeed = config.MaximumMovementSpeed * mobility /
            (1.0 + (0.08 * body.Cache.Drag / Math.Max(0.1, body.Cache.PhysicalMass)));
        double targetVelocity = Math.Clamp(poweredSurface, -maximumSpeed, maximumSpeed);
        double authority = maximumSpeed > 1e-12
            ? Math.Clamp(Math.Abs(targetVelocity) / maximumSpeed, 0.0, 1.0)
            : 0.0;
        double maximumChange = config.VerticalContractionAcceleration * mobility * authority * deltaSeconds;
        double activeChange = Math.Clamp(targetVelocity - verticalVelocity, -maximumChange, maximumChange);
        verticalVelocity += (float)activeChange;

        double buoyancyRatio = body.Cache.Buoyancy / Math.Max(0.1, body.Cache.PhysicalMass);
        verticalVelocity += (float)((buoyancyRatio - 1.0) *
            config.BuoyancyAccelerationScale * deltaSeconds);
        verticalVelocity *= (float)Math.Exp(-config.WaterVerticalDampingPerSecond * deltaSeconds);
        verticalVelocity = Math.Clamp(verticalVelocity, (float)-maximumSpeed, (float)maximumSpeed);

        float unconstrainedDepth = depth - verticalVelocity * (float)deltaSeconds;
        depth = Math.Clamp(unconstrainedDepth, minimumDepth, maximumDepth);
        if ((depth <= minimumDepth + boundaryTolerance && verticalVelocity > 0f) ||
            (depth >= maximumDepth - boundaryTolerance && verticalVelocity < 0f))
            verticalVelocity = 0f;

        return new VerticalMotionResult(energySpent, Math.Abs(requestedSurface),
            Math.Abs(poweredSurface), activeChange / deltaSeconds);
    }
}
