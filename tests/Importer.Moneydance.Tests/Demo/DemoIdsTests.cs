using System.Text.RegularExpressions;
using Coffer.Api.Provisioning.Demo;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// Generated MD ids are hashes of a row's own coordinates, so they are stable,
/// distinct per coordinate, and UUID-shaped.
/// </summary>
public sealed partial class DemoIdsTests
{
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex VersionEightUuid();

    [Fact]
    public void An_id_is_a_lowercase_version_8_uuid()
    {
        var id = new DemoIds(DemoSpec.DefaultSeed).Id("txn", "groceries", 0);

        Assert.Matches(VersionEightUuid(), id);
        Assert.True(Guid.TryParse(id, out _));
    }

    /// <summary>An id's layout, rebuilt here byte by byte: SHA-256 of the seed (8 big-endian bytes) and the UTF-8
    /// coordinates joined by 0x1F, the first 16 bytes, version nibble 8 and variant
    /// 10, lowercase 8-4-4-4-12.</summary>
    [Theory]
    [InlineData("txn", "groceries", 7L, null)]
    [InlineData("txn", "payroll", 0L, 3)]
    [InlineData("acct", "joint-checking", 0L, null)]
    public void An_id_is_the_sha256_of_seed_and_coordinates(string kind, string pattern, long ordinal, int? sub)
    {
        var seed = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(seed, DemoSpec.DefaultSeed);
        var text = kind + "\u001F" + pattern + "\u001F" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (sub is { } part ? "\u001F" + part.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
        var hash = System.Security.Cryptography.SHA256.HashData(seed.Concat(System.Text.Encoding.UTF8.GetBytes(text)).ToArray());
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexStringLower(bytes);
        var expected = $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";

        var ids = new DemoIds(DemoSpec.DefaultSeed);
        Assert.Equal(expected, sub is { } s ? ids.Id(kind, pattern, ordinal, s) : ids.Id(kind, pattern, ordinal));
    }

    [Fact]
    public void The_same_coordinates_give_the_same_id()
    {
        Assert.Equal(
            new DemoIds(DemoSpec.DefaultSeed).Id("txn", "groceries", 7, 2),
            new DemoIds(DemoSpec.DefaultSeed).Id("txn", "groceries", 7, 2));
    }

    [Fact]
    public void Every_coordinate_changes_the_id()
    {
        var ids = new DemoIds(DemoSpec.DefaultSeed);
        string[] all =
        [
            ids.Id("txn", "groceries", 7),
            ids.Id("acct", "groceries", 7),
            ids.Id("txn", "dining", 7),
            ids.Id("txn", "groceries", 8),
            ids.Id("txn", "groceries", 7, 0),
            ids.Id("txn", "groceries", 7, 1),
            new DemoIds(DemoSpec.DefaultSeed + 1).Id("txn", "groceries", 7),
        ];

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_coordinate_may_not_carry_the_separator()
    {
        var ids = new DemoIds(DemoSpec.DefaultSeed);

        Assert.Throws<ArgumentException>(() => ids.Id("txn\u001Fx", "groceries", 0));
        Assert.Throws<ArgumentException>(() => ids.Id("txn", "", 0));
    }
}
