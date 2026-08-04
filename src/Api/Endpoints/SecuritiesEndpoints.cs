using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger Securities catalog endpoints (slice A3). Same auth
/// shape as the other per-ledger endpoint groups: require an
/// authenticated user, check the user's grant on the ledger
/// (422 <c>ledger-not-visible</c> otherwise), then delegate to the
/// repo. Phase D RLS provides defence-in-depth at the DB layer.
/// </summary>
public static class SecuritiesEndpoints
{
    public static IEndpointRouteBuilder MapSecuritiesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/securities")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{securityId:guid}", GetAsync);
        group.MapPatch("/{securityId:guid}", PatchAsync);
        group.MapGet("/{securityId:guid}/transactions", ListTransactionsAsync);

        // Prices CRUD (slice A3 follow-on). Nested under the security
        // since prices are owned by it; the URL shape mirrors the
        // transactions sub-resource.
        group.MapGet("/{securityId:guid}/prices", ListPricesAsync);
        group.MapPost("/{securityId:guid}/prices", AddPriceAsync);
        group.MapPatch("/{securityId:guid}/prices/{priceId:guid}", PatchPriceAsync);
        group.MapDelete("/{securityId:guid}/prices/{priceId:guid}", DeletePriceAsync);

        // Look-through components (ADR-0067): multi-asset sleeve weights.
        group.MapGet("/{securityId:guid}/components", GetComponentsAsync);
        group.MapPut("/{securityId:guid}/components", ReplaceComponentsAsync);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        string? q,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SecuritiesRepository securities,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await securities.ListByLedgerAsync(ledgerId, q, cancellationToken)
                                   .ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetAsync(
        Guid ledgerId,
        Guid securityId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SecuritiesRepository securities,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var detail = await securities.GetByIdAsync(ledgerId, securityId, cancellationToken)
                                     .ConfigureAwait(false);
        if (detail is null)
            return BusinessError.Problem(BusinessError.Codes.SecurityNotInLedger,
                "Security does not belong to this ledger.");

        return Results.Ok(detail);
    }

    private static async Task<IResult> CreateAsync(
        Guid ledgerId,
        CreateSecurityRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SecuritiesRepository securities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await securities.CreateAsync(ledgerId, request, cancellationToken)
                                      .ConfigureAwait(false);
        return outcome.Kind switch
        {
            SecuritiesRepository.CreateResult.Ok =>
                Results.Created(
                    $"/api/ledgers/{ledgerId}/securities/{outcome.SecurityId}",
                    new { securityId = outcome.SecurityId }),
            SecuritiesRepository.CreateResult.NameRequired =>
                BusinessError.Problem(BusinessError.Codes.SecurityNameRequired,
                    "Security name is required."),
            SecuritiesRepository.CreateResult.AssetClassInvalid =>
                BusinessError.Problem(BusinessError.Codes.SecurityAssetClassInvalid,
                    "Asset class must be one of equity, fixed_income, multi_asset, cash, real_assets, alternative."),
            SecuritiesRepository.CreateResult.DuplicateTicker =>
                BusinessError.Problem(BusinessError.Codes.SecurityDuplicateTicker,
                    "A security with this ticker already exists in this ledger."),
            SecuritiesRepository.CreateResult.DuplicateCusip =>
                BusinessError.Problem(BusinessError.Codes.SecurityDuplicateCusip,
                    "A security with this CUSIP already exists in this ledger."),
            SecuritiesRepository.CreateResult.NotPublicNeedsSymbol =>
                BusinessError.Problem(BusinessError.Codes.SecurityQuoteSymbolRequired,
                    "Enter a quote symbol before marking it non-public — a bare ticker is always public."),
            _ => Results.Problem("Unknown create-security result.", statusCode: 500),
        };
    }

    private static async Task<IResult> PatchAsync(
        Guid ledgerId,
        Guid securityId,
        PatchSecurityRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SecuritiesRepository securities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await securities.PatchAsync(ledgerId, securityId, request, cancellationToken)
                                      .ConfigureAwait(false);
        return outcome switch
        {
            SecuritiesRepository.PatchResult.Ok => Results.NoContent(),
            SecuritiesRepository.PatchResult.NotInLedger =>
                BusinessError.Problem(BusinessError.Codes.SecurityNotInLedger,
                    "Security does not belong to this ledger."),
            SecuritiesRepository.PatchResult.NameRequired =>
                BusinessError.Problem(BusinessError.Codes.SecurityNameRequired,
                    "Security name cannot be empty."),
            SecuritiesRepository.PatchResult.AssetClassInvalid =>
                BusinessError.Problem(BusinessError.Codes.SecurityAssetClassInvalid,
                    "Asset class must be one of equity, fixed_income, multi_asset, cash, real_assets, alternative."),
            SecuritiesRepository.PatchResult.DuplicateTicker =>
                BusinessError.Problem(BusinessError.Codes.SecurityDuplicateTicker,
                    "A security with this ticker already exists in this ledger."),
            SecuritiesRepository.PatchResult.DuplicateCusip =>
                BusinessError.Problem(BusinessError.Codes.SecurityDuplicateCusip,
                    "A security with this CUSIP already exists in this ledger."),
            SecuritiesRepository.PatchResult.NotPublicNeedsSymbol =>
                BusinessError.Problem(BusinessError.Codes.SecurityQuoteSymbolRequired,
                    "Enter a quote symbol before marking it non-public — a bare ticker is always public."),
            _ => Results.Problem("Unknown patch-security result.", statusCode: 500),
        };
    }

    private static async Task<IResult> GetComponentsAsync(
        Guid ledgerId, Guid securityId,
        ICurrentUserAccessor currentUser, LedgersRepository ledgers,
        SecuritiesRepository securities, CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible, "Ledger not found or not visible to this user.");

        var components = await securities.GetComponentsAsync(ledgerId, securityId, cancellationToken).ConfigureAwait(false);
        return components is null
            ? BusinessError.Problem(BusinessError.Codes.SecurityNotInLedger, "Security does not belong to this ledger.")
            : Results.Ok(components);
    }

    private static async Task<IResult> ReplaceComponentsAsync(
        Guid ledgerId, Guid securityId, ReplaceSecurityComponentsRequest request,
        ICurrentUserAccessor currentUser, LedgersRepository ledgers,
        SecuritiesRepository securities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var visible = await ledgers.GetVisibleByIdAsync(currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible, "Ledger not found or not visible to this user.");

        var outcome = await securities.ReplaceComponentsAsync(ledgerId, securityId, request.Components, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            SecuritiesRepository.ComponentsResult.Ok => Results.NoContent(),
            SecuritiesRepository.ComponentsResult.NotInLedger =>
                BusinessError.Problem(BusinessError.Codes.SecurityNotInLedger, "Security does not belong to this ledger."),
            SecuritiesRepository.ComponentsResult.Invalid =>
                BusinessError.Problem(BusinessError.Codes.SecurityComponentsInvalid,
                    "Each component needs a valid asset class, optional region, and a non-negative weight."),
            _ => Results.Problem("Unknown replace-components result.", statusCode: 500),
        };
    }

    private static async Task<IResult> ListTransactionsAsync(
        Guid ledgerId,
        Guid securityId,
        string? cursor,
        int? limit,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SecuritiesRepository securities,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Confirm the security exists in this ledger BEFORE listing —
        // otherwise a cross-ledger securityId would silently return an
        // empty page (subtly leaks "this id doesn't apply here" via the
        // 200/empty distinction). The 422 is explicit and matches GET.
        var detail = await securities.GetByIdAsync(ledgerId, securityId, cancellationToken)
                                     .ConfigureAwait(false);
        if (detail is null)
            return BusinessError.Problem(BusinessError.Codes.SecurityNotInLedger,
                "Security does not belong to this ledger.");

        var page = await securities.ListTransactionsAsync(
            ledgerId, securityId,
            SecuritiesRepository.TxnCursor.TryParse(cursor),
            limit ?? 25, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(page);
    }

    private static async Task<IResult> ListPricesAsync(
        Guid ledgerId,
        Guid securityId,
        string? cursor,
        int? limit,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SecuritiesRepository securities,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var detail = await securities.GetByIdAsync(ledgerId, securityId, cancellationToken)
                                     .ConfigureAwait(false);
        if (detail is null)
            return BusinessError.Problem(BusinessError.Codes.SecurityNotInLedger,
                "Security does not belong to this ledger.");

        var page = await securities.ListPricesAsync(
            ledgerId, securityId, ParseCursor(cursor), limit ?? 50, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(page);
    }

    private static async Task<IResult> AddPriceAsync(
        Guid ledgerId,
        Guid securityId,
        CreateSecurityPriceRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SecuritiesRepository securities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await securities.AddPriceAsync(
            ledgerId, securityId, request, cancellationToken).ConfigureAwait(false);
        return outcome.Kind switch
        {
            SecuritiesRepository.AddPriceResult.Ok =>
                Results.Created(
                    $"/api/ledgers/{ledgerId}/securities/{securityId}/prices/{outcome.PriceId}",
                    new { priceId = outcome.PriceId }),
            SecuritiesRepository.AddPriceResult.SecurityNotInLedger =>
                BusinessError.Problem(BusinessError.Codes.SecurityNotInLedger,
                    "Security does not belong to this ledger."),
            SecuritiesRepository.AddPriceResult.PriceRequired =>
                BusinessError.Problem(BusinessError.Codes.SecurityPriceRequired,
                    "Price must be a positive number."),
            SecuritiesRepository.AddPriceResult.PriceDateRequired =>
                BusinessError.Problem(BusinessError.Codes.SecurityPriceDateRequired,
                    "Price date is required."),
            SecuritiesRepository.AddPriceResult.HighLowInvalid =>
                BusinessError.Problem(BusinessError.Codes.SecurityPriceHighLowInvalid,
                    "High must be greater than or equal to Low."),
            _ => Results.Problem("Unknown add-price result.", statusCode: 500),
        };
    }

    private static async Task<IResult> PatchPriceAsync(
        Guid ledgerId,
        Guid securityId,
        Guid priceId,
        PatchSecurityPriceRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SecuritiesRepository securities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await securities.PatchPriceAsync(
            ledgerId, securityId, priceId, request, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            SecuritiesRepository.PatchPriceResult.Ok => Results.NoContent(),
            SecuritiesRepository.PatchPriceResult.PriceNotInSecurity =>
                BusinessError.Problem(BusinessError.Codes.SecurityPriceNotInSecurity,
                    "Price does not belong to this security in this ledger."),
            SecuritiesRepository.PatchPriceResult.PriceRequired =>
                BusinessError.Problem(BusinessError.Codes.SecurityPriceRequired,
                    "Price must be a positive number."),
            SecuritiesRepository.PatchPriceResult.DateConflict =>
                BusinessError.Problem(BusinessError.Codes.SecurityPriceDateConflict,
                    "A price for this security on this date already exists."),
            SecuritiesRepository.PatchPriceResult.HighLowInvalid =>
                BusinessError.Problem(BusinessError.Codes.SecurityPriceHighLowInvalid,
                    "High must be greater than or equal to Low."),
            _ => Results.Problem("Unknown patch-price result.", statusCode: 500),
        };
    }

    private static async Task<IResult> DeletePriceAsync(
        Guid ledgerId,
        Guid securityId,
        Guid priceId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SecuritiesRepository securities,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await securities.DeletePriceAsync(
            ledgerId, securityId, priceId, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            SecuritiesRepository.DeletePriceResult.Ok => Results.NoContent(),
            SecuritiesRepository.DeletePriceResult.NotInSecurity =>
                BusinessError.Problem(BusinessError.Codes.SecurityPriceNotInSecurity,
                    "Price does not belong to this security in this ledger."),
            _ => Results.Problem("Unknown delete-price result.", statusCode: 500),
        };
    }

    private static DateOnly? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        // Cursor is the prior page's last AsOf as a bare date (DateOnly "O" =
        // "yyyy-MM-dd"); ADR-0070 made price_date a calendar DATE.
        return DateOnly.TryParse(cursor,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed) ? parsed : null;
    }
}
