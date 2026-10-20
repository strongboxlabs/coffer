using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Memorized reports (ADR-0104 D3, migration 244). Same auth contract as the
/// other per-ledger surfaces: an authenticated user plus a ledger-grant check
/// before any row is read or written.
/// </summary>
/// <remarks>
/// Visibility is enforced by row-level security, not here. The policy on
/// <c>saved_reports</c> restricts reads to rows you own or the ledger shares,
/// and writes to rows you own, so these handlers can be written as if every
/// row were visible and still cannot leak one. What they add is the difference
/// between a silent no-op and a 404.
/// </remarks>
public static class SavedReportsEndpoints
{
    public static IEndpointRouteBuilder MapSavedReportsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/reports/saved")
                          .RequireAuthorization()
                          .RequireLedgerMembership();

        group.MapGet("/", ListAsync);
        group.MapGet("/{reportId:guid}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{reportId:guid}", PatchAsync);
        group.MapDelete("/{reportId:guid}", DeleteAsync);

        // Pins live beside saved reports because they are about them, and the
        // rail reads both on the same page load.
        var pins = routes.MapGroup("/api/ledgers/{ledgerId:guid}/reports/pins")
                         .RequireAuthorization()
                         .RequireLedgerMembership();
        pins.MapGet("/", ListPinsAsync);
        pins.MapPut("/saved/{reportId:guid}", PinSavedAsync);
        pins.MapDelete("/saved/{reportId:guid}", UnpinSavedAsync);
        // Separate routes rather than one polymorphic id: a catalog key is a
        // slug and a saved report is a GUID, and a single path would have to
        // guess which it was looking at.
        pins.MapPut("/catalog/{catalogKey}", PinCatalogAsync);
        pins.MapDelete("/catalog/{catalogKey}", UnpinCatalogAsync);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        SavedReportsRepository reports,
        CancellationToken cancellationToken) =>
        Results.Ok(await reports.ListAsync(ledgerId, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetAsync(
        Guid ledgerId,
        Guid reportId,
        SavedReportsRepository reports,
        CancellationToken cancellationToken)
    {
        var report = await reports.GetAsync(ledgerId, reportId, cancellationToken).ConfigureAwait(false);
        return report is null ? Results.NotFound() : Results.Ok(report);
    }

    private static async Task<IResult> CreateAsync(
        Guid ledgerId,
        CreateSavedReportRequest request,
        ICurrentUserAccessor currentUser,
        SavedReportsRepository reports,
        CancellationToken cancellationToken)
    {
        if (Validate(request.Name, request.Spec, request.Visibility) is { } problem)
            return problem;

        var created = await reports.CreateAsync(
            ledgerId,
            currentUser.UserId,
            request.Name!.Trim(),
            request.Spec!.Value.GetRawText(),
            request.Visibility ?? SavedReportVisibility.Private,
            cancellationToken).ConfigureAwait(false);

        return Results.Created($"/api/ledgers/{ledgerId}/reports/saved/{created.Id}", created);
    }

    private static async Task<IResult> PatchAsync(
        Guid ledgerId,
        Guid reportId,
        PatchSavedReportRequest request,
        ICurrentUserAccessor currentUser,
        SavedReportsRepository reports,
        CancellationToken cancellationToken)
    {
        // A patch may omit everything it does not change, so each field is
        // validated only when it is present.
        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "name must not be blank." });
        if (request.Spec is { } spec && spec.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { error = "spec must be a JSON object." });
        if (request.Visibility is not null && !SavedReportVisibility.IsValid(request.Visibility))
            return Results.BadRequest(new { error = "visibility must be 'private' or 'ledger'." });

        var updated = await reports.UpdateAsync(
            ledgerId, reportId, currentUser.UserId,
            request.Name?.Trim(),
            request.Spec?.GetRawText(),
            request.Visibility,
            cancellationToken).ConfigureAwait(false);

        // 404 and not 403 for a report someone else owns. The alternative
        // confirms that a private report with that id exists, which is the one
        // thing its owner did not share.
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> DeleteAsync(
        Guid ledgerId,
        Guid reportId,
        ICurrentUserAccessor currentUser,
        SavedReportsRepository reports,
        CancellationToken cancellationToken) =>
        await reports.DeleteAsync(ledgerId, reportId, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> ListPinsAsync(
        Guid ledgerId,
        ReportPinsRepository pins,
        CancellationToken cancellationToken) =>
        Results.Ok(await pins.ListAsync(ledgerId, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> PinSavedAsync(
        Guid ledgerId,
        Guid reportId,
        ICurrentUserAccessor currentUser,
        ReportPinsRepository pins,
        CancellationToken cancellationToken) =>
        // 404 when the report is not one this user can see — the same reason
        // the saved-report handlers prefer it to 403: a 403 would confirm that
        // a private report with that id exists.
        await pins.PinSavedAsync(ledgerId, reportId, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> UnpinSavedAsync(
        Guid ledgerId,
        Guid reportId,
        ICurrentUserAccessor currentUser,
        ReportPinsRepository pins,
        CancellationToken cancellationToken)
    {
        await pins.UnpinSavedAsync(ledgerId, reportId, currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        // Always 204. Unpinning something already unpinned is the toggle
        // arriving twice, not an error worth telling anyone about.
        return Results.NoContent();
    }

    private static async Task<IResult> PinCatalogAsync(
        Guid ledgerId,
        string catalogKey,
        ICurrentUserAccessor currentUser,
        ReportPinsRepository pins,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(catalogKey))
            return Results.BadRequest(new { error = "catalogKey is required." });

        await pins.PinCatalogAsync(ledgerId, catalogKey.Trim(), currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> UnpinCatalogAsync(
        Guid ledgerId,
        string catalogKey,
        ICurrentUserAccessor currentUser,
        ReportPinsRepository pins,
        CancellationToken cancellationToken)
    {
        await pins.UnpinCatalogAsync(ledgerId, catalogKey.Trim(), currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        return Results.NoContent();
    }

    private static IResult? Validate(string? name, JsonElement? spec, string? visibility)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = "name is required." });
        if (spec is not { } value || value.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { error = "spec must be a JSON object." });
        if (visibility is not null && !SavedReportVisibility.IsValid(visibility))
            return Results.BadRequest(new { error = "visibility must be 'private' or 'ledger'." });
        return null;
    }
}
