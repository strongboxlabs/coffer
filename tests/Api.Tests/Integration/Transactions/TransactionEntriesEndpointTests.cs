using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// <c>GET /api/ledgers/{ledgerId}/transactions/entries</c>, the ledger-wide register
/// entries behind the home screen's To review and Recent transactions: every row is the
/// row its own account's register shows, the awaiting-review entries are exactly the ones
/// each account's review count and the register's status count count, the direction and
/// the caller's today narrow before the page is cut, and the route refuses what it does
/// not take. Every state is written through the product: the file importer, the register's
/// PATCH, merge and create routes, and the accounts' active switch.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TransactionEntriesEndpointTests : DatabaseTest
{
    public TransactionEntriesEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateOnly StatementStart = new(2026, 1, 1);

    // ---- helpers -----------------------------------------------------------------

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    /// <summary>A signed-in client for a second member, as AccountFoldersEndpointsTests makes one.</summary>
    private async Task<HttpClient> MemberClientAsync(ApiFactory factory, Guid userId)
    {
        var (plaintext, hash) = Coffer.Api.Auth.Webauthn.SessionService.GenerateCookieValue();
        await using (var db = Db.NewDbContext())
        {
            db.AuthSessions.Add(new SessionRow
            {
                Id = Guid.NewGuid(), UserId = userId, SessionHash = hash,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={plaintext}");
        return client;
    }

    private static string Entries(Guid ledgerId, string query = "") =>
        $"/api/ledgers/{ledgerId}/transactions/entries{(query.Length == 0 ? "" : "?" + query)}";

    private static async Task<JsonElement> GetEntriesAsync(HttpClient client, Guid ledgerId, string query = "")
    {
        var response = await client.GetAsync(Entries(ledgerId, query));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static async Task<string?> RefusalCodeAsync(HttpClient client, Guid ledgerId, string query)
    {
        var response = await client.GetAsync(Entries(ledgerId, query));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("code").GetString();
    }

    /// <summary>One returned entry: its account, its header, and the entry as the API wrote it.</summary>
    private sealed record Returned(Guid AccountId, string CurrencyCode, Guid HeaderId, JsonElement Entry)
    {
        public bool IsGroup => Entry.GetProperty("kind").GetString() == "group";

        /// <summary>
        /// The amount the row shows, as the web app draws it: a split group's net on its
        /// account (which every line carries, else the lines' sum), a single row's own amount.
        /// </summary>
        public decimal Amount
        {
            get
            {
                if (!IsGroup) return Entry.GetProperty("txn").GetProperty("amount").GetDecimal();
                var legs = Entry.GetProperty("legs");
                return legs[0].GetProperty("headerAccountNetAmount") is { ValueKind: JsonValueKind.Number } net
                    ? net.GetDecimal()
                    : legs.EnumerateArray().Sum(l => l.GetProperty("amount").GetDecimal());
            }
        }

        /// <summary>The transaction's net on the entry's account, which each of its rows there carries.</summary>
        public decimal NetOnAccount =>
            (IsGroup ? Entry.GetProperty("legs")[0] : Entry.GetProperty("txn"))
                .GetProperty("headerAccountNetAmount").GetDecimal();
    }

    private static List<Returned> Listed(JsonElement page) =>
    [
        .. page.GetProperty("entries").EnumerateArray().Select(e =>
        {
            var entry = e.GetProperty("entry");
            var row = entry.GetProperty("kind").GetString() == "group"
                ? entry.GetProperty("legs")[0]
                : entry.GetProperty("txn");
            return new Returned(
                e.GetProperty("accountId").GetGuid(),
                e.GetProperty("currencyCode").GetString()!,
                row.GetProperty("headerId").GetGuid(),
                entry.Clone());
        }),
    ];

    private static bool HasMore(JsonElement page) => page.GetProperty("hasMore").GetBoolean();

    private static async Task<List<JsonElement>> AccountsAsync(HttpClient client, Guid ledgerId) =>
        (await client.GetFromJsonAsync<List<JsonElement>>($"/api/ledgers/{ledgerId}/accounts?includeInactive=true"))!;

    private static async Task<int> StatusNeedsReviewAsync(HttpClient client, Guid ledgerId, Guid accountId)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/ledgers/{ledgerId}/transactions/status-counts?account_id={accountId}"));
        return doc.RootElement.GetProperty("needsReview").GetInt32();
    }

    /// <summary>A file statement of bank rows, imported through the OFX importer, which flags each one for review.</summary>
    private static async Task ImportAsync(
        HttpClient client, Guid ledgerId, Guid accountId, string accountNumber,
        params (string FitId, DateOnly On, decimal Amount, string Name)[] rows)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(OfxBankStatement(accountNumber, rows)));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(file, "file", "statement.qfx");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent($"{BankNumber}:{accountNumber}"), "providerAccountId");
        var response = await client.PostAsync($"/api/ledgers/{ledgerId}/ingest/ofx/import", content);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"import: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>The bank id of every statement <see cref="OfxBankStatement"/> writes.</summary>
    internal const string BankNumber = "999999999";

    /// <summary>
    /// An OFX 1.x bank statement of <paramref name="rows"/> for <paramref name="accountNumber"/>,
    /// imported as <c>{BankNumber}:{accountNumber}</c>; shared with the stress lane's cost test.
    /// </summary>
    internal static string OfxBankStatement(string accountNumber, IReadOnlyList<(string FitId, DateOnly On, decimal Amount, string Name)> rows)
    {
        static string Day(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var transactions = new StringBuilder();
        foreach (var (fitId, on, amount, name) in rows)
        {
            transactions.Append(CultureInfo.InvariantCulture, $"""
                <STMTTRN>
                <TRNTYPE>{(amount < 0m ? "DEBIT" : "CREDIT")}
                <DTPOSTED>{Day(on)}
                <TRNAMT>{amount.ToString(CultureInfo.InvariantCulture)}
                <FITID>{fitId}
                <NAME>{name}
                </STMTTRN>

                """);
        }
        var from = rows.Min(r => r.On);
        var to = rows.Max(r => r.On);
        return $"""
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
            <DTSERVER>{Day(to)}120000
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
            <BANKID>{BankNumber}
            <ACCTID>{accountNumber}
            <ACCTTYPE>CHECKING
            </BANKACCTFROM>
            <BANKTRANLIST>
            <DTSTART>{Day(from)}
            <DTEND>{Day(to)}
            {transactions}</BANKTRANLIST>
            </STMTRS>
            </STMTTRNRS>
            </BANKMSGSRSV1>
            </OFX>
            """;
    }

    private async Task<Guid> HeaderOfAsync(Guid ledgerId, string fitId)
    {
        await using var db = Db.NewDbContext();
        return (await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledgerId && h.ExternalId == fitId)).Id;
    }

    /// <summary>Re-split a row without approving it: a PATCH with postings and no approve leaves it awaiting review.</summary>
    private static async Task SplitWithoutApprovingAsync(
        HttpClient client, Guid ledgerId, Guid headerId, Guid sourceAccountId,
        params (Guid Counterparty, decimal Amount)[] lines)
    {
        var response = await client.PatchAsJsonAsync($"/api/ledgers/{ledgerId}/transactions/{headerId}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = sourceAccountId,
                    Items = [.. lines.Select(l => new TransactionPosting { CounterpartyAccountId = l.Counterparty, Amount = l.Amount })],
                },
            });
        Assert.True(response.IsSuccessStatusCode,
            $"split: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<Guid> CreateAsync(
        HttpClient client, Guid ledgerId, Guid sourceAccountId, DateOnly on, string payee,
        params (Guid Counterparty, decimal Amount)[] lines)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = on.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Payee = payee,
                SourceAccountId = sourceAccountId,
                Postings = [.. lines.Select(l => new TransactionPosting { CounterpartyAccountId = l.Counterparty, Amount = l.Amount })],
            });
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"create: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task<Guid> CreateInvestmentAsync(
        HttpClient client, Guid ledgerId, CreateInvestmentTransactionRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/investment-transactions", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"investment create: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    /// <summary>
    /// Every entry of <paramref name="entries"/> on one account and header equals what that
    /// account's register page, anchored at the header, returns first: the rows are the
    /// register's own.
    /// </summary>
    private static async Task AssertDrawnAsTheRegisterDrawsAsync(
        HttpClient client, Guid ledgerId, IReadOnlyList<Returned> entries)
    {
        foreach (var group in entries.GroupBy(e => (e.AccountId, e.HeaderId)))
        {
            var mine = group.ToList();
            using var doc = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/ledgers/{ledgerId}/transactions?account_id={group.Key.AccountId}"
                + $"&starting_at={group.Key.HeaderId}&limit={mine.Count}"));
            var theirs = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
            Assert.Equal(mine.Count, theirs.Count);
            for (var i = 0; i < mine.Count; i++)
                Assert.Equal(theirs[i].GetRawText(), mine[i].Entry.GetRawText());
        }
    }

    // ---- the review set reconciles with every count ------------------------------

    /// <summary>
    /// The awaiting-review entries on each account are exactly as many as that account's
    /// review count on <c>GET /accounts</c> and its register's needs-review status count,
    /// for every account a person reads, an inactive one included.
    /// </summary>
    /// <remarks>
    /// The fixture holds each shape that can make the three disagree: a split awaiting
    /// review (one entry on its account, one line per category), a split whose two lines
    /// land on one other real account (two entries there for one transaction), an
    /// account deactivated after its row arrived, a brokerage statement row edited into a
    /// buy and left awaiting review, which puts a leg awaiting review on the brokerage's
    /// holdings sibling (beside a settled buy's), and a row folded into its duplicate by
    /// the merge route, which leaves no register.
    /// </remarks>
    [Fact]
    public async Task Review_entries_on_each_account_equal_its_review_count_and_its_registers_status_count()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");
        var accountB = await ledger.AddBankAccountAsync("Account B");
        var accountC = await ledger.AddBankAccountAsync("Account C");
        var categoryA = await ledger.AddCategoryAsync("Category A");
        var categoryB = await ledger.AddCategoryAsync("Category B");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        const decimal PlainAmount = -12.34m, SplitAmount = -100m, SplitLineOne = -60m, SplitLineTwo = -40m,
            MergedAmount = -17m, InactiveAmount = -8m;
        await ImportAsync(client, ledger.LedgerId, accountA.Id, "ACCOUNT-A",
            ("FITID-PLAIN", StatementStart.AddDays(4), PlainAmount, "PAYEE A"),
            ("FITID-SPLIT", StatementStart.AddDays(5), SplitAmount, "PAYEE B"),
            ("FITID-TWO-ON-B", StatementStart.AddDays(6), SplitAmount, "PAYEE C"),
            ("FITID-MERGED", StatementStart.AddDays(7), MergedAmount, "PAYEE D"));
        await ImportAsync(client, ledger.LedgerId, accountC.Id, "ACCOUNT-C",
            ("FITID-INACTIVE", StatementStart.AddDays(8), InactiveAmount, "PAYEE E"));

        var plain = await HeaderOfAsync(ledger.LedgerId, "FITID-PLAIN");
        var split = await HeaderOfAsync(ledger.LedgerId, "FITID-SPLIT");
        var twoOnB = await HeaderOfAsync(ledger.LedgerId, "FITID-TWO-ON-B");
        var merged = await HeaderOfAsync(ledger.LedgerId, "FITID-MERGED");

        await SplitWithoutApprovingAsync(client, ledger.LedgerId, split, accountA.Id,
            (categoryA.Id, SplitLineOne), (categoryB.Id, SplitLineTwo));
        await SplitWithoutApprovingAsync(client, ledger.LedgerId, twoOnB, accountA.Id,
            (accountB.Id, SplitLineOne), (accountB.Id, SplitLineTwo));

        // The merged row's settled twin, entered by hand, then the merge the register offers.
        var twin = await CreateAsync(client, ledger.LedgerId, accountA.Id, StatementStart.AddDays(7), "Payee D",
            (categoryA.Id, MergedAmount));
        var merge = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{merged}/merge",
            new MergeTransactionRequest { FromHeaderId = twin });
        Assert.True(merge.IsSuccessStatusCode, await merge.Content.ReadAsStringAsync());

        var deactivate = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{accountC.Id}/active", new PatchAccountActiveRequest { Active = false });
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        // A settled buy puts a leg on the sibling; the statement's reinvest arrives for
        // review, bank-shaped, and is edited into a buy without approving it, so the
        // sibling holds a leg of a row awaiting review too.
        Assert.NotNull(brokerage.HoldingsAccountId);
        var sibling = brokerage.HoldingsAccountId.Value;
        const decimal BuyShares = 4.321m, BuyPrice = 25.102985m, BuyAmount = 108.47m;
        var tradeDay = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        await CreateInvestmentAsync(client, ledger.LedgerId, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, Action = "buy", SecurityId = security,
            Shares = BuyShares, Price = BuyPrice, Amount = BuyAmount, PostedAt = tradeDay,
        });
        await ImportReinvestAsync(client, ledger.LedgerId, brokerage.Id);
        var reinvest = await HeaderOfAsync(ledger.LedgerId, ReinvestFitId);
        var edited = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions/{reinvest}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id, Action = "buy", SecurityId = security,
                Shares = BuyShares, Price = BuyPrice, Amount = BuyAmount, PostedAt = tradeDay,
            });
        Assert.True(edited.IsSuccessStatusCode,
            $"investment edit: {(int)edited.StatusCode} {await edited.Content.ReadAsStringAsync()}");

        var page = await GetEntriesAsync(client, ledger.LedgerId, "needs_review=true&limit=50");
        Assert.False(HasMore(page));
        var entries = Listed(page);

        // Premises: there is something to reconcile; the split onto one other account
        // really is two entries there; the merged row has left; the sibling's own
        // register counts a row awaiting review, and still the sibling is not listed.
        Assert.NotEmpty(entries);
        Assert.Equal(2, entries.Count(e => e.AccountId == accountB.Id && e.HeaderId == twoOnB));
        Assert.Single(entries, e => e.AccountId == accountA.Id && e.HeaderId == split && e.IsGroup);
        Assert.Single(entries, e => e.AccountId == accountA.Id && e.HeaderId == plain);
        Assert.Single(entries, e => e.AccountId == accountC.Id);
        Assert.Single(entries, e => e.AccountId == brokerage.Id && e.HeaderId == reinvest);
        Assert.DoesNotContain(entries, e => e.HeaderId == merged || e.HeaderId == twin);
        Assert.True(await StatusNeedsReviewAsync(client, ledger.LedgerId, sibling) > 0,
            "the sibling holds no leg awaiting review, so its absence below proves nothing");
        Assert.DoesNotContain(entries, e => e.AccountId == sibling);

        var accounts = await AccountsAsync(client, ledger.LedgerId);
        var read = accounts.Where(a => a.GetProperty("accountType").GetString() != "category").ToList();
        Assert.Contains(read, a => a.GetProperty("id").GetGuid() == accountC.Id && !a.GetProperty("isActive").GetBoolean());
        Assert.DoesNotContain(entries, e => read.All(a => a.GetProperty("id").GetGuid() != e.AccountId));
        foreach (var account in read)
        {
            var id = account.GetProperty("id").GetGuid();
            var name = account.GetProperty("name").GetString();
            var listed = entries.Count(e => e.AccountId == id);
            Assert.True(listed == account.GetProperty("needsReviewCount").GetInt32(),
                $"{name}: {listed} entries listed, review count {account.GetProperty("needsReviewCount").GetInt32()}");
            Assert.True(listed == await StatusNeedsReviewAsync(client, ledger.LedgerId, id),
                $"{name}: {listed} entries listed, register status count {await StatusNeedsReviewAsync(client, ledger.LedgerId, id)}");
        }

        // Every row of the review list is the row its register shows.
        await AssertDrawnAsTheRegisterDrawsAsync(client, ledger.LedgerId, entries);
    }

    // ---- rows are drawn as the register draws them ------------------------------------

    /// <summary>
    /// A plain row, a split on its own account, each line of a split on the other account,
    /// a transfer on both of its accounts and an in-kind share transfer on both brokerages:
    /// each returned entry equals the one its own register returns at that header, with
    /// that account's currency.
    /// </summary>
    /// <remarks>
    /// The in-kind transfer is keyed by its header on both brokerages, each of them the
    /// event's investment side, so a page across accounts that grouped by key alone would
    /// fold the two into one row.
    /// </remarks>
    [Fact]
    public async Task Each_entry_is_the_row_its_own_register_shows()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");
        var accountB = (await ledger.AddBankAccountAsync("Account B")).Id;
        var categoryA = await ledger.AddCategoryAsync("Category A");
        var categoryB = await ledger.AddCategoryAsync("Category B");
        var brokerageA = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var brokerageB = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // An account in another currency, made the way a person makes one.
        const string OtherCurrency = "EUR";
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Account C", AccountType = "bank", CurrencyCode = OtherCurrency });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var accountC = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        const decimal PlainAmount = -5m, LineOne = -7m, LineTwo = -3m, TransferAmount = -11m;
        var plain = await CreateAsync(client, ledger.LedgerId, accountA.Id, StatementStart.AddDays(1), "Payee A",
            (categoryA.Id, PlainAmount));
        var split = await CreateAsync(client, ledger.LedgerId, accountA.Id, StatementStart.AddDays(2), "Payee B",
            (categoryA.Id, LineOne), (categoryB.Id, LineTwo));
        var twoLines = await CreateAsync(client, ledger.LedgerId, accountA.Id, StatementStart.AddDays(3), "Payee C",
            (accountB, LineOne), (accountB, LineTwo));
        var transfer = await CreateAsync(client, ledger.LedgerId, accountA.Id, StatementStart.AddDays(4), "Payee D",
            (accountB, TransferAmount));
        var elsewhere = await CreateAsync(client, ledger.LedgerId, accountC, StatementStart.AddDays(4), "Payee E",
            (categoryA.Id, PlainAmount));

        const decimal Shares = 10m, Price = 20m;
        var buy = await CreateInvestmentAsync(client, ledger.LedgerId, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerageA.Id, Action = "buy", SecurityId = security,
            Shares = Shares, Price = Price, Amount = Shares * Price,
            PostedAt = StatementStart.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        });
        var inKind = await CreateInvestmentAsync(client, ledger.LedgerId, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerageA.Id, Action = "transfer_shares", SecurityId = security,
            Shares = Shares, TransferAccountId = brokerageB.Id,
            PostedAt = StatementStart.AddDays(6).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        });

        var page = await GetEntriesAsync(client, ledger.LedgerId);
        Assert.False(HasMore(page));
        var entries = Listed(page);

        // Premises: each shape is on the page, as many times as its registers show it.
        Assert.Single(entries, e => e.HeaderId == plain && e.AccountId == accountA.Id && !e.IsGroup);
        Assert.Single(entries, e => e.HeaderId == split && e.AccountId == accountA.Id && e.IsGroup);
        Assert.Single(entries, e => e.HeaderId == twoLines && e.AccountId == accountA.Id && e.IsGroup);
        Assert.Equal(2, entries.Count(e => e.HeaderId == twoLines && e.AccountId == accountB));
        Assert.Single(entries, e => e.HeaderId == transfer && e.AccountId == accountA.Id);
        Assert.Single(entries, e => e.HeaderId == transfer && e.AccountId == accountB);
        Assert.Single(entries, e => e.HeaderId == buy && e.AccountId == brokerageA.Id);
        Assert.Single(entries, e => e.HeaderId == inKind && e.AccountId == brokerageA.Id);
        Assert.Single(entries, e => e.HeaderId == inKind && e.AccountId == brokerageB.Id);
        Assert.Single(entries, e => e.HeaderId == elsewhere && e.AccountId == accountC);
        // Categories and siblings have no entries here.
        Assert.DoesNotContain(entries, e => e.AccountId == categoryA.Id || e.AccountId == categoryB.Id
            || e.AccountId == brokerageA.HoldingsAccountId || e.AccountId == brokerageB.HoldingsAccountId);

        // Newest first.
        var dates = entries.Select(e => (e.IsGroup ? e.Entry.GetProperty("legs")[0] : e.Entry.GetProperty("txn"))
            .GetProperty("postedAt").GetDateTime()).ToList();
        Assert.Equal(dates.OrderByDescending(d => d).ToList(), dates);

        // Each account's currency.
        Assert.All(entries.Where(e => e.AccountId == accountC), e => Assert.Equal(OtherCurrency, e.CurrencyCode));
        Assert.All(entries.Where(e => e.AccountId != accountC), e => Assert.Equal("USD", e.CurrencyCode));

        await AssertDrawnAsTheRegisterDrawsAsync(client, ledger.LedgerId, entries);
    }

    // ---- direction narrows before the page is cut ----------------------------------

    /// <summary>
    /// With more outflows than the page holds, interleaved newest first with inflows, an
    /// outflow page of <c>limit</c> is full, holds outflows only and says more follow; a
    /// transfer is an outflow on the account it leaves and an inflow on the one it reaches.
    /// </summary>
    [Fact]
    public async Task An_outflow_page_is_full_when_enough_outflows_exist()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");
        var accountB = await ledger.AddBankAccountAsync("Account B");
        var categoryA = await ledger.AddCategoryAsync("Category A");
        var categoryB = await ledger.AddCategoryAsync("Category B");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        const int Limit = 4;
        const int Pairs = Limit + 2;   // more outflows than one page holds
        const decimal Outflow = -9m, Inflow = 9m, LineOne = -6m, LineTwo = -2m;
        var newest = StatementStart.AddDays(2 * Pairs + 1);
        // Newest of all: a transfer, an outflow on Account A and an inflow on Account B.
        var transfer = await CreateAsync(client, ledger.LedgerId, accountA.Id, newest, "Payee T", (accountB.Id, Outflow));
        for (var i = 0; i < Pairs; i++)
        {
            var day = newest.AddDays(-(2 * i + 1));
            await CreateAsync(client, ledger.LedgerId, accountA.Id, day, "Payee I", (categoryA.Id, Inflow));
            if (i == 0)
                await CreateAsync(client, ledger.LedgerId, accountA.Id, day.AddDays(-1), "Payee S",
                    (categoryA.Id, LineOne), (categoryB.Id, LineTwo));
            else
                await CreateAsync(client, ledger.LedgerId, accountA.Id, day.AddDays(-1), "Payee O", (categoryA.Id, Outflow));
        }

        // Anchor: unfiltered, the newest page mixes inflows into it.
        var all = Listed(await GetEntriesAsync(client, ledger.LedgerId, $"limit={Limit}"));
        Assert.Contains(all, e => e.Amount > 0m);

        var outflowPage = await GetEntriesAsync(client, ledger.LedgerId, $"direction=outflow&limit={Limit}");
        var outflows = Listed(outflowPage);
        Assert.Equal(Limit, outflows.Count);
        Assert.All(outflows, e => Assert.True(e.Amount < 0m, $"{e.HeaderId} on {e.AccountId}: {e.Amount}"));
        Assert.True(HasMore(outflowPage));
        Assert.Contains(outflows, e => e.HeaderId == transfer && e.AccountId == accountA.Id);
        Assert.Contains(outflows, e => e.IsGroup);
        Assert.DoesNotContain(outflows, e => e.AccountId == accountB.Id);

        var inflows = Listed(await GetEntriesAsync(client, ledger.LedgerId, $"direction=inflow&limit={Limit}"));
        Assert.Equal(Limit, inflows.Count);
        Assert.All(inflows, e => Assert.True(e.Amount > 0m, $"{e.HeaderId} on {e.AccountId}: {e.Amount}"));
        Assert.Contains(inflows, e => e.HeaderId == transfer && e.AccountId == accountB.Id);

        // "all" is the default spelled out.
        var spelled = Listed(await GetEntriesAsync(client, ledger.LedgerId, $"direction=all&limit={Limit}"));
        Assert.Equal(all.Select(e => (e.AccountId, e.Entry.GetRawText())), spelled.Select(e => (e.AccountId, e.Entry.GetRawText())));
    }

    /// <summary>
    /// A split whose two lines land on one other account with opposite signs is two rows
    /// there, and each takes the direction of the amount its row shows, although the
    /// transaction's net on that account, which both rows carry, has one sign. On its own
    /// account the split is one group, by its net.
    /// </summary>
    [Fact]
    public async Task Each_line_on_another_account_takes_the_direction_of_the_amount_its_row_shows()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");
        var accountB = await ledger.AddBankAccountAsync("Account B");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // From Account A: one line into Account B, and a smaller one back out of it.
        const decimal LineIn = -6m, LineBack = 1m;
        var split = await CreateAsync(client, ledger.LedgerId, accountA.Id, StatementStart, "Payee A",
            (accountB.Id, LineIn), (accountB.Id, LineBack));

        // Anchor: unfiltered, each row is its register's, and Account B shows the split as
        // two rows of opposite sign whose net there is above zero, so a direction read off
        // the net would put both rows on one side.
        var all = Listed(await GetEntriesAsync(client, ledger.LedgerId));
        await AssertDrawnAsTheRegisterDrawsAsync(client, ledger.LedgerId, all);
        var onB = all.Where(e => e.AccountId == accountB.Id && e.HeaderId == split).ToList();
        Assert.Equal(2, onB.Count);
        Assert.All(onB, e => Assert.True(e.NetOnAccount > 0m, $"net on the account: {e.NetOnAccount}"));
        var rowIn = Assert.Single(onB, e => e.Amount > 0m).Entry.GetRawText();
        var rowOut = Assert.Single(onB, e => e.Amount < 0m).Entry.GetRawText();
        var group = Assert.Single(all, e => e.AccountId == accountA.Id && e.HeaderId == split);
        Assert.True(group.IsGroup && group.Amount < 0m, $"the split on its own account: {group.Amount}");

        var outflows = Listed(await GetEntriesAsync(client, ledger.LedgerId, "direction=outflow"));
        var inflows = Listed(await GetEntriesAsync(client, ledger.LedgerId, "direction=inflow"));
        Assert.Equal(new[] { rowOut }, outflows.Where(e => e.AccountId == accountB.Id).Select(e => e.Entry.GetRawText()));
        Assert.Equal(new[] { rowIn }, inflows.Where(e => e.AccountId == accountB.Id).Select(e => e.Entry.GetRawText()));
        Assert.Equal(new[] { group.Entry.GetRawText() },
            outflows.Where(e => e.AccountId == accountA.Id).Select(e => e.Entry.GetRawText()));
        Assert.DoesNotContain(inflows, e => e.AccountId == accountA.Id);
    }

    // ---- the caller's today cuts what the register calls Scheduled -----------------

    [Fact]
    public async Task An_entry_dated_after_the_callers_today_is_left_out_and_todays_stays()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");
        var categoryA = await ledger.AddCategoryAsync("Category A");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var today = new DateOnly(2026, 3, 10);
        const decimal Amount = -4m;
        var onToday = await CreateAsync(client, ledger.LedgerId, accountA.Id, today, "Payee A", (categoryA.Id, Amount));
        var onTomorrow = await CreateAsync(client, ledger.LedgerId, accountA.Id, today.AddDays(1), "Payee B", (categoryA.Id, Amount));

        // Anchor: without a today, tomorrow's row is listed (first, as the newest).
        var uncut = Listed(await GetEntriesAsync(client, ledger.LedgerId));
        Assert.Equal(new[] { onTomorrow, onToday }, uncut.Select(e => e.HeaderId));

        var cut = Listed(await GetEntriesAsync(client, ledger.LedgerId,
            "today=" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Assert.Equal(new[] { onToday }, cut.Select(e => e.HeaderId));
    }

    // ---- what the route refuses -----------------------------------------------------

    [Fact]
    public async Task Limit_defaults_to_the_ceiling_and_anything_outside_it_or_an_unknown_direction_is_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        const decimal Amount = -1m;
        await ImportAsync(client, ledger.LedgerId, accountA.Id, "ACCOUNT-A",
            [.. Enumerable.Range(0, TransactionsEndpoints.EntriesMaxLimit + 1)
                .Select(i => ($"FITID-{i:D3}", StatementStart.AddDays(i), Amount, "PAYEE A"))]);

        var byDefault = await GetEntriesAsync(client, ledger.LedgerId);
        Assert.Equal(TransactionsEndpoints.EntriesMaxLimit, Listed(byDefault).Count);
        Assert.True(HasMore(byDefault));
        var atCeiling = await GetEntriesAsync(client, ledger.LedgerId, $"limit={TransactionsEndpoints.EntriesMaxLimit}");
        Assert.Equal(TransactionsEndpoints.EntriesMaxLimit, Listed(atCeiling).Count);
        Assert.Single(Listed(await GetEntriesAsync(client, ledger.LedgerId, "limit=1")));

        Assert.Equal("register-limit-invalid", await RefusalCodeAsync(client, ledger.LedgerId, "limit=0"));
        Assert.Equal("register-limit-invalid",
            await RefusalCodeAsync(client, ledger.LedgerId, $"limit={TransactionsEndpoints.EntriesMaxLimit + 1}"));
        // The register's paging direction is not a cash-flow direction.
        Assert.Equal("transaction-entries-direction-invalid", await RefusalCodeAsync(client, ledger.LedgerId, "direction=before"));
        Assert.Equal("transaction-entries-direction-invalid", await RefusalCodeAsync(client, ledger.LedgerId, "direction=Outflow"));
    }

    // ---- who may read it --------------------------------------------------------------

    [Fact]
    public async Task A_viewer_reads_the_entries_and_a_user_without_a_grant_is_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");
        var categoryA = await ledger.AddCategoryAsync("Category A");
        var stranger = await SyntheticLedger.CreateAsync(Db);
        var viewer = await ledger.AddMemberAsync("viewer");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var owner = await AuthedClientAsync(factory, ledger);
        using var viewerClient = await MemberClientAsync(factory, viewer);
        using var strangerClient = await AuthedClientAsync(factory, stranger);

        const decimal Amount = -2m;
        var row = await CreateAsync(owner, ledger.LedgerId, accountA.Id, StatementStart, "Payee A", (categoryA.Id, Amount));

        var seen = Listed(await GetEntriesAsync(viewerClient, ledger.LedgerId));
        Assert.Equal(new[] { row }, seen.Select(e => e.HeaderId));

        Assert.Equal("ledger-not-visible", await RefusalCodeAsync(strangerClient, ledger.LedgerId, ""));
    }

    // ---- fixtures ---------------------------------------------------------------------

    /// <summary>The statement id of the reinvest <see cref="OfxReinvestStatement"/> holds.</summary>
    private const string ReinvestFitId = "INV-FITID-ENTRIES-REINV";

    /// <summary>Import the reinvest statement: a brokerage row the importer flags, bank-shaped.</summary>
    private static async Task ImportReinvestAsync(HttpClient client, Guid ledgerId, Guid brokerageId)
    {
        var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(OfxReinvestStatement));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(body, "file", "statement.qfx");
        content.Add(new StringContent(brokerageId.ToString()), "accountId");
        content.Add(new StringContent("inv:brokerX:INV-0001"), "providerAccountId");
        var resp = await client.PostAsync($"/api/ledgers/{ledgerId}/ingest/ofx/import", content);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"reinvest import: {await resp.Content.ReadAsStringAsync()}");
    }

    private const string OfxReinvestStatement = $$"""
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
        <REINVEST>
        <INVTRAN>
        <FITID>{{ReinvestFitId}}
        <DTTRADE>20260120
        </INVTRAN>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <INCOMETYPE>DIV
        <TOTAL>-108.47
        <SUBACCTSEC>CASH
        <UNITS>4.321
        <UNITPRICE>25.10
        </REINVEST>
        </INVTRANLIST>
        </INVSTMTRS>
        </INVSTMTTRNRS>
        </INVSTMTMSGSRSV1>
        <SECLISTMSGSRSV1>
        <SECLIST>
        <STOCKINFO>
        <SECINFO>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <SECNAME>Fake Test Stock
        <TICKER>FAKE
        </SECINFO>
        </STOCKINFO>
        </SECLIST>
        </SECLISTMSGSRSV1>
        </OFX>
        """;
}
