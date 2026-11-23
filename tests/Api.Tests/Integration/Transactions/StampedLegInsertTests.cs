using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Reminders;
using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Every write path that creates legs inserts them with the stored presentation the rule
/// computes from what it saves, and leaves the ledger's stored values equal to the rule's:
/// the writer plans the notes, <see cref="AnnotationWriter.ApplyAsync"/> stamps the new
/// legs, and the recompute that follows the save finds nothing to change on them (it
/// writes their tag rows).
/// </summary>
/// <remarks>
/// <para><b>Production never refuses (ADR-0108 D7).</b> Whatever a save inserts, its recompute
/// leaves what the rule computes, and the consistency monitor checks stored == computed.
/// Every stored == computed check lives in the tests, CI and the monitor; this class is
/// where a path that skips the stamp, or stamps values its own save then replaces,
/// fails.</para>
/// <para><b>Every path, fail closed.</b> The paths are not picked by hand:
/// <see cref="Every_write_path_that_creates_legs_is_covered_by_a_test_here"/> finds every
/// method in the API that builds or adds a leg, follows a private helper to the methods
/// that call it, and fails on one with no entry in <see cref="Paths"/>, on an entry that
/// no longer creates legs, and on an entry naming a test that does not exist.</para>
/// <para><b>What each path's test asserts</b>, through the path's own endpoint (the
/// auto-post clone and the assistant's recategorize tools through the repository calls
/// they make, on the service factory), with <see cref="InsertRecorder"/> on the context,
/// after each step: every leg the step's saves inserted was
/// stamped (it carried stored values at the insert); what is stored for it now is what was
/// stamped, so no update replaced it; a fresh recompute writes nothing; and the
/// stored-equals-computed check over the ledger finds nothing.</para>
/// <para>The Moneydance import is not here: it inserts through Dapper and computes the
/// whole ledger inside its own transaction (<c>MoneydanceImportEndpointTests</c>).</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed partial class StampedLegInsertTests : DatabaseTest
{
    public StampedLegInsertTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Day(int month, int day) => new(2026, month, day, 12, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        SyntheticLedger Ledger, HttpClient Client, ApiFactory Factory,
        AccountRow BankA, AccountRow BankB, AccountRow CatA, AccountRow CatB, AccountRow Fee, AccountRow Income,
        AccountRow BrokerA, AccountRow BrokerB, Guid Security, InsertRecorder Inserts) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private Task<World> WorldAsync() => WorldAsync(f => f);

    private async Task<World> WorldAsync(Func<ApiFactory, ApiFactory> configure)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bankA = await ledger.AddBankAccountAsync("Account A");
        var bankB = await ledger.AddBankAccountAsync("Account B");
        var catA = await ledger.AddCategoryAsync("Category A");
        var catB = await ledger.AddCategoryAsync("Category B");
        var fee = await ledger.AddCategoryAsync("Category D");
        var income = await ledger.AddCategoryAsync("Category E", kind: "income");
        var brokerA = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var brokerB = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");
        var inserts = new InsertRecorder();
        var factory = configure(new ApiFactory(Db).WithoutDevAuth())
            .WithServices(s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(inserts)));
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return new World(ledger, client, factory, bankA, bankB, catA, catB, fee, income, brokerA, brokerB, security, inserts);
    }

    /// <summary>
    /// The saves that created <paramref name="headerIds"/>' new legs stamped every one of
    /// them, nothing replaced a stamped value afterwards, a fresh recompute writes nothing,
    /// and the ledger's stored values are what the rule computes. The recorder is then
    /// cleared, so the next step's saves are judged on their own inserts.
    /// </summary>
    private Task AssertInsertedWithValuesAsync(World w, params Guid[] headerIds) =>
        AssertSavedWithValuesAsync(w, requireInserts: true, headerIds);

    /// <summary>
    /// <see cref="AssertInsertedWithValuesAsync"/>, for a path that may keep every leg it
    /// reshapes rather than insert one: what it inserted, if anything, is held the same way.
    /// </summary>
    private async Task AssertSavedWithValuesAsync(World w, bool requireInserts, Guid[] headerIds)
    {
        Assert.NotEmpty(headerIds);
        await using var db = Db.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking().Where(l => headerIds.Contains(l.HeaderId)).ToListAsync();
        Assert.NotEmpty(legs);
        if (requireInserts)
            foreach (var header in headerIds)
                Assert.True(legs.Any(l => l.HeaderId == header && w.Inserts.Saw(l.Id)),
                    $"no leg of header {header} was seen inserted, so its path was not watched");

        var problems = new List<string>();
        foreach (var leg in legs)
        {
            if (!w.Inserts.Saw(leg.Id)) continue;
            var atInsert = w.Inserts.At(leg.Id);
            if (atInsert == InsertRecorder.Unstamped)
                problems.Add($"leg {leg.Id} of header {leg.HeaderId} was inserted without its stored notes");
            else if (atInsert != InsertRecorder.Describe(leg))
                problems.Add($"leg {leg.Id} of header {leg.HeaderId} was inserted with {atInsert}, then updated to {InsertRecorder.Describe(leg)}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        var writes = await LegNotes.RecomputeAsync(db, headerIds, CancellationToken.None);
        Assert.Equal((0, 0, 0), (writes.LegsUpdated, writes.TagRowsDeleted, writes.TagRowsInserted));

        var differences = await LegNotes.DifferencesAsync(db, w.Ledger.LedgerId, CancellationToken.None);
        Assert.True(differences.Count == 0, string.Join("\n", differences.Take(20)));
        w.Inserts.Clear();
    }

    /// <summary>The auto-post job's clone fire, on a service factory that also records inserts.</summary>
    private async Task<Guid> CloneFireAsync(World w, Guid reminderId, DateOnly occurrenceDate)
    {
        await using var db = Db.NewServiceFactoryWith(w.Inserts).Create();
        var repository = new RemindersRepository(
            db, new RecurrenceExpander(), new InvestmentTransactionsRepository(db, TimeProvider.System), new TransactionsRepository(db, TimeProvider.System),
            TimeProvider.System);
        var result = await repository.FireAsync(w.Ledger.LedgerId, reminderId, occurrenceDate);
        Assert.Equal(RemindersRepository.FireOutcome.Ok, result.Outcome);
        return result.HeaderId!.Value;
    }

    /// <summary>
    /// Records, at each save, the stored presentation every leg the save inserts carries:
    /// what the writer stamped, or <see cref="Unstamped"/>.
    /// </summary>
    private sealed class InsertRecorder : SaveChangesInterceptor
    {
        public const string Unstamped = "unstamped";

        private readonly ConcurrentDictionary<Guid, string> _inserted = new();

        public bool Saw(Guid legId) => _inserted.ContainsKey(legId);

        public string At(Guid legId) => _inserted[legId];

        public void Clear() => _inserted.Clear();

        public static string Describe(TxnLegRow leg) => leg.NotesKind is null
            ? Unstamped
            : string.Join('|', leg.NotesKind, leg.NotesIsReadOnly, leg.NotesGroupsByHeader, leg.NotesMemo ?? "∅",
                leg.NotesMemoInherited, leg.NotesInheritedMemo ?? "∅", leg.NotesInheritFromAccountId?.ToString() ?? "∅",
                leg.NotesTagsInherited, leg.NotesHeaderMemo ?? "∅", leg.NotesHeaderInPlay);

        private void Record(DbContext? context)
        {
            if (context is null) return;
            foreach (var entry in context.ChangeTracker.Entries<TxnLegRow>().Where(e => e.State == EntityState.Added))
                _inserted[entry.Entity.Id] = Describe(entry.Entity);
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Record(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static async Task<JsonElement> SendAsync(World w, HttpMethod method, string path, object body, HttpStatusCode? expected = null)
    {
        using var request = new HttpRequestMessage(method, $"/api/ledgers/{w.Ledger.LedgerId}{path}") { Content = JsonContent.Create(body) };
        var resp = await w.Client.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();
        if (expected is { } code) Assert.True(resp.StatusCode == code, $"{path}: {(int)resp.StatusCode} {text}");
        else Assert.True(resp.IsSuccessStatusCode, $"{path}: {(int)resp.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Guid> CreateBankAsync(World w, CreateTransactionRequest request) =>
        (await SendAsync(w, HttpMethod.Post, "/transactions", request, HttpStatusCode.Created)).GetProperty("headerId").GetGuid();

    private static async Task<Guid> CreateInvestmentAsync(World w, CreateInvestmentTransactionRequest request) =>
        (await SendAsync(w, HttpMethod.Post, "/investment-transactions", request, HttpStatusCode.Created)).GetProperty("headerId").GetGuid();

    private async Task<Guid> TemplateOfAsync(Guid reminderId)
    {
        await using var db = Db.NewDbContext();
        return (await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId).Select(r => r.TemplateHeaderId).SingleAsync())!.Value;
    }

    private static CreateReminderRequest BankReminder(World w, string payee) => new()
    {
        Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1), Payee = payee,
        SourceAccountId = w.BankA.Id,
        Postings =
        [
            new TransactionPosting { CounterpartyAccountId = w.CatA.Id, Amount = -30m },
            new TransactionPosting { CounterpartyAccountId = w.CatB.Id, Amount = -5m },
        ],
        Notes = new TransactionNotesRequest
        {
            Side = w.BankA.Id, Memo = "reminder memo", Tags = ["t-reminder"],
            Lines = [new TransactionLineNotesRequest { Index = 1, Memo = "line memo", Tags = ["t-line"] }],
        },
    };

    // ----- creates -----

    [Fact]
    public async Task A_bank_create_inserts_its_legs_with_their_values()
    {
        await using var w = await WorldAsync();
        var one = await CreateBankAsync(w, new CreateTransactionRequest
        {
            PostedAt = Day(3, 1), Payee = "Payee One", SourceAccountId = w.BankA.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = w.CatA.Id, Amount = -10m }],
            Notes = new TransactionNotesRequest { Side = w.BankA.Id, Memo = "one memo", Tags = ["t-one"] },
        });
        var transfer = await CreateBankAsync(w, new CreateTransactionRequest
        {
            PostedAt = Day(3, 2), Payee = "Payee Transfer", SourceAccountId = w.BankA.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = w.BankB.Id, Amount = -11m }],
        });
        var split = await CreateBankAsync(w, new CreateTransactionRequest
        {
            PostedAt = Day(3, 3), Payee = "Payee Split", SourceAccountId = w.BankA.Id,
            Postings =
            [
                new TransactionPosting { CounterpartyAccountId = w.CatA.Id, Amount = -12m },
                new TransactionPosting { CounterpartyAccountId = w.CatB.Id, Amount = -13m },
            ],
            Notes = new TransactionNotesRequest
            {
                Side = w.BankA.Id, Memo = "split memo", Tags = ["t-split", "t-one"],
                Lines = [new TransactionLineNotesRequest { Index = 0, Memo = "first line", Tags = ["t-line"] }],
            },
        });
        await AssertInsertedWithValuesAsync(w, one, transfer, split);
    }

    [Fact]
    public async Task An_investment_create_inserts_its_legs_with_their_values()
    {
        await using var w = await WorldAsync();
        var ids = new List<Guid>
        {
            // One posting, its memo and tags on the brokerage leg.
            await CreateInvestmentAsync(w, new()
            {
                BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Transfer, TransferAccountId = w.BankA.Id,
                Amount = 40m, PostedAt = Day(4, 1), Payee = "Payee Xfr", Memo = "xfr memo", Tags = ["t-xfr"],
            }),
            await CreateInvestmentAsync(w, new()
            {
                BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 10m, Price = 20m,
                PostedAt = Day(4, 2), Payee = "Payee Buy", Memo = "buy memo",
            }),
            // Several postings, the memo and tags on the header; the investment side
            // reads the brokerage's holdings link.
            await CreateInvestmentAsync(w, new()
            {
                BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 3m, Price = 21m,
                FeeAccountId = w.Fee.Id, FeeAmount = 3m, PostedAt = Day(4, 3), Payee = "Payee Buy Fee", Memo = "fee memo",
                Tags = ["t-fee"],
            }),
            await CreateInvestmentAsync(w, new()
            {
                BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.DividendCash, SecurityId = w.Security,
                CategoryAccountId = w.Income.Id, Amount = 12m, PostedAt = Day(4, 4), Payee = "Payee Dividend",
            }),
            // In kind: every brokerage on it, and theirs, is on the investment side.
            await CreateInvestmentAsync(w, new()
            {
                BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.TransferShares, SecurityId = w.Security, Shares = 4m,
                TransferAccountId = w.BrokerB.Id, PostedAt = Day(4, 5), Payee = "Payee In Kind", Memo = "in kind memo",
            }),
        };
        await AssertInsertedWithValuesAsync(w, [.. ids]);
    }

    [Fact]
    public async Task Reminder_template_creates_insert_their_legs_with_their_values()
    {
        await using var w = await WorldAsync();
        var bank = (await SendAsync(w, HttpMethod.Post, "/reminders", BankReminder(w, "Payee Reminder")))
            .GetProperty("id").GetGuid();
        var investment = (await SendAsync(w, HttpMethod.Post, "/reminders/investment", new CreateInvestmentReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1),
            Transaction = new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 1m, Price = 10m,
                FeeAccountId = w.Fee.Id, FeeAmount = 1m, PostedAt = Day(1, 1), Payee = "Payee Plan", Memo = "plan memo",
                Tags = ["t-plan"],
            },
        })).GetProperty("id").GetGuid();
        await AssertInsertedWithValuesAsync(w, await TemplateOfAsync(bank), await TemplateOfAsync(investment));
    }

    [Fact]
    public async Task A_managed_loan_reminder_inserts_its_template_legs_with_their_values()
    {
        await using var w = await WorldAsync();
        var loanId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            db.Accounts.Add(new AccountRow
            {
                Id = loanId, LedgerId = w.Ledger.LedgerId, Name = "Account C",
                AccountType = "loan", CurrencyCode = "USD", OpeningBalance = -100000m, IsActive = true,
            });
            await db.SaveChangesAsync();
            db.LoanTerms.Add(new LoanTermsRow
            {
                AccountId = loanId, LedgerId = w.Ledger.LedgerId,
                OriginalPrincipal = 100000m, AnnualInterestRate = 4.00m, Points = 0m,
                PaymentCount = 360, PaymentsPerYear = 12,
                EscrowAmount = 100m, InterestAccountId = w.CatA.Id, EscrowAccountId = w.BankB.Id,
                PaymentIsComputed = true, FixedPayment = null,
            });
            await db.SaveChangesAsync();
        }
        var reminder = (await SendAsync(w, HttpMethod.Post, $"/accounts/{loanId}/payment-reminder",
            new SetupPaymentReminderRequest(w.BankA.Id, new DateOnly(2026, 4, 10)))).GetProperty("reminderId").GetGuid();
        await AssertInsertedWithValuesAsync(w, await TemplateOfAsync(reminder));
    }

    [Fact]
    public async Task Posting_a_reminder_inserts_the_occurrences_legs_with_their_values()
    {
        await using var w = await WorldAsync();
        var bank = (await SendAsync(w, HttpMethod.Post, "/reminders", BankReminder(w, "Payee Fired"))).GetProperty("id").GetGuid();
        CreateInvestmentTransactionRequest Event() => new()
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Transfer, TransferAccountId = w.BankA.Id,
            Amount = 75m, PostedAt = Day(1, 1), Payee = "Payee Event", Memo = "event memo", Tags = ["t-event"],
        };
        var investment = (await SendAsync(w, HttpMethod.Post, "/reminders/investment", new CreateInvestmentReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1), Transaction = Event(),
        })).GetProperty("id").GetGuid();

        // The auto-post clone (the job's call), and both by-hand posts.
        var cloned = await CloneFireAsync(w, bank, new DateOnly(2026, 1, 1));
        var postedBank = (await SendAsync(w, HttpMethod.Post, $"/reminders/{bank}/fire/bank", new FireBankReminderRequest
        {
            OccurrenceDate = new DateOnly(2026, 2, 1), SourceAccountId = w.BankA.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = w.CatA.Id, Amount = -31m }],
            Notes = new TransactionNotesRequest { Side = w.BankA.Id, Memo = "posted memo", Tags = ["t-posted"] },
        })).GetProperty("headerId").GetGuid();
        var postedInvestment = (await SendAsync(w, HttpMethod.Post, $"/reminders/{investment}/fire/investment",
            new FireInvestmentReminderRequest { OccurrenceDate = new DateOnly(2026, 1, 1), Transaction = Event() }))
            .GetProperty("headerId").GetGuid();
        await AssertInsertedWithValuesAsync(w, cloned, postedBank, postedInvestment);
    }

    [Fact]
    public async Task A_file_import_inserts_its_legs_with_their_values()
    {
        await using var w = await WorldAsync();
        const string qif = """
            !Type:Bank
            D01/05/2026
            T-12.34
            PCOFFEE SHOP
            MCOFFEE SHOP 0042
            ^
            D01/15/2026
            T2500.00
            PPAYROLL
            ^
            """;
        using var content = new MultipartFormDataContent();
        var stream = new ByteArrayContent(Encoding.UTF8.GetBytes(qif));
        stream.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(stream, "file", "export.qif");
        content.Add(new StringContent(w.BankA.Id.ToString()), "accountId");
        content.Add(new StringContent("qif"), "providerAccountId");
        var resp = await w.Client.PostAsync($"/api/ledgers/{w.Ledger.LedgerId}/ingest/qif/import", content);
        Assert.True(resp.StatusCode == HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        await using var db = Db.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == w.Ledger.LedgerId).Select(h => h.Id).ToListAsync();
        Assert.Equal(2, headers.Count);
        Assert.True(await db.TxnLegs.AsNoTracking().AnyAsync(l => headers.Contains(l.HeaderId) && l.LegMemo != null),
            "no imported row carried a memo, so the stamp of a memo was not exercised");
        await AssertInsertedWithValuesAsync(w, [.. headers]);
    }

    // ----- new legs on an existing transaction -----

    [Fact]
    public async Task Reshapes_insert_their_new_legs_with_their_values()
    {
        await using var w = await WorldAsync();

        // A one-split split into two lines: the new posting's legs are stamped, the
        // kept ones are recomputed.
        var bank = await CreateBankAsync(w, new CreateTransactionRequest
        {
            PostedAt = Day(5, 1), Payee = "Payee Reshape", SourceAccountId = w.BankA.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = w.CatA.Id, Amount = -20m }],
            Notes = new TransactionNotesRequest { Side = w.BankA.Id, Memo = "before split", Tags = ["t-before"] },
        });
        await AssertInsertedWithValuesAsync(w, bank);
        Guid sourceLeg;
        await using (var db = Db.NewDbContext())
            sourceLeg = await db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == bank && l.AccountId == w.BankA.Id).Select(l => l.Id).SingleAsync();
        await SendAsync(w, HttpMethod.Patch, $"/transactions/{bank}", new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = w.BankA.Id,
                Items =
                [
                    new TransactionPosting { LegId = sourceLeg, CounterpartyAccountId = w.CatA.Id, Amount = -15m },
                    new TransactionPosting { CounterpartyAccountId = w.CatB.Id, Amount = -5m },
                ],
            },
        });
        await AssertInsertedWithValuesAsync(w, bank);

        // An event gaining a fee: one posting becomes two.
        var investment = await CreateInvestmentAsync(w, new()
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 2m, Price = 10m,
            PostedAt = Day(5, 2), Payee = "Payee Fee Added", Memo = "event memo",
        });
        await AssertInsertedWithValuesAsync(w, investment);
        await SendAsync(w, HttpMethod.Patch, $"/investment-transactions/{investment}", new PatchInvestmentTransactionRequest
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 2m, Price = 10m,
            FeeAccountId = w.Fee.Id, FeeAmount = 1m, PostedAt = Day(5, 2),
        });
        await AssertInsertedWithValuesAsync(w, investment);

        // A reminder template whose lines are replaced.
        var reminder = (await SendAsync(w, HttpMethod.Post, "/reminders", BankReminder(w, "Payee Edited"))).GetProperty("id").GetGuid();
        await AssertInsertedWithValuesAsync(w, await TemplateOfAsync(reminder));
        await SendAsync(w, HttpMethod.Patch, $"/reminders/{reminder}", new EditReminderRequest
        {
            Postings = new PatchReminderPostings
            {
                SourceAccountId = w.BankA.Id,
                Items =
                [
                    new TransactionPosting { CounterpartyAccountId = w.CatB.Id, Amount = -9m },
                    new TransactionPosting { CounterpartyAccountId = w.CatA.Id, Amount = -8m },
                    new TransactionPosting { CounterpartyAccountId = w.Fee.Id, Amount = -7m },
                ],
            },
        });
        await AssertInsertedWithValuesAsync(w, await TemplateOfAsync(reminder));

        // An investment reminder whose event is rebuilt with a fee.
        var plan = (await SendAsync(w, HttpMethod.Post, "/reminders/investment", new CreateInvestmentReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1),
            Transaction = new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 1m, Price = 10m,
                PostedAt = Day(1, 1), Payee = "Payee Plan Edited", Memo = "plan memo",
            },
        })).GetProperty("id").GetGuid();
        await AssertInsertedWithValuesAsync(w, await TemplateOfAsync(plan));
        await SendAsync(w, HttpMethod.Patch, $"/reminders/{plan}/investment", new EditInvestmentReminderRequest
        {
            Transaction = new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 1m, Price = 10m,
                FeeAccountId = w.Fee.Id, FeeAmount = 1m, PostedAt = Day(1, 1), Payee = "Payee Plan Edited", Memo = "plan memo",
            },
        });

        await AssertInsertedWithValuesAsync(w, await TemplateOfAsync(plan));
    }

    [Fact]
    public async Task Recategorizing_saves_what_it_inserts_with_its_values()
    {
        // The assistant's recategorize tools, through the repository calls they make: a
        // one-posting transaction moved to another category, and one line of a split.
        await using var w = await WorldAsync();
        var one = await CreateBankAsync(w, new CreateTransactionRequest
        {
            PostedAt = Day(7, 1), Payee = "Payee Recategorized", SourceAccountId = w.BankA.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = w.CatA.Id, Amount = -10m }],
            Notes = new TransactionNotesRequest { Side = w.BankA.Id, Memo = "one memo", Tags = ["t-one"] },
        });
        var split = await CreateBankAsync(w, new CreateTransactionRequest
        {
            PostedAt = Day(7, 2), Payee = "Payee Split Recategorized", SourceAccountId = w.BankA.Id,
            Postings =
            [
                new TransactionPosting { CounterpartyAccountId = w.CatA.Id, Amount = -12m },
                new TransactionPosting { CounterpartyAccountId = w.CatB.Id, Amount = -13m },
            ],
            Notes = new TransactionNotesRequest
            {
                Side = w.BankA.Id, Memo = "split memo", Tags = ["t-split"],
                Lines = [new TransactionLineNotesRequest { Index = 0, Memo = "moved line", Tags = ["t-line"] }],
            },
        });
        await AssertInsertedWithValuesAsync(w, one, split);

        await using (var db = Db.NewServiceFactoryWith(w.Inserts).Create())
        {
            var repository = new TransactionsRepository(db, TimeProvider.System);
            Assert.Equal(TransactionsRepository.RecategorizeResult.Ok,
                (await repository.RecategorizeAsync(w.Ledger.LedgerId, one, w.CatB.Id, dryRun: false)).Result);
            Assert.Equal(TransactionsRepository.SplitPostingRecategorizeResult.Ok,
                (await repository.RecategorizeSplitPostingsAsync(w.Ledger.LedgerId, split, w.CatA.Id, w.Fee.Id, dryRun: false)).Result);
        }
        await using (var check = Db.NewDbContext())
        {
            var accounts = await check.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == one || l.HeaderId == split).Select(l => new { l.HeaderId, l.AccountId }).ToListAsync();
            Assert.Contains(accounts, l => l.HeaderId == one && l.AccountId == w.CatB.Id);
            Assert.Contains(accounts, l => l.HeaderId == split && l.AccountId == w.Fee.Id);
            Assert.DoesNotContain(accounts, l => l.AccountId == w.CatA.Id);
        }
        await AssertSavedWithValuesAsync(w, requireInserts: false, [one, split]);
    }

    [Fact]
    public async Task A_feed_sync_inserts_its_legs_with_their_values()
    {
        const string body = """
            {"connections":[{"conn_id":"c-N","name":"Bank N","org_id":"bankn","sfin_url":"https://sfin/bankn"}],
             "errlist":[],"accounts":[
              {"id":"sf-n","conn_id":"c-N","name":"Checking","currency":"USD","balance":"100.00","transactions":[
                {"id":"t-1","posted":1715000000,"amount":"-42.50","payee":"Corner Cafe","description":"CORNER CAFE 0042"},
                {"id":"t-2","posted":1715000100,"amount":"-7.00","description":"KIOSK"}
              ]}
            ]}
            """;
        await using var w = await WorldAsync(f => f.WithService<SimpleFinClient>(_ => new SimpleFinClient(
            new HttpClient(new StubHandler(req => req.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("https://u:p@bridge.simplefin.org/simplefin/access/abc") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })),
            TimeProvider.System)));
        var keys = Db.NewLedgerKeyService();
        await using (var seed = Db.NewDbContext())
        {
            var row = await seed.Ledgers.SingleAsync(l => l.Id == w.Ledger.LedgerId);
            row.WrappedLek = keys.CreateWrappedLek();
            row.LekKekId = keys.CurrentKekId;
            row.LekCreatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://x/c")).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var connection = (await SendAsync(w, HttpMethod.Post, "/feed-connections", new CreateFeedConnectionRequest { SetupToken = token }))
            .GetProperty("id").GetGuid();
        await SendAsync(w, HttpMethod.Patch, $"/accounts/{w.BankA.Id}/feed-mapping",
            new PatchAccountFeedMappingRequest { FeedConnectionId = connection, SimpleFinAccountId = "sf-n" }, HttpStatusCode.NoContent);
        var sync = await w.Client.PostAsync($"/api/ledgers/{w.Ledger.LedgerId}/feed-connections/{connection}/sync", content: null);
        Assert.True(sync.StatusCode == HttpStatusCode.OK, await sync.Content.ReadAsStringAsync());

        await using var db = Db.NewDbContext();
        var headers = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == w.Ledger.LedgerId).Select(h => h.Id).ToListAsync();
        Assert.Equal(2, headers.Count);
        await AssertInsertedWithValuesAsync(w, [.. headers]);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    // ----- what production does with an insert the stamp got wrong -----

    private TxnHeaderRow NewHeader(World w, Guid id)
    {
        var header = TxnHeaderRow.NewTransaction(id, w.Ledger.LedgerId, w.BankA.Id);
        header.Payee = "Payee Recomputed";
        header.PostedAt = Day(6, 1);
        header.TransactedAt = header.PostedAt;
        return header;
    }

    private static TxnLegRow Leg(Guid headerId, Guid ledgerId, Guid accountId, decimal amount, int postingIndex = 0) => new()
    {
        Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId, AccountId = accountId, PostingIndex = postingIndex, Amount = amount,
    };

    [Fact]
    public async Task A_leg_inserted_without_stored_values_is_saved_with_what_the_rule_computes()
    {
        // The stamp spares a second write; it is not what makes the stored values right.
        // A save that inserts a leg without them goes through, and its recompute fills them.
        await using var w = await WorldAsync();
        var id = Guid.NewGuid();
        await using (var db = Db.NewServiceFactory().Create())
        {
            db.TxnHeaders.Add(NewHeader(w, id));
            db.TxnLegs.AddRange(Leg(id, w.Ledger.LedgerId, w.BankA.Id, -5m), Leg(id, w.Ledger.LedgerId, w.CatA.Id, 5m));
            await db.SaveChangesUnderLedgerLockAsync(w.Ledger.LedgerId);
        }
        await AssertStoredAsync(w, id);

        // A save whose only change is a new posting on that transaction: the one-split
        // becomes a split, so its kept legs change too.
        await using (var db = Db.NewServiceFactory().Create())
        {
            db.TxnLegs.AddRange(Leg(id, w.Ledger.LedgerId, w.BankA.Id, -3m, 1), Leg(id, w.Ledger.LedgerId, w.CatB.Id, 3m, 1));
            await db.SaveChangesUnderLedgerLockAsync(w.Ledger.LedgerId);
        }
        await AssertStoredAsync(w, id);
    }

    [Fact]
    public async Task A_leg_stamped_with_values_its_save_then_changes_is_saved_with_what_the_rule_computes()
    {
        await using var w = await WorldAsync();
        var id = Guid.NewGuid();
        await using (var db = Db.NewServiceFactory().Create())
        {
            db.TxnHeaders.Add(NewHeader(w, id));
            var legs = new[] { Leg(id, w.Ledger.LedgerId, w.BankA.Id, -5m), Leg(id, w.Ledger.LedgerId, w.CatA.Id, 5m) };
            db.TxnLegs.AddRange(legs);
            LegNotes.Stamp(
                new NotesHeaderInput(id, null, w.BankA.Id, null, [],
                    [.. legs.Select(l => new NotesLegInput(l.Id, l.AccountId, l.PostingIndex, l.LegMemo, []))]),
                new Dictionary<Guid, Guid>(), legs);
            // An input changed after the stamp: the stamped values are no longer the rule's.
            legs[0].AssignLegMemo("set after the stamp");
            await db.SaveChangesUnderLedgerLockAsync(w.Ledger.LedgerId);
        }
        await AssertStoredAsync(w, id);
        await using var check = Db.NewDbContext();
        Assert.Equal("set after the stamp", await check.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == id && l.AccountId == w.CatA.Id).Select(l => l.NotesMemo).SingleAsync());
    }

    private async Task AssertStoredAsync(World w, Guid headerId)
    {
        await using var db = Db.NewDbContext();
        Assert.All(await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync(),
            l => Assert.NotNull(l.NotesKind));
        var differences = await LegNotes.DifferencesAsync(db, w.Ledger.LedgerId, CancellationToken.None);
        Assert.True(differences.Count == 0, string.Join("\n", differences.Take(20)));
    }

    // ----- the paths -----

    /// <summary>
    /// Every method of the API that creates legs, as <c>"file:Method"</c>, with the tests in
    /// this class that drive it and check what it inserted. A private helper is listed by
    /// the methods that call it, up to the first one that is not private.
    /// </summary>
    private static readonly Dictionary<string, string[]> Paths = new(StringComparer.Ordinal)
    {
        ["src/Api/Db/Repositories/TransactionsRepository.cs:CreateAsync"] =
            [nameof(A_bank_create_inserts_its_legs_with_their_values), nameof(Reshapes_insert_their_new_legs_with_their_values)],
        ["src/Api/Db/Repositories/TransactionsRepository.cs:PatchAsync"] =
            [nameof(Reshapes_insert_their_new_legs_with_their_values)],
        ["src/Api/Db/Repositories/TransactionsRepository.cs:RecategorizeAsync"] =
            [nameof(Recategorizing_saves_what_it_inserts_with_its_values)],
        ["src/Api/Db/Repositories/TransactionsRepository.cs:RecategorizeSplitPostingsAsync"] =
            [nameof(Recategorizing_saves_what_it_inserts_with_its_values)],
        ["src/Api/Db/Repositories/InvestmentTransactionsRepository.cs:CreateAsync"] =
            [nameof(An_investment_create_inserts_its_legs_with_their_values), nameof(Posting_a_reminder_inserts_the_occurrences_legs_with_their_values)],
        ["src/Api/Db/Repositories/InvestmentTransactionsRepository.cs:PatchAsync"] =
            [nameof(Reshapes_insert_their_new_legs_with_their_values)],
        ["src/Api/Db/Repositories/InvestmentTransactionsRepository.cs:BuildTemplateLegsAsync"] =
            [nameof(Reminder_template_creates_insert_their_legs_with_their_values), nameof(Reshapes_insert_their_new_legs_with_their_values)],
        ["src/Api/Db/Repositories/RemindersRepository.cs:CreateBankAsync"] =
            [nameof(Reminder_template_creates_insert_their_legs_with_their_values), nameof(Reshapes_insert_their_new_legs_with_their_values)],
        ["src/Api/Db/Repositories/RemindersRepository.cs:CreateInvestmentAsync"] =
            [nameof(Reminder_template_creates_insert_their_legs_with_their_values), nameof(Reshapes_insert_their_new_legs_with_their_values)],
        ["src/Api/Db/Repositories/RemindersRepository.cs:CreateManagedLoanReminderAsync"] =
            [nameof(A_managed_loan_reminder_inserts_its_template_legs_with_their_values)],
        ["src/Api/Db/Repositories/RemindersRepository.cs:EditBankAsync"] =
            [nameof(Reshapes_insert_their_new_legs_with_their_values)],
        ["src/Api/Db/Repositories/RemindersRepository.cs:EditInvestmentAsync"] =
            [nameof(Reshapes_insert_their_new_legs_with_their_values)],
        ["src/Api/Db/Repositories/RemindersRepository.cs:FireAsync"] =
            [nameof(Posting_a_reminder_inserts_the_occurrences_legs_with_their_values)],
        ["src/Api/Ingest/IngestOrchestrator.cs:RunFileAsync"] =
            [nameof(A_file_import_inserts_its_legs_with_their_values)],
        ["src/Api/Ingest/IngestOrchestrator.cs:RunPullAsync"] =
            [nameof(A_feed_sync_inserts_its_legs_with_their_values)],
    };

    [Fact]
    public void Every_write_path_that_creates_legs_is_covered_by_a_test_here()
    {
        var root = RepoRoot();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Api"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count > 100, $"scanned only {files.Count} source files; the guard is not reading src/Api");
        foreach (var file in files)
        {
            var code = CodeOnly(File.ReadAllText(file));
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var methods = Methods(code);
            var sites = CreatesLeg().Matches(code).Select(m => MethodAt(methods, m.Index)).Where(m => m is not null).Select(m => m!).ToList();
            // A private helper stands for the methods that call it, transitively.
            var queue = new Queue<Method>(sites);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (queue.TryDequeue(out var method))
            {
                if (!seen.Add(method.Name)) continue;
                if (!method.IsPrivate)
                {
                    found.Add($"{relative}:{method.Name}");
                    continue;
                }
                var callers = Regex.Matches(code, $@"\b{Regex.Escape(method.Name)}\b")
                    .Where(m => m.Index < method.Start || m.Index >= method.NameEnd)
                    .Select(m => MethodAt(methods, m.Index))
                    .Where(m => m is not null && m.Name != method.Name)
                    .ToList();
                Assert.True(callers.Count > 0, $"{relative}:{method.Name} creates legs and nothing in its file calls it");
                foreach (var caller in callers) queue.Enqueue(caller!);
            }
        }
        Assert.True(found.Count >= 10, $"found only {found.Count} paths that create legs; the scan is not finding them");

        var tests = typeof(StampedLegInsertTests).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        problems.AddRange(found.Where(f => !Paths.ContainsKey(f))
            .Select(f => $"{f} creates legs and no test here covers it: add a test that drives it and list it in Paths"));
        problems.AddRange(Paths.Keys.Where(k => !found.Contains(k))
            .Select(k => $"{k} is listed but no longer creates legs: remove the entry"));
        problems.AddRange(Paths.SelectMany(p => p.Value.Where(t => !tests.Contains(t)).Select(t => $"{p.Key} names {t}, which is not a test here")));
        problems.AddRange(Paths.Where(p => p.Value.Length == 0).Select(p => $"{p.Key} lists no test"));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private sealed record Method(string Name, bool IsPrivate, int Start, int NameEnd);

    /// <summary>A leg built, or a leg set added to the context.</summary>
    [GeneratedRegex(@"\bnew\s+TxnLegRow\b|\bTxnLegRow\s+\w+\s*\([^)]*\)\s*=>\s*new\s*\(|\bTxnLegs\s*\.\s*Add(?:Range)?(?:Async)?\s*\(|\b(?:Add|AddRange|Attach|AttachRange)\s*\(\s*new\s+TxnLegRow\b")]
    private static partial Regex CreatesLeg();

    /// <summary>A member of a type: its access, then its name and its parameter list.</summary>
    [GeneratedRegex(@"^[ \t]+((?:public|private|internal|protected)(?:\s+(?:static|async|override|sealed|virtual|new|readonly|unsafe|extern|partial))*)\s+[\w<>\[\],.?() ]+?\s+(\w+)\s*(?:<[^>()]*>)?\s*\(", RegexOptions.Multiline)]
    private static partial Regex MemberHeader();

    private static List<Method> Methods(string code) =>
    [
        .. MemberHeader().Matches(code).Select(m => new Method(
            m.Groups[2].Value,
            m.Groups[1].Value.StartsWith("private", StringComparison.Ordinal),
            m.Index,
            m.Groups[2].Index + m.Groups[2].Length)),
    ];

    private static Method? MethodAt(List<Method> methods, int index) => methods.LastOrDefault(m => m.Start <= index);

    /// <summary>The code with comments blanked, so a mention in prose is not a site.</summary>
    private static string CodeOnly(string code) =>
        Regex.Replace(code, @"//[^\n]*|/\*[\s\S]*?\*/", m => new string(' ', m.Length));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile")) && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
