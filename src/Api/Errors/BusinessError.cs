using Microsoft.AspNetCore.Http;

namespace Coffer.Api.Errors;

/// <summary>
/// Helpers for the 422 + <c>code</c> business-error envelope. The
/// convention (see <c>docs/engineering-standards.md §5.3</c>): every
/// business-rule rejection is HTTP 422 Unprocessable Entity with a
/// stable <c>code</c> string in the ProblemDetails extensions, plus a
/// human-readable <c>detail</c>. Clients dispatch on <c>code</c>; the
/// HTTP status only distinguishes business rejection from
/// transport/auth failures (4xx-vs-401-vs-5xx).
/// </summary>
public static class BusinessError
{
    /// <summary>
    /// <c>HttpContext.Items</c> key under which a returned business error stamps
    /// its stable <c>code</c> as the result executes, so
    /// <c>RequestAccessLogMiddleware</c> can append the business outcome to the
    /// access line without re-parsing the response body. Absent when a request
    /// did not end in a business rejection.
    /// </summary>
    public const string CodeItemKey = "coffer.business_error_code";

    /// <summary>
    /// Build a 422 ProblemDetails response with a stable <c>code</c>
    /// and a human-readable <c>detail</c>. <paramref name="title"/>
    /// defaults to a generic phrase; override per call when a more
    /// specific title helps log readability.
    /// </summary>
    public static IResult Problem(string code, string detail, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        var problem = Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: title ?? "Unprocessable request",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

        return new CodeTaggedResult(code, problem);
    }

