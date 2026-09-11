namespace Coffer.Api.Ingest.Csv;

/// <summary>How a delimited file separates its fields.</summary>
/// <remarks>
/// A closed vocabulary rather than a literal character. The first real target file is
/// TAB-delimited despite a <c>.csv</c> extension, and a literal tab stored in a
/// document is invisible in every tool that would ever show it — "the delimiter line
/// looks blank" is a debugging session nobody needs.
/// </remarks>
public enum CsvDelimiter
{
    Comma,
    Tab,
    Semicolon,
    Pipe,
}

/// <summary>Where the signed amount comes from.</summary>
public enum CsvAmountShape
{
    /// <summary>One column carrying a signed value.</summary>
    Signed,

    /// <summary>
    /// Two columns, one for money out and one for money in — the shape plenty of card
    /// statements ship instead of a single signed column. Not derivable from
    /// <see cref="Signed"/>: a file with separate columns has no sign to read.
    /// </summary>
    DebitCredit,
}

/// <summary>
/// A validated description of one institution's delimited export (mig 222).
/// </summary>
/// <remarks>
/// <para>
/// Produced ONLY by <c>CsvMappingValidator</c>. Every property here is already checked:
/// indexes are 1-based and positive, the date format parses, and the amount columns
/// match the declared shape. Nothing downstream re-validates, so nothing downstream may
/// construct one from unvalidated input.
/// </para>
/// <para>
/// COLUMN INDEXES ARE 1-BASED. The wizard shows a person "column 1", and the first real
/// target file has no header row to name columns by, so indexes are the only way to
/// address a field. Off-by-one in an amount position means importing a date as money,
/// which is why the validator refuses 0 rather than treating it as the first column.
/// </para>
/// </remarks>
public sealed record CsvMapping(
    int Version,
    CsvDelimiter Delimiter,
    int HeaderRows,
    int FooterRows,
    int DateColumn,
    string DateFormat,
    int PayeeColumn,
    int? MemoColumn,
    CsvAmountShape AmountShape,
    int? AmountColumn,
    bool AmountInvert,
    int? DebitColumn,
    int? CreditColumn)
{
    /// <summary>The literal separator this mapping's delimiter names.</summary>
    public char Separator => Delimiter switch
    {
        CsvDelimiter.Comma => ',',
        CsvDelimiter.Tab => '\t',
        CsvDelimiter.Semicolon => ';',
        CsvDelimiter.Pipe => '|',
        _ => ',',
    };
}

/// <summary>
/// One thing wrong with a mapping document, addressed so its author can find it.
/// </summary>
/// <param name="Path">
/// Dotted key path, e.g. <c>amount.column</c>. The whole point of a hand-editable
/// document is that errors name the key, not the offset.
/// </param>
/// <param name="Line">1-based line in the source, or null when the problem is a missing
/// key (which has no line of its own).</param>
/// <param name="Message">What is wrong, in the terms the author wrote it in.</param>
public sealed record CsvMappingError(string Path, int? Line, string Message);
