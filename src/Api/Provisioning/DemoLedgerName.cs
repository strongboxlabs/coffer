using System.Globalization;

namespace Coffer.Api.Provisioning;

/// <summary>
/// The name of the next Demo ledger: "Demo", then "Demo 2", "Demo 3" and so on,
/// reusing the lowest free number (ADR-0115 D2).
/// </summary>
/// <remarks>
/// <para>A name is taken when one of the caller's visible ledgers, shared ones
/// included, has it once trimmed, ignoring case. Only the exact forms count: "Demo"
/// for 1, and "Demo N" for N of 2 or more, in digits with no leading zero, so
/// "Demo 02" and "Demo2" leave 2 free.</para>
/// <para>The name is a label, not a key: nothing makes ledger names unique, so a
/// ledger given the same name by hand while a Demo builds simply shares it.</para>
/// </remarks>
public static class DemoLedgerName
{
    /// <summary>The first Demo's name, and the prefix of every later one.</summary>
    public const string Base = "Demo";

    /// <summary>The smallest free Demo name among <paramref name="existing"/>.</summary>
    public static string Next(IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var taken = new HashSet<int>();
        foreach (var name in existing)
            if (NumberOf(name) is { } n)
                taken.Add(n);

        var next = 1;
        while (taken.Contains(next)) next++;
        return NameOf(next);
    }

    /// <summary>The Demo name with number <paramref name="n"/>: "Demo" for 1.</summary>
    public static string NameOf(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        return n == 1 ? Base : $"{Base} {n.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The Demo number a name holds, or null when it is not a Demo name.</summary>
    private static int? NumberOf(string? name)
    {
        if (name is null) return null;
        var trimmed = name.Trim();
        if (string.Equals(trimmed, Base, StringComparison.OrdinalIgnoreCase)) return 1;
        if (!trimmed.StartsWith(Base + " ", StringComparison.OrdinalIgnoreCase)) return null;

        var digits = trimmed[(Base.Length + 1)..];
        if (digits.Length == 0 || digits[0] == '0' || !digits.All(char.IsAsciiDigit)) return null;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 2
            ? n
            : null;
    }
}
