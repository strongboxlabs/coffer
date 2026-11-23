using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// Moneydance ids for generated rows, UUID-formatted and derived by hash, never drawn.
/// </summary>
/// <remarks>
/// <para>An id is the first 16 bytes of
/// <c>SHA-256(seed || kind || 0x1F || pattern || 0x1F || ordinal [|| 0x1F || sub])</c>
/// (the seed as 8 big-endian bytes, the rest UTF-8, the numbers in invariant
/// decimal), with the RFC 9562 version nibble set to 8 (custom) and the variant to
/// <c>10</c>, written lowercase as 8-4-4-4-12 hex.</para>
///
/// <para>Hashing a row's own coordinates, rather than drawing from a shared stream,
/// means adding, removing or reordering one pattern's rows moves no other row's id.
/// The 0x1F separator cannot appear in a kind or a pattern name, so no two
/// coordinate tuples share an input. Base rows keep their own ids.</para>
/// </remarks>
public sealed class DemoIds
{
    private const byte Separator = 0x1F;
    private readonly ulong _seed;

    /// <summary>Ids keyed by <paramref name="seed"/>.</summary>
    public DemoIds(ulong seed) => _seed = seed;

    /// <summary>The id of the <paramref name="ordinal"/>-th row of
    /// <paramref name="kind"/> that <paramref name="pattern"/> made.</summary>
    public string Id(string kind, string pattern, long ordinal) => Hash(kind, pattern, ordinal, sub: null);

    /// <summary>The id of part <paramref name="sub"/> of a row, such as a split of
    /// a transaction.</summary>
    public string Id(string kind, string pattern, long ordinal, int sub) => Hash(kind, pattern, ordinal, sub);

    private string Hash(string kind, string pattern, long ordinal, int? sub)
    {
        Check(kind, nameof(kind));
        Check(pattern, nameof(pattern));

        var text = new StringBuilder()
            .Append(kind).Append((char)Separator)
            .Append(pattern).Append((char)Separator)
            .Append(ordinal.ToString(CultureInfo.InvariantCulture));
        if (sub is { } part) text.Append((char)Separator).Append(part.ToString(CultureInfo.InvariantCulture));

        var tail = Encoding.UTF8.GetBytes(text.ToString());
        var input = new byte[8 + tail.Length];
        BinaryPrimitives.WriteUInt64BigEndian(input, _seed);
        tail.CopyTo(input, 8);

        var hash = SHA256.HashData(input);
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8: custom
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 10

        var hex = Convert.ToHexStringLower(bytes);
        return string.Join('-', hex[..8], hex[8..12], hex[12..16], hex[16..20], hex[20..]);
    }

    private static void Check(string value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        if (value.Contains((char)Separator, StringComparison.Ordinal))
            throw new ArgumentException("an id coordinate may not contain U+001F", name);
    }
}
