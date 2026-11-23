namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The status rule: each leg's <c>stat</c> from its account's rule on
/// the row's date, as a ledger in use ages.
/// </summary>
/// <remarks>
/// <para><b>Statement accounts</b> (a <see cref="DemoAccount.Statement"/> cycle:
/// checking, savings, the cards). Call the account's last closed statement S−1 and
/// the open one S. Rows through S−2 are <c>R</c>. Joint Checking's rows inside S−1
/// are uncleared (<c>' '</c>), so the seeder can half-reconcile that statement; the
/// other statement accounts' S−1 rows are <c>c</c>. Rows after the last close are
/// <c>c</c> when they came by a feed and are more than two days old, uncleared
/// otherwise.</para>
///
/// <para><b>Other accounts.</b> Investment accounts: <c>R</c> after ninety days,
/// uncleared within the last seven, <c>c</c> between. Every other account (the cash
/// wallet, the house, escrow, the loans, the payment plan): <c>c</c> after thirty
/// days, uncleared within them.</para>
///
/// <para><b>Per leg</b> (ADR-0082). The header's <c>stat</c> follows the row's own account; a
/// split on another real account (a transfer leg) gets that account's rule in its
/// <c>{N}.stat</c>. Category and holding splits carry none. Moneydance's <c>c</c>,
/// <c>x</c> and <c>R</c> all import as cleared, so only <c>' '</c> survives as
/// uncleared in Coffer.</para>
///
/// <para><see cref="Apply"/> runs once after every pattern and after provenance (the
/// open-statement rule reads whether a row came by a feed), so a pattern's rows get
/// their status without asking.</para>
/// </remarks>
public static class DemoStatusRule
{
    /// <summary>Reconciled.</summary>
    public const string Reconciled = "R";

    /// <summary>Cleared.</summary>
    public const string Cleared = "c";

    /// <summary>Uncleared.</summary>
    public const string Uncleared = " ";

    /// <summary>Days a feed row in the open statement stays uncleared.</summary>
    public const int FeedClearsAfterDays = 2;

    /// <summary>Days a hand-kept account's row stays uncleared.</summary>
    public const int ManualClearsAfterDays = 30;

    /// <summary>Days an investment row stays uncleared.</summary>
    public const int InvestmentUnclearedDays = 7;

    /// <summary>Days after which an investment row is reconciled.</summary>
    public const int InvestmentReconciledAfterDays = 90;

    /// <summary>Set every exported and simulated row's header and transfer-leg
    /// status.</summary>
    public static void Apply(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var rule = new Rule(book);
        foreach (var txn in book.Transactions)
        {
            var own = book.AccountById(txn.AccountId)!;
            txn.Status = rule.StatusOf(own, txn.Date, DemoProvenance.IsFeedRow(book, txn));
            foreach (var split in txn.Splits)
            {
                if (book.AccountById(split.AccountId) is not { IsHolding: false } other) continue;
                var feed = txn.Exported && txn.Entry != DemoEntry.Manual
                    && DemoProvenance.FeedOf(book, other, txn.Date) != DemoFeed.None;
                split.Status = rule.StatusOf(other, txn.Date, feed);
            }
        }
    }

    /// <summary>The status a leg on <paramref name="account"/> dated
    /// <paramref name="date"/> gets.</summary>
    public static string StatusOf(DemoBook book, DemoAccount account, DateOnly date, bool feedRow) =>
        new Rule(book).StatusOf(account, date, feedRow);

    /// <summary>The last closed statement of an account with a statement cycle (S−1),
    /// or null when none has closed.</summary>
    public static DemoStatementPeriod? LastClosedStatement(DemoBook book, DemoAccount account)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(account);
        var periods = book.StatementPeriods(account.Id);
        return periods.Count == 0 ? null : periods[^1];
    }

    /// <summary>
    /// Joint Checking's S−1 and every other exported row in it, by export order, that
    /// touches Joint Checking and that the statement lists: the rows the seeder marks
    /// reconciling.
    /// </summary>
    /// <remarks>
    /// The copies the bank re-sent are left out of the count: the statement does not list
    /// them, and the seeder hides them, so none can be marked reconciling while hidden.
    /// </remarks>
    public static DemoReconcileTarget? CheckingReconcile(DemoBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var checking = book.Account(DemoAccountRoles.JointChecking);
        if (LastClosedStatement(book, checking) is not { } period) return null;

        var resent = book.Marked(RegisterExamplesPattern.DuplicateMark).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var rows = book.TransactionsInExportOrder()
            .Where(t => t.Exported && period.Contains(t.Date) && !resent.Contains(t.Id)
                && (t.AccountId == checking.Id || t.Splits.Any(s => s.AccountId == checking.Id)))
            .ToList();
        var marked = new List<string>();
        for (var i = 0; i < rows.Count; i += 2) marked.Add(rows[i].Id);
        return new DemoReconcileTarget(period.OpensAfter.AddDays(1), period.ClosesOn, marked);
    }

    // The statement periods are read once per account per pass.
    private sealed class Rule(DemoBook book)
    {
        private readonly Dictionary<string, IReadOnlyList<DemoStatementPeriod>> _periods = new(StringComparer.Ordinal);

        public string StatusOf(DemoAccount account, DateOnly date, bool feedRow)
        {
            var age = book.Calendar.AsOf.DayNumber - date.DayNumber;
            if (account.Statement is not null) return Statement(account, date, feedRow, age);
            if (account.MdType == "v")
            {
                if (age > InvestmentReconciledAfterDays) return Reconciled;
                return age < InvestmentUnclearedDays ? Uncleared : Cleared;
            }
            return age > ManualClearsAfterDays ? Cleared : Uncleared;
        }

        private string Statement(DemoAccount account, DateOnly date, bool feedRow, int age)
        {
            if (!_periods.TryGetValue(account.Id, out var periods))
            {
                periods = book.StatementPeriods(account.Id);
                _periods[account.Id] = periods;
            }

            if (periods.Count == 0 || date > periods[^1].ClosesOn)
                return feedRow && age > FeedClearsAfterDays ? Cleared : Uncleared;
            if (periods[^1].Contains(date))
                return account.Role == DemoAccountRoles.JointChecking ? Uncleared : Cleared;
            return Reconciled;
        }
    }
}
