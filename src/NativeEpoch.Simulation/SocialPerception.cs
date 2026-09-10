using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SocialCandidate(
    ulong Id,
    Vector2 Position,
    float Depth,
    float Radius,
    float Elevation);

public readonly record struct SocialPerceptionResult(
    double EnergySpent,
    int ActiveSensors,
    ulong TargetId,
    Vector2 TargetPosition,
    float TargetDepth,
    double ForwardSignal,
    double LateralSignal,
    double SizeRatio,
    double ApproachGain,
    double AvoidanceGain,
    double MemorySeconds)
{
    public double ActivationGain { get; init; }
}

/// <summary>
/// Bounded outline perception for nearby organisms. This is a local contrast
/// receptor, not image recognition: it observes direction and apparent size
/// without reading another organism's genome, energy or intent.
/// </summary>
public static class SocialPerception
{
    private const int MaximumCandidates = 8;
    private const int LineOfSightSamples = 7;
    private const double MaximumRange = 12.0;

    public static SocialPerceptionResult Evaluate(
        DevelopingBody body,
        Genome genome,
        IEnvironmentField environment,
        Vector2 position,
        float depth,
        double headingRadians,
        IReadOnlyList<SocialCandidate> candidates,
        SimulationConfig config,
        double deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(genome);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (!double.IsFinite(headingRadians) || !float.IsFinite(depth) || depth < 0f)
            throw new ArgumentOutOfRangeException(nameof(depth));

        int candidateCount = Math.Min(MaximumCandidates, candidates.Count);
        double energySpent = 0.0;
        int activeSensors = 0;
        Observation best = default;
        bool hasBest = false;
        double headingCos = Math.Cos(headingRadians);
        double headingSin = Math.Sin(headingRadians);
        double ownRadius = Math.Max(0.04, body.Cache.BoundingRadius);

        for (int sensorIndex = 0; sensorIndex < genome.Sensors.Count; sensorIndex++)
        {
            SensorGene sensor = genome.Sensors[sensorIndex];
            if (sensor.Channel != SensorChannel.OrganismContrast) continue;
            int regionIndex = body.IndexOfRegion(sensor.SourceRegionId);
            if (regionIndex < 0 || sensor.TargetControllerNodeIndex < 0 ||
                sensor.TargetControllerNodeIndex >= genome.ControllerNodes.Count)
                continue;

            BodyRegion region = body.Regions[regionIndex];
            RegionGene sourceGene = genome.GetRegion(region.RegionId);
            BodyFunctionalGeometry geometry = body.FunctionalGeometry[regionIndex];
            ControllerNodeGene targetNode = genome.ControllerNodes[sensor.TargetControllerNodeIndex];
            double turnConnection = Math.Tanh(targetNode.LateralContractionOutputWeight);
            double activationConnection = Math.Tanh(targetNode.ContractionOutputWeight);
            double motorConnection = Math.Max(Math.Abs(turnConnection), Math.Abs(activationConnection));
            double availability = Math.Clamp(
                region.SensoryExpression * geometry.ExposureFraction *
                geometry.SignalTransportEfficiency * sourceGene.LightReactivity *
                motorConnection,
                0.0,
                1.0);
            double range = Math.Clamp(sensor.Range, 0.0, MaximumRange);
            if (availability <= 1e-12 || range <= 1e-9) continue;

            double localX = (geometry.LocalCenter.X * headingCos) -
                (geometry.LocalCenter.Y * headingSin);
            double localY = (geometry.LocalCenter.X * headingSin) +
                (geometry.LocalCenter.Y * headingCos);
            Vector2 sourcePosition = SphericalWorld.OffsetPosition(
                position, new Vector2((float)localX, (float)localY), config.WorldSize);
            EnvironmentSample sourceSample = environment.Sample(sourcePosition, depth);
            double sourceElevation = sourceSample.WaterDepth > 0.0
                ? sourceSample.WaterSurface - depth
                : sourceSample.TerrainHeight + Math.Max(0.04, ownRadius * 0.20);
            double receptorAngle = headingRadians +
                Math.Atan2(geometry.Direction.Y, geometry.Direction.X) +
                sensor.DirectionOffsetRadians;
            double selectivity = Math.Clamp(sensor.DirectionalSelectivity, 0.0, 1.0);
            double halfFieldOfView = Math.PI * (1.0 - (0.80 * selectivity));

            CandidateView selected = default;
            bool hasSelected = false;
            for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
            {
                SocialCandidate candidate = candidates[candidateIndex];
                if (candidate.Id == 0 || !Valid(candidate)) continue;
                Vector2 delta = SphericalWorld.Delta(sourcePosition, candidate.Position, config.WorldSize);
                double horizontalDistance = delta.Length();
                double verticalDistance = candidate.Elevation - sourceElevation;
                double distance = Math.Sqrt(
                    (horizontalDistance * horizontalDistance) + (verticalDistance * verticalDistance));
                if (distance > range || distance <= 1e-8) continue;
                double bearing = Math.Atan2(delta.Y, delta.X);
                double receptorOffset = NormalizeAngle(bearing - receptorAngle);
                double absoluteOffset = Math.Abs(receptorOffset);
                if (absoluteOffset > halfFieldOfView) continue;
                double viewWeight = Math.Cos(absoluteOffset * Math.PI / (2.0 * halfFieldOfView));
                double rangeWeight = Smoother(1.0 - (distance / range));
                double apparent = Math.Clamp(candidate.Radius / Math.Max(candidate.Radius, distance), 0.0, 1.0);
                double salience = viewWeight * (0.30 + (0.70 * rangeWeight)) *
                    (0.20 + (0.80 * apparent));
                if (hasSelected && salience <= selected.Salience) continue;
                selected = new CandidateView(candidate, delta, distance, salience);
                hasSelected = true;
            }

            double requested = config.SensorEnergyPerSlotPerSecond * deltaSeconds * availability *
                (0.75 + (0.25 * (range / MaximumRange))) *
                (0.40 + (0.60 * (hasSelected ? selected.Salience : 0.0)));
            double paid = body.ConsumeRegionEnergy(region.RegionId, requested);
            energySpent += paid;
            double paidFraction = requested > 1e-12 ? Math.Clamp(paid / requested, 0.0, 1.0) : 0.0;
            if (!hasSelected || paidFraction <= 1e-9) continue;

            double visibility = Visibility(
                environment, sourcePosition, depth, sourceElevation, selected, sourceSample, config);
            double signalStrength = selected.Salience * visibility * availability * paidFraction *
                sensor.Gain;
            double selectionScore = Math.Abs(signalStrength);
            if (selectionScore <= 1e-10) continue;
            if (availability * paidFraction > 0.05) activeSensors++;
            if (hasBest && selectionScore <= best.Score) continue;

            double bodyBearing = NormalizeAngle(
                Math.Atan2(selected.Delta.Y, selected.Delta.X) - headingRadians);
            double sizeRatio = selected.Candidate.Radius / ownRadius;
            best = new Observation(
                selected.Candidate,
                selectionScore,
                signalStrength * Math.Cos(bodyBearing),
                signalStrength * Math.Sin(bodyBearing),
                sizeRatio,
                signalStrength * sensor.ApproachWeight * turnConnection,
                signalStrength * sensor.AvoidanceWeight * turnConnection,
                signalStrength * activationConnection,
                sensor.TargetMemorySeconds);
            hasBest = true;
        }

        if (!hasBest)
            return new SocialPerceptionResult(energySpent, activeSensors, 0, default, 0f,
                0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
        return new SocialPerceptionResult(
            energySpent,
            activeSensors,
            best.Candidate.Id,
            best.Candidate.Position,
            best.Candidate.Depth,
            best.Forward,
            best.Lateral,
            best.SizeRatio,
            best.Approach,
            best.Avoidance,
            best.MemorySeconds)
        {
            ActivationGain = best.Activation
        };
    }

    private static double Visibility(
        IEnvironmentField environment,
        Vector2 sourcePosition,
        float sourceDepth,
        double sourceElevation,
        CandidateView view,
        EnvironmentSample sourceSample,
        SimulationConfig config)
    {
        double ambient = Math.Clamp(sourceSample.Light, 0.0, 1.0);
        if (ambient <= 0.015) return 0.0;
        for (int step = 1; step <= LineOfSightSamples; step++)
        {
            double fraction = step / (double)(LineOfSightSamples + 1);
            Vector2 rayPosition = SphericalWorld.OffsetPosition(
                sourcePosition, view.Delta * (float)fraction, config.WorldSize);
            float rayDepth = (float)(sourceDepth +
                ((view.Candidate.Depth - sourceDepth) * fraction));
            EnvironmentSample raySample = environment.Sample(rayPosition, Math.Max(0f, rayDepth));
            double rayElevation = sourceElevation +
                ((view.Candidate.Elevation - sourceElevation) * fraction);
            if (raySample.TerrainHeight > rayElevation + 0.015) return 0.0;
            ambient = Math.Min(ambient, Math.Clamp(raySample.Light, 0.0, 1.0));
        }
        double waterAttenuation = sourceSample.WaterDepth > 0.0
            ? Math.Exp(-view.Distance * (0.055 + (0.004 * sourceDepth)))
            : 1.0;
        return Math.Clamp(ambient * waterAttenuation, 0.0, 1.0);
    }

    private static bool Valid(SocialCandidate candidate) =>
        float.IsFinite(candidate.Position.X) && float.IsFinite(candidate.Position.Y) &&
        float.IsFinite(candidate.Depth) && candidate.Depth >= 0f &&
        float.IsFinite(candidate.Radius) && candidate.Radius > 0f &&
        float.IsFinite(candidate.Elevation);

    private static double Smoother(double value)
    {
        double clamped = Math.Clamp(value, 0.0, 1.0);
        return clamped * clamped * (3.0 - (2.0 * clamped));
    }

    private static double NormalizeAngle(double angle) =>
        Math.Atan2(Math.Sin(angle), Math.Cos(angle));

    private readonly record struct CandidateView(
        SocialCandidate Candidate,
        Vector2 Delta,
        double Distance,
        double Salience);

    private readonly record struct Observation(
        SocialCandidate Candidate,
        double Score,
        double Forward,
        double Lateral,
        double SizeRatio,
        double Approach,
        double Avoidance,
        double Activation,
        double MemorySeconds);
}
