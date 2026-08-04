namespace Coffer.Api.Contracts;

/// <summary>
/// One operation in the ledger-wide activity timeline (ADR-0055/0086) — any family
/// (ingest / quote / snapshot; i.e. feed sync, file or Moneydance import, quote
/// refresh, or snapshot restore). The family-specific counters live in
/// <see cref="Details"/> (parsed from <c>ledger_operations.details</c>); the SPA
/// renders the relevant keys per family. <see cref="TriggeredByUserId"/> is the
/// real user, or the system user for scheduled runs.
/// </summary>
public sealed record LedgerOperationSummaryDto(
    Guid Id,
    string Family,
    string ProviderKey,
    string TriggeredVia,
    string Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    Guid? TriggeredByUserId,
    IReadOnlyDictionary<string, int> Details,
    int ErrorCount);
