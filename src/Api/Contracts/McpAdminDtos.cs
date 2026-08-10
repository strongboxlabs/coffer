namespace Coffer.Api.Contracts;

/// <summary>
/// Admin oversight shapes for the MCP write surface (ADR-0081 D5): the write-audit
/// log and the OAuth clients that can reach <c>/mcp</c>.
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

/// <summary>An OAuth client registered against the MCP authorization server.</summary>
/// <param name="DisplayName">The name the client registered itself under, via DCR.
/// Client-supplied and therefore not unique: every install of a given client
/// registers under the same string, so a list of them is unreadable once there is
/// more than one.</param>
/// <param name="Label">Operator-assigned name, null when unset. The UI shows this
/// in preference to <paramref name="DisplayName"/> — it is the only way to tell
/// "Claude on the laptop" from "Claude on the phone".</param>
public sealed record McpClientDto(
    string ClientId,
    string DisplayName,
    string ClientType,
    IReadOnlyList<string> RedirectUris,
    int ActiveAuthorizations,
    string? Label);

/// <summary>Request body for renaming a client.</summary>
/// <param name="Label">New label; null or blank clears it, falling back to the
/// client's own registered name.</param>
public sealed record McpClientLabelRequest(string? Label);
