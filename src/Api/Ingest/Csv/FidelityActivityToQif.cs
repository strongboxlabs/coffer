using System.Globalization;
using System.Text;

namespace Coffer.Api.Ingest.Csv;

/// <summary>The QIF a Fidelity activity export converts to, with its audit trail.</summary>
/// <param name="Qif">A <c>!Type:Invst</c> document, one record per importable row.</param>
/// <param name="SourceRows">
/// The CSV line behind each emitted record, in the same order. Paired back onto the
/// imported transactions so a row in the register can be traced to the line it came
/// from, which a converted format otherwise loses.
/// </param>
public sealed record FidelityQifConversion(
    string Qif,
    IReadOnlyList<string> SourceRows,
    IReadOnlyList<IngestError> Errors);

/// <summary>
/// Fidelity brokerage "Activity &amp; Orders" CSV, converted to QIF (ADR-0031 Phase 6).
/// </summary>
/// <remarks>
/// <para><b>Every brokerage is a shim to QIF, not an ingest provider of its own.</b> The
/// dangerous knowledge in an investment import is which action a row represents, because
/// a wrong one corrupts cost basis silently. That mapping already exists, documented and
/// tested, in <c>QifFileProvider.ClassifyInvestmentAction</c> — so a per-brokerage
/// provider that emitted Coffer's own action hints would be a second copy of it, per
/// brokerage. This file's entire job is the format: what the columns mean, which rows
/// are junk, and which QIF verb a row is. The verbs then travel one well-trodden path.</para>
///
/// <para>What that leaves here is knowledge no QIF file needs, learned from two real
/// exports:</para>
/// <list type="bullet">
/// <item><b>Columns by NAME.</b> A real export orders them <c>…Type,Price ($),Quantity,…</c>
/// while the widely-cited reference implementation expects <c>…Quantity,Price ($),…</c> —
/// the two money columns, swapped. By position, a share count becomes a unit price.</item>
/// <item><b>The table has to be found, at both ends.</b> Two blank lines precede the
/// header; eight lines of legal prose follow the data, containing commas and quotes, so
/// they parse as ragged rows. Neither end can be skipped by count.</item>
/// <item><b>Quantity is present and ZERO on cash rows</b>, so "the column has a value"
/// says nothing about whether shares moved.</item>
/// <item><b>The date can be inside the sentence.</b> Run Date is when Fidelity processed
/// the row; an Action reading "as of 2025-06-30" says when it happened. Settlement Date
/// is blank on every row of a real export, so the text is the only place the effective
/// date exists.</item>
/// </list>
///
/// <para><b>What is deliberately NOT here: a cash-balance cross-check.</b> The reference
/// implementation compares consecutive rows' <c>Cash Balance</c> and, where the delta
/// disagrees with the stated amount, trusts the balance — "it's the change in balance
/// that's correct, not Fidelity's reported amount". That was implemented here and then
/// disproved by the maintainer's own export on first contact.</para>
///
/// <para>Fidelity does not stamp a per-ROW running balance. Rows arrive in settlement
/// PAIRS that share one balance — a dividend of 55.75 and the sweep of 55.75 into the
/// core fund both carry the balance after the pair — so the within-pair delta is always
/// zero. The check fired on every row of a perfectly good file and "corrected" each
/// amount to 0.00, which on import would have posted two dozen transactions of nothing.
/// The stated amounts were right all along.</para>
///
/// <para>It is not softened to a warning, because a check that cannot distinguish a
/// mis-parse from normal settlement grouping has nothing to warn about; and not repaired
/// to compare across pairs, because the grouping is not understood well enough to do
/// that without guessing a second time.</para>
///
/// <para>Classification mirrors the reference implementation's <c>map_action_type</c>,
/// including its order and its sign fallbacks — which is not a detail: of the seven
/// distinct actions in a real export, four are classified by SIGN alone and match no
/// name rule at all. <c>PURCHASE INTO CORE ACCOUNT</c> contains neither "BUY" nor
/// "BOUGHT".</para>
/// </remarks>
public static class FidelityActivityToQif
{
    /// <summary>
    /// FDRXX (Fidelity Government Cash Reserves), which rides along as a bare CUSIP on
    /// some cash rows where a zero amount makes the row meaningless.
    /// </summary>
    private const string CashReservesCusip = "315994103";

