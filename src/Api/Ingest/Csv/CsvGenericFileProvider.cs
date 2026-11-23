using System.Globalization;
using System.Text;

using CsvHelper;

namespace Coffer.Api.Ingest.Csv;

/// <summary>
/// Reads a delimited export according to a <see cref="CsvMapping"/> (ADR-0031 Phase 5).
/// </summary>
/// <remarks>
/// <para>
/// One provider for every institution whose export is describable by a mapping, instead
/// of a class per bank. The mapping arrives already validated on
/// <see cref="FileIngestContext.CsvMapping"/> — resolved by the endpoint from a saved row
/// or from draft YAML — so this class parses and never re-checks configuration.
/// </para>
/// <para>
/// <b>PURE, like every other provider.</b> Stream in, <see cref="FileResult"/> out, no
/// database. That is what makes the preview endpoint free: parse, return, write nothing.
/// </para>
/// <para>
/// <b>CsvHelper, not a hand-rolled splitter</b>, unlike the QIF parser. ADR-0042 hand-rolled
/// QIF because the only maintained .NET package had been dormant since 2022; here the
/// precedent that matters is OfxNet — take a maintained library when one exists. CSV
/// looks trivial and is not: quoted fields containing the delimiter, doubled quotes,
/// embedded newlines inside quotes. A splitter that gets those wrong does not throw, it
/// shifts an amount into a memo field, and that is money corruption that parses cleanly.
/// </para>
/// <para>
/// <b>Single-account-implicit</b>, like QIF. A delimited export carries no account header,
/// so one sentinel <see cref="DiscoveredFileAccount"/> is surfaced and the user binds it
/// to a Coffer account in the dialog.
/// </para>
/// <para>
/// <b>No dedup.</b> The external id is unique per import by construction — see
/// <see cref="ExternalIdFor"/>. That is a decision, not an oversight.
/// </para>
/// </remarks>
public sealed class CsvGenericFileProvider : IFileProvider
{
    /// <summary>Provider key, as stored on <c>txn_headers.provider_key</c>.</summary>
    public const string Key = "csv-generic";

    /// <summary>
    /// The one account a delimited file implies. Same sentinel idea as QIF: the file has
    /// no account header, so there is exactly one block and the user binds it.
    /// </summary>
    public const string SingleAccountKey = "csv";

    public string ProviderKey => Key;

    public Task<FileResult> ParseAsync(
        Stream payload, FileIngestContext context, CancellationToken cancellationToken)
    {
        var mapping = context.CsvMapping
            ?? throw new InvalidOperationException(
                "csv-generic was invoked without a mapping. The endpoint resolves one "
                + "before constructing the context; reaching here without it is a wiring "
                + "bug, not user error.");

        var errors = new List<IngestError>();
        var rows = ReadRows(payload, mapping);

        // Footers are dropped by COUNT, from the end, before anything is interpreted: a
        // statement's trailing "Total" line is not a transaction, and letting it reach
        // the row parser would report a parse error for a line that is doing its job.
        var usable = mapping.FooterRows > 0 && rows.Count >= mapping.FooterRows
            ? rows.Take(rows.Count - mapping.FooterRows).ToList()
            : rows;

        var transactions = new List<IngestedTransaction>();
        foreach (var (lineNumber, row) in usable)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A wholly blank line is skipped in silence. Exports end with one constantly,
            // and reporting it as a problem would train the reader to ignore warnings.
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            var parsed = ParseRow(row, mapping, lineNumber, errors);
            if (parsed is not null) transactions.Add(parsed);
        }

        var accounts = new List<DiscoveredFileAccount>
        {
            new(ProviderAccountId: SingleAccountKey,
                AccountType: "bank",
                Currency: null,
                TransactionCount: transactions.Count),
        };

