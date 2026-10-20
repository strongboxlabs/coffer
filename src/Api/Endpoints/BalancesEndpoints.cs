using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Microsoft.EntityFrameworkCore;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
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
    /// that meant a diagnostic silently rewriting thousands of rows. Migration 206
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
        NotificationPublisher publisher,
        ServiceDbContextFactory serviceFactory,
        TimeProvider clock,
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

        // A repair leaves no trace in the data it repairs — it makes the stored
        // rows agree with the transactions, which is indistinguishable from their
        // having always agreed. So the only record that it happened is this one.
        //
        // That matters because the check is advisory and per-ledger: an operator
        // with several ledgers otherwise cannot tell one that was repaired from
        // one nobody has opened, and a report expected to be unhealthy on some
        // unremembered subset trains people to ignore it (FU-012).
        //
        // NOT throttled, unlike the monitor's announcements: this is a deliberate
        // human action, and collapsing two repairs a day into one line would lose
        // exactly the history this exists to keep.
        // Stamp the repair on the ledger's consistency state (FU-012, mig 242).
        //
        // Through the SERVICE context: the table has a read policy for the app
        // role and no write policy, so RLS refuses an app-role write outright
        // rather than relying on nobody trying.
        //
        // Only LastRepairedAt, deliberately. The verdict belongs to whatever last
        // ran a FULL check, and this repaired ONE projection — writing "healthy"
        // here off a single-projection result would claim more than was checked,
        // which is the failure the whole entry is about. The monitor's next pass
        // settles the verdict.
        //
        // Nor is it cleared later: that a ledger was once repaired is history, and
        // a green check does not retract it.
        await using (var svc = serviceFactory.Create())
        {
            var state = await svc.LedgerConsistencyStates
                .FirstOrDefaultAsync(r => r.LedgerId == ledgerId, cancellationToken)
                .ConfigureAwait(false);
            if (state is null)
            {
                // No automatic check has run yet, so the repair is the only thing
                // there is to remember. Verdict stays NULL — "unknown" is a state
                // the column can express, and it has to be: false would render as
                // UNHEALTHY on a ledger nothing has examined, and a backdated
                // timestamp would be a magic value meaning the same thing badly.
                //
                // A null LastCheckedAt is also what makes the monitor treat this
                // ledger as due, rather than skipping it for twelve hours on the
                // strength of a row this endpoint wrote.
                svc.LedgerConsistencyStates.Add(new LedgerConsistencyStateRow
                {
                    LedgerId = ledgerId,
                    LastCheckedAt = null,
                    Healthy = null,
                    FailingProjections = [],
                    LastRepairedAt = clock.GetUtcNow().UtcDateTime,
                });
            }
            else
            {
                var repairedAt = clock.GetUtcNow().UtcDateTime;
                state.LastRepairedAt = repairedAt;
                state.UpdatedAt = repairedAt;
            }

            await svc.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await publisher.PublishLedgerAsync(ledgerId, new NotificationEvent(
            Severity: NotificationSeverity.Info,
            Topic: NotificationTopics.Consistency,
            EventKey: "consistency.repaired",
            Summary: repaired.Mismatches.Count == 0
                ? $"Repaired {projection}: nothing needed changing."
                : $"Repaired {projection}: {repaired.Mismatches.Count} row(s) rewritten.",
            Detail: new Dictionary<string, string>
            {
                ["projection"] = projection,
                ["remaining"] = repaired.Mismatches.Count.ToString(CultureInfo.InvariantCulture),
                ["healthy"] = repaired.Healthy ? "true" : "false",
            },
            Monitor: NotificationMonitors.Consistency,
            Signal: MonitorSignal.None), cancellationToken).ConfigureAwait(false);

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
