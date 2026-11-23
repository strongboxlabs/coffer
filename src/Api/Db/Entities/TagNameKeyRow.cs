namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless row of <c>tag_name_keys(text[])</c> (migration 263): one requested tag name
/// and its <c>tag_name_key</c>, so a caller learns every key of a request in the same
/// round trip that finds the matching tags, without folding a name in .NET.
/// </summary>
public sealed class TagNameKeyRow
{
    public string Name { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
}
