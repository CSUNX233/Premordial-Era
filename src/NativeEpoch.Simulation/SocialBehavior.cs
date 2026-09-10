using System.Numerics;

namespace NativeEpoch.Simulation;

public enum SocialResponse { None, Approach, Avoid, InjuryAvoidance }

public readonly record struct SocialMotorResponse(
    double Steering, double Activation, double EnergySpent, SocialResponse Mode, ulong TargetId);

/// <summary>Only remembered observations; never reads a target's live position.</summary>
public sealed class SocialMemory
{
    public ulong TargetId { get; private set; }
    public Vector2 LastSeenPosition { get; private set; }
    public float LastSeenDepth { get; private set; }
    public double SightAge { get; private set; } = 100;
    public double MemorySeconds { get; private set; }
    public double SizeRatio { get; private set; }
    public double ApproachGain { get; private set; }
    public double AvoidanceGain { get; private set; }
    public double ActivationGain { get; private set; }
    public double Confidence { get; private set; }
    public ulong InjurySourceId { get; private set; }
    public Vector2 InjuryPosition { get; private set; }
    public double InjuryAge { get; private set; } = 100;
    public double InjuryStrength { get; private set; }

    public bool AllFinite => float.IsFinite(LastSeenPosition.X) && float.IsFinite(LastSeenPosition.Y) &&
        float.IsFinite(LastSeenDepth) && double.IsFinite(SightAge) && double.IsFinite(MemorySeconds) &&
        double.IsFinite(SizeRatio) && double.IsFinite(ApproachGain) && double.IsFinite(AvoidanceGain) &&
        double.IsFinite(ActivationGain) && double.IsFinite(Confidence) && double.IsFinite(InjuryAge) &&
        double.IsFinite(InjuryStrength) && float.IsFinite(InjuryPosition.X) && float.IsFinite(InjuryPosition.Y);

    public void AddFingerprint(ref ulong hash)
    {
        FingerprintHash.Add(ref hash, TargetId);
        FingerprintHash.Add(ref hash, InjurySourceId);
        foreach(double value in new double[] { LastSeenPosition.X, LastSeenPosition.Y, LastSeenDepth,
            SightAge, MemorySeconds, SizeRatio, ApproachGain, AvoidanceGain, ActivationGain,
            Confidence, InjuryPosition.X, InjuryPosition.Y, InjuryAge, InjuryStrength })
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
    }

    public void RecordInjury(ulong sourceId, Vector2 position, double damage)
    {
        if (damage <= 0) return;
        InjurySourceId = sourceId; InjuryPosition = position; InjuryAge = 0;
        InjuryStrength = Math.Clamp(InjuryStrength + Math.Sqrt(damage) * 8, 0, 1);
    }

    public void Advance(double dt)
    {
        SightAge = Math.Min(100, SightAge + dt);
        InjuryAge = Math.Min(100, InjuryAge + dt);
        InjuryStrength *= Math.Exp(-dt * 0.5);
        if (SightAge > MemorySeconds) TargetId = 0;
    }

    public void Observe(SocialPerceptionResult sight)
    {
        if (sight.TargetId == 0) return;
        // Brief hysteresis prevents alternating equally visible individuals
        // every sample. The old position is never refreshed by a rejected sight.
        if (TargetId != 0 && sight.TargetId != TargetId && SightAge < Math.Min(0.6, MemorySeconds)) return;
        TargetId = sight.TargetId; LastSeenPosition = sight.TargetPosition; LastSeenDepth = sight.TargetDepth;
        SightAge = 0; MemorySeconds = sight.MemorySeconds; SizeRatio = sight.SizeRatio;
        ApproachGain = sight.ApproachGain; AvoidanceGain = sight.AvoidanceGain;
        ActivationGain = sight.ActivationGain;
        Confidence = Math.Clamp(Math.Sqrt(sight.ForwardSignal * sight.ForwardSignal +
            sight.LateralSignal * sight.LateralSignal), 0, 1);
    }

