using System.Globalization;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>A simulated Demo: the verified book and the market its trades priced at.</summary>
public sealed record DemoSimulation(DemoBook Book, DemoMarket Market);

/// <summary>
/// Generates the Demo: five years of a made-up two-earner household, ending on the
/// <see cref="DemoSpec.AsOf"/> it is given, as a Moneydance export, plus the card statement CSV and the
/// manifest the seeding step reads (ADR-0115 D1).
/// </summary>
/// <remarks>
/// <para><b>Pipeline.</b> Build the calendar, the streams, the ids and the market
/// from the <see cref="DemoSpec"/>; add the household's accounts and the security holdings to the
/// book; let each pattern post its rows; run the book's self-checks; write the
/// export; apply a short span.</para>
///
/// <para><b>Item order.</b> Metadata; the base rows; the real accounts, then the
/// holding accounts; the securities; the splits; the prices; the transactions in
/// export order; the reminders; <c>local_settings</c>.</para>
///
/// <para><b>Metadata</b> is the generator's own: exporter
/// <see cref="Exporter"/>, Moneydance build <see cref="MoneydanceBuild"/>, export
/// date AsOf, file name <see cref="FileName"/>.</para>
///
/// <para><b>Determinism.</b> No clock, no runtime generator, no binary fractions;
/// <c>long</c> cents and <c>decimal</c> only, invariant culture throughout. The
/// same <see cref="DemoSpec"/> gives the same bytes on every machine.</para>
/// </remarks>
public static class DemoExportGenerator
{
    /// <summary>The metadata's <c>exporter</c>.</summary>
    public const string Exporter = "Coffer demo generator";

    /// <summary>The metadata's <c>moneydance_build</c>: none, honestly.</summary>
    public const int MoneydanceBuild = 0;

    /// <summary>The metadata's <c>file_name</c>.</summary>
    public const string FileName = "Demo";

    /// <summary>
    /// The household's patterns, in the order they post. A pattern that moves cash
    /// into or out of an investment account posts before
    /// <see cref="InvestmentsPattern"/>, which invests what arrives. The household's
    /// spending follows; then the card payments, which pay what was spent on the
    /// cards; the cash wallet's withdrawals; and last the balance simulator
    /// (<see cref="SavingsPattern"/>), which sweeps and tops up Joint Checking once
    /// every other flow on it is known.
    /// </summary>
    /// <remarks>
    /// Provenance (<see cref="DemoProvenance.Apply"/>) and status
    /// (<see cref="DemoStatusRule.Apply"/>) are not patterns: they run over every
    /// posted row once all patterns have posted, so a pattern added here gets both
    /// without asking.
    /// </remarks>
    internal static IReadOnlyList<IDemoPattern> Patterns(DemoMarket market) =>
    [
        new PayrollPattern(market),
        new TaxSettlementPattern(),
        new HsaMedicalPattern(market),
        new IraContributionPattern(),
        new BrokerageContributionPattern(),
        new InvestmentsPattern(market),
        new HousingPattern(),
        new CarLoanPattern(),
        new BillsPattern(),
        new SpendingPattern(),
        new MedicalPattern(),
        new RegisterExamplesPattern(),
        new CardsPattern(),
        new CashPattern(),
        new SavingsPattern(),
    ];

    /// <summary>Generate the Demo for <paramref name="spec"/>.</summary>
    /// <exception cref="DemoSelfCheckException">The simulated ledger fails a self-check.</exception>
    public static DemoBuild Generate(DemoSpec spec) => Generate(spec, Patterns);

