using Coffer.Domain.Annotations;
using Coffer.Importer.Moneydance.Db;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// What each register shows for a mapped transaction, through the one inheritance rule
/// (<see cref="MemoTagRules"/>), so an importer test asserts what the user sees and not
/// only where a value was stored.
/// </summary>
/// <remarks>
/// The shape is built here from facts the test states (which accounts are on the
/// event's investment side), not through the importer's own shape builder, so the oracle
/// does not share the code it checks.
/// </remarks>
internal static class RegisterView
{
    public sealed record Row(string? Memo, IReadOnlyList<string> Tags, bool IsReadOnly, bool MemoInherited, bool TagsInherited);

    public static Row Of(
        TxnHeaderRow header,
        IReadOnlyList<TxnLegRow> legs,
        IReadOnlyList<string> headerTags,
        IReadOnlyList<LegTagSeed> legTags,
        Guid account,
        int posting,
        IReadOnlyCollection<Guid>? investmentSide = null)
    {
        var shape = new TxnShape(
            header.OriginatingAccountId,
            header.Action,
            [.. legs.Select(l => new ShapeLeg(
                new LegKey(l.PostingIndex, l.AccountId),
                l.Id,
                l.PostingRole,
                OnInvestmentSide: investmentSide?.Contains(l.AccountId) ?? false))]);
        var tagsByLeg = legTags.ToDictionary(s => s.LegId, s => s.Tags);
        var notes = new Annotations<string>(
            new Notes<string>(header.Memo, headerTags),
            legs.ToDictionary(
                l => new LegKey(l.PostingIndex, l.AccountId),
                l => new Notes<string>(l.LegMemo, tagsByLeg.GetValueOrDefault(l.Id) ?? [])));

        var shown = MemoTagRules.PresentInMemory(shape, notes, new LegKey(posting, account));
        return new Row(
            shown.Memo,
            shown.Tags,
            shown.Presentation.IsReadOnly,
            shown.Presentation.MemoInherited,
            shown.Presentation.TagsInherited);
    }
}
