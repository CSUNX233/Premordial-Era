using System.Numerics;

namespace NativeEpoch.Simulation;

public enum SurvivalReflexMode
{
    None,
    Probe,
    Retreat
}

public struct SurvivalReflexMemory
{
    public bool Initialized;
    public bool Active;
    public Vector2 PreviousPosition;
    public float PreviousDepth;
    public double PreviousStress;
    public double PreviousDamage;
    public double PreviousHydration;
    public double PreviousHypoxiaShortfall;
    public Vector2 HeldPlanarDirection;
    public double HeldVerticalDirection;
    public Vector2 ImprovingPlanarDirection;
    public double ImprovingVerticalDirection;
    public double DirectionLockSeconds;
    public double ImprovingDirectionSeconds;
    public double EvaluationStartStress;
    public double EvaluationSeconds;
    public int ProbeCount;
    public bool Recovering;
    public SurvivalReflexMode Mode;

    public readonly bool AllFinite =>
        float.IsFinite(PreviousPosition.X) && float.IsFinite(PreviousPosition.Y) &&
        float.IsFinite(PreviousDepth) && PreviousDepth >= 0f &&
        double.IsFinite(PreviousStress) && PreviousStress is >= 0.0 and <= 1.0 &&
        double.IsFinite(PreviousDamage) && PreviousDamage is >= 0.0 and <= 1.0 &&
        double.IsFinite(PreviousHydration) && PreviousHydration is >= 0.0 and <= 1.0 &&
        double.IsFinite(PreviousHypoxiaShortfall) && PreviousHypoxiaShortfall >= 0.0 &&
        float.IsFinite(HeldPlanarDirection.X) && float.IsFinite(HeldPlanarDirection.Y) &&
        double.IsFinite(HeldVerticalDirection) &&
        float.IsFinite(ImprovingPlanarDirection.X) && float.IsFinite(ImprovingPlanarDirection.Y) &&
        double.IsFinite(ImprovingVerticalDirection) &&
        double.IsFinite(DirectionLockSeconds) && DirectionLockSeconds >= 0.0 &&
        double.IsFinite(ImprovingDirectionSeconds) && ImprovingDirectionSeconds >= 0.0 &&
        double.IsFinite(EvaluationStartStress) && EvaluationStartStress is >= 0.0 and <= 1.0 &&
        double.IsFinite(EvaluationSeconds) && EvaluationSeconds >= 0.0 && ProbeCount >= 0 &&
        Enum.IsDefined(Mode);

    public readonly void AddFingerprint(ref ulong hash)
    {
        FingerprintHash.Add(ref hash, Initialized ? 1UL : 0UL);
        FingerprintHash.Add(ref hash, Active ? 1UL : 0UL);
        FingerprintHash.Add(ref hash, Recovering ? 1UL : 0UL);
        FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(PreviousPosition.X)));
        FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(PreviousPosition.Y)));
        FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(PreviousDepth)));
        foreach (double value in new[] { PreviousStress, PreviousDamage, PreviousHydration,
                     PreviousHypoxiaShortfall,HeldVerticalDirection,
                     ImprovingVerticalDirection, DirectionLockSeconds, ImprovingDirectionSeconds,
                     EvaluationStartStress, EvaluationSeconds })
            FingerprintHash.Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
        FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(HeldPlanarDirection.X)));
        FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(HeldPlanarDirection.Y)));
        FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(ImprovingPlanarDirection.X)));
        FingerprintHash.Add(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(ImprovingPlanarDirection.Y)));
        FingerprintHash.Add(ref hash, unchecked((ulong)Mode));
        FingerprintHash.Add(ref hash, unchecked((ulong)ProbeCount));
    }
}

