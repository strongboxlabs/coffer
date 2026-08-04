using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Quotes;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-(user, ledger) preferences (ADR-0057). v1 surfaces the <c>quotes</c>
/// namespace — the per-ledger opt-in for external market-data providers — plus
/// the catalog of available opt-in providers the settings UI renders toggles
/// from.
/// </summary>
public static class PreferencesEndpoints
{
    public static IEndpointRouteBuilder MapPreferencesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}").RequireAuthorization().RequireLedgerMembership();
        group.MapGet("/preferences/quotes", GetQuotesAsync);
        group.MapPut("/preferences/quotes", PutQuotesAsync);
        group.MapGet("/quote-providers", GetProvidersAsync);
        group.MapGet("/preferences/dashboard", GetDashboardAsync);
        group.MapPut("/preferences/dashboard", PutDashboardAsync);
        return routes;
    }

    /// <summary>The acting user's <c>quotes</c> pref for the ledger (defaulted).</summary>
    private static async Task<IResult> GetQuotesAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        UserPreferencesRepository prefs,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false))
            return LedgerNotVisible();
        var value = await prefs.GetQuotesAsync(currentUser.UserId, ledgerId, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(value);
    }

    /// <summary>
    /// Replace the acting user's <c>quotes</c> pref. Every enabled key must be a
    /// registered opt-in provider — 400 <c>quote-provider-unknown</c> otherwise.
    /// </summary>
    private static async Task<IResult> PutQuotesAsync(
        Guid ledgerId,
        QuotesPrefs body,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        UserPreferencesRepository prefs,
        IEnumerable<IQuotePullProvider> providers,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false))
            return LedgerNotVisible();

        var optIn = providers.Where(p => p.RequiresOptIn).Select(p => p.ProviderKey).ToHashSet(StringComparer.Ordinal);
        var requested = (body?.EnabledProviders ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var unknown = requested.FirstOrDefault(k => !optIn.Contains(k));
        if (unknown is not null)
            return BusinessError.Problem(BusinessError.Codes.QuoteProviderUnknown,
                $"'{unknown}' is not an opt-in quote provider.");

        var saved = new QuotesPrefs { EnabledProviders = requested };
        await prefs.SetQuotesAsync(currentUser.UserId, ledgerId, saved, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(saved);
    }

    /// <summary>The acting user's <c>dashboard</c> layout for the ledger (empty = default).</summary>
    private static async Task<IResult> GetDashboardAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        UserPreferencesRepository prefs,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false))
            return LedgerNotVisible();
        var value = await prefs.GetDashboardAsync(currentUser.UserId, ledgerId, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(value);
    }

    /// <summary>
    /// Replace the acting user's <c>dashboard</c> layout. The widget catalog
    /// lives in the SPA; the API stores the layout opaquely after dropping
    /// blank/duplicate keys (first occurrence wins).
    /// </summary>
    private static async Task<IResult> PutDashboardAsync(
        Guid ledgerId,
        DashboardPrefs body,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        UserPreferencesRepository prefs,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false))
            return LedgerNotVisible();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var widgets = (body?.Widgets ?? Array.Empty<DashboardWidgetPref>())
            .Where(w => !string.IsNullOrWhiteSpace(w.Key) && seen.Add(w.Key))
            .ToList();
        var saved = new DashboardPrefs { Widgets = widgets };
        await prefs.SetDashboardAsync(currentUser.UserId, ledgerId, saved, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(saved);
    }

    /// <summary>Catalog of opt-in quote providers (toggle list for settings).</summary>
    private static async Task<IResult> GetProvidersAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        IEnumerable<IQuotePullProvider> providers,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false))
            return LedgerNotVisible();
        var catalog = providers
            .Where(p => p.RequiresOptIn)
            .OrderBy(p => p.DisplayName, StringComparer.Ordinal)
            .Select(p => new QuoteProviderDto(p.ProviderKey, p.DisplayName))
            .ToList();
        return Results.Ok(catalog);
    }

    private static async Task<bool> NotVisibleAsync(
        LedgersRepository ledgers, ICurrentUserAccessor currentUser, Guid ledgerId, CancellationToken ct) =>
        await ledgers.GetVisibleByIdAsync(currentUser.UserId, ledgerId, ct).ConfigureAwait(false) is null;

    private static IResult LedgerNotVisible() =>
        BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
            "Ledger not found or not visible to this user.");
}
