using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;

using static Coffer.Api.Tests.Unit.Annotations.Scenarios;

namespace Coffer.Api.Tests.Unit.Annotations;

/// <summary>
/// The write primitive: where a value goes, what a change of shape does to the
/// values already there, and what is refused.
/// </summary>
public sealed class AnnotationPlannerTests
{
    private static readonly FieldEdit<string> LeaveMemo = FieldEdit<string>.Leave;
    private static readonly FieldEdit<IReadOnlyList<string>> LeaveTags = FieldEdit<IReadOnlyList<string>>.Leave;

    private static FieldEdit<string> Memo(string? value) =>
        value is null ? FieldEdit<string>.Inherit : FieldEdit<string>.Set(value);

    private static FieldEdit<IReadOnlyList<string>> Tags(params string[] tags) =>
        FieldEdit<IReadOnlyList<string>>.Set(tags);

    private static AnnotationIntent<string> From(Guid side, FieldEdit<string>? memo = null,
        FieldEdit<IReadOnlyList<string>>? tags = null, params LineIntent<string>[] lines) =>
        new(side, memo ?? LeaveMemo, tags ?? LeaveTags, lines);

    private static Annotations<string> Notes(Notes<string>? header, params (LegKey Key, string? Memo, string[] Tags)[] legs) =>
        new(header, legs.ToDictionary(l => l.Key, l => new Notes<string>(l.Memo, l.Tags)));

    private static LegKey K(int posting, Guid account) => new(posting, account);

    private static AnnotationPlan<string> Valid(AnnotationPlan<string> plan)
    {
        Assert.True(plan.IsValid, string.Join("; ", plan.Violations.Select(v => $"{v.Code}: {v.Detail}")));
        return plan;
    }

    // =====================================================================
    // Placement
    // =====================================================================

