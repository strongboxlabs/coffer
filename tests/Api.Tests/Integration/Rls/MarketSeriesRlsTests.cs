using Microsoft.EntityFrameworkCore;

using Npgsql;

using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Rls;

/// <summary>
/// The four outside market-series tables (migration 280, ADR-0114 D6) attacked directly as
/// coffer_app with the API's checks out of the way: any member reads, a non-member reads
/// nothing, a viewer writes nothing, an owner or editor writes. Each refusal is paired with
/// the write the same policy permits, so it is the policy's and not a missing reference's.
/// </summary>
/// <remarks>
/// The two series tables also hold to "a stored day is never rewritten" (ADR-0114 D6) at the
/// grant: even an owner's update or delete of a stored growth day or rate is refused.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MarketSeriesRlsTests : DatabaseTest
{
    public MarketSeriesRlsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateOnly Day = new(2026, 1, 5);

    /// <summary>42501: a privilege refused, or a row RLS will not admit.</summary>
    private static async Task AssertRefusedAsync(Func<Task> write)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(write);
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.True(pg is not null, $"refused by something other than Postgres: {ex}");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, pg!.SqlState);
    }

    private sealed record Members(SyntheticLedger Ledger, Guid Viewer, Guid Editor, Guid Outsider);

    private async Task<Members> MembersAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var viewer = await ledger.AddMemberAsync("viewer");
        var editor = await ledger.AddMemberAsync("editor");
        // An owner of another ledger only.
        var outsider = (await SyntheticLedger.CreateAsync(Db)).UserId;
        return new Members(ledger, viewer, editor, outsider);
    }

    private static LedgerBenchmarkRow Benchmark(Guid ledgerId, string symbol, int position) => new()
    {
        Id = Guid.NewGuid(), LedgerId = ledgerId, Symbol = symbol, Name = "Fund " + symbol,
        CurrencyCode = "USD", Position = position,
    };

    private static BenchmarkGrowthRow Growth(Guid ledgerId, DateOnly day) => new()
    {
        LedgerId = ledgerId, Symbol = "FNDA", GrowthDate = day, Growth = 1.000123456789m,
        Source = BenchmarkGrowthSource.Fetch,
    };

    private static FxRateRow Rate(Guid ledgerId, DateOnly day) => new()
    {
        Id = Guid.NewGuid(), LedgerId = ledgerId, FromCurrency = "EUR", ToCurrency = "USD", RateDate = day,
        Rate = 1.234567890123m, Source = FxRateSource.Frankfurter, Upstream = "Source A",
    };

    private static FxPairCoverageRow Coverage(Guid ledgerId, string from) => new()
    {
        LedgerId = ledgerId, FromCurrency = from, ToCurrency = "USD", FetchedFromDate = Day.AddDays(-30),
        FetchedThroughDate = Day,
    };

    private static BenchmarkCoverageRow BenchmarkRange(Guid ledgerId, string symbol) => new()
    {
        LedgerId = ledgerId, Symbol = symbol, FetchedFromDate = Day.AddDays(-30), FetchedThroughDate = Day,
    };

    [Fact]
    public async Task Ledger_benchmarks_are_read_by_members_and_written_by_owners_and_editors()
    {
        var m = await MembersAsync();
        var lid = m.Ledger.LedgerId;
        var existing = Benchmark(lid, "FNDA", 1);
        await using (var svc = Db.NewServiceDbContext())
        {
            svc.LedgerBenchmarks.Add(existing);
            await svc.SaveChangesAsync();
        }

        await using (var db = Db.NewAppDbContextAsUser(m.Outsider))
            Assert.False(await db.LedgerBenchmarks.AnyAsync(b => b.LedgerId == lid));
        await using (var db = Db.NewAppDbContextAsUser(m.Viewer))
        {
            Assert.True(await db.LedgerBenchmarks.AnyAsync(b => b.Id == existing.Id));
            Assert.Equal(0, await db.LedgerBenchmarks.Where(b => b.Id == existing.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Position, 2)));
            Assert.Equal(0, await db.LedgerBenchmarks.Where(b => b.Id == existing.Id).ExecuteDeleteAsync());
            db.LedgerBenchmarks.Add(Benchmark(lid, "FNDB", 2));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(m.Outsider))
        {
            db.LedgerBenchmarks.Add(Benchmark(lid, "FNDB", 2));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(m.Editor))
        {
            db.LedgerBenchmarks.Add(Benchmark(lid, "FNDB", 2));
            Assert.Equal(1, await db.SaveChangesAsync());
            Assert.Equal(1, await db.LedgerBenchmarks.Where(b => b.Id == existing.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Position, 3)));
        }
        await using (var db = Db.NewAppDbContextAsUser(m.Ledger.UserId))
            Assert.Equal(1, await db.LedgerBenchmarks.Where(b => b.Id == existing.Id).ExecuteDeleteAsync());
    }

    [Fact]
    public async Task Benchmark_growth_is_read_by_members_inserted_by_owners_and_editors_and_never_rewritten()
    {
        var m = await MembersAsync();
        var lid = m.Ledger.LedgerId;
        await using (var svc = Db.NewServiceDbContext())
        {
            svc.BenchmarkGrowth.Add(Growth(lid, Day));
            await svc.SaveChangesAsync();
        }

        await using (var db = Db.NewAppDbContextAsUser(m.Outsider))
            Assert.False(await db.BenchmarkGrowth.AnyAsync(g => g.LedgerId == lid));
        await using (var db = Db.NewAppDbContextAsUser(m.Viewer))
        {
            Assert.True(await db.BenchmarkGrowth.AnyAsync(g => g.LedgerId == lid && g.GrowthDate == Day));
            db.BenchmarkGrowth.Add(Growth(lid, Day.AddDays(1)));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(m.Editor))
        {
            db.BenchmarkGrowth.Add(Growth(lid, Day.AddDays(1)));
            Assert.Equal(1, await db.SaveChangesAsync());
        }
        // Not even the owner rewrites or removes a stored day: the grant is SELECT, INSERT.
        await using (var db = Db.NewAppDbContextAsUser(m.Ledger.UserId))
        {
            await AssertRefusedAsync(() => db.BenchmarkGrowth.Where(g => g.LedgerId == lid)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.Growth, 2m)));
            await AssertRefusedAsync(() => db.BenchmarkGrowth.Where(g => g.LedgerId == lid).ExecuteDeleteAsync());
        }
        await using (var svc = Db.NewServiceDbContext())
            Assert.Equal(2, await svc.BenchmarkGrowth.CountAsync(g => g.LedgerId == lid && g.Growth == 1.000123456789m));
    }

    [Fact]
    public async Task Fx_rates_are_read_by_members_inserted_by_owners_and_editors_and_never_rewritten()
    {
        var m = await MembersAsync();
        var lid = m.Ledger.LedgerId;
        await using (var svc = Db.NewServiceDbContext())
        {
            svc.FxRates.Add(Rate(lid, Day));
            await svc.SaveChangesAsync();
        }

        await using (var db = Db.NewAppDbContextAsUser(m.Outsider))
            Assert.False(await db.FxRates.AnyAsync(r => r.LedgerId == lid));
        await using (var db = Db.NewAppDbContextAsUser(m.Viewer))
        {
            Assert.True(await db.FxRates.AnyAsync(r => r.LedgerId == lid && r.RateDate == Day));
            db.FxRates.Add(Rate(lid, Day.AddDays(1)));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(m.Editor))
        {
            // A hand-entered rate, for a day with none, naming its author (ADR-0114 D10).
            db.FxRates.Add(new FxRateRow
            {
                Id = Guid.NewGuid(), LedgerId = lid, FromCurrency = "EUR", ToCurrency = "USD",
                RateDate = Day.AddDays(1), Rate = 1.25m, Source = FxRateSource.Manual, EnteredByUserId = m.Editor,
            });
            Assert.Equal(1, await db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(m.Ledger.UserId))
        {
            await AssertRefusedAsync(() => db.FxRates.Where(r => r.LedgerId == lid)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Rate, 2m)));
            await AssertRefusedAsync(() => db.FxRates.Where(r => r.LedgerId == lid).ExecuteDeleteAsync());
        }
        await using (var svc = Db.NewServiceDbContext())
        {
            var rates = await svc.FxRates.Where(r => r.LedgerId == lid).OrderBy(r => r.RateDate)
                .Select(r => new { r.Rate, r.Source, r.EnteredByUserId }).ToListAsync();
            var expected = new List<(decimal Rate, string Source, Guid? EnteredBy)>
            {
                (1.234567890123m, FxRateSource.Frankfurter, null),
                (1.25m, FxRateSource.Manual, m.Editor),
            };
            Assert.Equal(expected, rates.Select(r => (r.Rate, r.Source, r.EnteredByUserId)).ToList());
        }
    }

    [Fact]
    public async Task Fx_pair_coverage_is_read_by_members_and_advanced_by_owners_and_editors()
    {
        var m = await MembersAsync();
        var lid = m.Ledger.LedgerId;
        await using (var svc = Db.NewServiceDbContext())
        {
            svc.FxPairCoverage.Add(Coverage(lid, "EUR"));
            await svc.SaveChangesAsync();
        }

        await using (var db = Db.NewAppDbContextAsUser(m.Outsider))
            Assert.False(await db.FxPairCoverage.AnyAsync(c => c.LedgerId == lid));
        await using (var db = Db.NewAppDbContextAsUser(m.Viewer))
        {
            Assert.True(await db.FxPairCoverage.AnyAsync(c => c.LedgerId == lid && c.FromCurrency == "EUR"));
            Assert.Equal(0, await db.FxPairCoverage.Where(c => c.LedgerId == lid)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.FetchedThroughDate, Day.AddDays(1))));
            db.FxPairCoverage.Add(Coverage(lid, "GBP"));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(m.Editor))
        {
            db.FxPairCoverage.Add(Coverage(lid, "GBP"));
            Assert.Equal(1, await db.SaveChangesAsync());
            Assert.Equal(1, await db.FxPairCoverage.Where(c => c.LedgerId == lid && c.FromCurrency == "EUR")
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.FetchedThroughDate, Day.AddDays(2))));
            // Coverage is advanced, never removed, by a request.
            await AssertRefusedAsync(() => db.FxPairCoverage.Where(c => c.LedgerId == lid).ExecuteDeleteAsync());
        }
        await using (var svc = Db.NewServiceDbContext())
        {
            Assert.Equal(Day.AddDays(2), await svc.FxPairCoverage
                .Where(c => c.LedgerId == lid && c.FromCurrency == "EUR").Select(c => c.FetchedThroughDate).SingleAsync());
            Assert.Equal(2, await svc.FxPairCoverage.CountAsync(c => c.LedgerId == lid));
        }
    }

    [Fact]
    public async Task Benchmark_coverage_is_read_by_members_and_widened_by_owners_and_editors()
    {
        var m = await MembersAsync();
        var lid = m.Ledger.LedgerId;
        await using (var svc = Db.NewServiceDbContext())
        {
            svc.BenchmarkCoverage.Add(BenchmarkRange(lid, "FNDA"));
            await svc.SaveChangesAsync();
        }

        await using (var db = Db.NewAppDbContextAsUser(m.Outsider))
            Assert.False(await db.BenchmarkCoverage.AnyAsync(c => c.LedgerId == lid));
        await using (var db = Db.NewAppDbContextAsUser(m.Viewer))
        {
            Assert.True(await db.BenchmarkCoverage.AnyAsync(c => c.LedgerId == lid && c.Symbol == "FNDA"));
            Assert.Equal(0, await db.BenchmarkCoverage.Where(c => c.LedgerId == lid)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.FetchedFromDate, Day.AddDays(-60))));
            db.BenchmarkCoverage.Add(BenchmarkRange(lid, "FNDB"));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(m.Editor))
        {
            db.BenchmarkCoverage.Add(BenchmarkRange(lid, "FNDB"));
            Assert.Equal(1, await db.SaveChangesAsync());
            Assert.Equal(1, await db.BenchmarkCoverage.Where(c => c.LedgerId == lid && c.Symbol == "FNDA")
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.FetchedFromDate, Day.AddDays(-60))));
            // A range only widens; a request never removes one, so a removed benchmark keeps it.
            await AssertRefusedAsync(() => db.BenchmarkCoverage.Where(c => c.LedgerId == lid).ExecuteDeleteAsync());
        }
        await using (var svc = Db.NewServiceDbContext())
        {
            Assert.Equal(Day.AddDays(-60), await svc.BenchmarkCoverage
                .Where(c => c.LedgerId == lid && c.Symbol == "FNDA").Select(c => c.FetchedFromDate).SingleAsync());
            // A range that runs backwards is refused by the CHECK.
            svc.BenchmarkCoverage.Add(new BenchmarkCoverageRow
            {
                LedgerId = lid, Symbol = "FNDC", FetchedFromDate = Day, FetchedThroughDate = Day.AddDays(-1),
            });
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => svc.SaveChangesAsync());
            var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
            Assert.Equal(("ck_benchmark_coverage_range", PostgresErrorCodes.CheckViolation),
                (pg?.ConstraintName, pg?.SqlState));
        }
    }
}
