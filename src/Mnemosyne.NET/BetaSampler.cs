using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>Draws from a Beta distribution, for Thompson sampling. Plain arithmetic, so it is safe for native AOT.</summary>
internal sealed class BetaSampler
{
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    private readonly Random _random;

    /// <summary>Initializes a new instance of the <see cref="BetaSampler"/> class.</summary>
    /// <param name="random">The source of uniform numbers.</param>
    public BetaSampler(Random random) => _random = random;

    /// <summary>Builds a random source that is the same for the same seed, question and lineage.</summary>
    /// <param name="seed">The configured seed.</param>
    /// <param name="question">The question being asked.</param>
    /// <param name="lineage">The lineage whose variants are being drawn for.</param>
    /// <returns>A random source seeded from an FNV-1a hash of the three.</returns>
    public static Random SeededRandom(int seed, string question, ReasonletId lineage)
    {
        var hash = FnvOffsetBasis;
        for (var i = 0; i < 4; i++)
        {
            hash = Fold(hash, (uint)(seed >> (8 * i)) & 0xFF);
        }

        foreach (var unit in question)
        {
            hash = Fold(hash, unit);
        }

        Span<byte> bytes = stackalloc byte[16];
        // Big-endian, so the hash is the same on every platform. Sixteen bytes always fit.
        _ = lineage.Value.TryWriteBytes(bytes, bigEndian: true, out _);
        foreach (var b in bytes)
        {
            hash = Fold(hash, b);
        }

        return new Random(unchecked((int)hash));
    }

    /// <summary>Draws one value from Beta(<paramref name="alpha"/>, <paramref name="beta"/>).</summary>
    /// <param name="alpha">The first shape parameter. Must be finite and above 0.</param>
    /// <param name="beta">The second shape parameter. Must be finite and above 0.</param>
    /// <returns>A value in [0, 1].</returns>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is not finite and above 0.</exception>
    public double Sample(double alpha, double beta)
    {
        if (!IsPositiveFinite(alpha))
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "must be finite and above 0");
        }

        if (!IsPositiveFinite(beta))
        {
            throw new ArgumentOutOfRangeException(nameof(beta), beta, "must be finite and above 0");
        }

        var g1 = Gamma(alpha);
        var g2 = Gamma(beta);
        var sum = g1 + g2;

        // Both draws underflowed to 0, which happens only for shapes near 1e-300; the mean is the best answer left.
        return sum > 0.0 ? g1 / sum : alpha / (alpha + beta);
    }

    private static bool IsPositiveFinite(double value) => value > 0.0 && double.IsFinite(value);

    // One FNV-1a step over one unit: a byte, or a UTF-16 code unit.
    private static uint Fold(uint hash, uint unit) => unchecked((hash ^ unit) * FnvPrime);

    // Marsaglia and Tsang. A shape below 1 draws with shape + 1 and scales by u^(1/shape).
    private double Gamma(double shape)
    {
        if (shape < 1.0)
        {
            var u = 1.0 - _random.NextDouble();
            return Gamma(shape + 1.0) * Math.Pow(u, 1.0 / shape);
        }

        var d = shape - (1.0 / 3.0);
        var c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            var x = Normal();
            var v = 1.0 + (c * x);
            if (v <= 0.0)
            {
                continue;
            }

            v = v * v * v;
            var u = 1.0 - _random.NextDouble();
            var x2 = x * x;
            if (u < 1.0 - (0.0331 * x2 * x2) || Math.Log(u) < (0.5 * x2) + (d * (1.0 - v + Math.Log(v))))
            {
                return d * v;
            }
        }
    }

    // Box-Muller; both uniforms are in (0, 1] so the logarithm is finite.
    private double Normal()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