    public SocialMotorResponse Respond(DevelopingBody body, Genome genome, Vector2 position,
        double heading, SimulationConfig config, double dt)
    {
        if ((TargetId == 0 || SightAge > MemorySeconds) &&
            (InjurySourceId == 0 || InjuryAge > 8.0))
            return default;
        if (body.TotalEnergy <= config.MaximumEnergy * config.ForagingRestEnergyFraction)
            return default;
        Vector2 desired = Vector2.Zero;
        double activation = 0, spent = 0;
        SocialResponse mode = SocialResponse.None;
        ulong responseId = 0;
        if (TargetId != 0 && SightAge <= MemorySeconds)
        {
            double availability = 0;
            foreach (SensorGene sensor in genome.Sensors)
            {
                if (sensor.Channel != SensorChannel.OrganismContrast) continue;
                int index = body.IndexOfRegion(sensor.SourceRegionId);
                if (index < 0) continue;
                BodyRegion region = body.Regions[index];
                BodyFunctionalGeometry geometry = body.FunctionalGeometry[index];
                RegionGene sourceGene = genome.GetRegion(region.RegionId);
                ControllerNodeGene node = genome.ControllerNodes[sensor.TargetControllerNodeIndex];
                double motorConnection = Math.Max(
                    Math.Abs(Math.Tanh(node.LateralContractionOutputWeight)),
                    Math.Abs(Math.Tanh(node.ContractionOutputWeight)));
                if (motorConnection <= 1e-9) continue;
                double access = region.SensoryExpression * geometry.ExposureFraction *
                    geometry.SignalTransportEfficiency * sourceGene.LightReactivity;
                double request = config.SensorEnergyPerSlotPerSecond * dt * access * 0.25;
                double paid = body.ConsumeRegionEnergy(region.RegionId, request);
                spent += paid;
                // The stored gain already contains optical access and salience.
                // Charge memory use without applying those attenuation terms twice.
                if (request > 1e-12) availability = Math.Max(availability, paid / request);
            }
            double capacity = 0;
            foreach (BodyRegion region in body.Regions)
                capacity += RegionalPhysiology.SubstrateCapacity(region, genome.GetRegion(region.RegionId));
            double appetite = Math.Clamp(1 - body.TotalSubstrate / Math.Max(0.01, capacity * 0.8), 0, 1);
            double feeding = Math.Clamp(RegionalPhysiology.FeedingCapacity(body, genome) * 8, 0, 1);
            double approach = ApproachGain * appetite * feeding * genome.Metabolism.AnimalFoodAffinity;
            double danger = Math.Clamp((SizeRatio - 0.8) / 2.0, 0, 1);
            double avoidance = AvoidanceGain * danger;
            double decay = Math.Max(0, 1 - SightAge / Math.Max(0.1, MemorySeconds));
            double drive = (approach - avoidance) * availability * decay;
            Vector2 bearing = LocalBearing(position, LastSeenPosition, heading, config.WorldSize);
            desired += bearing * (float)drive;
            activation += Math.Abs(drive) * ActivationGain;
            if (Math.Abs(drive) > 1e-5)
            {
                mode = drive > 0 ? SocialResponse.Approach : SocialResponse.Avoid;
                responseId = TargetId;
            }
        }
        foreach (SensorGene sensor in genome.Sensors)
        {
            if (sensor.Channel != SensorChannel.ContactPressure || InjurySourceId == 0 ||
                InjuryAge > Math.Max(dt, sensor.TargetMemorySeconds)) continue;
            int index = body.IndexOfRegion(sensor.SourceRegionId);
            if (index < 0) continue;
            BodyRegion region = body.Regions[index];
            BodyFunctionalGeometry geometry = body.FunctionalGeometry[index];
            ControllerNodeGene node = genome.ControllerNodes[sensor.TargetControllerNodeIndex];
            double access = region.SensoryExpression * geometry.ExposureFraction * geometry.SignalTransportEfficiency;
            double request = config.SensorEnergyPerSlotPerSecond * dt * access * (0.3 + InjuryStrength);
            double paid = body.ConsumeRegionEnergy(region.RegionId, request);
            spent += paid;
            double signal = request > 1e-12 ? access * paid / request * InjuryStrength *
                sensor.Gain * sensor.AvoidanceWeight : 0;
            double motor = Math.Tanh(node.LateralContractionOutputWeight);
            Vector2 away = -LocalBearing(position, InjuryPosition, heading, config.WorldSize);
            desired += away * (float)(signal * motor);
            activation += Math.Abs(signal) * Math.Tanh(node.ContractionOutputWeight);
            if (Math.Abs(signal * motor) > 1e-5)
            {
                mode = SocialResponse.InjuryAvoidance; responseId = InjurySourceId;
            }
        }
        double strength = Math.Clamp(desired.Length(), 0, 1);
        double turn = strength > 1e-8 ? Math.Clamp(Math.Atan2(desired.Y, desired.X), -1, 1) * strength : 0;
        return new(turn, Math.Clamp(activation, -1, 1), spent, mode, responseId);
    }

    private static Vector2 LocalBearing(Vector2 position, Vector2 target, double heading, float size)
    {
        Vector2 direction = SphericalWorld.Delta(position, target, size);
        if (direction.LengthSquared() < 1e-10f) return Vector2.Zero;
        direction = Vector2.Normalize(direction);
        double c = Math.Cos(heading), s = Math.Sin(heading);
        return new((float)(direction.X * c + direction.Y * s), (float)(-direction.X * s + direction.Y * c));
    }
}
