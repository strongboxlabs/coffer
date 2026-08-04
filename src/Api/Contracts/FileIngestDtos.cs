namespace Coffer.Api.Contracts;

// Provider-neutral file-ingest wire shapes (ADR-0031 Phase 4 /
// ADR-0042). OFX/QFX and QIF imports share identical preview/import
// response shapes — they differ only by route segment, provider-key
// constant, and error-code prefix (carried by the endpoint mapper,
// not the DTO). These records replace the former per-provider
// OfxIngestDtos / QifIngestDtos sets; the JSON field names are
// unchanged so the SPA's existing ofx.ts / qif.ts types stay
// byte-compatible.

/// <summary>
/// One account block surfaced from a parsed import file. For OFX/QFX
/// this is one statement block (bank / credit_card / investment); for
/// QIF — single-account-implicit — there is exactly one entry with a
/// sentinel <see cref="ProviderAccountId"/>.
/// </summary>
public sealed record FileIngestAccountDto(
    /// <summary>Composite provider-stable key for this account block.
    /// The SPA echoes it back to the import endpoint as the
    /// <c>providerAccountId</c> filter so the orchestrator dispatches
    /// the right transactions to the right Coffer account. Opaque
    /// format — see each provider.</summary>
    string ProviderAccountId,
    /// <summary>Coarse account type the provider reported: OFX —
    /// <c>"bank"</c> / <c>"credit_card"</c> / <c>"investment"</c>;
    /// QIF — <c>"investment"</c> / <c>"bank"</c>. Drives the SPA
    /// mapping wizard's suggested-account filter.</summary>
    string AccountType,
    /// <summary>ISO-4217 currency reported on the statement (e.g.
    /// <c>"USD"</c>). NULL when the file omitted it (always null for
    /// QIF).</summary>
    string? Currency,
    /// <summary>Count of supported transactions parsed for this
    /// account block — previews the import size to the user.</summary>
    int TransactionCount);

/// <summary>One partial-failure entry surfaced during preview or
/// import (e.g. an unsupported investment transaction type that was
/// skipped).</summary>
public sealed record FileIngestErrorDto(string Code, string Message);

/// <summary>
/// Response body for <c>POST /api/ledgers/{ledgerId}/ingest/{segment}/preview</c>.
/// The SPA's upload wizard uses this to show "this file contains N
/// accounts" before the user picks per-account mappings. No DB writes
/// happen on preview.
/// </summary>
public sealed record FileIngestPreviewResponse(
    IReadOnlyList<FileIngestAccountDto> Accounts,
    IReadOnlyList<FileIngestErrorDto> Errors);

/// <summary>
/// Response body for <c>POST /api/ledgers/{ledgerId}/ingest/{segment}/import</c>.
/// Mirrors the SimpleFIN sync result so the SPA reuses its existing
/// import-result rendering across every ingest source.
/// </summary>
public sealed record FileIngestImportResponse(
    Guid SyncRunId,
    int AccountsDiscovered,
    int TransactionsForReview,
    int AlreadyKnown,
    IReadOnlyList<FileIngestErrorDto> Errors);
