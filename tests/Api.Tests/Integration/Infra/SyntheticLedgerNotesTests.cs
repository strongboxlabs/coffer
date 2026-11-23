using Microsoft.EntityFrameworkCore;

using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The side-aware seed helpers store what the write primitive plans, and only
/// that: a fixture built on them holds a state a writer can produce.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SyntheticLedgerNotesTests : DatabaseTest
{
    public SyntheticLedgerNotesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime Day = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_one_split_note_lands_on_the_writing_sides_own_leg_and_never_the_header()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var (bankLeg, categoryLeg) = await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -12m, Day);

        await ledger.SetSideMemoAsync(categoryLeg, groceries.Id, "beans", originatingAccountId: bank.Id);
        var afterMemo = await ledger.LoadNotesAsync(bankLeg);
        await ledger.SetSideTagsAsync(categoryLeg, groceries.Id, ["home", "home", " "], originatingAccountId: bank.Id);
        var notes = await ledger.LoadNotesAsync(bankLeg);

        Assert.True(afterMemo.Header.IsEmpty);
        Assert.Equal(new Notes<string>("beans"), afterMemo.For(new LegKey(0, groceries.Id)));
        Assert.True(notes.Header.IsEmpty);
        Assert.True(notes.For(new LegKey(0, bank.Id)).IsEmpty);
        Assert.Equal(new Notes<string>("beans", ["home"]), notes.For(new LegKey(0, groceries.Id)));
    }

    [Fact]
    public async Task A_multi_split_note_from_the_split_side_lands_on_the_header_and_its_lines()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var fuel = await ledger.AddCategoryAsync("Fuel");
        var (_, headerId) = await ledger.AddMultiSplitAsync(bank.Id, [(groceries.Id, -10m), (fuel.Id, -20m)], Day);

        await ledger.WriteNotesAsync(headerId,
            new AnnotationIntent<string>(bank.Id, FieldEdit<string>.Set("trip"), FieldEdit<IReadOnlyList<string>>.Set(["car"]),
                [new LineIntent<string>(1, FieldEdit<string>.Set("diesel"), FieldEdit<IReadOnlyList<string>>.Leave)]),
            originatingAccountId: bank.Id);
        var notes = await ledger.LoadNotesAsync(headerId);

        Assert.Equal(new Notes<string>("trip", ["car"]), notes.Header);
        Assert.Equal(new Notes<string>("diesel"), notes.For(new LegKey(1, bank.Id)));
        Assert.True(notes.For(new LegKey(1, fuel.Id)).IsEmpty);
        Assert.True(notes.For(new LegKey(0, bank.Id)).IsEmpty);
    }

    [Fact]
    public async Task A_tag_name_resolves_to_the_existing_tag_ignoring_case_and_never_creates_a_case_variant()
    {
        // Tag names are case-insensitive everywhere, so a seed that created "work"
        // beside "Work" would hold a state no writer can produce.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var (bankLeg, _) = await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -12m, Day);
        await ledger.AddBareTagAsync("Work");

        await ledger.SetSideTagsAsync(bankLeg, bank.Id, ["work", " WORK ", "Home", "home"], originatingAccountId: bank.Id);
        var notes = await ledger.LoadNotesAsync(bankLeg);
        await using var db = ledger.NewDbContext();
        var names = await db.Tags.AsNoTracking()
            .Where(t => t.LedgerId == ledger.LedgerId)
            .Select(t => t.Name)
            .ToListAsync();

        Assert.Equal(["Home", "Work"], names.Order(StringComparer.Ordinal));
        Assert.Equal(new Notes<string>(null, ["Home", "Work"]), notes.For(new LegKey(0, bank.Id)));
    }

    [Fact]
    public async Task A_write_from_a_read_only_side_is_refused_and_stores_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var fuel = await ledger.AddCategoryAsync("Fuel");
        var (_, headerId) = await ledger.AddMultiSplitAsync(bank.Id, [(groceries.Id, -10m), (fuel.Id, -20m)], Day);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.SetSideMemoAsync(headerId, groceries.Id, "override", originatingAccountId: bank.Id));
        var notes = await ledger.LoadNotesAsync(headerId);

        Assert.Contains(AnnotationViolationCodes.ReadOnlyRow, refused.Message, StringComparison.Ordinal);
        Assert.Equal(Annotations<string>.Empty, notes);
    }

    [Fact]
    public async Task An_in_kind_transfer_takes_notes_from_its_source_brokerage_and_refuses_its_destination()
    {
        // The helper decides the transfer's source from the stored legs and hands it
        // to the planner; only that brokerage owns the event's memo and tags.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source brokerage");
        var dest = await ledger.AddInvestmentAccountAsync("Destination brokerage");
        var security = await ledger.AddSecurityAsync("Growth", "GRW");
        var headerId = await ledger.AddTransferSharesAsync(
            source.Id, source.HoldingsAccountId!.Value,
            dest.Id, dest.HoldingsAccountId!.Value,
            security, 4m, 25m, Day);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.SetSideTagsAsync(headerId, dest.Id, ["moved"], originatingAccountId: dest.Id));
        var afterRefusal = await ledger.LoadNotesAsync(headerId);
        await ledger.SetSideTagsAsync(headerId, source.Id, ["moved"], originatingAccountId: source.Id);
        var notes = await ledger.LoadNotesAsync(headerId);

        Assert.StartsWith("The write primitive refused this seed", refused.Message, StringComparison.Ordinal);
        Assert.Contains(AnnotationViolationCodes.ReadOnlyRow, refused.Message, StringComparison.Ordinal);
        Assert.Equal(Annotations<string>.Empty, afterRefusal);
        Assert.Equal(new Notes<string>(null, ["moved"]), notes.For(new LegKey(0, source.Id)));
        Assert.True(notes.Header.IsEmpty);
        Assert.True(notes.For(new LegKey(0, dest.Id)).IsEmpty);
    }
}

