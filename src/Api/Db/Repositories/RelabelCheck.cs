using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Endpoints;
using Coffer.Api.Errors;
using Coffer.Domain.Money;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The one judgement of a relabel (ADR-0114 D13, D15): a move of a transaction into an
/// account in another currency (ADR-0072 D3), or a change of an account's currency, its
/// Holdings sibling's with it (ADR-0050 D8, ADR-0019 Rule 4). Every stored figure is kept
/// and reads in the new currency. Shared by the move and the account edit, so the two
/// cannot drift apart.
/// </summary>
/// <remarks>
/// <para><b>What is refused.</b> A pair (a posting of two legs, both on real accounts)
/// that carried two amounts
/// across currencies and would land in one currency with amounts that are not each
/// other's negation, which one currency cannot represent
/// (<see cref="BusinessError.Codes.RelabelPairNotRepresentable"/>, its count the pairs).
/// And a stored figure that carries more places than the currency it would read in allows
/// (<see cref="BusinessError.Codes.RelabelPlacesExceedCurrency"/>), counted as
/// transactions: one with a relabelled leg that does not fit, or whose category line
/// paired with that leg does not, counts once, so a category's mirror is not counted
/// twice. Account-level figures (the opening balance and the loan terms) are named by the
/// caller, which alone reads them.</para>
/// <para><b>What is reported.</b> A mirrored pair (each amount the other negated, a zero
/// pair included) that was in one currency and lands across two now carries two amounts,
/// equal in magnitude and opposite in sign; the verdict counts those pairs. A mirrored
/// zero pair landing across currencies is representable and allowed (ADR-0114 D13).</para>
/// <para><b>What is left alone.</b> A category's leg beside a real line mirrors the real
/// side and takes its currency (ADR-0114 D13), so it is never a relabelled leg, and it is
/// judged only for places, at the currency of the real leg it is paired with. A posting
/// between categories has no real side and reads in the category's own code (ADR-0114
/// D15), so a relabel of that category judges its line for places, and only for places.
/// A pair whose two sides stay in one currency, or stay across two, keeps its shape.
/// Every stored leg counts toward the places, a reminder's template and a hidden row
/// included: each is a stored figure that the relabel would read in the new currency, and
/// each can be opened and edited to fit. A transaction merged into another is the one
/// exception (decided by the owner, 2026-11-17; ADR-0114 D15): no screen shows or opens
/// it, so its figure is judged for no places, and counting it would refuse a change over
/// a row no screen reaches. Deleting its winner un-merges it (<c>is_merged_into</c> ON
/// DELETE SET NULL); a figure it holds that does not fit is then listed by the
/// consistency check's advisory like any other row, and an edit fixes it. Its pairs are
/// still judged for their shape, as every stored leg's are: nothing detects a pair in one
/// currency whose amounts differ, so the refusal holds, and it names the transaction the
/// merged row was merged into (decided by the owner, 2026-11-18). That transaction is
/// followed to the end of a chain of merges, to the row a register shows. The refusal
/// says that deleting brings the merged row back only when every refused merged-away row
/// was merged straight into a transaction a register shows, and each such transaction's
/// delete removes it outright (<see cref="TxnHeaderRow.IsDeletedOutright"/>); otherwise
/// it says nothing about a way back (settled by Claude, 2026-11-18).</para>
/// <para>No write reaches a merged-away row either: every write by its id, and the read an
/// editor loads it from, answers as for a transaction that does not exist (decided by the
/// owner, 2026-11-18; <see cref="Coffer.Api.Db.MergedAway"/>).</para>
/// <para>It reads the stored legs, so it runs under the ledger write lock the write took
/// first and before the write changes any leg or currency.</para>
/// </remarks>
public static class RelabelCheck
{
    /// <summary>One account whose legs a relabel would read in another currency: a moved
    /// transaction's source account, or the edited account and its Holdings
    /// sibling.</summary>
    internal readonly record struct Relabel(Guid AccountId, string From, string To);

