namespace NativeEpoch.Simulation;

/// <summary>A small PCG32 stream with explicit seed and stream selection.</summary>
public sealed class DeterministicRandom
{
    private ulong _state;
    private readonly ulong _increment;

    public DeterministicRandom(ulong seed, ulong stream)
    {
        _increment = (stream << 1) | 1UL;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    public uint NextUInt()
    {
        ulong previous = _state;
        _state = unchecked(previous * 6364136223846793005UL + _increment);
        uint xorShifted = (uint)(((previous >> 18) ^ previous) >> 27);
        int rotation = (int)(previous >> 59);
        return (xorShifted >> rotation) | (xorShifted << ((-rotation) & 31));
    }

    public double NextUnitDouble() => NextUInt() / 4294967296.0;

    public float NextFloat(float minimum, float maximum) =>
        minimum + ((maximum - minimum) * (float)NextUnitDouble());
}

internal sealed class RandomStreams
{
    public RandomStreams(ulong worldSeed)
    {
        Environment = new DeterministicRandom(worldSeed ^ 0x6A09E667F3BCC909UL, 1);
        Placement = new DeterministicRandom(worldSeed ^ 0xBB67AE8584CAA73BUL, 2);
        Reproduction = new DeterministicRandom(worldSeed ^ 0x3C6EF372FE94F82BUL, 3);
    }

    public DeterministicRandom Environment { get; }
    public DeterministicRandom Placement { get; }
    public DeterministicRandom Reproduction { get; }
}
