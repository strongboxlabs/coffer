namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of one posting in <c>transactions</c> under the
/// symmetric-posting model (ADR-0019). Every flow is a row; every row
/// pairs with exactly one <see cref="CounterpartyId"/>; rows that belong
/// to one logical user event share a <see cref="TxnGroupId"/>.
/// </summary>
/// <remarks>
/// <para>Per ADR-0003 the <c>feed_*</c> columns hold immutable raw values;
/// user edits live in a separate <c>transaction_overrides</c> row.</para>
///
/// <para>The investment-side metadata (<see cref="SecurityId"/>,
/// <see cref="Quantity"/>, <see cref="UnitPrice"/>) is non-null only on
/// the holdings-side row of an investment-transaction pair. On every
/// other row those fields are <c>NULL</c>. The legacy <c>commission</c>
/// column was dropped in migration 046 — fees live on their own paired
/// row under one <c>txn_group_id</c> per ADR-0019 Rule 5, and
/// <c>lots.unit_cost</c> carries the apportioned commission for
/// cost-basis math.</para>
/// </remarks>
public sealed record TransactionRow(
    Guid Id,
    Guid AccountId,
    string Origin,
    string? ExternalId,
    bool IsPending,
    string? InvestmentAction,
    string? FeedPayee,
    string? FeedMemo,
    decimal FeedAmount,
    DateTimeOffset FeedPostedAt,
    DateTimeOffset? FeedTransactedAt,
    string? FeedStatus,
    string? ImportSource,
    Guid CounterpartyId,
    Guid? TxnGroupId,
    int LegIndex,
    Guid? SecurityId,
    decimal? Quantity,
    decimal? UnitPrice,
    string? CheckNumber,
    // Migration 056: investment posting role for this row's posting.
    // One of 'security', 'income', 'transfer', 'fee'; NULL on
    // non-investment rows. Used by MapToHeaderAndLegs to stamp the
    // same role on both legs of the emitted posting.
    string? PostingRole = null,
    // Mig 107: per-provider audit detail. The investment mapper
    // computes (Origin, ProviderKey) from MD per-row metadata up
    // front; both ends of the pair carry the same value so
    // BuildHeader can plug it into the emitted TxnHeaderRow.
    string? ProviderKey = null,
    // Mig 109 / ADR-0035 §3: verbatim per-row MD JSON, forwarded
    // through to TxnHeaderRow.ProviderRawPayload at BuildHeader
    // time. Both rows of the investment pair carry the same value.
    string? ProviderRawPayload = null,
    // ADR-0082: this leg's OWN reconciliation-status source (raw MD stat,
    // NO parent fallback), used to seed txn_leg_recon per leg. The brokerage
    // cash leg carries the txn's parent stat; an external-cash counterparty
    // carries its own split stat; the Holdings/security leg is NULL (a
    // position, never reconciled). Absent/space => uncleared (no overlay row).
    string? ReconStat = null);
