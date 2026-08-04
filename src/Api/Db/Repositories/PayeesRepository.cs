using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Aggregation gateway behind <c>GET /api/ledgers/{ledgerId}/payees</c>.
/// Builds the typeahead source the SPA's payee field consumes: every
/// distinct resolved payee in the ledger, ranked by usage count then
/// recency, with hidden + merged-away headers filtered out.
/// </summary>
/// <remarks>
/// The query LEFT JOINs <c>txn_headers</c> against
/// <c>txn_header_overrides</c> and projects
/// <c>COALESCE(override.payee, header.payee)</c> as the resolved value
/// — same precedence the <c>resolved_transactions</c> view uses, kept
/// in C# here because aggregating over a per-leg view would
/// double-count splits. RLS on <c>txn_headers</c> + the override table
/// scopes the read to ledgers the caller already has a grant on; the
/// endpoint still proves visibility via <see cref="LedgersRepository"/>
/// before delegating here so the 422 surfaces before the work
/// happens.
/// </remarks>
public sealed class PayeesRepository
{
    /// <summary>
    /// Cap on the suggestion list. A personal-finance user has
    /// O(hundreds) of distinct payees over many years; 500 is high
    /// enough to never bite the realistic case and low enough that a
    /// pathological ledger doesn't blow up the response.
    /// </summary>
    public const int MaxSuggestions = 500;

    private readonly AppDbContext _db;

    public PayeesRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Every distinct resolved payee in <paramref name="ledgerId"/>,
    /// ordered by count desc then last-used-at desc. Headers that
    /// resolve to a NULL payee (no override, no header value) are
    /// excluded — they'd render as an empty suggestion.
    /// </summary>
    public async Task<IReadOnlyList<PayeeSuggestion>> ListByLedgerAsync(
        Guid ledgerId, CancellationToken cancellationToken = default) =>
        await _db.LedgerPayeeSuggestions(ledgerId, MaxSuggestions)
            .Select(r => new PayeeSuggestion(r.Name, (int)r.Count, r.LastUsedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
