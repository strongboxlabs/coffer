namespace Coffer.Api.Contracts;

/// <summary>One projection row that disagrees with what it is derived from: the
/// transactions, or, for holdings_currency, the holdings account's brokerage, or, for
/// reminder_auto_post, the ledger's reminders.</summary>
/// <param name="Scope">Human-readable location, for display.</param>
/// <param name="Field">Which figure disagrees.</param>
/// <param name="Stored">What the projection holds.</param>
/// <param name="Expected">What a fresh derivation produces.</param>
/// <param name="AccountId">Set where the projection is per-account.</param>
/// <param name="SecurityId">Set where it is per-(account, security).</param>
/// <param name="HeaderId">Set where it is per-header.</param>
/// <param name="PriceDate">Set where it is per-(security, day).</param>
/// <param name="Reason">What KIND of finding this is, where a projection has
/// more than one. Null where it has only one. Carried as data rather than left
/// for a reader to infer from <paramref name="Field"/>: the UI has to present
/// an unrepairable finding differently from a repairable one, and sniffing
/// prose for that is a bug waiting on a reworded label.</param>
/// <param name="Hidden">Whether the transaction <paramref name="HeaderId"/> names is hidden
/// (ADR-0072 D1), so its register shows it only in the Hidden view, which is where its link
/// opens. Set by the Category amounts check alone; false on every other projection's
/// rows.</param>
/// <remarks>
/// The ids are carried separately from <paramref name="Scope"/> so a repair can
/// target exactly what was reported. With only the display string, a repair has
/// to parse a Guid back out of <c>"header {guid}"</c> — which works right up until
/// someone rewords the label.
/// <paramref name="PriceDate"/> is here for the same reason: the trade-price
/// repair needs the day, and the day was otherwise only legible inside
/// <paramref name="Scope"/>.
/// </remarks>
public sealed record ConsistencyMismatch(
    string Scope,
    string Field,
    decimal Stored,
    decimal Expected,
    Guid? AccountId = null,
    Guid? SecurityId = null,
    Guid? HeaderId = null,
    DateOnly? PriceDate = null,
    string? Reason = null,
    bool Hidden = false)
{
    public decimal Diff => Expected - Stored;
}

/// <summary>
/// The kinds of finding a projection with a stored row per figure reports, as
/// <see cref="ConsistencyMismatch.Reason"/>: the balances, the holdings and the trade
/// prices name the first two alike, and the balances alone the third.
/// </summary>
/// <remarks>
/// String constants rather than an enum, for the reason <see cref="ConsistencyProjections"/>
/// gives: they are JSON values. The trade prices' words come from
/// <c>fn_trade_price_check</c> (migration 240), which returns the first two, so they
/// cannot be renamed here alone; the balance check's own DTO,
/// <see cref="BalanceHealthDriftDto"/>, carries all three.
/// </remarks>
public static class ConsistencyReasons
{
    /// <summary>A quote_currency repair's <see cref="ProjectionRepairResult.NotRepaired"/>
    /// reason for a security it could not ask Yahoo about: its symbol is not public, or it has
    /// none (ADR-0054 D2).</summary>
    public const string QuoteSymbolNotPublic = "quote-symbol-not-public";

    /// <summary>A stored row holds a figure other than the one the writer's rule derives.</summary>
    public const string ValueDiffers = "value";

    /// <summary>The rule derives a figure and no row is stored for it.</summary>
    public const string NotStored = "missing";

    /// <summary>A row is stored where the rule derives none: the repair removes it. The
    /// balances report it, for a running balance the walk does not write (on a
    /// template, a hidden or merged-away header, or one with no line on the
    /// account).</summary>
    public const string NotDerived = "extra";

    /// <summary>The quote_currency projection's first kind: a security with fetched prices
    /// whose currency no Yahoo read has confirmed (ADR-0114 D5, amendment). Its second kind,
    /// a fetched price in another currency than the confirmed one, is
    /// <see cref="ValueDiffers"/>.</summary>
    public const string CurrencyUnconfirmed = "unconfirmed";
}

