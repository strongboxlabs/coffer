namespace Coffer.Api.Db.Entities;

/// <summary>
/// A row in <c>oauth_connection_meta</c>: the owner-chosen name and the last-used time of one
/// OAuth connection to <c>/mcp</c>, keyed on its OpenIddict authorization id. OpenIddict's own
/// schema carries neither. A connection with no row has no name set and no recorded use.
/// </summary>
public sealed class OAuthConnectionMetaRow
{
    public string AuthorizationId { get; init; } = string.Empty;
    public Guid UserId { get; init; }
    public string? Label { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
