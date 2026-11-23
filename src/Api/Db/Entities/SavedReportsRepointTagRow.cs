namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result of <c>saved_reports_repoint_tag(ledger, from, to)</c> (migration 263),
/// the privileged rewrite of <c>saved_reports.spec.tagId</c> a tag merge runs.
/// </summary>
/// <remarks>
/// <see cref="Repointed"/> counts every report changed, other members' private ones
/// included; <see cref="RepointedVisible"/> only those the caller can see. Only the
/// second may reach a response: the first would reveal that another member keeps
/// private reports on the tag.
/// </remarks>
public sealed class SavedReportsRepointTagRow
{
    public int Repointed { get; init; }
    public int RepointedVisible { get; init; }
}