    /// <summary>Generate from a given pattern list, after <paramref name="prepare"/>
    /// has adjusted the fresh book (tests).</summary>
    internal static DemoBuild Generate(
        DemoSpec spec, Func<DemoMarket, IReadOnlyList<IDemoPattern>> patterns, Action<DemoBook>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var (book, market) = Run(spec, patterns, prepare);
        var keepsFrom = spec.Span.KeepsFrom(book.Calendar);

        // A pattern list without the household (a test's) has no card statement and
        // no marked rows; it gets the parts of the manifest that need neither.
        var household = book.Marked(DemoEvents.Closing).Count > 0;
        var manifest = household
            ? DemoManifestBuilder.Build(book, market, new DemoRngFactory(spec.Seed))
            : new DemoManifest
            {
                AccountIds = book.Accounts
                    .Where(a => !a.IsHolding)
                    .ToDictionary(a => a.Role, a => a.Id, StringComparer.Ordinal),
                Folders = HouseholdAccounts.Folders(book),
                TargetDateLookThrough = DemoSecurities.TargetDateWeights,
                StableValueMonthEnds = market.StableValueMonthEnds,
                CheckingReconcile = DemoStatusRule.CheckingReconcile(book),
            };
        var csv = household ? CardStatementCsv.Build(DemoCards.StatementRows(book).Select(r => r.Row)) : CardStatementCsv.Build([]);
        IReadOnlyList<DemoReminder> reminders = household ? DemoReminders.For(book) : [];

        var json = Write(book, market, keepsFrom, reminders);
        return new DemoBuild(json, csv, manifest);
    }

    /// <summary>Run every pattern over a fresh book and verify it. The export is
    /// written from this book.</summary>
    public static DemoBook Simulate(DemoSpec spec) => Run(spec, Patterns).Book;

    /// <summary>Run the given patterns over a fresh book, after
    /// <paramref name="prepare"/> has adjusted it, and verify it.</summary>
    internal static DemoSimulation Run(
        DemoSpec spec, Func<DemoMarket, IReadOnlyList<IDemoPattern>> patterns, Action<DemoBook>? prepare = null)
    {
        var simulation = RunUnverified(spec, patterns, prepare);
        simulation.Book.Verify();
        return simulation;
    }

    /// <summary><see cref="Run"/> without the final self-checks, so a test can list
    /// every problem (<see cref="DemoBook.FindProblems"/>) instead of the first throw.</summary>
    internal static DemoSimulation RunUnverified(
        DemoSpec spec, Func<DemoMarket, IReadOnlyList<IDemoPattern>> patterns, Action<DemoBook>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(patterns);
        var calendar = new DemoCalendar(spec.AsOf);
        var rng = new DemoRngFactory(spec.Seed);
        var ids = new DemoIds(spec.Seed);
        var book = new DemoBook(calendar, ids, DemoBase.Instance);
        var market = new DemoMarket(calendar, rng, ids);

        HouseholdAccounts.AddTo(book);
        DemoProvenance.ConfigureAccounts(book);
        DemoHoldings.AddTo(book, market);
        prepare?.Invoke(book);

        if (market.SplitDateInWindow is { } splitDate)
        {
            book.ApplySecuritySplit(
                market.CurrencyId(DemoSecurities.Nvda), splitDate, DemoSecurities.SplitNewShares, DemoSecurities.SplitOldShares);
        }

        foreach (var pattern in patterns(market)) pattern.Emit(calendar, rng, book);

        // Over every posted row, after every pattern: provenance first, because the
        // status of a row in an open statement depends on whether it came by a feed.
        DemoProvenance.Apply(book);
        DemoStatusRule.Apply(book);
        return new DemoSimulation(book, market);
    }

    private static byte[] Write(DemoBook book, DemoMarket market, DateOnly? keepsFrom, IReadOnlyList<DemoReminder> reminders)
    {
        var calendar = book.Calendar;
        var baseData = book.Base;

        // A short span drops bank-route rows before its first day, and raises each
        // account's opening balance by what they moved, so every end balance equals
        // the full document's. Investment-route rows stay in full.
        var rows = new List<DemoTxn>();
        var raised = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var txn in book.TransactionsInExportOrder())
        {
            if (!txn.Exported) continue;
            if (keepsFrom is { } first && txn.Route == DemoRoute.Bank && txn.Date < first)
            {
                foreach (var (accountId, amount) in book.CashEffects(txn))
                    raised[accountId] = raised.GetValueOrDefault(accountId) + amount;
                continue;
            }
            rows.Add(txn);
        }

