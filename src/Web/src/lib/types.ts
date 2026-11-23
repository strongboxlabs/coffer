// Barrel re-export for the lib/types/ split.
//
// Types are partitioned by business domain (auth / ledger /
// account / feed / register / bank / investment / security /
// holding / selection / payee) per ADR-0030. The `register.ts`
// file holds the universal read surface (the RegisterRow
// discriminated union — RegisterRowBase / BankRow / InvestmentRow,
// RegisterEntry, RegisterPage) plus universal mutations
// (SetReconStatusRequest, DeleteTransactionResponse) shared by
// every domain; `bank.ts` and `investment.ts` hold per-domain
// editor request bodies. Adding a Loan or Asset domain is a new
// file + one line here — no churn at call sites.
//
// Existing imports `from '@/lib/types'` or `from '../lib/types'`
// continue to work via this re-export.

export type { CurrentUser } from './types/auth';
export type { ApiVersion, DbVersion, VersionResponse } from './types/meta';
export type {
    LedgerConsistencyReport,
    LedgerConsistencyState,
    ProjectionConsistency,
    ProjectionRepairResult,
    ConsistencyMismatch,
    LedgerSummary,
    UnbackedPriceAdvisory,
    UpdateLedgerRequest,
} from './types/ledger';
export type { RateUnavailable } from './types/conversion';
export type {
    AddFxRateRequest,
    Benchmark,
    BenchmarkAvailability,
    BenchmarkUnavailableReason,
    FxPair,
    FxMissingSpan,
    FxPairNeed,
    FxPairSource,
    FxRate,
    FxRatePage,
    FxRateSource,
    FxRatesView,
} from './types/marketSeries';
export type {
    AccountSummary,
    AccountDetail,
    CreateAccountRequest,
    UpdateAccountRequest,
    LoanTermsInput,
    LoanPaymentPreviewRequest,
    LoanPaymentPreviewResponse,
    AccountFolder,
    CreateAccountFolderRequest,
    RenameAccountFolderRequest,
    AccountBalance,
    AccountSubtotals,
    AccountTypeSubtotal,
    AccountFolderSubtotal,
    BalanceForecast,
    BalanceForecastAccount,
    BalanceForecastCombined,
    BalanceForecastExclusion,
    BalanceForecastLowest,
    BalanceForecastPoint,
    PatchAccountSyncFromDateRequest,
    PatchAccountFeedMappingRequest,
    FrequentCounterparty,
    FrequentCounterpartiesResponse,
    ManagedReminder,
    SetupPaymentReminderRequest,
    RelabelMergedInto,
} from './types/account';
export type {
    CategoryList,
    CategoryNode,
    ReparentCategoryRequest,
    MergeCategoryRequest,
    MergeCategoryResponse,
} from './types/category';
export type {
    TagDto,
    CreateTagRequest,
    TagTransactionLine,
    TagTransactionPage,
    TagTransactionFilter,
    PatchTagRequest,
    MergeTagRequest,
    MergeTagResponse,
    CleanupTagsResponse,
} from './types/tag';
export type {
    FeedConnectionSummary,
    CreateFeedConnectionRequest,
    RenameFeedConnectionRequest,
    FeedConnectionAccountDto,
    SyncErrorDto,
    SyncResultDto,
    SyncAllConnectionEntry,
    SyncAllResultDto,
    SyncRunSummary,
    SyncRunPromotionDto,
    SyncRunDetail,
} from './types/feed';
export type {
    ReconStatus,
    NotesKind,
    RegisterRowNotes,
    RegisterRowBase,
    BankRow,
    InvestmentRow,
    RegisterRow,
    RegisterEntry,
    RegisterPage,
    LedgerEntry,
    LedgerEntriesPage,
    SetReconStatusRequest,
    DeleteTransactionResponse,
} from './types/register';
export type {
    TransactionPosting,
    TransactionNotesRequest,
    TransactionLineNotesRequest,
    CreateTransactionRequest,
    PatchTransactionPostings,
    PatchTransactionRequest,
    SimilarPayeeDto,
    MergeCandidateDto,
    MergeCandidatePostingDto,
} from './types/bank';
export type {
    LedgerInvestmentAction,
    CreateInvestmentTransactionRequest,
    PatchInvestmentTransactionRequest,
    CreateInvestmentTransactionResponse,
    InvestmentLotDto,
    InvestmentMergeCandidate,
} from './types/investment';
export type {
    SecuritySummary,
    SecurityDetail,
    SecurityPricePoint,
    CreateSecurityRequest,
    PatchSecurityRequest,
    SecurityTransaction,
    SecurityTransactionsPage,
    SecurityPriceRow,
    SecurityPricesPage,
    CreateSecurityPriceRequest,
    PatchSecurityPriceRequest,
    SecurityComponent,
} from './types/security';
export { SECURITY_ASSET_CLASSES } from './types/security';
export type {
    HoldingsViewDto,
    PortfolioSummaryDto,
    PositionDto,
} from './types/holding';
export type { UndoImportResult } from './types/imports';
export type { CsvAmountShapeName, CsvMappingShape, CsvDecimalSeparatorName, CsvDelimiterName, CsvMapping, CsvMappingProblem, CsvMappingVerdict } from './types/csvMapping';
export type {
    SelectionStatusFilter,
    SelectionRequest,
    SelectionSummary,
    BulkReconStatusResponse,
    BulkDeleteResponse,
    BulkUnhideResponse,
    BulkMoveAccountResponse,
} from './types/selection';
export type { PayeeSuggestion } from './types/payee';
export type { QuoteRunOutcome, QuoteError } from './types/quote';
export type { LedgerOperationSummary } from './types/ledgerOperation';
export type {
    LedgerOverview,
    NetWorthHistory,
    NetWorthHistoryPoint,
    OverviewAccount,
    OverviewAccountGroup,
    OverviewComparison,
    PortfolioRollup,
} from './types/overview';
export type {
    AllocationDimension,
    AllocationResult,
    AllocationRow,
    ReturnsResult,
    UndecomposedMultiAsset,
} from './types/portfolio';
export type {
    BudgetCategoryRow,
    BudgetDailyCell,
    BudgetProgress,
    BudgetTransactionLine,
    BudgetTransactionPage,
} from './types/budget';
export type {
    QuoteProvider,
    QuotesPrefs,
    DashboardPrefsV1,
    DashboardPrefsV2,
    DashboardSettingValue,
    DashboardWidgetPrefV2,
    StoredDashboardPrefs,
} from './types/preference';
export type { Schedule } from './types/schedule';
export type {
    OfxPreviewAccount,
    OfxPreviewResponse,
    OfxImportResponse,
    OfxIngestError,
} from './types/ofx';
export type {
    QifPreviewAccount,
    QifPreviewResponse,
    QifImportResponse,
    QifIngestError,
} from './types/qif';
export type {
    SnapshotSummary,
    CreateSnapshotRequest,
    CreateSnapshotResponse,
} from './types/snapshot';
export type { BackupKekCheck, BackupRetention, BackupRun, BackupRunAccepted, BackupSummary, BackupSchedule } from './types/backup';
export type { UpgradeAttempt, UpgradeHost, UpgradeRelease, UpgradeSettings, UpgradeStatus } from './types/upgrade';
export type { DriveSyncStatus, DriveConnectStart } from './types/driveSync';
export type {
    MasterKeyStatus,
    MasterKeyReveal,
    MasterKeyRotation,
} from './types/masterKey';
export type {
    ReminderSummary,
    UpcomingOccurrence,
    UpcomingAutoPost,
    AutoPostExcludedReason,
    UpcomingKind,
    ReminderKind,
    ReminderLegDto,
    ReminderDetail,
    SetReminderActiveRequest,
    SkipReminderRequest,
    SkipReminderResponse,
    FireReminderResponse,
    CreateReminderRequest,
    CreateInvestmentReminderRequest,
    PatchReminderPostings,
    EditReminderRequest,
    EditInvestmentReminderRequest,
} from './types/reminder';
export type {
    NotificationProvider,
    NotificationSubscriber,
    NotificationSubscribersResponse,
    SystemEvent,
    LedgerNotificationSettings,
    LedgerAttention,
    OutstandingProblem,
    SystemAttention,
    SystemAttentionSubject,
} from './types/notification';
