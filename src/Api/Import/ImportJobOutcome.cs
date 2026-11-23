using System.Globalization;

using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Import;

/// <summary>One validation check a finished import failed, with the check's plain-language
/// summary for the person who ran it.</summary>
public sealed record ImportWarning(string Check, string Message);

/// <summary>
/// One Moneydance transaction the import did not bring in, as the wizard lists it:
/// its date (<c>YYYY-MM-DD</c>, or null when the export's date is not a calendar date),
/// the importer's reason, and for an investment transaction the security and shares.
/// </summary>
public sealed record ImportSkippedTransaction(
    string? Date, string Reason, string? Security, string? Ticker, decimal? Shares);

/// <summary>
/// What a finished import reports beyond its ledger (ADR-0071 D2): each failed
/// validation check and every transaction the pipeline steps declined to import. Both
/// are read off the import's result, which already records them; nothing is computed.
/// </summary>
/// <remarks>
/// The skipped list is bounded, because a badly damaged export can skip a great many
/// transactions and the job status is held in memory and polled. Past the bound the
/// rest are counted, not listed; the no-dropped-transactions check still counts all.
/// </remarks>
public static class ImportJobOutcome
{
    /// <summary>The most skipped transactions one job status lists.</summary>
    public const int SkippedTransactionsListed = 500;

    /// <summary>One warning per failed check, in the validator's order. It carries the
    /// check's summary, not its message: the message is in the importer's own terms,
    /// which the person reading the wizard does not have.</summary>
    public static IReadOnlyList<ImportWarning> Warnings(ImportValidator.ValidationReport validation) =>
        validation.Checks
            .Where(c => !c.Passed)
            .Select(c => new ImportWarning(c.Name, c.Summary ?? string.Empty))
            .ToList();

    /// <summary>The steps' skipped transactions in pipeline order, up to
    /// <see cref="SkippedTransactionsListed"/>, and how many more there are.</summary>
    public static (IReadOnlyList<ImportSkippedTransaction> Listed, int NotListed) Skipped(
        IReadOnlyList<ImportStepResult> steps)
    {
        var all = steps.SelectMany(s => s.Skips ?? []).ToList();
        var listed = all
            .Take(SkippedTransactionsListed)
            .Select(s => new ImportSkippedTransaction(IsoDate(s.Date), s.Reason, s.Security, s.Ticker, s.Shares))
            .ToList();
        return (listed, all.Count - listed.Count);
    }

    /// <summary>Moneydance's <c>yyyyMMdd</c> integer as <c>YYYY-MM-DD</c>; null when it
    /// is not a calendar date.</summary>
    internal static string? IsoDate(int? yyyymmdd) =>
        yyyymmdd is { } value
        && DateOnly.TryParseExact(
            value.ToString(CultureInfo.InvariantCulture), "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
}
