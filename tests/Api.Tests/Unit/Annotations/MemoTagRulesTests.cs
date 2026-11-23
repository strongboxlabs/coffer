using System.Linq.Expressions;

using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;

using static Coffer.Api.Tests.Unit.Annotations.Scenarios;

namespace Coffer.Api.Tests.Unit.Annotations;

/// <summary>
/// The perspective matrix: every shape, every placement of stored values, every
/// leg as the viewing side, checked against the kinds table written out plainly.
/// </summary>
/// <remarks>
/// The oracle below restates the kinds table (<see cref="LegKind"/>) cell by cell, in the
/// most literal code available, so a change to the rule that the table does not
/// sanction fails here with the shape, the placement and the side named.
/// </remarks>
public sealed class MemoTagRulesTests
{
    [Fact]
    public void Every_shape_placement_and_side_shows_what_the_kinds_table_says()
    {
        var checkedCases = 0;
        var failures = new List<string>();

        foreach (var (name, shape) in All())
        {
            // Slot 0 is the header, slot i the i-th leg. Memos and tags vary
            // independently, so a source mix-up between the two cannot hide.
            var slots = shape.Legs.Count + 1;
            for (var memoMask = 0; memoMask < 1 << slots; memoMask++)
            for (var tagMask = 0; tagMask < 1 << slots; tagMask++)
            {
                var stored = Stored(shape, memoMask, tagMask);
                foreach (var viewing in shape.Legs)
                {
                    checkedCases++;
                    var actual = MemoTagRules.PresentInMemory(shape, stored, viewing.Key);
                    var expected = Oracle(shape, stored, viewing);
                    var diff = Diff(expected, actual);
                    if (diff is not null && failures.Count < 20)
                        failures.Add($"{name}, viewed from {NameOf(viewing.Key.AccountId)} (posting {viewing.Key.PostingIndex}), stored {stored}: {diff}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(checkedCases > 10_000, $"Only {checkedCases} cases ran; the matrix lost its shapes.");
    }

    // ---------------------------------------------------------------------
    // Named cases: the matrix proves agreement; these say what agreement means.
    // ---------------------------------------------------------------------

    [Fact]
    public void A_one_split_side_with_nothing_of_its_own_shows_the_other_sides_value_as_inherited()
    {
        var shape = OneSplit(Bank, Bank, Groceries);
        var stored = OnLeg(Bank, 0, "coffee", ["work"]);

        var category = MemoTagRules.PresentInMemory(shape, stored, new LegKey(0, Groceries));

        Assert.Equal(LegKind.OnePosting, category.Presentation.Kind);
        Assert.False(category.Presentation.IsReadOnly);
        Assert.Equal("coffee", category.Memo);
        Assert.True(category.Presentation.MemoInherited);
        Assert.Equal(["work"], category.Tags);
        Assert.True(category.Presentation.TagsInherited);
        Assert.Equal(Bank, category.Presentation.InheritFromAccountId);

        var bank = MemoTagRules.PresentInMemory(shape, stored, new LegKey(0, Bank));
        Assert.Equal("coffee", bank.Memo);
        Assert.False(bank.Presentation.MemoInherited);
    }

    [Fact]
    public void A_split_side_line_with_no_value_stays_blank_even_when_its_opposite_has_one()
    {
        // No header fallback and no reach across to the opposite leg.
        var shape = MultiSplit(Bank, Bank, Groceries, Fuel);
        var stored = new Annotations<string>(
            new Notes<string>("weekly shop", ["home"]),
            new Dictionary<LegKey, Notes<string>> { [new LegKey(0, Groceries)] = new("override", ["category-side"]) });

        var line = MemoTagRules.PresentInMemory(shape, stored, new LegKey(0, Bank));

        Assert.Equal(LegKind.SplitSide, line.Presentation.Kind);
        Assert.Null(line.Memo);
        Assert.Empty(line.Tags);
        Assert.Equal("weekly shop", line.Presentation.InPlayHeaderMemo);
        // Filters and reports count the transaction's tags for every line.
        Assert.Equal(["home"], line.InPlayTags);
        Assert.Null(line.Presentation.InheritFromAccountId);
    }

    [Fact]
    public void The_opposite_side_of_a_split_line_never_sees_the_header()
    {
        // Own, else that line's other leg. The transaction-level value is
        // not in play on the opposite side at all.
        var shape = MultiSplit(Bank, Bank, Groceries, Fuel);
        var stored = new Annotations<string>(
            new Notes<string>("weekly shop", ["home"]),
            new Dictionary<LegKey, Notes<string>> { [new LegKey(1, Bank)] = new("diesel", ["car"]) });

        var groceries = MemoTagRules.PresentInMemory(shape, stored, new LegKey(0, Groceries));
        var fuel = MemoTagRules.PresentInMemory(shape, stored, new LegKey(1, Fuel));

        Assert.Equal(LegKind.SplitOpposite, groceries.Presentation.Kind);
        Assert.True(groceries.Presentation.IsReadOnly);
        Assert.Null(groceries.Memo);
        Assert.Empty(groceries.InPlayTags);
        Assert.Equal("diesel", fuel.Memo);
        Assert.Equal(["car"], fuel.InPlayTags);
        Assert.Equal(Bank, fuel.Presentation.InheritFromAccountId);
    }

    [Fact]
    public void In_the_symmetric_shape_the_stored_originating_account_alone_decides_the_split_side()
    {
        // Both accounts are on every posting, so no posting count can tell them apart.
        var byBank = Symmetric(Bank);
        var byCategory = Symmetric(Groceries);
        var stored = new Annotations<string>(new Notes<string>("header"));

        Assert.Equal(LegKind.SplitSide, MemoTagRules.PresentInMemory(byBank, stored, new LegKey(0, Bank)).Presentation.Kind);
        Assert.Equal(LegKind.SplitOpposite, MemoTagRules.PresentInMemory(byBank, stored, new LegKey(0, Groceries)).Presentation.Kind);
        Assert.Equal(LegKind.SplitOpposite, MemoTagRules.PresentInMemory(byCategory, stored, new LegKey(1, Bank)).Presentation.Kind);
        Assert.Equal(LegKind.SplitSide, MemoTagRules.PresentInMemory(byCategory, stored, new LegKey(1, Groceries)).Presentation.Kind);
    }

    [Fact]
    public void An_Xfr_seen_from_the_bank_is_a_read_only_counterpart_that_inherits_the_brokerage_leg()
    {
        var shape = XfrToBank();
        var stored = OnLeg(Brokerage, 0, "to checking", ["moves"]);

        var bank = MemoTagRules.PresentInMemory(shape, stored, new LegKey(0, Bank));
        var brokerage = MemoTagRules.PresentInMemory(shape, stored, new LegKey(0, Brokerage));

        Assert.Equal(LegKind.EventCounterpartOne, bank.Presentation.Kind);
        Assert.True(bank.Presentation.IsReadOnly);
        Assert.Equal("to checking", bank.Memo);
        Assert.Equal(["moves"], bank.Tags);
        Assert.Equal(LegKind.OnePosting, brokerage.Presentation.Kind);
        Assert.False(brokerage.Presentation.IsReadOnly);
    }

    [Fact]
    public void The_funding_brokerage_of_a_buy_xfr_is_an_opposite_row_that_inherits_the_event_header()
    {
        var shape = BuyXfrFromOtherBrokerage();
        var stored = new Annotations<string>(new Notes<string>("rebalance", ["plan"]));

        var funding = MemoTagRules.PresentInMemory(shape, stored, new LegKey(1, OtherBrokerage));

        Assert.Equal(LegKind.EventOpposite, funding.Presentation.Kind);
        Assert.True(funding.Presentation.IsReadOnly);
        Assert.Equal("rebalance", funding.Memo);
        Assert.Equal(["plan"], funding.Tags);
        Assert.True(funding.Presentation.MemoInherited);
        // From the event's brokerage, not from the cash leg's id.
        Assert.Equal(Brokerage, funding.Presentation.InheritFromAccountId);
    }

    [Fact]
    public void An_event_opposite_row_whose_other_leg_is_off_the_investment_side_inherits_that_leg_not_the_header()
    {
        // No writer produces this today: every native and imported investment pair
        // carries the brokerage's cash leg. The table still defines the cell ("other is
        // investment side ? header : other"), so a future pair with no part in the
        // event's investment side cannot start showing the event's header.
        var shape = new TxnShape(Brokerage, LedgerActions.Misc,
        [
            Leg(0, Brokerage, PostingRoles.Income, investmentSide: true), Leg(0, Groceries, PostingRoles.Income),
            Leg(1, Fees, PostingRoles.Fee), Leg(1, Fuel, PostingRoles.Fee),
        ]);
        var stored = new Annotations<string>(
            new Notes<string>("event", ["event-tag"]),
            new Dictionary<LegKey, Notes<string>> { [new LegKey(1, Fuel)] = new("fuel side", ["fuel-tag"]) });

        var fees = MemoTagRules.PresentInMemory(shape, stored, new LegKey(1, Fees));
        var groceries = MemoTagRules.PresentInMemory(shape, stored, new LegKey(0, Groceries));

        Assert.Equal(LegKind.EventOpposite, fees.Presentation.Kind);
        Assert.Equal("fuel side", fees.Memo);
        Assert.Equal(["fuel-tag"], fees.Tags);
        Assert.Equal(Fuel, fees.Presentation.InheritFromAccountId);
        Assert.Equal("event", groceries.Memo);
    }

    [Fact]
    public void Every_leg_of_an_in_kind_transfer_shows_the_header_and_ignores_stored_leg_values()
    {
        var shape = TransferShares();
        var stored = new Annotations<string>(
            new Notes<string>("move to new broker", ["consolidate"]),
            new Dictionary<LegKey, Notes<string>> { [new LegKey(1, OtherBrokerage)] = new("stale", ["stale"]) });

        foreach (var leg in shape.Legs)
        {
            var shown = MemoTagRules.PresentInMemory(shape, stored, leg.Key);
            Assert.Equal(LegKind.EventSide, shown.Presentation.Kind);
            Assert.False(shown.Presentation.IsReadOnly);
            Assert.Equal("move to new broker", shown.Memo);
            Assert.Equal(["consolidate"], shown.Tags);
            Assert.Equal(["consolidate"], shown.InPlayTags);
        }
    }

    [Fact]
    public void An_entry_is_keyed_by_its_header_only_on_the_account_the_header_belongs_to()
    {
        var empty = Annotations<string>.Empty;
        bool Groups(TxnShape shape, int posting, Guid account) =>
            MemoTagRules.PresentInMemory(shape, empty, new LegKey(posting, account)).Presentation.GroupsByHeader;

        // A one-posting row is one leg in its account's register either way. Only the
        // side it was entered from keys it by the header, so a register spanning both
        // accounts still shows each side as its own row.
        Assert.True(Groups(OneSplit(Bank, Bank, Groceries), 0, Bank));
        Assert.False(Groups(OneSplit(Bank, Bank, Groceries), 0, Groceries));
        Assert.True(Groups(XfrToBank(), 0, Brokerage));
        Assert.False(Groups(XfrToBank(), 0, Bank));

        // The split side groups its lines; the opposite side never does, the
        // symmetric shape's other account included.
        var split = MultiSplit(Bank, Bank, Groceries, Fuel);
        Assert.True(Groups(split, 0, Bank));
        Assert.False(Groups(split, 0, Groceries));
        Assert.False(Groups(Symmetric(Bank), 0, Groceries));
        Assert.True(Groups(Symmetric(Groceries), 0, Groceries));

        // An event groups on its whole investment side, holdings and both brokerages
        // of an in-kind transfer included; the funding brokerage is an opposite row.
        foreach (var leg in TransferShares().Legs)
            Assert.True(Groups(TransferShares(), leg.Key.PostingIndex, leg.Key.AccountId));
        Assert.False(Groups(BuyXfrFromOtherBrokerage(), 1, OtherBrokerage));
    }

    [Fact]
    public void A_one_posting_transfer_between_brokerages_is_editable_only_from_the_one_it_was_entered_from()
    {
        var shape = BrokerageToBrokerage(OtherBrokerage);

        var entering = MemoTagRules.PresentInMemory(shape, Annotations<string>.Empty, new LegKey(0, OtherBrokerage));
        var counterpart = MemoTagRules.PresentInMemory(shape, Annotations<string>.Empty, new LegKey(0, Brokerage));

        Assert.Equal(LegKind.OnePosting, entering.Presentation.Kind);
        Assert.Equal(LegKind.EventCounterpartOne, counterpart.Presentation.Kind);
        Assert.True(counterpart.Presentation.IsReadOnly);
    }

    // ---------------------------------------------------------------------
    // The expression itself
    // ---------------------------------------------------------------------

    [Fact]
    public void The_rule_is_one_member_init_tree_with_nothing_left_to_inline()
    {
        // EF composes further operators only over a member-init projection, and it
        // cannot translate a call to the inlining marker.
        Assert.IsType<MemberInitExpression>(MemoTagRules.Present.Body);
        var calls = new CallCollector();
        calls.Visit(MemoTagRules.Present);
        Assert.Empty(calls.Methods);
        Assert.Empty(calls.Invocations);
    }

    [Fact]
    public void The_shared_transfer_shares_spelling_matches_the_action_catalog()
    {
        Assert.Equal(LedgerActions.TransferShares, TxnShape.TransferSharesAction);
    }

    private sealed class CallCollector : ExpressionVisitor
    {
        public List<string> Methods { get; } = [];
        public List<Expression> Invocations { get; } = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            Methods.Add(node.Method.Name);
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitInvocation(InvocationExpression node)
        {
            Invocations.Add(node);
            return base.VisitInvocation(node);
        }
    }

    // ---------------------------------------------------------------------
    // The oracle
    // ---------------------------------------------------------------------

    /// <param name="InheritedMemo">What the leg would show with its own memo blank, as stored.</param>
    /// <param name="InheritedTags">What the leg would show with its own tags blank.</param>
    /// <param name="InheritsFromHeader">The inherited tags come from the header (an event's opposite row).</param>
    /// <param name="InheritsFromLeg">The inherited tags come from the posting's other leg.</param>
    private sealed record Expected(
        LegKind Kind, bool ReadOnly, string? Memo, IReadOnlyList<string> Tags, IReadOnlyList<string> InPlayTags,
        bool MemoInherited, bool TagsInherited, Guid? InheritFrom, string? InPlayHeaderMemo,
        string? InheritedMemo, IReadOnlyList<string> InheritedTags, bool InheritsFromHeader, bool InheritsFromLeg)
    {
        /// <summary>
        /// An entry is keyed by the header only on the account the header belongs to:
        /// the originating account, or for an event its investment side. Elsewhere each
        /// leg is an entry of its own.
        /// </summary>
        public bool GroupsByHeader { get; init; }

        /// <summary>The transaction's tags, only where the header is in play: the split side and the event side.</summary>
        public IReadOnlyList<string>? HeaderTags { get; init; }
    }

    private static Expected Oracle(TxnShape shape, Annotations<string> stored, ShapeLeg viewing)
    {
        var other = shape.OtherOf(viewing.Key);
        var own = stored.For(viewing.Key);
        var otherNotes = other is null ? Notes<string>.Empty : stored.For(other.Key);
        var header = stored.Header;
        var viewingInvestmentSide = shape.IsEvent && viewing.OnInvestmentSide;
        var otherInvestmentSide = shape.IsEvent && other is { OnInvestmentSide: true };

        // | Kind                | Test                                                              |
        // | OnePosting          | total = 1, and not an actioned counterpart                        |
        // | EventCounterpartOne | action, total = 1, viewing account not on the investment side     |
        // | SplitSide           | no action, total > 1, account = originating                       |
        // | SplitOpposite       | no action, total > 1, account ≠ originating                       |
        // | EventSide           | action, total > 1, account on the investment side                 |
        // | EventOpposite       | action, total > 1, otherwise                                      |
        var groupsByHeader = shape.IsEvent ? viewingInvestmentSide : viewing.Key.AccountId == shape.OriginatingAccountId;
        return OracleKinds(shape, viewing, own, other, otherNotes, header, viewingInvestmentSide, otherInvestmentSide)
            with { GroupsByHeader = groupsByHeader };
    }

    private static Expected OracleKinds(
        TxnShape shape, ShapeLeg viewing, Notes<string> own, ShapeLeg? other,
        Notes<string> otherNotes, Notes<string> header, bool viewingInvestmentSide, bool otherInvestmentSide)
    {
        LegKind kind;
        if (shape.PostingCount == 1)
            kind = shape.IsEvent && !viewingInvestmentSide ? LegKind.EventCounterpartOne : LegKind.OnePosting;
        else if (!shape.IsEvent)
            kind = viewing.Key.AccountId == shape.OriginatingAccountId ? LegKind.SplitSide : LegKind.SplitOpposite;
        else
            kind = viewingInvestmentSide ? LegKind.EventSide : LegKind.EventOpposite;

        switch (kind)
        {
            case LegKind.SplitSide:
                // row: header; line: own only, else blank. In play: header ∪ line own.
                // The header is this side's own, so nothing is inherited.
                return new Expected(kind, false, own.Memo, own.Tags,
                    [.. header.Tags.Concat(own.Tags).Distinct()], false, false, null, header.Memo,
                    null, [], false, false) { HeaderTags = header.Tags };
            case LegKind.EventSide:
                return new Expected(kind, false, header.Memo, header.Tags, header.Tags, false, false, null, header.Memo,
                    null, [], false, false) { HeaderTags = header.Tags };
            default:
                // own ?? other; for EventOpposite the other is the header when that leg is on the investment side.
                var fromHeader = kind == LegKind.EventOpposite && otherInvestmentSide;
                var sourceMemo = fromHeader ? header.Memo : otherNotes.Memo;
                var sourceTags = fromHeader ? header.Tags : otherNotes.Tags;
                var tags = own.Tags.Count > 0 ? own.Tags : sourceTags;
                return new Expected(
                    kind,
                    kind != LegKind.OnePosting,
                    own.Memo ?? sourceMemo,
                    tags,
                    tags,
                    own.Memo is null && sourceMemo is not null,
                    own.Tags.Count == 0 && sourceTags.Count > 0,
                    fromHeader ? shape.OriginatingAccountId : other?.Key.AccountId,
                    null,
                    // Inherited means "what the source holds", whether or not an own
                    // value currently hides it.
                    sourceMemo,
                    sourceTags,
                    fromHeader,
                    !fromHeader && other is not null);
        }
    }

    private static string? Diff(Expected expected, ShownNotes<string> actual)
    {
        var p = actual.Presentation;
        var problems = new List<string>();
        if (expected.Kind != p.Kind) problems.Add($"kind {p.Kind}, expected {expected.Kind}");
        if (expected.ReadOnly != p.IsReadOnly) problems.Add($"read-only {p.IsReadOnly}, expected {expected.ReadOnly}");
        if (expected.GroupsByHeader != p.GroupsByHeader) problems.Add($"groups by header {p.GroupsByHeader}, expected {expected.GroupsByHeader}");
        if (expected.Memo != actual.Memo) problems.Add($"memo '{actual.Memo}', expected '{expected.Memo}'");
        if (!expected.Tags.SequenceEqual(actual.Tags)) problems.Add($"tags [{string.Join(",", actual.Tags)}], expected [{string.Join(",", expected.Tags)}]");
        if (!expected.InPlayTags.SequenceEqual(actual.InPlayTags)) problems.Add($"in-play tags [{string.Join(",", actual.InPlayTags)}], expected [{string.Join(",", expected.InPlayTags)}]");
        if (expected.MemoInherited != p.MemoInherited) problems.Add($"memo inherited {p.MemoInherited}, expected {expected.MemoInherited}");
        if (expected.TagsInherited != p.TagsInherited) problems.Add($"tags inherited {p.TagsInherited}, expected {expected.TagsInherited}");
        if (expected.InheritFrom != p.InheritFromAccountId) problems.Add($"inherit from {p.InheritFromAccountId}, expected {expected.InheritFrom}");
        if (expected.InPlayHeaderMemo != p.InPlayHeaderMemo) problems.Add($"in-play header memo '{p.InPlayHeaderMemo}', expected '{expected.InPlayHeaderMemo}'");
        if (expected.InheritedMemo != p.InheritedMemo) problems.Add($"inherited memo '{p.InheritedMemo}', expected '{expected.InheritedMemo}'");
        if (!expected.InheritedTags.SequenceEqual(actual.InheritedTags)) problems.Add($"inherited tags [{string.Join(",", actual.InheritedTags)}], expected [{string.Join(",", expected.InheritedTags)}]");
        if (expected.InheritsFromHeader != (p.InheritedTagHeaderId is not null)) problems.Add($"inherited tag header source {p.InheritedTagHeaderId}, expected {(expected.InheritsFromHeader ? "the header" : "none")}");
        if (expected.InheritsFromLeg != (p.InheritedTagLegId is not null)) problems.Add($"inherited tag leg source {p.InheritedTagLegId}, expected {(expected.InheritsFromLeg ? "the other leg" : "none")}");
        if (expected.HeaderTags is null ? actual.HeaderTags is not null : actual.HeaderTags is null || !expected.HeaderTags.SequenceEqual(actual.HeaderTags))
            problems.Add($"header tags [{(actual.HeaderTags is null ? "none" : string.Join(",", actual.HeaderTags))}], expected [{(expected.HeaderTags is null ? "none" : string.Join(",", expected.HeaderTags))}]");
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>Stored notes per mask: bit 0 the header, bit i the i-th leg.</summary>
    private static Annotations<string> Stored(TxnShape shape, int memoMask, int tagMask)
    {
        Notes<string> At(int slot) => new(
            (memoMask & (1 << slot)) != 0 ? $"m{slot}" : null,
            (tagMask & (1 << slot)) != 0 ? [$"t{slot}"] : []);

        return new Annotations<string>(
            At(0),
            shape.Legs.Select((leg, i) => (leg.Key, Notes: At(i + 1))).ToDictionary(p => p.Key, p => p.Notes));
    }

    private static Annotations<string> OnLeg(Guid account, int posting, string? memo, string[] tags) =>
        new(null, new Dictionary<LegKey, Notes<string>> { [new LegKey(posting, account)] = new(memo, tags) });
}
