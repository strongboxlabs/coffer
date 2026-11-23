namespace Coffer.Api.Contracts;

/// <summary>
/// Admin oversight shapes for MCP (ADR-0081 D5): the write-audit log, the connections people
/// hold to <c>/mcp</c>, and the OAuth clients registered against it.
/// </summary>
public sealed record McpAuditEntryDto(
    Guid Id,
    Guid UserId,
    string User,
    string ToolName,
    Guid? LedgerId,
    string? Arguments,
    string Status,
    string? Result,
    DateTime CreatedAt,
    DateTime? CompletedAt);

/// <summary>
/// An OAuth client registered against the MCP authorization server. A registration is shared:
/// many people can each connect through the one client, so it says nothing about who is using
/// it — that is <see cref="McpConnectionDto"/>.
/// </summary>
/// <param name="DisplayName">The name the client registered itself under, via DCR.
/// Client-supplied and therefore not unique: every install of a given client
/// registers under the same string.</param>
/// <param name="LiveConnections">How many live connections (valid and inside their age) were
/// made through this client: what revoking the registration would end, and the same rule
/// the connection list shows by. Zero means prune removes it.</param>
/// <param name="RegisteredAt">When the client registered; null for one registered before
/// Coffer recorded it, since OpenIddict keeps no such time.</param>
public sealed record McpClientDto(
    string ClientId,
    string DisplayName,
    string ClientType,
    IReadOnlyList<string> RedirectUris,
    int LiveConnections,
    DateTime? RegisteredAt);

/// <summary>What <see cref="McpConnectionDto.Kind"/> says a connection is.</summary>
public static class McpConnectionKinds
{
    /// <summary>A personal bearer token the owner minted and pasted into an app.</summary>
    public const string Token = "token";

    /// <summary>An app the owner signed in to through OAuth.</summary>
    public const string OAuth = "oauth";
}

/// <summary>
/// One connection to <c>/mcp</c> — a personal token or an OAuth sign-in — with who holds it,
/// as the admin sees it across users.
/// </summary>
/// <param name="Kind"><see cref="McpConnectionKinds"/>.</param>
/// <param name="Id">A token's id, or an OAuth authorization's id; with <paramref name="Kind"/>
/// it addresses the connection.</param>
/// <param name="User">The owner's display name.</param>
/// <param name="Name">What to call it: the owner's name for it, else the app's own.</param>
/// <param name="Label">The owner-chosen name for an OAuth connection, null when unset (a
/// token's name is its label, so this is null for one). Kept apart from
/// <paramref name="Name"/> so a rename form starts empty rather than pre-filled with the
/// app's name.</param>
/// <param name="AppName">An OAuth connection's client name, as the client registered it; null
/// for a token. Shown beside a renamed connection, because it says which software it is.</param>
/// <param name="LastUsedAt">Null when no use has been recorded: never used, or an OAuth
/// connection that predates the record.</param>
/// <param name="ClientId">An OAuth connection's client id, matching
/// <see cref="McpClientDto.ClientId"/>, so the list groups each sign-in under its app; null
/// for a token, and for a connection whose registration is gone.</param>
public sealed record McpConnectionDto(
    string Kind,
    string Id,
    Guid UserId,
    string User,
    string Name,
    string? Label,
    string? AppName,
    string? ClientId,
    bool CanWrite,
    DateTime ConnectedAt,
    DateTime? LastUsedAt,
    DateTime? ExpiresAt);

/// <summary>
/// Request body for renaming a connection, from its owner or an admin. A blank name clears an
/// OAuth connection's name, falling back to the app's own; a token must keep one.
/// </summary>
public sealed record RenameConnectionRequest(string? Name);
