// Barrel re-export for the lib/api/ split.
//
// Endpoint helpers are partitioned by business domain
// (auth / ledger / account / feed / register / bank / investment /
// security / holding / selection / payee) per ADR-0030. The thin
// fetch wrapper (`request` + `ApiError`) lives in
// `./api/_request.ts`. `api/register.ts` holds the universal
// register read endpoint plus universal mutations
// (setReconStatus, deleteTransaction). `api/bank.ts` and (future)
// `api/investment.ts` hold per-domain editor endpoints.
//
// Existing imports `from '@/lib/api'` or `from '../lib/api'`
// continue to work via this re-export.

export { ApiError, request } from './api/_request';
export { fetchCurrentUser } from './api/auth';
export { fetchVersion } from './api/meta';
export { fetchVisibleLedgers, checkLedgerConsistency, fetchConsistencyState, repairProjection, createLedger, renameLedger, updateLedger, deleteLedger } from './api/ledger';
export { startDemoLedger, fetchDemoJob } from './api/demo';
export type { DemoJob, DemoJobPhase } from './api/demo';
export { fetchLedgerMembers, setLedgerMemberRole, removeLedgerMember } from './api/member';
export type { LedgerMember } from './types/member';
export { fetchAdminUsers, setUserDisabled, setUserAdmin, signUserOutEverywhere } from './api/adminUsers';
export type { SignOutEverywhereResult } from './api/adminUsers';
export type { AdminUser } from './types/adminUser';
export {
    fetchLedgerInvites,
    createLedgerInvite,
    revokeLedgerInvite,
    fetchAdminInvites,
    createAdminInvite,
    revokeAdminInvite,
} from './api/invite';
export type { PendingInvite, InviteCreated, InvitePreview } from './types/invite';
export {
    fetchAccounts,
    fetchAccount,
    createAccount,
    updateAccount,
    loanPaymentPreview,
    fetchAccountBalances,
    fetchAccountSubtotals,
    fetchBalanceForecast,
    fetchAccountFolders,
    createAccountFolder,
    renameAccountFolder,
    moveAccountFolder,
    deleteAccountFolder,
    setAccountFolder,
    mapAccountToFeed,
    unbindAccountFromFeed,
    setAccountSyncFromDate,
    setAccountTradeCommission,
    setAccountActive,
    fetchFrequentCounterparties,
    setupPaymentReminder,
} from './api/account';
export {
    fetchCategories,
    reparentCategory,
    mergeCategory,
    deleteCategory,
} from './api/category';
export {
    fetchTags,
    createTag,
    fetchTagTransactions,
    patchTag,
    mergeTag,
    deleteTag,
    cleanupUnusedTags,
} from './api/tag';
export { fetchHoldings } from './api/holding';
export { undoImport } from './api/imports';
export {
    fetchCsvMappings, validateCsvMapping, createCsvMapping, updateCsvMapping,
    deleteCsvMapping, previewCsv, importCsv,
} from './api/csvMapping';
export {
    fetchSecurities,
    fetchSecurity,
    createSecurity,
    patchSecurity,
    fetchSecurityTransactions,
    fetchSecurityPrices,
    addSecurityPrice,
    patchSecurityPrice,
    deleteSecurityPrice,
    fetchSecurityComponents,
    replaceSecurityComponents,
} from './api/security';
export {
    fetchFeedConnections,
    createFeedConnection,
    renameFeedConnection,
    deleteFeedConnection,
    syncFeedConnection,
    syncAccount,
    syncAllConnections,
    fetchFeedConnectionAccounts,
    fetchSyncRunDetail,
} from './api/feed';
export type {
    EntriesDirection,
    FetchRegisterArgs,
    FetchTransactionEntriesArgs,
    RegisterDirection,
    RegisterStatusCounts,
} from './api/register';
export {
    fetchRegister,
    fetchTransactionEntries,
    setReconStatus,
    deleteTransaction,
    fetchIndexBuckets,
    fetchStatusCounts,
    fetchBalancesForHeaders,
    fetchHeaderLegs,
} from './api/register';
export {
    createTransaction,
    patchTransaction,
    approveTransaction,
    mergeTransaction,
    fetchSimilarPayees,
    fetchMergeCandidates,
} from './api/bank';
export {
    createInvestmentTransaction,
    patchInvestmentTransaction,
    approveInvestmentTransaction,
    mergeInvestmentTransaction,
    deleteInvestmentTransaction,
    fetchInvestmentMergeCandidates,
    fetchOpenLots,
} from './api/investment';
export { fetchPayees } from './api/payee';
export {
    fetchMcpTokens,
    createMcpToken,
    renameMcpToken,
    revokeMcpToken,
    fetchOAuthGrants,
    renameOAuthGrant,
    revokeOAuthGrant,
    fetchMcpSetting,
    setMcpSetting,
    fetchMcpConnections,
    renameMcpConnection,
    revokeMcpConnection,
    fetchMcpClients,
    revokeMcpClient,
    pruneMcpClients,
    fetchMcpAudit,
    clearMcpAudit,
    fetchOAuthConsentInfo,
    type OAuthConsentInfo,
    type McpTokenSummary,
    type IssuedMcpToken,
    type OAuthGrantSummary,
    type McpSetting,
    type McpConnection,
    type McpConnectionKind,
    type McpClient,
    type McpAuditEntry,
    type McpInvocationStatus,
} from './api/mcp';
export {
    fetchSelectionSummary,
    bulkSetReconStatus,
    bulkDeleteTransactions,
    bulkUnhideTransactions,
    bulkMoveToAccount,
} from './api/selection';
export { refreshQuotes } from './api/quote';
export {
    fetchBenchmarks,
    addBenchmark,
    removeBenchmark,
    fetchFxRatesView,
    fetchFxRatePage,
    addFxRate,
} from './api/marketSeries';
export { fetchLedgerOperations } from './api/ledgerOperation';
export { fetchLedgerOverview, fetchNetWorthHistory } from './api/overview';
export { fetchReturns, fetchAllocation } from './api/portfolio';
export {
    deleteBudgetTarget,
    fetchBudgetProgress,
    fetchBudgetTransactions,
    fillBudgetTargets,
    setBudgetTarget,
} from './api/budget';
export type {
    BudgetTargetEntry,
    BudgetTargetEntryResult,
    FillBudgetTargetsRequest,
    FillBudgetTargetsResponse,
} from './api/budget';
export {
    fetchQuoteProviders,
    fetchQuotesPrefs,
    saveQuotesPrefs,
    fetchDashboardPrefs,
    saveDashboardPrefs,
    deleteDashboardPrefs,
} from './api/preference';
export { fetchSchedule, fetchSchedules, saveSchedule } from './api/schedule';
export {
    BACKUPS_QUERY_KEY,
    fetchBackups,
    createBackup,
    startBackup,
    fetchBackupRun,
    deleteBackup,
    downloadBackup,
    pinBackup,
    unpinBackup,
    setBackupPassphrase,
    revealBackupPassphrase,
    fetchBackupSchedule,
    saveBackupSchedule,
    fetchBackupRetention,
    setBackupRetention,
    validateRestoreKek,
} from './api/backup';
export {
    UPGRADE_CONFIRM_PHRASE,
    UPGRADE_QUERY_KEY,
    fetchUpgradeStatus,
    checkForUpgrade,
    preflightUpgrade,
    startUpgrade,
    saveUpgradeSettings,
    saveUpgradeToken,
    clearUpgradeToken,
} from './api/upgrade';
export {
    fetchMasterKeyStatus,
    revealMasterKey,
    rotateMasterKey,
} from './api/masterKey';
export {
    fetchDriveSyncStatus,
    startDriveConnect,
    disconnectDrive,
    setDriveEnabled,
    uploadAllToDrive,
} from './api/driveSync';
export { previewOfx, importOfx } from './api/ofx';
export { previewQif, importQif } from './api/qif';
export { previewFidelity, importFidelity } from './api/fidelity';
export {
    fetchSnapshots,
    createSnapshot,
    restoreSnapshot,
    deleteSnapshot,
} from './api/snapshot';
export {
    fetchReminders,
    fetchUpcomingReminders,
    fetchReminderDetail,
    setReminderActive,
    skipReminder,
    unskipReminder,
    fireReminderBank,
    fireReminderInvestment,
    createReminderBank,
    createReminderInvestment,
    updateReminderBank,
    updateReminderInvestment,
} from './api/reminder';
export {
    fetchNotificationProviders,
    fetchNotificationSubscribers,
    createNotificationSubscriber,
    deleteNotificationSubscriber,
    fetchSystemEvents,
    fetchSystemAttention,
    fetchLedgerAttention,
    fetchLedgerEvents,
    fetchOutstandingProblems,
    fetchLedgerNotifications,
    fetchLedgerNotificationProviders,
    createLedgerNotificationSubscriber,
    deleteLedgerNotificationSubscriber,
} from './api/notification';
export {
    specToQuery,
    fetchReportSummary,
    fetchReturnsSeries,
    fetchSavedReports,
    createSavedReport,
    patchSavedReport,
    deleteSavedReport,
    fetchReportPins,
    pinSavedReport,
    unpinSavedReport,
    pinCatalogReport,
    unpinCatalogReport,
} from './api/report';
