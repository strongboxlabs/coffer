using System.Text;

using Coffer.Api.Crypto;
using Coffer.Api.Errors;
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
    private readonly TimeProvider _clock;

    public SimpleFinPullProvider(
        SimpleFinClient client,
        LedgerKeyService ledgerKeys,
        TimeProvider clock)
    {
        _client = client;
        _ledgerKeys = ledgerKeys;
        _clock = clock;
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
        var floor = _clock.GetUtcNow().UtcDateTime.AddDays(-MaxWindowDays).Add(WindowSafetyMargin);
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

        var errors = feed.Errors
            .Select(e => new IngestError(e.Code, e.Msg, e.ConnId, e.AccountId))
            .ToList();

        // An account's currency must match what is imported into it (ADR-0114 D13). A
        // binding made before the bind refused a mismatch, or made before the directory
        // knew the feed account's currency, is judged here by the same rule: its account's
        // pull fails with an error naming both currencies, and none of its transactions is
        // handed on. It stays in the result, so the directory still records what the feed
        // reports, and its error keeps the account's watermark where it was, so the next
        // sync after the binding is fixed fetches the same window.
        var mappedCurrency = context.MappedAccounts
            .Where(m => m.CurrencyCode is not null)
            .GroupBy(m => m.ExternalId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().CurrencyCode!, StringComparer.Ordinal);
        var pullAccounts = new List<PullAccount>(sfinAccounts.Count);
        foreach (var sf in sfinAccounts)
        {
            var pulled = ToPullAccount(sf);
            if (mappedCurrency.TryGetValue(sf.Id, out var accountCurrency)
                && CurrencyDiffers(sf.Currency, accountCurrency))
            {
                errors.Add(new IngestError(
                    BusinessError.Codes.FeedAccountCurrencyMismatch,
                    $"Account {sf.Id}: the feed reports {sf.Currency!.Trim()} and the account it is bound to "
                    + $"is in {accountCurrency}, so its transactions were not imported. Bind it to an "
                    + $"account in {sf.Currency.Trim()}.",
                    sf.ConnId,
                    sf.Id));
                pulled = pulled with { Transactions = Array.Empty<IngestedTransaction>() };
            }
            pullAccounts.Add(pulled);
        }

        return new PullResult(
            Accounts: pullAccounts,
            Errors: errors,
            RequiresReauth: false);
    }

    /// <summary>
    /// Whether a feed account's reported currency differs from a Coffer account's
    /// (ADR-0114 D13): the one rule the bind and the sync both apply.
    /// </summary>
    /// <remarks>
    /// A feed account that reports no currency is not judged. One that reports a value
    /// that is not a currency code (SimpleFIN names a custom currency by URL) differs from
    /// every account, so it fails closed. Otherwise the codes are compared in their stored
    /// form (<see cref="Coffer.Api.Quotes.CurrencyCodes.TryNormalize"/>).
    /// </remarks>
    public static bool CurrencyDiffers(string? feedCurrency, string accountCurrency)
    {
        ArgumentNullException.ThrowIfNull(accountCurrency);
        if (string.IsNullOrWhiteSpace(feedCurrency)) return false;
        if (!Coffer.Api.Quotes.CurrencyCodes.TryNormalize(feedCurrency, out var feed)) return true;
        return !string.Equals(feed, accountCurrency.Trim().ToUpperInvariant(), StringComparison.Ordinal);
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
    /// The bridge's cap check is not exact to the second: it may compare
    /// whole days or use its own clock, so a start date that is only
    /// hours inside the nominal boundary can still trip the warning. The
    /// floor therefore sits a whole day inside the cap
    /// (<see cref="WindowSafetyMargin"/>), which absorbs day-aligned
    /// comparison and any sub-day clock skew.</summary>
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