/// <summary>
/// The in-kind transfer source the seed helpers hand the planner: strict pairing in
/// one posting first, the holdings-account fallback only when nothing pairs.
/// </summary>
public sealed class SyntheticLedgerTransferSourceTests
{
    private static readonly Guid Source = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid SourceHoldings = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Dest = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid DestHoldings = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    [Fact]
    public void The_source_is_the_brokerage_paired_with_the_shares_leaving_its_holdings()
    {
        var holdingsOf = new Dictionary<Guid, Guid> { [Source] = SourceHoldings, [Dest] = DestHoldings };

        var sources = SyntheticLedger.TransferSources(
            [(0, SourceHoldings, -5m), (0, Source, null), (1, DestHoldings, 5m), (1, Dest, null)], holdingsOf);

        Assert.Equal([Source], sources);
    }

    [Fact]
    public void Two_brokerages_sharing_one_holdings_account_are_told_apart_by_the_posting()
    {
        var holdingsOf = new Dictionary<Guid, Guid> { [Source] = SourceHoldings, [Dest] = SourceHoldings };

        var sources = SyntheticLedger.TransferSources(
            [(0, SourceHoldings, -5m), (0, Source, null), (1, SourceHoldings, 5m), (1, Dest, null)], holdingsOf);

        Assert.Equal([Source], sources);
    }

    [Fact]
    public void With_no_pairing_in_one_posting_the_holdings_account_carrying_the_outflow_decides()
    {
        var holdingsOf = new Dictionary<Guid, Guid> { [Source] = SourceHoldings, [Dest] = DestHoldings };

        var sources = SyntheticLedger.TransferSources(
            [(0, SourceHoldings, -5m), (0, Dest, null), (1, DestHoldings, 5m), (1, Source, null)], holdingsOf);

        Assert.Equal([Source], sources);
    }
}
