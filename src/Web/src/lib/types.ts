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
    BalanceHealthDriftDto,
    BalanceHealthReport,
    LedgerSummary,
} from './types/ledger';
export type {
    AccountSummary,
    AccountDetail,
    CreateAccountRequest,
    UpdateAccountRequest,
    LoanTermsInput,
    LoanPaymentPreviewRequest,
    LoanPaymentPreviewResponse,
    AccountGroupSummary,
    CreateAccountGroupRequest,
    PatchAccountGroupRequest,
    PatchAccountSyncFromDateRequest,
    PatchAccountFeedMappingRequest,
    FrequentCounterparty,
    FrequentCounterpartiesResponse,
    ManagedReminder,
    SetupPaymentReminderRequest,
} from './types/account';
export type {
    CategoryNode,
    ReparentCategoryRequest,
    MergeCategoryRequest,
    MergeCategoryResponse,
} from './types/category';
export type {
    TagDto,
    PatchTagRequest,
    MergeTagRequest,
    MergeTagResponse,
    CleanupTagsResponse,
} from './types/tag';
export type {
    FeedConnectionSummary,
    CreateFeedConnectionRequest,
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
    RegisterRowBase,
    BankRow,
    InvestmentRow,
    RegisterRow,
    RegisterEntry,
    RegisterPage,
    SetReconStatusRequest,
    DeleteTransactionResponse,
} from './types/register';
export type {
    TransactionPosting,
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
    OverviewAccount,
    OverviewAccountGroup,
    PortfolioRollup,
} from './types/overview';
export type {
    QuoteProvider,
    QuotesPrefs,
    DashboardPrefs,
    DashboardWidgetPref,
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
export type { BackupKekCheck, BackupRetention, BackupSummary, BackupSchedule } from './types/backup';
export type { DriveSyncStatus, DriveConnectStart } from './types/driveSync';
export type {
    MasterKeyStatus,
    MasterKeyReveal,
    MasterKeyRotation,
} from './types/masterKey';
export type {
    ReminderSummary,
    UpcomingOccurrence,
    UpcomingKind,
    ReminderKind,
    ReminderLegDto,
    ReminderDetail,
    SetReminderActiveRequest,
    SkipReminderRequest,
    SkipReminderResponse,
    FireReminderRequest,
    FireReminderResponse,
    CreateReminderRequest,
    CreateInvestmentReminderRequest,
    PatchReminderPostings,
    EditReminderRequest,
    EditInvestmentReminderRequest,
} from './types/reminder';