/// <summary>The projections a consistency check knows how to examine and repair.</summary>
/// <remarks>
/// Every projection the report names has a repair, so a reader is never told about
/// a problem the product cannot fix. String constants rather than an enum: they are
/// route segments and JSON values, and a rename would be a breaking API change
/// worth seeing in a diff.
/// </remarks>
public static class ConsistencyProjections
{
    public const string Balances = "balances";
    public const string Holdings = "holdings";
    public const string RealizedGains = "realized_gains";
    public const string PostingCounts = "posting_counts";

    /// <summary>
    /// Trade-derived <c>security_prices</c> (migration 232). Maintained by
    /// <c>TradePriceFromLegInterceptor</c> like the other four — what was
    /// singular about it is that it was the one derived figure this report did
    /// not examine, while feeding valuation, allocation and returns.
    /// </summary>
    public const string TradePrices = "trade_prices";

    /// <summary>
    /// The stored memo-and-tags presentation (migration 264): each leg's
    /// <c>notes_*</c> columns and its tag-role rows, against what the rule computes
    /// from the header's rows now. Every writer recomputes in its own transaction, so a
    /// difference means a write that bypassed them (dynamic SQL, a hand-run statement).
    /// One mismatch per header, naming no memo or tag.
    /// </summary>
    public const string StoredNotes = "stored_notes";

    /// <summary>
    /// The currency of each brokerage's holdings account (ADR-0019 Rule 4, 2026-11-04
    /// amendment): a system account nobody edits, which keeps its brokerage's currency,
    /// against that brokerage's. A brokerage's currency edit writes it in the same
    /// transaction, so a difference is a write that went past the edit, or one an earlier
    /// version left. One mismatch per holdings account; its figures carry nothing, the
    /// field names the two currencies.
    /// </summary>
    public const string HoldingsCurrency = "holdings_currency";

    /// <summary>
    /// The currency of each security's fetched prices (<c>security_prices.source = 'fetch'</c>),
    /// against what Yahoo names (ADR-0114 D5, amendment, migration 300). Every fetch before the
    /// currency fix was stamped USD, so a security with fetched prices and no
    /// <c>quote_currency_confirmed_at</c> is reported (<see cref="ConsistencyReasons.CurrencyUnconfirmed"/>),
    /// and so is a confirmed security with fetched prices in more than one currency, against
    /// the currency of its latest fetched price (<see cref="ConsistencyReasons.ValueDiffers"/>).
    /// One mismatch per security; its figures carry nothing, the field says what differs.
    /// The repair asks Yahoo, so it needs the repairing user's Yahoo opt-in.
    /// </summary>
    public const string QuoteCurrency = "quote_currency";

    /// <summary>
    /// The category lines a Moneydance import wrote with their own figure where the account
    /// that paid them is in another currency, so the line does not carry the paying
    /// account's amount negated, as every category line now does (ADR-0114 D13). Shown as
    /// "Category amounts". A finding counted in the verdict, as every repairable class is.
    /// The clean shape is defined by structure alone: a posting of exactly one real line
    /// and one category line. Every such posting whose category line is not the real
    /// line's amount negated is out of step and counted, whatever the signs or zeros: a
    /// zero category line beside a nonzero real line, a nonzero category line beside a
    /// zero real line, and two lines of the same sign are all repaired. The repair sets the
    /// category line to the real line's amount negated, which moves no account's balance.
    /// A category line that is not the other line negated, on a posting of any other
    /// shape, is listed in <see cref="LedgerConsistencyReport.UnfitAmounts"/> with its
    /// reason instead, and nothing rewrites it; the reasons cover every such shape
    /// (<see cref="UnfitAmountReasons"/>: both lines on categories, or more than one real
    /// or category line). A posting of a transaction merged into another is the exception:
    /// no screen shows or opens it, so it is not listed until deleting its winner un-merges
    /// it (decided by the owner, 2026-11-17; ADR-0114 D15). <c>Checked</c> counts the
    /// postings of one real line and one category line.
    /// </summary>
    public const string CategoryAmounts = "category_amounts";

