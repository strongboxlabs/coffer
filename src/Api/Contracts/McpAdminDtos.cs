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
public sealed record McpClientDto(
    string ClientId,
    string DisplayName,
    string ClientType,
    IReadOnlyList<string> RedirectUris,
    int ActiveAuthorizations);
