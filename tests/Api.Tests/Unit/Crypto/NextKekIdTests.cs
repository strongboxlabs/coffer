using Coffer.Api.Crypto;

namespace Coffer.Api.Tests.Unit.Crypto;

/// <summary>
/// KEK-id succession for UI-driven rotation (ADR-0092 D4). The id is only a label
/// (<c>ledgers.lek_kek_id</c>), but a rotation that reused the current one would
/// make the column useless for telling rotations apart, so every input must yield
/// something different from its input.
/// </summary>
public sealed class NextKekIdTests
{
    [Theory]
    [InlineData("v1", "v2")]
    [InlineData("v2", "v3")]
    [InlineData("v9", "v10")]
    [InlineData("v99", "v100")]
    [InlineData("1", "2")]
    [InlineData("kek-7", "kek-8")]
    public void Bumps_a_trailing_number(string current, string expected)
        => Assert.Equal(expected, MasterKeyLoader.NextKekId(current));

    [Theory]
    [InlineData("2026-08", "2026-09")]   // padding kept, not degraded to 2026-9
    [InlineData("v007", "v008")]
    public void Preserves_zero_padding(string current, string expected)
        => Assert.Equal(expected, MasterKeyLoader.NextKekId(current));

    [Theory]
    [InlineData("prod", "prod-2")]
    [InlineData("v", "v-2")]
    public void Falls_back_to_a_suffix_when_there_is_no_trailing_number(
        string current, string expected)
        => Assert.Equal(expected, MasterKeyLoader.NextKekId(current));

    [Fact]
    public void A_pathological_number_does_not_overflow()
    {
        // A hand-set id like this must not turn a rotation into an exception.
        var absurd = "v" + new string('9', 40);
        var next = MasterKeyLoader.NextKekId(absurd);

        Assert.NotEqual(absurd, next);
        Assert.EndsWith("-2", next);
    }

    [Fact]
    public void Trims_and_never_returns_the_input_unchanged()
    {
        Assert.Equal("v2", MasterKeyLoader.NextKekId("  v1  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_blank_id(string current)
        => Assert.Throws<ArgumentException>(() => MasterKeyLoader.NextKekId(current));
}
