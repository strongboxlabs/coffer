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
export { fetchVisibleLedgers, verifyBalanceHealth, createLedger, renameLedger, deleteLedger } from './api/ledger';
export { fetchLedgerMembers, setLedgerMemberRole, removeLedgerMember } from './api/member';
export type { LedgerMember } from './types/member';
export { fetchAdminUsers, setUserDisabled, setUserAdmin } from './api/adminUsers';
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
    fetchAccountGroups,
    createAccountGroup,
    patchAccountGroup,
    deleteAccountGroup,
    addAccountGroupMember,
    removeAccountGroupMember,
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
    patchTag,
    mergeTag,
    deleteTag,
    cleanupUnusedTags,
} from './api/tag';
export { fetchHoldings } from './api/holding';
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
    deleteFeedConnection,
    syncFeedConnection,
    syncAccount,
    syncAllConnections,
    fetchFeedConnectionAccounts,
    fetchSyncRuns,
    fetchSyncRunDetail,
} from './api/feed';
export type { FetchRegisterArgs, RegisterDirection, RegisterStatusCounts } from './api/register';
export {
    fetchRegister,
    setReconStatus,
    deleteTransaction,
    unhideTransaction,
    moveTransactionToAccount,
    fetchIndexBuckets,
    fetchStatusCounts,
    fetchBalancesForHeaders,
    fetchHeaderLegs,
} from './api/register';
export {
    createTransaction,
    patchTransaction,
    fetchSimilarPayees,
    fetchMergeCandidates,
} from './api/bank';
export {
    createInvestmentTransaction,
    patchInvestmentTransaction,
    deleteInvestmentTransaction,
    fetchInvestmentMergeCandidates,
    fetchOpenLots,
} from './api/investment';
export { fetchPayees } from './api/payee';
export {
    fetchMcpTokens,
    createMcpToken,
    revokeMcpToken,
    fetchMcpSetting,
    setMcpSetting,
    fetchMcpClients,
    revokeMcpClient,
    setMcpClientLabel,
    pruneMcpClients,
    fetchMcpAudit,
    clearMcpAudit,
    type McpTokenSummary,
    type IssuedMcpToken,
    type McpSetting,
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
export { fetchLedgerOperations } from './api/ledgerOperation';
export { fetchLedgerOverview } from './api/overview';
export {
    fetchQuoteProviders,
    fetchQuotesPrefs,
    saveQuotesPrefs,
    fetchDashboardPrefs,
    saveDashboardPrefs,
} from './api/preference';
export { fetchSchedule, saveSchedule } from './api/schedule';
export {
    fetchBackups,
    createBackup,
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
    fireReminder,
    fireReminderBank,
    fireReminderInvestment,
    createReminderBank,
    createReminderInvestment,
    updateReminderBank,
    updateReminderInvestment,
} from './api/reminder';
