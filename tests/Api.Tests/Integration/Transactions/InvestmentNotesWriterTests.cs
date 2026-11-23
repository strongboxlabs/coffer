using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The investment create, PATCH and convert in-kind write an event's one memo and
/// tag set through the write primitive: on the brokerage leg of a one-posting event,
/// on the header of a multi-posting one, never on its investment side; the event's
/// brokerage owns it, the source brokerage for an in-kind transfer.
/// </summary>
/// <remarks>
/// The register still reads through the old view until the readers move, so these
/// assert the STORED state and what each account's register shows under the one
/// presentation rule (<see cref="SyntheticLedger.ShownForAsync"/>).
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InvestmentNotesWriterTests : DatabaseTest
{
    public InvestmentNotesWriterTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime Day1 = new(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        SyntheticLedger Ledger, ApiFactory Factory, HttpClient Client,
        Guid Brokerage, Guid Holdings, Guid OtherBrokerage, Guid OtherHoldings,
        Guid Checking, Guid Fees, Guid Security) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private async Task<World> WorldAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var other = await ledger.AddInvestmentAccountAsync("Second brokerage");
        var checking = await ledger.AddBankAccountAsync("Checking");
        var fees = await ledger.AddCategoryAsync("Investment Fees");
        var security = await ledger.AddSecurityAsync("Index Fund", ticker: "IDXF");
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return new World(ledger, factory, client, brokerage.Id, brokerage.HoldingsAccountId!.Value,
            other.Id, other.HoldingsAccountId!.Value, checking.Id, fees.Id, security);
    }

    private static async Task<Guid> CreateAsync(World w, CreateInvestmentTransactionRequest body)
    {
        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions", body);
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static Task<HttpResponseMessage> PatchJsonAsync(World w, Guid headerId, string json) =>
        w.Client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions/{headerId}")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

    private static CreateInvestmentTransactionRequest Buy(World w, DateTime at, string? memo = null, IReadOnlyList<string>? tags = null, bool withFee = false) => new()
    {
        BrokerageAccountId = w.Brokerage,
        PostedAt = at,
        Action = "buy",
        SecurityId = w.Security,
        Shares = 10m,
        Price = 10m,
        Amount = 100m,
        FeeAccountId = withFee ? w.Fees : null,
        FeeAmount = withFee ? 2m : null,
        Memo = memo,
        Tags = tags,
    };

    private async Task<Guid?> OwnerAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking().Where(h => h.Id == headerId).Select(h => h.OriginatingAccountId).SingleAsync();
    }

    private static IEnumerable<Notes<string>> On(Annotations<string> notes, Guid account) =>
        notes.Legs.Where(l => l.Key.AccountId == account).Select(l => l.Value);

    private Task AssertInvariantsAsync(World w) => OriginatingAccountCheck.AssertHoldsAsync(Db, w.Ledger.LedgerId);

    [Fact]
    public async Task A_one_posting_event_keeps_its_notes_on_the_brokerage_leg_and_its_counterpart_inherits()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = w.Brokerage, PostedAt = Day1, Action = "transfer",
            TransferAccountId = w.Checking, Amount = 500m, Memo = "funding", Tags = ["ira"],
        });

        var notes = await w.Ledger.LoadNotesAsync(id);
        Assert.True(notes.Header.IsEmpty);
        Assert.Equal(new Notes<string>("funding", ["ira"]), Assert.Single(On(notes, w.Brokerage)));
        Assert.All(On(notes, w.Checking), n => Assert.True(n.IsEmpty));
        Assert.Equal(w.Brokerage, await OwnerAsync(id));

        var fromChecking = Assert.Single(await w.Ledger.ShownForAsync(id, w.Checking));
        Assert.Equal(LegKind.EventCounterpartOne, fromChecking.Presentation.Kind);
        Assert.True(fromChecking.Presentation.IsReadOnly);
        Assert.Equal(("funding", true), (fromChecking.Memo, fromChecking.Presentation.MemoInherited));
        Assert.Equal(["ira"], fromChecking.Tags);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task A_multi_posting_event_keeps_its_notes_on_the_header_and_none_on_its_investment_side()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, Buy(w, Day1, "first lot", ["long"], withFee: true));

        var notes = await w.Ledger.LoadNotesAsync(id);
        Assert.Equal(new Notes<string>("first lot", ["long"]), notes.Header);
        Assert.All(notes.Legs.Values, n => Assert.True(n.IsEmpty));
        Assert.Equal(w.Brokerage, await OwnerAsync(id));

        var fromFees = Assert.Single(await w.Ledger.ShownForAsync(id, w.Fees));
        Assert.Equal(LegKind.EventOpposite, fromFees.Presentation.Kind);
        Assert.Equal(("first lot", true), (fromFees.Memo, fromFees.Presentation.MemoInherited));
        Assert.All(await w.Ledger.ShownForAsync(id, w.Brokerage), s => Assert.Equal("first lot", s.Memo));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task A_buy_funded_from_another_brokerage_is_owned_by_its_own_and_read_only_from_the_funder()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = w.Brokerage, PostedAt = Day1, Action = "buyx", SecurityId = w.Security,
            Shares = 5m, Price = 10m, Amount = 50m, TransferAccountId = w.OtherBrokerage,
            Memo = "rollover", Tags = ["moved"],
        });

        Assert.Equal(w.Brokerage, await OwnerAsync(id));
        var notes = await w.Ledger.LoadNotesAsync(id);
        Assert.Equal(new Notes<string>("rollover", ["moved"]), notes.Header);
        Assert.All(On(notes, w.OtherBrokerage), n => Assert.True(n.IsEmpty));

        var fromFunder = Assert.Single(await w.Ledger.ShownForAsync(id, w.OtherBrokerage));
        Assert.Equal(LegKind.EventOpposite, fromFunder.Presentation.Kind);
        Assert.True(fromFunder.Presentation.IsReadOnly);
        Assert.Equal("rollover", fromFunder.Memo);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task An_omitted_memo_is_left_and_a_null_one_clears_it()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, Buy(w, Day1, "keep me", ["roth"]));
        string Body(string memoField) =>
            $$"""{"brokerageAccountId":"{{w.Brokerage}}","postedAt":"2026-05-04T12:00:00Z","action":"buy","securityId":"{{w.Security}}","shares":10,"price":10,"amount":120{{memoField}}}""";

        var omitted = await PatchJsonAsync(w, id, Body(""));
        Assert.True(omitted.IsSuccessStatusCode, await omitted.Content.ReadAsStringAsync());
        Assert.Equal(new Notes<string>("keep me", ["roth"]), Assert.Single(On(await w.Ledger.LoadNotesAsync(id), w.Brokerage)));

        var cleared = await PatchJsonAsync(w, id, Body(",\"memo\":null"));
        Assert.True(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());
        Assert.Equal(new Notes<string>(null, ["roth"]), Assert.Single(On(await w.Ledger.LoadNotesAsync(id), w.Brokerage)));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Adding_and_removing_a_fee_moves_the_value_between_the_brokerage_leg_and_the_header()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, Buy(w, Day1, "lot", ["long"]));

        var withFee = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions/{id}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = w.Brokerage, PostedAt = Day1, Action = "buy", SecurityId = w.Security,
                Shares = 10m, Price = 10m, Amount = 100m, FeeAccountId = w.Fees, FeeAmount = 2m,
            });
        Assert.True(withFee.IsSuccessStatusCode, await withFee.Content.ReadAsStringAsync());
        var several = await w.Ledger.LoadNotesAsync(id);
        Assert.Equal(new Notes<string>("lot", ["long"]), several.Header);
        Assert.All(several.Legs.Values, n => Assert.True(n.IsEmpty));

        var withoutFee = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions/{id}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = w.Brokerage, PostedAt = Day1, Action = "buy", SecurityId = w.Security,
                Shares = 10m, Price = 10m, Amount = 100m,
            });
        Assert.True(withoutFee.IsSuccessStatusCode, await withoutFee.Content.ReadAsStringAsync());
        var one = await w.Ledger.LoadNotesAsync(id);
        Assert.True(one.Header.IsEmpty);
        Assert.Equal(new Notes<string>("lot", ["long"]), Assert.Single(On(one, w.Brokerage)));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Making_a_feed_row_an_event_hands_it_to_the_brokerage_and_no_register_changes()
    {
        // A feed row on the brokerage, entered from Checking with Checking's memo; the
        // brokerage register shows it inherited. Setting the action in the brokerage
        // register hands it over, and both registers still show the memo.
        await using var w = await WorldAsync();
        var (checkingLeg, _) = await w.Ledger.AddTransactionPairAsync(w.Checking, w.Brokerage, -500m, Day1);
        var id = await w.Ledger.ResolveHeaderIdAsync(checkingLeg);
        await using (var db = Db.NewDbContext())
            await db.TxnHeaders.Where(h => h.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(h => h.Origin, "online_import")
                .SetProperty(h => h.ProviderKey, "simplefin")
                .SetProperty(h => h.ExternalId, "feed-1"));
        await w.Ledger.SetSideMemoAsync(checkingLeg, w.Checking, "from the bank", w.Checking);

        var resp = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions/{id}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = w.Brokerage, PostedAt = Day1, Action = "transfer",
                TransferAccountId = w.Checking, Amount = 500m,
            });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

        Assert.Equal(w.Brokerage, await OwnerAsync(id));
        Assert.Equal("from the bank", Assert.Single(await w.Ledger.ShownForAsync(id, w.Brokerage)).Memo);
        Assert.Equal("from the bank", Assert.Single(await w.Ledger.ShownForAsync(id, w.Checking)).Memo);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task An_in_kind_transfer_keeps_its_notes_through_an_edit_and_belongs_to_its_source()
    {
        await using var w = await WorldAsync();
        await CreateAsync(w, Buy(w, Day1));
        CreateInvestmentTransactionRequest Move(string? memo, IReadOnlyList<string>? tags, decimal shares) => new()
        {
            BrokerageAccountId = w.Brokerage, PostedAt = Day2, Action = "transfer_shares", SecurityId = w.Security,
            Shares = shares, TransferAccountId = w.OtherBrokerage, Memo = memo, Tags = tags,
        };
        var id = await CreateAsync(w, Move("to the new firm", ["rollover"], 4m));

        var patch = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions/{id}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = w.Brokerage, PostedAt = Day2, Action = "transfer_shares", SecurityId = w.Security,
                Shares = 6m, TransferAccountId = w.OtherBrokerage,
            });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(id);
        Assert.Equal(new Notes<string>("to the new firm", ["rollover"]), notes.Header);
        Assert.All(notes.Legs.Values, n => Assert.True(n.IsEmpty));
        Assert.Equal(w.Brokerage, await OwnerAsync(id));
        Assert.All(await w.Ledger.ShownForAsync(id, w.OtherBrokerage), s => Assert.Equal("to the new firm", s.Memo));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Converting_a_sell_and_a_buy_in_kind_joins_their_memos_and_unions_their_tags()
    {
        await using var w = await WorldAsync();
        await CreateAsync(w, Buy(w, Day1));
        var sell = await CreateAsync(w, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = w.Brokerage, PostedAt = Day2, Action = "sell", SecurityId = w.Security,
            Shares = -4m, Price = 10m, Amount = 40m, Memo = "out", Tags = ["a"],
        });
        var buy = await CreateAsync(w, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = w.OtherBrokerage, PostedAt = Day2, Action = "buy", SecurityId = w.Security,
            Shares = 4m, Price = 10m, Amount = 40m, Memo = "in", Tags = ["b"],
        });

        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/in-kind-transfers/convert",
            new ConvertInKindTransferRequest { SellHeaderId = sell, BuyHeaderId = buy });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

        await using var db = Db.NewDbContext();
        var transfer = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == w.Ledger.LedgerId && h.Action == "transfer_shares");
        var notes = await w.Ledger.LoadNotesAsync(transfer.Id);
        Assert.Equal(new Notes<string>("out · in", ["a", "b"]), notes.Header);
        Assert.All(notes.Legs.Values, n => Assert.True(n.IsEmpty));
        Assert.Equal(w.Brokerage, transfer.OriginatingAccountId);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Tagging_an_events_counterpart_row_through_mcp_is_refused()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = w.Brokerage, PostedAt = Day1, Action = "transfer",
            TransferAccountId = w.Checking, Amount = 500m,
        });

        await using var db = Db.NewAppDbContextAsUser(w.Ledger.UserId);
        var result = await McpWriteTools.SetTransactionTags(
            McpTestGuard.Writable(), new TransactionsRepository(db, TimeProvider.System), w.Ledger.LedgerId,
            new[] { id }, w.Checking, new[] { "x" });

        Assert.False(result.Ok);
        Assert.Equal(SetTransactionTagsContract.ReadOnlyRowReason, Assert.Single(result.Placements).Refused);
        Assert.True((await w.Ledger.LoadNotesAsync(id)).Legs.Values.All(n => n.Tags.Count == 0));
    }

    [Fact]
    public async Task Tagging_a_buy_from_the_brokerage_that_funded_it_is_refused()
    {
        // The funder shows the event read-only; the only writer that can address a
        // notes write to its side is set_transaction_tags (the bank PATCH refuses an
        // event outright, and the investment PATCH writes from the brokerage it names).
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = w.Brokerage, PostedAt = Day1, Action = "buyx", SecurityId = w.Security,
            Shares = 5m, Price = 10m, Amount = 50m, TransferAccountId = w.OtherBrokerage,
            Memo = "rollover", Tags = ["moved"],
        });
        var before = await w.Ledger.LoadNotesAsync(id);

        await using var db = Db.NewAppDbContextAsUser(w.Ledger.UserId);
        var result = await McpWriteTools.SetTransactionTags(
            McpTestGuard.Writable(), new TransactionsRepository(db, TimeProvider.System), w.Ledger.LedgerId,
            new[] { id }, w.OtherBrokerage, new[] { "x" });

        Assert.False(result.Ok);
        Assert.Equal(SetTransactionTagsContract.ReadOnlyRowReason, Assert.Single(result.Placements).Refused);
        Assert.Equal(before, await w.Ledger.LoadNotesAsync(id));
        Assert.Equal(w.Brokerage, await OwnerAsync(id));
    }
}
