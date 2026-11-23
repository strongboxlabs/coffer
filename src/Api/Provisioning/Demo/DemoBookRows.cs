namespace Coffer.Api.Provisioning.Demo;

/// <summary>How an account's statements close, for the status rule and for card
/// statement periods.</summary>
public abstract record DemoStatementCycle
{
    private protected DemoStatementCycle()
    {
    }

    /// <summary>Statements close on the last day of each month.</summary>
    public static readonly DemoStatementCycle MonthEnd = new MonthEndCycle();

    /// <summary>Statements close on day <paramref name="day"/> of each month.</summary>
    public static DemoStatementCycle OnDay(int day) =>
        day is >= 1 and <= 28
            ? new DayOfMonthCycle(day)
            : throw new ArgumentOutOfRangeException(nameof(day), day, "a close day is 1 to 28, so every month has it");

    /// <summary>The close date of the statement that closes in the month of
    /// <paramref name="anyDayOfMonth"/>.</summary>
    public abstract DateOnly CloseIn(DateOnly anyDayOfMonth);

    private sealed record MonthEndCycle : DemoStatementCycle
    {
        public override DateOnly CloseIn(DateOnly anyDayOfMonth) =>
            new(anyDayOfMonth.Year, anyDayOfMonth.Month, DateTime.DaysInMonth(anyDayOfMonth.Year, anyDayOfMonth.Month));
    }

    private sealed record DayOfMonthCycle(int Day) : DemoStatementCycle
    {
        public override DateOnly CloseIn(DateOnly anyDayOfMonth) => new(anyDayOfMonth.Year, anyDayOfMonth.Month, Day);
    }
}

/// <summary>One statement: the postings dated after <see cref="OpensAfter"/>
/// through <see cref="ClosesOn"/>.</summary>
/// <param name="OpensAfter">The previous statement's close; not part of this one.</param>
/// <param name="ClosesOn">This statement's close, included.</param>
public readonly record struct DemoStatementPeriod(DateOnly OpensAfter, DateOnly ClosesOn)
{
    /// <summary>True when <paramref name="date"/> falls in the period.</summary>
    public bool Contains(DateOnly date) => date > OpensAfter && date <= ClosesOn;
}

/// <summary>
/// One account of the simulated ledger: a real account (bank, card, investment,
/// asset, liability, loan) or a type-<c>s</c> security holding account.
/// </summary>
/// <remarks>
/// <para><see cref="DateCreated"/> is what the export writes; <see cref="ActiveFrom"/>
/// and <see cref="ActiveThrough"/> bound the dates a posting may fall on. They
/// differ where the export needs them to: a loan's <c>date_created</c> is its first
/// payment date, which the importer reads as the loan's first payment, while the
/// mortgage receives its closing legs two months before it.</para>
///
/// <para>Amounts are <see cref="long"/> minor units of the account's own currency:
/// cents for a real account, the security's smallest share unit for a holding
/// account.</para>
/// </remarks>
public sealed class DemoAccount
{
    /// <summary>The stable role the generator and the manifest name this account by.</summary>
    public required string Role { get; init; }

    /// <summary>The account's MD id.</summary>
    public required string Id { get; init; }

    /// <summary>The display name the export writes.</summary>
    public required string Name { get; init; }

    /// <summary>Moneydance's account type code: <c>b</c>, <c>c</c>, <c>v</c>,
    /// <c>a</c>, <c>l</c>, <c>o</c> or <c>s</c>.</summary>
    public required string MdType { get; init; }

    /// <summary>The <c>date_created</c> the export writes.</summary>
    public required DateOnly DateCreated { get; init; }

    /// <summary>The first day a posting may fall on.</summary>
    public required DateOnly ActiveFrom { get; init; }

    /// <summary>The last day a posting may fall on, once the account closes; null
    /// while it is open.</summary>
    public DateOnly? ActiveThrough { get; set; }

    /// <summary>True when the export marks the account inactive (closed).</summary>
    public bool IsInactive { get; init; }

    /// <summary>The account's <c>sbal</c>: its balance before the first posting.</summary>
    public long OpeningBalance { get; set; }

    /// <summary>The parent account's MD id, or null for the root.</summary>
    public string? ParentId { get; init; }

    /// <summary>The account's currency MD id, or null for the base currency. A
    /// holding account names its security's <c>curr</c>.</summary>
    public string? CurrencyId { get; init; }

    /// <summary>An invented institution name, written as <c>bank_name</c>.</summary>
    public string? BankName { get; init; }

