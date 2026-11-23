using System.Text.Json;

using Coffer.Domain.Money;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// Each currency's ISO 4217 minor units and the codes an account or a ledger can take
/// (ADR-0114 D15), read from the one checked-in table the SPA imports too.
/// </summary>
/// <remarks>
/// <para>The table on disk is read here on its own, token by token, so the embedded copy
/// is compared with the file rather than with itself, and a duplicate key that a
/// dictionary deserialize would keep silently is seen.</para>
/// <para>The parity rule (accepted places, else the default) is applied here from the raw
/// file, code by code, as <c>minorUnits.test.ts</c> applies it to the SPA's reader and the
/// SQL guard to the SQL function, so the three readers are held to one rule over every
/// code.</para>
/// </remarks>
public sealed class CurrencyMinorUnitsTests
{
    private static readonly Lazy<DiskTable> Disk = new(ReadDisk);

    // The 0-place and 3-place sets, pinned so a stale or mistyped table fails here.
    private static readonly string[] WholeUnitCodes =
    [
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "UYI",
        "VND", "VUV", "XAF", "XOF", "XPF",
    ];

    private static readonly string[] ThreePlaceCodes = ["BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND"];

    [Fact]
    public void The_embedded_table_is_the_checked_in_file_key_for_key()
    {
        var disk = Disk.Value;
        Assert.Equal(disk.Published, CurrencyMinorUnits.Published);
        Assert.Equal(
            disk.Entries.Select(e => e.Code).Order(StringComparer.Ordinal),
            CurrencyMinorUnits.ListOne.Keys.Order(StringComparer.Ordinal));
        foreach (var (code, exponent) in disk.Entries)
            Assert.True(CurrencyMinorUnits.ListOne[code] == exponent, $"{code}: embedded {CurrencyMinorUnits.ListOne[code]}, file {exponent}");
    }

    [Fact]
    public void The_file_holds_each_code_once_as_three_upper_case_letters_with_an_exponent_or_none()
    {
        var disk = Disk.Value;
        // Fail closed: a file that read as nearly empty would pass every per-key check.
        Assert.True(disk.Entries.Count > 150, $"read only {disk.Entries.Count} codes");
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", disk.Published);

        var duplicates = disk.Entries.GroupBy(e => e.Code, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "codes listed twice: " + string.Join(", ", duplicates));
        foreach (var (code, exponent) in disk.Entries)
        {
            Assert.Matches("^[A-Z]{3}$", code);
            Assert.True(exponent is null or (>= 0 and <= 4), $"{code}: exponent {exponent}");
        }
    }

    [Fact]
    public void Every_listed_code_takes_its_places_when_accepted_and_the_default_otherwise()
    {
        foreach (var (code, exponent) in Disk.Value.Entries)
        {
            var accepted = exponent is { } e && e <= CurrencyMinorUnits.MaxPlaces;
            Assert.True(accepted == CurrencyMinorUnits.IsAcceptedAccountCurrency(code), $"{code}: accepted");
            var expected = accepted ? exponent!.Value : CurrencyMinorUnits.DefaultPlaces;
            Assert.True(expected == CurrencyMinorUnits.PlacesOf(code), $"{code}: places {CurrencyMinorUnits.PlacesOf(code)}, expected {expected}");
        }
        Assert.Equal(
            Disk.Value.Entries.Where(e => e.Exponent is { } x && x <= CurrencyMinorUnits.MaxPlaces)
                .Select(e => e.Code).Order(StringComparer.Ordinal),
            CurrencyMinorUnits.AcceptedCodes);
    }

    [Fact]
    public void The_whole_unit_and_three_place_currencies_are_the_ones_the_list_names()
    {
        Assert.Equal(0, CurrencyMinorUnits.PlacesOf("JPY"));
        Assert.Equal(2, CurrencyMinorUnits.PlacesOf("USD"));
        Assert.Equal(3, CurrencyMinorUnits.PlacesOf("KWD"));
        Assert.Equal(3, CurrencyMinorUnits.PlacesOf("BHD"));
        Assert.Equal(
            WholeUnitCodes,
            CurrencyMinorUnits.AcceptedCodes.Where(c => CurrencyMinorUnits.PlacesOf(c) == 0));
        Assert.Equal(
            ThreePlaceCodes,
            CurrencyMinorUnits.AcceptedCodes.Where(c => CurrencyMinorUnits.PlacesOf(c) == 3));
    }

    [Theory]
    // The two fund units the list gives four places.
    [InlineData("CLF")]
    [InlineData("UYW")]
    // "N.A." entries: a metal and the no-currency code.
    [InlineData("XAU")]
    [InlineData("XXX")]
    // Not on the list at all, and not in the stored form.
    [InlineData("ABC")]
    [InlineData("jpy")]
    public void A_refused_code_is_not_accepted_and_keeps_the_default_places(string code)
    {
        Assert.False(CurrencyMinorUnits.IsAcceptedAccountCurrency(code));
        Assert.False(CurrencyMinorUnits.TryGetAccepted(code, out _));
        Assert.DoesNotContain(code, CurrencyMinorUnits.AcceptedCodes);
        Assert.Equal(CurrencyMinorUnits.DefaultPlaces, CurrencyMinorUnits.PlacesOf(code));
    }

