using System.Globalization;
using System.Text;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>One CSV line: the bank's raw description and the signed amount, in
/// cents, as it moved the card (a charge is negative).</summary>
public sealed record CardStatementCsvRow(DateOnly Date, string Description, long Amount);

/// <summary>
/// The card statement as the issuer's CSV download: a header row, then one row per
/// posting with an ISO date, the raw description and the signed amount.
/// </summary>
/// <remarks>
/// The seeder's saved layout maps exactly these three columns. A description that
/// holds a comma, a quote or a line break is quoted, with each quote written twice. Lines
/// end in LF. Amounts are written from cents with two decimals and a leading minus,
/// never through a culture.
/// </remarks>
public static class CardStatementCsv
{
    /// <summary>The header row.</summary>
    public const string Header = "Date,Description,Amount";

    /// <summary>The CSV text for <paramref name="rows"/>, in the order given.</summary>
    public static string Build(IEnumerable<CardStatementCsvRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder().Append(Header).Append('\n');
        foreach (var row in rows)
        {
            text.Append(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(Quote(row.Description))
                .Append(',')
                .Append(DemoMoney.Major(row.Amount))
                .Append('\n');
        }
        return text.ToString();
    }

    private static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
