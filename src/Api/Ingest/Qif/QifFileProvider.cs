using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Coffer.Api.Ingest.Qif;

/// <summary>
/// QIF (Quicken Interchange Format) file provider — ADR-0042.
/// Handles investment (<c>!Type:Invst</c>) and bank
/// (<c>!Type:Bank</c> / <c>!Type:CCard</c>) sections. Built for
/// a workplace 401(k) plan export (QIF or CSV are its only
/// download formats; no OFX/QFX), but the parser is the generic QIF
/// line grammar, not the workplace plan-specific.
/// </summary>
/// <remarks>
/// <para><b>Hand-rolled parser, no NuGet dependency (ADR-0042 §D1).</b>
/// QIF is a trivially simple line-based format: one single-letter
/// field code per line, records terminated by <c>^</c>, sections
/// switched by <c>!Type:</c> headers. The only maintained .NET QIF
/// package with investment support (Hazzik.Qif) has been dormant
/// since 2022; the issuer-specific quirks below (parenthetical fund
/// codes as the security identity, no account metadata) need custom
/// handling on top of any library anyway. A ~200-line internal
/// parser gives full control with zero supply-chain surface.</para>
///
/// <para><b>The importer reports the feed; it does not impose a cash
/// model (ADR-0042 §"Cash model").</b> Each QIF action maps to the
/// nearest ADR-0027 action and the wire amount is carried as the
/// bank-shape cash-flow hint, signed by the action's convention. The
/// importer does NOT interpret memo strings ("Contribution",
/// "Fees") to net cash to zero or synthesize offsetting rows — the
/// cash model is a stable property of the ADR-0027 action, owned by
/// the user, adjusted in the editor (e.g. <c>buy</c> → <c>buyx</c>
/// for a transfer-funded contribution). <c>dividend_reinvest</c>
/// carries $0 because that is the action's net-zero contract on
/// every feed, not because QIF said so.</para>
///
/// <para><b>Single-account-implicit.</b> A QIF file (at least the
/// workplace plan dialect) carries no account header — it is one
/// account's transaction list. The provider surfaces exactly one
/// <see cref="DiscoveredFileAccount"/> with a sentinel
/// <c>ProviderAccountId</c> (<see cref="SingleAccountKey"/>); the
/// user binds it to a target Coffer account in the dialog. Every
/// transaction carries that same sentinel so the orchestrator's
/// per-provider-account filter (a no-op for single-account files)
/// passes them all through.</para>
///
/// <para><b>Synthetic external id.</b> QIF has no FITID. The dedup
/// key is a SHA-1 of the target account id + the row's stable fields
/// (date, action, security, qty, price, amount, memo). Re-importing
/// the same file into the same account is idempotent; folding the
/// target account id in prevents two distinct accounts' identical-
/// shaped rows from colliding in the per-provider
/// <c>(ledger, provider_key, external_id)</c> dedup scope. Genuinely
/// identical rows (every field equal) collapse to one — the standard
/// QIF limitation, documented in ADR-0042.</para>
/// </remarks>
public sealed class QifFileProvider : IFileProvider
{
    /// <summary>Provider dispatch key. Matches
    /// <c>IngestOrchestrator.ProviderOriginFor["qif"]</c> and the
    /// <c>provider_key</c> persisted on inserted rows.</summary>
    public const string Key = "qif";

    /// <summary>Sentinel provider-account id for the single implicit
    /// account a QIF file represents. The dialog echoes it back as
    /// the import filter; the orchestrator matches every row against
    /// it.</summary>
    public const string SingleAccountKey = "qif";

    public string ProviderKey => Key;

    // Trailing "(CODE)" on a security name — the workplace plan encodes a
    // stable per-fund code there (e.g. "BOND FUND(BFND)"). We
    // lift it as the ticker hint for the provider_security_mappings
    // rail (ADR-0038); the user maps it to a real Coffer security
    // once and every same-code row auto-resolves thereafter.
    private static readonly Regex TrailingParenCode =
        new(@"\(([^)]+)\)\s*$", RegexOptions.Compiled);

    public async Task<FileResult> ParseAsync(
        Stream payload,
        FileIngestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);