    /// <summary>
    /// Wraps the ProblemDetails result so the business <c>code</c> is recorded in
    /// <see cref="HttpContext.Items"/> the moment the result runs — inside the
    /// request pipeline, before the access-log middleware's <c>finally</c> reads
    /// it. Keeps the outcome-logging concern out of every endpoint and off the
    /// response-body parsing path.
    /// </summary>
    private sealed class CodeTaggedResult(string code, IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Items[CodeItemKey] = code;
            return inner.ExecuteAsync(httpContext);
        }
    }

    /// <summary>
    /// Stable error codes referenced by both endpoint code and the test
    /// suite. Add new codes here so the catalogue is one searchable file
    /// instead of scattered string literals. Codes are kebab-case to
    /// match the URL/JSON ergonomics; clients dispatch on these strings.
    /// </summary>
    public static class Codes
    {
        // Ledgers
        public const string LedgerNameRequired = "ledger-name-required";
        public const string LedgerNotVisible   = "ledger-not-visible";
        public const string LedgerNotOwner     = "ledger-not-owner";
        public const string LedgerNotWritable  = "ledger-not-writable";
        public const string MemberNotFound     = "member-not-found";
        public const string MemberInvalidRole  = "member-invalid-role";
        public const string MemberLastOwner    = "member-last-owner";
        public const string MemberSystemUser   = "member-system-user";
        public const string UserNotFound       = "user-not-found";
        public const string UserLastAdmin      = "user-last-admin";
        // Invites (ADR-0083 slice B)
        public const string InviteInvalid        = "invite-invalid";
        public const string InviteScopeInvalid   = "invite-scope-invalid";
        public const string InviteRoleInvalid    = "invite-role-invalid";
        public const string InviteUsernameTaken  = "invite-username-taken";
        public const string InviteFieldRequired  = "invite-field-required";
        public const string InviteAttestationFailed = "invite-attestation-failed";
        public const string InviteNotFound        = "invite-not-found";

        // MCP access tokens (ADR-0063)
        public const string McpTokenNameRequired = "mcp-token-name-required";
        public const string McpTokenNotFound      = "mcp-token-not-found";

        // Moneydance UI import (ADR-0071 D2)
        public const string ImportInvalid        = "import-invalid";
        public const string ImportFileRequired   = "import-file-required";
        public const string ImportParseFailed    = "import-parse-failed";
        public const string ImportAlreadyRunning = "import-already-running";

        // Preferences (ADR-0057)
        public const string QuoteProviderUnknown = "quote-provider-unknown";

        // Schedules (mig 136 — quote-refresh / snapshot)
        public const string ScheduleInvalid = "schedule-invalid";
        public const string ScheduleJobTypeUnknown = "schedule-job-type-unknown";

        // Accounts / register
        public const string AccountNotInLedger          = "account-not-in-ledger";
        // Inactive-accounts slice: system accounts (holdings siblings,
        // Uncategorized) reject deactivation.
        public const string AccountIsSystem             = "account-is-system";
        // Inactive-accounts gate (PR #132 follow-up): new transactions
        // and re-targeting PATCHes cannot land on accounts that have
        // been deactivated. Editing other fields on existing legs
        // whose account is already inactive is still allowed
        // (historical preservation).
        public const string AccountInactive             = "account-inactive";
        // Holdings / Portfolio View (slice A1 — investments)
        public const string AccountNotInvestment       = "account-not-investment";
        public const string AccountMissingHoldingsSibling = "account-missing-holdings-sibling";
        // ADR-0050: account create/edit validation.
        public const string AccountNameRequired         = "account-name-required";
        public const string AccountTypeInvalid          = "account-type-invalid";
        public const string AccountCategoryKindInvalid  = "account-category-kind-invalid";
        public const string AccountCurrencyInvalid      = "account-currency-invalid";
        public const string AccountParentInvalid        = "account-parent-invalid";
        public const string AccountPatchEmpty           = "account-patch-empty";
        // ADR-0050 slice 3: opening balance + loan-terms editing.
        public const string AccountOpeningBalanceInvalid = "account-opening-balance-invalid";
        public const string AccountLoanTermsRequired     = "account-loan-terms-required";
        public const string AccountLoanTermsInvalid      = "account-loan-terms-invalid";
        public const string AccountLoanTermsNotAllowed   = "account-loan-terms-not-allowed";
        public const string AccountTaxStatusInvalid      = "account-tax-status-invalid";
        // Managed loan-payment reminder (ADR-0050 ext / mig 168).
        public const string PaymentReminderExists         = "payment-reminder-exists";
        public const string PaymentReminderTermsMissing   = "payment-reminder-terms-missing";
        public const string PaymentReminderSourceInvalid  = "payment-reminder-source-invalid";
        // Categories management (Slice A) — REST over the ADR-0068 repo methods.
        public const string AccountNotACategory         = "account-not-a-category";
        public const string CategoryKindMismatch        = "category-kind-mismatch";
        public const string CategoryCycle               = "category-cycle";
        public const string CategoryInUse               = "category-in-use";
        public const string CategoryMergeSelf           = "category-merge-self";
        public const string RegisterLimitInvalid        = "register-limit-invalid";
        public const string TransactionNotInLedger      = "transaction-not-in-ledger";
        public const string TransactionPatchEmpty       = "transaction-patch-empty";
        public const string TransactionLegNotInHeader   = "transaction-leg-not-in-header";
        public const string TransactionAccountRequired  = "transaction-account-required";
        public const string TransactionPairSelf         = "transaction-pair-self";
        public const string TransactionPostedAtRequired = "transaction-posted-at-required";
        public const string TransactionReconStatusInvalid = "transaction-recon-status-invalid";
        public const string RegisterDirectionInvalid       = "register-direction-invalid";
        public const string RegisterStatusFilterInvalid    = "register-status-filter-invalid";
        public const string RegisterSortInvalid            = "register-sort-invalid";

        // Tags (slice 2c.6b — first-class in PATCH)
        public const string TransactionTagEmpty         = "transaction-tag-empty";
        public const string TransactionTagTooLong       = "transaction-tag-too-long";
        public const string TransactionTagsTooMany      = "transaction-tags-too-many";

        // Tags management (v1) — dictionary CRUD (rename / recolor / merge / delete).
        public const string TagNotFound                 = "tag-not-found";
        public const string TagNameExists               = "tag-name-exists";
        public const string TagMergeSelf                = "tag-merge-self";
        public const string TagColorInvalid             = "tag-color-invalid";

        // Merge (slice 2c.6d — mergeFromHeaderId on PATCH)
        public const string MergeSourceInvalid          = "merge-source-invalid";

        // Ledger snapshots (ADR-0037)
        public const string SnapshotManualAtCap         = "snapshot-manual-at-cap";
        public const string SnapshotNotFound            = "snapshot-not-found";
        public const string SnapshotSchemaVersionMismatch = "snapshot-schema-version-mismatch";
        public const string SnapshotPayloadCorrupt      = "snapshot-payload-corrupt";

        // Postings reshape (ADR-0025)
        public const string TransactionPostingsEmpty             = "transaction-postings-empty";
        public const string TransactionPostingSelf               = "transaction-posting-self";
        public const string TransactionPostingCounterpartyRequired = "transaction-posting-counterparty-required";
        public const string TransactionPostingLegNotInHeader     = "transaction-posting-leg-not-in-header";
        public const string TransactionSourceAccountMismatch     = "transaction-source-account-mismatch";

        // Move to account (ADR-0072 D3)
        public const string TransactionMoveTargetInvalid     = "transaction-move-target-invalid";
        public const string TransactionMoveTargetSameAsSource = "transaction-move-target-same-as-source";
        public const string TransactionMoveSplitToInvestment = "transaction-move-split-to-investment";
        public const string TransactionMoveCollision         = "transaction-move-collision";
        public const string TransactionMoveSourceRequired    = "transaction-move-source-required";

        // Investment transactions (A4.c / ADR-0029)
        public const string InvestmentTxnActionInvalid       = "investment-txn-action-invalid";
        public const string InvestmentTxnAccountNotInvestment = "investment-txn-account-not-investment";
        public const string InvestmentTxnSecurityRequired    = "investment-txn-security-required";
        public const string InvestmentTxnSharesRequired      = "investment-txn-shares-required";
        public const string InvestmentTxnPriceRequired       = "investment-txn-price-required";
        public const string InvestmentTxnAmountRequired      = "investment-txn-amount-required";
        public const string InvestmentTxnCategoryRequired    = "investment-txn-category-required";
        public const string InvestmentTxnTransferRequired    = "investment-txn-transfer-required";
        public const string InvestmentTxnFeeAmountRequired   = "investment-txn-fee-amount-required";
        public const string InvestmentTxnFeeWithoutAccount   = "investment-txn-fee-without-account";
        public const string InvestmentTxnSharesNonZero       = "investment-txn-shares-nonzero";
        public const string InvestmentTxnPricePositive       = "investment-txn-price-positive";
        public const string InvestmentTxnFeeAmountPositive   = "investment-txn-fee-amount-positive";
        public const string InvestmentTxnSecurityNotInLedger = "investment-txn-security-not-in-ledger";
        public const string InvestmentTxnInsufficientShares  = "investment-txn-insufficient-shares";
        // Inactive-account gate per role (PR #132 follow-up).
        public const string InvestmentTxnBrokerageInactive  = "investment-txn-brokerage-inactive";
        public const string InvestmentTxnCategoryInactive   = "investment-txn-category-inactive";
        public const string InvestmentTxnTransferInactive   = "investment-txn-transfer-inactive";
        public const string InvestmentTxnFeeAccountInactive = "investment-txn-fee-account-inactive";
        // transfer_shares (in-kind, ADR-0065).
        public const string InvestmentTxnTransferSharesQtyPositive                 = "investment-txn-transfer-shares-qty-positive";
        public const string InvestmentTxnTransferSharesToSelf                      = "investment-txn-transfer-shares-to-self";
        public const string InvestmentTxnTransferSharesDestNotInvestment           = "investment-txn-transfer-shares-dest-not-investment";
        public const string InvestmentTxnTransferSharesDestMissingHoldingsSibling  = "investment-txn-transfer-shares-dest-missing-holdings-sibling";
        public const string InvestmentTxnTransferSharesInsufficient                = "investment-txn-transfer-shares-insufficient";
        // In-kind transfer scrub (ADR-0065 D4).
        public const string InKindTransferSellNotFound   = "in-kind-transfer-sell-not-found";
        public const string InKindTransferBuyNotFound    = "in-kind-transfer-buy-not-found";
        public const string InKindTransferNotAValidPair  = "in-kind-transfer-not-a-valid-pair";

        // Reminders / recurring transactions (ADR-0047)
        public const string ReminderNotInLedger     = "reminder-not-in-ledger";
        public const string ReminderNotMaterialized = "reminder-not-materialized";

        // Reminders mutation surface (ADR-0047 slice — manual authoring).
        public const string ReminderRruleInvalid           = "reminder-rrule-invalid";
        public const string ReminderStartDateRequired      = "reminder-start-date-required";
        public const string ReminderEndBeforeStart         = "reminder-end-before-start";
        public const string ReminderAutoCommitNegative     = "reminder-auto-commit-negative";
        public const string ReminderPatchEmpty             = "reminder-patch-empty";
        public const string ReminderOccurrenceAlreadyFired = "reminder-occurrence-already-fired";
        public const string ReminderOccurrenceSkipped      = "reminder-occurrence-skipped";
        // Cross-shape protection: a bank edit on an investment series (or vice
        // versa) is structurally wrong, like the live /transactions split.
        public const string ReminderShapeMismatch          = "reminder-shape-mismatch";

        // Cross-topic protection (ADR-0029): bank `/transactions` rejects
        // investment txns and vice versa. Each endpoint owns its shape;
        // mixing them is structurally wrong, not just a validation error.
        public const string TransactionAccountIsInvestment   = "transaction-account-is-investment";
        public const string TransactionHeaderIsInvestment    = "transaction-header-is-investment";
        public const string InvestmentTxnHeaderNotInvestment = "investment-txn-header-not-investment";

        // Account groups (sidebar tabs, migration 033)
        public const string AccountGroupNameRequired = "account-group-name-required";
        public const string AccountGroupNameConflict = "account-group-name-conflict";
        public const string AccountGroupNotFound     = "account-group-not-found";

        // Securities (slice A3)
        public const string SecurityNotInLedger        = "security-not-in-ledger";
        public const string SecurityNameRequired       = "security-name-required";
        public const string SecurityAssetClassInvalid  = "security-asset-class-invalid";
        public const string SecurityComponentsInvalid  = "security-components-invalid";
        public const string SecurityDuplicateTicker    = "security-duplicate-ticker";
        public const string SecurityDuplicateCusip     = "security-duplicate-cusip";
        public const string SecurityQuoteSymbolRequired = "security-quote-symbol-required";

        // Security prices (slice A3 follow-on)
        public const string SecurityPriceNotInSecurity = "security-price-not-in-security";
        public const string SecurityPriceRequired      = "security-price-required";
        public const string SecurityPriceDateRequired  = "security-price-date-required";
        public const string SecurityPriceDateConflict  = "security-price-date-conflict";
        public const string SecurityPriceHighLowInvalid = "security-price-high-low-invalid";

        // Feed connections (Phase 5 / SimpleFIN slice 1)
        public const string FeedConnectionSetupTokenRequired = "feed-connection-setup-token-required";
        public const string FeedConnectionSetupTokenInvalid  = "feed-connection-setup-token-invalid";
        public const string FeedConnectionNotFound           = "feed-connection-not-found";
        public const string FeedConnectionAccessUrlMissing   = "feed-connection-access-url-missing";
        public const string FeedConnectionAccessUrlCorrupted = "feed-connection-access-url-corrupted";
        public const string FeedMappingTargetRequired        = "feed-mapping-target-required";
        public const string FeedMappingConnectionMismatch    = "feed-mapping-connection-mismatch";
        public const string SyncRunNotInLedger               = "sync-run-not-in-ledger";
        public const string FeedSyncInProgress               = "feed-sync-in-progress";
        public const string AccountNotBoundToFeed            = "account-not-bound-to-feed";
        public const string SyncFromDateInFuture             = "sync-from-date-in-future";

        // Bulk selection
        public const string SelectionKindInvalid       = "selection-kind-invalid";
        public const string SelectionStatusFilterInvalid = "selection-status-filter-invalid";
        public const string SelectionExcludeTooLarge   = "selection-exclude-too-large";
        public const string SelectionEmpty             = "selection-empty";

        // Setup ceremony
        public const string SetupUsernameRequired      = "setup-username-required";
        public const string SetupDisplayNameRequired   = "setup-display-name-required";
        public const string SetupUsernameTaken         = "setup-username-taken";
        public const string SetupNicknameRequired      = "setup-nickname-required";
        public const string SetupAttestationRequired   = "setup-attestation-required";
        public const string SetupAttestationFailed     = "setup-attestation-failed";
        public const string SetupBootstrapConsumed     = "setup-bootstrap-consumed";
        // (setup-ledger-choice-required / -conflict / -not-found / -name-required
        //  retired in ADR-0088: setup no longer picks a ledger.)

        // Login ceremony
        public const string LoginUsernameRequired   = "login-username-required";
        public const string LoginAssertionRequired  = "login-assertion-required";
        public const string RecoveryCodeRequired    = "recovery-code-required";

        // Account self-service: passkey management + recovery codes
        // (ADR-0013 follow-through — the /api/auth/register/* surface the
        // setup ceremony always referenced).
        public const string RegisterNicknameRequired    = "register-nickname-required";
        public const string RegisterAttestationRequired = "register-attestation-required";
        public const string RegisterAttestationFailed   = "register-attestation-failed";
        public const string CredentialNotFound          = "credential-not-found";
        public const string CredentialLastRemaining     = "credential-last-remaining";

        // Whole-DB backups (ADR-0060, admin surface)
        public const string BackupPassphraseNotSet  = "backup-passphrase-not-set";
        public const string BackupPassphraseInvalid = "backup-passphrase-invalid";
        public const string BackupFailed            = "backup-failed";
        public const string BackupRetentionInvalid  = "backup-retention-invalid";
        // Bootstrap restore (ADR-0061): malformed upload (missing file/passphrase).
        public const string BackupRestoreInvalid    = "backup-restore-invalid";
        // Authenticated-admin restore (ADR-0071 D3/D4).
        public const string BackupRestoreConfirmRequired = "backup-restore-confirm-required";
        public const string BackupKekMismatch            = "backup-kek-mismatch";
        // Adopt-the-source-key path on restore (ADR-0092 D4).
        public const string BackupSourceKeyInvalid       = "backup-source-key-invalid";

        // Master-KEK reveal (ADR-0092 D2, admin surface). The reveal requires a
        // fresh passkey assertion on top of the admin session, so a malformed or
        // failed ceremony is a distinct code from the login one.
        public const string MasterKeyAssertionRequired = "master-key-assertion-required";
        public const string MasterKeyNoCredentials     = "master-key-no-credentials";
        public const string MasterKeyRotateBlocked    = "master-key-rotate-blocked";

        // Google Drive backup sync (ADR-0062, admin surface)
        public const string DriveClientRequired   = "drive-client-required";
        public const string DriveNotConnected     = "drive-not-connected";
        public const string DriveConnectFailed    = "drive-connect-failed";
    }
}
