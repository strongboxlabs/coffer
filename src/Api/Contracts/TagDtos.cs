namespace Coffer.Api.Contracts;

/// <summary>
/// One tag in the per-ledger dictionary (Tags v1). Carries the assignment count
/// the management panel + autocomplete need to show usage and gate cleanup.
/// <see cref="Color"/> is <c>null</c> for never-coloured tags (rendered gray).
/// </summary>
public sealed record TagDto(Guid Id, string Name, string? Color, int UsageCount);

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

/// <summary>Echo of a tag merge — how many assignments were repointed.</summary>
public sealed record MergeTagResponse(int TransactionsRepointed);

/// <summary>Echo of a cleanup-unused sweep — how many orphan tags were removed.</summary>
public sealed record CleanupTagsResponse(int TagsRemoved);
