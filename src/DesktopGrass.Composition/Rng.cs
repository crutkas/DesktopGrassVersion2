// Rng.cs - small, fast, seedable PRNG (xorshift64* seeded through SplitMix64).
using System;
using System.Collections.Generic;

namespace DesktopGrass.Composition;

internal sealed class Rng
{
    private ulong _state;

    public Rng(ulong seed)
    {
        _state = SplitMix(ref seed);
        if (_state == 0) _state = 0x9E3779B97F4A7C15UL;
    }

    public static Rng FromTime() => new((ulong)DateTime.UtcNow.Ticks ^ (ulong)Environment.ProcessId << 32);

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextU64()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return _state * 2685821657736338717UL;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public float Next() => (NextU64() >> 40) * (1.0f / (1 << 24));

    public float Range(float min, float max) => min + (max - min) * Next();

    /// <summary>Uniform integer in [min, maxExclusive).</summary>
    public int Int(int min, int maxExclusive) => Math.Min(maxExclusive - 1, min + (int)(Next() * (maxExclusive - min)));

    public bool Chance(float p) => Next() < p;

    public float Sign() => Next() < 0.5f ? -1f : 1f;

    public T Pick<T>(IReadOnlyList<T> items) => items[Int(0, items.Count)];

    /// <summary>Exponentially distributed wait for a Poisson process with the given rate.</summary>
    public float Exponential(float ratePerSecond) => -MathF.Log(1f - Next()) / ratePerSecond;
}