    /// <summary>
    /// The ledger's <c>reminder-auto-post</c> job (ADR-0097, 2026-11-18 amendment), against
    /// its reminders: a ledger with a reminder set to auto-post needs the job, which a
    /// reminder saved with Auto-post, and a Moneydance import that brings one, create. No
    /// job row while a reminder asks means nothing posts it: a reminder imported, or saved,
    /// by an earlier version that did not turn the job on. A row that is there and switched
    /// off is the user's choice, or the scheduler's after repeated failures, and is never a
    /// finding. One ledger examined; one mismatch, naming how many reminders ask, with
    /// figures that carry nothing.
    /// </summary>
    public const string ReminderAutoPost = "reminder_auto_post";

    public static readonly IReadOnlyList<string> All =
        [Balances, Holdings, RealizedGains, PostingCounts, TradePrices, StoredNotes, HoldingsCurrency, QuoteCurrency,
         CategoryAmounts, ReminderAutoPost];

    public static bool IsKnown(string projection) => All.Contains(projection);
}

/// <summary>The state of one derived projection.</summary>
public sealed record ProjectionConsistency(
    string Projection,
    bool Healthy,
    int Checked,
    int MismatchedCount,
    IReadOnlyList<ConsistencyMismatch> Mismatches);

/// <summary>
/// Whether every derived projection still agrees with what it is derived from: the
/// transactions, or, for holdings_currency, each holdings account's brokerage, or, for
/// reminder_auto_post, the ledger's reminders.
/// </summary>
/// <remarks>
/// Four interceptors keep denormalised state in step on every EF save. A write
/// that bypasses the ChangeTracker — raw SQL, Dapper, <c>ExecuteUpdateAsync</c>,
/// a hand-run scrub — skips all of them, and the projections drift silently.
/// For example, a one-off scrub that reshapes in-kind transfers can correctly
/// recompute the FIFO side and never touch balances, and the register then shows
/// wrong figures for as long as nothing asks whether the projections still agree.
/// <para>
/// This asks. It changes no figure, so it is safe to run on a schedule or on a
/// whim, and repairing is a separate deliberate act. Whoever runs it records its
/// verdict (<c>ConsistencyVerdicts</c>), which is derived data, not a repair.
/// </para>
/// <para>
/// <b>Trade-derived <c>security_prices</c> is covered</b> (migration 232). A naive
/// row-presence check would report drift that is not there: the per-day
/// source-priority rule makes a MISSING trade price legitimate whenever a
/// <c>fetch</c> close or a <c>manual</c> gap-fill already owns that day. The
/// check asks what the rule IMPLIES instead — last trade of the day by
/// <c>h.seq</c>, comparable only against import/simplefin/trade rows — and it
/// lives in SQL beside the writer it checks, so the two cannot drift apart.
/// </para>
/// </remarks>
public sealed record LedgerConsistencyReport(
    bool Healthy,
    IReadOnlyList<ProjectionConsistency> Projections,
    IReadOnlyList<UnbackedPriceAdvisory> UnbackedPrices,
    IReadOnlyList<OversoldPosition> Oversold)
{
    /// <summary>
    /// ADVISORY (ADR-0114 D15, shown as "Amounts with more decimals than their currency"):
    /// stored figures with more places than their currency now allows, kept as they are,
    /// and category lines the Category amounts repair cannot rewrite. Not counted in
    /// <see cref="Healthy"/>, no repair: an edit that makes a figure fit takes it off the
    /// list. One row per place the figure lives, never naming the figure. A transaction
    /// merged into another is not listed, as no screen shows or opens it; it is listed once
    /// deleting its winner un-merges it.
    /// </summary>
    public IReadOnlyList<UnfitAmountAdvisory> UnfitAmounts { get; init; } = [];

    /// <summary>
    /// ADVISORY (ADR-0114 D15, shown as "Currencies Coffer no longer accepts"): stored
    /// account and reporting currencies that a create or a change would now refuse. They
    /// keep working at 2 places. Not counted in <see cref="Healthy"/>, no repair: choosing
    /// an accepted currency takes one off the list.
    /// </summary>
    public IReadOnlyList<RefusedCurrencyAdvisory> RefusedCurrencies { get; init; } = [];
}

