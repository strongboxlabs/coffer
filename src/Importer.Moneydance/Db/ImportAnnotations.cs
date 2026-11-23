using Coffer.Domain.Annotations;
using Coffer.Importer.Moneydance.Mappers;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>Tag names Moneydance recorded for one leg, bound to the leg's proposed id.</summary>
public sealed record LegTagSeed(Guid LegId, IReadOnlyList<string> Tags);

/// <summary>
/// A mapped header and its legs with the planned notes written onto them, and the tag
/// names to link to the header and to each leg.
/// </summary>
/// <param name="TransactionMemo">The memo where the transaction's own value sits: the
/// header's on several postings, the home account's leg on one. The feed originals are
/// compared against it.</param>
public sealed record StampedNotes(
    TxnHeaderRow Header,
    IReadOnlyList<TxnLegRow> Legs,
    IReadOnlyList<string> HeaderTags,
    IReadOnlyList<LegTagSeed> LegTags,
    string? TransactionMemo);

/// <summary>
/// The importer's adapter onto the write primitive (<see cref="AnnotationPlanner"/>):
/// it describes a mapped header as a <see cref="TxnShape"/> and writes a plan back
/// onto the rows the bulk insert persists. The mappers decide what Moneydance's
/// fields mean; the planner decides where each value may sit; this only persists.
/// </summary>
/// <remarks>
/// <para><b>Originating account.</b> The plan's, which is the Moneydance home account
/// the mapper planned from: every mapper builds each posting from the home, so the home
/// carries a leg in every posting it emits. A header with no legs names no account and
/// keeps NULL. A header with legs on none of which the plan's owner sits cannot come out
/// of the mappers; if one ever does, the import stops instead of stamping an account the
/// transaction does not touch.</para>
/// <para><b>Fail closed.</b> A plan with violations is never written: on an import it can
/// only mean the mapper asked for a placement the shape cannot hold, so the import stops
/// naming the transaction.</para>
/// </remarks>
public static class ImportAnnotations
{
    /// <summary>
    /// The shape of a mapped header for the planner. An event's investment side is the
    /// home brokerage and its holdings account; only the home brokerage writes and owns
    /// it. A Moneydance import never produces an in-kind transfer.
    /// </summary>
    public static TxnShape ShapeOf(AccountRef home, string? action, IReadOnlyList<TxnLegRow> legs)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(legs);

        var isEvent = action is not null;
        var homeIsBrokerage = home.HoldingsAccountId is not null;
        return new TxnShape(
            originatingAccountId: null,
            action: action,
            legs: [.. legs.Select(l => new ShapeLeg(
                Key: new LegKey(l.PostingIndex, l.AccountId),
                LegId: l.Id,
                Role: l.PostingRole,
                OnInvestmentSide: isEvent && (l.AccountId == home.Id || l.AccountId == home.HoldingsAccountId),
                IsBrokerage: homeIsBrokerage && l.AccountId == home.Id))]);
    }

    /// <summary>Write <paramref name="plan"/> onto <paramref name="header"/> and <paramref name="legs"/>.</summary>
    public static StampedNotes Stamp(AnnotationPlan<string> plan, TxnHeaderRow header, IReadOnlyList<TxnLegRow> legs)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(legs);

        if (!plan.IsValid)
            throw new InvalidOperationException(
                $"Moneydance transaction '{header.ExternalId}' was mapped to notes its shape cannot hold "
                + $"({string.Join(", ", plan.Violations.Select(v => v.Code))}); the import is stopped.");

        var planned = plan.OriginatingAccountId;
        if (legs.Count == 0 && planned is not null)
            throw new InvalidOperationException(
                $"Moneydance transaction '{header.ExternalId}' has no legs but was planned with an originating account.");
        var owner = legs.Count == 0
            ? null
            : ImportOriginatingAccount.For(
                planned ?? throw new InvalidOperationException(
                    $"Moneydance transaction '{header.ExternalId}' was planned without an originating account."),
                legs, header.ExternalId);

        var keys = legs.Select(l => new LegKey(l.PostingIndex, l.AccountId)).ToHashSet();
        if (plan.Result.Legs.Keys.Any(k => !keys.Contains(k)))
            throw new InvalidOperationException(
                $"Moneydance transaction '{header.ExternalId}' was planned onto a leg it does not have.");

        var notes = plan.Result;
        var stampedLegs = legs
            .Select(l => l with { LegMemo = notes.For(new LegKey(l.PostingIndex, l.AccountId)).Memo })
            .ToList();
        var legTags = legs
            .Select(l => new LegTagSeed(l.Id, notes.For(new LegKey(l.PostingIndex, l.AccountId)).Tags))
            .Where(s => s.Tags.Count > 0)
            .ToList();

        var several = legs.Select(l => l.PostingIndex).Distinct().Count() > 1;
        var transactionMemo = several || owner is null
            ? notes.Header.Memo
            : stampedLegs.Where(l => l.AccountId == owner).OrderBy(l => l.PostingIndex).First().LegMemo;

        return new StampedNotes(
            header with { Memo = notes.Header.Memo, OriginatingAccountId = owner },
            stampedLegs,
            notes.Header.Tags,
            legTags,
            transactionMemo);
    }
}
