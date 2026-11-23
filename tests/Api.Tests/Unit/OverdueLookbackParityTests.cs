using System.Globalization;
using System.Text.RegularExpressions;

using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The balance forecast and the Bills widget reach back the same number of days for an
/// overdue reminder.
/// </summary>
/// <remarks>
/// <para>The forecast places every un-posted occurrence its look-back reaches on day 1
/// and counts the ones dated before today as overdue; the home screen's Bills widget and
/// header count overdue occurrences over their own fixed look-back. The two are one rule
/// written twice, a C# constant and a TypeScript one, and nothing in either build reads
/// the other. Should they drift, the forecast would say it includes overdue reminders
/// that Bills does not list, or leave out ones it does.</para>
/// <para>Checked across the language boundary, as the CSV vocabulary is
/// (<see cref="Ingest.CsvVocabularyParityTests"/>): the TypeScript declaration is read out
/// of its source. It fails closed: a declaration it cannot find is a failure, not a
/// pass.</para>
/// </remarks>
public sealed class OverdueLookbackParityTests
{
    private const string AttentionSource = "src/Web/src/routes/ledgers/home/attention.ts";

    [Fact]
    public void The_forecasts_look_back_is_the_one_the_bills_widget_counts_overdue_over()
    {
        var source = File.ReadAllText(Locate(AttentionSource));
        var declaration = Regex.Matches(source, @"^export const OVERDUE_LOOKBACK_DAYS\s*=\s*(\d+)\s*;", RegexOptions.Multiline);
        Assert.True(declaration.Count == 1,
            $"Expected exactly one `export const OVERDUE_LOOKBACK_DAYS = <number>;` in {AttentionSource}, "
            + $"found {declaration.Count}. If it was renamed, moved or computed, point this guard at its new "
            + "form rather than deleting it: it is the only thing holding the two look-backs together.");

        var web = int.Parse(declaration[0].Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(web == BalanceForecastRepository.OverdueLookbackDays,
            $"The Bills widget counts overdue reminders over {web} days ({AttentionSource}) and the balance "
            + $"forecast places them over {BalanceForecastRepository.OverdueLookbackDays} "
            + "(BalanceForecastRepository.OverdueLookbackDays). Change both together.");
    }

    private static string Locate(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Could not locate '{relative}' from {AppContext.BaseDirectory}");
    }
}
