namespace Coffer.Api.Quotes;

/// <summary>
/// An opt-in egress provider that is not an <see cref="IQuotePullProvider"/>: its key may sit
/// in the <c>quotes</c> preference's <c>enabledProviders</c> and it is listed on the Quotes tab
/// beside Yahoo, but the quote pull never runs it, since it prices no held security
/// (ADR-0114 D8: Frankfurter's exchange rates). Registered as a singleton per provider.
/// </summary>
/// <remarks>
/// The opt-in catalog is the pull providers that <see cref="IQuotePullProvider.RequiresOptIn"/>
/// plus these (<see cref="OptInProviders.Catalog"/>), so registering one is all it takes for
/// the preference to accept its key.
/// </remarks>
/// <param name="DestinationHost">The host the provider's requests go to, as the Quotes tab
/// names it: the host alone, never a path, port or query. Null when it is not known, such as
/// a configured base URL that is not usable.</param>
public sealed record OptInProvider(string ProviderKey, string DisplayName, string? DestinationHost = null);

/// <summary>The one place the opt-in catalog is assembled.</summary>
public static class OptInProviders
{
    /// <summary>Every opt-in provider, keyed once: the opt-in quote pull providers by display
    /// name, then the others in registration order, so Frankfurter is the row after Yahoo
    /// (ADR-0114 D8).</summary>
    public static IReadOnlyList<OptInProvider> Catalog(
        IEnumerable<IQuotePullProvider> pullProviders, IEnumerable<OptInProvider> others)
    {
        ArgumentNullException.ThrowIfNull(pullProviders);
        ArgumentNullException.ThrowIfNull(others);
        return pullProviders
            .Where(p => p.RequiresOptIn)
            .OrderBy(p => p.DisplayName, StringComparer.Ordinal)
            .Select(p => new OptInProvider(p.ProviderKey, p.DisplayName, p.DestinationHost))
            .Concat(others)
            .DistinctBy(p => p.ProviderKey, StringComparer.Ordinal)
            .ToList();
    }
}