    /// <summary>A masked last-four account number, written as
    /// <c>bank_account_number</c>.</summary>
    public string? BankAccountNumber { get; init; }

    /// <summary>How the account's statements close, if it has statements.</summary>
    public DemoStatementCycle? Statement { get; init; }

    /// <summary>True when no posting may ever touch the account.</summary>
    public bool Dormant { get; init; }

    /// <summary>True when every day-end balance in the window must be zero or more.</summary>
    public bool MustStayNonNegative { get; init; }

    /// <summary>Further export fields, in the order written: loan terms, security
    /// codes, provenance.</summary>
    public List<KeyValuePair<string, string>> ExtraFields { get; } = [];

    /// <summary>True for a type-<c>s</c> security holding account.</summary>
    public bool IsHolding => MdType == "s";

    /// <summary>True when the account may take a posting dated <paramref name="date"/>.</summary>
    public bool IsActiveOn(DateOnly date) =>
        !Dormant && date >= ActiveFrom && (ActiveThrough is not { } through || date <= through);
}

/// <summary>How a row reached the ledger, as far as a pattern decides it.</summary>
public enum DemoEntry
{
    /// <summary>The provenance pass decides from the account and the date: a feed
    /// row on an account with a feed in that era, a hand-entered row elsewhere.</summary>
    Auto,

    /// <summary>Typed in by hand (a cheque, a correction): no feed fields, whatever
    /// the account.</summary>
    Manual,
}

/// <summary>Which importer route a transaction takes.</summary>
public enum DemoRoute
{
    /// <summary>A bank-route row: <c>xfer_type</c> <c>xfrtp_bank</c>, no
    /// <c>invest.txntype</c>.</summary>
    Bank,

    /// <summary>An MD-native investment row: <c>invest.txntype</c> set, splits
    /// carrying <c>invest.splittype</c>.</summary>
    Investment,
}

/// <summary>
/// One line of a transaction, in Moneydance's encoding: <see cref="ParentAmount"/>
/// (<c>pamt</c>) is the amount on the transaction's own account and
/// <see cref="SplitAmount"/> (<c>samt</c>) the amount on this split's account.
/// </summary>
/// <remarks>
/// On a bank route the two are opposite. On a <c>sec</c> split <c>samt</c> is a
/// share count in the security's minor units, so no one sign rule covers both.
/// </remarks>
public sealed class DemoSplit
{
    /// <summary>The split's account: a book account or a base category.</summary>
    public required string AccountId { get; init; }

    /// <summary><c>pamt</c>: the effect on the transaction's own account.</summary>
    public required long ParentAmount { get; init; }

    /// <summary><c>samt</c>: the effect on this split's account.</summary>
    public required long SplitAmount { get; init; }

    /// <summary><c>{N}.desc</c>. Null writes the transaction's description, which
    /// is what Moneydance fills a one-split row's other side with.</summary>
    public string? Description { get; init; }

    /// <summary><c>{N}.stat</c>: the counterpart leg's status, for a transfer.</summary>
    public string? Status { get; set; }

    /// <summary><c>{N}.tags</c>, comma-separated.</summary>
    public string? Tags { get; init; }

    /// <summary><c>{N}.invest.splittype</c> on an investment row: <c>sec</c>,
    /// <c>inc</c>, <c>fee</c>, <c>xfr</c> and the like.</summary>
    public string? SplitType { get; init; }

    /// <summary>The split's MD id, assigned when the transaction is posted.</summary>
    public string Id { get; internal set; } = string.Empty;

    /// <summary>A bank-route split that moves <paramref name="amountOnAccount"/>
    /// onto <paramref name="accountId"/>; the transaction's own account takes the
    /// opposite.</summary>
    public static DemoSplit Bank(
        string accountId, long amountOnAccount, string? description = null, string? tags = null) => new()
        {
            AccountId = accountId,
            SplitAmount = amountOnAccount,
            ParentAmount = -amountOnAccount,
            Description = description,
            Tags = tags,
        };
}

/// <summary>
/// One generated transaction, in the shape the export writes.
/// </summary>
/// <remarks>
/// The book assigns <see cref="Id"/>, <see cref="Pattern"/>, <see cref="PatternRank"/>
/// and <see cref="Ordinal"/> when the row is posted. The export orders rows by
/// (date, pattern rank, pattern name, ordinal). A row with <see cref="Exported"/>
/// false is simulated (it moves balances) but not written: a bill the seeder posts
/// later, or a card row that arrives only in the statement CSV.
/// </remarks>
public sealed class DemoTxn
{
    /// <summary>The MD id of the transaction's own account (<c>acctid</c>).</summary>
    public required string AccountId { get; init; }

