namespace Coffer.Api.Contracts;

/// <summary>
/// One tag in the per-ledger dictionary (Tags v1). Carries the assignment count that
/// gates cleanup, and the number of transactions the tag is in play on, which is what the
/// management panel and the autocomplete show. <see cref="Color"/> is <c>null</c> for
/// never-coloured tags (rendered gray).
/// </summary>
/// <param name="UsageCount">Pairings: a transaction tagged on itself and on a posting
/// counts twice (ADR-0103). Zero means nothing points at the tag, which is what makes it
/// safe to remove.</param>
/// <param name="TransactionCount">Distinct transactions the tag is in play on a line of: the
/// lines a register shows it on, which is what the tag's own page lists. A line with no tags of
/// its own shows its category side's, so those count; a tag a line's own tags hide (the category
/// side's, when the money line has tags of its own), reminder templates, hidden rows and rows
/// merged away are not counted. The number a reader means by "how many transactions"
/// (ADR-0111).</param>
public sealed record TagDto(Guid Id, string Name, string? Color, int UsageCount, int TransactionCount);

/// <summary>
/// Create a tag by name. <see cref="Color"/> is optional (<c>#rrggbb</c>); a tag with none
/// is drawn gray. A name another tag already carries (case and surrounding white space
/// aside) is refused with <c>tag-name-exists</c>. Tags are otherwise made on first use, when
/// a name is assigned to a transaction.
/// </summary>
public sealed record CreateTagRequest(string? Name, string? Color);

/// <summary>
/// Rename and/or recolor a tag. Both optional: an absent (JSON <c>null</c>)
/// field is left unchanged. Renaming to a name another tag already carries
/// (case-insensitive) is rejected with <c>tag-name-exists</c> — merge instead.
/// A colour is a <c>#rrggbb</c> hex value (validated server-side).
/// </summary>
public sealed record PatchTagRequest(string? Name, string? Color);

/// <summary>Merge one tag into another (<see cref="IntoTagId"/>): every
/// assignment on the source is repointed to the target (deduped), then the
/// source tag is deleted. Atomic.</summary>
public sealed record MergeTagRequest(Guid IntoTagId);

/// <summary>Echo of a tag merge: how many assignments were repointed, and how many
/// saved reports the merging user can see now filter on the target.</summary>
/// <param name="SavedReportsRepointed">See <see cref="IMergeTagResponseNotes.SavedReportsRepointed"/>.</param>
public sealed record MergeTagResponse(int TransactionsRepointed, int SavedReportsRepointed)
    : IMergeTagResponseNotes;

/// <summary>Echo of a cleanup-unused sweep — how many orphan tags were removed.</summary>
public sealed record CleanupTagsResponse(int TagsRemoved);
