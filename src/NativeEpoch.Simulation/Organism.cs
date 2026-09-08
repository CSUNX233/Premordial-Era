using System.Numerics;

namespace NativeEpoch.Simulation;

public struct Organism
{
    public ulong Id;
    public ulong ParentId;
    public Vector2 Position;
    public double AgeSeconds;
    public double Energy;
    public double BodyMatter;
    public double StoredMatter;
    public double ReproductionCooldownSeconds;

    public readonly bool AllFinite =>
        float.IsFinite(Position.X) &&
        float.IsFinite(Position.Y) &&
        double.IsFinite(AgeSeconds) &&
        double.IsFinite(Energy) &&
        double.IsFinite(BodyMatter) &&
        double.IsFinite(StoredMatter) &&
        double.IsFinite(ReproductionCooldownSeconds);
}
