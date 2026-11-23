using System.Globalization;
using Coffer.Api.Provisioning.Demo;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Tests.Demo;

/// <summary>
/// A short span (<see cref="DemoSpan.LastMonths"/>) is the full Demo with its early bank-route rows dropped:
/// every account ends where the full export ends, and the investment route, the
/// accounts, securities, prices, splits and reminders are all still there.
/// </summary>
/// <remarks>
/// Compared on the written exports, read through the importer's typed readers: the
/// short span's raised opening balances are only right if they land on what the
/// importer reads as each account's opening balance.
/// </remarks>
public sealed class DemoSpanTests
{
    public static TheoryData<int> Spans() => new() { 3, 12 };

    [Theory]
    [MemberData(nameof(Spans))]
    public void Every_account_ends_where_the_full_export_ends(int months)
    {
        using var full = DemoTestExports.FullExport();
        using var span = Short(months).ToExport();

        var fullEnds = EndBalances(full);
        var spanEnds = EndBalances(span);
        Assert.Equal(fullEnds.Keys.Order(StringComparer.Ordinal), spanEnds.Keys.Order(StringComparer.Ordinal));
        Assert.True(fullEnds.Count >= 17, $"only {fullEnds.Count} accounts compared");
        foreach (var (id, balance) in fullEnds)
            Assert.True(balance == spanEnds[id], $"{id} ends at {spanEnds[id]} in the span, {balance} in full");

        // The raise is not vacuous: some account opens higher or lower than in full.
        var fullOpenings = Openings(full);
        var spanOpenings = Openings(span);
        Assert.Contains(fullOpenings, o => spanOpenings[o.Key] != o.Value);
        // The dormant account, touched by nothing, opens where it always did.
        var cd = DemoTestExports.Full.Value.Manifest.AccountIds[DemoAccountRoles.CertificateOfDeposit];
        Assert.Equal(fullOpenings[cd], spanOpenings[cd]);
    }

    [Theory]
    [MemberData(nameof(Spans))]
    public void The_span_drops_only_early_bank_rows_and_keeps_everything_else(int months)
    {
        using var full = DemoTestExports.FullExport();
        using var span = Short(months).ToExport();
        var keepsFrom = new DemoCalendar(DemoTestExports.AsOf).MonthStart(DemoCalendar.CurrentLifeMonth - months);

        var fullTxns = full.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From).ToList();
        var spanIds = span.AllItems.Where(i => i.ObjType == "txn").Select(i => i.Id).ToList();
        Assert.Equal(spanIds.Count, spanIds.Distinct(StringComparer.Ordinal).Count());

        var expected = fullTxns
            .Where(t => t.IsInvestmentShape || ToDate(t.Date) >= keepsFrom)
            .Select(t => t.Id)
            .ToList();
        Assert.Equal(expected, spanIds);