    /// <summary>What a relabel would do, or why it is refused.</summary>
    /// <param name="CurrencyCode">The currency the figures would read in: the move's target's,
    /// or the edit's new code.</param>
    /// <param name="NotRepresentable">Pairs that would land in one currency with amounts
    /// that are not each other's negation.</param>
    /// <param name="BecameTwoAmount">Mirrored pairs that would land across currencies.</param>
    /// <param name="UnfitTransactions">Transactions with a relabelled figure that does not
    /// fit the currency it would read in, a transaction merged into another left out
    /// (ADR-0114 D15).</param>
    /// <param name="AccountFigures">The account-level figures that do not fit, each one of
    /// <see cref="MoneyPlaces.RelabelAccountFigures"/>.</param>
    /// <param name="MergedAwayPairs">How many of <paramref name="NotRepresentable"/> are on
    /// a transaction merged into another.</param>
    /// <param name="MergedInto">The transactions those were merged into, each once.</param>
    public sealed record Verdict(
        string CurrencyCode,
        int NotRepresentable,
        int BecameTwoAmount,
        int UnfitTransactions,
        IReadOnlyList<string> AccountFigures,
        int MergedAwayPairs = 0,
        IReadOnlyList<MergedWinner>? MergedInto = null)
    {
        /// <summary>The verdict of a relabel that reads no figure in another
        /// currency.</summary>
        public static Verdict None(string currencyCode) => new(currencyCode, 0, 0, 0, []);

        /// <summary>Whether the relabel is refused.</summary>
        public bool Refused => NotRepresentable > 0 || UnfitTransactions > 0 || AccountFigures.Count > 0;
    }

    /// <summary>A transaction a refused pair's transaction was merged into (decided by the
    /// owner, 2026-11-18; ADR-0114 D13).</summary>
    /// <param name="Link">Where it is, as the refusal sends it.</param>
    /// <param name="RestoredByDelete">Whether deleting it brings back every merged row
    /// behind it: each was merged straight into it, and its delete removes it outright
    /// (<see cref="TxnHeaderRow.IsDeletedOutright"/>), which un-merges them. A row merged
    /// into one since merged into this, or into a feed row, which a delete only hides, is
    /// not.</param>
    public sealed record MergedWinner(RelabelMergedInto Link, bool RestoredByDelete);

