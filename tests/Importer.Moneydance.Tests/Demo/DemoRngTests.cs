using Coffer.Api.Provisioning.Demo;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// The Demo's hand-written SplitMix64 and its per-stream keying. The generator's
/// bytes are only as fixed as these streams, so the core step is pinned to the
/// published reference sequence, and each stream is shown to depend on its seed,
/// its name and its month, and on nothing else.
/// </summary>
public sealed class DemoRngTests
{
    [Fact]
    public void SplitMix64_matches_the_reference_sequence_from_seed_zero()
    {
        // The first outputs of the reference splitmix64.c with x = 0.
        var rng = new DemoRng(0);

        Assert.Equal(0xE220A8397B1DCDAFUL, rng.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, rng.NextUInt64());
        Assert.Equal(0x06C45D188009454FUL, rng.NextUInt64());
        Assert.Equal(0xF88BB8A8724C81ECUL, rng.NextUInt64());
        Assert.Equal(0x1B39896A51A8749BUL, rng.NextUInt64());
    }

    [Fact]
    public void SplitMix64_matches_the_reference_sequence_from_another_seed()
    {
        var rng = new DemoRng(1234567);

        Assert.Equal(0x599ED017FB08FC85UL, rng.NextUInt64());
        Assert.Equal(0x2C73F08458540FA5UL, rng.NextUInt64());
        Assert.Equal(0x883EBCE5A3F27C77UL, rng.NextUInt64());
    }

    /// <summary>A stream's key, rebuilt here byte by byte: the seed as 8 big-endian bytes, the name in UTF-8,
    /// the month as six ASCII digits, SHA-256, and the first 8 bytes big-endian. A
    /// change to the layout fails here by name, not only as a moved golden hash.</summary>
    [Theory]
    [InlineData("groceries", 2025, 3, "202503")]
    [InlineData("price:vti", 2021, 11, "202111")]
    [InlineData("tax-settlement:federal", 2026, 12, "202612")]
    public void A_streams_key_is_the_sha256_of_seed_name_and_month(string name, int year, int month, string stamp)
    {
        var expected = new DemoRng(KeyOf(DemoSpec.DefaultSeed, name, stamp));
        var actual = new DemoRngFactory(DemoSpec.DefaultSeed).Stream(name, year, month);

        for (var i = 0; i < 3; i++) Assert.Equal(expected.NextUInt64(), actual.NextUInt64());
    }

    [Fact]
    public void A_whole_window_stream_is_stamped_with_six_zeros()
    {
        var expected = new DemoRng(KeyOf(DemoSpec.DefaultSeed, "budget", "000000"));
        var actual = new DemoRngFactory(DemoSpec.DefaultSeed).WholeWindowStream("budget");

        for (var i = 0; i < 3; i++) Assert.Equal(expected.NextUInt64(), actual.NextUInt64());
    }

    private static ulong KeyOf(ulong seed, string name, string stamp)
    {
        var seedBytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(seedBytes, seed);
        var input = seedBytes
            .Concat(System.Text.Encoding.UTF8.GetBytes(name))
            .Concat(System.Text.Encoding.ASCII.GetBytes(stamp))
            .ToArray();
        var hash = System.Security.Cryptography.SHA256.HashData(input);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, 8));
    }

    [Fact]
    public void The_same_seed_name_and_month_give_the_same_stream()
    {
        var a = new DemoRngFactory(DemoSpec.DefaultSeed).Stream("groceries", 2025, 3);
        var b = new DemoRngFactory(DemoSpec.DefaultSeed).Stream("groceries", new DateOnly(2025, 3, 31));

        Assert.Equal(Draw(a, 16), Draw(b, 16));
    }

    [Fact]
    public void Streams_differ_by_name_by_month_and_by_seed()
    {
        var factory = new DemoRngFactory(DemoSpec.DefaultSeed);
        var baseline = Draw(factory.Stream("groceries", 2025, 3), 8);

        Assert.NotEqual(baseline, Draw(factory.Stream("dining", 2025, 3), 8));
        Assert.NotEqual(baseline, Draw(factory.Stream("groceries", 2025, 4), 8));
        Assert.NotEqual(baseline, Draw(factory.Stream("groceries", 2026, 3), 8));
        Assert.NotEqual(baseline, Draw(new DemoRngFactory(DemoSpec.DefaultSeed + 1).Stream("groceries", 2025, 3), 8));
        Assert.NotEqual(baseline, Draw(factory.WholeWindowStream("groceries"), 8));
    }

    [Fact]
    public void Drawing_from_one_stream_leaves_another_untouched()
    {
        var factory = new DemoRngFactory(DemoSpec.DefaultSeed);
        var untouched = Draw(factory.Stream("rent", 2025, 3), 8);

        var other = factory.Stream("groceries", 2025, 3);
        Draw(other, 1000);

        Assert.Equal(untouched, Draw(factory.Stream("rent", 2025, 3), 8));
    }

    [Fact]
    public void Bounded_draws_stay_in_their_ranges_and_reach_both_ends()
    {
        var rng = new DemoRng(42);
        var seen = new HashSet<long>();
        for (var i = 0; i < 2000; i++)
        {
            var value = rng.NextLong(-3, 3);
            Assert.InRange(value, -3, 3);
            seen.Add(value);

            var unit = rng.NextUnit();
            Assert.True(unit >= 0m && unit < 1m, $"unit draw {unit} outside [0, 1)");

            var amount = rng.NextDecimal(10m, 20m);
            Assert.True(amount >= 10m && amount < 20m, $"decimal draw {amount} outside [10, 20)");

            var bell = rng.NextStandardBell();
            Assert.True(bell > -6m && bell < 6m, $"bell draw {bell} outside (-6, 6)");
        }
        Assert.Equal(7, seen.Count);
    }

    [Fact]
    public void Choice_and_index_pick_inside_the_collection()
    {
        var rng = new DemoRng(7);
        string[] items = ["a", "b", "c"];
        var picked = new HashSet<string>();
        for (var i = 0; i < 200; i++) picked.Add(rng.Choice(items));

        Assert.Equal(items.ToHashSet(), picked);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextIndex(0));
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(12)]
    [InlineData(26)]
    [InlineData(45)]
    public void Counts_average_close_to_their_mean(double meanAsWritten)
    {
        var mean = (decimal)meanAsWritten;
        var rng = new DemoRng(99);
        const int draws = 4000;
        var total = 0L;
        for (var i = 0; i < draws; i++)
        {
            var count = rng.NextCount(mean);
            Assert.True(count >= 0);
            total += count;
        }

        var average = (decimal)total / draws;
        // A Poisson count's standard error over 4000 draws is sqrt(mean / 4000);
        // five of them is far outside chance and well inside a broken sampler.
        var tolerance = 5m * Sqrt(mean / draws);
        Assert.InRange(average, mean - tolerance, mean + tolerance);
    }

    [Fact]
    public void A_zero_mean_count_is_zero()
    {
        Assert.Equal(0, new DemoRng(1).NextCount(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DemoRng(1).NextCount(-1m));
    }

    private static ulong[] Draw(DemoRng rng, int count)
    {
        var values = new ulong[count];
        for (var i = 0; i < count; i++) values[i] = rng.NextUInt64();
        return values;
    }

    private static decimal Sqrt(decimal value)
    {
        var guess = value < 1m ? 1m : value;
        for (var i = 0; i < 60; i++) guess = (guess + value / guess) / 2m;
        return guess;
    }
}