        // Both halves are real: early bank rows went, early investment rows stayed.
        Assert.Contains(fullTxns, t => !t.IsInvestmentShape && ToDate(t.Date) < keepsFrom);
        Assert.Contains(fullTxns, t => t.IsInvestmentShape && ToDate(t.Date) < keepsFrom);
        Assert.DoesNotContain(span.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From),
            t => !t.IsInvestmentShape && ToDate(t.Date) < keepsFrom);

        // Every kept row is written exactly as in full.
        var fullRows = full.AllItems.Where(i => i.ObjType == "txn").ToDictionary(i => i.Id, Canon, StringComparer.Ordinal);
        Assert.All(span.AllItems.Where(i => i.ObjType == "txn"), i => Assert.Equal(fullRows[i.Id], Canon(i)));

        // Everything that is not a transaction is kept in full, the accounts' opening
        // balances aside.
        foreach (var objType in new[] { "curr", "csnap", "csplit", "reminder", "olsvc", "misc", "secsubtypes" })
        {
            var expectedItems = full.AllItems.Where(i => i.ObjType == objType).Select(Canon).ToList();
            Assert.True(expectedItems.Count > 0, $"the full export has no {objType}");
            Assert.Equal(expectedItems, span.AllItems.Where(i => i.ObjType == objType).Select(Canon));
        }
        Assert.Equal(
            full.AllItems.Where(i => i.ObjType == "acct").Select(i => i.Id),
            span.AllItems.Where(i => i.ObjType == "acct").Select(i => i.Id));
        Assert.Contains(span.AllItems, i => i.ObjType == "csplit");
        Assert.Equal(2, span.AllItems.Count(i => i.ObjType == "reminder"));
    }

    /// <summary>
    /// From the day before the cut on, every real account ends each day exactly
    /// where the full export does. Before the cut a short span is not the
    /// household's history: only the kept investment-route legs move a bank account
    /// there, while its raised opening balance already nets out every dropped bank
    /// row. So a bank account that an investment row pays into before the cut (the
    /// down payment's cash moves, a transferred dividend) opens LOW, possibly below
    /// zero, and climbs as those legs land. That stretch
    /// is by design: a short span keeps every investment row, so holdings and lots stay complete; a
    /// balance-floor or net-worth assertion over the whole window belongs on the
    /// full export.
    /// </summary>
    [Theory]
    [MemberData(nameof(Spans))]
    public void Day_ends_equal_the_full_exports_from_the_cut_on(int months)
    {
        using var full = DemoTestExports.FullExport();
        using var span = Short(months).ToExport();
        var calendar = new DemoCalendar(DemoTestExports.AsOf);
        var keepsFrom = calendar.MonthStart(DemoCalendar.CurrentLifeMonth - months);

        var fullDays = DayEnds(full, calendar);
        var spanDays = DayEnds(span, calendar);
        var compared = 0;
        var differsBefore = false;
        foreach (var (id, fullEnds) in fullDays)
        {
            var spanEnds = spanDays[id];
            for (var i = 0; i < fullEnds.Length; i++)
            {
                var day = calendar.WindowStart.AddDays(i);
                if (day >= keepsFrom.AddDays(-1))
                {
                    Assert.True(fullEnds[i] == spanEnds[i],
                        $"{id} ends {day:yyyy-MM-dd} at {spanEnds[i]} in the span, {fullEnds[i]} in full");
                    compared++;
                }
                else if (fullEnds[i] != spanEnds[i])
                {
                    differsBefore = true;
                }
            }
        }
        Assert.True(compared > 17 * 28 * months, $"only {compared} day-ends compared");
        // The pre-cut stretch is real, so the comparison above starts where it must.
        Assert.True(differsBefore, "no day-end before the cut differs from full; the span dropped nothing");
    }

    [Fact]
    public void The_manifest_and_the_card_statement_do_not_change_with_the_span()
    {
        var full = DemoTestExports.Full.Value;
        foreach (var span in new[] { DemoTestExports.Short.Value, DemoTestExports.Tiny.Value })
        {
            Assert.Equal(full.CardStatementCsv, span.CardStatementCsv);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(full.Manifest),
                System.Text.Json.JsonSerializer.Serialize(span.Manifest));
        }
    }

    [Fact]
    public void A_span_of_the_whole_window_is_the_full_export()
    {
        var whole = DemoExportGenerator.Generate(DemoTestExports.Spec(DemoSpan.LastMonths(DemoSpan.MaxMonths)));
        Assert.Equal(DemoTestExports.Full.Value.Json, whole.Json);
    }

    private static DemoBuild Short(int months) => months switch
    {
        3 => DemoTestExports.Tiny.Value,
        12 => DemoTestExports.Short.Value,
        _ => DemoExportGenerator.Generate(DemoTestExports.Spec(DemoSpan.LastMonths(months))),
    };

    /// <summary>Each real account's opening balance as the importer reads it.</summary>
    private static Dictionary<string, long> Openings(MdExport export) =>
        export.AllItems
            .Where(i => i.ObjType == "acct" && i.GetString("type") is not ("r" or "e" or "i" or "s"))
            .ToDictionary(i => i.Id, i => long.Parse(i.GetString("sbal") ?? "0", CultureInfo.InvariantCulture),
                StringComparer.Ordinal);

    /// <summary>Each real account's end balance: its opening balance plus every
    /// posting on it, the parent side of its own rows and the split side of every
    /// row that splits to it (a security split moves shares, not cash).</summary>
    private static Dictionary<string, long> EndBalances(MdExport export)
    {
        var balances = Openings(export);
        foreach (var txn in export.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From))
        {
            if (balances.ContainsKey(txn.AcctId)) balances[txn.AcctId] += txn.Splits.Sum(s => s.ParentAmount);
            foreach (var split in txn.Splits)
            {
                if (split.InvestSplitType != "sec" && balances.ContainsKey(split.AcctId))
                    balances[split.AcctId] += split.SplitAmount;
            }
        }
        return balances;
    }

    /// <summary>Each real account's balance at the end of every day of the window,
    /// summed as <see cref="EndBalances"/> sums them.</summary>
    private static Dictionary<string, long[]> DayEnds(MdExport export, DemoCalendar calendar)
    {
        var openings = Openings(export);
        var days = calendar.AsOf.DayNumber - calendar.WindowStart.DayNumber + 1;
        var deltas = openings.Keys.ToDictionary(id => id, _ => new long[days], StringComparer.Ordinal);
        foreach (var txn in export.AllItems.Where(i => i.ObjType == "txn").Select(MdTxn.From))
        {
            var index = ToDate(txn.Date).DayNumber - calendar.WindowStart.DayNumber;
            if (deltas.TryGetValue(txn.AcctId, out var own)) own[index] += txn.Splits.Sum(s => s.ParentAmount);
            foreach (var split in txn.Splits)
            {
                if (split.InvestSplitType != "sec" && deltas.TryGetValue(split.AcctId, out var other))
                    other[index] += split.SplitAmount;
            }
        }
        foreach (var (id, delta) in deltas)
        {
            var running = openings[id];
            for (var i = 0; i < delta.Length; i++)
            {
                running += delta[i];
                delta[i] = running;
            }
        }
        return deltas;
    }

    private static DateOnly ToDate(int mdDate) => new(mdDate / 10000, mdDate / 100 % 100, mdDate % 100);

    /// <summary>An item's id and every field as written, in order.</summary>
    private static string Canon(MdItem item) =>
        item.Id + "|" + string.Join(";", item.Fields.Select(f => f.Key + "=" + f.Value.GetRawText()));
}
