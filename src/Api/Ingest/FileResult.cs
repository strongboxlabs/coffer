namespace Coffer.Api.Ingest;

/// <summary>
/// Output of <see cref="IFileProvider.ParseAsync"/>. Mirrors
/// <see cref="PullResult"/> minus the auth-state signals that don't
/// apply to a per-upload stateless provider.
/// </summary>
/// <remarks>
/// OFX/QFX uploads are inherently multi-account (a single file from
/// e.g. Eastbank typically carries checking + savings + a credit card).
/// <see cref="DiscoveredAccounts"/> exposes one entry per statement
/// block in the file so the preview surface can show "this file has
/// 3 accounts" and the SPA's mapping wizard can prompt for one Coffer
/// account binding per discovered provider account. Each
/// <see cref="IngestedTransaction"/> carries its statement's
/// <see cref="IngestedTransaction.ProviderAccountId"/> so the
/// orchestrator can dispatch transactions to the right Coffer account
/// during import.
/// </remarks>
public sealed record FileResult(
    IReadOnlyList<IngestedTransaction> Transactions,
    IReadOnlyList<DiscoveredFileAccount> DiscoveredAccounts,
    IReadOnlyList<IngestError> Errors);

/// <summary>
/// One account block surfaced by a file-provider parse. For OFX/QFX,
/// composed from <c>BANKACCTFROM</c> or <c>CCACCTFROM</c> /
/// <c>INVACCTFROM</c>; for CSV-per-institution providers, derived
/// from the institution's wire shape (single-account is common
/// there).
/// </summary>
public sealed record DiscoveredFileAccount(
    /// <summary>Composite provider-stable key for the source account.
    /// OFX: <c>{BANKID}:{ACCTID}</c> for bank, <c>card:{ACCTID}</c>
    /// for credit cards, <c>inv:{BROKERID}:{ACCTID}</c> for
    /// investments. The exact composition is provider-private; the
    /// orchestrator treats it as opaque and uses it only to dispatch
    /// transactions per mapping.</summary>
    string ProviderAccountId,
    /// <summary>Coarse account type the provider reported.
    /// OFX values: <c>"bank"</c>, <c>"credit_card"</c>,
    /// <c>"investment"</c>. Drives the SPA's mapping wizard's
    /// suggested-account filter.</summary>
    string AccountType,
    /// <summary>ISO-4217 currency on the statement (e.g. <c>"USD"</c>).
    /// Lets the SPA refuse mappings whose Coffer account has a
    /// different currency.</summary>
    string? Currency,
    /// <summary>Count of transactions parsed for this account block.
    /// Surface only — the actual transactions live on
    /// <see cref="FileResult.Transactions"/>, filterable by
    /// <see cref="IngestedTransaction.ProviderAccountId"/>.</summary>
    int TransactionCount);
