namespace Coffer.Api.Mcp;

/// <summary>
/// Argument parsing shared by the MCP tools. A string enum param FAILS LOUD on an
/// unrecognized value — the thrown <see cref="ArgumentException"/> surfaces to the
/// caller as a tool error, which the model corrects from — instead of silently
/// coercing it to a default. A silent default returns a <i>different</i> report
/// than the model asked for, which it then narrates as the answer: the numbers
/// stay authoritative (ADR-0063 §D4) but the framing is silently wrong, with no
/// signal. The error message lists the valid values so the model can retry.
/// </summary>
internal static class McpArgs
{
    /// <summary>
    /// Parse <paramref name="value"/> into <typeparamref name="TEnum"/>
    /// (case-insensitive), or throw with the allowed values. Rejects numeric /
    /// undefined inputs (<c>Enum.TryParse</c> accepts "5"); only defined names
    /// pass.
    /// </summary>
    public static TEnum ParseEnum<TEnum>(string value, string paramName)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unknown {paramName} '{value}'. Valid values: "
                + string.Join(", ", Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()))
                + ".");
}
