namespace Coffer.Api.Ingest;

/// <summary>
/// Provider-produced transaction record (ADR-0031 §D1 — single
/// record with optional investment fields, resolved Phase 3).
/// The bank-shape fields (<see cref="ExternalId"/>,
/// <see cref="PostedAt"/>, <see cref="Amount"/>, etc.) are filled
/// for every ingested row regardless of source; the investment-
/// shape fields (<see cref="Action"/>, <see cref="SecurityTickerHint"/>)
/// are populated by the provider's classifier when the row's
/// description matched a known pattern, null otherwise.
/// </summary>
/// <remarks>
/// <para>Identity: <see cref="ExternalId"/> is the provider-stable
/// ID (SimpleFIN <c>id</c>, OFX <c>FITID</c>, CSV hash). The
/// orchestrator pairs it with <c>online_match_fi_id</c> from the
/// connection metadata for cross-source dedup per slice 2c.</para>
///
/// <para>SimpleFIN's wire format doesn't discriminate bank vs
/// investment per row — the same shape carries both — so the
/// investment fields below are <em>classifier outputs</em>, not
/// wire-provided values. A bank-shape provider (or an unmatched
/// brokerage description) leaves them null.</para>
/// </remarks>
public sealed record IngestedTransaction(
    /// <summary>Provider-stable identifier for this transaction.
    /// SimpleFIN sends it as <c>id</c>; OFX as <c>FITID</c>; CSV
    /// providers derive it from a row hash. Persisted as
    /// <c>txn_headers.external_id</c>.</summary>
    string ExternalId,
    /// <summary>UTC instant when the institution posted the
    /// transaction (cleared / settled).</summary>
    DateTime PostedAt,
    /// <summary>Optional UTC instant when the user transacted —
    /// SimpleFIN v2 <c>transacted_at</c> / OFX <c>DTUSER</c>.
    /// Falls back to <see cref="PostedAt"/> when null.</summary>
    DateTime? TransactedAt,
    /// <summary>Signed amount in the account's currency. Sign
    /// follows the user's perspective on the target account
    /// (debit-out is negative).</summary>
    decimal Amount,
    /// <summary>Cleaned counterparty / merchant name as supplied by
    /// the provider (SimpleFIN v2 <c>payee</c> field). NULL when
    /// the provider didn't carry one. Persisted to
    /// <c>txn_headers.payee</c>; user sees this in the register's
    /// Payee column.</summary>
    string? Payee,
    /// <summary>Free-text description / raw bank-format text as
    /// provided by the source (SimpleFIN <c>description</c>; OFX
    /// <c>NAME</c>/<c>MEMO</c>). Inputs the description classifier +
    /// holdings matcher. Persisted to <c>txn_headers.memo</c> —
    /// the cleaner <see cref="Payee"/> lands in payee instead.</summary>
    string? Description,
    /// <summary>Bank-side pending flag — true if the source marks
    /// this row not-yet-cleared. Defaults to false.</summary>
    bool Pending,
    /// <summary>Provider-classified action per ADR-0027 catalog
    /// (<c>buy</c> / <c>sell</c> / <c>dividend_cash</c> /
    /// <c>dividend_reinvest</c> / <c>transfer</c>). NULL when the
    /// classifier abstained — the orchestrator's brokerage branch
    /// falls back to a cash-flow insert with <c>needs_review=true</c>.</summary>
    string? Action = null,
    /// <summary>Provider-extracted security ticker hint
    /// (SimpleFIN: parenthesized symbol from the description, OR
    /// the holding's <c>symbol</c> recovered via
    /// <c>SimpleFinHoldingsMatcher</c>). NULL when neither
    /// extractor produced one. The orchestrator persists this on
    /// <c>txn_headers.ingest_security_ticker_hint</c> AND uses it
    /// (with the provider key) to look up
    /// <c>provider_security_mappings</c>; on hit, the resolved
    /// <c>security_id</c> lands on <c>ingest_security_id</c>.</summary>
    string? SecurityTickerHint = null,
    /// <summary>ADR-0031 follow-up: verbatim provider payload for
    /// this transaction (SimpleFIN sends the JsonElement.GetRawText
    /// at parse time; future OFX / CSV providers carry their
    /// respective wire shapes). The orchestrator stores this on
    /// the inserted txn_headers row so classifier-iteration +
    /// per-row debugging can read the exact provider data after
    /// the fact. NULL when the provider doesn't carry a raw
    /// payload (e.g. file-based imports that pre-process).</summary>
    string? RawProviderPayload = null,
    /// <summary>OFX / QFX file providers tag every row with the
    /// statement-level (BANKID, ACCTID) pair it came from so the
    /// orchestrator can dispatch transactions from a multi-account
    /// file to the right Coffer account. NULL for pull providers
    /// (SimpleFIN scopes per-account at the wire level) and for
    /// single-account file providers.</summary>
    string? ProviderAccountId = null,
    /// <summary>OFX-protocol financial-institution id (the OFX
    /// <c>BANKID</c> or investment <c>BROKERID</c>). Populated by
    /// OFX/QFX providers; persisted to
    /// <c>txn_headers.online_match_fi_id</c> for cross-source dedup
    /// against MD-imported rows whose preserved OFX state carries the
    /// same FI id. NULL for non-OFX providers (mig 105 reverted
    /// <c>online_match_*</c> to OFX-protocol-only).</summary>
    string? OnlineMatchFiId = null,
    /// <summary>OFX-protocol per-transaction unique id (the OFX
    /// <c>FITID</c>) — populated ONLY by the OFX/QFX provider, where
    /// it equals <see cref="ExternalId"/>. Persisted to
    /// <c>txn_headers.online_match_fitid</c>, the OFX-protocol-only
    /// cross-source-dedup substrate (mig 105 reverted this column to
    /// OFX-only). QIF's <see cref="ExternalId"/> is a synthetic
    /// <c>qif-&lt;hash&gt;</c> — NOT an OFX FITID — so QIF and
    /// SimpleFIN leave this null and never pollute the column.</summary>
    string? OnlineMatchFitid = null,
    /// <summary>Provider-extracted share count for investment-shape
    /// rows (OFX <c>UNITS</c>). Persisted to
    /// <c>txn_headers.ingest_shares</c> for the editor's
    /// bank→investment upgrade flow (hintToDraft pre-fills the
    /// shares input). NULL on bank/credit rows and on providers
    /// that don't carry shares natively (SimpleFIN). Sign follows
    /// the OFX wire (positive for buy/in, negative for sell/out).</summary>
    decimal? Shares = null,
    /// <summary>Provider-extracted per-share price (OFX
    /// <c>UNITPRICE</c>). Persisted to
    /// <c>txn_headers.ingest_unit_price</c>; same population rules
    /// as <see cref="Shares"/>.</summary>
    decimal? UnitPrice = null,
    /// <summary>Provider-extracted aggregated fee — sum of OFX
    /// Commission + Fees + Load + Markup + Markdown for the
    /// transaction's subtype. Persisted to
    /// <c>txn_headers.ingest_fee</c>. NULL when the wire carried
    /// no fee-shaped fields OR they summed to zero.</summary>
    decimal? Fee = null);