public readonly record struct SurvivalReflexResponse(
    double Stress,
    bool Active,
    SurvivalReflexMode Mode,
    double Steering,
    double VerticalDirection,
    Vector2 PlanarDirection,
    bool Recovering)
{
    public bool AllFinite => double.IsFinite(Stress) && Stress is >= 0.0 and <= 1.0 &&
        double.IsFinite(Steering) && Steering is >= -1.0 and <= 1.0 &&
        double.IsFinite(VerticalDirection) && VerticalDirection is >= -1.0 and <= 1.0 &&
        float.IsFinite(PlanarDirection.X) && float.IsFinite(PlanarDirection.Y) &&
        Enum.IsDefined(Mode);
}

/// <summary>
/// A cheap interoceptive reflex. It remembers whether the organism's own recent
/// motion made physiological stress better or worse; it never samples a remote
/// environment position or stores a destination.
/// </summary>
public static class SurvivalReflex
{
    private const double EngageStress = 0.12;
    private const double ReleaseStress = 0.055;
    private const double TrendThreshold = 0.018;
    // A full reversal through the ordinary body mechanics takes several seconds.
    // Do not judge a trial while the body is still turning.
    private const double DirectionHoldSeconds = 15.0;
    private const double ImprovingMemorySeconds = 5.0;

    public static double MeasureStress(Organism organism, SimulationConfig config, double deltaSeconds)
    {
        double maximumAerobicDemand = config.MetabolicSubstratePerSecond *
            Math.Max(0.05, organism.Body.Cache.TotalMatter) * deltaSeconds;
        double hypoxia = Math.Clamp(organism.HypoxiaShortfallLastStep /
            Math.Max(1e-10, maximumAerobicDemand), 0.0, 1.0);
        // A partly used water reserve is not yet an emergency. Retention and
        // exchange traits affect the real inventory; do not equate the paid
        // cost of living in a medium with failure to survive in that medium.
        double dehydration = Math.Clamp((0.70 - organism.Hydration) / 0.50, 0.0, 1.0);
        double damageRise = organism.SurvivalMemory.Initialized
            ? Math.Clamp((organism.Body.AverageDamage - organism.SurvivalMemory.PreviousDamage) /
                Math.Max(1e-8, deltaSeconds / config.HypoxiaFailureSeconds), 0.0, 1.0)
            : 0.0;
        return Math.Clamp(Math.Max(Math.Max(hypoxia, 0.90 * dehydration),
            damageRise), 0.0, 1.0);
    }

