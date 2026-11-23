using System.Text.Json.Serialization;

namespace Coffer.Api.Contracts;

/// <summary>
/// The <c>quotes</c> preference namespace (ADR-0057 D4): which external,
/// opt-in market-data providers this ledger auto-prices with. Default empty —
/// no outbound egress until the user enables one. Also the wire shape for
/// <c>GET/PUT /api/ledgers/{id}/preferences/quotes</c>.
/// </summary>
public sealed record QuotesPrefs
{
    [JsonPropertyName("enabledProviders")]
    public IReadOnlyList<string> EnabledProviders { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One opt-in quote provider available to a ledger — the catalog the settings
/// UI renders toggles from (<c>GET /api/ledgers/{id}/quote-providers</c>).
/// </summary>
/// <param name="Host">The host its requests go to, so the switch can name where data is
/// sent (ADR-0114 D4, D8): Yahoo's chart host, and Frankfurter's from the install's
/// configured base URL (<c>Quotes:Frankfurter:BaseUrl</c>, set by
/// <c>COFFER_FX_RATES_URL</c>). The host alone, never a path, port or query; null when the
/// configured URL is not usable.</param>
public sealed record QuoteProviderDto(string Key, string DisplayName, string? Host);

/// <summary>
/// A <c>dashboard</c> layout as a PUT sends it (version 2): the per-ledger home
/// screen's columns and widgets. Used to check the document's structure and nothing
/// else.
/// </summary>
/// <remarks>
/// <para>The dashboard is an exception to ADR-0057's typed namespaces: the SPA owns
/// the widget catalog (keys, titles, default column and visibility, setting defaults
/// and allowed values) and resolves every default, so a new widget or setting needs
/// no API change. The API checks types and bounds, never vocabulary: it does not know
/// which keys, column names or settings exist.</para>
/// <para><see cref="UpdatedAt"/> is the stored row's <c>updated_at</c> as the last
/// read returned it, null when no row exists; it is never stored inside the
/// document.</para>
/// </remarks>
public sealed record DashboardDocument
{
    [JsonPropertyName("version")]
    public int? Version { get; init; }

    [JsonPropertyName("columns")]
    public string? Columns { get; init; }

    [JsonPropertyName("widgets")]
    public IReadOnlyList<DashboardWidgetDocument>? Widgets { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>One widget in a <see cref="DashboardDocument"/>. <see cref="Id"/> equals
/// <see cref="Key"/> for a widget that appears once; a report tile's id names its
/// report. Every <see cref="Settings"/> value is a boolean, number, string, null or
/// array of strings.</summary>
public sealed record DashboardWidgetDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("visible")]
    public bool? Visible { get; init; }

    [JsonPropertyName("column")]
    public string? Column { get; init; }

    [JsonPropertyName("settings")]
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Settings { get; init; }
}
