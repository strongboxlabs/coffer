using Coffer.Api.Auth;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Quotes.MarketSeries;

/// <summary>
/// ADR-0114 D11's one rule for a write made through an MCP tool: what the ledger needs is
/// read before the write (<see cref="ReadAsync"/>), and after it the backfill starts as the
/// calling user when the write added a needed pair or moved a start earlier
/// (<see cref="StartIfWidenedAsync"/>). The request's own context reads both, so the second
/// read sees the write. Scoped.
/// </summary>
public sealed class MarketSeriesWriteTrigger
{
    private readonly AppDbContext _db;
    private readonly MarketSeriesBackfill _backfill;
    private readonly ICurrentUserAccessor _currentUser;

    public MarketSeriesWriteTrigger(AppDbContext db, MarketSeriesBackfill backfill, ICurrentUserAccessor currentUser)
    {
        _db = db;
        _backfill = backfill;
        _currentUser = currentUser;
    }

    /// <summary>What <paramref name="ledgerId"/> needs now (<see cref="NeededFxPairs.StartsAsync"/>).</summary>
    internal Task<NeededStarts> ReadAsync(Guid ledgerId, CancellationToken cancellationToken) =>
        NeededFxPairs.StartsAsync(_db, ledgerId, cancellationToken);

    /// <summary>After a write that landed: starts the backfill when it widened what
    /// <paramref name="before"/> read (<see cref="MarketSeriesBackfill.StartIfEarlierAsync"/>).</summary>
    internal Task StartIfWidenedAsync(Guid ledgerId, NeededStarts before, CancellationToken cancellationToken) =>
        _backfill.StartIfEarlierAsync(_db, ledgerId, _currentUser.UserId, before, cancellationToken);
}