    public static SurvivalReflexResponse Update(ref SurvivalReflexMemory memory,
        ulong organismId, Vector2 position, float depth, double headingRadians,
        Vector2 velocity, float verticalVelocity, double stress, double damage,
        double hydration,double hypoxiaShortfall,
        float worldSize, double deltaSeconds)
    {
        if (!double.IsFinite(stress) || stress is < 0.0 or > 1.0 ||
            !double.IsFinite(damage) || damage is < 0.0 or > 1.0 ||
            !double.IsFinite(hydration) || hydration is < 0.0 or > 1.0 ||
            !double.IsFinite(hypoxiaShortfall) || hypoxiaShortfall<0.0 ||
            !double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(stress));

        Vector2 actualPlanar = Vector2.Zero;
        double actualVertical = 0.0;
        if (memory.Initialized)
        {
            memory.HeldPlanarDirection = TransportDirection(memory.HeldPlanarDirection,
                memory.PreviousPosition, position, worldSize);
            memory.ImprovingPlanarDirection = TransportDirection(memory.ImprovingPlanarDirection,
                memory.PreviousPosition, position, worldSize);
            actualPlanar = SphericalWorld.Delta(memory.PreviousPosition, position, worldSize);
            actualPlanar = TransportDirection(actualPlanar, memory.PreviousPosition, position, worldSize);
            actualVertical = memory.PreviousDepth - depth;
        }

        memory.DirectionLockSeconds = Math.Max(0.0, memory.DirectionLockSeconds - deltaSeconds);
        memory.ImprovingDirectionSeconds = Math.Max(0.0, memory.ImprovingDirectionSeconds - deltaSeconds);
        if (memory.Active) memory.EvaluationSeconds += deltaSeconds;
        if (memory.ImprovingDirectionSeconds <= 0.0)
        {
            memory.ImprovingPlanarDirection = Vector2.Zero;
            memory.ImprovingVerticalDirection = 0.0;
        }

        bool wasActive = memory.Active;
        memory.Active = memory.Active ? stress > ReleaseStress : stress >= EngageStress;
        bool moved = actualPlanar.LengthSquared() > 1e-8f || Math.Abs(actualVertical) > 1e-5;
        double instantTrend=memory.Initialized?stress-memory.PreviousStress:0.0;
        bool physiologyImproving=memory.Initialized&&
            (hydration>memory.PreviousHydration+0.001||
             hypoxiaShortfall+1e-10<memory.PreviousHypoxiaShortfall);
        if(memory.Recovering&&instantTrend>0.004)memory.Recovering=false;

        if (!memory.Active)
        {
            memory.Mode = SurvivalReflexMode.None;
            memory.HeldPlanarDirection = Vector2.Zero;
            memory.HeldVerticalDirection = 0.0;
            memory.DirectionLockSeconds = 0.0;
            memory.Recovering = false;
            memory.EvaluationSeconds = 0.0;
            memory.EvaluationStartStress = stress;
        }
        else if (!wasActive)
        {
            memory.Recovering = false;
            if (moved)
                SetDirection(ref memory, -actualPlanar, -actualVertical,
                    SurvivalReflexMode.Retreat, stress);
            else
                SetProbe(ref memory, organismId, stress);
        }
        else if((instantTrend < -0.004||physiologyImproving) && moved)
        {
            // A clear immediate improvement is acted on before the long trend
            // window expires, so crossing back into a safe medium is not undone.
            SetDirection(ref memory, actualPlanar, actualVertical,
                SurvivalReflexMode.Retreat,stress);
            memory.ImprovingPlanarDirection=memory.HeldPlanarDirection;
            memory.ImprovingVerticalDirection=memory.HeldVerticalDirection;
            memory.ImprovingDirectionSeconds=ImprovingMemorySeconds;
            memory.Recovering=true;
        }
        else if (memory.DirectionLockSeconds <= 0.0 &&
                 memory.EvaluationSeconds >= DirectionHoldSeconds)
        {
            double windowTrend = stress - memory.EvaluationStartStress;
            if (windowTrend < -TrendThreshold && moved)
            {
                // Improvement is the only way to create or refresh a remembered safe direction.
                SetDirection(ref memory, actualPlanar, actualVertical,
                    SurvivalReflexMode.Retreat, stress);
                memory.ImprovingPlanarDirection = memory.HeldPlanarDirection;
                memory.ImprovingVerticalDirection = memory.HeldVerticalDirection;
                memory.ImprovingDirectionSeconds = ImprovingMemorySeconds;
                memory.Recovering = true;
            }
            else if (windowTrend > TrendThreshold && moved)
            {
                memory.Recovering = false;
                SetDirection(ref memory, -actualPlanar, -actualVertical,
                    SurvivalReflexMode.Retreat, stress);
            }
            else if (memory.ImprovingDirectionSeconds > 0.0 &&
                     memory.ImprovingPlanarDirection.LengthSquared() +
                         (float)(memory.ImprovingVerticalDirection * memory.ImprovingVerticalDirection) > 1e-8f)
                SetDirection(ref memory, memory.ImprovingPlanarDirection,
                    memory.ImprovingVerticalDirection, SurvivalReflexMode.Retreat, stress);
            else
            {
                memory.Recovering = false;
                SetProbe(ref memory, organismId, stress);
            }
        }

        memory.Initialized = true;
        memory.PreviousPosition = position;
        memory.PreviousDepth = depth;
        memory.PreviousStress = stress;
        memory.PreviousDamage = damage;
        memory.PreviousHydration=hydration;
        memory.PreviousHypoxiaShortfall=hypoxiaShortfall;

        double steering = 0.0;
        if (memory.Active && memory.HeldPlanarDirection.LengthSquared() > 1e-8f)
        {
            double target = Math.Atan2(memory.HeldPlanarDirection.Y, memory.HeldPlanarDirection.X);
            steering = Math.Clamp(NormalizeAngle(target - headingRadians) / (Math.PI * 0.65), -1.0, 1.0);
        }
        return new(stress, memory.Active, memory.Mode, steering,
            Math.Clamp(memory.HeldVerticalDirection, -1.0, 1.0),
            memory.Active ? memory.HeldPlanarDirection : Vector2.Zero,memory.Recovering);
    }