    [Fact]
    public void A_fund_unit_finer_than_the_ceiling_is_on_the_list_but_refused()
    {
        // Anchor: the table does hold these, at more places than the ceiling, so the
        // refusal above is the rule's and not a missing key's.
        Assert.Equal(4, CurrencyMinorUnits.ListOne["CLF"]);
        Assert.Equal(4, CurrencyMinorUnits.ListOne["UYW"]);
        Assert.Null(CurrencyMinorUnits.ListOne["XAU"]);
        Assert.False(CurrencyMinorUnits.ListOne.ContainsKey("ABC"));
    }

    [Fact]
    public void A_missing_code_is_not_accepted_and_has_no_places()
    {
        Assert.False(CurrencyMinorUnits.IsAcceptedAccountCurrency(null));
        Assert.Throws<ArgumentNullException>(() => CurrencyMinorUnits.PlacesOf(null!));
    }

    [Fact]
    public void Fits_compares_the_value_not_its_scale()
    {
        Assert.True(CurrencyMinorUnits.Fits(5.00m, "JPY"));
        Assert.False(CurrencyMinorUnits.Fits(5.5m, "JPY"));
        Assert.True(CurrencyMinorUnits.Fits(1.234m, "KWD"));
        Assert.False(CurrencyMinorUnits.Fits(1.2345m, "KWD"));
        Assert.True(CurrencyMinorUnits.Fits(12.30m, "USD"));
        Assert.False(CurrencyMinorUnits.Fits(12.301m, "USD"));
        // A refused code is checked at the default places, not at the table's four.
        Assert.False(CurrencyMinorUnits.Fits(1.234m, "CLF"));
    }

    [Fact]
    public void A_computed_figure_rounds_half_away_from_zero_at_its_currency_places()
    {
        Assert.Equal(1m, CurrencyMinorUnits.RoundAwayFromZero(0.5m, "JPY"));
        Assert.Equal(-1m, CurrencyMinorUnits.RoundAwayFromZero(-0.5m, "JPY"));
        Assert.Equal(2.35m, CurrencyMinorUnits.RoundAwayFromZero(2.345m, "USD"));
        Assert.Equal(-2.35m, CurrencyMinorUnits.RoundAwayFromZero(-2.345m, "USD"));
        Assert.Equal(1.235m, CurrencyMinorUnits.RoundAwayFromZero(1.2345m, "KWD"));
    }

    [Fact]
    public void A_loan_payment_rounds_up_to_the_next_minor_unit()
    {
        Assert.Equal(1.01m, CurrencyMinorUnits.RoundUpToMinorUnit(1.001m, "USD"));
        Assert.Equal(1.001m, CurrencyMinorUnits.RoundUpToMinorUnit(1.0001m, "KWD"));
        Assert.Equal(11m, CurrencyMinorUnits.RoundUpToMinorUnit(10.2m, "JPY"));
        // A figure that already fits is unchanged.
        Assert.Equal(10m, CurrencyMinorUnits.RoundUpToMinorUnit(10m, "JPY"));
        Assert.Equal(1.25m, CurrencyMinorUnits.RoundUpToMinorUnit(1.25m, "USD"));
    }

    private sealed record DiskTable(string Published, IReadOnlyList<(string Code, int? Exponent)> Entries);

    private static DiskTable ReadDisk()
    {
        var path = Path.Combine(RepoRoot(), "src", "Web", "src", "lib", "currencyMinorUnits.json");
        var reader = new Utf8JsonReader(File.ReadAllBytes(path));
        string? published = null;
        var entries = new List<(string, int?)>();
        var inCodes = false;
        string? code = null;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    var name = reader.GetString();
                    inCodes = name == "minorUnits";
                    if (name == "published")
                    {
                        reader.Read();
                        published = reader.GetString();
                    }
                    break;
                case JsonTokenType.PropertyName when inCodes && reader.CurrentDepth == 2:
                    code = reader.GetString();
                    break;
                case JsonTokenType.Null when inCodes && code is not null:
                    entries.Add((code, null));
                    code = null;
                    break;
                case JsonTokenType.Number when inCodes && code is not null:
                    entries.Add((code, reader.TryGetInt32(out var e) ? e : -1));
                    code = null;
                    break;
                // Any other value (a string, a boolean, an object) is out of shape: record
                // it as an exponent the shape test refuses rather than skip it.
                case not JsonTokenType.EndObject when inCodes && code is not null:
                    entries.Add((code, -1));
                    code = null;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                    break;
            }
        }
        Assert.NotNull(published);
        return new DiskTable(published, entries);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
