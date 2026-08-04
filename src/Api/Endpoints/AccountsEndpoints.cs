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
        group.MapGet("/{accountId:guid}", GetAccountAsync);
        group.MapPatch("/{accountId:guid}", UpdateAsync);
        group.MapPatch("/{accountId:guid}/feed-mapping", PatchFeedMappingAsync);
        // Slice 2c.4: clear the feed binding (unmap) — paired with
        // the PATCH bind, completes the mapping lifecycle the
        // unified accounts panel drives.
        group.MapDelete("/{accountId:guid}/feed-mapping", DeleteFeedMappingAsync);
        // Slice 2c.3: per-account sync — narrows the SimpleFIN
        // call to one account on its bound connection.
        group.MapPost("/{accountId:guid}/sync", SyncAccountAsync);
        // Slice 2c.5: user-resettable per-account sync watermark.
        // PATCH with a date sets the watermark; PATCH with null
        // clears it ("backfill 90 days on next sync").
        group.MapPatch("/{accountId:guid}/sync-from-date", PatchSyncFromDateAsync);
        // Slice A1: Portfolio View read surface for investment accounts.
        group.MapGet("/{accountId:guid}/holdings", GetHoldingsAsync);
        // ADR-0043: most-used counterparties for this account, to float
        // to the top of the account/category picker.
        group.MapGet("/{accountId:guid}/frequent-counterparties", GetFrequentCounterpartiesAsync);
        // Slice A4.a: per-brokerage "treat in-transaction fees as cost
        // basis" toggle. Flips `accounts.is_trade_commission` + invokes
        // `recompute_holdings_cost_basis(ledger_id)` in the same
        // transaction so the holdings.cost_basis + lots.unit_cost
        // converge before the response returns.
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
            ledgerId, accountId, cancellationToken).ConfigureAwait(false);
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
            ledgerId, accountId, FrequentPerKind, DateTime.UtcNow, cancellationToken)
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

        var result = await accounts.BindFeedMappingAsync(
            ledgerId, accountId,
            request.FeedConnectionId, request.SimpleFinAccountId.Trim(),
            cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AccountsRepository.BindFeedMappingResult.Ok => Results.NoContent(),
            AccountsRepository.BindFeedMappingResult.ConnectionMismatch =>
                BusinessError.Problem(BusinessError.Codes.FeedMappingConnectionMismatch,
                    "Feed connection does not belong to this ledger."),
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await accounts.CreateAsync(ledgerId, request, cancellationToken).ConfigureAwait(false);
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
                    "A category requires kind 'income' or 'expense'; other account types must not set a category kind."),
            AccountsRepository.CreateAccountFailure.CurrencyInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountCurrencyInvalid,
                    "Currency must be a 3-letter ISO code."),
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

        // The periodic payment is balance-independent (fixed over the loan life),
        // so the preview needs only the form fields — no current balance.
        var periodic = request.PaymentIsComputed
            ? LoanAmortization.PeriodicPayment(
                request.OriginalPrincipal, request.AnnualInterestRate,
                request.PaymentCount, request.PaymentsPerYear)
            : (request.FixedPayment is { } fp && fp > 0m ? fp : 0m);
        var escrow = request.EscrowAmount > 0m ? request.EscrowAmount : 0m;
        return Results.Ok(new LoanPaymentPreviewResponse(periodic, escrow, periodic + escrow));
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}</c> — ADR-0050.
    /// Edit an account's general attributes (name, currency, institution,
    /// active, category kind, opening balance, opened-on, and loan terms).
    /// <c>account_type</c> is immutable. System accounts reject with
    /// <c>account-is-system</c>.
    /// </summary>
    private static async Task<IResult> UpdateAsync(
        Guid ledgerId,
        Guid accountId,
        UpdateAccountRequest request,
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

        var result = await accounts.UpdateAsync(ledgerId, accountId, request, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AccountsRepository.UpdateAccountResult.Ok => Results.NoContent(),
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
                    "Category kind must be 'income' or 'expense', and only on categories."),
            AccountsRepository.UpdateAccountResult.CurrencyInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountCurrencyInvalid,
                    "Currency must be a 3-letter ISO code."),
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
            _ => Results.Problem("Unknown setup-payment-reminder result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/trade-commission</c>
    /// — slice A4.a. Flip the per-brokerage <c>is_trade_commission</c>
    /// flag. The repository invokes
    /// <c>recompute_holdings_cost_basis(ledgerId)</c> in the same
    /// transaction so the response returns with the holdings + lots
    /// already converged.
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
