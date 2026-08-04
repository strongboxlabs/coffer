namespace Coffer.Api.Sync.SimpleFin;

/// <summary>
/// Surfaced from <see cref="SimpleFinClient"/> for protocol-level
/// failures the endpoint should map to a user-facing 422 (vs. an
/// opaque 500). Wraps the network/parse exception when present so
/// telemetry still captures the root cause.
/// </summary>
public sealed class SimpleFinException : Exception
{
    public SimpleFinException(string message) : base(message) { }
    public SimpleFinException(string message, Exception inner) : base(message, inner) { }
}
