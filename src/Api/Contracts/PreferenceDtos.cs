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
public sealed record QuoteProviderDto(string Key, string DisplayName);

/// <summary>
/// The <c>dashboard</c> preference namespace (ADR-0056 slice 3 / ADR-0057):
/// the per-ledger Overview layout. <see cref="Widgets"/> order = display order;
/// each carries a show/hide flag. The widget catalog (keys + labels) lives in
/// the SPA; the API stores the layout opaquely (distinct, non-empty keys), so a
/// new widget needs no API change. An unset pref means the canonical default
/// (all widgets, canonical order), resolved client-side.
/// </summary>
public sealed record DashboardPrefs
{
    [JsonPropertyName("widgets")]
    public IReadOnlyList<DashboardWidgetPref> Widgets { get; init; } = Array.Empty<DashboardWidgetPref>();
}

public sealed record DashboardWidgetPref(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("visible")] bool Visible);