    /// <summary><c>dt</c>.</summary>
    public required DateOnly Date { get; init; }

    /// <summary><c>td</c>, when it differs from the date.</summary>
    public DateOnly? TaxDate { get; init; }

    /// <summary><c>desc</c>: the payee.</summary>
    public required string Description { get; init; }

    /// <summary><c>memo</c>.</summary>
    public string? Memo { get; init; }

    /// <summary>The route, which decides <c>xfer_type</c> and whether
    /// <c>invest.txntype</c> is written.</summary>
    public DemoRoute Route { get; init; } = DemoRoute.Bank;

    /// <summary><c>invest.txntype</c> on an investment row.</summary>
    public string? InvestTxnType { get; init; }

    /// <summary><c>xfer_type</c>; a bank row's is <c>xfrtp_bank</c>.</summary>
    public string XferType { get; init; } = "xfrtp_bank";

    /// <summary><c>stat</c>: the origin leg's status. The status rule may set it
    /// after the simulation.</summary>
    public string Status { get; set; } = " ";

    /// <summary><c>chk</c>.</summary>
    public string? CheckNumber { get; init; }

    /// <summary><c>tags</c> on the header, comma-separated.</summary>
    public string? Tags { get; init; }

    /// <summary>How the row reached the ledger. <see cref="DemoEntry.Auto"/> lets the
    /// provenance pass decide from the row's account and date; a pattern marks a
    /// cheque or a hand-entered row <see cref="DemoEntry.Manual"/>.</summary>
    public DemoEntry Entry { get; init; } = DemoEntry.Auto;

    /// <summary>The bank's raw payee string, written as <c>ol.orig-payee</c> when the
    /// row arrives by a feed. Null lets the provenance pass derive one from the
    /// payee; set it equal to <see cref="Description"/> for a payee the bank already
    /// spells cleanly.</summary>
    public string? BankPayee { get; init; }

    /// <summary>The bank's raw memo string, written as <c>ol.orig-memo</c> when the
    /// row arrives by a feed. Null writes none.</summary>
    public string? BankMemo { get; init; }

    /// <summary><c>ol_fi_id</c>. Set by the provenance pass.</summary>
    public string? OlFiId { get; set; }

    /// <summary><c>ol_fitid_1</c>. Set by the provenance pass.</summary>
    public string? OlFitid { get; set; }

    /// <summary><c>ol.orig-payee</c>: the bank's own payee string. Set by the
    /// provenance pass.</summary>
    public string? OlOrigPayee { get; set; }

    /// <summary><c>ol.orig-memo</c>: the bank's own memo string. Set by the
    /// provenance pass.</summary>
    public string? OlOrigMemo { get; set; }

    /// <summary>Days after the date the row was entered: 0 by hand, 1 or 2 for a
    /// feed row. Set by the provenance pass.</summary>
    public int EnteredDaysLater { get; set; }

    /// <summary>False for a row that is simulated but not written.</summary>
    public bool Exported { get; init; } = true;

    /// <summary>The lines, in order. At most 32.</summary>
    public required IReadOnlyList<DemoSplit> Splits { get; init; }

    /// <summary>The MD id, assigned when posted.</summary>
    public string Id { get; internal set; } = string.Empty;

    /// <summary>The pattern that posted the row.</summary>
    public string Pattern { get; internal set; } = string.Empty;

    /// <summary>The posting pattern's same-day rank.</summary>
    public int PatternRank { get; internal set; }

    /// <summary>The row's ordinal within its pattern, from 0.</summary>
    public long Ordinal { get; internal set; }
}

/// <summary>An open FIFO lot: <see cref="Shares"/> in the security's minor units
/// bought on <see cref="Acquired"/> for <see cref="Cost"/> cents.</summary>
public readonly record struct DemoLot(DateOnly Acquired, long Shares, long Cost);

/// <summary>One disposal out of one lot.</summary>
/// <param name="Acquired">The lot's acquisition date.</param>
/// <param name="Disposed">The sale date.</param>
/// <param name="Shares">Shares taken from the lot.</param>
/// <param name="Cost">The cost basis those shares carried, in cents.</param>
public readonly record struct DemoDisposal(DateOnly Acquired, DateOnly Disposed, long Shares, long Cost)
{
    /// <summary>True when the lot was held more than one year.</summary>
    public bool LongTerm => Disposed > Acquired.AddYears(1);
}
