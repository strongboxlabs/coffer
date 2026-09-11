namespace Coffer.Api.Ingest;

/// <summary>
/// Context an <see cref="IFileProvider"/> needs that isn't
/// inferable from the payload bytes alone. Carried separately so
/// providers stay stateless.
/// </summary>
/// <remarks>
/// The generic CSV provider (ADR-0031 Phase 5) reads
/// <see cref="CsvMapping"/>, which the ENDPOINT resolves — from a saved
/// <c>feed_csv_mappings</c> row or from draft YAML that has never been
/// saved. OFX / QFX and the per-institution providers of Phase 6 ignore
/// it; their format is implicit.
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
    /// <summary>
    /// For <c>csv-generic</c>: the ALREADY-VALIDATED mapping to read the file with.
    /// NULL for OFX / QFX / per-institution providers, whose format is implicit.
    /// </summary>
    /// <remarks>
    /// The resolved mapping, deliberately, not a <c>feed_csv_mappings.id</c>. Two
    /// reasons, and the second is the one that decided it:
    /// <list type="number">
    ///   <item><description>Providers stay PURE. Every other one is a function from a
    ///   stream to a result with no database access at all, which is what makes a
    ///   preview endpoint free — parse, return, write nothing. Handing this one an id
    ///   would give it a DbContext and end that.</description></item>
    ///   <item><description>The wizard can preview a DRAFT. Passing an id means a
    ///   mapping must be SAVED before it can be tried, so getting a delimiter wrong
    ///   would leave half-built rows behind. Resolving in the endpoint lets the caller
    ///   hand over YAML that exists nowhere yet.</description></item>
    /// </list>
    /// Being resolved also means being validated: <c>CsvMappingValidator</c> is the only
    /// thing that constructs a <see cref="Csv.CsvMapping"/>, so a provider holding one
    /// need not re-check it.
    /// </remarks>
    Csv.CsvMapping? CsvMapping = null,
    /// <summary>For OFX / QFX uploads: the composite provider-side
    /// account key (e.g. <c>{BANKID}:{ACCTID}</c>) the user wants to
    /// import from this multi-account file. The orchestrator filters
    /// parsed transactions to those whose
    /// <see cref="IngestedTransaction.ProviderAccountId"/> matches.
    /// NULL for single-account file formats — every parsed
    /// transaction lands in <see cref="AccountId"/>.</summary>
    string? ProviderAccountId = null);
