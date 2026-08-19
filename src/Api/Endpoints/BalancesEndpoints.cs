using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger balance diagnostic surface. Exists because the
/// stored-running-balance scheme (txn_header_account_balances,
/// written by <c>fn_recompute_balances_for_account</c> via the
/// interceptor) is non-trivial to audit by eye — if ANY writer
/// of a balance-relevant field skips the interceptor, the
/// stored value drifts from the canonical recompute and the
/// SPA register shows a wrong number forever. This endpoint
/// is the explicit verify-and-heal lever for that class of bug.
/// </summary>
public static class BalancesEndpoints
{
    public static IEndpointRouteBuilder MapBalancesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/balances")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        // Verify + heal — snapshot all balance rows, run the
        // canonical recompute for every account in the ledger,
        // diff. The recompute is the heal step; the diff is
        // the report. It MUTATES (rewrites txn_header_account_balances),
        // so it requires write access (the RequireLedgerAccess default for a
        // POST) — NOT AsLedgerRead, under which a viewer's heal would be
        // silently no-op'd by RLS and still report "ok".
        // READ-ONLY check (mig 206). A GET because it is a question, and because
        // a verb that mutates on GET would be the same trap in reverse.
        group.MapGet("/health", CheckAsync).AsLedgerRead();
        // The REPAIR, which the user chooses explicitly. Kept POST + write
        // access: under AsLedgerRead a viewer's heal would be silently no-op'd
        // by RLS and still report "ok".
        group.MapPost("/repair", RepairAsync);
        // The whole-ledger version: every derived projection, not just balances.
        // Also read-only.
        group.MapGet("/consistency", ConsistencyAsync).AsLedgerRead();
        // Repair for the one projection the report can rebuild targeted rather than
        // ledger-wide. POST + write access, like the balance repair.
        // One repair per projection, uniform across all four: a reader is never told
        // about a problem the product cannot fix.
        group.MapPost("/consistency/{projection}/repair", RepairProjectionAsync);

        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/balances/health</c>. Report which stored
    /// running balances disagree with the pure walk. Writes NOTHING.
    /// </summary>
    /// <remarks>
    /// This used to be a POST that healed as a side effect of checking, because
    /// the only implementation of the rules was inside the recompute's
    /// DELETE + INSERT — asking the question rewrote the answer. On one ledger
    /// that meant a diagnostic silently rewriting 2,741 rows. Migration 206
    /// split calculation from persistence, so the check is now a genuine read
    /// and repairing is a separate, deliberate act (<see cref="RepairAsync"/>).
    /// </remarks>
    private static async Task<IResult> CheckAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RegisterRepository register,
        CancellationToken cancellationToken)
    {
        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var report = await register.CheckBalancesAsync(
            ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(report);
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/balances/consistency</c>. Does every
    /// derived projection still agree with the transactions? Writes nothing.
    /// </summary>
    /// <remarks>
    /// Balances were only the projection someone happened to check. Four
    /// interceptors maintain denormalised state, and a write that bypasses the
    /// ChangeTracker skips all of them — so a scrub that correctly recomputed the
    /// FIFO side still left the register wrong for months. This asks about all of
    /// them at once, which is the question nobody could ask before.
    /// </remarks>
    private static async Task<IResult> ConsistencyAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerConsistencyRepository consistency,
        CancellationToken cancellationToken)
    {
        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var report = await consistency.CheckAsync(ledgerId, cancellationToken)
                                      .ConfigureAwait(false);
        return Results.Ok(report);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/balances/consistency/{projection}/repair</c>.
    /// Rebuild one projection, touching only what a check reported.
    /// </summary>
    /// <remarks>
    /// Every projection the report names has a repair. Reporting a problem with no
    /// way to fix it in the product is what left a scrub's damage sitting for months
    /// while ad-hoc SQL was written to look at it — and hand-run SQL is how the
    /// damage arrived.
    /// </remarks>
    private static async Task<IResult> RepairProjectionAsync(
        Guid ledgerId,
        string projection,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerConsistencyRepository consistency,
        CancellationToken cancellationToken)
    {
        if (!ConsistencyProjections.IsKnown(projection))
            return BusinessError.Problem(BusinessError.Codes.ConsistencyProjectionUnknown,
                "Unknown projection '" + projection + "'. Expected one of: "
                + string.Join(", ", ConsistencyProjections.All) + ".");

        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var repaired = await consistency.RepairAsync(ledgerId, projection, cancellationToken)
                                        .ConfigureAwait(false);
        return Results.Ok(repaired);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/balances/repair</c>. Rebuild every stored
    /// running balance from the legs, returning what changed.
    /// </summary>
    /// <remarks>
    /// The deliberate counterpart to <see cref="CheckAsync"/>. Drift means some
    /// writer mutated legs without invoking the recompute — a raw-SQL fix, a
    /// Dapper path, a hand-run scrub — so this is the remedy for that, not
    /// something to run speculatively.
    /// </remarks>
    private static async Task<IResult> RepairAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RegisterRepository register,
        CancellationToken cancellationToken)
    {
        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var report = await register.VerifyAndHealBalancesAsync(
            ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(report);
    }
}