        return Task.FromResult(new FileResult(transactions, accounts, errors));
    }

    // ----- reading ------------------------------------------------------------

    /// <summary>
    /// Every record after the header rows, with the 1-based line of the file it starts on.
    /// </summary>
    /// <remarks>
    /// The line is the reader's, counting every line of the file: header rows, blank lines,
    /// and the lines inside a quoted field that spans several. So the number in an error
    /// message is the line the author sees in their editor.
    /// </remarks>
    private static List<(int Line, string[] Fields)> ReadRows(Stream payload, CsvMapping mapping)
    {
        // detectEncodingFromByteOrderMarks: an export can be UTF-8 WITH a BOM, and a
        // BOM read as data puts three invisible bytes on the front of the first field —
        // which, when that column is the date, makes every row fail to parse and the
        // file look corrupt.
        using var reader = new StreamReader(
            payload, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        // Every option that decides how the file splits into records and fields is set,
        // including those equal to CsvHelper's default: several of its defaults would
        // change what a mapping means, and none may change with a package update.
        var options = new CsvReaderOptions
        {
            // Never detected. A detected separator overrides the mapping, and an Excel
            // `sep=` first line would be consumed as an instruction instead of counted
            // as a header row.
            Separator = mapping.Separator,
            DetectSeparatorHint = false,
            // The mapping addresses columns by INDEX, because a file need not have a
            // header row to name them by. Header handling is therefore purely
            // "skip this many lines".
            HasHeader = false,
            // A blank line is a record, so header_rows counts it like the author does.
            SkipEmptyLines = false,
            // A '#' is data: a payee or a reference can start with one.
            CommentPrefix = null,
            // RFC 4180: a quote opens a field only as its first character, two quotes are
            // one, and a quoted field may span lines. A backslash is data.
            Mode = CsvMode.Rfc4180,
            Quote = '"',
            AllowLineBreaksInQuotes = true,
            // Whitespace is the field's. ParseRow trims what it reads.
            TrimWhitespace = false,
            // A short row is a data problem to report per row, not a reason to abandon
            // the file: one malformed line should not cost every other line.
            MalformedData = CsvMalformedDataHandling.Recover,
            FieldCountMismatch = CsvFieldCountHandling.Allow,
            TrackLineNumbers = true,
        };

        using var csv = CsvReader.Create(reader, options, leaveOpen: true);
        var rows = new List<(int Line, string[] Fields)>();
        var skipped = 0;
        while (csv.Read())
        {
            if (skipped < mapping.HeaderRows) { skipped++; continue; }
            var record = new string[csv.FieldCount];
            for (var i = 0; i < csv.FieldCount; i++) record[i] = csv.GetString(i);
            rows.Add((checked((int)csv.LineNumber), record));
        }
        return rows;
    }

    private static IngestedTransaction? ParseRow(
        string[] row, CsvMapping mapping, int lineNumber, List<IngestError> errors)
    {
        var date = Field(row, mapping.DateColumn);
        if (date is null)
        {
            errors.Add(Problem(lineNumber, $"row has no column {mapping.DateColumn} for the date."));
            return null;
        }
        if (!DateTime.TryParseExact(date.Trim(), mapping.DateFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var postedAt))
        {
            errors.Add(Problem(lineNumber,
                $"'{date.Trim()}' is not a date in the format {mapping.DateFormat}."));
            return null;
        }

        decimal amount;
        var separator = mapping.DecimalSeparator;
        if (mapping.AmountShape == CsvAmountShape.Signed)
        {
            var raw = Field(row, mapping.AmountColumn!.Value);
            var cell = ReadMoney(raw, separator, out amount);
            // A cell with no digits reads with neither separator, so the message does
            // not name one: switching the setting cannot fix that row.
            if (cell == MoneyCell.Empty)
            {
                errors.Add(Problem(lineNumber, $"'{raw?.Trim()}' is not an amount."));
                return null;
            }
            if (cell == MoneyCell.Unreadable)
            {
                errors.Add(NotAnAmount(lineNumber, raw, separator));
                return null;
            }
            // The whole point of the flag. The file signs from the ISSUER's perspective
            // (a purchase is positive because it increases what you owe);
            // IngestedTransaction.Amount is signed from the user's perspective on the
            // target account, where a purchase is money out. Applied uniformly, so a
            // refund or a payment lands positive without a second rule.
            if (mapping.AmountInvert) amount = -amount;
        }
        else
        {
            var debitRaw = Field(row, mapping.DebitColumn!.Value);
            var creditRaw = Field(row, mapping.CreditColumn!.Value);
            var debitCell = ReadMoney(debitRaw, separator, out var debit);
            var creditCell = ReadMoney(creditRaw, separator, out var credit);

            // A cell with digits that does not read is a row error, not an empty side: a
            // decimal comma read as absent would land the row on the other column's
            // value, or report it as having none, and neither names the real problem.
            // A cell with no digits at all (blank, a lone dash, a bare symbol) is the
            // empty side, as exports pad the unused column with one.
            if (debitCell == MoneyCell.Unreadable)
            {
                errors.Add(NotAnAmount(lineNumber, debitRaw, separator));
                return null;
            }
            if (creditCell == MoneyCell.Unreadable)
            {
                errors.Add(NotAnAmount(lineNumber, creditRaw, separator));
                return null;
            }

            var hasDebit = debitCell == MoneyCell.Amount && debit != 0m;
            var hasCredit = creditCell == MoneyCell.Amount && credit != 0m;

            if (hasDebit && hasCredit)
            {
                // Both filled is not a row this can interpret, and picking one would be
                // inventing an answer about money.
                errors.Add(Problem(lineNumber,
                    "both the debit and credit columns carry a value; only one can apply."));
                return null;
            }
            if (!hasDebit && !hasCredit)
            {
                errors.Add(Problem(lineNumber, "neither the debit nor the credit column has an amount."));
                return null;
            }
            // Debit is money out of the account, so it is the negative side. Abs() first:
            // some exports already sign their debit column, and negating a negative would
            // turn a charge into a deposit.
            amount = hasDebit ? -Math.Abs(debit) : Math.Abs(credit);
        }

        var payee = Field(row, mapping.PayeeColumn)?.Trim();
        var memo = mapping.MemoColumn is { } memoColumn
            ? Field(row, memoColumn)?.Trim()
            : null;

        var posted = DateTime.SpecifyKind(postedAt, DateTimeKind.Utc);
        return new IngestedTransaction(
            ExternalId: ExternalIdFor(),
            PostedAt: posted,
            // A delimited export carries ONE date. Passing it as the transacted date too
            // matches what the OFX path does when a feed omits one, and beats inventing
            // a distinction the file does not make.
            TransactedAt: posted,
            Amount: amount,
            Payee: string.IsNullOrWhiteSpace(payee) ? null : payee,
            Description: string.IsNullOrWhiteSpace(memo) ? payee : memo,
            // A statement line has already posted; nothing in a delimited export marks a
            // row as pending.
            Pending: false,
            ProviderAccountId: SingleAccountKey);
    }

    /// <summary>
    /// A NEW id for every row of every import, deliberately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no dedup for delimited imports, and this is how that decision is
    /// implemented rather than an absence. A delimited row carries no issuer-assigned id,
    /// so identity would have to be derived from content — and content cannot separate
    /// two genuinely identical transactions (same date, same amount, same merchant, two
    /// actual coffees). A content hash collapses them and eats a real row; a hash
    /// including the row ordinal duplicates the whole file the moment a download window
    /// shifts by a day. Both fail silently, in a ledger reconciled against real balances.
    /// </para>
    /// <para>
    /// What replaces it is UNDO: mig 221 stamps the import on every row it writes, and
    /// undoing one removes exactly that set.
    /// </para>
    /// <para>
    /// It cannot simply be NULL. <c>ck_txn_headers_external_id_for_non_manual</c> (mig
    /// 109) is <c>external_id IS NOT NULL OR origin = 'manual'</c>, and an import writes
    /// <c>origin = 'file_import'</c> — so a value is required. A deliberately
    /// non-matching one satisfies the constraint and guarantees the dedup lookup never
    /// hits, which is exactly the intent stated out loud.
    /// </para>
    /// </remarks>
    private static string ExternalIdFor() => "csv:" + Guid.NewGuid().ToString("n");

    // ----- primitives ---------------------------------------------------------

    /// <summary>The 1-based column, or null when the row is too short to have it.</summary>
    private static string? Field(string[] row, int oneBasedColumn) =>
        oneBasedColumn >= 1 && oneBasedColumn <= row.Length ? row[oneBasedColumn - 1] : null;

    /// <summary>What an amount cell holds, once read.</summary>
    internal enum MoneyCell
    {
        /// <summary>No digits at all: blank, a lone sign, a bare currency symbol.</summary>
        Empty,

        /// <summary>A figure, read one way only.</summary>
        Amount,

        /// <summary>Digits that do not read as an amount with the layout's separator.</summary>
        Unreadable,
    }

    /// <summary>
    /// Money with the decoration an export can put around it, read with the layout's
    /// decimal separator (ADR-0114 D15).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Strips everything that is not a digit, sign, point or comma: a currency symbol, a
    /// space, a no-break space. Handles accounting parentheses, which mean negative, and
    /// one sign at either end.
    /// </para>
    /// <para>
    /// <b>The separator decides, never the cell.</b> The layout's decimal character may
    /// appear once; the other character groups thousands and is accepted only as whole
    /// groups of three digits after a first group of one to three, or as lakh grouping
    /// (a first group of one or two, groups of two, a last group of three), which
    /// invariant parsing always read for a version-1 layout. The first group never starts
    /// with a zero. This is <see cref="AmountGrouping"/>, the rule the QIF reader shares.
    /// Anything else is <see cref="MoneyCell.Unreadable"/> rather than read
    /// some other way: invariant number parsing took a comma followed by two digits as
    /// grouping and read a decimal-comma cell as a far larger figure, silently, which is
    /// the defect this replaces.
    /// </para>
    /// <para>
    /// The figure keeps the scale it was written with, trailing zeros included, so the
    /// bank's figure as sent is what an import that rounds records.
    /// </para>
    /// </remarks>
    internal static MoneyCell ReadMoney(string? raw, CsvDecimalSeparator separator, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return MoneyCell.Empty;

        var text = raw.Trim();
        var parenthesised = text.Length >= 2 && text.StartsWith('(') && text.EndsWith(')');
        if (parenthesised) text = text[1..^1];

        var kept = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c) || c == '-' || c == '+' || c == '.' || c == ',')
                kept.Append(c);
        }
        var body = kept.ToString();
        if (!body.Any(char.IsAsciiDigit)) return MoneyCell.Empty;

        // One sign, leading or trailing; a sign anywhere else is not a figure.
        var minus = false;
        if (body[0] is '-' or '+')
        {
            minus = body[0] == '-';
            body = body[1..];
        }
        else if (body[^1] is '-' or '+')
        {
            minus = body[^1] == '-';
            body = body[..^1];
        }
        if (body.Contains('-', StringComparison.Ordinal) || body.Contains('+', StringComparison.Ordinal))
            return MoneyCell.Unreadable;

        var (decimalChar, groupChar) = separator == CsvDecimalSeparator.Comma ? (',', '.') : ('.', ',');
        var at = body.IndexOf(decimalChar, StringComparison.Ordinal);
        if (at >= 0 && body.IndexOf(decimalChar, at + 1) >= 0) return MoneyCell.Unreadable;

        var whole = at >= 0 ? body[..at] : body;
        var fraction = at >= 0 ? body[(at + 1)..] : "";
        if (fraction.Contains(groupChar, StringComparison.Ordinal)) return MoneyCell.Unreadable;

        if (whole.Contains(groupChar, StringComparison.Ordinal))
        {
            // The grouping rule QIF reads by too (AmountGrouping).
            if (!AmountGrouping.IsGrouped(whole, groupChar)) return MoneyCell.Unreadable;
            whole = whole.Replace(groupChar.ToString(), string.Empty, StringComparison.Ordinal);
        }

        var canonical = (whole.Length == 0 ? "0" : whole) + (fraction.Length == 0 ? "" : "." + fraction);
        if (!decimal.TryParse(canonical, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value))
        {
            // Only overflow reaches here: the canonical text is digits and one point.
            return MoneyCell.Unreadable;
        }

        if (minus || parenthesised) value = -Math.Abs(value);
        return MoneyCell.Amount;
    }

    /// <summary>
    /// The row error for an amount cell that does not read, naming the separator it was
    /// read with (ADR-0114 D15, the CSV wizard paragraph).
    /// </summary>
    private static IngestError NotAnAmount(int line, string? raw, CsvDecimalSeparator separator) =>
        Problem(line, $"'{raw?.Trim()}' is not an amount with "
            + (separator == CsvDecimalSeparator.Comma ? "a comma" : "a point")
            + " as the decimal separator.");

    private static IngestError Problem(int line, string message) =>
        new(Code: "csv_row_unreadable",
            Message: $"Line {line}: {message} The row was skipped.",
            ConnectionId: null,
            AccountId: null);
}