    [Fact]
    public void A_one_split_edit_writes_the_sides_own_leg_from_either_side_and_never_the_header()
    {
        var shape = OneSplit(Bank, Bank, Groceries);

        var fromBank = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, Annotations<string>.Empty),
            From(Bank, Memo("coffee"), Tags("work"))));
        var fromCategory = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, fromBank.Result),
            From(Groceries, Memo("beans"))));

        Assert.Equal(new Notes<string>("coffee", ["work"]), fromBank.Result.For(K(0, Bank)));
        Assert.True(fromBank.Result.Header.IsEmpty);
        Assert.Equal(new Notes<string>("beans"), fromCategory.Result.For(K(0, Groceries)));
        Assert.Equal(new Notes<string>("coffee", ["work"]), fromCategory.Result.For(K(0, Bank)));
        Assert.True(fromCategory.Result.Header.IsEmpty);
        // Either side of a one-split may edit; neither edit moves ownership.
        Assert.Equal(Bank, fromCategory.OriginatingAccountId);
    }

    [Fact]
    public void Clearing_a_value_reverts_the_side_to_inheriting_and_leaving_it_keeps_it()
    {
        var shape = OneSplit(Bank, Bank, Groceries);
        var stored = Notes(null, (K(0, Bank), "coffee", ["work"]), (K(0, Groceries), "beans", ["home"]));

        var plan = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, stored),
            From(Groceries, FieldEdit<string>.Inherit, FieldEdit<IReadOnlyList<string>>.Inherit)));
        var untouched = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, stored),
            AnnotationIntent<string>.Untouched(Groceries)));

        Assert.True(plan.Result.For(K(0, Groceries)).IsEmpty);
        Assert.Equal("coffee", MemoTagRules.PresentInMemory(shape, plan.Result, K(0, Groceries)).Memo);
        Assert.Equal(stored, untouched.Result);
    }

    [Fact]
    public void A_multi_split_edit_from_the_split_side_writes_the_header_and_each_lines_split_side_leg()
    {
        var shape = MultiSplit(Bank, Bank, Groceries, Fuel);

        var plan = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, Annotations<string>.Empty),
            From(Bank, Memo("trip"), Tags("car"),
                new LineIntent<string>(1, Memo("diesel"), Tags("fuel-card")))));

        Assert.Equal(new Notes<string>("trip", ["car"]), plan.Result.Header);
        Assert.Equal(new Notes<string>("diesel", ["fuel-card"]), plan.Result.For(K(1, Bank)));
        Assert.True(plan.Result.For(K(1, Fuel)).IsEmpty);
        Assert.True(plan.Result.For(K(0, Bank)).IsEmpty);
    }

    [Fact]
    public void Line_notes_on_a_one_posting_result_are_refused()
    {
        var shape = OneSplit(Bank, Bank, Groceries);

        var plan = AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, Annotations<string>.Empty),
            From(Bank, lines: new LineIntent<string>(0, Memo("x"), LeaveTags)));

        Assert.Equal(AnnotationViolationCodes.LineNotesOnOnePosting, Assert.Single(plan.Violations).Code);
    }

    [Fact]
    public void Line_notes_a_client_gets_wrong_are_refused_as_a_violation_never_thrown()
    {
        // Each of these comes straight from a request body, so each must reach the
        // client as a 422, not a 500.
        var split = MultiSplit(Bank, Bank, Groceries, Fuel);
        var line = (int index, string memo) => new LineIntent<string>(index, Memo(memo), LeaveTags);
        var cases = new (string Name, TxnShape Shape, LineIntent<string>[] Lines)[]
        {
            ("a line past the last posting", split, [line(5, "x")]),
            ("a negative line", split, [line(-1, "x")]),
            ("the same line twice", split, [line(1, "x"), line(1, "y")]),
            ("line notes on an investment event", BuyWithFee(), [line(1, "x")]),
        };

        foreach (var (name, shape, lines) in cases)
        {
            var side = shape.OriginatingAccountId!.Value;
            var plan = AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, Annotations<string>.Empty),
                From(side, Memo("whole"), lines: lines));

            Assert.True(plan.Violations.Count == 1 && plan.Violations[0].Code == AnnotationViolationCodes.LineNotesInvalid,
                $"{name}: {string.Join("; ", plan.Violations.Select(v => v.Code))}");
        }
    }

    [Fact]
    public void Every_read_only_row_is_refused_and_says_so()
    {
        var split = MultiSplit(Bank, Bank, Groceries, Fuel);
        var xfr = XfrToBank();
        var buyXfr = BuyXfrFromOtherBrokerage();

        var cases = new[]
        {
            AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(split, Annotations<string>.Empty), From(Groceries, Memo("x"))),
            AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(xfr, Annotations<string>.Empty), From(Bank, Memo("x"))),
            AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(buyXfr, Annotations<string>.Empty), From(OtherBrokerage, Memo("x"))),
        };

        foreach (var plan in cases)
            Assert.Equal(AnnotationViolationCodes.ReadOnlyRow, Assert.Single(plan.Violations).Code);
    }

    [Fact]
    public void Only_the_events_brokerage_writes_it_never_a_holdings_account_or_an_in_kind_destination()
    {
        // A holdings account is on the investment side, on every posting of a buy with
        // no fee, but it is not a brokerage. The destination of an in-kind transfer is a
        // brokerage on the investment side, but the source owns the transfer. Neither may
        // write, and neither may take ownership.
        var cases = new (string Name, TxnShape Shape, Guid Side)[]
        {
            ("holdings on a one-posting buy", OnePostingBuy(), Holdings),
            ("holdings on a buy with a fee", BuyWithFee(), Holdings),
            ("the destination brokerage of an in-kind transfer", TransferShares(), OtherBrokerage),
            ("the destination's holdings account", TransferShares(), OtherHoldings),
        };

        foreach (var (name, shape, side) in cases)
        {
            var plan = AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, Annotations<string>.Empty),
                From(side, Memo("x"), Tags("y")));

            Assert.True(plan.Violations.Count == 1 && plan.Violations[0].Code == AnnotationViolationCodes.ReadOnlyRow,
                $"{name}: {string.Join("; ", plan.Violations.Select(v => v.Code))}");
            Assert.Equal(shape.OriginatingAccountId, plan.OriginatingAccountId);
        }
    }

    [Fact]
    public void An_in_kind_transfer_whose_direction_is_reversed_is_written_and_owned_by_its_new_source()
    {
        // The investment editor names the source brokerage; reversing the transfer moves
        // the source marker, and the new source is the only one that may write it.
        var before = TransferShares();
        var after = new TxnShape(null, LedgerActions.TransferShares,
        [
            Leg(0, Brokerage, PostingRoles.Security, investmentSide: true),
            Leg(0, Holdings, PostingRoles.Security, investmentSide: true),
            Leg(1, OtherBrokerage, PostingRoles.Security, investmentSide: true, transferSource: true),
            Leg(1, OtherHoldings, PostingRoles.Security, investmentSide: true),
        ]);
        var change = new ShapeChange<string>(before, Notes(new Notes<string>("move")), after,
            ShapeChange<string>.ByAccountAndRole(before, after));

        var fromNewSource = Valid(AnnotationPlanner.Plan(change, AnnotationIntent<string>.Untouched(OtherBrokerage)));
        var fromOldSource = AnnotationPlanner.Plan(change, AnnotationIntent<string>.Untouched(Brokerage));

        Assert.Equal(OtherBrokerage, fromNewSource.OriginatingAccountId);
        Assert.Equal(new Notes<string>("move"), fromNewSource.Result.Header);
        Assert.Equal(AnnotationViolationCodes.ReadOnlyRow, Assert.Single(fromOldSource.Violations).Code);
    }

    [Fact]
    public void An_event_writes_its_one_posting_value_on_the_brokerage_leg_and_its_multi_posting_value_on_the_header()
    {
        var one = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(XfrToBank(), Annotations<string>.Empty),
            From(Brokerage, Memo("to checking"), Tags("moves"))));
        var many = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(BuyWithFee(), Annotations<string>.Empty),
            From(Brokerage, Memo("monthly buy"), Tags("plan"))));

        Assert.Equal(new Notes<string>("to checking", ["moves"]), one.Result.For(K(0, Brokerage)));
        Assert.True(one.Result.Header.IsEmpty);
        Assert.Equal(new Notes<string>("monthly buy", ["plan"]), many.Result.Header);
        Assert.All(BuyWithFee().Legs, l => Assert.True(many.Result.For(l.Key).IsEmpty));
    }

    [Fact]
    public void An_in_kind_transfer_writes_the_header_from_the_source_brokerage_and_nothing_on_its_four_legs()
    {
        var shape = TransferShares();

        var plan = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, Annotations<string>.Empty),
            From(Brokerage, Memo("consolidate"), Tags("move"))));

        Assert.Equal(new Notes<string>("consolidate", ["move"]), plan.Result.Header);
        Assert.All(shape.Legs, l => Assert.True(plan.Result.For(l.Key).IsEmpty));
        Assert.Equal(Brokerage, plan.OriginatingAccountId);
    }

    // =====================================================================
    // Transitions: a change of shape changes no register's display, except for
    // the lines the user removes
    // =====================================================================

    [Fact]
    public void Splitting_copies_the_sides_value_to_the_header_and_line_one_and_keeps_the_opposite_override()
    {
        var before = OneSplit(Bank, Bank, Groceries);
        var after = new TxnShape(null, null,
            [before.Legs[0], before.Legs[1], Leg(1, Bank), Leg(1, Fuel)]);
        var stored = Notes(null, (K(0, Bank), "trip", ["car"]), (K(0, Groceries), "snacks", ["road"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByLegId(before, after)),
            AnnotationIntent<string>.Untouched(Bank)));

        Assert.Equal(new Notes<string>("trip", ["car"]), plan.Result.Header);
        Assert.Equal(new Notes<string>("trip", ["car"]), plan.Result.For(K(0, Bank)));
        Assert.Equal(new Notes<string>("snacks", ["road"]), plan.Result.For(K(0, Groceries)));
        Assert.True(plan.Result.For(K(1, Bank)).IsEmpty);
        Assert.True(plan.Result.For(K(1, Fuel)).IsEmpty);
        Assert.Equal(Bank, plan.OriginatingAccountId);

        // Nothing either register shows for the original posting changed.
        var shaped = after.WithOriginating(plan.OriginatingAccountId);
        Assert.Equal("trip", MemoTagRules.PresentInMemory(shaped, plan.Result, K(0, Bank)).Presentation.InPlayHeaderMemo);
        Assert.Equal("snacks", MemoTagRules.PresentInMemory(shaped, plan.Result, K(0, Groceries)).Memo);
    }

    [Fact]
    public void Splitting_from_a_side_that_inherits_copies_what_that_side_showed()
    {
        // The value copied is the side's RESOLVED one: its register showed the other
        // side's memo, so the transaction it now owns starts with that memo.
        var before = OneSplit(Bank, Bank, Groceries);
        var after = new TxnShape(null, null,
            [before.Legs[1], before.Legs[0], Leg(1, Groceries), Leg(1, Savings)]);
        var stored = Notes(null, (K(0, Bank), "trip", ["car"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByLegId(before, after)),
            AnnotationIntent<string>.Untouched(Groceries)));

        Assert.Equal(Groceries, plan.OriginatingAccountId);
        Assert.Equal(new Notes<string>("trip", ["car"]), plan.Result.Header);
        Assert.Equal(new Notes<string>("trip", ["car"]), plan.Result.For(K(0, Groceries)));
        Assert.Equal(new Notes<string>("trip", ["car"]), plan.Result.For(K(0, Bank)));
    }

    [Fact]
    public void Splitting_when_the_sides_original_leg_does_not_survive_copies_to_the_header_only()
    {
        var before = OneSplit(Bank, Bank, Groceries);
        var after = new TxnShape(null, null, [Leg(0, Bank), Leg(0, Fuel), Leg(1, Bank), Leg(1, Savings)]);
        var stored = Notes(null, (K(0, Bank), "trip", []));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByLegId(before, after)),
            AnnotationIntent<string>.Untouched(Bank)));

        Assert.Equal(new Notes<string>("trip"), plan.Result.Header);
        Assert.All(after.Legs, l => Assert.True(plan.Result.For(l.Key).IsEmpty));
    }

    [Fact]
    public void Removing_a_line_deletes_only_its_values_and_survivors_keep_theirs_through_a_reorder()
    {
        var before = MultiSplit(Bank, Bank, Groceries, Fuel, Fees);
        // Line 0 is removed; line 2 moves up to posting 0, line 1 stays at 1.
        var after = new TxnShape(null, null,
            [Moved(before.Legs[4], 0), Moved(before.Legs[5], 0), before.Legs[2], before.Legs[3]]);
        var stored = Notes(new Notes<string>("trip", ["car"]),
            (K(0, Bank), "food", []), (K(1, Bank), "diesel", []), (K(2, Fees), "card fee", ["bank"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByLegId(before, after)),
            AnnotationIntent<string>.Untouched(Bank)));

        Assert.Equal(new Notes<string>("trip", ["car"]), plan.Result.Header);
        Assert.Equal(new Notes<string>("card fee", ["bank"]), plan.Result.For(K(0, Fees)));
        Assert.Equal(new Notes<string>("diesel"), plan.Result.For(K(1, Bank)));
        Assert.DoesNotContain(plan.Result.Legs, l => l.Value.Memo == "food");
        Assert.Equal(Bank, plan.OriginatingAccountId);
    }

    [Fact]
    public void Collapsing_joins_header_and_line_memos_unions_the_tags_and_clears_the_header()
    {
        var before = MultiSplit(Bank, Bank, Groceries, Fuel);
        var after = new TxnShape(null, null, [before.Legs[0], before.Legs[1]]);
        var stored = Notes(new Notes<string>("trip", ["car", "shared"]),
            (K(0, Bank), "food", ["shared", "snack"]), (K(0, Groceries), "override", ["category"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByLegId(before, after)),
            AnnotationIntent<string>.Untouched(Bank)));

        Assert.Equal(new Notes<string>("trip · food", ["car", "shared", "snack"]), plan.Result.For(K(0, Bank)));
        Assert.Equal(new Notes<string>("override", ["category"]), plan.Result.For(K(0, Groceries)));
        Assert.True(plan.Result.Header.IsEmpty);
        Assert.Equal(Bank, plan.OriginatingAccountId);
    }

    [Fact]
    public void After_a_collapse_an_opposite_side_with_no_override_inherits_the_collapsed_value()
    {
        // The one-split rule applies as it stands: the collapsed value is not pinned
        // onto the opposite side.
        var before = MultiSplit(Bank, Bank, Groceries, Fuel);
        var after = new TxnShape(null, null, [before.Legs[0], before.Legs[1]]);
        var stored = Notes(new Notes<string>("trip", ["car"]), (K(0, Bank), "trip", []));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByLegId(before, after)),
            AnnotationIntent<string>.Untouched(Bank)));

        // Equal memos join to one.
        Assert.Equal(new Notes<string>("trip", ["car"]), plan.Result.For(K(0, Bank)));
        var category = MemoTagRules.PresentInMemory(after.WithOriginating(Bank), plan.Result, K(0, Groceries));
        Assert.Equal("trip", category.Memo);
        Assert.True(category.Presentation.MemoInherited);
    }

    [Fact]
    public void A_collapsed_tag_set_over_the_limit_saves_unchanged_but_cannot_grow()
    {
        // The editor pre-joins into its fields and sends the result back; its baseline
        // is the transition's result, not the empty set the leg held before.
        var before = MultiSplit(Bank, Bank, Groceries, Fuel);
        var after = new TxnShape(null, null, [before.Legs[0], before.Legs[1]]);
        string[] header = [.. Enumerable.Range(1, 12).Select(i => $"h{i}")];
        string[] line = [.. Enumerable.Range(1, 12).Select(i => $"l{i}")];
        var stored = Notes(new Notes<string>(null, header), (K(0, Bank), null, line));
        var change = new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByLegId(before, after));
        string[] union = [.. header, .. line];

        var unchanged = AnnotationPlanner.Plan(change, From(Bank, tags: Tags(union)));
        var grown = AnnotationPlanner.Plan(change, From(Bank, tags: Tags([.. union, "one-more"])));

        Assert.True(unchanged.IsValid);
        Assert.Equal(24, unchanged.Result.For(K(0, Bank)).Tags.Count);
        Assert.Equal(AnnotationViolationCodes.TagsTooMany, Assert.Single(grown.Violations).Code);
    }

    [Fact]
    public void An_event_gaining_a_second_posting_moves_the_brokerage_legs_value_to_the_header()
    {
        var before = new TxnShape(Brokerage, LedgerActions.Buy,
            [Leg(0, Brokerage, PostingRoles.Security, true), Leg(0, Holdings, PostingRoles.Security, true)]);
        var after = BuyWithFee();
        var stored = Notes(null, (K(0, Brokerage), "monthly buy", ["plan"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByAccountAndRole(before, after)),
            AnnotationIntent<string>.Untouched(Brokerage)));

        Assert.Equal(new Notes<string>("monthly buy", ["plan"]), plan.Result.Header);
        Assert.All(after.Legs, l => Assert.True(plan.Result.For(l.Key).IsEmpty));
    }

    [Fact]
    public void An_event_dropping_to_one_posting_moves_the_header_to_the_brokerage_leg_and_keeps_surviving_overrides()
    {
        var before = BuyXfrFromOtherBrokerage();
        var after = new TxnShape(Brokerage, LedgerActions.Transfer,
            [Leg(0, Brokerage, PostingRoles.Transfer, true), Leg(0, OtherBrokerage, PostingRoles.Transfer)]);
        var stored = Notes(new Notes<string>("rebalance", ["plan"]), (K(1, OtherBrokerage), "from old", ["moves"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByAccountAndRole(before, after)),
            AnnotationIntent<string>.Untouched(Brokerage)));

        Assert.True(plan.Result.Header.IsEmpty);
        Assert.Equal(new Notes<string>("rebalance", ["plan"]), plan.Result.For(K(0, Brokerage)));
        // Same account and role survived, so the override survives.
        Assert.Equal(new Notes<string>("from old", ["moves"]), plan.Result.For(K(0, OtherBrokerage)));
    }

    [Theory]
    [InlineData(LedgerActions.DividendCash)]
    [InlineData(LedgerActions.Misc)]
    public void Changing_a_one_posting_events_action_keeps_its_value_though_the_brokerage_leg_changes_role(string action)
    {
        // The brokerage leg goes from the transfer role to the income role, so it does
        // not survive by (account, role). The event's one value is not an override: it
        // moves to the new editable leg whatever survives. The counterpart is replaced,
        // and its override goes with it.
        var before = XfrToBank();
        var after = new TxnShape(Brokerage, action,
            [Leg(0, Brokerage, PostingRoles.Income, true), Leg(0, Groceries, PostingRoles.Income)]);
        var stored = Notes(null, (K(0, Brokerage), "move", ["moves"]), (K(0, Bank), "incoming", ["in"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByAccountAndRole(before, after)),
            AnnotationIntent<string>.Untouched(Brokerage)));

        Assert.Equal(new Notes<string>("move", ["moves"]), plan.Result.For(K(0, Brokerage)));
        Assert.True(plan.Result.For(K(0, Groceries)).IsEmpty);
        Assert.True(plan.Result.Header.IsEmpty);
        Assert.Equal(Brokerage, plan.OriginatingAccountId);
    }

    [Fact]
    public void Moving_a_one_posting_event_to_another_brokerage_carries_its_value_and_keeps_the_counterparts_override()
    {
        var before = XfrToBank();
        var after = new TxnShape(null, LedgerActions.Transfer,
            [Leg(0, OtherBrokerage, PostingRoles.Transfer, true), Leg(0, Bank, PostingRoles.Transfer)]);
        var stored = Notes(null, (K(0, Brokerage), "move", ["moves"]), (K(0, Bank), "incoming", ["in"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByAccountAndRole(before, after)),
            AnnotationIntent<string>.Untouched(OtherBrokerage)));

        Assert.Equal(OtherBrokerage, plan.OriginatingAccountId);
        Assert.Equal(new Notes<string>("move", ["moves"]), plan.Result.For(K(0, OtherBrokerage)));
        // The counterpart's leg survived (same account and role), so its override did.
        Assert.Equal(new Notes<string>("incoming", ["in"]), plan.Result.For(K(0, Bank)));
        Assert.True(plan.Result.Header.IsEmpty);
    }

    [Fact]
    public void Moving_an_event_to_another_brokerage_while_its_posting_count_changes_puts_its_value_on_the_new_editable_slot()
    {
        // One posting to several, and back, each with a move: the value lands on the
        // header of a multi-posting result and on the new brokerage's leg of a
        // one-posting one.
        var oneLeg = XfrToBank();
        var multi = new TxnShape(null, LedgerActions.BuyXfr,
        [
            Leg(0, OtherBrokerage, PostingRoles.Security, true), Leg(0, OtherHoldings, PostingRoles.Security, true),
            Leg(1, OtherBrokerage, PostingRoles.Transfer, true), Leg(1, Bank, PostingRoles.Transfer),
        ]);
        var oneStored = Notes(null, (K(0, Brokerage), "move", ["moves"]), (K(0, Bank), "incoming", []));
        var toMulti = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(oneLeg, oneStored, multi, ShapeChange<string>.ByAccountAndRole(oneLeg, multi)),
            AnnotationIntent<string>.Untouched(OtherBrokerage)));

        var buy = BuyWithFee();
        var single = new TxnShape(null, LedgerActions.DividendCash,
            [Leg(0, OtherBrokerage, PostingRoles.Income, true), Leg(0, Groceries, PostingRoles.Income)]);
        var buyStored = Notes(new Notes<string>("monthly buy", ["plan"]), (K(1, Fees), "fee side", []));
        var toSingle = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(buy, buyStored, single, ShapeChange<string>.ByAccountAndRole(buy, single)),
            AnnotationIntent<string>.Untouched(OtherBrokerage)));

        Assert.Equal(new Notes<string>("move", ["moves"]), toMulti.Result.Header);
        Assert.All(multi.Legs.Where(l => l.OnInvestmentSide), l => Assert.True(toMulti.Result.For(l.Key).IsEmpty));
        Assert.Equal(new Notes<string>("incoming"), toMulti.Result.For(K(1, Bank)));
        Assert.Equal(new Notes<string>("monthly buy", ["plan"]), toSingle.Result.For(K(0, OtherBrokerage)));
        Assert.True(toSingle.Result.Header.IsEmpty);
    }

    [Fact]
    public void A_one_posting_event_whose_brokerage_inherited_starts_its_multi_posting_header_with_what_it_showed()
    {
        // The brokerage leg had nothing of its own and showed the counterpart's
        // override; the header it gains must show the same, not go blank.
        var before = XfrToBank();
        var after = new TxnShape(null, LedgerActions.BuyXfr,
        [
            Leg(0, Brokerage, PostingRoles.Security, true), Leg(0, Holdings, PostingRoles.Security, true),
            Leg(1, Brokerage, PostingRoles.Transfer, true), Leg(1, Bank, PostingRoles.Transfer),
        ]);
        var stored = Notes(null, (K(0, Bank), "incoming", ["in"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByAccountAndRole(before, after)),
            AnnotationIntent<string>.Untouched(Brokerage)));

        Assert.Equal(new Notes<string>("incoming", ["in"]), plan.Result.Header);
        Assert.Equal(new Notes<string>("incoming", ["in"]), plan.Result.For(K(1, Bank)));
    }

    [Fact]
    public void Reversing_a_one_posting_transfer_between_brokerages_moves_its_value_and_leaves_no_copy_behind()
    {
        // Entered from the brokerage, re-entered from the other one: both legs survive
        // by (account, role), but the old brokerage leg is now the counterpart. The
        // event's value moves to the new editable leg, joined with the override that
        // leg carried, and the counterpart inherits it rather than keeping a copy.
        var before = BrokerageToBrokerage(Brokerage);
        var after = new TxnShape(null, LedgerActions.Transfer,
            [Leg(0, OtherBrokerage, PostingRoles.Transfer, true), Leg(0, Brokerage, PostingRoles.Transfer)]);
        var stored = Notes(null, (K(0, Brokerage), "move", ["moves"]), (K(0, OtherBrokerage), "arriving", ["in"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByAccountAndRole(before, after)),
            AnnotationIntent<string>.Untouched(OtherBrokerage)));

        Assert.Equal(OtherBrokerage, plan.OriginatingAccountId);
        Assert.Equal(new Notes<string>("move · arriving", ["moves", "in"]), plan.Result.For(K(0, OtherBrokerage)));
        Assert.True(plan.Result.For(K(0, Brokerage)).IsEmpty);
    }

    [Fact]
    public void An_override_whose_leg_does_not_survive_an_investment_edit_goes_with_it()
    {
        var before = XfrToBank();
        var after = new TxnShape(Brokerage, LedgerActions.Transfer,
            [Leg(0, Brokerage, PostingRoles.Transfer, true), Leg(0, Savings, PostingRoles.Transfer)]);
        var stored = Notes(null, (K(0, Brokerage), "move", []), (K(0, Bank), "incoming", []));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByAccountAndRole(before, after)),
            AnnotationIntent<string>.Untouched(Brokerage)));

        Assert.Equal(new Notes<string>("move"), plan.Result.For(K(0, Brokerage)));
        Assert.True(plan.Result.For(K(0, Savings)).IsEmpty);
    }

    [Fact]
    public void Turning_a_bank_multi_split_into_an_event_folds_investment_side_line_values_into_the_header()
    {
        // The brokerage lines become investment-side legs, which carry nothing on a
        // multi-posting event; their values are merged onto the editable side rather
        // than dropped: nothing is ever dropped.
        var before = MultiSplit(Brokerage, Brokerage, Groceries, Fees);
        var after = new TxnShape(null, LedgerActions.Misc,
            [Leg(0, Brokerage, PostingRoles.Income, true), Leg(0, Groceries, PostingRoles.Income),
             Leg(1, Brokerage, PostingRoles.Fee, true), Leg(1, Fees, PostingRoles.Fee)]);
        var stored = Notes(new Notes<string>("adjust", ["a"]),
            (K(0, Brokerage), "first", ["b"]), (K(1, Brokerage), "second", ["a", "c"]), (K(1, Fees), "fee side", []));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByAccountAndRole(before, after)),
            AnnotationIntent<string>.Untouched(Brokerage)));

        Assert.Equal(new Notes<string>("adjust · first · second", ["a", "b", "c"]), plan.Result.Header);
        Assert.True(plan.Result.For(K(0, Brokerage)).IsEmpty);
        Assert.True(plan.Result.For(K(1, Brokerage)).IsEmpty);
        Assert.Equal(new Notes<string>("fee side"), plan.Result.For(K(1, Fees)));
    }

    [Fact]
    public void Folding_investment_side_values_into_the_header_keeps_each_distinct_memo_once()
    {
        // The event rule (ADR-0108 D11): the event's memo is every distinct memo once, not once per pair; a fold
        // that compared only neighbours would keep "a · b · a".
        var before = MultiSplit(Brokerage, Brokerage, Groceries, Fees);
        var after = new TxnShape(null, LedgerActions.Misc,
            [Leg(0, Brokerage, PostingRoles.Income, true), Leg(0, Groceries, PostingRoles.Income),
             Leg(1, Brokerage, PostingRoles.Fee, true), Leg(1, Fees, PostingRoles.Fee)]);
        var stored = Notes(new Notes<string>("a", ["x"]),
            (K(0, Brokerage), "b", ["y"]), (K(1, Brokerage), "a", ["x", "z"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByAccountAndRole(before, after)),
            AnnotationIntent<string>.Untouched(Brokerage)));

        Assert.Equal(new Notes<string>("a · b", ["x", "y", "z"]), plan.Result.Header);
        Assert.True(plan.Result.For(K(0, Brokerage)).IsEmpty);
        Assert.True(plan.Result.For(K(1, Brokerage)).IsEmpty);
    }

    [Fact]
    public void Collapsing_a_split_into_a_one_posting_event_keeps_each_distinct_memo_once_on_the_brokerage_leg()
    {
        // The collapse joins the header onto the brokerage leg; the holdings leg, now on
        // the investment side, then joins too. All three are one join (the event rule, ADR-0108 D11), so the
        // header's memo the holdings leg repeats is kept once.
        var before = MultiSplit(Brokerage, Brokerage, Holdings, Fees);
        var after = OnePostingBuy();
        var stored = Notes(new Notes<string>("a", ["x"]),
            (K(0, Brokerage), "b", ["y"]), (K(0, Holdings), "a", ["z"]));

        var plan = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, stored, after, ShapeChange<string>.ByAccountAndRole(before, after)),
            AnnotationIntent<string>.Untouched(Brokerage)));

        Assert.Equal(new Notes<string>("a · b", ["x", "y", "z"]), plan.Result.For(K(0, Brokerage)));
        Assert.True(plan.Result.For(K(0, Holdings)).IsEmpty);
        Assert.True(plan.Result.Header.IsEmpty);
    }

    // =====================================================================
    // Normalisation (trimmed; blank means no value) and idempotence
    // =====================================================================

    [Fact]
    public void A_blank_memo_is_never_stored_as_an_own_value_and_blank_tag_names_are_dropped()
    {
        var shape = OneSplit(Bank, Bank, Groceries);
        var stored = Notes(null, (K(0, Groceries), "  ", []), (K(0, Bank), " ", []));

        var plan = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(shape, stored),
            From(Bank, Memo("\t "), Tags(" ", "a", "a", "　"))));

        Assert.Equal(new Notes<string>(null, ["a"]), plan.Result.For(K(0, Bank)));
        // A stored blank is normalised on the way through too.
        Assert.Null(plan.Result.For(K(0, Groceries)).Memo);
    }

    [Fact]
    public void Every_memo_the_planner_stores_is_trimmed_whichever_path_wrote_it()
    {
        // An edit, a collapse of a header-only memo and a clone or import that places
        // its own values all store the same bytes for the same text.
        var oneSplit = OneSplit(Bank, Bank, Groceries);
        var before = MultiSplit(Bank, Bank, Groceries, Fuel);
        var collapsed = new TxnShape(null, null, [before.Legs[0], before.Legs[1]]);

        var edit = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(oneSplit, Annotations<string>.Empty),
            From(Bank, Memo(" coffee\t"))));
        var collapse = Valid(AnnotationPlanner.Plan(
            new ShapeChange<string>(before, Notes(new Notes<string>(" x ")), collapsed, ShapeChange<string>.ByLegId(before, collapsed)),
            AnnotationIntent<string>.Untouched(Bank)));
        var line = Valid(AnnotationPlanner.Plan(ShapeChange<string>.Unchanged(before, Annotations<string>.Empty),
            From(Bank, Memo("  trip"), lines: new LineIntent<string>(1, Memo("diesel  "), LeaveTags))));
        var clone = Valid(AnnotationPlanner.Seed(before,
            Notes(new Notes<string>(" rent "), (K(1, Fuel), " opposite", []))));

        Assert.Equal("coffee", edit.Result.For(K(0, Bank)).Memo);
        Assert.Equal("x", collapse.Result.For(K(0, Bank)).Memo);
        Assert.Equal("trip", line.Result.Header.Memo);
        Assert.Equal("diesel", line.Result.For(K(1, Bank)).Memo);
        Assert.Equal("rent", clone.Result.Header.Memo);
        Assert.Equal("opposite", clone.Result.For(K(1, Fuel)).Memo);
    }

    [Fact]
    public void Planning_the_same_write_again_on_its_own_result_changes_nothing()
    {
        var oneSplit = OneSplit(Bank, Bank, Groceries);
        var before = MultiSplit(Bank, Bank, Groceries, Fuel);
        var collapsed = new TxnShape(null, null, [before.Legs[0], before.Legs[1]]);
        var stored = Notes(new Notes<string>("trip", ["car"]), (K(0, Bank), "food", ["snack"]));

        var cases = new (ShapeChange<string> Change, AnnotationIntent<string> Intent)[]
        {
            (ShapeChange<string>.Unchanged(oneSplit, Annotations<string>.Empty), From(Groceries, Memo("beans"), Tags("home"))),
            (new ShapeChange<string>(before, stored, collapsed, ShapeChange<string>.ByLegId(before, collapsed)), AnnotationIntent<string>.Untouched(Bank)),
            (ShapeChange<string>.Unchanged(before, stored), From(Bank, Memo("road trip"), lines: new LineIntent<string>(1, Memo("diesel"), Tags("x")))),
        };

        foreach (var (change, intent) in cases)
        {
            var first = Valid(AnnotationPlanner.Plan(change, intent));
            var again = Valid(AnnotationPlanner.Plan(
                ShapeChange<string>.Unchanged(change.After.WithOriginating(first.OriginatingAccountId), first.Result), intent));

            Assert.Equal(first.Result, again.Result);
            Assert.Equal(first.OriginatingAccountId, again.OriginatingAccountId);
        }
    }

    // =====================================================================
    // Seeding a new transaction, and cloning one
    // =====================================================================

    [Fact]
    public void A_new_transaction_is_owned_by_the_side_it_is_entered_from()
    {
        var shape = new TxnShape(null, null, OneSplit(Bank, Bank, Groceries).Legs);

        var plan = Valid(AnnotationPlanner.Seed(shape, From(Groceries, Memo("beans"))));

        Assert.Equal(Groceries, plan.OriginatingAccountId);
        Assert.Equal(new Notes<string>("beans"), plan.Result.For(K(0, Groceries)));
    }

    [Fact]
    public void A_new_multi_split_from_a_side_not_on_every_line_is_refused()
    {
        var shape = new TxnShape(null, null, MultiSplit(Bank, Bank, Groceries, Fuel).Legs);

        var plan = AnnotationPlanner.Seed(shape, From(Groceries, Memo("x")));

        Assert.Equal(AnnotationViolationCodes.SideNotOnTransaction, Assert.Single(plan.Violations).Code);
    }

    [Fact]
    public void A_clone_keeps_every_value_uncapped_and_normalises_blanks()
    {
        var shape = MultiSplit(Bank, Bank, Groceries, Fuel);
        string[] many = [.. Enumerable.Range(1, 30).Select(i => $"t{i}")];
        var values = Notes(new Notes<string>("rent", many), (K(1, Bank), " ", ["x"]), (K(1, Fuel), "opposite", []));

        var plan = Valid(AnnotationPlanner.Seed(shape, values));

        Assert.Equal(new Notes<string>("rent", many), plan.Result.Header);
        Assert.Equal(new Notes<string>(null, ["x"]), plan.Result.For(K(1, Bank)));
        Assert.Equal(new Notes<string>("opposite"), plan.Result.For(K(1, Fuel)));
        Assert.Equal(Bank, plan.OriginatingAccountId);
    }

    [Fact]
    public void A_clone_that_breaks_an_invariant_fails_closed()
    {
        // A one-split never has header notes; an actioned multi-posting event never
        // has values on its investment side. A mapper that produced either is a bug.
        var oneSplit = OneSplit(Bank, Bank, Groceries);
        var buy = BuyWithFee();

        Assert.Throws<ArgumentException>(() =>
            AnnotationPlanner.Seed(oneSplit, new Annotations<string>(new Notes<string>("header"))));
        Assert.Throws<ArgumentException>(() =>
            AnnotationPlanner.Seed(buy, Notes(null, (K(0, Holdings), "stale", []))));
        Assert.Throws<ArgumentException>(() =>
            AnnotationPlanner.Seed(new TxnShape(Savings, null, oneSplit.Legs), Annotations<string>.Empty));
        // Ownership: an event belongs to the brokerage on every posting,
        // and a multi-split with an account on every line to one of those accounts.
        Assert.Throws<ArgumentException>(() =>
            AnnotationPlanner.Seed(new TxnShape(Holdings, buy.Action, buy.Legs), Annotations<string>.Empty));
        Assert.Throws<ArgumentException>(() =>
            AnnotationPlanner.Seed(MultiSplit(Groceries, Bank, Groceries, Fuel), Annotations<string>.Empty));
        Assert.Throws<ArgumentException>(() =>
            AnnotationPlanner.Seed(new TxnShape(Fees, buy.Action, buy.Legs), Annotations<string>.Empty));
        // The account on every posting of an event must also be a brokerage: a
        // holdings account is on every posting of a buy with no fee.
        var onePostingBuy = OnePostingBuy();
        Assert.Throws<ArgumentException>(() =>
            AnnotationPlanner.Seed(new TxnShape(Holdings, onePostingBuy.Action, onePostingBuy.Legs), Annotations<string>.Empty));
        // An in-kind transfer belongs to its source brokerage, and only to it.
        var transfer = TransferShares();
        Assert.Throws<ArgumentException>(() =>
            AnnotationPlanner.Seed(new TxnShape(OtherBrokerage, transfer.Action, transfer.Legs), Annotations<string>.Empty));
        Assert.Throws<ArgumentException>(() =>
            AnnotationPlanner.Seed(new TxnShape(Holdings, transfer.Action, transfer.Legs), Annotations<string>.Empty));
        var unmarked = transfer.Legs.Select(l => l with { IsTransferSource = false }).ToList();
        Assert.Throws<ArgumentException>(() =>
            AnnotationPlanner.Seed(new TxnShape(Brokerage, transfer.Action, unmarked), Annotations<string>.Empty));
        Assert.True(AnnotationPlanner.Seed(transfer, Annotations<string>.Empty).IsValid);
    }

    // =====================================================================
    // Convert in-kind
    // =====================================================================

    /// <summary>A sell in the brokerage: the one-posting form, or with a fee.</summary>
    private static TxnShape Sell(bool withFee) => new(Brokerage, LedgerActions.Sell,
    [
        Leg(0, Brokerage, PostingRoles.Security, true), Leg(0, Holdings, PostingRoles.Security, true),
        .. withFee ? new[] { Leg(1, Brokerage, PostingRoles.Fee, true), Leg(1, Fees, PostingRoles.Fee) } : [],
    ]);

    /// <summary>The matching buy in the other brokerage, one posting.</summary>
    private static TxnShape BuyInOther() => new(OtherBrokerage, LedgerActions.Buy,
        [Leg(0, OtherBrokerage, PostingRoles.Security, true), Leg(0, OtherHoldings, PostingRoles.Security, true)]);

    [Fact]
    public void Converting_a_sell_and_a_buy_in_kind_joins_their_memos_and_unions_their_tags_on_the_transfer()
    {
        var sellNotes = Notes(new Notes<string>("sold", ["a", "b"]), (K(1, Fees), "fee side", ["fee"]));
        var buyNotes = Notes(null, (K(0, OtherBrokerage), "bought", ["b", "c"]));

        var plan = Valid(AnnotationPlanner.ConvertInKind(Sell(withFee: true), sellNotes, BuyInOther(), buyNotes, TransferShares()));

        Assert.Equal(new Notes<string>("sold · bought", ["a", "b", "c"]), plan.Result.Header);
        Assert.All(TransferShares().Legs, l => Assert.True(plan.Result.For(l.Key).IsEmpty));
        Assert.Equal(Brokerage, plan.OriginatingAccountId);
    }

    [Fact]
    public void An_in_kind_conversion_is_never_capped_and_equal_memos_join_to_one()
    {
        string[] sellTags = [.. Enumerable.Range(1, 15).Select(i => $"s{i}")];
        string[] buyTags = [.. Enumerable.Range(1, 15).Select(i => $"b{i}")];
        var sellNotes = Notes(null, (K(0, Brokerage), "rebalance", sellTags));
        var buyNotes = Notes(null, (K(0, OtherBrokerage), " rebalance", buyTags));

        var plan = Valid(AnnotationPlanner.ConvertInKind(Sell(withFee: false), sellNotes, BuyInOther(), buyNotes, TransferShares()));

        Assert.Equal("rebalance", plan.Result.Header.Memo);
        Assert.Equal([.. sellTags, .. buyTags], plan.Result.Header.Tags);
    }

    [Fact]
    public void An_in_kind_conversion_must_produce_an_in_kind_transfer_from_two_events()
    {
        Assert.Throws<ArgumentException>(() => AnnotationPlanner.ConvertInKind(
            Sell(false), Annotations<string>.Empty, BuyInOther(), Annotations<string>.Empty, BuyWithFee()));
        Assert.Throws<ArgumentException>(() => AnnotationPlanner.ConvertInKind(
            OneSplit(Bank, Bank, Groceries), Annotations<string>.Empty, BuyInOther(), Annotations<string>.Empty, TransferShares()));
        // The transfer must belong to its source brokerage.
        var transfer = TransferShares();
        Assert.Throws<InvalidOperationException>(() => AnnotationPlanner.ConvertInKind(
            Sell(false), Annotations<string>.Empty, BuyInOther(), Annotations<string>.Empty,
            new TxnShape(OtherBrokerage, transfer.Action, transfer.Legs)));
    }

    // =====================================================================
    // Survivors
    // =====================================================================

    [Fact]
    public void Survivors_match_by_stored_id_or_by_account_and_role_in_posting_order()
    {
        var keep = Guid.NewGuid();
        var before = new TxnShape(Bank, null, [Leg(0, Bank, legId: keep), Leg(0, Groceries)]);
        var after = new TxnShape(Bank, null, [Leg(3, Bank, legId: keep), Leg(3, Fuel)]);

        var byId = ShapeChange<string>.ByLegId(before, after);

        Assert.Equal(K(0, Bank), Assert.Single(byId, p => p.Key == K(3, Bank)).Value);
        Assert.Single(byId);

        var transfer = TransferShares();
        var lots = new TxnShape(Brokerage, LedgerActions.TransferShares,
        [
            Leg(0, Brokerage, PostingRoles.Security, true), Leg(0, Holdings, PostingRoles.Security, true),
            Leg(1, Brokerage, PostingRoles.Security, true), Leg(1, Holdings, PostingRoles.Security, true),
        ]);
        var byRole = ShapeChange<string>.ByAccountAndRole(transfer, lots);
        Assert.Equal(K(0, Brokerage), byRole[K(0, Brokerage)]);
        Assert.False(byRole.ContainsKey(K(1, Brokerage)));

        // A bank-shape leg has no role yet; turning it into an event keeps it by account.
        var bankShape = OneSplit(Bank, Bank, Brokerage);
        var byAccount = ShapeChange<string>.ByAccountAndRole(bankShape, XfrToBank());
        Assert.Equal(K(0, Brokerage), byAccount[K(0, Brokerage)]);
        Assert.Equal(K(0, Bank), byAccount[K(0, Bank)]);
    }

    private static ShapeLeg Moved(ShapeLeg leg, int posting) => leg with { Key = leg.Key with { PostingIndex = posting } };
}
