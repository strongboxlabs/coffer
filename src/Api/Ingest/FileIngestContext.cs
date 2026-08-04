namespace Coffer.Api.Ingest;

/// <summary>
/// Context an <see cref="IFileProvider"/> needs that isn't
/// inferable from the payload bytes alone. Carried separately so
/// providers stay stateless.
/// </summary>
/// <remarks>
/// Per-institution CSV providers (ADR-0031 §4 / Phase 6) consult
/// <see cref="MappingId"/> when present to load a saved
/// <c>feed_csv_mappings</c> row; OFX / QFX providers ignore it.
/// </remarks>
public sealed record FileIngestContext(
    /// <summary>The ledger the upload targets. All ingested records
    /// must land in this ledger; the orchestrator enforces this
    /// when it writes to the DB.</summary>
    Guid LedgerId,
    /// <summary>The Coffer account the imported transactions land in.
    /// Most file formats have no native account binding (CSV
    /// especially), so the user picks the destination at upload
    /// time. For OFX / QFX multi-account files the caller pairs
    /// this with <see cref="ProviderAccountId"/> to scope the
    /// import to one statement block per call.</summary>
    Guid AccountId,
    /// <summary>Audit attribution for the <c>sync_runs</c> row this
    /// import generates. The endpoint resolves the current user
    /// before constructing the context.</summary>
    Guid TriggeredByUserId,
    /// <summary>For <c>csv-generic</c>: the
    /// <c>feed_csv_mappings.id</c> to load for column-to-field
    /// mapping. NULL for OFX / QFX / per-institution CSV providers
    /// where the mapping is implicit.</summary>
    Guid? MappingId = null,
    /// <summary>For OFX / QFX uploads: the composite provider-side
    /// account key (e.g. <c>{BANKID}:{ACCTID}</c>) the user wants to
    /// import from this multi-account file. The orchestrator filters
    /// parsed transactions to those whose
    /// <see cref="IngestedTransaction.ProviderAccountId"/> matches.
    /// NULL for single-account file formats — every parsed
    /// transaction lands in <see cref="AccountId"/>.</summary>
    string? ProviderAccountId = null);
