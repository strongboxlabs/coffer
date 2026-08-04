using System.Text;

using Coffer.Api.Crypto;
using Coffer.Api.Ingest;
using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Ingest.SimpleFin;

/// <summary>
/// SimpleFIN bank-feed pull provider (ADR-0031 Phase 2). Owns the
/// SimpleFIN-specific concerns: access URL decryption,
/// HTTP fetch via <see cref="SimpleFinClient"/>, smart start-date
/// calculation across mapped account watermarks, and translating
/// the raw <see cref="SimpleFinSyncResponse"/> into ingest-neutral
/// records.
/// </summary>
/// <remarks>
/// All orchestration concerns (sync_runs lifecycle, FITID dedup,
/// txn_legs writes, promote-on-clear, watermark advance) live on
/// <see cref="IngestOrchestrator"/>. Per ADR-0031 §1 the provider
/// is a pure translator — no DB writes happen here.
/// </remarks>
public sealed class SimpleFinPullProvider : IPullProvider
{
    public const string Key = "simplefin";

    private readonly SimpleFinClient _client;
    private readonly LedgerKeyService _ledgerKeys;

    public SimpleFinPullProvider(
        SimpleFinClient client,
        LedgerKeyService ledgerKeys)
    {
        _client = client;
        _ledgerKeys = ledgerKeys;
    }

    public string ProviderKey => Key;

