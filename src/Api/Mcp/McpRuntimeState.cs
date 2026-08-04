namespace Coffer.Api.Mcp;

/// <summary>
/// MCP runtime state (singleton). <see cref="Active"/> = whether the MCP surface +
/// OAuth AS were registered at startup (ADR-0063 §D8 — restart to change).
/// <see cref="WritesEnabled"/> = whether write tools are permitted RIGHT NOW
/// (ADR-0081 D2): a HOT flag the admin toggle flips immediately, read per write-tool
/// call by <see cref="McpWriteGuard"/> — so turning writes off takes effect at once,
/// no restart. The write tools are always registered (ADR-0081 D2); this flag, not
/// their presence, is the gate.
/// </summary>
public sealed class McpRuntimeState
{
    public bool Active { get; }

    // volatile: the admin thread flips it; request threads read it. A single bool
    // needs no lock — volatile guarantees the flip is visible promptly.
    private volatile bool _writesEnabled;

    public bool WritesEnabled
    {
        get => _writesEnabled;
        set => _writesEnabled = value;
    }

    public McpRuntimeState(bool active, bool writesEnabled)
    {
        Active = active;
        _writesEnabled = writesEnabled;
    }
}
