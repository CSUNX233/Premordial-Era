using System.Numerics;

namespace NativeEpoch.Simulation;

public struct Organism
{
    public ulong Id;
    public ulong ParentId;
    public int GenomeId;
    public Vector2 Position;
    public Vector2 Velocity;
    public double HeadingRadians;
    public double AgeSeconds;
    public double Maturity;
    public double Energy;
    public double StoredMatter;
    public double ReproductionCooldownSeconds;
    public DevelopingBody Body;

    public readonly bool AllFinite =>
        float.IsFinite(Position.X) &&
        float.IsFinite(Position.Y) &&
        float.IsFinite(Velocity.X) &&
        float.IsFinite(Velocity.Y) &&
        double.IsFinite(HeadingRadians) &&
        double.IsFinite(AgeSeconds) &&
        double.IsFinite(Maturity) &&
        double.IsFinite(Energy) &&
        double.IsFinite(StoredMatter) &&
        double.IsFinite(ReproductionCooldownSeconds) &&
        Maturity is >= 0.0 and <= 1.0 &&
        Body is not null;
}
