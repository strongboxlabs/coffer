using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The daily time bucket, and the session-timezone pin it rests on.
///
/// <para>These belong in one file because one is meaningless without the other.
/// Postgres extracts date parts from a <c>timestamptz</c> in the SESSION
/// timezone, so which DAY a transaction counts toward is decided by a setting,
/// not by the data. At month granularity an unpinned session is a bug you might
/// never notice; at day granularity it is wrong every day for anyone west of
/// UTC, and wrong in the direction that makes late-evening spending appear
/// tomorrow.</para>
///
/// <para>The pin itself lives on the CONNECTION STRING
/// (<c>DbSessionTimeZone</c>), not in a <c>SET</c> from the interceptor —
/// Npgsql sends it in the startup packet, so it holds before a connection can
/// run anything. <c>PostgresFixture</c> pins it the same way for the same
/// reason; the assertion below is deliberately written against the resulting
/// OFFSET so it cannot pass just because the fixture reached UTC some other
/// way.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DailyBucketAndTimeZoneTests
{
    private readonly PostgresFixture _fixture;

    public DailyBucketAndTimeZoneTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_session_timezone_is_UTC()
    {
        // Fail-closed. This passes today on a UTC-defaulting postgres:16 whether
        // or not anything pins it — which is exactly why the assertion is worth
        // having: it turns a coincidence of the base image into a promise, and
        // goes red if a deployment, a managed Postgres regional default or an
        // ALTER ROLE ever changes it underneath the reporting queries.
        // Asserted as an OFFSET, not a name. The first cut compared the name
        // against "UTC" and failed against a container reporting "Etc/UTC" —
        // the same zone, spelled differently, as are "Z" and "+00". What
        // actually has to hold is that date-part extraction agrees with the
        // C# UTC convention, and zero offset is that property stated directly.
        await using var db = _fixture.NewDbContext();
        var offsetSeconds = await db.Database
            .SqlQuery<double>($"SELECT EXTRACT(timezone FROM now())::float8 AS \"Value\"")
            .SingleAsync();

        Assert.Equal(0d, offsetSeconds);
    }

    [Fact]
    public async Task A_daily_bucket_puts_each_transaction_on_its_own_UTC_day()
    {
        // The three timestamps that catch a timezone slip: the first instant of
        // a day, the last, and one in the middle. Under any zone west of UTC the
        // 23:30 spend slides into the following day and the 00:15 one slides
        // back — so a passing assertion here is a real statement about the
        // session zone, not just about the GROUP BY.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var cat = await ledger.AddCategoryAsync("Groceries", "expense");

        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 10m,
            new DateTime(2026, 9, 10, 0, 15, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 20m,
            new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 40m,
            new DateTime(2026, 9, 11, 23, 30, 0, DateTimeKind.Utc));

        var repo = new ReportingRepository(_fixture.NewDbContext());
        var result = await repo.SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger.LedgerId,
            FromUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Measure = ReportMeasure.Spending,
            GroupBy = ReportGroupBy.Category,
            TimeBucket = ReportTimeBucket.Day,
        });

        var byDay = result.Rows
            .Where(r => r.GroupId == cat.Id)
            .ToDictionary(r => r.Period!, r => r.Amount);

        Assert.Equal(30m, byDay["2026-09-10"]);   // 10 + 20, both the same UTC day
        Assert.Equal(40m, byDay["2026-09-11"]);
        Assert.Equal(2, byDay.Count);             // and no stray third day
    }

    [Fact]
    public async Task A_daily_bucket_is_refused_for_the_account_and_payee_dimensions()
    {
        // Those two never put the day in their SQL key, so honouring the request
        // would return MONTHLY buckets wearing daily labels — the failure mode
        // where every number is plausible and the axis is a lie.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var repo = new ReportingRepository(_fixture.NewDbContext());

        foreach (var dimension in new[] { ReportGroupBy.Account, ReportGroupBy.Payee })
        {
            var spec = new ReportSpec
            {
                LedgerId = ledger.LedgerId,
                Measure = ReportMeasure.Spending,
                GroupBy = dimension,
                TimeBucket = ReportTimeBucket.Day,
            };
            await Assert.ThrowsAsync<ArgumentException>(() => repo.SummarizeAsync(spec));
        }
    }

    [Fact]
    public async Task Adding_the_day_bucket_did_not_disturb_the_monthly_one()
    {
        // The day now rides in the Cell record for every dimension. If a
        // non-daily path leaked a real day into the grouping key, a month with
        // spending on two dates would split into two rows — so this is the
        // regression guard for the change that added Day, not a test of Month.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var cat = await ledger.AddCategoryAsync("Groceries", "expense");

        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 10m,
            new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 25m,
            new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc));

        var repo = new ReportingRepository(_fixture.NewDbContext());
        var result = await repo.SummarizeAsync(new ReportSpec
        {
            LedgerId = ledger.LedgerId,
            FromUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Measure = ReportMeasure.Spending,
            GroupBy = ReportGroupBy.Category,
            TimeBucket = ReportTimeBucket.Month,
        });

        var row = Assert.Single(result.Rows, r => r.GroupId == cat.Id);
        Assert.Equal("2026-09", row.Period);
        Assert.Equal(35m, row.Amount);
    }
}