        string text;
        using (var reader = new StreamReader(payload, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var transactions = new List<IngestedTransaction>();
        var errors = new List<IngestError>();
        var supported = 0;
        var sawInvestmentSection = false;
        var sawBankSection = false;

        QifSection section = QifSection.Unknown;
        var record = new QifRecordBuilder();

        // QIF lines are CR / LF / CRLF separated; split on both.
        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var raw in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            var code = line[0];
            var value = line.Length > 1 ? line[1..].Trim() : string.Empty;

            switch (code)
            {
                case '!':
                    // Section / option header, e.g. "!Type:Invst".
                    section = ClassifySection(value);
                    if (section == QifSection.Investment) sawInvestmentSection = true;
                    else if (section == QifSection.Bank) sawBankSection = true;
                    record = new QifRecordBuilder();
                    break;

                case '^':
                    // Record terminator — flush the accumulated record.
                    FlushRecord(section, record, context, transactions, errors, ref supported);
                    record = new QifRecordBuilder();
                    break;

                default:
                    record.Apply(code, value);
                    break;
            }
        }

        // A trailing record without a closing '^' (some exporters omit
        // the final terminator) still flushes.
        if (record.HasAny)
            FlushRecord(section, record, context, transactions, errors, ref supported);

        var accountType =
            sawInvestmentSection ? "investment"
            : sawBankSection ? "bank"
            : "bank";

        var discovered = new List<DiscoveredFileAccount>
        {
            new(
                ProviderAccountId: SingleAccountKey,
                AccountType: accountType,
                Currency: null,            // QIF carries no currency
                TransactionCount: supported),
        };

        return new FileResult(transactions, discovered, errors);
    }

