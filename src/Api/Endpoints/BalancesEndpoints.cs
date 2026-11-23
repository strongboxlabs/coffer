using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger consistency surface: does every derived projection (the stored running
/// balances in txn_header_account_balances among them) still agree with what it is
/// derived from, and the repair for each one that does not. Exists because a writer that
/// skips the recompute leaves a stored figure wrong, and the SPA shows it forever.
/// </summary>
/// <remarks>
/// The balances are checked and repaired as the <c>balances</c> projection, like every
/// other projection: one check for all of them, and one repair per projection.
/// </remarks>
public static class BalancesEndpoints
{
    public static IEndpointRouteBuilder MapBalancesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/balances")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        // Every derived projection. It changes no figure (mig 206 split calculation
        // from persistence, so asking the question never rewrites the answer), and
        // records its verdict as the monitor's pass does, so it is a POST. Anyone who
        // reads the ledger may run it: what it writes is derived from the check alone,
        // through the service context.
        group.MapPost("/consistency", ConsistencyAsync).AsLedgerRead();
        // The stored verdict, without running the check.
        group.MapGet("/consistency/state", ConsistencyStateAsync).AsLedgerRead();
        // The repair, which the user chooses explicitly: one per projection, uniform
        // across all of them (ConsistencyProjections.All), so a reader is never told
        // about a problem the product cannot fix. POST + write access (the
        // RequireLedgerAccess default for a POST), NOT AsLedgerRead, under which a
        // viewer's rewrite would be silently no-op'd by RLS and still report "ok".
        group.MapPost("/consistency/{projection}/repair", RepairProjectionAsync);

        return routes;
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/balances/consistency</c>. Does every
    /// derived projection still agree with what it is derived from (the transactions,
    /// or, for holdings_currency, each holdings account's brokerage, or, for
    /// reminder_auto_post, the ledger's reminders)? Heals nothing:
    /// it records its verdict, as the monitor's pass does, and changes no figure.
    /// </summary>
    /// <remarks>
    /// Balances are only one projection. Four interceptors maintain denormalised
    /// state, and a write that bypasses the ChangeTracker skips all of them — so a
    /// scrub that correctly recomputes the FIFO side can still leave the register
    /// wrong, indefinitely. This asks about all of them at once.
    /// <para>
    /// The verdict is recorded through the monitor's own path
    /// (<see cref="ConsistencyVerdicts.RecordCheckAsync"/>): the stored state, which the
    /// Overview's "checked" text reads, and the consistency notice its Problems widget
    /// lists. A check that wrote nothing would leave both saying what the monitor last
    /// found, so the Overview could read "No open problems" beside a check that had just
    /// found one. A person's check announces only a verdict the ledger does not already
    /// hold, so pressing it again sends nothing.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ConsistencyAsync(
        Guid ledgerId,
        // The caller's local date (?today=), as the register takes it: the advisory's
        // holding value is a current value, cut where the register's Scheduled begins.
        DateOnly? today,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerConsistencyRepository consistency,
        ConsistencyVerdicts verdicts,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // When the check began to read the ledger: the verdict's time.
        var checkedAt = clock.GetUtcNow().UtcDateTime;
        var report = await consistency.CheckAsync(ledgerId, today, cancellationToken)
                                      .ConfigureAwait(false);
        await verdicts.RecordCheckAsync(ledgerId, report, ConsistencyCheckOrigin.Person, checkedAt, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(report);
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/balances/consistency/state</c>. The last check's
    /// verdict as stored (mig 242). Runs nothing, so a reader can show the verdict on
    /// every load. A ledger never checked reads as no verdict (every field null),
    /// which a reader shows as "not checked yet".
    /// </summary>
    private static async Task<IResult> ConsistencyStateAsync(
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

        var state = await consistency.GetStateAsync(ledgerId, cancellationToken).ConfigureAwait(false)
            ?? new LedgerConsistencyStateDto(null, null, [], null);
        return Results.Ok(state);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/balances/consistency/{projection}/repair</c>.
    /// Rebuild one projection, touching only what a check reported, and answer what it
    /// rewrote and the whole ledger's verdict after it (<see cref="ProjectionRepairResult"/>).
    /// </summary>
    /// <remarks>
    /// Every projection the report names has a repair. Reporting a problem with no
    /// way to fix it in the product leaves the damage sitting while ad-hoc SQL is
    /// written to look at it — and hand-run SQL is how such damage arrives.
    /// <para>
    /// A repair that has begun runs to the end and is recorded, whatever happens to the
    /// client. Several rewrites commit in pieces (balances an account at a time, holdings and
    /// realized gains a position at a time, posting counts a header at a time), so a rewrite
    /// stopped partway would leave some of it committed and none of it recorded. So once the
    /// ledger is found, the rewrite, the check after it
    /// and the record run under no token of the request's, as a restore's bookkeeping does,
    /// and a client that hangs up or a proxy that times out stops none of them. A check
    /// that fails leaves the rewrite recorded with no verdict, and the answer says what was
    /// rewritten with <c>after</c> null, rather than an error that would say the repair had
    /// not happened. A rewrite that throws partway answers its error, since the repair did
    /// not happen in full; what it committed stands, so the verdict taken before it is
    /// cleared all the same (<see cref="ConsistencyVerdicts.RecordFailedRewriteAsync"/>),
    /// with <c>last_repaired_at</c> left as it was.
    /// </para>
    /// <para>
    /// Each piece of a rewrite takes the ledger's write lock (migration 275,
    /// <see cref="LedgerConsistencyRepository.RewriteAsync"/>). A piece refused because
    /// another write held it past the wait stops the rewrite: the verdict is cleared as for
    /// any rewrite that throws partway, and the answer is the 422 <c>ledger-write-busy</c>
    /// every busy refusal is, never a 500, with
    /// <see cref="LedgerWriteLock.RepairBusyDetail"/> for its detail, since the pieces
    /// committed before it stand. The Settings page keeps its report on a 4xx, shows the
    /// detail, and reads only a 500 as a repair that failed partway.
    /// </para>
    /// <para>
    /// One repair of a ledger at a time (<see cref="ConsistencyRepairLock"/>): a repair sent
    /// while another repair of the ledger runs, of any projection, is refused with
    /// <c>consistency-repair-in-progress</c> before it rewrites anything: each piece of a
    /// rewrite takes the ledger's write lock, so a second would take turns with the first and
    /// rewrite what the first had just rewritten.
    /// </para>
    /// </remarks>
    private static async Task<IResult> RepairProjectionAsync(
        Guid ledgerId,
        string projection,
        // The caller's local date (?today=), for the check after the repair, as the
        // check itself takes it.
        DateOnly? today,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerConsistencyRepository consistency,
        ConsistencyVerdicts verdicts,
        ConsistencyRepairLock repairs,
        UserPreferencesRepository prefs,
        Coffer.Api.Quotes.Yahoo.YahooFinanceQuoteProvider yahoo,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
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

        // The quote_currency repair asks Yahoo for each security's currency (ADR-0114 D5,
        // amendment): egress, so only under the repairing user's own Yahoo opt-in
        // (ADR-0057), refused before anything is rewritten otherwise.
        Coffer.Api.Quotes.IQuoteCurrencyLookup? currencyLookup = null;
        if (projection == ConsistencyProjections.QuoteCurrency)
        {
            var quotes = await prefs.GetQuotesAsync(currentUser.UserId, ledgerId, cancellationToken)
                .ConfigureAwait(false);
            if (!quotes.EnabledProviders.Contains(yahoo.ProviderKey, StringComparer.Ordinal))
                return BusinessError.Problem(BusinessError.Codes.ConsistencyRepairNeedsYahoo,
                    "This repair asks Yahoo Finance for each security's currency. Turn on Yahoo Finance "
                    + "in Settings → Quotes, then repair again. Nothing was changed.");
            currencyLookup = yahoo;
        }

        // Taken once the ledger is known to be the caller's, so the refusal says nothing
        // of a ledger they cannot see, and held until the repair has answered, its record
        // included, however it ends.
        using var running = repairs.TryAcquire(ledgerId);
        if (running is null)
            return BusinessError.Problem(BusinessError.Codes.ConsistencyRepairInProgress,
                "A repair of this ledger is already running. Wait for it to finish before starting another.");

        var log = loggerFactory.CreateLogger("Coffer.Api.Consistency.Repair");

        // From here on, no token of the request's: the rewrite commits in pieces, and
        // what it has committed is recorded below.
        ProjectionConsistency repaired;
        IReadOnlyList<ConsistencyMismatch> notRepaired;
        try
        {
            (repaired, notRepaired) = await consistency.RewriteAsync(
                    ledgerId, projection, currentUser.UserId, currencyLookup, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A rewrite that throws partway (a database error, say) leaves the pieces it
            // committed standing, so the verdict taken before it may no longer describe
            // the data: it is cleared as of now, after the last piece committed, as for a
            // repair whose check did not finish, and the ledger is due on the monitor's
            // next tick. No repair completed, so last_repaired_at stays and nothing is
            // announced. The error is still the answer.
            var stoppedAt = clock.GetUtcNow().UtcDateTime;
            var busy = LedgerWriteLock.IsBusy(ex);
            if (busy)
                log.LogWarning(ex,
                    "Repairing {Projection} on ledger {LedgerId} stopped: another write held the ledger's " +
                    "write lock past the wait. What it committed stands, and the verdict taken before it " +
                    "is cleared.",
                    projection, ledgerId);
            else
                log.LogError(ex,
                    "Repairing {Projection} on ledger {LedgerId} failed partway; what it committed stands, " +
                    "and the verdict taken before it is cleared.",
                    projection, ledgerId);
            try
            {
                await verdicts.RecordFailedRewriteAsync(ledgerId, stoppedAt, CancellationToken.None)
                              .ConfigureAwait(false);
            }
            catch (Exception clearing)
            {
                log.LogError(clearing,
                    "The verdict on ledger {LedgerId} could not be cleared after a failed repair of " +
                    "{Projection}; it stays until the next scheduled check.",
                    ledgerId, projection);
            }
            // The busy refusal is the 422 ledger-write-busy every write answers, in words of
            // its own: the pieces committed before it stand, so "Nothing was changed" would
            // be false.
            if (busy)
                return BusinessError.Problem(BusinessError.Codes.LedgerWriteBusy, LedgerWriteLock.RepairBusyDetail);
            throw;
        }

        // Committed.
        var repairedAt = clock.GetUtcNow().UtcDateTime;

        // The whole ledger checked again, for the verdict the repair records.
        LedgerConsistencyReport? after = null;
        try
        {
            after = await consistency.CheckAsync(ledgerId, today, CancellationToken.None)
                                     .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Repaired {Projection} on ledger {LedgerId}, but the check after the repair failed; " +
                "the repair is recorded with no verdict.",
                projection, ledgerId);
        }
        var repair = new ProjectionRepairResult(projection, repaired, after) { NotRepaired = notRepaired };

        // A repair leaves no trace in the data it repairs, so the record of it is the
        // ledger's stored state and one consistency.repaired event (FU-012): the
        // verdict of the full check the repair ran once it had committed, when, and
        // that a repair happened. The event stays outstanding while that check still
        // reports anything, or did not finish, and clears the ledger's consistency
        // problem only when it reports nothing (ConsistencyVerdicts.RecordRepairAsync).
        //
        // NOT throttled, unlike a person's repeated check: this is a deliberate act
        // that changed the data, and collapsing two repairs into one line would lose
        // exactly the history this exists to keep.
        //
        // A record that cannot be written is logged, not returned: the repair HAS
        // committed, and an error would say it had not.
        try
        {
            await verdicts.RecordRepairAsync(ledgerId, repair, repairedAt, CancellationToken.None)
                          .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Repaired {Projection} on ledger {LedgerId} (rewrote {Rewritten}), but the repair " +
                "could not be recorded.",
                projection, ledgerId, repaired.MismatchedCount);
        }

        return Results.Ok(repair);
    }
}
