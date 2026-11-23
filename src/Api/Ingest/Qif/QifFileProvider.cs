using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Coffer.Domain.Money;

namespace Coffer.Api.Ingest.Qif;

/// <summary>
/// QIF (Quicken Interchange Format) file provider — ADR-0042.
/// Handles investment (<c>!Type:Invst</c>) and bank
/// (<c>!Type:Bank</c> / <c>!Type:CCard</c>) sections. Built for
/// workplace-plan exports (a plan recordkeeper may offer only QIF or
/// CSV downloads, no OFX/QFX), but the parser is the generic QIF
/// line grammar, not specific to any one recordkeeper.
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
/// carries zero because that is the action's net-zero contract on
/// every feed, not because QIF said so.</para>
///
/// <para><b>Single-account-implicit.</b> A single-account QIF export
/// need carry no account header, and any <c>!Account</c> block is
/// ignored, so a file is read as one
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
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[index].TrimEnd();
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
                    // The file line, one-based, so a number that does not read can be
                    // named where the user will find it (ADR-0114 D15).
                    record.Apply(code, value, line: index + 1);
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
            QifSection.Bank => MapBankRecord(record, context, errors),
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

        // Cash moves inside the investment section are the investment
        // register's BANK rows, not security actions, so they leave here
        // before classification rather than failing it.
        var cashSign = CashTransferSign(rawAction);
        if (cashSign != 0m)
            return MapInvestmentCashTransfer(r, rawAction, cashSign, context, errors);

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

        // QIF amounts are unsigned magnitudes; the action sets the
        // sign of the bank-shape cash-flow hint (ADR-0042). The wire
        // amount is `T`, falling back to `U` (a historical duplicate).
        // A number that is written but does not read is an error on its
        // row, never a silent zero (ADR-0114 D15).
        if (!TryReadMagnitude(r, context, errors, out var magnitude)
            || !TryRead(r.Quantity, NumberField.Quantity, context, errors, out var quantity)
            || !TryRead(r.Price, NumberField.Price, context, errors, out var price)
            || !TryRead(r.Commission, NumberField.Commission, context, errors, out var commission))
        {
            return null;
        }

        var (securityName, tickerHint) = SplitSecurity(r.Security);
        var signedAmount = SignAmountForAction(action, magnitude);

        var postedAt = DateTime.SpecifyKind(r.Date!.Value, DateTimeKind.Utc);
        var externalId = SynthesizeExternalId(
            context.AccountId, r.Date.Value, rawAction,
            tickerHint ?? securityName, quantity, price, magnitude, r.Memo);

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
            Shares: quantity,
            UnitPrice: price,
            // QIF `O` is commission; null when absent or zero.
            Fee: commission is > 0m ? commission : null);
    }

    /// <summary>
    /// The cash direction of an investment-section cash move, or zero for
    /// every other action, which the caller then classifies as a security
    /// action.
    /// </summary>
    /// <remarks>
    /// Unlike the security actions, the direction IS knowable from the
    /// action alone, so these do not land on the positive magnitude the
    /// <c>misc</c> fallback uses.
    /// </remarks>
    private static decimal CashTransferSign(string rawAction) =>
        rawAction.Trim().ToUpperInvariant() switch
        {
            "XIN" or "CONTRIBX"   => 1m,
            "XOUT" or "WITHDRWX"  => -1m,
            _                     => 0m,
        };

    /// <summary>
    /// Map an investment-section cash move — <c>XIn</c> / <c>XOut</c> /
    /// <c>ContribX</c> / <c>WithdrwX</c> — to a bank-shape
    /// <see cref="IngestedTransaction"/>, or null with a row error when its
    /// amount is written but does not read.
    /// </summary>
    /// <remarks>
    /// These rows carry no security and no share count: they are cash
    /// arriving in or leaving the account. The OFX provider already
    /// treats its equivalent that way — <c>INVBANKTRAN</c> maps to the
    /// bank shape with a null action — and this is the same fact in a
    /// different dialect.
    ///
    /// <para>Skipping them is not an option. Contributions to a
    /// workplace plan arrive as <c>ContribX</c>, so skipping the action
    /// would import such a QIF with its contributions dropped: the cash
    /// balance would be wrong, and the missing rows would look like the
    /// user's own oversight rather than an importer decision.</para>
    ///
    /// <para>QIF <c>L</c> (the transfer counterparty) is deliberately not
    /// read here, consistent with the rest of this provider — the row
    /// lands needs_review and the user names the other side in the
    /// editor. So a cash row usually arrives with no payee, which is
    /// honest: the file did not say.</para>
    /// </remarks>
    private IngestedTransaction? MapInvestmentCashTransfer(
        QifRecordBuilder r,
        string rawAction,
        decimal sign,
        FileIngestContext context,
        List<IngestError> errors)
    {
        if (!TryReadMagnitude(r, context, errors, out var read)) return null;
        var magnitude = Math.Abs(read);
        var postedAt = DateTime.SpecifyKind(r.Date!.Value, DateTimeKind.Utc);

        // rawAction stays in the key so a cash row cannot collide with a
        // bank-section row of the same date and amount.
        var externalId = SynthesizeExternalId(
            context.AccountId, r.Date.Value, rawAction,
            null, null, null, magnitude, r.Memo);

        return new IngestedTransaction(
            ExternalId: externalId,
            PostedAt: postedAt,
            TransactedAt: null,
            Amount: sign * magnitude,
            Payee: NullIfEmpty(r.Payee),
            Description: NullIfEmpty(r.Memo),
            Pending: false,
            ProviderAccountId: SingleAccountKey);
    }

    /// <summary>
    /// Map a parsed <c>!Type:Bank</c> / <c>!Type:CCard</c> record to
    /// a bank-shape <see cref="IngestedTransaction"/>.
    /// </summary>
    private IngestedTransaction? MapBankRecord(
        QifRecordBuilder r,
        FileIngestContext context,
        List<IngestError> errors)
    {
        if (!TryReadMagnitude(r, context, errors, out var magnitude)) return null;
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
        // A quantity that does not read is left out: the row is skipped for
        // its action either way, and the date still identifies it.
        if (r.Quantity is { } field
            && ReadNumber(field.Text, threePlaceMoney: false, out var quantity) == NumberReading.Read)
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
    /// to the catalog line.
    ///
    /// <para><c>RtrnCap</c>, <c>XIn</c> / <c>XOut</c> and
    /// <c>ContribX</c> / <c>WithdrwX</c> are not listed here: return
    /// of capital classifies as a cash distribution like the OFX
    /// provider's RETOFCAP, and the four cash moves take the bank shape
    /// before classification runs.</para>
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
            // Return of capital is a cash distribution, which is exactly
            // how the OFX provider maps its RETOFCAP. The X variant
            // follows the DIV/DIVX pair already above it.
            "RTRNCAP" => "dividend_cash",
            "RTRNCAPX" => "divx",
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
    /// hint. A recordkeeper may encode a stable fund code in trailing
    /// parens — "BOND FUND(BFND)" → name "BOND FUND", hint
    /// "BFND". When there's no parenthetical, the hint is null (the
    /// user maps the security by picking it in the editor) and the
    /// whole value is the name.
    /// </summary>
    /// <remarks>
    /// A scan, not the regex it replaces (FU-092). <c>\(([^)]+)\)\s*$</c> was tried from every
    /// <c>(</c> and ran to the end of the string from each, so a security name of many
    /// parentheses with no closing one at the end cost quadratic time, from an uploaded file.
    /// This finds the same span in one pass: the leftmost <c>(</c> after the last <c>)</c>
    /// that is not the final character, with at least one character before that final
    /// <c>)</c>. <c>QifSecurityNameTests</c> holds it to the regex's answers.
    /// </remarks>
    internal static (string? Name, string? TickerHint) SplitSecurity(string? security)
    {
        if (string.IsNullOrWhiteSpace(security)) return (null, null);
        var trimmed = security.Trim();
        if (TrailingParenCode(trimmed) is var (open, close) && open >= 0)
        {
            var hint = trimmed[(open + 1)..close].Trim();
            var name = trimmed[..open].Trim();
            return (
                Name: name.Length > 0 ? name : trimmed,
                TickerHint: hint.Length > 0 ? hint : null);
        }
        return (trimmed, null);
    }

    /// <summary>Where the trailing <c>(CODE)</c> of <paramref name="s"/> opens and closes,
    /// or (-1, -1). <paramref name="s"/> is already trimmed. Where a recordkeeper puts a
    /// stable per-fund code there, it is lifted as the ticker hint for the
    /// provider_security_mappings rail (ADR-0038): mapped once, every row with the same
    /// code resolves.</summary>
    internal static (int Open, int Close) TrailingParenCode(string s)
    {
        var close = s.Length - 1;
        if (close < 2 || s[close] != ')') return (-1, -1);
        var previousClose = s.LastIndexOf(')', close - 1);
        var open = s.IndexOf('(', previousClose + 1, close - previousClose - 1);
        return open >= 0 && open < close - 1 ? (open, close) : (-1, -1);
    }

    /// <summary>
    /// Deterministic synthetic external id for a QIF row. QIF carries
    /// no transaction id; the SHA-1 over (target account, stable
    /// fields) makes re-import idempotent and prevents cross-account
    /// collisions in the per-provider dedup scope (ADR-0042).
    /// </summary>
    /// <remarks>
    /// <para><b><paramref name="amount"/> is the figure as parsed, before any
    /// rounding</b> (ADR-0114 D15), with the scale it was written at. The
    /// orchestrator alone fits an amount to its account currency's places, at
    /// the write; this provider never rounds. Hashing a rounded figure, or
    /// normalising the amount in the parser, would give two rows the file
    /// tells apart one id, so the second is dropped as already known, and
    /// would change the id of every row already imported, so a re-import
    /// duplicates them.</para>
    /// <para>The same holds for <paramref name="quantity"/> and
    /// <paramref name="price"/>: a different reading of the same text is a
    /// different id.</para>
    /// </remarks>
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

    /// <summary>A numeric field of a record as the file wrote it: its text and the
    /// one-based file line it is on.</summary>
    internal readonly record struct QifNumber(string Text, int Line);

    /// <summary>The numeric fields a mapper reads. <see cref="Amount"/> and
    /// <see cref="Commission"/> are money; quantity and price are not.</summary>
    internal enum NumberField { Amount, Commission, Quantity, Price }

    /// <summary>How the text of a numeric field reads (ADR-0114 D15).</summary>
    internal enum NumberReading
    {
        /// <summary>A number, read one way only.</summary>
        Read,

        /// <summary>Not a number at all.</summary>
        Unparseable,

        /// <summary>A comma that is not followed by exactly three digits of the whole
        /// part, in a figure that is not grouped in thousands or lakh-style, so it could
        /// be a decimal comma.</summary>
        AmbiguousComma,

        /// <summary>Money in an account whose currency has three places, with every comma
        /// followed by exactly three digits of the whole part, grouped by commas in
        /// thousands or lakh-style or not; a comma there reads as a thousands separator or
        /// as a decimal comma alike.</summary>
        CommaInThreePlaceMoney,

        /// <summary>In an account of fewer than three places, or in a figure that is not
        /// money, commas each followed by three digits of the whole part, but in groups
        /// <see cref="AmountGrouping"/> does not accept: a first group of more than three
        /// digits, or one that starts with a zero.</summary>
        UngroupedComma,
    }

    /// <summary>
    /// The magnitude of a record's amount: <c>T</c>, or <c>U</c> when there is no
    /// <c>T</c>, or zero when the record carries neither. A <c>T</c> that is written
    /// decides on its own, so a <c>U</c> behind it is never read. False, with a row
    /// error, when the field that decides does not read.
    /// </summary>
    private static bool TryReadMagnitude(
        QifRecordBuilder r, FileIngestContext context, List<IngestError> errors, out decimal magnitude)
    {
        var ok = TryRead(r.Amount ?? r.AltAmount, NumberField.Amount, context, errors, out var read);
        magnitude = read ?? 0m;
        return ok;
    }

    /// <summary>
    /// Read one numeric field, null when the record does not carry it. False, with a
    /// row error naming the field's line, when it is written but does not read one way
    /// only (ADR-0114 D15). An absent field is not an unreadable one.
    /// </summary>
    private static bool TryRead(
        QifNumber? field,
        NumberField kind,
        FileIngestContext context,
        List<IngestError> errors,
        out decimal? value)
    {
        value = null;
        if (field is not { } f) return true;

        var money = kind is NumberField.Amount or NumberField.Commission;
        var threePlaceMoney = money
            && context.CurrencyCode is { } code
            && CurrencyMinorUnits.PlacesOf(code) >= 3;
        var reading = ReadNumber(f.Text, threePlaceMoney, out var read);
        if (reading == NumberReading.Read)
        {
            value = read;
            return true;
        }

        errors.Add(NotANumber(f, kind, reading, context.CurrencyCode));
        return false;
    }

    /// <summary>
    /// Read the text of a QIF numeric field the one way it can be read, or say why it
    /// cannot (ADR-0114 D15, "QIF has no layout, so it refuses an amount it cannot read
    /// one way only"; ADR-0042).
    /// </summary>
    /// <remarks>
    /// <para>QIF names no decimal separator, so commas are read only where they can be
    /// nothing but grouping: the whole part grouped in thousands or lakh-style, by the
    /// rule a CSV layout reads by (<see cref="AmountGrouping"/>), and no comma in the
    /// fraction. Any other comma could be a decimal comma, and the field is refused
    /// rather than read a hundred or a thousand times too large, which is what removing
    /// every comma did. In money whose currency has three places
    /// (<paramref name="threePlaceMoney"/>), a comma followed by three digits is a decimal
    /// comma as readily as a separator, so every comma is refused there. A quantity or a
    /// price is never money, so there grouping commas read as grouping.</para>
    /// <para><b>An accepted text reads exactly as before.</b> The figure is parsed by the
    /// same call, with the same styles, as the parser this replaces, so its value, its
    /// sign at either end and its scale are unchanged, and so is the external id hashed
    /// from it (<see cref="SynthesizeExternalId"/>). A different reading of a text a
    /// re-imported file has always carried would give its row a new id and duplicate
    /// it.</para>
    /// </remarks>
    internal static NumberReading ReadNumber(string text, bool threePlaceMoney, out decimal value)
    {
        if (!decimal.TryParse(
                text.Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out value))
        {
            value = 0m;
            return NumberReading.Unparseable;
        }

        if (!text.Contains(',', StringComparison.Ordinal)) return NumberReading.Read;

        // The text parsed with its commas removed, so around its digits, commas and one
        // point there is only white space and a sign at either end.
        var body = text.Trim().Trim('-', '+').Trim();
        var point = body.IndexOf('.', StringComparison.Ordinal);
        var whole = point < 0 ? body : body[..point];
        var grouped = (point < 0 || !body[(point + 1)..].Contains(',', StringComparison.Ordinal))
            && whole.Contains(',', StringComparison.Ordinal)
            && AmountGrouping.IsGrouped(whole, ',');

        if (grouped && !threePlaceMoney) return NumberReading.Read;

        value = 0m;
        if (grouped) return NumberReading.CommaInThreePlaceMoney;
        if (!EveryCommaPrecedesThreeWholeDigits(body, point)) return NumberReading.AmbiguousComma;

        // Every comma is followed by exactly three digits, in groups no export writes. In
        // three-place money that is the three-place case still: a zero before a comma and
        // three digits there reads most readily as a decimal comma.
        return threePlaceMoney ? NumberReading.CommaInThreePlaceMoney : NumberReading.UngroupedComma;
    }

    /// <summary>True when every comma of <paramref name="body"/> follows a digit of the
    /// whole part and is followed by exactly three digits.</summary>
    private static bool EveryCommaPrecedesThreeWholeDigits(string body, int point)
    {
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != ',') continue;
            var digits = 0;
            while (i + 1 + digits < body.Length && char.IsAsciiDigit(body[i + 1 + digits])) digits++;
            var three = i > 0
                && char.IsAsciiDigit(body[i - 1])
                && (point < 0 || i < point)
                && digits == 3;
            if (!three) return false;
        }
        return true;
    }

    /// <summary>The row error for a numeric field that does not read, naming its file
    /// line and its text.</summary>
    private static IngestError NotANumber(
        QifNumber field, NumberField kind, NumberReading reading, string? currencyCode)
    {
        var noun = kind switch
        {
            NumberField.Amount => "an amount",
            NumberField.Commission => "a commission",
            NumberField.Quantity => "a quantity",
            _ => "a price",
        };
        var why = reading switch
        {
            NumberReading.AmbiguousComma =>
                " one way only: a comma not followed by exactly three digits could be a decimal comma.",
            NumberReading.CommaInThreePlaceMoney =>
                $" one way only: in a {currencyCode} account, whose currency has three decimal places, "
                + "a comma followed by three digits could be a decimal comma or a thousands separator.",
            // UngroupedComma takes the plain sentence: it never arises in three-place money,
            // and its every comma is followed by exactly three digits.
            _ => ".",
        };
        return new IngestError(
            Code: "qif_row_unreadable",
            Message: $"Line {field.Line}: '{field.Text}' does not read as {noun}{why} The row was skipped.",
            ConnectionId: null,
            AccountId: null);
    }

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
    /// <remarks>
    /// The numeric fields are kept as written, with their line, and read by the
    /// mapper, which knows whether a field is money and the account's currency: a field
    /// that does not read is an error on its row, never a null that a mapper would take
    /// as zero (ADR-0114 D15). A field written blank is absent, as it always was.
    /// </remarks>
    private sealed class QifRecordBuilder
    {
        public DateTime? Date { get; private set; }
        public string? Action { get; private set; }
        public string? Security { get; private set; }
        public QifNumber? Price { get; private set; }
        public QifNumber? Quantity { get; private set; }
        public QifNumber? Amount { get; private set; }
        public QifNumber? AltAmount { get; private set; }
        public QifNumber? Commission { get; private set; }
        public string? Memo { get; private set; }
        public string? Payee { get; private set; }
        public bool HasAny { get; private set; }

        public void Apply(char code, string value, int line)
        {
            HasAny = true;
            switch (code)
            {
                case 'D': Date = ParseQifDate(value); break;
                case 'N': Action = value; break;
                case 'Y': Security = value; break;
                case 'I': Price = Number(value, line); break;
                case 'Q': Quantity = Number(value, line); break;
                case 'T': Amount = Number(value, line); break;
                case 'U': AltAmount = Number(value, line); break;
                case 'O': Commission = Number(value, line); break;
                case 'M': Memo = value; break;
                case 'P': Payee = value; break;
                // L (category), C (cleared) intentionally ignored —
                // all imported rows land needs_review for the editor.
                default: break;
            }
        }

        private static QifNumber? Number(string value, int line) =>
            string.IsNullOrWhiteSpace(value) ? null : new QifNumber(value, line);

        private static DateTime? ParseQifDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            // Modern exports use MM/DD/YYYY. QIF historically also used
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