    private static QifSection ClassifySection(string headerValue)
    {
        // headerValue is the part after '!', e.g. "Type:Invst",
        // "Type:Bank", "Type:CCard", "Account", "Option:AutoSwitch".
        if (headerValue.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
        {
            var type = headerValue["Type:".Length..].Trim();
            return type.ToUpperInvariant() switch
            {
                "INVST" => QifSection.Investment,
                "BANK" or "CCARD" or "CASH" or "OTH A" or "OTH L" => QifSection.Bank,
                _ => QifSection.Unknown,
            };
        }
        // "!Account" blocks and "!Option:" directives carry no
        // transactions we ingest; treat as a non-transaction section.
        return QifSection.Unknown;
    }

    private void FlushRecord(
        QifSection section,
        QifRecordBuilder record,
        FileIngestContext context,
        List<IngestedTransaction> transactions,
        List<IngestError> errors,
        ref int supported)
    {
        if (!record.HasAny) return;
        if (record.Date is null) return;   // no date → not a transaction record

        var mapped = section switch
        {
            QifSection.Investment => MapInvestmentRecord(record, context, errors),
            QifSection.Bank => MapBankRecord(record, context),
            _ => null,
        };
        if (mapped is not null)
        {
            transactions.Add(mapped);
            supported++;
        }
    }

    /// <summary>
    /// Map a parsed <c>!Type:Invst</c> record to an
    /// <see cref="IngestedTransaction"/>. Returns null (with a
    /// preview warning) for QIF actions this slice doesn't support
    /// (stock splits route through the security-splits surface, not
    /// the txn editor).
    /// </summary>
    private IngestedTransaction? MapInvestmentRecord(
        QifRecordBuilder r,
        FileIngestContext context,
        List<IngestError> errors)
    {
        var rawAction = r.Action ?? string.Empty;
        var action = ClassifyInvestmentAction(rawAction);
        if (action is null)
        {
            errors.Add(new IngestError(
                Code: "qif_investment_action_unsupported",
                Message: DescribeUnsupportedAction(rawAction, r),
                ConnectionId: null,
                AccountId: null));
            return null;
        }

        var (securityName, tickerHint) = SplitSecurity(r.Security);
        // QIF amounts are unsigned magnitudes; the action sets the
        // sign of the bank-shape cash-flow hint (ADR-0042). The wire
        // amount is `T`, falling back to `U` (a historical duplicate).
        var magnitude = r.Amount ?? r.AltAmount ?? 0m;
        var signedAmount = SignAmountForAction(action, magnitude);

        var postedAt = DateTime.SpecifyKind(r.Date!.Value, DateTimeKind.Utc);
        var externalId = SynthesizeExternalId(
            context.AccountId, r.Date.Value, rawAction,
            tickerHint ?? securityName, r.Quantity, r.Price, magnitude, r.Memo);

        return new IngestedTransaction(
            ExternalId: externalId,
            PostedAt: postedAt,
            TransactedAt: null,
            Amount: signedAmount,
            // Security name is the register's Payee (mirrors the OFX
            // investment path); the QIF memo (M) is the description.
            Payee: securityName,
            Description: NullIfEmpty(r.Memo),
            Pending: false,
            Action: action,
            SecurityTickerHint: tickerHint,
            RawProviderPayload: null,
            ProviderAccountId: SingleAccountKey,
            OnlineMatchFiId: null,           // QIF has no FI id
            Shares: r.Quantity,
            UnitPrice: r.Price,
            // QIF `O` is commission; null when absent or zero.
            Fee: r.Commission is > 0m ? r.Commission : null);
    }

    /// <summary>
    /// Map a parsed <c>!Type:Bank</c> / <c>!Type:CCard</c> record to
    /// a bank-shape <see cref="IngestedTransaction"/>.
    /// </summary>
    private IngestedTransaction? MapBankRecord(
        QifRecordBuilder r,
        FileIngestContext context)
    {
        var magnitude = r.Amount ?? r.AltAmount ?? 0m;
        var postedAt = DateTime.SpecifyKind(r.Date!.Value, DateTimeKind.Utc);
        // Bank QIF `T` is already signed (debit negative). No action
        // classification — bank rows land as cash-flow needs_review.
        var externalId = SynthesizeExternalId(
            context.AccountId, r.Date.Value, r.Action ?? string.Empty,
            r.Payee, null, null, magnitude, r.Memo);

        return new IngestedTransaction(
            ExternalId: externalId,
            PostedAt: postedAt,
            TransactedAt: null,
            Amount: magnitude,
            Payee: NullIfEmpty(r.Payee),
            Description: NullIfEmpty(r.Memo),
            Pending: false,
            ProviderAccountId: SingleAccountKey);
    }

    /// <summary>
    /// Build the preview warning for a record whose <c>N</c> action
    /// this slice doesn't import. Mirrors the OFX provider's
    /// <c>DescribeUnsupported</c>: the user has to find the row on
    /// their own statement to judge whether the skip matters, so the
    /// message carries the row's identity — security, quantity, date —
    /// not just the action token and the date.
    /// </summary>
    private static string DescribeUnsupportedAction(string rawAction, QifRecordBuilder r)
    {
        var token = rawAction.Trim();
        var (securityName, tickerHint) = SplitSecurity(r.Security);

        var identity = new List<string>(3);
        if ((tickerHint ?? NullIfEmpty(securityName)) is { } security)
            identity.Add(security);
        if (r.Quantity is { } quantity)
        {
            // 12dp matches txn_legs.quantity's scale; the format trims
            // trailing zeros so whole-share rows stay readable.
            identity.Add($"{quantity.ToString("0.############", CultureInfo.InvariantCulture)} units");
        }
        // HandleRecord drops dateless records before they reach the
        // mapper, so identity is never empty — the date is always here.
        if (r.Date is { } date)
            identity.Add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var subject = token.Length > 0 ? token : "(no action)";
        return $"QIF {subject} row skipped ({string.Join(", ", identity)}). "
            + UnsupportedActionReason(token);
    }

    /// <summary>
    /// Why this slice skips a given QIF action. The recognised-but-
    /// declined codes get a specific reason; anything else falls back
    /// to the catalog line. Codes here are deliberately NOT routed to
    /// actions — several (<c>RtrnCap</c>, <c>XIn</c> / <c>XOut</c>)
    /// have ADR-0027 equivalents the OFX provider already maps, and
    /// closing that gap is a classification change, not message text.
    /// </summary>
    private static string UnsupportedActionReason(string qifAction) =>
        qifAction.ToUpperInvariant() switch
        {
            "" => "The record carries no action (N) field.",
            "STKSPLIT" =>
                "Stock splits are recorded on the security, not in the register.",
            "REMINDERTXN" =>
                "Reminder placeholders aren't transactions.",
            "SHTSELL" or "CVRSHRT" =>
                "Short sales are outside the ADR-0027 action catalog.",
            "GRANT" or "VEST" or "EXERCISE" or "EXERCISX" or "EXPIRE" =>
                "Equity-compensation actions are outside the ADR-0027 action catalog.",
            "XIN" or "XOUT" or "CONTRIBX" or "WITHDRWX" =>
                "Cash transfers inside an investment section aren't imported in this slice.",
            "RTRNCAP" or "RTRNCAPX" =>
                "Return of capital isn't imported in this slice.",
            _ =>
                "This QIF action is outside the ADR-0027 action catalog.",
        };

    /// <summary>
    /// Map a QIF investment action code (the <c>N</c> field) to an
    /// ADR-0027 action. Returns null for unsupported codes (caller
    /// surfaces a skip warning). Faithful mapping only — no cash-model
    /// interpretation (ADR-0042).
    /// </summary>
    /// <remarks>
    /// Share-movement codes (<c>ShrsIn</c> / <c>ShrsOut</c>) default
    /// to the PLAIN variants (<c>buy</c> / <c>sell</c>), not the
    /// transfer variants. The X-variants (<c>buyx</c> / <c>sellx</c>)
    /// require a transfer counter-account, and that field only
    /// accepts asset accounts (bank / asset / investment) — never an
    /// expense category — so defaulting a share-movement to the
    /// X-variant dead-ends every row whose real counterpart is an
    /// expense (e.g. an administrative fee paid by liquidating
    /// shares: the wire emits <c>ShrsOut</c>, but there is no asset
    /// account to transfer to). The plain variant opens cleanly and
    /// is saveable; the user upgrades to <c>buyx</c> / <c>sellx</c>
    /// only for genuine inter-account transfers. Stock splits
    /// (<c>StkSplit</c>) are skipped — they belong on the
    /// security-splits surface, not the txn editor (parity with the
    /// OFX SPLIT skip).
    /// </remarks>
    internal static string? ClassifyInvestmentAction(string qifAction) =>
        qifAction.Trim().ToUpperInvariant() switch
        {
            "BUY" => "buy",
            "BUYX" => "buyx",
            "SELL" => "sell",
            "SELLX" => "sellx",
            "DIV" => "dividend_cash",
            "DIVX" => "divx",
            "CGLONG" or "CGSHORT" or "CGMID" => "dividend_cash",
            "CGLONGX" or "CGSHORTX" or "CGMIDX" => "divx",
            "INTINC" => "dividend_cash",
            "INTINCX" => "divx",
            "REINVDIV" or "REINVLG" or "REINVSH" or "REINVMD" or "REINVINT"
                => "dividend_reinvest",
            "MISCINC" or "MISCEXP" or "MISCINCX" or "MISCEXPX" or "MARGINT"
                => "misc",
            "SHRSIN" => "buy",
            "SHRSOUT" => "sell",
            // StkSplit / ReminderTxn / unrecognised → unsupported.
            _ => null,
        };

    /// <summary>
    /// Sign the unsigned QIF magnitude to the bank-shape cash-flow
    /// hint per the action's convention. This is the editor's
    /// <c>amountForAction</c> rule applied at ingest. The standard
    /// per-action cash model applies uniformly across every feed;
    /// the importer never overrides it from memo / feed cues
    /// (ADR-0042).
    /// </summary>
    internal static decimal SignAmountForAction(string action, decimal magnitude)
    {
        var m = Math.Abs(magnitude);
        return action switch
        {
            // Reinvested dividends move no cash — the dividend funds
            // the buy. Net-zero at the action level (same contract as
            // the OFX REINVEST handling), not a feed-imposed model.
            "dividend_reinvest" => 0m,
            // Cash leaves to acquire shares.
            "buy" or "buyx" => -m,
            // Cash arrives from disposing shares / distributions.
            "sell" or "sellx" or "dividend_cash" or "divx" => m,
            // misc covers both income (+) and expense (-); without a
            // reliable sign signal from the action alone, carry the
            // positive magnitude and let the user set direction in
            // the editor.
            _ => m,
        };
    }

    /// <summary>
    /// Split a QIF security field into a display name and a ticker
    /// hint. The workplace plan encodes a stable fund code in trailing
    /// parens — "BOND FUND(BFND)" → name "BOND FUND", hint
    /// "BFND". When there's no parenthetical, the hint is null (the
    /// user maps the security by picking it in the editor) and the
    /// whole value is the name.
    /// </summary>
    internal static (string? Name, string? TickerHint) SplitSecurity(string? security)
    {
        if (string.IsNullOrWhiteSpace(security)) return (null, null);
        var trimmed = security.Trim();
        var match = TrailingParenCode.Match(trimmed);
        if (match.Success)
        {
            var hint = match.Groups[1].Value.Trim();
            var name = trimmed[..match.Index].Trim();
            return (
                Name: name.Length > 0 ? name : trimmed,
                TickerHint: hint.Length > 0 ? hint : null);
        }
        return (trimmed, null);
    }

    /// <summary>
    /// Deterministic synthetic external id for a QIF row. QIF carries
    /// no transaction id; the SHA-1 over (target account, stable
    /// fields) makes re-import idempotent and prevents cross-account
    /// collisions in the per-provider dedup scope (ADR-0042).
    /// </summary>
    internal static string SynthesizeExternalId(
        Guid accountId,
        DateTime date,
        string action,
        string? security,
        decimal? quantity,
        decimal? price,
        decimal amount,
        string? memo)
    {
        var canonical = string.Join(
            '|',
            accountId.ToString("N"),
            date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            action.Trim().ToUpperInvariant(),
            (security ?? string.Empty).Trim().ToUpperInvariant(),
            (quantity ?? 0m).ToString(CultureInfo.InvariantCulture),
            (price ?? 0m).ToString(CultureInfo.InvariantCulture),
            amount.ToString(CultureInfo.InvariantCulture),
            (memo ?? string.Empty).Trim());
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(canonical));
        return "qif-" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private enum QifSection { Unknown, Investment, Bank }

    /// <summary>
    /// Accumulates the single-letter QIF fields of one record until
    /// the <c>^</c> terminator. QIF field codes:
    /// <c>D</c> date, <c>N</c> action/number, <c>Y</c> security,
    /// <c>I</c> price, <c>Q</c> quantity, <c>T</c> amount,
    /// <c>U</c> amount (duplicate of T), <c>O</c> commission,
    /// <c>M</c> memo, <c>P</c> payee, <c>L</c> category, <c>C</c>
    /// cleared status. Unrecognised codes are ignored.
    /// </summary>
    private sealed class QifRecordBuilder
    {
        public DateTime? Date { get; private set; }
        public string? Action { get; private set; }
        public string? Security { get; private set; }
        public decimal? Price { get; private set; }
        public decimal? Quantity { get; private set; }
        public decimal? Amount { get; private set; }
        public decimal? AltAmount { get; private set; }
        public decimal? Commission { get; private set; }
        public string? Memo { get; private set; }
        public string? Payee { get; private set; }
        public bool HasAny { get; private set; }

        public void Apply(char code, string value)
        {
            HasAny = true;
            switch (code)
            {
                case 'D': Date = ParseQifDate(value); break;
                case 'N': Action = value; break;
                case 'Y': Security = value; break;
                case 'I': Price = ParseDecimal(value); break;
                case 'Q': Quantity = ParseDecimal(value); break;
                case 'T': Amount = ParseDecimal(value); break;
                case 'U': AltAmount = ParseDecimal(value); break;
                case 'O': Commission = ParseDecimal(value); break;
                case 'M': Memo = value; break;
                case 'P': Payee = value; break;
                // L (category), C (cleared) intentionally ignored —
                // all imported rows land needs_review for the editor.
                default: break;
            }
        }

        private static decimal? ParseDecimal(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            // QIF amounts may carry thousands separators.
            var cleaned = value.Replace(",", string.Empty, StringComparison.Ordinal);
            return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var d)
                ? d
                : null;
        }

        private static DateTime? ParseQifDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            // The 401(k) provider emits MM/DD/YYYY. QIF historically also used
            // M/D'YY (apostrophe for 20xx) and M/D/YY; accept the
            // common shapes. Quicken pads with spaces ("Q 5/ 9/26").
            var v = value.Replace(" ", string.Empty, StringComparison.Ordinal)
                         .Replace("'", "/", StringComparison.Ordinal);
            string[] formats =
            {
                "MM/dd/yyyy", "M/d/yyyy",
                "MM/dd/yy", "M/d/yy",
                "yyyy-MM-dd",
            };
            return DateTime.TryParseExact(v, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d)
                ? d.Date
                : null;
        }
    }
}