    /// <summary>
    /// Judge relabelling the legs on <paramref name="relabels"/>' accounts, of the
    /// transactions in <paramref name="headerIds"/> (null: every transaction).
    /// </summary>
    /// <param name="db">The write's own context, inside its transaction.</param>
    /// <param name="ledgerId">The ledger.</param>
    /// <param name="relabels">The accounts whose legs change currency; one whose
    /// <c>From</c> equals its <c>To</c> relabels nothing.</param>
    /// <param name="headerIds">The moved transactions, or null for an account edit, which
    /// relabels every leg on the account.</param>
    /// <param name="currencyCode">The currency the verdict names.</param>
    /// <param name="accountFigures">The account-level figures the caller found that do not
    /// fit.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<Verdict> JudgeAsync(
        AppDbContext db,
        Guid ledgerId,
        IReadOnlyCollection<Relabel> relabels,
        IReadOnlyCollection<Guid>? headerIds,
        string currencyCode,
        IReadOnlyCollection<string> accountFigures,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(relabels);
        ArgumentNullException.ThrowIfNull(accountFigures);
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);

        var changing = relabels
            .Where(r => !string.Equals(r.From, r.To, StringComparison.Ordinal))
            .ToDictionary(r => r.AccountId);
        var figures = accountFigures.ToList();
        if (changing.Count == 0) return new Verdict(currencyCode, 0, 0, 0, figures);

        var accountIds = changing.Keys.ToList();
        var relabelled = db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId && accountIds.Contains(l.AccountId));
        if (headerIds is not null)
        {
            var ids = headerIds.ToList();
            relabelled = relabelled.Where(l => ids.Contains(l.HeaderId));
        }

        // Each relabelled leg with every other leg of its posting, and whether its
        // transaction was merged into another.
        var rows = await (
                from l in relabelled
                join h in db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
                from p in db.TxnLegs.AsNoTracking()
                    .Where(p => p.HeaderId == l.HeaderId && p.PostingIndex == l.PostingIndex && p.Id != l.Id)
                    .DefaultIfEmpty()
                select new
                {
                    l.Id,
                    l.HeaderId,
                    l.PostingIndex,
                    l.AccountId,
                    l.Amount,
                    MergedInto = h.IsMergedInto,
                    PartnerAccountId = (Guid?)p.AccountId,
                    PartnerAmount = (decimal?)p.Amount,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0) return new Verdict(currencyCode, 0, 0, 0, figures);

        var sides = await PostingLegPair.LoadSidesAsync(
                db, ledgerId,
                rows.Where(r => r.PartnerAccountId is not null).Select(r => r.PartnerAccountId!.Value).Concat(accountIds),
                cancellationToken)
            .ConfigureAwait(false);

        var unfit = new HashSet<Guid>();
        var notRepresentable = new HashSet<(Guid, int)>();
        var becameTwoAmount = new HashSet<(Guid, int)>();
        var mergedAwayPairs = new HashSet<(Guid, int)>();
        var winners = new HashSet<Guid>();
        foreach (var leg in rows.GroupBy(r => r.Id))
        {
            var row = leg.First();
            if (!sides.TryGetValue(row.AccountId, out var own)) continue;
            var relabel = changing[row.AccountId];
            var partners = leg
                .Where(r => r.PartnerAccountId is not null && sides.ContainsKey(r.PartnerAccountId.Value))
                .Select(r => (AccountId: r.PartnerAccountId!.Value, Amount: r.PartnerAmount!.Value))
                .ToList();
            // A transaction merged into another is judged for no places (ADR-0114 D15): no
            // screen shows or opens it, so its figure cannot be made to fit there.
            var judgedForPlaces = row.MergedInto is null;

            if (!own.IsReal)
            {
                // A category's leg beside a real line mirrors the real side and takes its
                // currency: it is no relabelled leg of its own. A posting between categories
                // has no real side, so its category line reads in the category's own code
                // (ADR-0114 D15) and is judged for places at the new one. A category leg
                // never forms a pair carrying two amounts.
                if (judgedForPlaces
                    && partners.All(p => !sides[p.AccountId].IsReal)
                    && !CurrencyMinorUnits.Fits(row.Amount, relabel.To))
                    unfit.Add(row.HeaderId);
                continue;
            }

            if (judgedForPlaces && !CurrencyMinorUnits.Fits(row.Amount, relabel.To)) unfit.Add(row.HeaderId);
            // A category line reads in the real leg's currency, which this changes.
            if (judgedForPlaces
                && partners.Any(p => !sides[p.AccountId].IsReal && !CurrencyMinorUnits.Fits(p.Amount, relabel.To)))
                unfit.Add(row.HeaderId);

            // A pair is a posting of two legs between real accounts. A posting of more legs
            // (an in-kind share transfer's) carries no second amount to judge.
            if (partners.Count != 1 || !sides[partners[0].AccountId].IsReal) continue;
            var (partnerId, partnerAmount) = partners[0];
            var partnerFrom = sides[partnerId].CurrencyCode;
            var partnerTo = changing.TryGetValue(partnerId, out var other) ? other.To : partnerFrom;
            var acrossBefore = !string.Equals(relabel.From, partnerFrom, StringComparison.Ordinal);
            var acrossAfter = !string.Equals(relabel.To, partnerTo, StringComparison.Ordinal);
            var mirrored = row.Amount == -partnerAmount;
            var posting = (row.HeaderId, row.PostingIndex);
            if (acrossBefore && !acrossAfter && !mirrored)
            {
                notRepresentable.Add(posting);
                // On a transaction merged into another, the refusal names the one it was
                // merged into (decided by the owner, 2026-11-18; ADR-0114 D13).
                if (row.MergedInto is { } winner)
                {
                    mergedAwayPairs.Add(posting);
                    winners.Add(winner);
                }
            }
            else if (!acrossBefore && acrossAfter && mirrored) becameTwoAmount.Add(posting);
        }

        var mergedInto = await MergedIntoAsync(db, ledgerId, winners, cancellationToken).ConfigureAwait(false);
        return new Verdict(currencyCode, notRepresentable.Count, becameTwoAmount.Count, unfit.Count, figures,
            mergedAwayPairs.Count, mergedInto);
    }

    /// <summary>One transaction a refused pair's transaction was merged into, or one further
    /// along its chain, as <see cref="MergedIntoAsync"/> reads it.</summary>
    private sealed record WinnerRow(
        Guid Id,
        Guid? MergedInto,
        bool IsHidden,
        DateTime PostedAt,
        string? Payee,
        Guid? OriginatingAccountId,
        bool DeletesOutright);

    /// <summary>
    /// The transactions <paramref name="winnerIds"/> names, each followed to the end of its
    /// chain: one since merged into another is itself in no register, so the row a register
    /// shows is named (a merge never folds a row into one merged away, so a chain has no
    /// cycle; the visited set only bounds the walk). Each is linked through the account it
    /// was entered from, whose register shows it. One whose chain ends nowhere, or at a row
    /// with no such account, which no API writer leaves, is not linked.
    /// </summary>
    private static async Task<IReadOnlyList<MergedWinner>> MergedIntoAsync(
        AppDbContext db, Guid ledgerId, IReadOnlyCollection<Guid> winnerIds, CancellationToken cancellationToken)
    {
        if (winnerIds.Count == 0) return [];

        var read = new Dictionary<Guid, WinnerRow>();
        var next = winnerIds.ToList();
        while (next.Count > 0)
        {
            var ids = next;
            var batch = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledgerId && ids.Contains(h.Id))
                .Select(h => new
                {
                    h.Id, h.IsMergedInto, h.IsHidden, h.PostedAt, h.Payee, h.OriginatingAccountId,
                    h.ExternalId, h.RecurringTransactionId, h.OccurrenceDate, h.IsRecurringTemplate,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var h in batch)
            {
                read[h.Id] = new WinnerRow(h.Id, h.IsMergedInto, h.IsHidden, h.PostedAt, h.Payee, h.OriginatingAccountId,
                    TxnHeaderRow.IsDeletedOutright(h.ExternalId, h.RecurringTransactionId, h.OccurrenceDate, h.IsRecurringTemplate));
            }
            next = batch
                .Where(h => h.IsMergedInto is { } up && !read.ContainsKey(up))
                .Select(h => h.IsMergedInto!.Value)
                .Distinct()
                .ToList();
        }

        // Each winner's shown row, and whether deleting that row brings the merged row back.
        // A winner left unlinked leaves the refusal unable to promise a way back for it, so
        // then none is promised.
        var shown = new List<(WinnerRow Row, bool Restored)>();
        var allShown = true;
        foreach (var id in winnerIds)
        {
            if (!read.TryGetValue(id, out var first)) { allShown = false; continue; }
            var row = first;
            var visited = new HashSet<Guid> { row.Id };
            while (row.MergedInto is { } up && read.TryGetValue(up, out var above) && visited.Add(above.Id)) row = above;
            if (row.MergedInto is not null || row.OriginatingAccountId is null) { allShown = false; continue; }
            shown.Add((row, first.MergedInto is null && first.DeletesOutright));
        }

        var accountIds = shown.Select(s => s.Row.OriginatingAccountId!.Value).Distinct().ToList();
        var names = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken)
            .ConfigureAwait(false);
        if (shown.Any(s => !names.ContainsKey(s.Row.OriginatingAccountId!.Value))) allShown = false;

        return shown
            .Where(s => names.ContainsKey(s.Row.OriginatingAccountId!.Value))
            .GroupBy(s => s.Row.Id)
            .Select(g =>
            {
                var row = g.First().Row;
                var account = row.OriginatingAccountId!.Value;
                return new MergedWinner(
                    new RelabelMergedInto(row.Id, account, names[account], DateOnly.FromDateTime(row.PostedAt), row.Payee, row.IsHidden),
                    allShown && g.All(s => s.Restored));
            })
            .OrderBy(w => w.Link.Date)
            .ThenBy(w => w.Link.HeaderId)
            .ToList();
    }

    /// <summary>The 422 for a refused <paramref name="verdict"/>, or null when it passes. A
    /// pair one currency cannot represent is named first: no edit of places makes it
    /// fit. When any such pair is on a transaction merged into another, the refusal says so
    /// and carries <c>mergedInto</c>, the transactions it was merged into (decided by the
    /// owner, 2026-11-18; ADR-0114 D13); pairs on rows a register shows keep the refusal
    /// they had.</summary>
    internal static IResult? Refusal(Verdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.NotRepresentable > 0)
        {
            var extra = new Dictionary<string, object?> { ["count"] = verdict.NotRepresentable };
            if (verdict.MergedAwayPairs > 0)
                extra["mergedInto"] = (verdict.MergedInto ?? []).Select(w => w.Link).ToList();
            return BusinessError.Problem(
                BusinessError.Codes.RelabelPairNotRepresentable, PairDetail(verdict), "Transfer amounts differ", extra);
        }
        if (verdict.UnfitTransactions > 0 || verdict.AccountFigures.Count > 0)
            return MoneyPlaces.RelabelRefusal(verdict.CurrencyCode, verdict.UnfitTransactions, verdict.AccountFigures);
        return null;
    }

    // The pair refusal's wording, one sentence each (ADR-0114 D13). The merged-away
    // sentences are a proposal awaiting the owner's review (decided by the owner,
    // 2026-11-18, that the refusal names the way out; the words are not yet approved).
    private const string PairLive = " Make its two amounts match first.";
    private const string PairMergedAway =
        " That transfer was merged into another transaction, so no register shows it.";
    private const string PairMixed =
        " Where you can open the transfer, make its two amounts match first. Where it was merged into "
        + "another transaction, no register shows it.";
    private const string PairRestore =
        " Deleting the transaction it was merged into brings it back, so you can make its two amounts match.";

    /// <summary>The pair refusal's detail: what blocks, and the way out. A pair on a row a
    /// register shows is made to match there. One on a transaction merged into another is
    /// shown by none; deleting the transaction it was merged into brings it back when every
    /// such transaction's delete removes it outright, and the detail says so only then.</summary>
    private static string PairDetail(Verdict verdict)
    {
        var live = verdict.NotRepresentable - verdict.MergedAwayPairs;
        var detail = $"A transfer between two currencies would end up in {verdict.CurrencyCode} with two amounts "
            + "that differ, which one currency cannot hold.";
        if (verdict.MergedAwayPairs == 0) return detail + PairLive;
        detail += live > 0 ? PairMixed : PairMergedAway;
        var restored = verdict.MergedInto is { Count: > 0 } winners && winners.All(w => w.RestoredByDelete);
        return restored ? detail + PairRestore : detail;
    }
}
