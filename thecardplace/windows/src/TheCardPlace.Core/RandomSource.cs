namespace TheCardPlace.Core;

/// <summary>
/// The one source of randomness an engine is handed. Every deal, cut and computer choice goes
/// through this, and it can be seeded, which makes a whole game replayable in a test. Seeded, it
/// is SplitMix64, the same generator the iOS engines use, so a seed produces the same numbers
/// there and here. Unseeded it uses the system generator.
/// </summary>
public sealed class RandomSource
{
    private ulong _state;
    private readonly bool _seeded;

    /// <summary>System randomness.</summary>
    public RandomSource() { }

    /// <summary>Deterministic. Not for anything cryptographic.</summary>
    public RandomSource(ulong seed)
    {
        _state = seed;
        _seeded = true;
    }

    public ulong Next()
    {
        if (!_seeded)
        {
            Span<byte> b = stackalloc byte[8];
            Random.Shared.NextBytes(b);
            return BitConverter.ToUInt64(b);
        }
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A uniform double in [0, 1), the shape the ported heuristics expect.</summary>
    public double NextUnit() => (Next() >> 11) / (double)(1UL << 53);

    /// <summary>A uniform integer in [0, n).</summary>
    public int NextInt(int below)
    {
        if (below <= 0) throw new ArgumentOutOfRangeException(nameof(below), "needs a positive bound");
        return (int)(Next() % (ulong)below);
    }

    /// <summary>True with the given probability.</summary>
    public bool Chance(double p) => NextUnit() < p;
}

public static class ShuffleExtensions
{
    /// <summary>
    /// Fisher–Yates with the engine's own generator, so a seeded game shuffles the same way every
    /// time. The same order of draws as the Swift engines.
    /// </summary>
    public static List<T> Shuffled<T>(this IEnumerable<T> items, RandomSource rng)
    {
        var a = items.ToList();
        for (var i = a.Count - 1; i > 0; i--)
        {
            var j = rng.NextInt(i + 1);
            if (i != j) (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }
}
