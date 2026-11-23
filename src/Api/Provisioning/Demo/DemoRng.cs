using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// One deterministic pseudo-random stream: a hand-written SplitMix64, with helpers that
/// return only <see cref="long"/> and <see cref="decimal"/>.
/// </summary>
/// <remarks>
/// <para>Hand-written so the output is fixed by this file alone: no runtime's
/// generator, no binary fractions, no platform difference. The core step is
/// Steele, Lea and Flood's SplitMix64 with Vigna's published constants, so a known
/// sequence pins it.</para>
///
/// <para>A stream is not thread-safe and is meant to be used by one pattern for one
/// month (see <see cref="DemoRngFactory"/>).</para>
/// </remarks>
public sealed class DemoRng
{
    private const ulong Gamma = 0x9E37_79B9_7F4A_7C15UL;
    private const ulong Mix1 = 0xBF58_476D_1CE4_E5B9UL;
    private const ulong Mix2 = 0x94D0_49BB_1331_11EBUL;

    // 2^53: a unit draw keeps the top 53 bits of a step.
    private const decimal UnitScale = 9_007_199_254_740_992m;

    // Above this mean a count is drawn as two halves, so the exponential below stays
    // inside the decimal range with precision to spare.
    private const decimal MaxDirectMean = 30m;

    private ulong _state;

    /// <summary>A stream that starts from <paramref name="state"/>.</summary>
    public DemoRng(ulong state) => _state = state;

    /// <summary>The next 64 raw bits.</summary>
    public ulong NextUInt64()
    {
        unchecked
        {
            _state += Gamma;
            var z = _state;
            z = (z ^ (z >> 30)) * Mix1;
            z = (z ^ (z >> 27)) * Mix2;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A uniform decimal in [0, 1), on a 2^-53 grid.</summary>
    public decimal NextUnit() => (NextUInt64() >> 11) / UnitScale;

    /// <summary>A uniform integer in [<paramref name="min"/>, <paramref name="max"/>],
    /// both ends included.</summary>
    public long NextLong(long min, long max)
    {
        if (max < min) throw new ArgumentOutOfRangeException(nameof(max), max, "max is below min");
        var range = (ulong)(max - min) + 1UL;
        if (range == 0) return (long)NextUInt64(); // the whole 64-bit range
        // Multiply-high maps 64 bits onto the range without the modulo's skew
        // towards low values.
        var scaled = (ulong)(((UInt128)NextUInt64() * range) >> 64);
        return min + (long)scaled;
    }

    /// <summary>A uniform index into a collection of <paramref name="count"/> items.</summary>
    public int NextIndex(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), count, "nothing to choose from");
        return (int)NextLong(0, count - 1);
    }

    /// <summary>A uniform decimal in [<paramref name="min"/>, <paramref name="max"/>).</summary>
    public decimal NextDecimal(decimal min, decimal max)
    {
        if (max < min) throw new ArgumentOutOfRangeException(nameof(max), max, "max is below min");
        return min + (max - min) * NextUnit();
    }

    /// <summary>A uniform amount in minor units, both ends included.</summary>
    public long NextMinor(long minMinor, long maxMinor) => NextLong(minMinor, maxMinor);

    /// <summary>True with probability <paramref name="probability"/>.</summary>
    public bool Chance(decimal probability) => NextUnit() < probability;

    /// <summary>One item, uniformly.</summary>
    public T Choice<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items[NextIndex(items.Count)];
    }

    /// <summary>
    /// A bell-shaped draw with mean 0 and unit variance: the sum of twelve uniform
    /// draws, less six. Bounded to (-6, 6), which a price path wants.
    /// </summary>
    public decimal NextStandardBell()
    {
        var sum = 0m;
        for (var i = 0; i < 12; i++) sum += NextUnit();
        return sum - 6m;
    }

    /// <summary>
    /// A Poisson-distributed count with mean <paramref name="mean"/>: Knuth's
    /// product-of-uniforms method, in decimal. A large mean is drawn as two halves,
    /// which is exact for a Poisson count.
    /// </summary>
    public int NextCount(decimal mean)
    {
        if (mean < 0) throw new ArgumentOutOfRangeException(nameof(mean), mean, "a mean count is not negative");
        if (mean == 0) return 0;
        if (mean > MaxDirectMean)
        {
            var half = mean / 2m;
            return NextCount(half) + NextCount(mean - half);
        }

        var limit = 1m / Exp(mean);
        var k = 0;
        var product = 1m;
        do
        {
            k++;
            product *= NextUnit();
        }
        while (product > limit);
        return k - 1;
    }

    /// <summary>e to the power <paramref name="x"/> for 0 &lt;= x &lt;= 30, by its
    /// Taylor series in decimal.</summary>
    internal static decimal Exp(decimal x)
    {
        if (x < 0 || x > MaxDirectMean) throw new ArgumentOutOfRangeException(nameof(x), x, "outside 0 to 30");
        var sum = 1m;
        var term = 1m;
        for (var n = 1; n < 200; n++)
        {
            term = term * x / n;
            sum += term;
            if (term < 0.000_000_000_000_000_000_01m) break;
        }
        return sum;
    }
}

/// <summary>
/// Hands out independent <see cref="DemoRng"/> streams, one per pattern and month.
/// </summary>
/// <remarks>
/// A stream's state is the first 8 bytes (big-endian) of
/// <c>SHA-256(seed || streamName || yyyymm)</c>: the seed as 8 big-endian bytes, the
/// name in UTF-8, then the month as six ASCII digits. The month is the last six
/// bytes, so no two (name, month) pairs share an input. Keying by month as well as
/// by pattern is what keeps a change to one pattern, or to one month of it, from
/// reshuffling any other: no stream is shared, and none runs on past its month.
/// </remarks>
public sealed class DemoRngFactory
{
    /// <summary>The month stamp of a stream that is not tied to one month.</summary>
    public const int WholeWindow = 0;

    private readonly ulong _seed;

    /// <summary>A factory for one seed.</summary>
    public DemoRngFactory(ulong seed) => _seed = seed;

    /// <summary>The seed every stream is keyed by.</summary>
    public ulong Seed => _seed;

    /// <summary>The stream for <paramref name="streamName"/> in the given month.</summary>
    public DemoRng Stream(string streamName, int year, int month)
    {
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year), year, "not a calendar year");
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month), month, "not a calendar month");
        return Stream(streamName, year * 100 + month);
    }

    /// <summary>The stream for <paramref name="streamName"/> in the month that
    /// contains <paramref name="anyDayOfMonth"/>.</summary>
    public DemoRng Stream(string streamName, DateOnly anyDayOfMonth) =>
        Stream(streamName, anyDayOfMonth.Year, anyDayOfMonth.Month);

    /// <summary>One stream for the whole window, stamped <see cref="WholeWindow"/>
    /// in place of a month.</summary>
    public DemoRng WholeWindowStream(string streamName) => Stream(streamName, WholeWindow);

    private DemoRng Stream(string streamName, int yyyymm)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamName);
        var name = Encoding.UTF8.GetBytes(streamName);
        var stamp = Encoding.ASCII.GetBytes(yyyymm.ToString("D6", CultureInfo.InvariantCulture));

        var input = new byte[8 + name.Length + stamp.Length];
        BinaryPrimitives.WriteUInt64BigEndian(input, _seed);
        name.CopyTo(input, 8);
        stamp.CopyTo(input, 8 + name.Length);

        var hash = SHA256.HashData(input);
        return new DemoRng(BinaryPrimitives.ReadUInt64BigEndian(hash));
    }
}