/// <summary>
/// Where an <see cref="UnfitAmountAdvisory"/>'s figure lives, as
/// <see cref="UnfitAmountAdvisory.Kind"/>. Each names the place a link opens.
/// </summary>
public static class UnfitAmountKinds
{
    /// <summary>A line of a transaction: the account's register at the transaction.</summary>
    public const string Transaction = "transaction";

    /// <summary>The account's opening balance: the account's editor.</summary>
    public const string OpeningBalance = "opening_balance";

    /// <summary>A loan's money terms (principal, escrow, fixed payment): the loan's editor.</summary>
    public const string LoanTerms = "loan_terms";

    /// <summary>A category's budget target for a month: the budget at that month.</summary>
    public const string BudgetTarget = "budget_target";

    /// <summary>A line of a reminder's template: the reminder.</summary>
    public const string Reminder = "reminder";
}

/// <summary>
/// Why a category line the Category amounts repair cannot rewrite is listed, as
/// <see cref="UnfitAmountAdvisory.Reason"/> (ADR-0114 D13). Null on a row listed for its
/// places. A reason names a shape, never signs or zeros: every posting of one real line
/// and one category line is repaired (<see cref="ConsistencyProjections.CategoryAmounts"/>),
/// so these two are the only postings it leaves.
/// </summary>
public static class UnfitAmountReasons
{
    /// <summary>Both lines of the posting are on categories.</summary>
    public const string CategoryLineBetweenCategories = "category-line-between-categories";

    /// <summary>The posting has more than one real line or more than one category line.</summary>
    public const string CategoryLineManyLines = "category-line-many-lines";
}

/// <summary>
/// One row of the "Amounts with more decimals than their currency" advisory
/// (<see cref="LedgerConsistencyReport.UnfitAmounts"/>): where a stored figure that does
/// not fit its currency lives, or a category line the Category amounts repair leaves.
/// </summary>
/// <param name="Kind">Where the figure lives (<see cref="UnfitAmountKinds"/>); with the ids
/// below it is what a link opens. The line reads <c>account · date · payee</c> for a
/// transaction, <c>account · opening balance</c>, <c>loan · loan terms</c>,
/// <c>category · month target</c> or <c>reminder · reminder</c>.</param>
/// <param name="AccountId">The account the figure is on, which always exists: the
/// register's account for a transaction (the real account, for a category line beside a
/// real line; the category line's own category, for a posting between categories), the
/// account, the loan, the category of a target, or, for a reminder, the account of the
/// template line that does not fit (never the series' source account, which can be
/// unset).</param>
/// <param name="Account">That account's name.</param>
/// <param name="CurrencyCode">The currency the figure is held to, that account's: for a
/// category line beside a real line, the real side's; for a posting between categories,
/// the category's own (ADR-0114 D15, as the selection sum and the register Balance read
/// it); for a reminder, the template line's account's.</param>
/// <param name="Places">That currency's places, <c>CurrencyMinorUnits.PlacesOf</c>.</param>
/// <param name="HeaderId">The transaction, for <see cref="UnfitAmountKinds.Transaction"/>.</param>
/// <param name="Date">The transaction's posted date.</param>
/// <param name="Payee">The transaction's payee, or the reminder's.</param>
/// <param name="Month">The target's month (its first day), for
/// <see cref="UnfitAmountKinds.BudgetTarget"/>.</param>
/// <param name="ReminderId">The reminder, for <see cref="UnfitAmountKinds.Reminder"/>.</param>
/// <param name="Reason">Null for a figure listed because it carries more places than
/// <paramref name="CurrencyCode"/> allows; for a category line the repair cannot rewrite,
/// why (<see cref="UnfitAmountReasons"/>).</param>
/// <param name="Hidden">For <see cref="UnfitAmountKinds.Transaction"/>, whether the
/// transaction is hidden (ADR-0072 D1). A hidden row keeps its stored figure and comes back
/// on unhide (ADR-0072 D2), so it is listed; its register shows it only in the Hidden view,
/// which is where its link opens. False for every other kind.</param>
public sealed record UnfitAmountAdvisory(
    string Kind,
    Guid AccountId,
    string Account,
    string CurrencyCode,
    int Places,
    Guid? HeaderId = null,
    DateOnly? Date = null,
    string? Payee = null,
    DateOnly? Month = null,
    Guid? ReminderId = null,
    string? Reason = null,
    bool Hidden = false);

