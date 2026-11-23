using Coffer.Domain.Annotations;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Mappers;

/// <summary>
/// The importer's adapter onto the write primitive: the originating account it stamps
/// (the home account when it is on a leg, NULL for a header with no legs, a stopped
/// import otherwise), and a plan it refuses to write.
/// </summary>
public sealed class ImportAnnotationsTests
{
    private static readonly AccountRef Home = new(Guid.NewGuid(), "bank", AmountDecimals: 2);
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid Third = Guid.NewGuid();

    private static TxnLegRow Leg(Guid accountId, int posting = 0, string? memo = null) => new(
        Id: Guid.NewGuid(), HeaderId: Guid.NewGuid(), LedgerId: Guid.NewGuid(), AccountId: accountId,
        PostingIndex: posting, LegMemo: memo, Amount: 1m, SecurityId: null, Quantity: null, UnitPrice: null);

    private static TxnHeaderRow Header(string externalId, string? memo = null) => new(
        Id: Guid.NewGuid(), LedgerId: Guid.NewGuid(), Origin: "manual", ExternalId: externalId,
        Payee: null, Memo: memo, PostedAt: DateTimeOffset.UnixEpoch, TransactedAt: null, Status: "uncleared",
        CheckNumber: null, IsPending: false, IsHidden: false, IsMergedInto: null, ImportSource: "test",
        ClearedAt: null, ClearedByUserId: null, OnlineMatchFitid: null, OnlineMatchFiId: null,
        Action: null, OriginatingAccountId: null);

    private static StampedNotes Seeded(IReadOnlyList<TxnLegRow> legs, string externalId, Guid side)
    {
        var shape = ImportAnnotations.ShapeOf(Home, action: null, legs);
        var plan = AnnotationPlanner.Seed(shape, new AnnotationIntent<string>(
            side, FieldEdit<string>.Set("memo"), FieldEdit<IReadOnlyList<string>>.Set(["tag"])), TagLimit.SystemMerge);
        return ImportAnnotations.Stamp(plan, Header(externalId), legs);
    }

    [Fact]
    public void The_home_on_a_leg_is_the_owner_and_holds_a_one_splits_values()
    {
        var home = Leg(Home.Id);
        var other = Leg(Other);

        var stamped = Seeded([home, other], "t-1", Home.Id);

        Assert.Equal(Home.Id, stamped.Header.OriginatingAccountId);
        Assert.Null(stamped.Header.Memo);
        Assert.Empty(stamped.HeaderTags);
        Assert.Equal("memo", stamped.Legs.Single(l => l.Id == home.Id).LegMemo);
        Assert.Null(stamped.Legs.Single(l => l.Id == other.Id).LegMemo);
        Assert.Equal(["tag"], Assert.Single(stamped.LegTags, s => s.LegId == home.Id).Tags);
        Assert.Equal("memo", stamped.TransactionMemo);
    }

    [Fact]
    public void A_header_without_legs_has_no_owner_and_no_notes()
    {
        var plan = AnnotationPlanner.Seed(ImportAnnotations.ShapeOf(Home, null, []), Annotations<string>.Empty);

        var stamped = ImportAnnotations.Stamp(plan, Header("t-2", memo: "stale"), []);

        Assert.Null(stamped.Header.OriginatingAccountId);
        Assert.Null(stamped.Header.Memo);
    }

    [Fact]
    public void A_home_on_none_of_the_legs_stops_the_import()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Seeded([Leg(Other), Leg(Third)], "t-3", Home.Id));
        Assert.Contains("t-3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_naming_an_owner_off_the_legs_is_never_stamped()
    {
        var legs = new[] { Leg(Other), Leg(Third) };
        var plan = new AnnotationPlan<string>(Annotations<string>.Empty, Home.Id, []);

        var ex = Assert.Throws<InvalidOperationException>(() => ImportAnnotations.Stamp(plan, Header("t-4"), legs));
        Assert.Contains("t-4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_multi_posting_transactions_memo_is_its_headers()
    {
        var stamped = Seeded([Leg(Home.Id, 0), Leg(Other, 0), Leg(Home.Id, 1), Leg(Third, 1)], "t-5", Home.Id);

        Assert.Equal("memo", stamped.Header.Memo);
        Assert.Equal("memo", stamped.TransactionMemo);
        Assert.All(stamped.Legs, l => Assert.Null(l.LegMemo));
    }

    [Fact]
    public void An_event_is_owned_by_its_brokerage_and_its_investment_side_holds_nothing()
    {
        var holdings = Guid.NewGuid();
        var brokerage = new AccountRef(Guid.NewGuid(), "investment", HoldingsAccountId: holdings, AmountDecimals: 2);
        var legs = new[] { Leg(brokerage.Id, 0), Leg(holdings, 0), Leg(brokerage.Id, 1), Leg(Other, 1) };
        var shape = ImportAnnotations.ShapeOf(brokerage, "buy", legs);

        Assert.True(shape.Legs.Single(l => l.Key == new LegKey(0, brokerage.Id)).IsBrokerage);
        Assert.True(shape.Legs.Single(l => l.Key == new LegKey(0, holdings)).OnInvestmentSide);
        Assert.False(shape.Legs.Single(l => l.Key == new LegKey(0, holdings)).IsBrokerage);
        Assert.False(shape.Legs.Single(l => l.Key == new LegKey(1, Other)).OnInvestmentSide);

        // The planner refuses a value on the investment side of a multi-posting event.
        var wrong = new Annotations<string>(null, new Dictionary<LegKey, Notes<string>>
        {
            [new LegKey(0, holdings)] = new("misplaced"),
        });
        Assert.Throws<ArgumentException>(() => AnnotationPlanner.Seed(shape.WithOriginating(brokerage.Id), wrong));
    }
}
