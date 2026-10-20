using System.Text.Json;

namespace Coffer.Api.Contracts;

/// <summary>
/// A memorized report (ADR-0104 D3).
/// </summary>
/// <param name="Spec">
/// The ReportSpec, as a JSON object rather than a string. The column is jsonb
/// and the API surface should match it: returning a quoted string would make
/// every client parse a payload the server already had structured, and would
/// let a malformed spec round-trip unnoticed.
/// </param>
/// <param name="OwnerUserId">
/// Whose report it is. Present on every response because a ledger-shared
/// report is readable by people who cannot edit it, and the UI has to be able
/// to tell those two states apart without a second request.
/// </param>
public sealed record SavedReportDto(
    Guid Id,
    string Name,
    JsonElement Spec,
    string Visibility,
    Guid OwnerUserId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Create a memorized report. Visibility defaults to private.</summary>
public sealed class CreateSavedReportRequest
{
    public string? Name { get; init; }
    public JsonElement? Spec { get; init; }
    public string? Visibility { get; init; }
}

/// <summary>
/// Patch a memorized report. Every field is optional; an omitted field is left
/// alone, which is what lets a rename and a re-share be separate actions
/// without either having to resend the spec.
/// </summary>
public sealed class PatchSavedReportRequest
{
    public string? Name { get; init; }
    public JsonElement? Spec { get; init; }
    public string? Visibility { get; init; }
}

/// <summary>
/// One entry in a user's report rail (ADR-0104 D2). Carries the name because
/// the rail renders labels and a pin row has none of its own.
/// </summary>
/// <param name="CatalogKey">
/// Set instead of <paramref name="SavedReportId"/> for a canned report. Name
/// and spec are null then: the client resolves both from its own catalog, so
/// a preset's definition is never frozen into a pin row.
/// </param>
/// <param name="Spec">
/// A saved report's spec, carried here on purpose. A rail entry links into the
/// report view, and that view takes its whole question from the URL — so a
/// link with only an id would open the DEFAULT report under a pinned name,
/// which is the same lie as a stale title. The join already has the row.
/// </param>
public sealed record PinnedReportDto(
    Guid? SavedReportId, string? CatalogKey, string? Name, JsonElement? Spec, int SortOrder);
