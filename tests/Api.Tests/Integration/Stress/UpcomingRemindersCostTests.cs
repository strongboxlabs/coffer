using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// What the agenda read costs with its per-occurrence source account and auto-post
/// reason, beside the auto-post job's scan, which reads the same agenda.
/// </summary>
/// <remarks>
/// A ledger with many series, half of them auto-posting, each with months of posted
/// occurrences, so both branches of the agenda (posted and still due) carry rows. The
/// home screen asks for one fixed window, ninety days back and thirty ahead; the scan
/// asks for its own. No timing assertion, per this lane's rule: the printed figures are
/// the output. What is asserted is that both reads saw the seeded volume.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class UpcomingRemindersCostTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public UpcomingRemindersCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private const int Series = 200;
    private const int PostedMonths = 6;
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly Today = new(2026, 7, 10);

    [Fact]
    public async Task The_agenda_and_the_auto_post_scan_print_their_cost()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var banks = new List<Guid>();
        for (var i = 0; i < 10; i++)
            banks.Add((await ledger.AddBankAccountAsync($"account-{i:D2}", openingBalance: 100_000m)).Id);
        var category = await ledger.AddCategoryAsync("category-a");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");

        var seed = Stopwatch.StartNew();
        var seriesIds = new List<Guid>();
        for (var i = 0; i < Series; i++)
        {
            var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
            {
                Rrule = $"FREQ=MONTHLY;BYMONTHDAY={1 + (i % 28)}",
                StartDate = Start,
                AutoCommitDaysBefore = i % 2 == 0 ? 3 : null,
                Payee = $"payee-{i:D3}",
                SourceAccountId = banks[i % banks.Count],
                Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -10m - i }],
            });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            seriesIds.Add((await resp.Content.ReadFromJsonAsync<ReminderDetail>())!.Id);
        }
        for (var i = 0; i < Series; i++)
            for (var m = 0; m < PostedMonths; m++)
                await ReminderCloneFire.PostedAsync(Db, ledger.LedgerId, seriesIds[i], Start.AddMonths(m).AddDays(i % 28));
        seed.Stop();

        await using var db = Db.NewServiceFactory().Create();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET statement_timeout = '30s'");
        var repository = new RemindersRepository(
            db, new RecurrenceExpander(), new InvestmentTransactionsRepository(db, TimeProvider.System),
            new TransactionsRepository(db, TimeProvider.System), TimeProvider.System);

        async Task<(T Result, double Ms)> TimeAsync<T>(Func<Task<T>> read)
        {
            await read();   // warm
            var samples = new List<double>();
            T result = default!;
            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                result = await read();
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            return (result, samples[1]);
        }

        var (agenda, agendaMs) = await TimeAsync(() =>
            repository.GetUpcomingAsync(ledger.LedgerId, Today.AddDays(-90), Today.AddDays(30)));
        var (scan, scanMs) = await TimeAsync(() =>
            repository.ScanForAutoPostAsync(ledger.LedgerId, Today, catchUpDays: 45, maxAutoCommitDaysBefore: 30));

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"upcoming: seed {seed.Elapsed.TotalSeconds:F1}s ({Series} series, {Series * PostedMonths} posted); agenda {agendaMs:F0} ms over {agenda.Count} occurrences; auto-post scan {scanMs:F0} ms ({scan.Due.Count} due) (median of 3, warm)"));

        Assert.Contains(agenda, o => o.Kind == "scheduled" && o.SourceAccountName is not null);
        Assert.Contains(agenda, o => o.Kind == "reminder" && o.AutoPost is not null);
        Assert.True(agenda.Count >= Series * 3, $"expected the window to hold several months per series, got {agenda.Count}");
    }
}
