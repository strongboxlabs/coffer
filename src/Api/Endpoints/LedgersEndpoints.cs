using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Microsoft.Extensions.Logging;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Import;
using Coffer.Api.Provisioning;
using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Api.Quotes;
using Coffer.Api.Quotes.MarketSeries;
using Coffer.Domain.Money;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Ledger management + auto-open endpoints (per ADR-0020 Phase A and the
/// auto-open flow added to <c>users.last_opened_ledger_id</c>). Every
/// endpoint requires the default authorisation policy (Cookie or
/// DevAuth), so the current user comes from <see cref="ICurrentUserAccessor"/>.
/// </summary>
public static class LedgersEndpoints
{
    public static IEndpointRouteBuilder MapLedgersEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers")
                          .RequireAuthorization();

        group.MapGet("/", ListVisibleAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{ledgerId:guid}", RenameAsync);
        group.MapDelete("/{ledgerId:guid}", DeleteAsync);
        group.MapGet("/me/last-opened", GetLastOpenedAsync);
        group.MapPut("/me/last-opened/{ledgerId:guid}", PutLastOpenedAsync);
        // The hub's Demo ledger (ADR-0115 D2, D3): add one, and find its build again.
        group.MapPost("/demo", DemoLedgerEndpoints.StartAsync);
        group.MapGet("/demo/job", DemoLedgerEndpoints.GetJob);

        return routes;
    }

    /// <summary>
    /// Resolve the caller's role on a ledger, returning the owner-gate
    /// error result when they aren't an owner (or can't see it). Returns
    /// null when the caller IS an owner (proceed).
    /// </summary>
    private static async Task<IResult?> RequireOwnerAsync(
        Guid userId, Guid ledgerId, LedgersRepository ledgers, CancellationToken ct)
    {
        var ledger = await ledgers.GetVisibleByIdAsync(userId, ledgerId, ct).ConfigureAwait(false);
        if (ledger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");
        if (ledger.Role != "owner")
            return BusinessError.Problem(BusinessError.Codes.LedgerNotOwner,
                "Only an owner can rename a ledger, change its reporting currency, or delete it.");
        return null;
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}</c> — rename the ledger, change its reporting
    /// currency (ADR-0114 D7), or both. Owner only.
    /// <para>422: <c>ledger-name-required</c> (a blank name, or neither field sent),
    /// <c>ledger-currency-invalid</c>, <c>ledger-not-visible</c>, <c>ledger-not-owner</c>,
    /// <c>ledger-write-busy</c>.</para>
    /// <para>A currency change starts a market-series backfill under the owner's own
    /// consent (ADR-0114 D11); the response does not wait for it.</para>
    /// </summary>
    private static async Task<IResult> RenameAsync(
        Guid ledgerId,
        UpdateLedgerRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        MarketSeriesBackfill backfill,
        CancellationToken cancellationToken)
    {
        if (request.Name is null && request.CurrencyCode is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNameRequired,
                "name or currencyCode is required.");
        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
            return BusinessError.Problem(BusinessError.Codes.LedgerNameRequired,
                "name is required.");
        string? currency = null;
        if (request.CurrencyCode is not null && !CurrencyCodes.TryNormalize(request.CurrencyCode, out currency))
            return LedgerCurrencyInvalid();

        var gate = await RequireOwnerAsync(currentUser.UserId, ledgerId, ledgers, cancellationToken)
            .ConfigureAwait(false);
        if (gate is not null) return gate;

        // A new reporting currency must be one a ledger can take (ADR-0114 D15). Resending
        // the ledger's own stored code is not a change, so a code stored before the rule
        // keeps working, at 2 places.
        if (currency is not null && !CurrencyMinorUnits.IsAcceptedAccountCurrency(currency))
        {
            var stored = await ledgers.GetVisibleByIdAsync(currentUser.UserId, ledgerId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(stored?.CurrencyCode, currency, StringComparison.Ordinal))
                return LedgerCurrencyInvalid();
        }

        // The currency first: it is the half that can be refused as busy, and that refusal
        // says nothing was changed, so the rename must not have run yet.
        var currencyChanged = currency is not null
            && await ledgers.SetReportingCurrencyAsync(ledgerId, currency, cancellationToken).ConfigureAwait(false);
        if (request.Name is not null)
            await ledgers.RenameAsync(ledgerId, request.Name.Trim(), cancellationToken)
                         .ConfigureAwait(false);
        if (currencyChanged)
            backfill.Start(ledgerId, currentUser.UserId);
        return Results.NoContent();
    }

    /// <summary>The refusal of a reporting currency a ledger cannot take: not an ISO 4217
    /// currency with at most 3 places (ADR-0114 D15). Shared with the Moneydance import's
    /// chosen currency (<see cref="ImportEndpoints"/>).</summary>
    internal static IResult LedgerCurrencyInvalid() =>
        BusinessError.Problem(BusinessError.Codes.LedgerCurrencyInvalid,
            "The reporting currency must be an ISO 4217 currency code with at most 3 decimal places.");

    private static async Task<IResult> DeleteAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        CancellationToken cancellationToken)
    {
        var gate = await RequireOwnerAsync(currentUser.UserId, ledgerId, ledgers, cancellationToken)
            .ConfigureAwait(false);
        if (gate is not null) return gate;

        await ledgers.DeleteAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// <c>GET /api/ledgers</c> — the caller's visible ledgers, each with
    /// <see cref="LedgerListItem.DemoExamplesMissing"/> read from its Demo marker, if it
    /// has one (ADR-0115 D9).
    /// </summary>
    private static async Task<IResult> ListVisibleAsync(
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerOperationsRepository operations,
        ImportJobRegistry jobs,
        CancellationToken cancellationToken)
    {
        var rows = await ledgers.GetVisibleAsync(currentUser.UserId, cancellationToken)
                                .ConfigureAwait(false);
        var markers = await operations
            .DemoMarkersAsync(rows.Select(r => r.Id).ToList(), cancellationToken)
            .ConfigureAwait(false);
        var building = jobs.RunningDemoLedgerIds();

        return Results.Ok(rows.Select(r =>
        {
            var missing = markers.TryGetValue(r.Id, out var marker)
                ? DemoExamplesMissing.From(marker.Status, marker.DetailsJson, building.Contains(r.Id))
                : null;
            return new LedgerListItem(r.Id, r.Name, r.Role, r.CurrencyCode, missing);
        }).ToList());
    }

    private static async Task<IResult> CreateAsync(
        CreateLedgerRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        StarterCategoriesSeeder starterCategories,
        ServiceDbContextFactory serviceDb,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BusinessError.Problem(BusinessError.Codes.LedgerNameRequired,
                "name is required.");

        // ADR-0114 D7: the reporting currency is chosen here; blank is USD.
        var currency = DisplayCurrency.Fallback;
        if (!string.IsNullOrWhiteSpace(request.CurrencyCode)
            && (!CurrencyCodes.TryNormalize(request.CurrencyCode, out currency)
                || !CurrencyMinorUnits.IsAcceptedAccountCurrency(currency)))
            return LedgerCurrencyInvalid();

        var created = await ledgers.CreateWithOwnerAsync(
            currentUser.UserId, request.Name.Trim(), currency, cancellationToken).ConfigureAwait(false);

        // ADR-0071 D5: seed a starter category tree (opt-in, default on) so the
        // new ledger is usable immediately. Best-effort — the ledger already
        // exists; a failed seed is logged, not fatal (the user can add
        // categories by hand). Runs as service-role: the owner grant exists, but
        // this is a cross-cutting seed, not a request-scoped RLS write.
        if (request.SeedDefaultCategories)
        {
            try
            {
                await using var db = serviceDb.Create();
                await starterCategories.SeedAsync(db, created.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("Coffer.Api.Provisioning")
                    .LogWarning(ex, "Starter-category seed failed for ledger {LedgerId}.", created.Id);
            }
        }

        // 201 Created with a Location pointing at the future per-ledger
        // endpoints (transactions / accounts) PR 3.7 introduces. The
        // resource itself doesn't have a per-id URL today, but the
        // Location convention is the right shape and clients can issue
        // GET /api/ledgers and pick out the new id immediately.
        return Results.Created($"/api/ledgers/{created.Id}", created);
    }

    /// <summary>
    /// Return the ledger the user most recently switched to, or
    /// <see cref="StatusCodes.Status204NoContent"/> when none is set / the
    /// stored id is no longer visible to the user (a grant could have
    /// been revoked since they last logged in). Clearing the stale value
    /// in the latter case keeps subsequent reads stable.
    /// </summary>
    private static async Task<IResult> GetLastOpenedAsync(
        ICurrentUserAccessor currentUser,
        UsersRepository users,
        LedgersRepository ledgers,
        CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(currentUser.UserId, cancellationToken)
                              .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Authenticated user resolved to no row — schema invariant violation.");

        if (user.LastOpenedLedgerId is null)
            return Results.NoContent();

        var ledger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, user.LastOpenedLedgerId.Value, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
        {
            // The user no longer has a grant on the previously-opened
            // ledger; clear the stored value so the next call doesn't
            // pay the same lookup.
            await users.SetLastOpenedLedgerAsync(currentUser.UserId, null, cancellationToken)
                       .ConfigureAwait(false);
            return Results.NoContent();
        }

        return Results.Ok(ledger);
    }

    /// <summary>
    /// Set <c>users.last_opened_ledger_id</c>. Returns 204 on success,
    /// 404 when the supplied ledger isn't visible to the user (no grant
    /// or doesn't exist).
    /// </summary>
    private static async Task<IResult> PutLastOpenedAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        UsersRepository users,
        LedgersRepository ledgers,
        CancellationToken cancellationToken)
    {
        var ledger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        await users.SetLastOpenedLedgerAsync(currentUser.UserId, ledgerId, cancellationToken)
                   .ConfigureAwait(false);
        return Results.NoContent();
    }
}