    public static ControllerOutputs Apply(ControllerOutputs output, SurvivalReflexResponse reflex)
    {
        if (!reflex.Active) return output;
        if(reflex.Recovering)
            return output with
            {
                ContractionActivation=Math.Clamp(output.ContractionActivation,0.16,0.28),
                LateralContraction=Math.Clamp((0.15*output.LateralContraction)+(0.85*reflex.Steering),-1.0,1.0),
                VerticalContraction=Math.Clamp((0.20*output.VerticalContraction)+(0.80*reflex.VerticalDirection),-1.0,1.0)
            };
        double urgency = Math.Clamp((reflex.Stress - ReleaseStress) /
            (1.0 - ReleaseStress), 0.0, 1.0);
        return output with
        {
            ContractionActivation = Math.Max(output.ContractionActivation, 0.62 + (0.38 * urgency)),
            LateralContraction = Math.Clamp((0.15 * output.LateralContraction) +
                ((0.85 + (0.15 * urgency)) * reflex.Steering), -1.0, 1.0),
            VerticalContraction = Math.Clamp((0.20 * output.VerticalContraction) +
                ((0.80 + (0.20 * urgency)) * reflex.VerticalDirection), -1.0, 1.0)
        };
    }

    private static void SetDirection(ref SurvivalReflexMemory memory, Vector2 planar,
        double vertical, SurvivalReflexMode mode, double stress)
    {
        memory.HeldPlanarDirection = planar.LengthSquared() > 1e-8f
            ? Vector2.Normalize(planar)
            : Vector2.Zero;
        memory.HeldVerticalDirection = Math.Abs(vertical) > 1e-5 ? Math.Sign(vertical) : 0.0;
        memory.DirectionLockSeconds = DirectionHoldSeconds;
        memory.EvaluationStartStress = stress;
        memory.EvaluationSeconds = 0.0;
        memory.Mode = mode;
    }

    private static void SetProbe(ref SurvivalReflexMemory memory, ulong organismId, double stress)
    {
        int probe = memory.ProbeCount++;
        double phase = ProbePhase(organismId, probe);
        SetDirection(ref memory,
            new Vector2((float)Math.Cos(phase), (float)Math.Sin(phase)),
            ((organismId + (ulong)probe) & 1UL) == 0 ? 1.0 : -1.0,
            SurvivalReflexMode.Probe, stress);
    }

    private static Vector2 TransportDirection(Vector2 direction, Vector2 from, Vector2 to, float worldSize)
    {
        if (direction.LengthSquared() <= 1e-8f) return Vector2.Zero;
        Vector2 transported = SphericalWorld.Transport(from, to, direction, worldSize);
        return transported.LengthSquared() > 1e-8f ? Vector2.Normalize(transported) : Vector2.Zero;
    }

    private static double ProbePhase(ulong id, int probe)
    {
        ulong mixed = (id + ((ulong)probe * 0xD1B54A32D192ED03UL)) * 0x9E3779B97F4A7C15UL;
        return (mixed >> 11) * (Math.Tau / (1UL << 53));
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > Math.PI) angle -= Math.Tau;
        while (angle < -Math.PI) angle += Math.Tau;
        return angle;
    }
}
