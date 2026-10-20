namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>saved_reports</c> (ADR-0104 D3, migration 244) — a
/// memorized report.
/// </summary>
/// <remarks>
/// <para>A row rather than a <c>reports</c> namespace in
/// <c>user_preferences</c>. That table is a singleton document per namespace,
/// so a collection of user-named, individually-deletable things would share one
/// blob: every save rewrites the whole list, no report gets a stable id, and —
/// the part that cannot be fixed afterwards — a preference is permanently
/// per-user. Coffer is multi-user on one ledger, and a household that cannot
/// show each other a report has a missing feature, not a missing
/// preference.</para>
///
/// <para><see cref="Spec"/> is the serialized <c>ReportSpec</c>, stored whole.
/// Columns per field would fork the shape the MCP tools already take, and the
/// next dimension would then need a migration as well as a C# change.</para>
/// </remarks>
internal sealed class SavedReportRow
{
    public Guid Id { get; init; }

    public Guid LedgerId { get; init; }

    /// <summary>
    /// Who wrote it. The RLS policy lets a ledger member READ a row whose
    /// <see cref="Visibility"/> is <c>ledger</c>, but only its owner may write
    /// it — shared means readable-by, not writable-by.
    /// </summary>
    public Guid OwnerUserId { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The serialized ReportSpec, as JSON.</summary>
    public string Spec { get; set; } = "{}";

    /// <summary><c>private</c> (its author only) or <c>ledger</c> (everyone
    /// granted this ledger). Default private: sharing is a decision someone
    /// makes, not one they discover they made.</summary>
    public string Visibility { get; set; } = SavedReportVisibility.Private;

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>The two values <c>ck_saved_reports_visibility</c> permits.</summary>
internal static class SavedReportVisibility
{
    public const string Private = "private";
    public const string Ledger = "ledger";

    public static bool IsValid(string? value) =>
        value is Private or Ledger;
}
