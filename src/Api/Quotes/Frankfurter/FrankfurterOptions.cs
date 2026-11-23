namespace Coffer.Api.Quotes.Frankfurter;

/// <summary>
/// Where exchange rates are fetched from (ADR-0114 D4), bound from <c>Quotes:Frankfurter</c>.
/// An install points <see cref="BaseUrl"/> at its own Frankfurter container with
/// <c>COFFER_FX_RATES_URL</c> (docker-compose passes it through); unset or blank is the public
/// service. Whether rates are fetched at all is the per-ledger <c>frankfurter</c> opt-in
/// (ADR-0057 <c>quotes</c> preference), not config.
/// </summary>
/// <remarks>
/// The URL is the operator's, not a user's, so the outbound guard for user-entered URLs does
/// not apply to it (ADR-0114 D4).
/// </remarks>
public sealed class FrankfurterOptions
{
    public const string SectionName = "Quotes:Frankfurter";

    /// <summary>The public Frankfurter service.</summary>
    public const string DefaultBaseUrl = "https://api.frankfurter.dev";

    /// <summary>The service's root, with or without a path prefix and trailing slash. Blank
    /// means <see cref="DefaultBaseUrl"/>.</summary>
    public string? BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// <see cref="BaseUrl"/> as an absolute http(s) URI ending in a slash, so a relative
    /// request path lands under any prefix; null when it is set to something else. Never
    /// falls back to the public service for a value that is set but unusable: that would
    /// send requests somewhere the operator did not choose.
    /// </summary>
    public Uri? ResolveBaseUri()
    {
        var raw = string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }
}
