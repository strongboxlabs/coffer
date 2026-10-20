using System.Security.Cryptography;
using System.Text;

using Konscious.Security.Cryptography;

namespace Coffer.Api.Auth.Webauthn;

/// <summary>
/// Generates and verifies the 10 single-use recovery codes ADR-0013 mints
/// at registration. Codes are stored as Argon2id PHC strings — the
/// parameters live in the hash itself so increasing cost is a one-line
/// change without a migration. OWASP 2025-minimum parameters
/// (m=64MiB, t=3, p=1) are pinned in <see cref="HashCode"/>; raise them
/// over time as the threat model warrants.
/// </summary>
public static class RecoveryCodes
{
    /// <summary>
    /// Number of codes minted per regeneration. Per ADR-0013.
    /// </summary>
    public const int CodesPerSet = 10;

    /// <summary>
    /// Length of each code in plaintext characters. 10 base32-style chars
    /// (~50 bits of entropy) is overkill for an interactive code that's
    /// also gated by a physical re-registration ceremony, but it's cheap
    /// and matches what most password managers display nicely.
    /// </summary>
    public const int CodeLength = 10;

    private const int SaltLengthBytes = 16;
    private const int HashLengthBytes = 32;
    private const int MemoryKb = 64 * 1024;     // 64 MiB — OWASP 2025 minimum
    private const int Iterations = 3;
    private const int Parallelism = 1;

    private const string PhcPrefix = "$argon2id$v=19$";

    /// <summary>
    /// Crockford-base32 alphabet (no <c>I</c>, <c>L</c>, <c>O</c>, <c>U</c>)
    /// so handwritten codes don't collide with similar-looking glyphs.
    /// </summary>
    private static readonly char[] Alphabet =
        "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

    /// <summary>
    /// Mint <see cref="CodesPerSet"/> codes. Returns the plaintext list
    /// (caller shows them once to the user) and the matching PHC strings
    /// (caller persists). Order matches index-by-index.
    /// </summary>
    public static (IReadOnlyList<string> Plaintext, IReadOnlyList<string> Hashes) Generate()
    {
        var plaintext = new string[CodesPerSet];
        var hashes = new string[CodesPerSet];

        for (var i = 0; i < CodesPerSet; i++)
        {
            plaintext[i] = GenerateCode();
            hashes[i] = HashCode(plaintext[i]);
        }
        return (plaintext, hashes);
    }

    /// <summary>
    /// Verify <paramref name="presented"/> against <paramref name="phc"/>.
    /// Constant-time inside Argon2's verify path.
    /// </summary>
    public static bool Verify(string presented, string phc)
    {
        if (string.IsNullOrWhiteSpace(presented) || string.IsNullOrWhiteSpace(phc))
            return false;
        if (!phc.StartsWith(PhcPrefix, StringComparison.Ordinal))
            return false;

        var (memory, iterations, parallelism, salt, expected) = ParsePhc(phc);
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(NormalizeForVerify(presented)))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            MemorySize = memory,
            Iterations = iterations,
        };
        var actual = argon2.GetBytes(expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Hash a recovery code using OWASP-2025-minimum Argon2id parameters,
    /// returning the PHC string suitable for storage.
    /// </summary>
    internal static string HashCode(string code)
    {
        Span<byte> salt = stackalloc byte[SaltLengthBytes];
        RandomNumberGenerator.Fill(salt);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(NormalizeForVerify(code)))
        {
            Salt = salt.ToArray(),
            DegreeOfParallelism = Parallelism,
            MemorySize = MemoryKb,
            Iterations = Iterations,
        };
        var hash = argon2.GetBytes(HashLengthBytes);

        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{PhcPrefix}m={MemoryKb},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>
    /// Verify accepts the user's input either with or without dashes /
    /// whitespace and as either case — the hash is over the canonical
    /// upper-case undashed form.
    /// </summary>
    private static string NormalizeForVerify(string code) =>
        new string(code.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

    private static string GenerateCode()
    {
        Span<byte> rnd = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(rnd);
        Span<char> chars = stackalloc char[CodeLength + 1];
        var alphabetMask = (byte)(Alphabet.Length - 1);    // alphabet length is 32 → mask
        for (var i = 0; i < CodeLength; i++)
        {
            chars[i] = Alphabet[rnd[i] & alphabetMask];
        }
        // Insert a dash in the middle for readability, e.g. "ABCDE-FGHJK".
        var mid = CodeLength / 2;
        chars[CodeLength] = chars[CodeLength - 1];
        for (var i = CodeLength - 1; i > mid; i--)
            chars[i] = chars[i - 1];
        chars[mid] = '-';

        return new string(chars);
    }

    private static (int Memory, int Iterations, int Parallelism, byte[] Salt, byte[] Hash) ParsePhc(string phc)
    {
        // Format: $argon2id$v=19$m=…,t=…,p=…$<salt-b64>$<hash-b64>
        var parts = phc.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "argon2id")
            throw new FormatException("Unrecognised Argon2id PHC string.");

        // parts[1] = "v=19", parts[2] = "m=…,t=…,p=…", parts[3] = salt, parts[4] = hash
        var costs = parts[2].Split(',');
        var memory = ParseLabelled(costs, "m=");
        var iterations = ParseLabelled(costs, "t=");
        var parallelism = ParseLabelled(costs, "p=");

        var salt = Convert.FromBase64String(parts[3]);
        var hash = Convert.FromBase64String(parts[4]);
        return (memory, iterations, parallelism, salt, hash);
    }

    private static int ParseLabelled(string[] costs, string prefix)
    {
        foreach (var c in costs)
        {
            if (c.StartsWith(prefix, StringComparison.Ordinal))
                return int.Parse(c.AsSpan(prefix.Length), System.Globalization.CultureInfo.InvariantCulture);
        }
        throw new FormatException($"Argon2id PHC missing '{prefix}'.");
    }
}