/// <summary>Whose currency a <see cref="RefusedCurrencyAdvisory"/> is, as its
/// <see cref="RefusedCurrencyAdvisory.Kind"/>.</summary>
public static class RefusedCurrencyKinds
{
    /// <summary>An account's currency: the account's editor.</summary>
    public const string Account = "account";

    /// <summary>The ledger's reporting currency: the ledger's settings. The line reads
    /// "Reporting currency · code".</summary>
    public const string Reporting = "reporting";
}

/// <summary>
/// One row of the "Currencies Coffer no longer accepts" advisory
/// (<see cref="LedgerConsistencyReport.RefusedCurrencies"/>): a stored code that is not an
/// ISO 4217 currency an account or a ledger can take (not on List One, an "N.A." entry,
/// CLF or UYW). It keeps working at 2 places.
/// </summary>
/// <param name="Kind"><see cref="RefusedCurrencyKinds"/>.</param>
/// <param name="AccountId">The account, for <see cref="RefusedCurrencyKinds.Account"/>;
/// null for the reporting currency.</param>
/// <param name="Account">That account's name; null for the reporting currency.</param>
/// <param name="CurrencyCode">The stored code.</param>
public sealed record RefusedCurrencyAdvisory(
    string Kind,
    Guid? AccountId,
    string? Account,
    string CurrencyCode);

/// <summary>
/// What one projection's repair rewrote, and the whole ledger's verdict once it had:
/// <c>POST …/balances/consistency/{projection}/repair</c>.
/// </summary>
/// <param name="Projection">The projection repaired.</param>
/// <param name="Repaired">The check the repair worked from: what disagreed, which the
/// repair rewrote. Healthy, with nothing listed, when there was nothing to rewrite.</param>
/// <param name="After">Every projection checked again once the repair had committed: the
/// verdict the repair records (<c>ledger_consistency_state</c>), and what its
/// <c>consistency.repaired</c> event says still disagrees. Its entry for
/// <paramref name="Projection"/> is what that projection still holds out of step, if
/// anything; another unhealthy entry is a problem this repair does not touch. Null when
/// that check did not finish: the rewrite stands and is recorded, with no verdict, so the
/// stored state reads "not checked yet" and what still disagrees is not known until the
/// next check.</param>
/// <remarks>
/// Every repair reports the same two things, whichever projection it rewrote: what it
/// changed, and the verdict after it. A result that is the pre-repair check for some
/// projections and the post-repair check for others cannot be read without knowing which
/// projection answered.
/// </remarks>
public sealed record ProjectionRepairResult(
    string Projection,
    ProjectionConsistency Repaired,
    LedgerConsistencyReport? After)
{
    /// <summary>
    /// The rows of <see cref="Repaired"/> the repair could not rewrite, each in the shape of a
    /// mismatch, with <see cref="ConsistencyMismatch.Reason"/> the code saying why. Empty for
    /// every projection whose repair rewrites all it reports. The quote_currency repair fills
    /// it (ADR-0114 D5, amendment): a security whose Yahoo lookup failed carries the lookup's
    /// code (<c>fetch-failed</c>, <c>rate-limited</c>, <c>currency-unknown</c>, …), and one it
    /// could not ask carries <c>quote-symbol-not-public</c>, so the answer says which were not
    /// repaired and why without the reader working it out from <see cref="After"/>.
    /// </summary>
    public IReadOnlyList<ConsistencyMismatch> NotRepaired { get; init; } = [];

    /// <summary>This projection as <see cref="After"/> found it. A method, so the
    /// response does not carry the entry twice. Only for a repair a check followed.</summary>
    /// <exception cref="InvalidOperationException">No check followed the repair.</exception>
    public ProjectionConsistency ProjectionAfter() =>
        (After ?? throw new InvalidOperationException("No check followed the repair of " + Projection + "."))
            .Projections.Single(p => p.Projection == Projection);
}

