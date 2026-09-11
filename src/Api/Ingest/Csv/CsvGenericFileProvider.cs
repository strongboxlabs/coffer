using System.Globalization;
using System.Text;

using CsvHelper;
using CsvHelper.Configuration;

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
        for (var i = 0; i < usable.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1-based, and counting the header rows the reader already consumed, so the
            // number in an error message is the line the author sees in their editor.
            var lineNumber = mapping.HeaderRows + i + 1;
            var row = usable[i];

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

    private static List<string[]> ReadRows(Stream payload, CsvMapping mapping)
    {
        // detectEncodingFromByteOrderMarks: the first real target file is UTF-8 WITH a
        // BOM, and a BOM read as data puts three invisible bytes on the front of the
        // first field — which is the date, so every row would fail to parse and the file
        // would look corrupt.
        using var reader = new StreamReader(
            payload, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = mapping.Separator.ToString(),
            // The mapping addresses columns by INDEX, because the first real target file
            // has no header row to name them by. Header handling is therefore purely
            // "skip this many lines".
            HasHeaderRecord = false,
            // A short row is a data problem to report per row, not a reason to abandon
            // the file: one malformed line should not cost the other 46.
            BadDataFound = null,
            MissingFieldFound = null,
            IgnoreBlankLines = false,
            TrimOptions = TrimOptions.None,
        };

        using var csv = new CsvReader(reader, config);
        var rows = new List<string[]>();
        var skipped = 0;
        while (csv.Read())
        {
            if (skipped < mapping.HeaderRows) { skipped++; continue; }
            var record = new string[csv.Parser.Count];
            for (var i = 0; i < csv.Parser.Count; i++) record[i] = csv.Parser[i] ?? string.Empty;
            rows.Add(record);
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
        if (mapping.AmountShape == CsvAmountShape.Signed)
        {
            var raw = Field(row, mapping.AmountColumn!.Value);
            if (!TryMoney(raw, out amount))
            {
                errors.Add(Problem(lineNumber, $"'{raw?.Trim()}' is not an amount."));
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
            var hasDebit = TryMoney(debitRaw, out var debit) && debit != 0m;
            var hasCredit = TryMoney(creditRaw, out var credit) && credit != 0m;

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

    /// <summary>
    /// Money with the decoration real exports put around it.
    /// </summary>
    /// <remarks>
    /// Strips everything that is not a digit, sign, separator or point — a currency
    /// symbol, a space, a stray non-breaking space. Handles accounting parentheses,
    /// which mean negative. Deliberately not configurable: there is exactly one right
    /// answer here and a setting would only be a way to get it wrong.
    /// </remarks>
    internal static bool TryMoney(string? raw, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        var negative = text.StartsWith('(') && text.EndsWith(')');
        if (negative) text = text[1..^1];

        var cleaned = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c) || c == '-' || c == '+' || c == '.' || c == ',')
                cleaned.Append(c);
        }
        if (cleaned.Length == 0) return false;

        if (!decimal.TryParse(cleaned.ToString(),
                NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (negative) value = -Math.Abs(value);
        return true;
    }

    private static IngestError Problem(int line, string message) =>
        new(Code: "csv_row_unreadable",
            Message: $"Line {line}: {message} The row was skipped.",
            ConnectionId: null,
            AccountId: null);
}
