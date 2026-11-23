using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.Frankfurter;

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
        // A viewer may reset their own layout: it is their view state, not ledger data.
        group.MapDelete("/preferences/dashboard", DeleteDashboardAsync);
        return routes;
    }

    /// <summary>The longest layout document accepted, in characters: far above a real
    /// one, and independent of the widget catalog. CsvMappingValidator's cap.</summary>
    internal const int MaxDashboardLength = 64 * 1024;

    /// <summary>The deepest nesting accepted. A layout is four levels deep.</summary>
    internal const int MaxDashboardDepth = 16;

    /// <summary>The longest column name, widget key or column value accepted.</summary>
    private const int MaxNameLength = 64;

    /// <summary>The longest widget id: a report tile's id carries its report's.</summary>
    private const int MaxIdLength = 128;

    private static readonly JsonSerializerOptions DashboardJson = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = MaxDashboardDepth,
    };

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
        IEnumerable<OptInProvider> otherOptIn,
        LedgerAuthorizer authorizer,
        Quotes.MarketSeries.MarketSeriesBackfill backfill,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false))
            return LedgerNotVisible();

        var optIn = OptInProviders.Catalog(providers, otherOptIn)
            .Select(p => p.ProviderKey)
            .ToHashSet(StringComparer.Ordinal);
        var requested = (body?.EnabledProviders ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var unknown = requested.FirstOrDefault(k => !optIn.Contains(k));
        if (unknown is not null)
            return BusinessError.Problem(BusinessError.Codes.QuoteProviderUnknown,
                $"'{unknown}' is not an opt-in quote provider.");

        var before = await prefs.GetQuotesAsync(currentUser.UserId, ledgerId, cancellationToken)
            .ConfigureAwait(false);
        var saved = new QuotesPrefs { EnabledProviders = requested };
        await prefs.SetQuotesAsync(currentUser.UserId, ledgerId, saved, cancellationToken)
            .ConfigureAwait(false);

        // ADR-0114 D11: turning Frankfurter on backfills the ledger's rates at once. Only
        // for a member who can write the ledger: a viewer's own opt-in is theirs to keep,
        // but the rates it would fetch are ledger data they cannot store.
        var turnedOn = requested.Contains(FrankfurterRatesClient.Key, StringComparer.Ordinal)
                       && !before.EnabledProviders.Contains(FrankfurterRatesClient.Key, StringComparer.Ordinal);
        if (turnedOn && await authorizer.CanWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false))
            backfill.Start(ledgerId, currentUser.UserId);
        return Results.Ok(saved);
    }

    /// <summary>
    /// The acting user's <c>dashboard</c> layout for the ledger: the stored document as
    /// stored, whatever its version, plus <c>updatedAt</c> from the row. With no row, an
    /// empty document and <c>updatedAt: null</c>; the SPA resolves either to a layout.
    /// </summary>
    private static async Task<IResult> GetDashboardAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        UserPreferencesRepository prefs,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false))
            return LedgerNotVisible();
        var (json, updatedAt) = await prefs.GetDashboardAsync(currentUser.UserId, ledgerId, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(WithUpdatedAt(json, updatedAt));
    }

    /// <summary>
    /// Replace the acting user's <c>dashboard</c> layout, if nobody saved since their
    /// last read.
    /// </summary>
    /// <remarks>
    /// <para>Checks structure only (<see cref="DashboardDocument"/>): a version-2
    /// document within the length and depth caps, with typed, non-blank, bounded fields.
    /// The SPA resolver maps any value it does not know to its catalog default, so the
    /// API judges nothing it would need a layout concept to judge. A version-1 document
    /// is refused: only the SPA's migration writes v2 from it. Blank and duplicate ids
    /// are dropped, the first occurrence winning.</para>
    /// <para>A stale <c>updatedAt</c> is 422 <c>dashboard-prefs-stale</c> and writes
    /// nothing; the response to a save carries the row's new <c>updatedAt</c> for the
    /// next one.</para>
    /// </remarks>
    private static async Task<IResult> PutDashboardAsync(
        Guid ledgerId,
        HttpRequest http,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        UserPreferencesRepository prefs,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false))
            return LedgerNotVisible();

        var body = await ReadCappedAsync(http, MaxDashboardLength + 1, cancellationToken).ConfigureAwait(false);
        var check = CheckDashboard(body);
        if (check.Problem is { } problem) return problem;

        var updatedAt = await prefs
            .SaveDashboardAsync(currentUser.UserId, ledgerId, check.Stored!, check.UpdatedAt, cancellationToken)
            .ConfigureAwait(false);
        if (updatedAt is null)
            return BusinessError.Problem(BusinessError.Codes.DashboardPrefsStale,
                "The layout changed since it was read. Read it again before saving.");

        return Results.Ok(WithUpdatedAt(check.Stored, updatedAt));
    }

    /// <summary>
    /// A layout document's structure checks, the PUT handler's (<see cref="PutDashboardAsync"/>
    /// remarks): the document as it would be stored and the <c>updatedAt</c> it carries, or
    /// the 422 <c>dashboard-prefs-invalid</c> it gets.
    /// </summary>
    /// <remarks>Shared with the Demo's examples phase, which runs them on the Home layout it
    /// writes (ADR-0115).</remarks>
    internal static DashboardCheck CheckDashboard(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length > MaxDashboardLength)
            return DashboardCheck.Refused($"A layout is at most {MaxDashboardLength} characters.");

        DashboardDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<DashboardDocument>(body, DashboardJson);
        }
        catch (JsonException)
        {
            return DashboardCheck.Refused(
                $"A layout is a JSON object nested at most {MaxDashboardDepth} levels, with the documented types.");
        }

        if (document is null || document.Version != 2)
            return DashboardCheck.Refused("A layout is a version 2 document.");
        if (!IsName(document.Columns, MaxNameLength) || document.Widgets is null)
            return DashboardCheck.Refused("A layout names its columns and lists its widgets.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var widgets = new List<object>(document.Widgets.Count);
        foreach (var w in document.Widgets)
        {
            if (w is null || !IsName(w.Key, MaxNameLength) || !IsName(w.Column, MaxNameLength) || w.Visible is null)
                return DashboardCheck.Refused("Each widget has a key, a column and a visibility.");
            if (w.Id is { Length: > MaxIdLength })
                return DashboardCheck.Refused($"A widget id is at most {MaxIdLength} characters.");
            if (w.Settings is { } settings && !settings.Values.All(IsSettingValue))
                return DashboardCheck.Refused(
                    "Every setting is a boolean, number, string, null or list of strings.");

            // Dropped rather than refused: a blank or repeated id carries nothing the
            // first occurrence does not.
            if (string.IsNullOrWhiteSpace(w.Id) || !seen.Add(w.Id)) continue;
            widgets.Add(new
            {
                id = w.Id,
                key = w.Key,
                visible = w.Visible.Value,
                column = w.Column,
                settings = w.Settings ?? new Dictionary<string, JsonElement>(),
            });
        }

        // The document as stored: never its updatedAt, which is the row's.
        var stored = JsonSerializer.Serialize(
            new { version = 2, columns = document.Columns, widgets }, DashboardJson);
        return new DashboardCheck(stored, document.UpdatedAt, null);
    }

    /// <summary>What <see cref="CheckDashboard"/> found: the document to store and the
    /// <c>updatedAt</c> it carried, or the refusal.</summary>
    internal sealed record DashboardCheck(string? Stored, DateTime? UpdatedAt, IResult? Problem)
    {
        public static DashboardCheck Refused(string detail) => new(null, null, DashboardInvalid(detail));
    }

    /// <summary>
    /// Remove the acting user's <c>dashboard</c> layout, so the default applies. 204,
    /// whether or not one was stored; a later PUT then carries <c>updatedAt: null</c>.
    /// </summary>
    private static async Task<IResult> DeleteDashboardAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        UserPreferencesRepository prefs,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false))
            return LedgerNotVisible();
        await prefs.DeleteDashboardAsync(currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>The stored document with the row's <c>updatedAt</c> beside its own
    /// fields; an empty document when nothing usable is stored.</summary>
    private static JsonObject WithUpdatedAt(string? json, DateTime? updatedAt)
    {
        JsonObject document;
        try
        {
            document = json is null ? EmptyDashboard() : JsonNode.Parse(json) as JsonObject ?? EmptyDashboard();
        }
        catch (JsonException)
        {
            document = EmptyDashboard();
        }
        document["updatedAt"] = updatedAt is { } at ? JsonValue.Create(at) : null;
        return document;
    }

    // Today's empty document, which the SPA reads as "use the default".
    private static JsonObject EmptyDashboard() => new() { ["widgets"] = new JsonArray() };

    private static bool IsName(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static bool IsSettingValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number
            or JsonValueKind.String or JsonValueKind.Null => true,
        JsonValueKind.Array => value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
        _ => false,
    };

    private static IResult DashboardInvalid(string detail) =>
        BusinessError.Problem(BusinessError.Codes.DashboardPrefsInvalid, detail);

    /// <summary>The request body, read no further than <paramref name="maxChars"/>
    /// characters, so an oversized body is refused without being held whole.</summary>
    private static async Task<string> ReadCappedAsync(
        HttpRequest http, int maxChars, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(http.Body, Encoding.UTF8);
        var buffer = new char[maxChars];
        var read = 0;
        while (read < maxChars)
        {
            var n = await reader.ReadAsync(buffer.AsMemory(read, maxChars - read), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        return new string(buffer, 0, read);
    }

    /// <summary>Catalog of opt-in quote providers (toggle list for settings).</summary>
    private static async Task<IResult> GetProvidersAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        IEnumerable<IQuotePullProvider> providers,
        IEnumerable<OptInProvider> otherOptIn,
        CancellationToken cancellationToken)
    {
        if (await NotVisibleAsync(ledgers, currentUser, ledgerId, cancellationToken).ConfigureAwait(false))
            return LedgerNotVisible();
        var catalog = OptInProviders.Catalog(providers, otherOptIn)
            .Select(p => new QuoteProviderDto(p.ProviderKey, p.DisplayName, p.DestinationHost))
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
