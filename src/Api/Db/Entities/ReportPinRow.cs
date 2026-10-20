namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>report_pins</c> (ADR-0104 D2, migration 245) — a saved
/// report one user keeps in their sidebar.
/// </summary>
/// <remarks>
/// <para>Per USER, not per ledger. A pin says "I come back to this", which is
/// not a property of the report and not something its author decides for
/// everyone else on the ledger. The asymmetry with
/// <see cref="SavedReportRow.Visibility"/> is deliberate: a ledger-shared
/// report can be pinned by someone who cannot edit it, and unpinning it is
/// theirs alone.</para>
///
/// <para>Targets a saved report OR a catalog preset, exactly one. The second
/// matters more than it looks: without it the only route into the rail is to
/// SAVE something, which means inventing a name for "the report I always look
/// at".</para>
/// </remarks>
internal sealed class ReportPinRow
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>Set when the pin targets a memorized report.</summary>
    public Guid? SavedReportId { get; init; }

    /// <summary>
    /// Set when the pin targets a canned report. Only the KEY is stored: the
    /// name and spec come from the SPA's own catalog, so improving a preset
    /// never needs a data migration, and a key the client no longer knows
    /// simply disappears from the rail rather than lingering as an entry that
    /// opens a retired report.
    /// </summary>
    public string? CatalogKey { get; init; }

    /// <summary>
    /// Denormalized from <c>saved_reports.ledger_id</c>. Required for snapshot
    /// capture, which chunks on this column, and made a derived fact rather
    /// than an assertion by the composite FK to
    /// <c>saved_reports(id, ledger_id)</c>.
    /// </summary>
    public Guid LedgerId { get; init; }

    /// <summary>The reader's own order. A rail you can arrange is the point of
    /// pinning a few rather than listing everything.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; init; }
}
