namespace Coffer.Api.Sync.SimpleFin;

/// <summary>
/// Top-level shape of the SimpleFIN v2.0.0 <c>/accounts</c>
/// response. Three sibling arrays at the root (no nesting):
/// <c>errlist</c> · <c>connections</c> · <c>accounts</c>. Each
/// <see cref="SimpleFinAccount.ConnId"/> joins to a
/// <see cref="SimpleFinConnection.ConnId"/>. The
/// <see cref="SimpleFinSyncResponse"/> below is what the client
/// surfaces to callers — accounts already enriched with their
/// connection's display data, plus the raw error list for
/// partial-failure surfacing.
/// </summary>
public sealed record SimpleFinSyncResponse(
    IReadOnlyList<SimpleFinAccount> Accounts,
    IReadOnlyList<SimpleFinError> Errors,
    /// <summary>True when the SimpleFIN endpoint returned 403,
    /// indicating the access URL is no longer valid (revoked /
    /// expired token). The caller flips the connection's status
    /// to <c>needs_reauth</c> and surfaces this to the SPA so the
    /// user can re-generate a setup token. Distinct from a
    /// transient HTTP error (which throws SimpleFinException).</summary>
    bool RequiresReauth);

/// <summary>
/// One connection in <c>connections[]</c> — institution metadata,
/// matched to accounts via <see cref="ConnId"/>. Replaces the
/// pre-v2 <c>account.org</c> nested object.
/// </summary>
public sealed record SimpleFinConnection(
    string ConnId,
    string Name,
    /// <summary>Stable identifier for the institution. Persisted
    /// on accepted txn_headers as <c>online_match_fi_id</c> so
    /// future syncs match across (fi_id, fitid).</summary>
    string OrgId,
    /// <summary>Optional institution URL (e.g. the bank's website).</summary>
    string? OrgUrl,
    string SfinUrl);

/// <summary>
/// Typed projection of one SimpleFIN v2.0.0 account, enriched
/// post-parse with the matched <see cref="SimpleFinConnection"/>'s
/// display fields (<see cref="OrgName"/>, <see cref="OrgKey"/>)
/// so downstream code stays oblivious to the connection table.
/// </summary>
public sealed record SimpleFinAccount(
    string Id,
    string ConnId,
    /// <summary>Display name from the matched connection
    /// (institution name). NULL when the connection couldn't be
    /// resolved — degraded mode.</summary>
    string? OrgName,
    /// <summary>Stable institution key (matched connection's
    /// <c>org_id</c>). Persisted as the synthetic
    /// <c>online_match_fi_id</c> when transactions are accepted
    /// so future-FITID dedup pairs (fi_id, fitid).</summary>
    string? OrgKey,
    string Name,
    string? Currency,
    decimal? Balance,
    /// <summary>UTC seconds-since-epoch — when the balance was
    /// as-of, per v2.0.0. NULL on older feeds.</summary>
    long? BalanceDateUnix,
    /// <summary>Optional intra-day balance, distinct from the
    /// posted balance (v2.0.0). NULL when not supplied.</summary>
    decimal? AvailableBalance,
    IReadOnlyList<SimpleFinTransaction> Transactions,
    /// <summary>ADR-0031 follow-up: verbatim JSON for this account
    /// object as SimpleFIN sent it (JsonElement.GetRawText at parse
    /// time). Preserves <c>holdings[]</c> + any other institution-
    /// specific fields we don't model. The orchestrator stores this
    /// on the corresponding feed_connection_accounts row for
    /// debugging / future iteration.</summary>
    string RawJson);

/// <summary>
/// One transaction from SimpleFIN v2.0.0. <see cref="Id"/> is the
/// FITID-equivalent — globally-stable per institution.
/// </summary>
public sealed record SimpleFinTransaction(
    string Id,
    /// <summary>UTC seconds-since-epoch — when the bank posted
    /// the transaction (cleared).</summary>
    long PostedUnix,
    /// <summary>Optional UTC seconds-since-epoch — when the user
    /// transacted (v2.0.0). Prefer this for the user-visible
    /// date when present; fall back to <see cref="PostedUnix"/>.</summary>
    long? TransactedAtUnix,
    decimal Amount,
    /// <summary>SimpleFIN v2.0.0 <c>payee</c>: the cleaned merchant /
    /// counterparty name (e.g. a tidied "Acme Corp" or a person's
    /// name on a P2P transfer). Separate from <see cref="Description"/>
    /// which carries the raw bank-format text (e.g. the all-caps
    /// merchant string the bank sent). NULL when the provider
    /// didn't send the field.</summary>
    string? Payee,
    /// <summary>SimpleFIN <c>description</c>: the raw bank-format text
    /// for this transaction. Inputs the description classifier +
    /// holdings matcher. NULL when missing.</summary>
    string? Description,
    /// <summary>Bank-side pending flag — pre-cleared transactions
    /// surface here while still pending. Defaults to false per
    /// v2.0.0.</summary>
    bool Pending,
    /// <summary>ADR-0031 follow-up: verbatim JSON for this
    /// transaction as SimpleFIN sent it (the JsonElement's GetRawText
    /// at parse time). Preserves any fields we don't currently
    /// model in this record — used by the orchestrator to populate
    /// <c>txn_headers.provider_raw_payload</c> for classifier
    /// iteration / debugging. Not used for normalization or dedup.</summary>
    string RawJson);

/// <summary>
/// One entry from <c>errlist[]</c> (v2.0.0). Structured —
/// <see cref="Code"/> is a <c>prefix.subcode</c> string the SPA
/// can switch on; <see cref="Msg"/> is the human message.
/// Connection-scoped or account-scoped via the optional ids.
/// </summary>
public sealed record SimpleFinError(
    string Code,
    string Msg,
    string? ConnId,
    string? AccountId);