        using var writer = new MdJsonWriter();
        writer.WriteMetadata(Exporter, MoneydanceBuild, DemoCalendar.MdDate(calendar.AsOf), FileName);
        foreach (var item in baseData.Items) writer.WriteBaseItem(item);

        foreach (var service in DemoProvenance.Services(book))
        {
            writer.WriteItem(MdSection.OnlineServices, "olsvc", service.Id,
            [
                new("fi_name", service.FiName),
                new("fi_org", service.FiOrg),
                new("fi_id", service.FiId),
                new("ts", Invariant(DemoCalendar.NoonEpochMillis(calendar.WindowStart))),
            ]);
        }

        foreach (var account in book.Accounts.Where(a => !a.IsHolding))
        {
            writer.WriteAccount(account, baseData, account.OpeningBalance + raised.GetValueOrDefault(account.Id));
        }
        foreach (var account in book.Accounts.Where(a => a.IsHolding))
        {
            writer.WriteAccount(account, baseData, account.OpeningBalance);
        }

        WriteSecurities(writer, book, market);

        foreach (var txn in rows) writer.WriteTxn(txn, calendar);
        foreach (var reminder in reminders) writer.WriteItem(MdSection.Reminders, "reminder", reminder.Id, reminder.Fields);

        writer.WriteLocalSettings(baseData.LocalSettings);
        return writer.ToArray();
    }

    // The security curr rows (ticker, decimals, name and visibility only: the type
    // codes live on the holding accounts), the split, and one csnap per security per
    // business day at the inverse of the price, as Moneydance stores it.
    private static void WriteSecurities(MdJsonWriter writer, DemoBook book, DemoMarket market)
    {
        var created = Invariant(DemoCalendar.NoonEpochMillis(book.Calendar.WindowStart));
        foreach (var security in market.Securities)
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                new("type", "s"),
                new("currid", security.Symbol),
                new("name", security.Name),
            };
            if (security.Ticker is { } ticker) fields.Add(new("ticker", ticker));
            fields.Add(new("dec", Invariant(security.Decimals)));
            fields.Add(new("hide_in_ui", "0"));
            fields.Add(new("ts", created));
            writer.WriteItem(MdSection.Securities, "curr", market.CurrencyId(security.Key), fields);
        }

        foreach (var split in book.SecuritySplits)
        {
            var ratio = (decimal)split.NewShares / split.OldShares;
            writer.WriteItem(MdSection.SecuritySplits, "csplit",
                book.Ids.Id("csplit", split.SecurityCurrencyId, DemoCalendar.MdDate(split.Date)),
            [
                new("curr", split.SecurityCurrencyId),
                new("dt", Invariant(DemoCalendar.MdDate(split.Date))),
                new("ratio", ratio.ToString("0.0###", CultureInfo.InvariantCulture)),
                new("oldshrs", Invariant(split.OldShares)),
                new("newshrs", Invariant(split.NewShares)),
                new("ts", Invariant(DemoCalendar.NoonEpochMillis(split.Date))),
            ]);
        }

        foreach (var security in market.Securities)
        {
            var currencyId = market.CurrencyId(security.Key);
            foreach (var (date, price) in market.PriceRows(security.Key))
            {
                var rate = DemoMoney.Invariant(1m / price);
                writer.WriteItem(MdSection.Prices, "csnap", book.Ids.Id("csnap", security.Key, DemoCalendar.MdDate(date)),
                [
                    new("curr", currencyId),
                    new("dt", Invariant(DemoCalendar.MdDate(date))),
                    new("urt", rate),
                    new("relrt", rate),
                    new("hi", "0.0"),
                    new("lo", "0.0"),
                    new("vol", "0"),
                    new("ts", Invariant(DemoCalendar.NoonEpochMillis(date))),
                ]);
            }
        }
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
