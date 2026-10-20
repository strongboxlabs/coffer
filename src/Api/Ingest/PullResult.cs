namespace Coffer.Api.Ingest;

/// <summary>
/// Output of <see cref="IPullProvider.PullAsync"/> — a fully-
/// translated view of what the provider fetched. Accounts (mapped
/// or not) appear here so the orchestrator can update the
/// bank-side directory + dispatch transactions to mapped Ledger
/// accounts. <see cref="Errors"/> carries provider-side partial
/// failures; <see cref="RequiresReauth"/> is the auth-revoked
/// signal that flips the connection's status to
/// <c>needs_reauth</c>.
/// </summary>
public sealed record PullResult(
    IReadOnlyList<PullAccount> Accounts,
    IReadOnlyList<IngestError> Errors,
    bool RequiresReauth);

/// <summary>
/// One account on a pull connection. The provider returns these
/// for ALL accounts the connection sees (mapped or not); the
/// orchestrator decides what to do with each (insert directory
/// row, dispatch transactions to mapped account, ignore unmapped).
/// </summary>
public sealed record PullAccount(
    /// <summary>Provider-stable identifier (SimpleFIN account id,
    /// future OFX <c>ACCTID</c>). Joins to
    /// <c>accounts.external_id</c> + <c>feed_connection_accounts.external_id</c>.</summary>
    string ExternalId,
    string Name,
    /// <summary>Institution display name (SimpleFIN connection
    /// <c>name</c>). NULL when the provider couldn't resolve it.</summary>
    string? OrgName,
    /// <summary>Stable institution key (SimpleFIN connection
    /// <c>org_id</c>). Used by the directory upsert path to keep
    /// the bank-side display name in sync; NOT persisted on
    /// txn_headers (a SimpleFIN org_id is not an OFX FI_ID — see
    /// mig 105 for the column reclassification).</summary>
    string? OrgKey,
    string? Currency,
    decimal? Balance,
    DateTime? BalanceAt,
    decimal? AvailableBalance,
    IReadOnlyList<IngestedTransaction> Transactions,
    /// <summary>ADR-0031 follow-up: verbatim provider payload for
    /// THIS account object (SimpleFinAccount.GetRawText today),
    /// including any wire-shape fields we don't model — most
    /// notably the <c>holdings[]</c> block brokerages
    /// send. The orchestrator stores it on the matching
    /// feed_connection_accounts row for downstream classifier
    /// iteration. NULL when the provider doesn't carry an
    /// account-level raw payload.</summary>
    string? RawAccountPayload = null);
