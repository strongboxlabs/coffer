using System.Text;
using System.Text.Json;

using Coffer.Api.Ingest.Qif;

namespace Coffer.Api.Ingest.Csv;

/// <summary>
/// Fidelity brokerage "Activity &amp; Orders" CSV (ADR-0031 Phase 6).
/// </summary>
/// <remarks>
/// <para><b>A shim, not an ingest path.</b> The CSV is converted to QIF by
/// <see cref="FidelityActivityToQif"/> and handed to the QIF provider, which does the
/// actual import. Every brokerage added after this one is another shim — a text
/// transform — rather than another set of ingest semantics.</para>
///
/// <para>The reason is where the danger lives. Deciding which action a row represents is
/// what corrupts cost basis when it goes wrong, and that mapping already exists,
/// documented and tested, in <c>QifFileProvider.ClassifyInvestmentAction</c>. A
/// per-brokerage provider emitting Coffer's own action hints would be a second copy of
/// it — then a third, then a fourth. Shims keep one copy, and make each brokerage's
/// contribution purely a question of format.</para>
///
/// <para><b>Per brokerage, not per account.</b> One provider reads any Fidelity brokerage
/// export; the destination account is chosen at upload time, as for every other file
/// import.</para>
///
/// <para><b>The conversion is invisible but not hidden.</b> The user picks a CSV and gets
/// transactions; nobody is asked to know QIF was involved. But every imported row carries
/// both the CSV line it came from and the QIF record it became, on
/// <c>RawProviderPayload</c> — because a converted format otherwise loses the trail
/// exactly when it is wanted, which is when a number looks wrong.</para>
///
/// <para><b>The provider key stays this one's.</b> Delegation is an implementation
/// detail, and <c>provider_security_mappings</c> is keyed by provider — so a symbol
/// learned here is remembered as Fidelity's rather than pooled with genuine QIF imports
/// from somewhere else.</para>
/// </remarks>
public sealed class FidelityActivityFileProvider : IFileProvider
{
    /// <summary>Dispatch key. One per brokerage.</summary>
    public const string Key = "csv-fidelity";

    /// <summary>The single account a Fidelity activity export describes.</summary>
    public const string SingleAccountKey = "fidelity";

    public string ProviderKey => Key;

    public async Task<FileResult> ParseAsync(
        Stream payload,
        FileIngestContext context,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(payload, detectEncodingFromByteOrderMarks: true);
        var csv = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var converted = FidelityActivityToQif.Convert(csv);
        if (converted.SourceRows.Count == 0)
        {
            return new FileResult([], [], converted.Errors);
        }

        using var qifStream = new MemoryStream(Encoding.UTF8.GetBytes(converted.Qif));
        var parsed = await new QifFileProvider()
            .ParseAsync(qifStream, context, cancellationToken)
            .ConfigureAwait(false);

        var errors = new List<IngestError>(converted.Errors);
        errors.AddRange(parsed.Errors);

        // The shim only ever emits verbs the QIF provider supports, so a count mismatch
        // means one was dropped — a bug in this file, not a fact about the user's data.
        // Reported rather than papered over, because the alternative is pairing the wrong
        // CSV line to the wrong transaction and making the trail lie.
        var paired = parsed.Transactions.Count == converted.SourceRows.Count;
        if (!paired && parsed.Transactions.Count > 0)
        {
            errors.Add(new IngestError(
                Code: "fidelity_shim_row_mismatch",
                Message: $"The converter produced {converted.SourceRows.Count} records but "
                    + $"{parsed.Transactions.Count} were read back, so rows could not be "
                    + "traced to their source lines. The import itself is unaffected.",
                ConnectionId: null,
                AccountId: null));
        }

        // Scanned ONCE. This was RecordAt(qif, i) inside the loop, which re-split the
        // whole document per row: quadratic in rows, and eager, so the early return
        // saved nothing. A 5 MB export — which the endpoint's own cap admits — is ~20k
        // rows over ~120k lines, i.e. billions of substring allocations in one request.
        var records = QifRecords(converted.Qif);

        var transactions = new List<IngestedTransaction>(parsed.Transactions.Count);
        for (var i = 0; i < parsed.Transactions.Count; i++)
        {
            var t = parsed.Transactions[i];
            transactions.Add(t with
            {
                // Fidelity's account, not the QIF provider's sentinel — the discovered
                // account below has to agree with it for the binding step to work.
                ProviderAccountId = SingleAccountKey,
                RawProviderPayload = Trail(
                    paired ? converted.SourceRows[i] : null,
                    i < records.Count ? records[i] : null),
            });
        }

        var accounts = new List<DiscoveredFileAccount>
        {
            new(ProviderAccountId: SingleAccountKey,
                AccountType: "investment",
                Currency: null,
                TransactionCount: transactions.Count),
        };

        return new FileResult(transactions, accounts, errors);
    }

    /// <summary>
    /// Both halves of the sausage, for the moment someone asks why a number looks wrong.
    /// </summary>
    /// <remarks>
    /// JSON rather than prose so it survives being read by something other than a person.
    /// This lands on <c>txn_headers.provider_raw_payload</c>, which the
    /// classifier-iteration and per-row debugging surfaces already read.
    /// </remarks>
    private static string Trail(string? csvRow, string? qifRecord) =>
        JsonSerializer.Serialize(new
        {
            source = "fidelity-activity-csv",
            csv = csvRow,
            qif = qifRecord,
        });

    /// <summary>
    /// Every <c>^</c>-terminated record of a QIF document, in order, in one pass.
    /// </summary>
    private static List<string> QifRecords(string qif)
    {
        var records = new List<string>();
        var record = new StringBuilder();

        foreach (var line in qif.AsSpan().EnumerateLines())
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("!", StringComparison.Ordinal)) continue;
            if (trimmed.SequenceEqual("^"))
            {
                records.Add(record.ToString().TrimEnd('\n'));
                record.Clear();
                continue;
            }

            record.Append(trimmed).Append('\n');
        }

        return records;
    }
}
