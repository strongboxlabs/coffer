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
    public static IResult Problem(string code, string detail, string? title = null) =>
        Problem(code, detail, title, extra: null);

    /// <summary><see cref="Problem(string, string, string?)"/> with further extension
    /// members beside <c>code</c>, for a refusal whose reason is data (the missing rate a
    /// write needed, ADR-0114 D9).</summary>
    public static IResult Problem(
        string code, string detail, string? title, IReadOnlyDictionary<string, object?>? extra)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (extra is not null)
            foreach (var (key, value) in extra) extensions[key] = value;
        var problem = Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: title ?? "Unprocessable request",
            detail: detail,
            extensions: extensions);

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
        public string Code => code;

        public string? Detail => (inner as Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)?.ProblemDetails.Detail;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Items[CodeItemKey] = code;
            return inner.ExecuteAsync(httpContext);
        }
    }

    /// <summary>
    /// A refusal a shared validator returned, in words, for a caller that is not a request:
    /// its <c>code</c> and <c>detail</c>; the <c>error</c> text of a plain 400 that carries
    /// one; or the result's type for anything else.
    /// </summary>
    /// <remarks>An internal writer that runs an endpoint's own checks (the Demo's examples
    /// phase) reports what was refused with this, rather than executing the result.</remarks>
    internal static string Describe(IResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result is CodeTaggedResult tagged)
            return tagged.Detail is { } detail ? $"{tagged.Code}: {detail}" : tagged.Code;
        if (result is IValueHttpResult { Value: { } value }
            && System.Text.Json.JsonSerializer.SerializeToElement(value) is { ValueKind: System.Text.Json.JsonValueKind.Object } body
            && body.TryGetProperty("error", out var error)
            && error.ValueKind == System.Text.Json.JsonValueKind.String)
            return error.GetString()!;
        return result.GetType().Name;
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
        /// <summary>Another write to the same ledger held its write lock (migration 275)
        /// longer than this write waits for it. Nothing of the refused write was written,
        /// except that a consistency repair keeps the pieces it committed before the refusal
        /// and says so in its own detail; a retry once that write has committed succeeds.
        /// Raised from anywhere a ledger write takes the lock, and answered by
        /// <c>LedgerWriteBusyExceptionHandler</c>, by the repair endpoint
        /// (<c>BalancesEndpoints</c>) and by the sync endpoint mapping
        /// (<c>SyncEndpointMapping</c>).</summary>
        public const string LedgerWriteBusy    = "ledger-write-busy";
        public const string LedgerNameRequired = "ledger-name-required";
        /// <summary>A ledger's reporting currency (create-ledger, the ledger PATCH, the
        /// Moneydance import) is not one a ledger can take: an ISO 4217 currency whose
        /// exponent is at most 3 places, the rule an account's currency is held to
        /// (ADR-0114 D7, D15). A code that is not on ISO 4217 List One, an "N.A." entry, CLF
        /// and UYW are refused. A PATCH that resends the ledger's own stored code is not
        /// refused, so a code stored before the rule keeps working at 2 places.</summary>
        public const string LedgerCurrencyInvalid = "ledger-currency-invalid";
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
        // OAuth grants a user ends on Account → Security (FU-087)
        public const string OAuthGrantNotFound    = "oauth-grant-not-found";
        // A connection an admin renames or revokes on System → MCP
        public const string McpConnectionNotFound = "mcp-connection-not-found";
        // A user's own sessions (FU-087)
        public const string SessionNotFound       = "session-not-found";

        // Moneydance UI import (ADR-0071 D2)
        public const string ImportInvalid        = "import-invalid";
        public const string ImportFileRequired   = "import-file-required";
        public const string ImportParseFailed    = "import-parse-failed";
        public const string ImportAlreadyRunning = "import-already-running";
        public const string ImportTooLarge       = "import-too-large";
        /// <summary>The export's currencies cannot be imported (ADR-0114 D5, D15): it marks
        /// no single base currency; an account's own currency is missing, names no currency
        /// of the file, or has no 3-letter code; its code is not one an account can take (not
        /// on ISO 4217 List One, an "N.A." entry, CLF or UYW); the file keeps more decimal
        /// places for it than Coffer keeps for that code, or none; a loan's escrow or fixed
        /// payment carries more places than the loan's currency; a category that pays another
        /// category is kept at more places than the reporting currency the import was given,
        /// else the file's base; or a security's relative
        /// currency cannot be read. The detail names each account or security, and both
        /// place counts where places are the reason. Raised by the preview and the start
        /// alike; nothing is defaulted to USD or to a number of places.</summary>
        public const string ImportCurrencyUnresolved = "import-currency-unresolved";

        // The hub's Demo ledger (ADR-0115 D2)
        /// <summary>The <c>today</c> the Demo would end on is more than a day after the
        /// server's UTC date. No time zone's local date is further ahead than that, so the
        /// sending computer's clock is wrong, and a Demo ending on it would carry rows the
        /// register calls Scheduled.</summary>
        public const string DemoDateInFuture = "demo-date-in-future";

        // Preferences (ADR-0057)
        public const string QuoteProviderUnknown = "quote-provider-unknown";

        // Benchmarks (ADR-0114 D1, D2). An add the Yahoo check refuses answers with the
        // check's own code: benchmark-is-index, benchmark-instrument-unsupported,
        // benchmark-no-adjusted-close, currency-missing, currency-unknown,
        // ticker-not-resolved, rate-limited, fetch-failed or parse-failed.
        /// <summary>The symbol is blank, too long, or has a character no Yahoo symbol has.</summary>
        public const string BenchmarkSymbolInvalid = "benchmark-symbol-invalid";
        /// <summary>The adding user has not turned Yahoo on for this ledger: the add-time
        /// check is egress and needs their consent (ADR-0114 D2, D8).</summary>
        public const string BenchmarkYahooOff = "benchmark-yahoo-off";
        public const string BenchmarkAlreadyAdded = "benchmark-already-added";
        /// <summary>The ledger already has its three benchmarks (ADR-0114 D1).</summary>
        public const string BenchmarkLimitReached = "benchmark-limit-reached";
        public const string BenchmarkNotFound = "benchmark-not-found";
        /// <summary>The MCP <c>returns_series</c> named a symbol that is not one of the ledger's
        /// benchmarks (ADR-0114 D3). REST answers the same request with the
        /// <c>benchmark_not_in_ledger</c> reason instead, so a saved report keeps its pick.</summary>
        public const string BenchmarkNotInLedger = "benchmark-not-in-ledger";

        // Exchange rates (ADR-0114 D10)
        /// <summary>A currency of a hand-entered rate is not a currency code, or both are the same.</summary>
        public const string FxRateCurrencyInvalid = "fx-rate-currency-invalid";
        /// <summary>The pair is not one the ledger needs (the rates view lists those).</summary>
        public const string FxPairNotNeeded = "fx-pair-not-needed";
        /// <summary>The rate is not positive, or does not fit NUMERIC(28,12) once rounded.</summary>
        public const string FxRateInvalid = "fx-rate-invalid";
        /// <summary>The day is not a <c>yyyy-MM-dd</c> date.</summary>
        public const string FxRateDateInvalid = "fx-rate-date-invalid";
        /// <summary>The day is after the server's UTC today: no rate is stored for a day
        /// that has not happened, typed or fetched, so a current value never converts at a
        /// rate someone guessed ahead.</summary>
        public const string FxRateDateInFuture = "fx-rate-date-in-future";
        /// <summary>A rate is already stored for the pair on that day, fetched or typed:
        /// the first stored wins and is never rewritten (ADR-0114 D6, D10).</summary>
        public const string FxRateDayTaken = "fx-rate-day-taken";

        /// <summary>A dashboard layout PUT is not a version-2 document within the
        /// structural bounds: wrong types, a missing or blank field, a value that is
        /// not a boolean, number, string, null or string array, or over the length or
        /// depth cap.</summary>
        public const string DashboardPrefsInvalid = "dashboard-prefs-invalid";

        /// <summary>A dashboard layout PUT named an <c>updatedAt</c> that is not the
        /// stored row's (or null while a row exists): another save came first. Nothing
        /// was written; read again.</summary>
        public const string DashboardPrefsStale = "dashboard-prefs-stale";

        /// <summary>A budget-progress request named a month or window the
        /// endpoint cannot honour (unparseable month, window outside 1..24).</summary>
        public const string BudgetRangeInvalid = "budget-range-invalid";

        /// <summary>A target was refused because an ANCESTOR of that category already
        /// holds one for the month (ADR-0099 D1a). The detail names the ancestor.</summary>
        public const string BudgetTargetAncestorConflict = "budget-target-ancestor-conflict";

        /// <summary>The mirror: a DESCENDANT already holds a target for the month.
        /// The detail names the descendant.</summary>
        public const string BudgetTargetDescendantConflict = "budget-target-descendant-conflict";

        // Overview (ADR-0056)
        /// <summary>The overview's <c>since</c> is not a month-end strictly before
        /// <c>today</c>, or was sent without <c>today</c>.</summary>
        public const string OverviewSinceInvalid = "overview-since-invalid";

        // Schedules (mig 136 — quote-refresh / snapshot)
        public const string ScheduleInvalid = "schedule-invalid";
        public const string ScheduleJobTypeUnknown = "schedule-job-type-unknown";

        /// <summary>A consistency repair named a projection the report does not cover.</summary>
        public const string ConsistencyProjectionUnknown = "consistency-projection-unknown";
        /// <summary>A consistency repair was sent while another repair of the same ledger
        /// runs (<c>ConsistencyRepairLock</c>); it rewrites nothing.</summary>
        public const string ConsistencyRepairInProgress = "consistency-repair-in-progress";
        /// <summary>A quote_currency repair was sent by a user whose quotes preference does not
        /// enable Yahoo for the ledger: the repair asks Yahoo for each security's currency, which
        /// is egress (ADR-0114 D5, amendment; ADR-0057). It rewrites nothing.</summary>
        public const string ConsistencyRepairNeedsYahoo = "consistency-repair-needs-yahoo";
        /// <summary>A notification target named a provider this build does not have.</summary>
        public const string NotificationProviderUnknown = "notification-provider-unknown";
        public const string NotificationUrlRefused      = "notification-url-refused";

        // Accounts / register
        public const string AccountNotInLedger          = "account-not-in-ledger";
        // Inactive-accounts slice: system accounts (holdings siblings,
        // Uncategorized) reject deactivation.
        public const string AccountIsSystem             = "account-is-system";
        // Inactive-accounts gate: new transactions
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
        /// <summary>An account's currency on create or the account PATCH is not one an
        /// account can take: an ISO 4217 currency whose exponent is at most 3 places
        /// (ADR-0114 D15). A code that is not on ISO 4217 List One, an "N.A." entry, CLF and
        /// UYW are refused. A PATCH that resends the account's own stored code is not
        /// refused, so a code stored before the rule keeps working at 2 places. The loan
        /// payment preview refuses only a code that is missing or not three characters
        /// once trimmed; any
        /// other code previews at its places, 2 for a code stored before the rule. A
        /// category keeps its own rule (<see cref="CategoryCurrencyNotReporting"/>).</summary>
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
        /// <summary>A managed loan payment would cross currencies (ADR-0114 D13): the paying
        /// account, or a real escrow account, is in another currency than the loan's, and
        /// interest and escrow are computed in the loan's. Refused at setup, and at fire
        /// time for a series an earlier currency edit left across currencies, so it never
        /// posts. A category escrow is not checked: it mirrors the real side. Enter such a
        /// payment as an ordinary transfer with both amounts.</summary>
        public const string PaymentReminderCurrencyMismatch = "payment-reminder-currency-mismatch";
        // Categories management (Slice A) — REST over the ADR-0068 repo methods.
        public const string AccountNotACategory         = "account-not-a-category";

        /// <summary>A request tried to CHANGE an existing category's kind. Refused
        /// outright (ADR-0017): the flip would retroactively move every posting in
        /// that category between the Spending and Income measures, silently and with
        /// no record. Reclassify by merging into a category of the wanted kind
        /// instead, which states what moves.</summary>
        public const string CategoryKindImmutable      = "category-kind-immutable";

        /// <summary>A category create or edit named a currency other than the ledger's
        /// reporting currency. A category has no currency of its own (ADR-0109 D9): its
        /// lines carry the paying account's, and its row holds the reporting currency.</summary>
        public const string CategoryCurrencyNotReporting = "category-currency-not-reporting";
        public const string CategoryKindMismatch        = "category-kind-mismatch";
        public const string CategoryCycle               = "category-cycle";
        public const string CategoryInUse               = "category-in-use";
        public const string CategoryMergeSelf           = "category-merge-self";
        /// <summary>A write that needs an exchange rate the ledger does not have
        /// (ADR-0114 D9); nothing is written. A category merge whose two budget targets for
        /// a month are in different currencies: the response's <c>rateUnavailable</c> is one
        /// <see cref="Contracts.RateUnavailable"/>, the pair and the day. A share move between
        /// brokerages in different currencies with a lot that has no derived rate for its
        /// acquisition day (ADR-0114 D13): <c>rateUnavailable</c> is a list of them, each
        /// missing stored leg in the direction it is stored (account currency into the
        /// reporting currency) with its day.</summary>
        public const string RateUnavailable             = "rate-unavailable";

        /// <summary>A typed amount carries more places than its currency allows
        /// (ADR-0114 D15): a whole-unit currency takes none, most take 2, a few take 3. The
        /// response names the currency (<c>currencyCode</c>), its places (<c>places</c>) and
        /// the request field (<c>field</c>, its wire name); nothing is written. Only a value
        /// the write changes is checked: one resent equal to its stored value passes even
        /// when it does not fit, as stored amounts that do not fit are kept. A reminder's
        /// fire is a new write, so a template figure that does not fit is refused when it is
        /// posted, by hand or by auto-post. A figure the API computes is rounded at its
        /// currency's places instead, never refused. Built by <c>MoneyPlaces</c>.</summary>
        public const string AmountPlacesExceedCurrency  = "amount-places-exceed-currency";
        public const string RegisterLimitInvalid        = "register-limit-invalid";
        public const string TransactionNotInLedger      = "transaction-not-in-ledger";
        public const string TransactionPatchEmpty       = "transaction-patch-empty";
        public const string TransactionLegNotInHeader   = "transaction-leg-not-in-header";
        public const string TransactionAccountRequired  = "transaction-account-required";
        public const string TransactionPairSelf         = "transaction-pair-self";
        public const string TransactionPostedAtRequired = "transaction-posted-at-required";
        /// <summary>A PATCH named <c>postedAt</c> or <c>transactedAt</c> and set
        /// it to null. Since migration 230 a header field the body carries is
        /// ASSIGNED, null included — which is how payee / memo / check number
        /// get cleared — but both date columns are NOT NULL, so there is no
        /// cleared state to move to. Rejected rather than ignored: silently
        /// dropping a field the caller named is the failure that change
        /// removed.</summary>
        public const string TransactionDateNull         = "transaction-date-null";
        public const string TransactionReconStatusInvalid = "transaction-recon-status-invalid";
        public const string RegisterDirectionInvalid       = "register-direction-invalid";
        public const string RegisterStatusFilterInvalid    = "register-status-filter-invalid";
        public const string RegisterSortInvalid            = "register-sort-invalid";
        // The ledger-wide entries' cash-flow direction (all / inflow / outflow), which is
        // not the register's paging direction (before / after) above.
        public const string TransactionEntriesDirectionInvalid = "transaction-entries-direction-invalid";

        // Tags (slice 2c.6b — first-class in PATCH)
        public const string TransactionTagEmpty         = "transaction-tag-empty";
        public const string TransactionTagTooLong       = "transaction-tag-too-long";
        public const string TransactionTagsTooMany      = "transaction-tags-too-many";

        // Memo and tags per side. The write primitive raises these as
        // AnnotationViolationCodes; the strings are the same by test.
        public const string ReadOnlyRow                 = "read-only-row";
        public const string SideNotOnTransaction        = "side-not-on-transaction";
        public const string LineNotesOnOnePosting       = "line-notes-on-one-posting";
        /// <summary>A line of <c>notes.lines</c> names no line of the account the
        /// transaction is split from, names one twice, or addresses an investment
        /// event, whose memo and tags are the whole event's.</summary>
        public const string LineNotesInvalid            = "line-notes-invalid";

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
        public const string TransactionPostingLegIdDuplicated    = "transaction-posting-leg-id-duplicated";
        public const string TransactionSourceAccountMismatch     = "transaction-source-account-mismatch";

        // Transfers between currencies (ADR-0114 D13): a posting between two real accounts
        // in different currencies carries both amounts, each in its own account's
        // currency. A posting with a category, or between accounts in one currency, carries
        // one amount, and a second one sent on it is dropped. Raised by the bank create, the
        // PATCH reshape, and a bank reminder's create, edit and fire; nothing is written.
        // The nonzero and sign rules, like the places rule, check only a pair the write
        // changes: a pair resent equal to its two stored legs, on the same accounts, passes
        // both, so a mirrored zero pair that a move or a currency edit left across
        // currencies stays editable (PostingLegPair.CheckSecondAmount).
        /// <summary>The posting is between real accounts in different currencies and sends
        /// no <c>counterpartyAmount</c>.</summary>
        public const string TransactionPostingCounterpartyAmountRequired = "transaction-posting-counterparty-amount-required";
        /// <summary>The posting is between real accounts in different currencies and its
        /// <c>amount</c> or its <c>counterpartyAmount</c> is zero: money that leaves one
        /// account arrives in the other, so neither side can be nothing. Not raised for a
        /// pair resent equal to its two stored legs on the same accounts.</summary>
        public const string TransactionPostingTwoAmountsNonzero = "transaction-posting-two-amounts-nonzero";
        /// <summary>The posting is between real accounts in different currencies and its
        /// two amounts have the same sign: what leaves one account arrives in the other,
        /// so the signs are opposite. Not raised for a pair resent equal to its two stored
        /// legs on the same accounts.</summary>
        public const string TransactionPostingTwoAmountsSign = "transaction-posting-two-amounts-sign";

        // Move to account (ADR-0072 D3)
        public const string TransactionMoveTargetInvalid     = "transaction-move-target-invalid";
        public const string TransactionMoveTargetSameAsSource = "transaction-move-target-same-as-source";
        /// <summary>A move that would put a category leg on a money account,
        /// or a money leg on a category. Banking and investment accounts ARE
        /// interchangeable for a cash row (ADR-0072 D3).</summary>
        public const string TransactionMoveDomainCrossed     = "transaction-move-domain-crossed";
        public const string TransactionMoveSplitToInvestment = "transaction-move-split-to-investment";
        /// <summary>The reverse: a split leaving an investment account for a
        /// bank. Only a single cash leg may cross, in either direction.</summary>
        public const string TransactionMoveSplitFromInvestment = "transaction-move-split-from-investment";
        public const string TransactionMoveCollision         = "transaction-move-collision";
        public const string TransactionMoveSourceRequired    = "transaction-move-source-required";

        // Relabel (ADR-0114 D13, D15): moving a transaction into an account in another
        // currency, and changing an account's currency, keep every stored figure and read
        // it in the new currency. Raised by the move, the bulk move (which refuses the whole
        // selection) and the account PATCH; nothing is written.
        /// <summary>A two-amount pair would end up in one currency with magnitudes that
        /// differ, which one currency cannot represent. <c>count</c> is how many such pairs
        /// the write would leave. <c>mergedInto</c> is present when any of them is on a
        /// transaction merged into another, which no register shows: each entry a
        /// <c>RelabelMergedInto</c>, the transaction it was merged into (ADR-0114 D13,
        /// decided by the owner, 2026-11-18), followed to the end of a chain of merges, so
        /// the row a register shows (settled by Claude, 2026-11-18); absent when every pair
        /// is on a transaction a register shows.</summary>
        public const string RelabelPairNotRepresentable = "relabel-pair-not-representable";
        /// <summary>Stored figures carry more places than the new currency allows
        /// (<c>MoneyPlaces.RelabelRefusal</c> builds it). <c>count</c> is how many
        /// transactions have a leg that does not fit, on the account or its Holdings
        /// sibling (a category's mirror of that leg is not counted again, and a transaction
        /// merged into another is left out, ADR-0114 D15).
        /// <c>accountFigures</c> names each account-level figure that does not fit, as an
        /// <c>UnfitAmountKinds</c> value, <c>opening_balance</c> or <c>loan_terms</c>,
        /// empty when none does; <c>count</c> is zero when only those do.
        /// <c>currencyCode</c> and <c>places</c> name the new currency and its
        /// places.</summary>
        public const string RelabelPlacesExceedCurrency = "relabel-places-exceed-currency";

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
        // A transfer-shape action (LedgerActions.Transfer, BuyXfr, SellXfr and DivXfr, on
        // the wire transfer, buyx, sellx and divx) between the brokerage and an account in another currency carries transferAmount,
        // the other account's leg in its own currency (ADR-0114 D13, ADR-0029). Sent on a
        // category or a same-currency transfer it is dropped. Raised by the create, the
        // PATCH, and an investment reminder's create, edit and fire; nothing is written.
        // The nonzero and sign rules check only a pair the write changes: a transfer pair
        // resent equal to its two stored legs, on the same accounts, passes both
        // (PostingLegPair.CheckSecondAmount).
        /// <summary>The transfer is between currencies and sends no <c>transferAmount</c>.</summary>
        public const string InvestmentTxnTransferAmountRequired = "investment-txn-transfer-amount-required";
        /// <summary>The transfer is between currencies and its brokerage-side amount or its
        /// <c>transferAmount</c> is zero. Not raised for a pair resent equal to its two
        /// stored legs on the same accounts.</summary>
        public const string InvestmentTxnTransferAmountsNonzero = "investment-txn-transfer-amounts-nonzero";
        /// <summary>The transfer is between currencies and <c>transferAmount</c> has the
        /// same sign as the brokerage's leg of the transfer pair. Not raised for a pair
        /// resent equal to its two stored legs on the same accounts.</summary>
        public const string InvestmentTxnTransferAmountsSign    = "investment-txn-transfer-amounts-sign";
        public const string InvestmentTxnSharesNonZero       = "investment-txn-shares-nonzero";
        public const string InvestmentTxnPricePositive       = "investment-txn-price-positive";
        public const string InvestmentTxnFeeAmountPositive   = "investment-txn-fee-amount-positive";
        public const string InvestmentTxnSecurityNotInLedger = "investment-txn-security-not-in-ledger";
        /// <summary>A create or edit would leave a sale taking more shares than
        /// the account held at that point, or deepen a shortfall already there
        /// (migration 256). Declared long before anything returned it; until
        /// then an oversell was silently accepted, took the holding negative and
        /// booked the unmatched shares at zero cost.</summary>
        public const string InvestmentTxnInsufficientShares  = "investment-txn-insufficient-shares";
        // Inactive-account gate per role.
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

        /// <summary>
        /// The reminder's template has no lines, so posting it would name no account to
        /// post from.
        /// </summary>
        public const string ReminderTemplateHasNoLines = "reminder-template-has-no-lines";

        // Reminders mutation surface (ADR-0047 slice — manual authoring).
        public const string ReminderRruleInvalid           = "reminder-rrule-invalid";
        public const string ReminderStartDateRequired      = "reminder-start-date-required";
        public const string ReminderEndBeforeStart         = "reminder-end-before-start";
        public const string ReminderAutoCommitNegative     = "reminder-auto-commit-negative";

        /// <summary>estimateSampleCount outside 1-24 (mig 220).</summary>
        public const string ReminderEstimateRangeInvalid    = "reminder-estimate-range-invalid";

        /// <summary>
        /// An estimate was asked for on a series that cannot have one — a split, or a
        /// loan reminder.
        /// </summary>
        /// <remarks>
        /// Rejected rather than silently dropped: a user who ticks it and gets no error
        /// believes the amount is estimated when it never will be — a silent-nothing
        /// failure.
        /// </remarks>
        public const string ReminderEstimateNotEligible     = "reminder-estimate-not-eligible";
        public const string ReminderPatchEmpty             = "reminder-patch-empty";
        public const string ReminderOccurrenceAlreadyFired = "reminder-occurrence-already-fired";
        public const string ReminderOccurrenceSkipped      = "reminder-occurrence-skipped";
        // Cross-shape protection: a bank edit on an investment series (or vice
        // versa) is structurally wrong, like the live /transactions split.
        public const string ReminderShapeMismatch          = "reminder-shape-mismatch";

        /// <summary>
        /// A reminder edit names a source account (a bank reminder's source, an
        /// investment reminder's brokerage) other than the series' stored one. The source
        /// is set when the reminder is created and an edit cannot change it; nothing is
        /// written.
        /// </summary>
        public const string ReminderSourceImmutable        = "reminder-source-immutable";

        // Cross-topic protection (ADR-0029): bank `/transactions` rejects
        // investment txns and vice versa. Each endpoint owns its shape;
        // mixing them is structurally wrong, not just a validation error.
        public const string TransactionAccountIsInvestment   = "transaction-account-is-investment";
        public const string TransactionHeaderIsInvestment    = "transaction-header-is-investment";
        public const string InvestmentTxnHeaderNotInvestment = "investment-txn-header-not-investment";

        // Account folders (sidebar, mig 258, FU-006)
        public const string AccountFolderNameRequired = "account-folder-name-required";
        public const string AccountFolderNameTooLong  = "account-folder-name-too-long";
        /// <summary>A sibling folder (same parent, same type) already has that name,
        /// ignoring case. Also refuses a delete whose contents would move up beside
        /// a folder of the same name.</summary>
        public const string AccountFolderNameTaken    = "account-folder-name-taken";
        public const string AccountFolderNotFound     = "account-folder-not-found";
        public const string AccountFolderTypeInvalid  = "account-folder-type-invalid";
        /// <summary>The folder (or the parent folder) is of another account type than
        /// the account or folder being put in it.</summary>
        public const string AccountFolderTypeMismatch = "account-folder-type-mismatch";
        /// <summary>The move would put a folder inside itself or one of its own
        /// sub-folders.</summary>
        public const string AccountFolderCycle        = "account-folder-cycle";
        /// <summary>A concurrent change to the same folders got in the way of a move or
        /// a delete. Nothing was changed; reload and try again.</summary>
        public const string AccountFolderChanged      = "account-folder-changed";
        /// <summary>A category was named where only a real account is accepted (the
        /// mirror of <see cref="AccountNotACategory"/>).</summary>
        public const string AccountIsCategory         = "account-is-category";

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
        /// <summary>A price's currency is not a currency code: three characters once trimmed,
        /// the rule an account's currency is held to (ADR-0114 D5).</summary>
        public const string SecurityPriceCurrencyInvalid = "security-price-currency-invalid";
        /// <summary>A price was added without a currency, and none can be inferred: the
        /// security has no price yet and its holding accounts are none or disagree
        /// (ADR-0114 D5).</summary>
        public const string SecurityPriceCurrencyRequired = "security-price-currency-required";

        // Feed connections (Phase 5 / SimpleFIN slice 1)
        public const string FeedConnectionSetupTokenRequired = "feed-connection-setup-token-required";
        public const string FeedConnectionSetupTokenInvalid  = "feed-connection-setup-token-invalid";
        public const string FeedConnectionNotFound           = "feed-connection-not-found";
        public const string FeedConnectionAccessUrlMissing   = "feed-connection-access-url-missing";
        public const string FeedConnectionAccessUrlCorrupted = "feed-connection-access-url-corrupted";
        /// <summary>A connection's new name is blank once trimmed.</summary>
        public const string FeedConnectionNameRequired       = "feed-connection-name-required";
        /// <summary>A connection's new name is longer than 80 characters once trimmed.</summary>
        public const string FeedConnectionNameTooLong        = "feed-connection-name-too-long";
        public const string FeedMappingTargetRequired        = "feed-mapping-target-required";
        public const string FeedMappingConnectionMismatch    = "feed-mapping-connection-mismatch";
        /// <summary>A feed account's currency differs from the Coffer account's
        /// (ADR-0114 D13): a binding is refused with it (422), and an account bound before
        /// the check fails its pull with it as the sync error's code, the message naming
        /// both currencies. A feed account that reports no currency is not checked.</summary>
        public const string FeedAccountCurrencyMismatch      = "feed-account-currency-mismatch";
        /// <summary>An OFX or QFX statement's currency (<c>CURDEF</c>) differs from the
        /// target account's (ADR-0114 D13). Refused before the import run opens, so nothing
        /// is written. Snake case, as the file-ingest family's other codes
        /// (<c>{prefix}_parse_failed</c>) are.</summary>
        public const string OfxStatementCurrencyMismatch     = "ofx_statement_currency_mismatch";
        public const string SyncRunNotInLedger               = "sync-run-not-in-ledger";
        public const string FeedSyncInProgress               = "feed-sync-in-progress";
        /// <summary>The bank/provider faulted — unreachable, non-2xx, or malformed
        /// payload. Distinct from needs_reauth, which is actionable by reconnecting;
        /// this one usually resolves itself.</summary>
        public const string FeedSyncProviderFault            = "feed-sync-provider-fault";
        public const string AccountNotBoundToFeed            = "account-not-bound-to-feed";
        public const string SyncFromDateInFuture             = "sync-from-date-in-future";

        // Bulk selection
        public const string SelectionKindInvalid       = "selection-kind-invalid";
        public const string SelectionStatusFilterInvalid = "selection-status-filter-invalid";
        public const string SelectionExcludeTooLarge   = "selection-exclude-too-large";
        public const string SelectionEmpty             = "selection-empty";
        /// <summary>An explicit selection names a transaction the register shows
        /// read-only; the bulk action is refused whole.</summary>
        public const string SelectionIncludesReadOnly  = "selection-includes-read-only";

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
        // A backup is already running; a second would race its dump and its pruning (FU-067).
        public const string BackupInProgress        = "backup-in-progress";
        public const string BackupRetentionInvalid  = "backup-retention-invalid";
        // In-app upgrade (ADR-0110)
        public const string UpgradeUnavailable      = "upgrade-unavailable";
        public const string UpgradeInProgress       = "upgrade-in-progress";
        public const string UpgradePhraseWrong      = "upgrade-phrase-wrong";
        public const string UpgradeNothingNewer     = "upgrade-nothing-newer";
        public const string UpgradeTokenInvalid     = "upgrade-token-invalid";
        // Bootstrap restore (ADR-0061): malformed upload (missing file/passphrase).
        public const string BackupRestoreInvalid    = "backup-restore-invalid";
        // The service role may not yet create databases, or does not own the live
        // one, so a restore-by-swap cannot run (migration 246). Refused before staging.
        public const string BackupRestoreNotProvisioned = "backup-restore-not-provisioned";
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

        // Step-up for sensitive actions (ADR-0105 D1). The action needs a
        // fresh passkey tap; the client starts one at /api/auth/step-up/begin and
        // sends the result in the X-Coffer-Step-Up header.
        public const string StepUpRequired          = "step-up-required";
        public const string StepUpUnknownAction     = "step-up-unknown-action";
        public const string StepUpNoEligiblePasskey = "step-up-no-eligible-passkey";
        public const string StepUpNotVerified       = "step-up-user-not-verified";

        // Google Drive backup sync (ADR-0062, admin surface)
        public const string DriveClientRequired   = "drive-client-required";
        public const string DriveNotConnected     = "drive-not-connected";
        public const string DriveConnectFailed    = "drive-connect-failed";
    }
}