    /// <summary>Header cell names, mapped to the field they carry.</summary>
    /// <remarks>
    /// Both spellings appear across export vintages, and the older variant carries no
    /// cash-balance column at all — so absent columns are tolerated, not assumed.
    /// </remarks>
    private static readonly Dictionary<string, string> ColumnRoles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Run Date"] = "date",
            ["Action"] = "action",
            ["Symbol"] = "symbol",
            ["Description"] = "description",
            ["Security Description"] = "description",
            ["Type"] = "type",
            ["Security Type"] = "type",
            ["Price ($)"] = "price",
            ["Quantity"] = "quantity",
            ["Commission ($)"] = "commission",
            ["Fees ($)"] = "fees",
            ["Amount ($)"] = "amount",
            ["Cash Balance ($)"] = "cash_balance",
            ["Settlement Date"] = "settlement",
        };

    public static FidelityQifConversion Convert(string csvText)
    {
        var errors = new List<IngestError>();
        var qif = new StringBuilder();
        var sources = new List<string>();
        var lines = csvText.Split('\n');

        var headerIndex = FindHeader(lines);
        if (headerIndex < 0)
        {
            errors.Add(Problem("fidelity_no_header",
                "No Fidelity activity header found. This provider expects an "
                + "\"Activity & Orders\" export, whose header row names \"Run Date\" and "
                + "\"Action\"."));
            return new FidelityQifConversion(string.Empty, sources, errors);
        }

        var header = SplitRow(lines[headerIndex]);
        var roles = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            if (ColumnRoles.TryGetValue(header[i].Trim(), out var role) && !roles.ContainsKey(role))
                roles[role] = i;
        }

        foreach (var required in new[] { "date", "action", "amount" })
        {
            if (!roles.ContainsKey(required))
            {
                errors.Add(Problem("fidelity_missing_column",
                    $"The activity header has no '{required}' column, so the file cannot be "
                    + "read. Re-download it as \"Activity & Orders\" without changing the "
                    + "columns."));
                return new FidelityQifConversion(string.Empty, sources, errors);
            }
        }

        // Collected first: the balance cross-check needs the neighbouring row, which a
        // single forward pass cannot see.
        var rows = new List<(int Line, string Raw, List<string> Cells)>();
        for (var line = headerIndex + 1; line < lines.Length; line++)
        {
            var raw = lines[line];
            if (raw.Trim().Length == 0) continue;
            var cells = SplitRow(raw);
            // The boilerplate starts here. Its prose splits into a different number of
            // cells than the table does, and that difference is the only end-of-data
            // marker the format offers — so STOP, rather than skipping and reading on.
            if (cells.Count != header.Count) break;
            rows.Add((line, raw.TrimEnd('\r'), cells));
        }

        qif.Append("!Type:Invst\n");

        for (var index = 0; index < rows.Count; index++)
        {
            var (line, raw, cells) = rows[index];

            var date = Cell(cells, roles, "date");
            // A blank date is Fidelity's own filler, not an error worth reporting.
            if (date.Length == 0) continue;
            // A pending trade carries the literal word where a balance belongs; its
            // amount is not final.
            if (string.Equals(Cell(cells, roles, "cash_balance"), "Processing",
                    StringComparison.OrdinalIgnoreCase)) continue;
            // FDRXX's CUSIP on a zero-amount cash row means nothing at all.
            if (Cell(cells, roles, "symbol") == CashReservesCusip
                && TryMoney(Cell(cells, roles, "amount"), out var probe) && probe == 0m) continue;

            var action = Cell(cells, roles, "action");
            // Fidelity's Run Date is when it PROCESSED the row; when the Action says
            // "as of <date>", that is when the row actually happened, and the two differ.
            // Ten of twenty-four rows in a real export are dated a day late by Run Date
            // alone — which put them one row above the identical transactions already
            // imported from the same plan's QIF, where nothing could see they were the
            // same event.
            if (EffectiveDate(action) is { } effective)
            {
                date = effective;
            }

            if (!TryDate(date, out var postedAt))
            {
                errors.Add(Problem("fidelity_row_unreadable",
                    $"Line {line + 1}: '{date}' is not a date this provider can read "
                    + "(expected MM/DD/YYYY). The row was skipped."));
                continue;
            }

            if (!TryMoney(Cell(cells, roles, "amount"), out var amount))
            {
                errors.Add(Problem("fidelity_row_unreadable",
                    $"Line {line + 1}: '{Cell(cells, roles, "amount")}' is not an amount. "
                    + "The row was skipped."));
                continue;
            }

            var quantity = TryMoney(Cell(cells, roles, "quantity"), out var q) ? q : 0m;
            var verb = QifVerbFor(action, Cell(cells, roles, "type"), quantity, amount);

            qif.Append('D').Append(postedAt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)).Append('\n');
            qif.Append('N').Append(verb).Append('\n');

            // Security: the Description column carries the fund's NAME, which is what
            // QIF's Y field means. The ticker rides in the memo rather than being
            // invented as a name — the file path resolves no securities, and
            // provider_security_mappings learns the pairing from one human Accept.
            var security = Cell(cells, roles, "description");
            var symbol = Cell(cells, roles, "symbol");
            if (IsShareVerb(verb) && security.Length > 0) qif.Append('Y').Append(Sanitise(security)).Append('\n');

            if (quantity != 0m) qif.Append('Q').Append(Units(Math.Abs(quantity))).Append('\n');
            if (TryMoney(Cell(cells, roles, "price"), out var price) && price != 0m)
                qif.Append('I').Append(Units(price)).Append('\n');

            var fees = Fees(cells, roles);
            if (fees is not null) qif.Append('O').Append(Money(fees.Value)).Append('\n');

            qif.Append('T').Append(Money(Math.Abs(amount))).Append('\n');
            // The Action sentence is the most human-readable thing on the row, and it is
            // the only place the ticker survives for a reader.
            qif.Append('M').Append(Sanitise(
                symbol.Length > 0 ? $"{action} [{symbol}]" : action)).Append('\n');
            qif.Append("^\n");

            sources.Add(raw);
        }

        return new FidelityQifConversion(qif.ToString(), sources, errors);
    }

    /// <summary>
    /// One Fidelity row to a QIF investment verb.
    /// </summary>
    /// <remarks>
    /// <para>Reverse-engineered from tkralphs/MoneydanceScripts' <c>map_action_type</c>.
    /// Two properties of it matter more than any individual rule.</para>
    ///
    /// <para>The decision is a JOINT function of the action sentence, the type cell, and
    /// the SIGNS of quantity and amount — never of the text alone. And the ORDER is
    /// load-bearing: <c>DIVIDEND</c> is tested before <c>REINVEST</c>, so a sentence
    /// carrying both is a cash dividend.</para>
    ///
    /// <para>The sign fallbacks at the end are not a safety net, they are the main path:
    /// four of the seven distinct actions in a real export reach them.</para>
    ///
    /// <para>Cash arrivals and departures with no shares become <c>XIn</c>/<c>XOut</c>,
    /// which the QIF provider deliberately gives the BANK shape — the right answer for a
    /// rollover or an ACAT cash leg, which are not security transactions at all.</para>
    /// </remarks>
    internal static string QifVerbFor(string action, string type, decimal quantity, decimal amount)
    {
        var a = action.ToUpperInvariant().Trim();
        var t = type.ToUpperInvariant().Trim();

        static bool Has(string haystack, params string[] needles) =>
            needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

        if (Has(a, "BUY", "BOUGHT", "MERGER MER", "TENDERED TEX", "DISTRIBUTION") && quantity > 0)
            return "Buy";
        if (Has(a, "SELL", "SOLD", "MERGER MER", "TENDERED TEX") && quantity < 0)
            return "Sell";
        if (Has(a, "DIVIDEND")) return "Div";
        if (Has(a, "INTEREST", "IN LIEU") && amount > 0) return "IntInc";
        // The quantity guard is load-bearing, not defensive. The Action cell is a SENTENCE
        // that contains the fund's name ("PURCHASE INTO CORE ACCOUNT ... FIDELITY
        // TAX-EXEMPT MONEY MARKET (FTEXX)"), so a substring test for "TAX" or "FEE" reads
        // the security, not the verb. Without it, every purchase or reinvestment of a
        // tax-exempt or tax-free fund — FTEXX and FZEXX are common core positions, FTABX a
        // common holding — lands here instead of at Buy/ReinvDiv below, posting the cash
        // with the wrong sign and dropping the shares entirely.
        //
        // A genuine fee or withholding is a pure cash row, and Fidelity writes Quantity as
        // an explicit 0 on those, so requiring it costs nothing and cannot be read off the
        // fund name.
        if (quantity == 0m
            && (Has(a, "TAX", "FEE", "IN LIEU") || Has(t, "TAX", "FEE", "IN LIEU"))
            && amount < 0)
            return "MiscExp";
        if (Has(a, "REINVEST")) return "ReinvDiv";

        // Shares moving between institutions are a share movement, not a purchase. The
        // QIF provider maps ShrsIn/ShrsOut to plain buy/sell on purpose — the X variants
        // need an asset counter-account and dead-end rows that have none.
        if (Has(a, "TRANSFER OF ASSETS", "ACAT", "ROLLOVER"))
            return quantity > 0 ? "ShrsIn" : quantity < 0 ? "ShrsOut" : amount >= 0 ? "XIn" : "XOut";

        if (quantity > 0) return "Buy";
        if (quantity < 0) return "Sell";
        return amount >= 0 ? "XIn" : "XOut";
    }

    /// <summary>
    /// The "as of" date Fidelity splices into an Action sentence, or null.
    /// </summary>
    /// <remarks>
    /// <para>Written as <c>PURCHASE INTO CORE ACCOUNT as of 2025-06-30 FIDELITY …</c> —
    /// mid-sentence, so it is neither a prefix nor a column. Both orders appear in the
    /// wild (ISO and US), and Settlement Date is blank on every row of a real export, so
    /// this text is the only place the effective date exists.</para>
    ///
    /// <para>Returned in the Run Date column's own format so one parser handles both.</para>
    /// </remarks>
    internal static string? EffectiveDate(string action)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            action,
            @"\bas of\s+(?<iso>\d{4}-\d{2}-\d{2})|\bas of\s+(?<us>\d{1,2}/\d{1,2}/\d{4})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        if (!match.Success) return null;

        if (match.Groups["us"].Success) return match.Groups["us"].Value;

        var iso = match.Groups["iso"].Value;
        // Normalised to the column's format rather than parsed here, so TryDate stays
        // the single place that decides what a Fidelity date is.
        return $"{iso[5..7]}/{iso[8..10]}/{iso[0..4]}";
    }

    /// <summary>Whether a verb names a security, and so needs QIF's <c>Y</c> field.</summary>
    private static bool IsShareVerb(string verb) =>
        verb is "Buy" or "Sell" or "Div" or "ReinvDiv" or "ShrsIn" or "ShrsOut" or "IntInc";

    private static int FindHeader(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var cells = SplitRow(lines[i]);
            if (cells.Count >= 5
                && cells.Any(c => string.Equals(c.Trim(), "Run Date", StringComparison.OrdinalIgnoreCase))
                && cells.Any(c => string.Equals(c.Trim(), "Action", StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    private static decimal? Fees(List<string> cells, Dictionary<string, int> roles)
    {
        var total = 0m;
        var any = false;
        foreach (var role in new[] { "commission", "fees" })
        {
            if (TryMoney(Cell(cells, roles, role), out var v) && v != 0m)
            {
                total += v;
                any = true;
            }
        }

        return any ? total : null;
    }

    private static string Cell(List<string> cells, Dictionary<string, int> roles, string role) =>
        roles.TryGetValue(role, out var i) && i < cells.Count ? cells[i].Trim() : string.Empty;

    /// <summary>
    /// Cash amounts, whose destination columns are all 2dp. Four decimals is headroom,
    /// not precision — a money cell in this export never carries more.
    /// </summary>
    private static string Money(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// Share counts and unit prices, which are NOT money and must not be rounded here.
    /// <para>
    /// This projection is transport. Anything it drops is unrecoverable, because no
    /// later stage can see the CSV cell. The destinations are wider than four decimals
    /// in every direction — <c>ingest_shares</c> is NUMERIC(28,8), and the
    /// <c>txn_legs.quantity</c> / <c>holdings.quantity</c> / <c>lots.quantity</c> that
    /// a row grows into are NUMERIC(25,12) precisely because migration 043 widened them
    /// for fractional shares. Rounding to 4dp here turned a real 0.000980392-unit row
    /// (which this repo already ships as a QIF and an OFX fixture) into 0.001 — a 2%
    /// overstatement carried into cost basis and realized gain — and silently zeroed any
    /// holding under 0.00005 units.
    /// </para>
    /// <para>
    /// So: emit the decimal exactly as parsed and let the column round once, at its own
    /// scale. Rounding at any earlier scale rounds twice.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Twenty-eight '#' is the full scale a <see cref="decimal"/> can hold, so this
    /// rounds nothing; the '#' also drop the source's trailing zeros, which keeps the
    /// emitted document stable ("5", not "5.00") without costing a digit that matters.
    /// </remarks>
    private static string Units(decimal value) =>
        value.ToString(
            "0.############################", CultureInfo.InvariantCulture);

    /// <summary>QIF is line-oriented and <c>^</c> ends a record, so neither may appear.</summary>
    private static string Sanitise(string value) =>
        value.Replace('\n', ' ').Replace('\r', ' ').Replace('^', '-').Trim();

    private static bool TryDate(string value, out DateTime utc)
    {
        utc = default;
        if (value.Length == 0) return false;
        var cleaned = value.TrimEnd('*', ' ');
        if (!DateTime.TryParseExact(cleaned, ["MM/dd/yyyy", "M/d/yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        utc = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
        return true;
    }

    /// <summary>Money as Fidelity writes it: quoted, signed, comma-grouped, or blank.</summary>
    private static bool TryMoney(string value, out decimal amount)
    {
        amount = 0m;
        if (value.Length == 0) return false;
        var cleaned = value.Replace(",", string.Empty).Replace("$", string.Empty).Trim();
        if (cleaned.Length == 0) return false;
        return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out amount);
    }

    /// <summary>
    /// Split one CSV line, honouring double quotes.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than handed to CsvHelper because the boilerplate trailer is not
    /// valid CSV at all, and a strict reader throws on it partway through a file whose
    /// real data has already been read.
    /// </remarks>
    private static List<string> SplitRow(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        foreach (var c in line.TrimEnd('\r'))
        {
            if (quoted)
            {
                if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else cell.Append(c);
        }

        cells.Add(cell.ToString());
        return cells;
    }

    private static IngestError Problem(string code, string message) =>
        new(Code: code, Message: message, ConnectionId: null, AccountId: null);
}
