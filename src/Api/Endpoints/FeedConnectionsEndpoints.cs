using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Crypto;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Ingest;
using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger feed-connection endpoints (Phase 5 slice 1 — SimpleFIN
/// setup-token exchange + persist). Same authorisation contract as
/// every other per-ledger endpoint: authenticated user + ledger-
/// grant check (422 <c>ledger-not-visible</c> otherwise).
/// </summary>
public static class FeedConnectionsEndpoints
{
    public static IEndpointRouteBuilder MapFeedConnectionsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/feed-connections")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapDelete("/{connectionId:guid}", DeleteAsync);
        group.MapPost("/{connectionId:guid}/sync", SyncAsync);
        // Slice 2c.4: per-connection bank-side account directory
        // — backs the unified accounts panel (mapped + unmapped).
        group.MapGet("/{connectionId:guid}/accounts", ListConnectionAccountsAsync);
        // Slice 2c.3: ledger-wide "Sync all" — fires the sync
        // sequentially for every connection on the ledger and
        // returns one aggregate.
        routes.MapPost("/api/ledgers/{ledgerId:guid}/sync-all", SyncAllAsync)
              .RequireAuthorization()
              .RequireLedgerAccess();

        return routes;
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/sync-all</c> — slice 2c.3.
    /// Walks every connection on the ledger and dispatches a
    /// per-connection sync. Sequential, not parallel: hitting
    /// multiple SimpleFIN endpoints simultaneously risks rate-limit
    /// pressure on the bank side, and per-connection failures
    /// shouldn't cascade — a 403 on one bank just records
    /// needs_reauth for that connection and the loop continues.
    /// </summary>
    private static async Task<IResult> SyncAllAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        FeedConnectionsRepository connections,
        IngestOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await connections.ListByLedgerAsync(ledgerId, cancellationToken)
                                    .ConfigureAwait(false);

        var entries = new List<SyncAllConnectionEntry>(rows.Count);
        var hadAnyFailure = false;
        foreach (var conn in rows)
        {
            var outcome = await orchestrator.RunPullAsync(
                ledgerId, conn.Id, currentUser.UserId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (outcome.IsSuccess)
            {
                var dto = SyncEndpointMapping.ToDto(outcome.Result!);
                entries.Add(new SyncAllConnectionEntry(conn.Id, dto, FailureCode: null));
                // A "needs_reauth" or partial outcome is still a
                // successful sync (we wrote a sync_runs row), but
                // it's user-visible failure for surfacing the
                // banner. errors[] non-empty likewise.
                if (dto.ConnectionStatus is "needs_reauth" or "error" || dto.Errors.Count > 0)
                    hadAnyFailure = true;
            }
            else
            {
                entries.Add(new SyncAllConnectionEntry(
                    conn.Id,
                    Result: null,
                    FailureCode: SyncEndpointMapping.ToFailureCode(outcome.Failure!.Value)));
                hadAnyFailure = true;
            }
        }

        return Results.Ok(new SyncAllResultDto(entries, hadAnyFailure));
    }

    private static async Task<IResult> SyncAsync(
        Guid ledgerId,
        Guid connectionId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        IngestOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await orchestrator.RunPullAsync(
            ledgerId, connectionId, currentUser.UserId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return SyncEndpointMapping.ToResult(outcome);
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/feed-connections/{connectionId}/accounts</c>
    /// — slice 2c.4 unified accounts list. Returns one
    /// <see cref="FeedConnectionAccountDto"/> per SimpleFIN account
    /// the bank has surfaced, joined to the current Coffer binding
    /// (null when unmapped). Independent of any recent sync —
    /// reads the persisted directory from
    /// <c>feed_connection_accounts</c>.
    /// </summary>
    private static async Task<IResult> ListConnectionAccountsAsync(
        Guid ledgerId,
        Guid connectionId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        FeedConnectionsRepository feedConnections,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (!await feedConnections.BelongsToLedgerAsync(ledgerId, connectionId, cancellationToken)
                                  .ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.FeedConnectionNotFound,
                "Feed connection not found in this ledger.");

        var rows = await feedConnections.ListConnectionAccountsAsync(
            connectionId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        FeedConnectionsRepository feedConnections,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await feedConnections.ListByLedgerAsync(ledgerId, cancellationToken)
                                        .ConfigureAwait(false);
        var summaries = rows
            .Select(r => new FeedConnectionSummary(
                r.Id, r.LedgerId, r.Provider, r.InstitutionName,
                r.Status, r.LastSyncedAt, r.CreatedAt))
            .ToList();
        return Results.Ok(summaries);
    }

    private static async Task<IResult> DeleteAsync(
        Guid ledgerId,
        Guid connectionId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        FeedConnectionsRepository feedConnections,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var deleted = await feedConnections.DeleteAsync(ledgerId, connectionId, cancellationToken)
                                           .ConfigureAwait(false);
        return deleted > 0
            ? Results.NoContent()
            : BusinessError.Problem(BusinessError.Codes.FeedConnectionNotFound,
                "Feed connection not found in this ledger.");
    }

    private static async Task<IResult> CreateAsync(
        Guid ledgerId,
        CreateFeedConnectionRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        FeedConnectionsRepository feedConnections,
        LedgerKeyService ledgerKeys,
        SimpleFinClient simpleFin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SetupToken))
            return BusinessError.Problem(BusinessError.Codes.FeedConnectionSetupTokenRequired,
                "setupToken is required.");

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Pull the wrapped LEK off the ledger row. Phase-5 ledgers
        // always have one (created in the same transaction as the
        // ledger per ADR-0026); pre-035 ledgers carry NULL and get
        // a fresh LEK generated transparently on first secret-write.
        // The ledger-grant check above is the auth gate; the
        // backfill itself is mechanical.
        var wrappedLek = await ledgers.EnsureWrappedLekAsync(
            ledgerId, ledgerKeys, cancellationToken).ConfigureAwait(false);
        if (wrappedLek is null)
            // Shouldn't happen — the GetVisibleByIdAsync above
            // already proved the ledger exists. Defensive: surface
            // as a clear 422 rather than a 500 if a race
            // (deletion) snuck in between the two queries.
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Exchange the setup token. SimpleFinException → 422 with a
        // user-facing message so the SPA can show "token expired,
        // generate a fresh one." Anything else propagates to the
        // generic 500 handler.
        string accessUrl;
        try
        {
            accessUrl = await simpleFin.ExchangeSetupTokenAsync(
                request.SetupToken, cancellationToken).ConfigureAwait(false);
        }
        catch (SimpleFinException ex)
        {
            return BusinessError.Problem(BusinessError.Codes.FeedConnectionSetupTokenInvalid,
                ex.Message);
        }

        // Best-effort institution-name probe. Never fatal — the
        // wizard renders "SimpleFIN" as a fallback. Slice 2's first
        // sync will populate the name reliably from
        // <c>org.name</c> on each account.
        var institutionName = await simpleFin.GetInstitutionNameAsync(
            accessUrl, cancellationToken).ConfigureAwait(false);

        var sealedBytes = ledgerKeys.Seal(
            wrappedLek, Encoding.UTF8.GetBytes(accessUrl));

        var row = await feedConnections.CreateSimpleFinAsync(
            ledgerId, currentUser.UserId, sealedBytes, institutionName,
            cancellationToken).ConfigureAwait(false);

        return Results.Created(
            $"/api/ledgers/{ledgerId}/feed-connections/{row.Id}",
            new FeedConnectionSummary(
                row.Id, row.LedgerId, row.Provider, row.InstitutionName,
                row.Status, row.LastSyncedAt, row.CreatedAt));
    }
}
