using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Ingest;
using Coffer.Api.Loans;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger accounts endpoints. Same authorisation contract as
/// <see cref="LedgersEndpoints"/>: require an authenticated user, then
/// verify the user has a grant on the ledger (422 ledger-not-visible
/// otherwise). Once Phase D RLS lands (PR 3.8), Postgres enforces the
/// same predicate as a defence-in-depth layer.
/// </summary>
public static class AccountsEndpoints
{
    public static IEndpointRouteBuilder MapAccountsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/accounts")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/", ListAsync);
        // ADR-0050: account create + general-attribute edit (all types). The
        // editor screen + sidebar "Edit account" both target these.
        group.MapPost("/", CreateAsync);
        // ADR-0050 slice 3: stateless amortization preview for the editor's
        // Loan Terms block. Literal segment — no collision with the {accountId}
        // routes.
        group.MapPost("/loan-payment-preview", LoanPaymentPreviewAsync).AsLedgerRead();
        // FU-052: every real account's current balance, for the sidebar. Literal
        // segment, like the preview above.
        group.MapGet("/balances", ListBalancesAsync);
        // The sidebar's section and folder totals, converted (ADR-0114 D7). Literal
        // segment, like the two above.
        group.MapGet("/subtotals", SubtotalsAsync);
        // The home screen's Cash ahead: each account's balance over a short window ahead.
        // A read, so any member's; literal segment, like the two above.
        group.MapGet("/balance-forecast", BalanceForecastAsync);
        group.MapGet("/{accountId:guid}", GetAccountAsync);
        group.MapPatch("/{accountId:guid}", UpdateAsync);
        // Which bank account feeds this one, and from when, is feed configuration: the
        // owner's, like connecting the bank (FU-095).
        group.MapPatch("/{accountId:guid}/feed-mapping", PatchFeedMappingAsync).AsLedgerOwner();
        // Slice 2c.4: clear the feed binding (unmap) — paired with
        // the PATCH bind, completes the mapping lifecycle the
        // unified accounts panel drives.
        group.MapDelete("/{accountId:guid}/feed-mapping", DeleteFeedMappingAsync).AsLedgerOwner();
        // Slice 2c.3: per-account sync — narrows the SimpleFIN
        // call to one account on its bound connection.
        group.MapPost("/{accountId:guid}/sync", SyncAccountAsync);
        // Slice 2c.5: user-resettable per-account sync watermark.
        // PATCH with a date sets the watermark; PATCH with null
        // clears it ("backfill 90 days on next sync").
        group.MapPatch("/{accountId:guid}/sync-from-date", PatchSyncFromDateAsync).AsLedgerOwner();
        // Slice A1: Portfolio View read surface for investment accounts.
        group.MapGet("/{accountId:guid}/holdings", GetHoldingsAsync);
        // ADR-0043: most-used counterparties for this account, to float
        // to the top of the account/category picker.
        group.MapGet("/{accountId:guid}/frequent-counterparties", GetFrequentCounterpartiesAsync);
        // Slice A4.a: per-brokerage "treat in-transaction fees as cost
        // basis" toggle. Flips `accounts.is_trade_commission` and recomputes
        // the brokerage's holdings in the same transaction so the
        // holdings.cost_basis + lots.unit_cost converge before the response
        // returns.
        group.MapPatch("/{accountId:guid}/trade-commission", PatchTradeCommissionAsync);
        // Inactive-accounts slice: flip the per-account `is_active`
        // flag. Default-list filter excludes inactive accounts, so
        // toggling this hides the account from every picker + the
        // sidebar's default view. Re-activation symmetric.
        group.MapPatch("/{accountId:guid}/active", PatchActiveAsync);
        // ADR-0050 ext (mig 168): set up the managed payment reminder for a loan
        // account (the scheduled auto-payment whose split is computed from the
        // loan terms). Read side is on the account detail's `managedReminder`.
        group.MapPost("/{accountId:guid}/payment-reminder", SetupPaymentReminderAsync);

        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/accounts/{accountId}/holdings</c>
    /// — slice A1. Portfolio View payload for one investment account:
    /// per-security positions (quantity, cost basis, latest price,
    /// current value, unrealized) plus a summary block + the brokerage's
    /// cash-side balance. Read-only.
    ///
    /// 422 cases: <c>ledger-not-visible</c>, <c>account-not-in-ledger</c>,
    /// <c>account-not-investment</c> (only investment accounts have
    /// holdings), <c>account-missing-holdings-sibling</c> (the
    /// system-managed Holdings sibling per ADR-0019 was never created
    /// for this brokerage — recoverable by re-running the importer).
    /// </summary>
    private static async Task<IResult> GetHoldingsAsync(
        Guid ledgerId,
        Guid accountId,
        // The caller's local date (?today=), as the register takes it: the card counts
        // exactly the rows the register does not call Scheduled. Absent, the
        // register's own fallback applies.
        DateOnly? today,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        HoldingsRepository holdings,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await holdings.GetByBrokerageAsync(
            ledgerId, accountId, today, cancellationToken).ConfigureAwait(false);
        return result.Kind switch
        {
            HoldingsRepository.ResultKind.Ok => Results.Ok(result.View),
            HoldingsRepository.ResultKind.AccountNotInLedger =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "Account does not belong to this ledger."),
            HoldingsRepository.ResultKind.NotAnInvestmentAccount =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInvestment,
                    "Holdings are only available on investment accounts."),
            HoldingsRepository.ResultKind.NoHoldingsSibling =>
                BusinessError.Problem(BusinessError.Codes.AccountMissingHoldingsSibling,
                    "This investment account is missing its Holdings sibling — re-run the importer to repair."),
            _ => Results.Problem("Unknown holdings result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/accounts/{accountId}/frequent-counterparties</c>
    /// — ADR-0043. Top-N most-used counterparty accounts + categories
    /// for this account, derived from transaction history. Powers the
    /// "Frequent" pinned group in the account/category picker.
    /// </summary>
    private const int FrequentPerKind = 3;

    private static async Task<IResult> GetFrequentCounterpartiesAsync(
        Guid ledgerId,
        Guid accountId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var belongs = await accounts.BelongsToLedgerAsync(
            ledgerId, accountId, cancellationToken).ConfigureAwait(false);
        if (!belongs)
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        var result = await accounts.GetFrequentCounterpartiesAsync(
            ledgerId, accountId, FrequentPerKind, clock.GetUtcNow().UtcDateTime, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(result);
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/sync-from-date</c>
    /// — slice 2c.5. Set or clear the per-account SimpleFIN sync
    /// watermark so the next sync against this account asks the
    /// bank for transactions from a user-chosen date forward.
    /// </summary>
    private static async Task<IResult> PatchSyncFromDateAsync(
        Guid ledgerId,
        Guid accountId,
        PatchAccountSyncFromDateRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (!await accounts.BelongsToLedgerAsync(ledgerId, accountId, cancellationToken).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        var result = await accounts.SetSyncFromDateAsync(
            ledgerId, accountId, request.SyncFromDate, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            AccountsRepository.SetSyncFromDateResult.Ok => Results.NoContent(),
            AccountsRepository.SetSyncFromDateResult.AccountNotBoundToFeed =>
                BusinessError.Problem(BusinessError.Codes.AccountNotBoundToFeed,
                    "This account is not bound to a SimpleFIN connection. Map it from the Bank feeds page first."),
            AccountsRepository.SetSyncFromDateResult.DateInFuture =>
                BusinessError.Problem(BusinessError.Codes.SyncFromDateInFuture,
                    "Sync-from date cannot be in the future."),
            _ => Results.Problem("Unknown set-sync-from-date result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>DELETE /api/ledgers/{ledgerId}/accounts/{accountId}/feed-mapping</c>
    /// — slice 2c.4. Clears <c>accounts.feed_connection_id</c> +
    /// <c>accounts.external_id</c>. The user changes a mapping via
    /// re-PATCH; this is the explicit "unmap" path. Idempotent
    /// (unmapping an already-unmapped account returns 204).
    /// </summary>
    private static async Task<IResult> DeleteFeedMappingAsync(
        Guid ledgerId,
        Guid accountId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (!await accounts.BelongsToLedgerAsync(ledgerId, accountId, cancellationToken)
                           .ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        await accounts.UnbindFeedMappingAsync(ledgerId, accountId, cancellationToken)
                      .ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/accounts/{accountId}/sync</c>
    /// — slice 2c.3. Sync ONE Coffer account that's bound to a
    /// SimpleFIN connection. Returns the same
    /// <see cref="SyncResultDto"/> shape as the per-connection
    /// sync, scoped to one bound SimpleFIN account.
    /// </summary>
    private static async Task<IResult> SyncAccountAsync(
        Guid ledgerId,
        Guid accountId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        IngestOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (!await accounts.BelongsToLedgerAsync(ledgerId, accountId, cancellationToken).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        var binding = await accounts.GetFeedBindingAsync(ledgerId, accountId, cancellationToken)
                                    .ConfigureAwait(false);
        if (binding is null)
            return BusinessError.Problem(BusinessError.Codes.AccountNotBoundToFeed,
                "This account is not bound to a SimpleFIN connection. Map it from the Bank feeds page first.");

        var outcome = await orchestrator.RunPullAsync(
            ledgerId, binding.FeedConnectionId, currentUser.UserId,
            accountIdFilter: binding.ExternalId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return SyncEndpointMapping.ToResult(outcome);
    }

    private static async Task<IResult> PatchFeedMappingAsync(
        Guid ledgerId,
        Guid accountId,
        PatchAccountFeedMappingRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FeedConnectionId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.SimpleFinAccountId))
        {
            return BusinessError.Problem(BusinessError.Codes.FeedMappingTargetRequired,
                "feedConnectionId and simpleFinAccountId are both required.");
        }

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (!await accounts.BelongsToLedgerAsync(ledgerId, accountId, cancellationToken).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        var outcome = await accounts.BindFeedMappingAsync(
            ledgerId, accountId,
            request.FeedConnectionId, request.SimpleFinAccountId.Trim(),
            cancellationToken).ConfigureAwait(false);
        return outcome.Result switch
        {
            AccountsRepository.BindFeedMappingResult.Ok => Results.NoContent(),
            AccountsRepository.BindFeedMappingResult.ConnectionMismatch =>
                BusinessError.Problem(BusinessError.Codes.FeedMappingConnectionMismatch,
                    "Feed connection does not belong to this ledger."),
            // An account's currency must match what is imported into it (ADR-0114 D13).
            AccountsRepository.BindFeedMappingResult.CurrencyMismatch =>
                BusinessError.Problem(BusinessError.Codes.FeedAccountCurrencyMismatch,
                    $"This feed account is in {outcome.FeedCurrency} and this account is in "
                    + $"{outcome.AccountCurrency}. Bind it to an account in {outcome.FeedCurrency}."),
            _ => Results.Problem("Unknown bind result.", statusCode: 500),
        };
    }

    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken,
        // Inactive-accounts slice: opt-in for the "Show inactive"
        // sidebar toggle + account-settings dialog. Default false
        // keeps every existing consumer (pickers, sidebar default
        // render) seeing only active accounts.
        bool? includeInactive = null)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await accounts.ListByLedgerAsync(
            ledgerId, includeInactive ?? false, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(rows);
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/accounts/balances</c> — FU-052. One row per
    /// real account, inactive ones included, with the balance the dashboard shows:
    /// a projection of <see cref="AccountsReportingRepository.ListAccountsAsync"/>, so
    /// the rule (cash, plus holdings market value for an investment account;
    /// liabilities negative) has one definition. Kept off the accounts list on
    /// purpose: every picker reads that list and would pay for the valuation.
    /// </summary>
    private static async Task<IResult> ListBalancesAsync(
        Guid ledgerId,
        // The caller's local date (?today=), as the register takes it.
        DateOnly? today,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsReportingRepository reporting,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await reporting.ListAccountsAsync(
            ledgerId, includeCategories: false, includeInactive: true, type: null, today,
            cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(rows
            .Where(a => a.Balance is not null)
            .Select(a => new AccountBalanceDto(a.Id, a.Balance!.Value, a.CurrencyCode))
            .ToList());
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/accounts/subtotals?today</c> — the sidebar's totals:
    /// each account type's and each folder's (<see cref="OverviewRepository.SubtotalsAsync"/>).
    /// Each combines accounts, so it is in the ledger's reporting currency, converted at the
    /// latest rate, and null with the missing rates named when one is missing (ADR-0114 D7,
    /// D9). Never summed in the browser: a sum there would add currencies.
    ///
    /// 422 case: <c>ledger-not-visible</c>.
    /// </summary>
    private static async Task<IResult> SubtotalsAsync(
        Guid ledgerId,
        // The caller's local date (?today=), as the register takes it.
        DateOnly? today,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        OverviewRepository overview,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        return Results.Ok(await overview.SubtotalsAsync(ledgerId, today, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/accounts/balance-forecast?today&amp;days</c> — each
    /// account's balance from the caller's today through <c>days</c> ahead
    /// (<see cref="BalanceForecastRepository"/>): day 0 is the register's current
    /// balance, then the register's committed rows and the reminders not yet posted, over
    /// every account a reminder is paid from. <c>days</c> defaults to
    /// <see cref="BalanceForecastRepository.DefaultDays"/> and is clamped to 1 through
    /// <see cref="BalanceForecastRepository.MaxDays"/>.
    ///
    /// 422 case: <c>ledger-not-visible</c>.
    /// </summary>
    private static async Task<IResult> BalanceForecastAsync(
        Guid ledgerId,
        // The caller's local date (?today=), as the register takes it.
        DateOnly? today,
        int? days,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        BalanceForecastRepository forecasts,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        return Results.Ok(await forecasts.GetAsync(ledgerId, today, days, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/accounts/{accountId}</c> — ADR-0050. The
    /// full editable shape of one account (incl. the metadata the list omits)
    /// for the editor's edit mode. 422 <c>account-not-in-ledger</c> otherwise.
    /// </summary>
    private static async Task<IResult> GetAccountAsync(
        Guid ledgerId,
        Guid accountId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var detail = await accounts.GetDetailAsync(ledgerId, accountId, cancellationToken).ConfigureAwait(false);
        return detail is null
            ? BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.")
            : Results.Ok(detail);
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/accounts</c> — ADR-0050. Create an
    /// account of any type. Validation (name, type catalog, category-kind
    /// invariant, currency, parent) lives in the repository; this maps the
    /// outcome. 201 with the created <see cref="AccountSummary"/> on success.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        Guid ledgerId,
        CreateAccountRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        Quotes.MarketSeries.MarketSeriesBackfill backfill,
        Db.AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // ADR-0114 D11, one rule: a change that adds a needed pair or moves one's start
        // earlier starts the backfill. Both are asked before the create, which adds the
        // account (and a brokerage's holdings sibling). An account in a currency no other
        // account of the ledger is kept in, and that is not the reporting currency, needs
        // rates the ledger has never fetched. An account in any currency, the reporting one
        // included, opened before the ledger's earliest day moves every net-worth pair's start
        // to its opened date, and a funded brokerage opened before the first funding moves the
        // benchmarks' start; the starts are read before the create and again after it.
        var currency = string.IsNullOrWhiteSpace(request.CurrencyCode) ? DisplayCurrency.Fallback : request.CurrencyCode;
        var newCurrency = request.AccountType != "category"
            && Quotes.CurrencyCodes.TryNormalize(currency, out currency)
            && !string.Equals(currency, visible.CurrencyCode, StringComparison.Ordinal)
            && !await accounts.CurrencyHeldByAnotherAccountAsync(ledgerId, currency, [], cancellationToken)
                .ConfigureAwait(false);
        NeededStarts? before = !newCurrency && request.AccountType != "category" && request.OpenedOn is not null
            ? await NeededFxPairs.StartsAsync(db, ledgerId, cancellationToken).ConfigureAwait(false)
            : null;

        var outcome = await accounts.CreateAsync(ledgerId, request, cancellationToken).ConfigureAwait(false);
        if (outcome.Failure == AccountsRepository.CreateAccountFailure.None)
        {
            if (newCurrency) backfill.Start(ledgerId, currentUser.UserId);
            else if (before is { } read)
                await backfill.StartIfEarlierAsync(db, ledgerId, currentUser.UserId, read, cancellationToken)
                    .ConfigureAwait(false);
        }
        return outcome.Failure switch
        {
            AccountsRepository.CreateAccountFailure.None =>
                Results.Created(
                    $"/api/ledgers/{ledgerId}/accounts/{outcome.Account!.Id}", outcome.Account),
            AccountsRepository.CreateAccountFailure.NameRequired =>
                BusinessError.Problem(BusinessError.Codes.AccountNameRequired,
                    "Account name is required."),
            AccountsRepository.CreateAccountFailure.TypeInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountTypeInvalid,
                    "Unknown account type."),
            AccountsRepository.CreateAccountFailure.CategoryKindInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountCategoryKindInvalid,
                    "A category requires kind 'income', 'expense' or 'adjustment'; other account types must not set a category kind."),
            AccountsRepository.CreateAccountFailure.CurrencyInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountCurrencyInvalid, AccountCurrencyInvalidDetail),
            AccountsRepository.CreateAccountFailure.AmountPlacesExceedCurrency =>
                MoneyPlaces.Refusal(outcome.Unfit!.Value.CurrencyCode, outcome.Unfit.Value.Field),
            AccountsRepository.CreateAccountFailure.CategoryCurrencyNotReporting =>
                CategoryCurrencyProblem(visible.CurrencyCode),
            AccountsRepository.CreateAccountFailure.ParentInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountParentInvalid,
                    "Parent must be a category in this ledger."),
            AccountsRepository.CreateAccountFailure.OpeningBalanceInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountOpeningBalanceInvalid,
                    "Categories must have a zero opening balance."),
            AccountsRepository.CreateAccountFailure.LoanTermsRequired =>
                BusinessError.Problem(BusinessError.Codes.AccountLoanTermsRequired,
                    "A loan account requires loan terms (principal, rate, term, and payment frequency)."),
            AccountsRepository.CreateAccountFailure.LoanTermsInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountLoanTermsInvalid,
                    "Loan terms are incomplete or invalid."),
            AccountsRepository.CreateAccountFailure.LoanTermsNotAllowed =>
                BusinessError.Problem(BusinessError.Codes.AccountLoanTermsNotAllowed,
                    "Only loan accounts may carry loan terms."),
            _ => Results.Problem("Unknown create-account result.", statusCode: 500),
        };
    }

    /// <summary>The detail of <c>account-currency-invalid</c> on create and edit: the code
    /// is not one an account can take (ADR-0114 D15).</summary>
    private const string AccountCurrencyInvalidDetail =
        "Currency must be an ISO 4217 currency code with at most 3 decimal places.";

    /// <summary>A category has no currency of its own (ADR-0109 D9).</summary>
    private static IResult CategoryCurrencyProblem(string reporting) =>
        BusinessError.Problem(BusinessError.Codes.CategoryCurrencyNotReporting,
            $"A category has no currency of its own: its lines are in the currency of the "
            + $"account that paid them, and it is reported in the ledger's reporting currency, "
            + $"{reporting}. Leave the currency out.");

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/accounts/loan-payment-preview</c> —
    /// ADR-0050 slice 3. Stateless amortization preview so the editor's Loan
    /// Terms block can show the estimated payment as the user types. The C#
    /// <see cref="LoanAmortization"/> service is the single source of truth (no
    /// duplicated math in the SPA). Reads no ledger data; ledger-scoped only for
    /// auth. An incomplete/invalid term set yields a zero preview.
    /// </summary>
    private static async Task<IResult> LoanPaymentPreviewAsync(
        Guid ledgerId,
        LoanPaymentPreviewRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // The payment rounds up to one minor unit of the loan's currency (ADR-0114 D15), so
        // the preview needs the editor's currency and never assumes one. A three-letter code
        // the table does not accept takes 2 places, as a stored code from before the rule
        // keeps (CurrencyMinorUnits.PlacesOf), so a grandfathered account still previews.
        if (!Quotes.CurrencyCodes.TryNormalize(request.CurrencyCode, out var currencyCode))
            return BusinessError.Problem(BusinessError.Codes.AccountCurrencyInvalid,
                "currencyCode is required: the loan's payment is rounded at its currency's places.");

        // The periodic payment is balance-independent (fixed over the loan life),
        // so the preview needs only the form fields — no current balance.
        var periodic = request.PaymentIsComputed
            ? LoanAmortization.PeriodicPayment(
                request.OriginalPrincipal, request.AnnualInterestRate,
                request.PaymentCount, request.PaymentsPerYear, currencyCode)
            : (request.FixedPayment is { } fp && fp > 0m ? fp : 0m);
        var escrow = request.EscrowAmount > 0m ? request.EscrowAmount : 0m;
        return Results.Ok(new LoanPaymentPreviewResponse(periodic, escrow, periodic + escrow));
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}</c> — ADR-0050.
    /// Edit an account's general attributes (name, currency, institution,
    /// active, category kind, opening balance, opened-on, and loan terms).
    /// <c>account_type</c> is immutable. System accounts reject with
    /// <c>account-is-system</c>. Answers 200 with <see cref="UpdateAccountResponse"/>: a
    /// currency change relabels (ADR-0114 D13, D15, <see cref="RelabelCheck"/>) and says how
    /// many mirrored pairs now carry two amounts.
    /// </summary>
    private static async Task<IResult> UpdateAsync(
        Guid ledgerId,
        Guid accountId,
        UpdateAccountRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        Quotes.MarketSeries.MarketSeriesBackfill backfill,
        Db.AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // ADR-0114 D11, one rule, both asked before the edit: an edit that moves the account
        // into a currency no other account of the ledger is kept in (its own holdings sibling
        // aside), and that is not the reporting currency, backfills the rates it now needs;
        // an edit of a non-category account's opened date to before the ledger's earliest day,
        // in any currency, moves every net-worth pair's start to it, and a funded brokerage's
        // to before the first funding, or an opening balance that makes it funded from its
        // opened date, moves the benchmarks' start; the starts are read again after the edit.
        var dated = request.OpenedOn is not null || request.OpeningBalance is not null;
        var was = request.CurrencyCode is not null || dated
            ? await accounts.CurrencyOfAsync(ledgerId, accountId, cancellationToken).ConfigureAwait(false)
            : null;
        var isAccount = was is { } held && held.AccountType != "category";
        var newCurrency = false;
        if (isAccount
            && request.CurrencyCode is not null
            && Quotes.CurrencyCodes.TryNormalize(request.CurrencyCode, out var to)
            && !string.Equals(was!.Value.CurrencyCode, to, StringComparison.Ordinal)
            && !string.Equals(to, visible.CurrencyCode, StringComparison.Ordinal))
        {
            Guid[] self = was.Value.HoldingsAccountId is { } sibling ? [accountId, sibling] : [accountId];
            newCurrency = !await accounts.CurrencyHeldByAnotherAccountAsync(ledgerId, to, self, cancellationToken)
                .ConfigureAwait(false);
        }
        NeededStarts? before = !newCurrency && isAccount && dated
            ? await NeededFxPairs.StartsAsync(db, ledgerId, cancellationToken).ConfigureAwait(false)
            : null;

        var outcome = await accounts.UpdateAsync(ledgerId, accountId, request, cancellationToken).ConfigureAwait(false);
        if (outcome.Result == AccountsRepository.UpdateAccountResult.Ok)
        {
            if (newCurrency) backfill.Start(ledgerId, currentUser.UserId);
            else if (before is { } read)
                await backfill.StartIfEarlierAsync(db, ledgerId, currentUser.UserId, read, cancellationToken)
                    .ConfigureAwait(false);
        }
        return outcome.Result switch
        {
            // A currency change relabels (ADR-0114 D13): the answer says how many mirrored
            // pairs now carry two amounts, zero for any other edit.
            AccountsRepository.UpdateAccountResult.Ok => Results.Ok(new UpdateAccountResponse(outcome.BecameTwoAmount)),
            AccountsRepository.UpdateAccountResult.RelabelRefused => RelabelCheck.Refusal(outcome.Relabel!)!,
            AccountsRepository.UpdateAccountResult.NotInLedger =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "Account does not belong to this ledger."),
            AccountsRepository.UpdateAccountResult.IsSystem =>
                BusinessError.Problem(BusinessError.Codes.AccountIsSystem,
                    "System accounts cannot be edited."),
            AccountsRepository.UpdateAccountResult.PatchEmpty =>
                BusinessError.Problem(BusinessError.Codes.AccountPatchEmpty,
                    "No editable fields supplied."),
            AccountsRepository.UpdateAccountResult.NameRequired =>
                BusinessError.Problem(BusinessError.Codes.AccountNameRequired,
                    "Account name cannot be blank."),
            AccountsRepository.UpdateAccountResult.CategoryKindInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountCategoryKindInvalid,
                    "Category kind must be 'income', 'expense' or 'adjustment', and only on categories."),
            AccountsRepository.UpdateAccountResult.CategoryKindImmutable =>
                BusinessError.Problem(BusinessError.Codes.CategoryKindImmutable,
                    "A category's kind cannot be changed — it would move every posting in it "
                    + "between the spending and income totals, for all of its history. Create a "
                    + "category of the kind you want and merge this one into it instead; the merge "
                    + "says what moves."),
            AccountsRepository.UpdateAccountResult.CurrencyInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountCurrencyInvalid, AccountCurrencyInvalidDetail),
            AccountsRepository.UpdateAccountResult.AmountPlacesExceedCurrency =>
                MoneyPlaces.Refusal(outcome.Unfit!.Value.CurrencyCode, outcome.Unfit.Value.Field),
            AccountsRepository.UpdateAccountResult.CategoryCurrencyNotReporting =>
                CategoryCurrencyProblem(visible.CurrencyCode),
            AccountsRepository.UpdateAccountResult.OpeningBalanceInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountOpeningBalanceInvalid,
                    "Categories must have a zero opening balance."),
            AccountsRepository.UpdateAccountResult.LoanTermsInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountLoanTermsInvalid,
                    "Loan terms are incomplete or invalid."),
            AccountsRepository.UpdateAccountResult.LoanTermsNotAllowed =>
                BusinessError.Problem(BusinessError.Codes.AccountLoanTermsNotAllowed,
                    "Only loan accounts may carry loan terms."),
            AccountsRepository.UpdateAccountResult.TaxStatusInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountTaxStatusInvalid,
                    "Tax status must be 'taxable', 'tax_deferred', 'tax_free', or 'other'."),
            _ => Results.Problem("Unknown update-account result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/accounts/{accountId}/payment-reminder</c> —
    /// set up the managed payment reminder for a loan account (ADR-0050 ext). The
    /// principal/interest/escrow split is derived from the loan terms + balance;
    /// the cadence from the loan's payments-per-year. The payment is drawn from
    /// the supplied bank-shape source account (the loan is a counterparty leg).
    /// Read side: the account detail's <c>managedReminder</c>.
    /// </summary>
    private static async Task<IResult> SetupPaymentReminderAsync(
        Guid ledgerId,
        Guid accountId,
        SetupPaymentReminderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        RemindersRepository reminders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // The payment is drawn from a bank-shape account — the loan itself is a
        // counterparty leg, never the source.
        if (!await accounts.IsBankShapeInLedgerAsync(ledgerId, request.SourceAccountId, cancellationToken)
                .ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.PaymentReminderSourceInvalid,
                "The paying account must be a bank-type account in this ledger.");

        var outcome = await reminders.CreateManagedLoanReminderAsync(
            ledgerId, accountId, request.SourceAccountId, request.StartDate, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Result switch
        {
            RemindersRepository.ManagedLoanReminderResult.Ok =>
                Results.Ok(new { reminderId = outcome.ReminderId }),
            RemindersRepository.ManagedLoanReminderResult.LoanTermsMissing =>
                BusinessError.Problem(BusinessError.Codes.PaymentReminderTermsMissing,
                    "This loan needs complete loan terms (including interest + escrow accounts) "
                    + "before a scheduled payment can be set up."),
            RemindersRepository.ManagedLoanReminderResult.AlreadyExists =>
                BusinessError.Problem(BusinessError.Codes.PaymentReminderExists,
                    "This loan already has a scheduled payment."),
            RemindersRepository.ManagedLoanReminderResult.CurrencyMismatch =>
                RemindersEndpoints.LoanAcrossCurrenciesProblem(outcome.Currencies!),
            _ => Results.Problem("Unknown setup-payment-reminder result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/trade-commission</c>
    /// — slice A4.a. Flip the per-brokerage <c>is_trade_commission</c>
    /// flag. The repository recomputes the brokerage's holdings in the
    /// same transaction, which takes the ledger's write lock first, so the
    /// response returns with the holdings + lots already converged and a
    /// failed recompute leaves the flag unchanged.
    ///
    /// 422 cases: <c>ledger-not-visible</c>, <c>account-not-in-ledger</c>,
    /// <c>account-not-investment</c> (CHECK constraint refuses TRUE on
    /// non-investment accounts; we surface it as a friendly 422 rather
    /// than letting the DB throw).
    /// </summary>
    private static async Task<IResult> PatchTradeCommissionAsync(
        Guid ledgerId,
        Guid accountId,
        PatchAccountTradeCommissionRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        if (!await accounts.BelongsToLedgerAsync(ledgerId, accountId, cancellationToken)
                           .ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        var result = await accounts.SetIsTradeCommissionAsync(
            ledgerId, accountId, request.Enabled, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            AccountsRepository.SetIsTradeCommissionResult.Ok =>
                Results.NoContent(),
            AccountsRepository.SetIsTradeCommissionResult.AccountNotInvestment =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInvestment,
                    "Treat-fees-as-commission is only meaningful on investment accounts."),
            _ => Results.Problem("Unknown trade-commission flip result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/active</c>
    /// — inactive-accounts slice. Flips the per-account
    /// <c>is_active</c> flag. Server doesn't refuse a deactivation
    /// just because the account still has positions or balance —
    /// the SPA owns that confirm-dialog flow (locked decision in
    /// follow-ups.md). System accounts (Holdings siblings,
    /// Uncategorized) reject as not-user-deactivatable.
    ///
    /// 422 cases: <c>ledger-not-visible</c>, <c>account-not-in-ledger</c>,
    /// <c>account-is-system</c>.
    /// </summary>
    private static async Task<IResult> PatchActiveAsync(
        Guid ledgerId,
        Guid accountId,
        PatchAccountActiveRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await accounts.SetIsActiveAsync(
            ledgerId, accountId, request.Active, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            AccountsRepository.SetIsActiveResult.Ok =>
                Results.NoContent(),
            AccountsRepository.SetIsActiveResult.AccountNotInLedger =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "Account does not belong to this ledger."),
            AccountsRepository.SetIsActiveResult.AccountIsSystem =>
                BusinessError.Problem(BusinessError.Codes.AccountIsSystem,
                    "System accounts cannot be deactivated."),
            _ => Results.Problem("Unknown is-active flip result.", statusCode: 500),
        };
    }
}
