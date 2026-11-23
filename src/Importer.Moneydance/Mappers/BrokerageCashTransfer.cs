using Coffer.Domain.Investment;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// The one rule that gives a brokerage row on the BANK route an investment
/// action. Shared by <see cref="TransactionMapper"/> and
/// <see cref="ReminderMapper"/> so a live row and a reminder template of the
/// same shape classify identically.
/// </summary>
/// <remarks>
/// <para>A brokerage row reaches the bank route whenever
/// <see cref="Json.Typed.MdTxn.IsInvestmentShape"/> is false: QIF-origin cash
/// rows (<c>qif_invst_action</c> XOut / XIn / Cash / ContribX / MiscIncX with
/// <c>xfrtp_bank</c>, no <c>invest.txntype</c>) and plain rows with no
/// <c>xfer_type</c> at all. ADR-0027 already names those QIF words 'transfer',
/// and the investment route writes exactly that for txntype <c>bank</c>
/// (<see cref="InvestmentPostings.BuildXferPair"/>: header action and posting
/// role 'transfer' on both legs). Left action-less, a brokerage row whose
/// other side is a category derives no action in the register and the
/// investment editor opens on "Pick action...".</para>
///
/// <para>Exactly ONE emittable posting, counted after the self-reference
/// filter. 'transfer' is cash between the brokerage and ONE other account, which
/// is all the Xfr editor can show; with two or more postings the row fails closed
/// and stays action-less, every posting intact, rather than asserting a shape.</para>
/// </remarks>
public static class BrokerageCashTransfer
{
    /// <summary>
    /// The header action and per-leg posting role for a bank-route event whose
    /// primary (origin) account is <paramref name="primary"/> and which emits
    /// <paramref name="emittablePostings"/> postings. Both null unless the rule
    /// applies.
    /// </summary>
    public static (string? Action, string? PostingRole) Classify(AccountRef primary, int emittablePostings)
    {
        ArgumentNullException.ThrowIfNull(primary);

        return primary.AccountType == "investment" && emittablePostings == 1
            ? (LedgerActions.Transfer, PostingRoles.Transfer)
            : (null, null);
    }
}
