using Coffer.Api.Db.Entities;
using Coffer.Domain.Investment;

namespace Coffer.Api.Db;

/// <summary>
/// Maps a <see cref="ResolvedTransactionView"/> row to the domain projector's leg
/// input (ADR-0080). Shared by the register read (RegisterRepository) and the MCP
/// activity feed (InvestmentReportingRepository) so both feed
/// <see cref="InvestmentEventProjector"/> from one mapping — the view is the single
/// source for a leg's post-override amount / role / security / counterparty.
/// </summary>
internal static class InvestmentEventLegMapping
{
    public static InvestmentEventLeg ToEventLeg(ResolvedTransactionView r) => new(
        Id: r.Id,
        LegIndex: r.LegIndex,
        Amount: r.Amount,
        BalanceAfter: r.BalanceAfter,
        HasOverrides: r.HasOverrides,
        PostingRole: r.PostingRole,
        DerivedAction: r.DerivedAction,
        CounterpartyId: r.CounterpartyId,
        SecurityId: r.SecurityId,
        SecurityTicker: r.SecurityTicker,
        SecurityName: r.SecurityName,
        Quantity: r.Quantity,
        UnitPrice: r.UnitPrice,
        CounterpartyAccountId: r.CounterpartyAccountId,
        CounterpartyAccountName: r.CounterpartyAccountName,
        CounterpartyAccountType: r.CounterpartyAccountType);
}