    public async Task<PullResult> PullAsync(
        PullContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var conn = context.Connection;
        if (conn.AccessUrlCiphertext is null)
        {
            // Defensive — the orchestrator already gates on
            // AccessUrlMissing before calling the provider. If we
            // got here without ciphertext the caller is buggy.
            throw new InvalidOperationException(
                "SimpleFinPullProvider invoked with no access URL ciphertext.");
        }

        // Unwrap the per-connection access URL under the ledger's
        // LEK (ADR-0026). Any cryptographic failure surfaces
        // upstream as an unrecoverable fault that the orchestrator
        // maps to a failed sync_run.
        var plaintext = _ledgerKeys.Open(
            context.LedgerWrappedLek,
            conn.AccessUrlCiphertext);
        var accessUrl = Encoding.UTF8.GetString(plaintext);

        // Smart start-date math (per-account watermarks → MIN
        // request date). Provider-specific because SimpleFIN takes
        // ONE start-date per request and we must widen to the
        // earliest mapped-account need.
        var floor = DateTime.UtcNow.AddDays(-MaxWindowDays).Add(WindowSafetyMargin);
        DateTime startUtc;
        if (context.MappedAccounts.Count == 0)
        {
            // No mapped accounts: still ask for the full window so
            // the bank-side directory upsert populates 90 days for
            // future mappings to dedup against (slice 2c.4).
            startUtc = floor;
        }
        else
        {
            startUtc = context.MappedAccounts
                .Select(a => a.LastSyncedAt is { } w
                    ? (w.AddDays(-OverlapDays) > floor ? w.AddDays(-OverlapDays) : floor)
                    : floor)
                .Min();
        }
        var startDate = new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeSeconds();

        var accountFilter = context.AccountIdFilter is not null
            ? new[] { context.AccountIdFilter }
            : null;

        SimpleFinSyncResponse feed;
        try
        {
            feed = await _client.GetAccountsWithTransactionsAsync(
                accessUrl, startDate, accountFilter, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SimpleFinException)
        {
            // Non-403 non-2xx HTTP / parse faults bubble up. The
            // orchestrator catches them, marks sync_run failed,
            // and rethrows so the endpoint's existing error
            // mapping (→ 422) still fires.
            throw;
        }

        if (feed.RequiresReauth)
        {
            return new PullResult(
                Accounts: Array.Empty<PullAccount>(),
                Errors: Array.Empty<IngestError>(),
                RequiresReauth: true);
        }

        // Defensive narrow (slice 2c.3): when a per-account filter
        // is set, ignore any extras SimpleFIN returned anyway.
        // Belt-and-suspenders — we already passed `?account=` to
        // the bank.
        var sfinAccounts = context.AccountIdFilter is not null
            ? feed.Accounts.Where(a => a.Id == context.AccountIdFilter).ToList()
            : feed.Accounts;

        var pullAccounts = sfinAccounts.Select(ToPullAccount).ToList();
        var errors = feed.Errors
            .Select(e => new IngestError(e.Code, e.Msg, e.ConnId, e.AccountId))
            .ToList();

        return new PullResult(
            Accounts: pullAccounts,
            Errors: errors,
            RequiresReauth: false);
    }

    private static PullAccount ToPullAccount(SimpleFinAccount sf)
    {
        var balanceAt = sf.BalanceDateUnix is { } unix
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : (DateTime?)null;

        return new PullAccount(
            ExternalId: sf.Id,
            Name: sf.Name,
            OrgName: sf.OrgName,
            OrgKey: sf.OrgKey,
            Currency: sf.Currency,
            Balance: sf.Balance,
            BalanceAt: balanceAt,
            AvailableBalance: sf.AvailableBalance,
            Transactions: sf.Transactions.Select(ToIngestedTxn).ToList(),
            // ADR-0031 follow-up: pass through SimpleFIN's verbatim
            // account JSON so the orchestrator can store it on the
            // feed_connection_accounts directory row.
            RawAccountPayload: sf.RawJson);
    }

    private static IngestedTransaction ToIngestedTxn(SimpleFinTransaction t)
    {
        var posted = DateTimeOffset.FromUnixTimeSeconds(t.PostedUnix).UtcDateTime;
        // v2 prefers `transacted_at` (user-side date) over
        // `posted` (cleared date) for the user-visible date when
        // present. Fall back to PostedUnix on older feeds.
        DateTime? transacted = t.TransactedAtUnix is { } u && u != t.PostedUnix
            ? DateTimeOffset.FromUnixTimeSeconds(u).UtcDateTime
            : null;

        // Phase 3b: run the description classifier on every
        // SimpleFIN transaction regardless of account type. For
        // bank-shape rows the description doesn't match the
        // investment patterns and both outputs are null — same
        // shape as Phase 2. For brokerage rows where the pattern
        // matches, the orchestrator's brokerage branch (Phase 3c)
        // picks up Action + SecurityTickerHint to dispatch into
        // the investment-shape insert path.
        var (action, ticker) = SimpleFinDescriptionClassifier.Classify(t.Description);

        return new IngestedTransaction(
            ExternalId: t.Id,
            PostedAt: posted,
            TransactedAt: transacted,
            Amount: t.Amount,
            // SimpleFIN ships cleaned `payee` (merchant name) +
            // raw `description` separately. The orchestrator routes
            // payee→txn_headers.payee and description→txn_headers.memo.
            Payee: t.Payee,
            Description: t.Description,
            Pending: t.Pending,
            Action: action,
            SecurityTickerHint: ticker,
            // ADR-0031 follow-up: pass through SimpleFIN's verbatim
            // transaction JSON so the orchestrator can store it on
            // the inserted header for classifier-iteration debugging.
            RawProviderPayload: t.RawJson);
    }

    /// <summary>Maximum lookback window SimpleFIN exposes — exceeding
    /// it triggers the `gen.api` "date range exceeds 90 days" warning.
    /// Empirically the bridge fires the cap warning well inside the
    /// nominal boundary (a 1-hour inset still tripped it on real
    /// first-syncs against bridge.simplefin.org), suggesting either
    /// clock skew or day-aligned comparison on their side, so we
    /// shift the floor inward by <see cref="WindowSafetyMargin"/>.</summary>
    private const int MaxWindowDays = 90;

    /// <summary>Safety margin baked into the first-sync floor so we
    /// don't sit on or near SimpleFIN's 90-day cap boundary.</summary>
    private static readonly TimeSpan WindowSafetyMargin = TimeSpan.FromDays(1);

    /// <summary>Days of overlap between consecutive syncs (slice
    /// 2c.2). Catches transactions the bank back-dated after the
    /// last sync, plus amount adjustments on recently-posted rows.
    /// Standard aggregator pattern — MX recommends 7; YNAB does
    /// the same.</summary>
    private const int OverlapDays = 7;
}