/// <summary>
/// The stored consistency verdict (mig 242): the last check's result, kept so a
/// reader need not run the check. <c>GET …/balances/consistency/state</c>; a ledger
/// nobody has ever checked reads as no verdict, every field null.
/// </summary>
/// <param name="LastCheckedAt">When the last check ran; null when there is no current
/// verdict.</param>
/// <param name="Healthy">The verdict, or null when there is none, as after a restore.
/// Null reads as "not checked yet", never as unhealthy.</param>
/// <param name="FailingProjections">The projections that disagreed; empty when healthy
/// or unknown.</param>
/// <param name="LastRepairedAt">The last repair of anything on the ledger, or null.</param>
public sealed record LedgerConsistencyStateDto(
    DateTime? LastCheckedAt,
    bool? Healthy,
    IReadOnlyList<string> FailingProjections,
    DateTime? LastRepairedAt);

/// <summary>
/// A position some sale took more shares from than it held at the time (FU-097).
/// A FINDING, counted in <see cref="LedgerConsistencyReport.Healthy"/>, but not a
/// projection and with no repair: the walk books the unmatched shares at zero cost,
/// so the realized gain is overstated by their proceeds, and the purchase they are
/// missing is not in the data. The fix is the reader's: correct the sale, or enter
/// the purchase. Since migration 256 no create or edit can make one, or make one
/// worse; these come from before it, or from an import.
/// </summary>
/// <param name="AccountId">The user-visible brokerage, so a link opens its register.</param>
/// <param name="SharesShort">Shares the position's sales took beyond what it held.</param>
public sealed record OversoldPosition(
    Guid AccountId,
    string Account,
    Guid SecurityId,
    string Security,
    decimal SharesShort);

/// <summary>
/// One security holding prices that claim a trade produced them while no trade
/// currently does. ADVISORY — deliberately not a projection and not counted in
/// <see cref="LedgerConsistencyReport.Healthy"/>.
/// </summary>
/// <remarks>
/// <c>security_prices</c> records THAT a trade wrote a row and never WHICH one,
/// so this can only be inferred by re-deriving and finding nothing — which
/// cannot tell a deleted trade (ADR-0084 D4 keeps its price on purpose) from a
/// rule change from an edit. Reporting all three as defects would mark a sound
/// ledger broken with rows none of which a repair can fix, so these are advisory.
/// <para>
/// Grouped per security because that is the unit someone acts on, and ordered
/// by what the holding is worth now, so what still carries money comes first.
/// </para>
/// </remarks>
/// <param name="Count">How many of this security's prices are unbacked.</param>
/// <param name="HoldingValue">What is held now, in the ledger's reporting currency: each
/// position at its current price, or at its cost basis when it has none, converted at
/// the latest rate and summed across the holding accounts (ADR-0114 D7); 0 for a
/// position since sold, which sorts it last without hiding it. Null, with
/// <paramref name="RateUnavailable"/>, when a rate is missing (ADR-0114 D9); such a
/// row sorts after every valued one.</param>
/// <param name="CurrencyCode">The reporting currency <paramref name="HoldingValue"/> is
/// in.</param>
/// <param name="RateUnavailable">Every missing rate the value needed.</param>
public sealed record UnbackedPriceAdvisory(
    Guid SecurityId,
    string Security,
    int Count,
    DateOnly Earliest,
    DateOnly Latest,
    decimal? HoldingValue,
    string CurrencyCode,
    IReadOnlyList<RateUnavailable> RateUnavailable);
