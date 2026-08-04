namespace Coffer.Api.Contracts;

/// <summary>
/// Wire shape for the admin MCP runtime toggle (ADR-0063 §D8).
/// </summary>
/// <param name="Enabled">The persisted desired state (the `mcp.enabled` system
/// setting) — the toggle position.</param>
/// <param name="Active">Whether MCP is actually live in the currently-running
/// process (decided at startup). When this differs from <paramref name="Enabled"/>
/// the change is pending a server restart.</param>
/// <param name="ConfigForced">Whether <c>Api:Mcp:Enabled</c> config forces MCP on
/// regardless of the setting (a deployment override). When true the DB setting
/// cannot turn MCP off; the UI surfaces this.</param>
/// <param name="WritesEnabled">Persisted desired state of the MCP <b>write</b>
/// toggle (`mcp.writes_enabled`, ADR-0068). Only meaningful when MCP is on.</param>
/// <param name="WritesActive">Whether the write tools are actually registered in
/// the running process. Differs from <paramref name="WritesEnabled"/> ⇒ pending
/// restart.</param>
/// <param name="WritesConfigForced">Whether <c>Api:Mcp:WritesEnabled</c> config
/// forces writes on regardless of the setting.</param>
public sealed record McpSettingResponse(
    bool Enabled,
    bool Active,
    bool ConfigForced,
    bool WritesEnabled,
    bool WritesActive,
    bool WritesConfigForced);

/// <summary>Request body for setting the MCP runtime toggles.</summary>
/// <param name="Enabled">Desired MCP master state to persist.</param>
/// <param name="WritesEnabled">Desired MCP write-tools state to persist.</param>
public sealed record McpSettingRequest(bool Enabled, bool WritesEnabled);
