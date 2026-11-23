using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// A transaction merged into another (<c>is_merged_into</c> set) is a tombstone: every write
/// a user aims at it by id answers exactly as it answers an id that does not exist, and
/// changes nothing (decided by the owner, 2026-11-18; ADR-0114 D15). So does the read the
/// editor loads a transaction from. The merge keeps its own refusal.
/// </summary>
/// <remarks>
/// <para>The merged-away row is reached only through real paths: a manual row posted, a
/// statement row written by an OFX import, and the merge route folding the imported row into
/// the manual one. Each write is aimed at the merged-away row and at a random id, and the two
/// answers (status, problem code, title and detail) must be the same; the row must be as it
/// was after every one. The anchor: each write aimed at the live winner reaches it.</para>
/// <para>Every guarded entry of <see cref="MergedAwayGuardCatalogTests"/> is exercised here.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MergedAwayRowWritesTests : DatabaseTest
{
    public MergedAwayRowWritesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private sealed record Bank(
        SyntheticLedger Ledger, Guid Checking, Guid Savings, Guid Groceries, Guid Dining,
        HttpClient Client, ApiFactory Factory, Guid Winner, Guid Loser);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>A manual row, and the imported statement row of its day merged into it.</summary>
    private async Task<Bank> BankAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Account A");
        var savings = await ledger.AddBankAccountAsync("Account B");
        var groceries = await ledger.AddCategoryAsync("Category A");
        var dining = await ledger.AddCategoryAsync("Category B");
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var client = await ClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc),
                Payee = "Payee A",
                SourceAccountId = checking.Id,
                Postings = [new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -9m }],
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var winner = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();

        var loser = await ImportAsync(client, ledger.LedgerId, checking.Id, BankStatement, "999999999:12345", "LOSER-1");
        await MergeAsync(client, $"/api/ledgers/{ledger.LedgerId}/transactions", loser, winner);
        return new Bank(ledger, checking.Id, savings.Id, groceries.Id, dining.Id, client, factory, winner, loser);
    }

    private async Task<Guid> ImportAsync(
        HttpClient client, Guid ledgerId, Guid accountId, string statement, string providerAccountId, string externalId)
    {
        using var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(statement));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(body, "file", "statement.qfx");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent(providerAccountId), "providerAccountId");
        var resp = await client.PostAsync($"/api/ledgers/{ledgerId}/ingest/ofx/import", content);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        await using var db = Db.NewDbContext();
        var row = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.LedgerId == ledgerId && h.ExternalId == externalId);
        Assert.True(row.NeedsReview);
        return row.Id;
    }

    private async Task MergeAsync(HttpClient client, string route, Guid loser, Guid winner)
    {
        var merge = await client.PostAsJsonAsync($"{route}/{loser}/merge", new MergeTransactionRequest { FromHeaderId = winner });
        Assert.True(merge.IsSuccessStatusCode, $"merge failed: {(int)merge.StatusCode} {await merge.Content.ReadAsStringAsync()}");
        await using var db = Db.NewDbContext();
        Assert.Equal(winner, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);
    }

    /// <summary>What an answer says: its status and, for a problem, its code, title and
    /// detail; otherwise its body.</summary>
    private static async Task<string> AnswerAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var status = (int)response.StatusCode;
        if (body.Length == 0) return $"{status}";
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code))
            return $"{status} {body}";
        string? Text(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;
        return $"{status} {code.GetString()} | {Text("title")} | {Text("detail")}";
    }

    /// <summary>Every stored part of a transaction a write could touch.</summary>
    private async Task<string> StateAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        var h = await db.TxnHeaders.AsNoTracking().SingleAsync(x => x.Id == headerId);
        var legs = await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId)
            .OrderBy(l => l.PostingIndex).ThenBy(l => l.Id)
            .Select(l => new { l.Id, l.AccountId, l.Amount, l.PostingIndex, l.LegMemo, l.PostingRole, l.SecurityId, l.Quantity })
            .ToListAsync();
        var legIds = legs.Select(l => l.Id).ToList();
        var recon = await db.TxnLegRecon.AsNoTracking().Where(r => legIds.Contains(r.LegId))
            .OrderBy(r => r.LegId).Select(r => new { r.LegId, r.Status }).ToListAsync();
        var headerTags = await db.TxnHeaderTags.AsNoTracking().Where(t => t.HeaderId == headerId)
            .Select(t => t.TagId).OrderBy(t => t).ToListAsync();
        var legTags = await db.TxnLegTags.AsNoTracking().Where(t => legIds.Contains(t.LegId))
            .OrderBy(t => t.LegId).ThenBy(t => t.TagId).Select(t => new { t.LegId, t.TagId }).ToListAsync();
        return JsonSerializer.Serialize(new
        {
            h.Payee, h.Memo, h.PostedAt, h.TransactedAt, h.NeedsReview, h.IsHidden, h.IsMergedInto, h.Action,
            h.OriginatingAccountId, legs, recon, headerTags, legTags,
        });
    }

    /// <summary>The write aimed at the merged-away row answers as at an id that does not
    /// exist, and leaves the row as it was.</summary>
    private async Task<string> AssertAbsentAsync(string what, Guid loser, Func<Guid, Task<HttpResponseMessage>> send)
    {
        var before = await StateAsync(loser);
        var unknown = await AnswerAsync(await send(Guid.NewGuid()));
        var merged = await AnswerAsync(await send(loser));
        Output.WriteLine($"{what}: {merged}");
        Assert.True(unknown == merged, $"{what}: an unknown id answers\n  {unknown}\nbut the merged-away row\n  {merged}");
        Assert.True(before == await StateAsync(loser), $"{what} changed the merged-away row");
        return merged;
    }

    private static async Task AssertReachesAsync(string what, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{what} on the winner: {(int)response.StatusCode} {body}");
    }

    private static object Explicit(Guid accountId, Guid headerId) =>
        new { kind = "explicit", accountId, headerIds = new[] { headerId } };

    [Fact]
    public async Task Every_write_by_id_answers_a_merged_away_row_as_an_unknown_one_and_changes_nothing()
    {
        var s = await BankAsync();
        await using var _ = s.Factory;
        using var client = s.Client;
        var url = $"/api/ledgers/{s.Ledger.LedgerId}/transactions";
        Guid loserLeg, winnerLeg;
        await using (var db = Db.NewDbContext())
        {
            loserLeg = (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.HeaderId == s.Loser && l.AccountId == s.Checking)).Id;
            winnerLeg = (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.HeaderId == s.Winner && l.AccountId == s.Checking)).Id;
        }
        object Reshape(Guid leg) => new
        {
            postings = new
            {
                sourceAccountId = s.Checking,
                items = new[] { new { legId = leg, counterpartyAccountId = s.Dining, amount = -9m } },
            },
        };

        await AssertAbsentAsync("PATCH payee", s.Loser, id => client.PatchAsJsonAsync($"{url}/{id}", new { payee = "Payee B" }));
        await AssertAbsentAsync("PATCH postings", s.Loser, id => client.PatchAsJsonAsync($"{url}/{id}", Reshape(loserLeg)));
        await AssertAbsentAsync("PATCH approve", s.Loser, id => client.PatchAsJsonAsync($"{url}/{id}", new { approve = true }));
        await AssertAbsentAsync("PUT recon-status", s.Loser, id => client.PutAsJsonAsync($"{url}/{id}/recon-status",
            new SetReconStatusRequest { Status = "cleared", AccountId = s.Checking }));
        await AssertAbsentAsync("POST approve", s.Loser, id => client.PostAsync($"{url}/{id}/approve", null));
        await AssertAbsentAsync("DELETE", s.Loser, id => client.DeleteAsync($"{url}/{id}"));
        await AssertAbsentAsync("GET legs", s.Loser, id => client.GetAsync($"{url}/{id}/legs"));
        await AssertAbsentAsync("GET similar-payees", s.Loser, id => client.GetAsync($"{url}/{id}/similar-payees"));
        await AssertAbsentAsync("GET merge-candidates", s.Loser, id => client.GetAsync($"{url}/{id}/merge-candidates"));
        await AssertAbsentAsync("POST bulk-recon-status", s.Loser, id => client.PostAsJsonAsync($"{url}/bulk-recon-status",
            new { selection = Explicit(s.Checking, id), status = "cleared", accountId = s.Checking }));
        await AssertAbsentAsync("POST bulk-delete", s.Loser, id => client.PostAsJsonAsync($"{url}/bulk-delete",
            new { selection = Explicit(s.Checking, id) }));
        await AssertAbsentAsync("POST bulk-unhide", s.Loser, id => client.PostAsJsonAsync($"{url}/bulk-unhide",
            new { selection = Explicit(s.Checking, id) }));
        await AssertAbsentAsync("POST bulk-move-account", s.Loser, id => client.PostAsJsonAsync($"{url}/bulk-move-account",
            new { selection = Explicit(s.Checking, id), targetAccountId = s.Savings }));
        await AssertAbsentAsync("POST selection-summary", s.Loser, id => client.PostAsJsonAsync($"{url}/selection-summary",
            Explicit(s.Checking, id)));

        // The connected-app tools answer as for an id they do not know.
        await AssertMcpAbsentAsync(s.Loser, async (repo, id) =>
        {
            var r = await McpWriteTools.SetTransactionCategory(McpTestGuard.Writable(), repo, s.Ledger.LedgerId, [id], s.Dining);
            return $"{r.Ok} {r.Recategorized} {r.Unchanged} {string.Join(",", r.Rejects.Select(x => x.Reason))}";
        }, "set_transaction_category");
        await AssertMcpAbsentAsync(s.Loser, async (repo, id) =>
        {
            var r = await McpWriteTools.SetSplitPostingCategory(
                McpTestGuard.Writable(), repo, s.Ledger.LedgerId, [id], s.Groceries, s.Dining);
            return $"{r.Ok} {r.Moved} {r.Unchanged} {string.Join(",", r.Rejects.Select(x => x.Reason))}";
        }, "set_split_posting_category");
        await AssertMcpAbsentAsync(s.Loser, async (repo, id) =>
        {
            var r = await McpWriteTools.SetTransactionTags(
                McpTestGuard.Writable(), repo, s.Ledger.LedgerId, [id], s.Checking, ["Tag A"]);
            return $"{r.Ok} {r.After} {r.Error?.Replace(id.ToString(), "<id>", StringComparison.Ordinal)}";
        }, "set_transaction_tags");

        // The anchor: each write reaches the live winner. The delete last: it un-merges the
        // loser, which the database does and the delete keeps doing.
        await AssertReachesAsync("PATCH payee", await client.PatchAsJsonAsync($"{url}/{s.Winner}", new { payee = "Payee B" }));
        await AssertReachesAsync("PATCH postings", await client.PatchAsJsonAsync($"{url}/{s.Winner}", Reshape(winnerLeg)));
        await AssertReachesAsync("PUT recon-status", await client.PutAsJsonAsync($"{url}/{s.Winner}/recon-status",
            new SetReconStatusRequest { Status = "cleared", AccountId = s.Checking }));
        await AssertReachesAsync("POST approve", await client.PostAsync($"{url}/{s.Winner}/approve", null));
        await AssertReachesAsync("POST bulk-unhide", await client.PostAsJsonAsync($"{url}/bulk-unhide",
            new { selection = Explicit(s.Checking, s.Winner) }));
        var legs = await client.GetAsync($"{url}/{s.Winner}/legs");
        await AssertReachesAsync("GET legs", legs);
        Assert.Equal(2, (await legs.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        await using (var db = Db.NewServiceFactory().Create())
        {
            var repo = new TransactionsRepository(db, TimeProvider.System);
            var category = await McpWriteTools.SetTransactionCategory(McpTestGuard.Writable(), repo, s.Ledger.LedgerId, [s.Winner], s.Groceries);
            Assert.Equal((true, 1), (category.Ok, category.Recategorized));
        }
        await using (var db = Db.NewServiceFactory().Create())
        {
            var repo = new TransactionsRepository(db, TimeProvider.System);
            // Found, and refused for its shape rather than as unknown.
            var split = await McpWriteTools.SetSplitPostingCategory(
                McpTestGuard.Writable(), repo, s.Ledger.LedgerId, [s.Winner], s.Groceries, s.Dining);
            Assert.Equal("not-a-split", Assert.Single(split.Rejects).Reason);
        }
        await using (var db = Db.NewServiceFactory().Create())
        {
            var repo = new TransactionsRepository(db, TimeProvider.System);
            var tags = await McpWriteTools.SetTransactionTags(
                McpTestGuard.Writable(), repo, s.Ledger.LedgerId, [s.Winner], s.Checking, ["Tag A"]);
            Assert.True(tags.Ok, tags.Error);
        }

        var deleted = await client.DeleteAsync($"{url}/{s.Winner}");
        await AssertReachesAsync("DELETE", deleted);
        await using (var db = Db.NewDbContext())
            Assert.Null((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == s.Loser)).IsMergedInto);
        var register = (await client.GetFromJsonAsync<RegisterPage>($"{url}?account_id={s.Checking}&limit=100"))!;
        Assert.Contains(register.Entries, e => e.Txn?.HeaderId == s.Loser);
        await AssertHealthyAsync(client, s.Ledger.LedgerId);
    }

    /// <summary>As <see cref="AssertAbsentAsync"/>, for a connected-app tool called the way
    /// the MCP tests call one.</summary>
    private async Task AssertMcpAbsentAsync(
        Guid loser, Func<TransactionsRepository, Guid, Task<string>> call, string what)
    {
        var before = await StateAsync(loser);
        string unknown, merged;
        await using (var db = Db.NewServiceFactory().Create())
            unknown = await call(new TransactionsRepository(db, TimeProvider.System), Guid.NewGuid());
        await using (var db = Db.NewServiceFactory().Create())
            merged = await call(new TransactionsRepository(db, TimeProvider.System), loser);
        Output.WriteLine($"{what}: {merged}");
        Assert.True(unknown == merged, $"{what}: an unknown id answers\n  {unknown}\nbut the merged-away row\n  {merged}");
        Assert.True(before == await StateAsync(loser), $"{what} changed the merged-away row");
    }

    [Fact]
    public async Task The_merge_still_refuses_a_merged_away_row_with_its_own_answer()
    {
        var s = await BankAsync();
        await using var _ = s.Factory;
        using var client = s.Client;
        var before = await StateAsync(s.Loser);

        var merge = await client.PostAsJsonAsync($"/api/ledgers/{s.Ledger.LedgerId}/transactions/{s.Loser}/merge",
            new MergeTransactionRequest { FromHeaderId = s.Winner });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, merge.StatusCode);
        Assert.Contains("merge-source-invalid", await merge.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, await StateAsync(s.Loser));
    }

    [Fact]
    public async Task Every_investment_write_by_id_answers_a_merged_away_row_as_an_unknown_one_and_changes_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var other = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var url = $"/api/ledgers/{ledger.LedgerId}/investment-transactions";
        var day = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);

        // A purchase entered by hand; a statement row edited into a purchase of the same day
        // while it awaits review, then merged into it.
        var winner = await InvestAsync(client, url, Buy(brokerage.Id, security, day, 10m, 10m));
        var loser = await ImportAsync(client, ledger.LedgerId, brokerage.Id, BrokerageStatement, "inv:brokerX:INV-0001", "INV-LOSER-1");
        await AssertReachesAsync("PATCH into a purchase", await client.PatchAsJsonAsync($"{url}/{loser}",
            PatchBuy(brokerage.Id, security, day, 5m, 12m)));
        await MergeAsync(client, url, loser, winner);
        // A sale on another brokerage that day, the other half an in-kind pair would need.
        await InvestAsync(client, url, Buy(other.Id, security, day.AddDays(-5), 5m, 10m));
        var sale = await InvestAsync(client, url, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = other.Id, PostedAt = day, Action = "sell", SecurityId = security, Shares = -5m, Price = 11m,
        });

        await AssertAbsentAsync("PATCH", loser, id => client.PatchAsJsonAsync($"{url}/{id}", PatchBuy(brokerage.Id, security, day, 6m, 12m)));
        await AssertAbsentAsync("POST approve", loser, id => client.PostAsync($"{url}/{id}/approve", null));
        await AssertAbsentAsync("DELETE", loser, id => client.DeleteAsync($"{url}/{id}"));
        await AssertAbsentAsync("GET merge-candidates", loser, id => client.GetAsync($"{url}/{id}/merge-candidates"));
        // The sale is found, so the answer is about the purchase named.
        Assert.Contains("in-kind-transfer-buy-not-found", await AssertAbsentAsync("POST in-kind convert", loser,
            id => client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/in-kind-transfers/convert",
                new ConvertInKindTransferRequest { SellHeaderId = sale, BuyHeaderId = id })), StringComparison.Ordinal);
        // The bank routes that take either shape, and the editor's leg read.
        var bank = $"/api/ledgers/{ledger.LedgerId}/transactions";
        await AssertAbsentAsync("GET legs", loser, id => client.GetAsync($"{bank}/{id}/legs"));
        await AssertAbsentAsync("PUT recon-status", loser, id => client.PutAsJsonAsync($"{bank}/{id}/recon-status",
            new SetReconStatusRequest { Status = "cleared", AccountId = brokerage.Id }));
        await AssertAbsentAsync("bank DELETE", loser, id => client.DeleteAsync($"{bank}/{id}"));

        var before = await StateAsync(loser);
        string unknown, merged;
        await using (var db = Db.NewServiceFactory().Create())
            unknown = (await McpWriteTools.ConvertInKindTransfer(McpTestGuard.Writable(),
                new InvestmentTransactionsRepository(db, TimeProvider.System), ledger.LedgerId, sale, Guid.NewGuid())).Error ?? "";
        await using (var db = Db.NewServiceFactory().Create())
            merged = (await McpWriteTools.ConvertInKindTransfer(McpTestGuard.Writable(),
                new InvestmentTransactionsRepository(db, TimeProvider.System), ledger.LedgerId, sale, loser)).Error ?? "";
        Assert.Equal(unknown, merged);
        Assert.Equal("BuyNotFound", merged);
        Assert.Equal(before, await StateAsync(loser));

        // The anchor: each reaches the live winner. The in-kind pair is found and refused
        // for its quantities, not as unknown.
        var convert = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/in-kind-transfers/convert",
            new ConvertInKindTransferRequest { SellHeaderId = sale, BuyHeaderId = winner });
        Assert.Contains("in-kind-transfer-not-a-valid-pair", await convert.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertReachesAsync("PATCH", await client.PatchAsJsonAsync($"{url}/{winner}", PatchBuy(brokerage.Id, security, day, 10m, 10m)));
        await AssertReachesAsync("POST approve", await client.PostAsync($"{url}/{winner}/approve", null));
        await AssertReachesAsync("GET legs", await client.GetAsync($"{bank}/{winner}/legs"));
        await AssertReachesAsync("DELETE", await client.DeleteAsync($"{url}/{winner}"));
        await using (var db = Db.NewDbContext())
            Assert.Null((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);
        await AssertHealthyAsync(client, ledger.LedgerId);
    }

    private static CreateInvestmentTransactionRequest Buy(Guid brokerage, Guid security, DateTime at, decimal shares, decimal price) =>
        new() { BrokerageAccountId = brokerage, PostedAt = at, Action = "buy", SecurityId = security, Shares = shares, Price = price };

    private static PatchInvestmentTransactionRequest PatchBuy(Guid brokerage, Guid security, DateTime at, decimal shares, decimal price) =>
        new() { BrokerageAccountId = brokerage, PostedAt = at, Action = "buy", SecurityId = security, Shares = shares, Price = price };

    private static async Task<Guid> InvestAsync(HttpClient client, string url, CreateInvestmentTransactionRequest request)
    {
        var resp = await client.PostAsJsonAsync(url, request);
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task AssertHealthyAsync(HttpClient client, Guid ledgerId)
    {
        var resp = await client.PostAsync($"/api/ledgers/{ledgerId}/balances/consistency", null);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, body);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("healthy").GetBoolean(), body);
    }

    private const string BankStatement = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII

        <OFX>
        <SIGNONMSGSRSV1>
        <SONRS>
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <DTSERVER>20260501120000
        <LANGUAGE>ENG
        </SONRS>
        </SIGNONMSGSRSV1>
        <BANKMSGSRSV1>
        <STMTTRNRS>
        <TRNUID>0
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <STMTRS>
        <CURDEF>USD
        <BANKACCTFROM>
        <BANKID>999999999
        <ACCTID>12345
        <ACCTTYPE>CHECKING
        </BANKACCTFROM>
        <BANKTRANLIST>
        <DTSTART>20260401
        <DTEND>20260430
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20260410
        <TRNAMT>-9.00
        <FITID>LOSER-1
        <NAME>PAYEE A
        </STMTTRN>
        </BANKTRANLIST>
        </STMTRS>
        </STMTTRNRS>
        </BANKMSGSRSV1>
        </OFX>
        """;

    private const string BrokerageStatement = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE

        <OFX>
        <SIGNONMSGSRSV1>
        <SONRS>
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <DTSERVER>20260201120000
        <LANGUAGE>ENG
        </SONRS>
        </SIGNONMSGSRSV1>
        <INVSTMTMSGSRSV1>
        <INVSTMTTRNRS>
        <TRNUID>0
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <INVSTMTRS>
        <DTASOF>20260131120000
        <CURDEF>USD
        <INVACCTFROM>
        <BROKERID>brokerX
        <ACCTID>INV-0001
        </INVACCTFROM>
        <INVTRANLIST>
        <DTSTART>20260101
        <DTEND>20260131
        <INVBANKTRAN>
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20260120
        <TRNAMT>-60.00
        <FITID>INV-LOSER-1
        <NAME>PURCHASE
        </STMTTRN>
        <SUBACCTFUND>CASH
        </INVBANKTRAN>
        </INVTRANLIST>
        </INVSTMTRS>
        </INVSTMTTRNRS>
        </INVSTMTMSGSRSV1>
        </OFX>
        """;
}
