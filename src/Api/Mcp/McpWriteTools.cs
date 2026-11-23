using System.ComponentModel;
using System.Reflection;

using ModelContextProtocol.Server;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Ingest.Csv;
using Coffer.Api.Quotes.MarketSeries;

namespace Coffer.Api.Mcp;

/// <summary>
/// MCP <b>write</b> primitives (ADR-0068) — AI-assisted data cleanup. Mechanical
/// set / merge / delete / recategorize / convert / tag tools; all judgment lives in
/// the assistant, the human reviews, these persist the result. <b>One entity per
/// call</b> (no batch) — the assistant iterates — EXCEPT <c>set_transaction_tags</c>,
/// the deliberate bulk exception (ADR-0081 D6): tagging is an idempotent replace-set
/// with low blast radius. Each call supports <c>dryRun</c> and echoes the resulting
/// state. Every tool first calls <see cref="McpWriteGuard.EnsureWritable"/>
/// (ADR-0081 D1/D2) — writes must be enabled deployment-wide AND the token must carry
/// the <c>coffer.write</c> scope. Like the read tools, each method takes its
/// RLS-scoped repository from the request DI scope, so the bearer's user is the data
/// boundary; writes go through the same repositories (and DB invariants) as the SPA.
/// </summary>
/// <remarks>
/// This class deliberately carries NO <see cref="McpServerToolTypeAttribute"/>:
/// the read-tool assembly scan must skip it. Program.cs registers it via
/// <c>WithTools&lt;McpWriteTools&gt;()</c> ALWAYS (ADR-0081 D2) — the tools are always
/// present; <see cref="McpWriteGuard"/> (the runtime writes kill-switch + the token's
/// <c>coffer.write</c> scope), not their registration, is the gate, so an admin can
/// turn writes off and have it take effect immediately, no restart.
/// (Non-static so it's usable as the <c>WithTools&lt;T&gt;</c> type argument; the tool
/// methods are static and take their dependencies from the request DI scope.)
/// </remarks>
public sealed class McpWriteTools
{
    /// <summary>
    /// The tool names of every write tool here (their <c>[McpServerTool(Name=…)]</c>),
    /// reflected once. <see cref="McpAuditFilter"/> uses this to audit only writes; a
    /// new write tool is covered automatically (same single-source idea as the guard
    /// completeness test).
    /// </summary>
    public static readonly IReadOnlySet<string> ToolNames =
        typeof(McpWriteTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .ToHashSet();

    [McpServerTool(Name = "save_csv_mapping"), Description(
        "Create or replace a delimited-import mapping document (ADR-0031 Phase 5). " +
        "Pass mappingId to replace an existing one, omit it to create. " +
        "NOTHING IS STORED UNLESS THE DOCUMENT VALIDATES — call validate_csv_mapping " +
        "first and fix everything it reports; this returns the same problems rather than " +
        "saving a document the importer would then fail on row by row. " +
        "dryRun=true validates and reports without writing. " +
        "Names are unique per ledger, case-insensitively. " +
        "See validate_csv_mapping for the document shape. " +
        CsvMappingTools.DecimalSeparatorGuidance)]
    public static async Task<McpWriteResult> SaveCsvMapping(
        McpWriteGuard guard,
        CsvMappingsRepository mappings,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("What to call it; unique per ledger, e.g. 'Card statement CSV'.")]
        string name,
        [Description("The YAML document.")] string definitionYaml,
        [Description("Existing mapping id (GUID) to replace. Omit to create a new one.")]
        Guid? mappingId = null,
        [Description("Validate and report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        if (string.IsNullOrWhiteSpace(name))
            return McpWriteResult.Fail(mappingId ?? Guid.Empty, "mapping-name-required");

        // Validated on the dry-run path too, which is the point of offering one: an
        // assistant composing YAML gets the verdict without leaving a half-built row
        // behind when it guesses wrong.
        var candidate = CsvMappingsRepository.Validate(definitionYaml ?? "", out var errors);
        if (candidate is null)
        {
            return McpWriteResult.Fail(mappingId ?? Guid.Empty,
                "mapping-invalid: " + string.Join("; ",
                    errors.Select(e => (e.Path.Length > 0 ? e.Path + " — " : "") + e.Message)));
        }
        if (dryRun)
            return new McpWriteResult(mappingId ?? Guid.Empty, true, null, name, null);

        var result = await mappings
            .SaveAsync(ledgerId, mappingId, name, definitionYaml!, cancellationToken)
            .ConfigureAwait(false);

        if (result.NameTaken)
            return McpWriteResult.Fail(mappingId ?? Guid.Empty, "mapping-name-taken");
        if (result.Saved is null)
            return McpWriteResult.Fail(mappingId ?? Guid.Empty, "mapping-not-in-ledger");

        return new McpWriteResult(result.Saved.Id, true, null, result.Saved.Name, null);
    }

    [McpServerTool(Name = "delete_csv_mapping"), Description(
        "Remove a mapping document. Does NOT touch anything already imported with it: " +
        "those are ordinary transactions carrying their own import stamp, and undoing " +
        "an import is what removes those. dryRun=true reports without deleting.")]
    public static async Task<McpWriteResult> DeleteCsvMapping(
        McpWriteGuard guard,
        CsvMappingsRepository mappings,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Mapping id (GUID) from list_csv_mappings.")] Guid mappingId,
        [Description("Report without deleting.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var existing = await mappings.GetAsync(ledgerId, mappingId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
            return McpWriteResult.Fail(mappingId, "mapping-not-in-ledger");
        if (dryRun)
            return new McpWriteResult(mappingId, true, existing.Name, null, null);

        var removed = await mappings.DeleteAsync(ledgerId, mappingId, cancellationToken)
            .ConfigureAwait(false);
        return removed
            ? new McpWriteResult(mappingId, true, existing.Name, null, null)
            : McpWriteResult.Fail(mappingId, "mapping-not-in-ledger");
    }

    [McpServerTool(Name = "set_account_taxstatus"), Description(
        "Set the tax treatment of ONE account (ADR-0066): taxStatus = taxable / " +
        "tax_deferred / tax_free / other (empty or null clears it). The assistant " +
        "infers from the account name (401k/IRA → tax_deferred, Roth → tax_free, " +
        "brokerage → taxable) and the human reviews; call once per account (no " +
        "batch). dryRun=true previews before/after without writing. Returns the " +
        "before/after value. Resolve the id via list_accounts.")]
    public static async Task<McpWriteResult> SetAccountTaxStatus(
        McpWriteGuard guard,
        AccountsRepository accounts,
        AccountsReportingRepository accountsRead,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Account id (GUID) from list_accounts.")] Guid accountId,
        [Description("taxable / tax_deferred / tax_free / other (empty/null clears).")]
        string? taxStatus,
        [Description("Preview only — report before/after without persisting.")]
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        // Current value (incl. inactive accounts — they're the ones with null
        // tax_status) for the before echo + not-in-ledger detection.
        var before = (await accountsRead
                .ListAccountsAsync(ledgerId, includeCategories: true, includeInactive: true, type: null,
                    today: null, cancellationToken)
                .ConfigureAwait(false))
            .FirstOrDefault(a => a.Id == accountId);
        if (before is null)
            return McpWriteResult.Fail(accountId, "account-not-in-ledger");

        if (dryRun)
            return new McpWriteResult(accountId, true, before.TaxStatus, taxStatus, null);

        // null/empty clears (UpdateAccountRequest: "" clears, null = leave alone —
        // for a setter we always intend to apply, so map null → clear).
        var result = await accounts.UpdateAsync(
            ledgerId, accountId,
            new UpdateAccountRequest { TaxStatus = taxStatus ?? "" },
            cancellationToken).ConfigureAwait(false);

        return result == AccountsRepository.UpdateAccountResult.Ok
            ? new McpWriteResult(accountId, true, before.TaxStatus, taxStatus, null)
            : McpWriteResult.Fail(accountId, result.ToString());
    }

    [McpServerTool(Name = "set_security_classification"), Description(
        "Set the rich classification of ONE security (ADR-0067). Fields: assetClass " +
        "(equity / fixed_income / multi_asset / cash / real_assets / alternative), " +
        "vehicleType (etf / mutual_fund / stock / bond / money_market / cd / cit / " +
        "separate_account / plan_529 / option / other), region (us / developed_ex_us / " +
        "emerging / global), equitySize " +
        "(large / mid / small), equityStyle (value / blend / growth), fiDuration " +
        "(short / intermediate / long), fiCredit (government / investment_grade / " +
        "high_yield), taxCharacter (taxable / tax_exempt / tax_managed). The assistant " +
        "infers each from the security name/ticker; the human reviews. Pass only the " +
        "fields you want to set — null leaves a field alone, \"\" clears it. " +
        "overwrite=false (default) fills only fields that are currently empty; set " +
        "overwrite=true to correct an existing value. One security per call (no batch); " +
        "the assistant iterates. dryRun=true previews before/after without writing. " +
        "Echoes the full classification before/after. Resolve the id via list_securities. " +
        "(Multi-asset look-through sleeves are a separate, later tool.)")]
    public static async Task<McpWriteResult> SetSecurityClassification(
        McpWriteGuard guard,
        SecuritiesRepository securities,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Security id (GUID) from list_securities.")] Guid securityId,
        [Description("equity / fixed_income / multi_asset / cash / real_assets / alternative (\"\" clears).")]
        string? assetClass = null,
        [Description("etf / mutual_fund / stock / bond / money_market / cd / cit / separate_account / plan_529 / option / other (\"\" clears).")]
        string? vehicleType = null,
        [Description("us / developed_ex_us / emerging / global (\"\" clears).")]
        string? region = null,
        [Description("large / mid / small (\"\" clears).")] string? equitySize = null,
        [Description("value / blend / growth (\"\" clears).")] string? equityStyle = null,
        [Description("short / intermediate / long (\"\" clears).")] string? fiDuration = null,
        [Description("government / investment_grade / high_yield (\"\" clears).")] string? fiCredit = null,
        [Description("taxable / tax_exempt / tax_managed (\"\" clears).")] string? taxCharacter = null,
        [Description("false (default) fills only empty fields; true overwrites existing values.")]
        bool overwrite = false,
        [Description("Preview only — report before/after without persisting.")]
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var before = await securities.GetByIdAsync(ledgerId, securityId, today: null, cancellationToken)
            .ConfigureAwait(false);
        if (before is null) return McpWriteResult.Fail(securityId, "security-not-in-ledger");

        // Fill-nulls-only unless overwrite: include a field in the PATCH only when the
        // caller provided it AND (overwrite OR the current value is empty). null = leave
        // alone; "" = clear (a deliberate correction, so it only lands under overwrite).
        static string? Take(string? provided, string? current, bool overwrite) =>
            provided is null ? null
            : (overwrite || string.IsNullOrEmpty(current)) ? provided
            : null;

        var patch = new PatchSecurityRequest
        {
            AssetClass   = Take(assetClass,   before.AssetClass,   overwrite),
            VehicleType  = Take(vehicleType,  before.VehicleType,  overwrite),
            Region       = Take(region,       before.Region,       overwrite),
            EquitySize   = Take(equitySize,   before.EquitySize,   overwrite),
            EquityStyle  = Take(equityStyle,  before.EquityStyle,  overwrite),
            FiDuration   = Take(fiDuration,   before.FiDuration,   overwrite),
            FiCredit     = Take(fiCredit,     before.FiCredit,     overwrite),
            TaxCharacter = Take(taxCharacter, before.TaxCharacter, overwrite),
        };

        var beforeStr = FmtClassification(before);
        var anyChange = patch.AssetClass is not null || patch.VehicleType is not null
            || patch.Region is not null || patch.EquitySize is not null
            || patch.EquityStyle is not null || patch.FiDuration is not null
            || patch.FiCredit is not null || patch.TaxCharacter is not null;
        if (!anyChange)
            return new McpWriteResult(securityId, true, beforeStr, beforeStr, null); // no-op

        static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        if (dryRun)
        {
            var afterStr = FmtClassification(
                patch.AssetClass   is { } ac  ? Norm(ac)  : before.AssetClass,
                patch.VehicleType  is { } vt  ? Norm(vt)  : before.VehicleType,
                patch.Region       is { } rg  ? Norm(rg)  : before.Region,
                patch.EquitySize   is { } es  ? Norm(es)  : before.EquitySize,
                patch.EquityStyle  is { } est ? Norm(est) : before.EquityStyle,
                patch.FiDuration   is { } fd  ? Norm(fd)  : before.FiDuration,
                patch.FiCredit     is { } fc  ? Norm(fc)  : before.FiCredit,
                patch.TaxCharacter is { } tx  ? Norm(tx)  : before.TaxCharacter);
            return new McpWriteResult(securityId, true, beforeStr, afterStr, null);
        }

        var result = await securities.PatchAsync(ledgerId, securityId, patch, cancellationToken)
            .ConfigureAwait(false);
        if (result != SecuritiesRepository.PatchResult.Ok)
            return McpWriteResult.Fail(securityId, result.ToString());

        var after = await securities.GetByIdAsync(ledgerId, securityId, today: null, cancellationToken)
            .ConfigureAwait(false);
        return new McpWriteResult(securityId, true, beforeStr, FmtClassification(after!), null);
    }

    [McpServerTool(Name = "merge_category"), Description(
        "Merge ONE source category into a target (ADR-0068) — the lever for collapsing " +
        "duplicate / redundant categories. Repoints every transaction (and reminder) " +
        "from the source to the target, reparents the source's child categories to the " +
        "target, then DEACTIVATES the emptied source (the row is preserved, not deleted; " +
        "re-activating it brings back an EMPTY category — moved transactions, child " +
        "categories and budget targets are not restored). The kinds may differ (a " +
        "cross-kind merge is how a category is reclassified); the target must not be the " +
        "source or beneath it (refused as TargetIsDescendant), and the source can't be a " +
        "system category. Budget targets follow the source and are summed where the " +
        "target holds one for the same month, in the target's currency: a source target " +
        "typed in another currency converts at the month's last day (the latest rate for " +
        "the current month), and when that exchange rate is missing the whole merge is " +
        "refused as RateUnavailable, naming the currency pair and day, and nothing is " +
        "written; a target that would sit beneath another for " +
        "the same month (anything beneath the target against the target's, what arrives " +
        "against the target's ancestors') is dropped; a real merge reports how many moved " +
        "and were dropped. One pair per call; the assistant runs a category revamp as many " +
        "merge_category calls. dryRun=true reports how many transactions / child " +
        "categories would move without writing (budget targets are not previewed). " +
        "Resolve ids via list_accounts (categories).")]
    public static async Task<McpWriteResult> MergeCategory(
        McpWriteGuard guard,
        AccountsRepository accounts,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Source category id (GUID) — merged away (deactivated).")] Guid sourceId,
        [Description("Target category id (GUID) — keeps everything.")] Guid targetId,
        [Description("Preview only — report counts without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var r = await accounts.MergeCategoryAsync(ledgerId, sourceId, targetId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        if (r.Result == AccountsRepository.MergeCategoryResult.RateUnavailable && r.MissingRate is { } missing)
            return McpWriteResult.Fail(sourceId,
                $"RateUnavailable: no {missing.FromCurrency}->{missing.ToCurrency} rate for {missing.Date:yyyy-MM-dd}; "
                + "nothing was merged");
        if (r.Result != AccountsRepository.MergeCategoryResult.Ok)
            return McpWriteResult.Fail(sourceId, r.Result.ToString());

        var before = $"{r.TransactionsMoved} txns, {r.ChildrenReparented} child categories";
        var after = dryRun
            ? $"would merge into {targetId}; source deactivated"
            : $"merged into {targetId}; source deactivated; "
              + $"{r.BudgetTargetsMoved} budget targets moved, {r.BudgetTargetsDropped} dropped";
        return new McpWriteResult(sourceId, true, before, after, null);
    }

    [McpServerTool(Name = "delete_category"), Description(
        "Delete ONE empty category (ADR-0068). Succeeds only when the category has no " +
        "transactions and no child categories and is not system-managed; otherwise " +
        "returns an error (use merge_category to relocate its transactions first). One " +
        "category per call. dryRun=true reports whether it's deletable. Resolve the id " +
        "via list_accounts (categories).")]
    public static async Task<McpWriteResult> DeleteCategory(
        McpWriteGuard guard,
        AccountsRepository accounts,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Category id (GUID) to delete.")] Guid categoryId,
        [Description("Preview only — report deletability without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var r = await accounts.DeleteCategoryAsync(ledgerId, categoryId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        if (r.Result == AccountsRepository.DeleteCategoryResult.InUse)
            return McpWriteResult.Fail(categoryId,
                $"category-in-use ({r.TransactionCount} txns, {r.ChildCount} children) — merge it instead");
        if (r.Result != AccountsRepository.DeleteCategoryResult.Ok)
            return McpWriteResult.Fail(categoryId, r.Result.ToString());

        return new McpWriteResult(categoryId, true,
            $"{r.TransactionCount} txns, {r.ChildCount} children",
            dryRun ? "deletable" : "deleted", null);
    }

    [McpServerTool(Name = "set_transaction_category"), Description(
        "Recategorize one or MANY simple transactions to a single target category " +
        "(ADR-0068). Best-effort: each single-posting, bank-shape transaction with exactly " +
        "one category leg is repointed to the new category, and the category side's own memo " +
        "and tags move with that leg; a split (multiple postings), a " +
        "transfer (no category), or an investment transaction is REJECTED and returned in " +
        "'rejects' with a reason (edit those in the app) so one bad row never blocks the " +
        "rest. The whole call is rejected (nothing written) only when the target category " +
        "isn't in the ledger or the id list is empty. Returns { recategorized, unchanged, " +
        "category, rejects: [{ headerId, reason }] }. reason is one of split / transfer / " +
        "investment-transaction / not-in-ledger / ledger-write-busy. Each transaction is " +
        "saved on its own, so recategorized counts every one that moved, whatever was " +
        "rejected after it. ledger-write-busy means another change to the ledger was still " +
        "being saved: that transaction was not changed; send it again. dryRun=true previews the split without " +
        "writing. Resolve header ids via the register / list_transactions and the category " +
        "id via list_accounts.")]
    public static async Task<McpBulkRecategorizeResult> SetTransactionCategory(
        McpWriteGuard guard,
        TransactionsRepository transactions,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Transaction (header) ids to recategorize - one or many.")] Guid[] headerIds,
        [Description("New category id (GUID) from list_accounts.")] Guid categoryId,
        [Description("Preview only - report the split without persisting.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        if (headerIds is null || headerIds.Length == 0)
            return McpBulkRecategorizeResult.Fail("no-transactions");

        var r = await transactions
            .BulkRecategorizeAsync(ledgerId, headerIds, categoryId, dryRun, cancellationToken)
            .ConfigureAwait(false);

        return r.Result switch
        {
            TransactionsRepository.BulkRecategorizeResult.TargetNotInLedger =>
                McpBulkRecategorizeResult.Fail("target-category-not-in-ledger"),
            TransactionsRepository.BulkRecategorizeResult.NoHeaders =>
                McpBulkRecategorizeResult.Fail("no-transactions"),
            _ => new McpBulkRecategorizeResult(
                true, r.Recategorized, r.Unchanged, r.CategoryName,
                r.Rejects.Select(x => new McpRecategorizeReject(x.HeaderId, x.Reason)).ToList(),
                dryRun, null),
        };
    }

    [McpServerTool(Name = "set_split_posting_category"), Description(
        "Recategorize the posting(s) of one or MANY SPLIT (multi-posting) BANK transactions " +
        "that sit on fromCategoryId, repointing them to toCategoryId and leaving every other " +
        "posting untouched — the posting-level parallel to set_transaction_category, for the " +
        "splits it can't touch. Best-effort across headerIds: a header that isn't a " +
        "bank-shape split with a fromCategory posting is returned in 'rejects' with a reason " +
        "(investment-transaction / not-a-split / posting-not-found / unsupported-shape / " +
        "not-in-ledger / apply-failed / ledger-write-busy), so one bad row never blocks the " +
        "rest. Each transaction is saved on its own, so moved counts every posting that moved, " +
        "whatever was rejected after it. ledger-write-busy means another change to the ledger " +
        "was still being saved: that transaction was not changed; send it again. BANK-SHAPE " +
        "ONLY — investment transactions are rejected. The whole call fails (nothing written) " +
        "only when the target category isn't in the ledger or the id list is empty. EVERY " +
        "fromCategory posting in a header moves (a re-home; no per-posting targeting). " +
        "Returns { ok, moved, unchanged, category, rejects:[{ headerId, reason }] }. " +
        "dryRun=true previews the tally. Find splits + their per-category postings via " +
        "list_transactions(categoryId=…); resolve category ids via list_accounts.")]
    public static async Task<McpBulkSplitPostingResult> SetSplitPostingCategory(
        McpWriteGuard guard,
        TransactionsRepository transactions,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Split transaction (header) ids to touch — one or many.")] Guid[] headerIds,
        [Description("Category the target posting(s) are currently on (GUID from list_accounts).")]
        Guid fromCategoryId,
        [Description("New category id (GUID from list_accounts).")] Guid toCategoryId,
        [Description("Preview only — report the tally without persisting.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        if (headerIds is null || headerIds.Length == 0)
            return McpBulkSplitPostingResult.Fail("no-transactions");

        var r = await transactions
            .BulkRecategorizeSplitPostingsAsync(
                ledgerId, headerIds, fromCategoryId, toCategoryId, dryRun, cancellationToken)
            .ConfigureAwait(false);

        return r.Result switch
        {
            TransactionsRepository.BulkSplitPostingResult.TargetNotInLedger =>
                McpBulkSplitPostingResult.Fail("target-category-not-in-ledger"),
            TransactionsRepository.BulkSplitPostingResult.NoHeaders =>
                McpBulkSplitPostingResult.Fail("no-transactions"),
            _ => new McpBulkSplitPostingResult(
                true, r.Moved, r.Unchanged, r.ToCategory,
                r.Rejects.Select(x => new McpRecategorizeReject(x.HeaderId, x.Reason)).ToList(),
                dryRun, null),
        };
    }

    [McpServerTool(Name = "create_category"), Description(
        "Create ONE new category (ADR-0068) — for building out a clean category tree. " +
        "kind = income | expense | adjustment (adjustment: a valuation or other entry that is " +
        "neither income nor spending). Optional parentId nests it under an existing category in " +
        "the same ledger (omit for a root category). Returns the new category's id. " +
        "dryRun=true previews without creating. Pair with merge_category / " +
        "reparent_category / rename_category to reshape the tree.")]
    public static async Task<McpWriteResult> CreateCategory(
        McpWriteGuard guard,
        AccountsRepository accounts,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Category name.")] string name,
        [Description("income | expense | adjustment.")] string kind,
        [Description("Optional parent category id (GUID) to nest under; omit for a root category.")]
        Guid? parentId = null,
        [Description("Preview only — report without creating.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var where = parentId is { } pp ? $" under {pp}" : "";
        if (dryRun)
            return new McpWriteResult(Guid.Empty, true, null, $"would create {kind} category '{name}'{where}", null);

        var outcome = await accounts.CreateAsync(ledgerId, new CreateAccountRequest
        {
            Name = name,
            AccountType = "category",
            CategoryKind = kind,
            ParentId = parentId,
        }, cancellationToken).ConfigureAwait(false);

        return outcome.Failure == AccountsRepository.CreateAccountFailure.None && outcome.Account is { } acct
            ? new McpWriteResult(acct.Id, true, null, $"created {kind} category '{acct.Name}'{where}", null)
            : McpWriteResult.Fail(Guid.Empty, outcome.Failure.ToString());
    }

    [McpServerTool(Name = "rename_category"), Description(
        "Rename ONE category (ADR-0068). New name only — kind and parent are unchanged " +
        "(use reparent_category to move it). dryRun=true previews. Resolve the id via " +
        "list_accounts (categories).")]
    public static async Task<McpWriteResult> RenameCategory(
        McpWriteGuard guard,
        AccountsRepository accounts,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Category id (GUID).")] Guid categoryId,
        [Description("New name.")] string name,
        [Description("Preview only.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var before = await accounts.GetBasicAsync(ledgerId, categoryId, cancellationToken).ConfigureAwait(false);
        if (before is null) return McpWriteResult.Fail(categoryId, "category-not-in-ledger");
        if (before.AccountType != "category") return McpWriteResult.Fail(categoryId, "not-a-category");
        if (dryRun) return new McpWriteResult(categoryId, true, before.Name, name, null);

        var result = await accounts.UpdateAsync(
            ledgerId, categoryId, new UpdateAccountRequest { Name = name }, cancellationToken)
            .ConfigureAwait(false);
        return result == AccountsRepository.UpdateAccountResult.Ok
            ? new McpWriteResult(categoryId, true, before.Name, name, null)
            : McpWriteResult.Fail(categoryId, result.ToString());
    }

    [McpServerTool(Name = "update_security"), Description(
        "Set ONE security's ticker / cusip / name (ADR-0068) — e.g. add a missing ticker " +
        "(which also unblocks ticker-based matching) or fix a name. Pass only the fields to " +
        "change; null leaves a field alone, \"\" clears ticker/cusip. Ticker and cusip are " +
        "unique per ledger (a collision errors). dryRun=true previews. Resolve the id via " +
        "list_securities. (Use set_security_classification for asset-class/vehicle.)")]
    public static async Task<McpWriteResult> UpdateSecurity(
        McpWriteGuard guard,
        SecuritiesRepository securities,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Security id (GUID) from list_securities.")] Guid securityId,
        [Description("Ticker symbol; \"\" clears.")] string? ticker = null,
        [Description("CUSIP; \"\" clears.")] string? cusip = null,
        [Description("Display name (non-empty).")] string? name = null,
        [Description("Preview only.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var before = await securities.GetByIdAsync(ledgerId, securityId, today: null, cancellationToken).ConfigureAwait(false);
        if (before is null) return McpWriteResult.Fail(securityId, "security-not-in-ledger");

        if (ticker is null && cusip is null && name is null)
            return new McpWriteResult(securityId, true, FmtSecurity(before), FmtSecurity(before), null);

        static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        var beforeStr = FmtSecurity(before);
        if (dryRun)
        {
            var afterStr =
                $"ticker={(ticker is { } t ? Norm(t) ?? "-" : before.Ticker ?? "-")} " +
                $"cusip={(cusip is { } c ? Norm(c) ?? "-" : before.Cusip ?? "-")} " +
                $"name={(name is { } n ? Norm(n) ?? before.Name : before.Name)}";
            return new McpWriteResult(securityId, true, beforeStr, afterStr, null);
        }

        var result = await securities.PatchAsync(ledgerId, securityId,
            new PatchSecurityRequest { Ticker = ticker, Cusip = cusip, Name = name }, cancellationToken)
            .ConfigureAwait(false);
        if (result != SecuritiesRepository.PatchResult.Ok)
            return McpWriteResult.Fail(securityId, result.ToString());

        var after = await securities.GetByIdAsync(ledgerId, securityId, today: null, cancellationToken).ConfigureAwait(false);
        return new McpWriteResult(securityId, true, beforeStr, FmtSecurity(after!), null);
    }

    [McpServerTool(Name = "convert_in_kind_transfer"), Description(
        "Convert a mis-recorded in-kind transfer (ADR-0065/0068): a (sell/sellx)+(buy/buyx) " +
        "pair that is really shares moving between two accounts — same security, same date, " +
        "equal quantity, distinct investment accounts — into ONE transfer_shares (carrying " +
        "the FIFO lots + basis, zero realized gain). Between accounts in different " +
        "currencies each lot's basis converts at the derived rate for its acquisition day " +
        "(ADR-0114 D13); a lot with no rate refuses the whole conversion, naming each " +
        "missing rate. The transfer's memo joins the two " +
        "events' memos (the sell's first) and its tags are the union of theirs. Pass the two header ids. Use the read " +
        "tool that finds in-kind candidates to identify pairs. (No dryRun — preview the pair " +
        "via that read first.)")]
    public static async Task<McpWriteResult> ConvertInKindTransfer(
        McpWriteGuard guard,
        InvestmentTransactionsRepository investments,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("The sell/sellx header id (GUID).")] Guid sellHeaderId,
        [Description("The buy/buyx header id (GUID).")] Guid buyHeaderId,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var r = await investments
            .ConvertInKindTransferAsync(ledgerId, sellHeaderId, buyHeaderId, cancellationToken)
            .ConfigureAwait(false);
        return r.Result == InvestmentTransactionsRepository.ConvertInKindResult.Ok
            ? new McpWriteResult(r.HeaderId, true, "sell + buy pair", "transfer_shares", null)
            : McpWriteResult.Fail(sellHeaderId, ConvertFailure(r));
    }

    /// <summary>
    /// Why a conversion failed: the outcome and the validation failure, and for a pair
    /// between brokerages in different currencies whose lots have no rate (ADR-0114 D13)
    /// each missing stored leg, in the direction it is stored, and its day.
    /// </summary>
    private static string ConvertFailure(InvestmentTransactionsRepository.ConvertInKindOutcome r)
    {
        if (r.CreateFail is not { } f) return r.Result.ToString();
        var reason = $"{r.Result}:{f}";
        return r.Detail?.MissingRates is { Count: > 0 } missing
            ? reason + " (no rate: " + string.Join("; ", missing.Select(m =>
                $"{m.FromCurrency}->{m.ToCurrency} on {m.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}")) + ")"
            : reason;
    }

    [McpServerTool(Name = "reparent_category"), Description(
        "Move ONE category under a different parent (ADR-0068) — or to the root (omit " +
        "parentId / pass null). The new parent must be a category in the same ledger; " +
        "moving a category under itself or one of its own descendants is rejected (cycle). " +
        "dryRun=true previews. Resolve ids via list_accounts (categories).")]
    public static async Task<McpWriteResult> ReparentCategory(
        McpWriteGuard guard,
        AccountsRepository accounts,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Category id (GUID) to move.")] Guid categoryId,
        [Description("New parent category id (GUID); omit / null to move to the root.")]
        Guid? parentId = null,
        [Description("Preview only.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var r = await accounts
            .ReparentCategoryAsync(ledgerId, categoryId, parentId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        return r is AccountsRepository.ReparentCategoryResult.Ok
                  or AccountsRepository.ReparentCategoryResult.SameParent
            ? new McpWriteResult(categoryId, true, null,
                parentId is { } p ? $"parent -> {p}" : "parent -> (root)", null)
            : McpWriteResult.Fail(categoryId, r.ToString());
    }

    [McpServerTool(Name = "set_security_components"), Description(
        "Set the multi-asset look-through sleeves of ONE security (ADR-0067/0068) — so a " +
        "balanced / target-date fund decomposes into its asset classes instead of showing " +
        "as one undecomposed multi_asset lump in allocation. REPLACES the whole sleeve set. " +
        "Each sleeve: assetClass (equity / fixed_income / cash / real_assets / alternative), " +
        "optional region (us / developed_ex_us / emerging / global / na), and weight (a " +
        "percent, 0-100); weights should sum to ~100. Pass an empty list to clear. The " +
        "security should be classified asset_class=multi_asset for the sleeves to drive " +
        "allocation. dryRun=true previews before/after. Resolve the id via list_securities.")]
    public static async Task<McpWriteResult> SetSecurityComponents(
        McpWriteGuard guard,
        SecuritiesRepository securities,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Security id (GUID) from list_securities.")] Guid securityId,
        [Description("The look-through sleeves; replaces the whole set. Empty list clears.")]
        SecurityComponentDto[] sleeves,
        [Description("Preview only — report before/after without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var before = await securities.GetComponentsAsync(ledgerId, securityId, cancellationToken)
            .ConfigureAwait(false);
        if (before is null) return McpWriteResult.Fail(securityId, "security-not-in-ledger");

        static string Fmt(IReadOnlyList<SecurityComponentDto> cs) => cs.Count == 0
            ? "(none)"
            : string.Join(", ", cs.Select(c => $"{c.AssetClass}{(c.Region is { } r ? "/" + r : "")}={c.Weight}"));
        var beforeStr = Fmt(before);
        var afterStr = Fmt(sleeves);
        if (dryRun) return new McpWriteResult(securityId, true, beforeStr, afterStr, null);

        var result = await securities.ReplaceComponentsAsync(ledgerId, securityId, sleeves, cancellationToken)
            .ConfigureAwait(false);
        return result == SecuritiesRepository.ComponentsResult.Ok
            ? new McpWriteResult(securityId, true, beforeStr, afterStr, null)
            : McpWriteResult.Fail(securityId, result.ToString());
    }

    [McpServerTool(Name = "merge_securities"), Description(
        "Merge a duplicate / alias security into the keeper (ADR-0068) — collapse records " +
        "that are really the same instrument (an alias with no ticker, or two rows for the " +
        "same fund). Repoints every transaction, realized gain, and provider-mapping from " +
        "the source to the target, rebuilds holdings, then DEACTIVATES the source " +
        "(reversible — not deleted; its own price history stays with it). One pair per call. " +
        "The answer counts the transaction lines that move and the accounts whose holdings are " +
        "rebuilt: each account that holds the source or carries its shares on a transaction. " +
        "dryRun=true reports those counts without writing. Resolve ids via list_securities.")]
    public static async Task<McpWriteResult> MergeSecurities(
        McpWriteGuard guard,
        SecuritiesRepository securities,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Source (duplicate) security id (GUID) — merged away (deactivated).")] Guid sourceId,
        [Description("Target (keeper) security id (GUID).")] Guid targetId,
        [Description("Preview only — report counts without writing.")] bool dryRun = false,
        MarketSeriesWriteTrigger? starts = null,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        // ADR-0114 D11: the target takes the source's positions, so the target's price
        // currencies meet accounts they did not, which can add a position pair.
        var needed = dryRun || starts is null ? null : await starts.ReadAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var r = await securities.MergeSecuritiesAsync(ledgerId, sourceId, targetId, dryRun, cancellationToken)
            .ConfigureAwait(false);
        if (r.Result != SecuritiesRepository.MergeSecuritiesResult.Ok)
            return McpWriteResult.Fail(sourceId, r.Result.ToString());
        if (needed is not null)
            await starts!.StartIfWidenedAsync(ledgerId, needed, cancellationToken).ConfigureAwait(false);
        var before = $"{r.LegsMoved} txns across {r.AccountsRecomputed} account(s)";
        var after = dryRun
            ? $"would merge into {targetId}; source deactivated"
            : $"merged into {targetId}; source deactivated";
        return new McpWriteResult(sourceId, true, before, after, null);
    }

    [McpServerTool(Name = "set_transaction_tags"), Description(
        "Replace the tag set on one or MANY transactions at once (ADR-0081 — the " +
        "deliberate BULK exception; every other write tool is one-entity-per-call). " +
        "'tags' is the COMPLETE set to assign to each transaction (a replace-set, not " +
        "additive): pass [\"reimbursable\",\"tax-2026\"] to set exactly those, or [] to " +
        "clear them. Tags are freeform labels orthogonal to the single category; " +
        "unknown names are created on first use (case-insensitive). 'accountId' is the " +
        "register the tags are written from, because each side of a transaction carries " +
        "its own: on a one-posting transaction they are that account's, on a split the " +
        "whole transaction's. ALL transactionIds must be in the ledger, and each must be " +
        "one that account's register may edit — if any isn't (an id outside the ledger, " +
        "an account not on the transaction, a row that register shows read-only), the " +
        "whole call is rejected and nothing is tagged. dryRun=true reports, per " +
        "transaction, where the tags would land ('side' or 'transaction') or why not, " +
        "without writing. Resolve transaction ids via the register / activity tools and " +
        "tag names via list_tags.")]
    public static async Task<McpSetTransactionTagsResult> SetTransactionTags(
        McpWriteGuard guard,
        TransactionsRepository transactions,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Transaction (header) ids to tag — one or many.")] Guid[] transactionIds,
        [Description("The register account (GUID) the tags are written from.")] Guid accountId,
        [Description("The complete tag set to assign to each transaction; [] clears it.")] string[] tags,
        [Description("Preview only — report where the tags would land without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        if (transactionIds is null || transactionIds.Length == 0)
            return McpSetTransactionTagsResult.Fail("no-transactions");
        if (accountId == Guid.Empty)
            return McpSetTransactionTagsResult.Fail($"{SetTransactionTagsContract.AccountIdParameter}-required");

        var outcome = await transactions
            .SetTransactionTagsAsync(ledgerId, transactionIds, accountId, tags ?? Array.Empty<string>(), dryRun, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Result == TransactionsRepository.SetTagsResult.HeadersNotInLedger)
            return McpSetTransactionTagsResult.Fail(
                $"transactions-not-in-ledger: {string.Join(", ", outcome.UnknownHeaderIds)}");
        if (outcome.Result == TransactionsRepository.SetTagsResult.Refused)
        {
            var refused = outcome.Placements.Where(p => p.Refused is not null)
                .Select(p => $"{p.TransactionId} ({p.Refused})");
            return McpSetTransactionTagsResult.Fail(
                $"refused, nothing tagged: {string.Join(", ", refused)}", outcome.Placements);
        }

        var tagList = outcome.Tags.Count == 0 ? "(cleared)" : string.Join(", ", outcome.Tags);
        var verb = dryRun ? "would tag" : "tagged";
        return new McpSetTransactionTagsResult(
            true, dryRun, $"{verb} {outcome.HeaderCount} transaction(s) -> [{tagList}]",
            outcome.Tags, outcome.Placements, null);
    }

    // --- Tag lifecycle (Tags v1) — the tag-dictionary curation the category side
    // already has (merge / rename / delete). Wrap TagsRepository, guarded like the rest. ---

    [McpServerTool(Name = "rename_tag"), Description(
        "Rename and/or recolor ONE tag in place (Tags v1) - fix a typo or restyle so every " +
        "transaction carrying it updates at once (no re-tagging). newName renames; newColor " +
        "(hex like #3b82f6) recolors; pass either or both. A rename that collides with " +
        "another tag's name (case-insensitive) is rejected - use merge_tags to combine them " +
        "instead. dryRun previews before/after. Resolve the id via list_tags.")]
    public static async Task<McpWriteResult> RenameTag(
        McpWriteGuard guard,
        TagsRepository tags,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Tag id (GUID) from list_tags.")] Guid tagId,
        [Description("New name; null leaves the name unchanged (recolor-only).")] string? newName = null,
        [Description("New color as hex (e.g. #3b82f6); null leaves the color unchanged.")] string? newColor = null,
        [Description("Preview only - report before/after without persisting.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var name = Norm(newName);
        var color = Norm(newColor);
        if (name is null && color is null)
            return McpWriteResult.Fail(tagId, "nothing-to-change");

        var current = (await tags.ListWithUsageAsync(ledgerId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(t => t.Id == tagId);
        if (current is null) return McpWriteResult.Fail(tagId, "tag-not-in-ledger");

        static string Fmt(string n, string? c) => n + (string.IsNullOrEmpty(c) ? "" : $" [{c}]");
        var before = Fmt(current.Name, current.Color);
        var after = Fmt(name ?? current.Name, color ?? current.Color);
        if (dryRun) return new McpWriteResult(tagId, true, before, after, null);

        var result = await tags.PatchAsync(ledgerId, tagId, name, color, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            TagsRepository.PatchTagResult.Ok => new McpWriteResult(tagId, true, before, after, null),
            TagsRepository.PatchTagResult.NameExists => McpWriteResult.Fail(tagId, "name-exists (use merge_tags)"),
            _ => McpWriteResult.Fail(tagId, result.ToString()),
        };
    }

    [McpServerTool(Name = "merge_tags"), Description(
        "Merge ONE tag into another (Tags v1): repoint every assignment (transaction level " +
        "and per side) from the source tag to the target, then delete the source. Saved " +
        "reports filtered on the source are repointed to the target too, every member's " +
        "included; the result reports how many of those you can see. The lever for " +
        "collapsing duplicates / near-duplicates (e.g. 'reimburse' -> 'reimbursable'). One " +
        "pair per call. dryRun reports the source's current assignment count (an upper " +
        "bound - assignments already on the target are de-duped, not double-counted) " +
        "without writing; it does not count saved reports. Resolve ids via list_tags.")]
    public static async Task<McpWriteResult> MergeTags(
        McpWriteGuard guard,
        TagsRepository tags,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Source tag id (GUID) - merged away (deleted).")] Guid sourceTagId,
        [Description("Target tag id (GUID) - keeps everything.")] Guid intoTagId,
        [Description("Preview only - report source usage without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        if (sourceTagId == intoTagId) return McpWriteResult.Fail(sourceTagId, "merge-self");

        var all = await tags.ListWithUsageAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var source = all.FirstOrDefault(t => t.Id == sourceTagId);
        var target = all.FirstOrDefault(t => t.Id == intoTagId);
        if (source is null) return McpWriteResult.Fail(sourceTagId, "source-tag-not-in-ledger");
        if (target is null) return McpWriteResult.Fail(intoTagId, "target-tag-not-in-ledger");

        var before = $"'{source.Name}' ({source.UsageCount} assignments)";
        if (dryRun)
            return new McpWriteResult(sourceTagId, true, before, $"would merge into '{target.Name}'", null);

        var outcome = await tags.MergeAsync(ledgerId, sourceTagId, intoTagId, cancellationToken).ConfigureAwait(false);
        return outcome.Result == TagsRepository.MergeTagResult.Ok
            ? new McpWriteResult(sourceTagId, true, before,
                $"merged into '{target.Name}'; {outcome.TransactionsRepointed} repointed, "
                + $"{outcome.SavedReportsRepointed} saved report(s) now filter on it", null)
            : McpWriteResult.Fail(sourceTagId, outcome.Result.ToString());
    }

    [McpServerTool(Name = "delete_tag"), Description(
        "Delete ONE tag (Tags v1). Removes the tag and every assignment of it, at transaction " +
        "level and per side (the junctions cascade) - so deleting an in-use tag is allowed and " +
        "clears it everywhere, and deleting a 0-use tag cleans up a stray. A side left with no " +
        "tags of its own then shows the other side's as inherited. To keep the assignments " +
        "under a different label, use merge_tags instead. dryRun reports the tag + how many " +
        "assignments would be removed. Resolve the id via list_tags.")]
    public static async Task<McpWriteResult> DeleteTag(
        McpWriteGuard guard,
        TagsRepository tags,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Tag id (GUID) from list_tags.")] Guid tagId,
        [Description("Preview only - report the tag + affected count without deleting.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var current = (await tags.ListWithUsageAsync(ledgerId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(t => t.Id == tagId);
        if (current is null) return McpWriteResult.Fail(tagId, "tag-not-in-ledger");

        var before = $"'{current.Name}' ({current.UsageCount} assignments)";
        if (dryRun) return new McpWriteResult(tagId, true, before, "would delete", null);

        var result = await tags.DeleteAsync(ledgerId, tagId, cancellationToken).ConfigureAwait(false);
        return result == TagsRepository.DeleteTagResult.Ok
            ? new McpWriteResult(tagId, true, before, "deleted", null)
            : McpWriteResult.Fail(tagId, result.ToString());
    }

    [McpServerTool(Name = "cleanup_unused_tags"), Description(
        "Delete EVERY tag in the ledger with no assignments at all (usageCount 0 in list_tags; " +
        "Tags v1) - one-shot cleanup of strays left by prior untag / removals. A tag whose only " +
        "transactions are hidden, merged away or reminder templates, or whose tags a line's own " +
        "tags hide, still has assignments and is kept, even when its transactionCount is 0. " +
        "dryRun reports how many would be removed " +
        "(and their names) without deleting. Returns the count removed.")]
    public static async Task<McpWriteResult> CleanupUnusedTags(
        McpWriteGuard guard,
        TagsRepository tags,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Preview only - report the count / names without deleting.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        if (dryRun)
        {
            var unused = (await tags.ListWithUsageAsync(ledgerId, cancellationToken).ConfigureAwait(false))
                .Where(t => t.UsageCount == 0).Select(t => t.Name).ToList();
            return new McpWriteResult(Guid.Empty, true, null,
                unused.Count == 0 ? "no unused tags" : $"would remove {unused.Count}: {string.Join(", ", unused)}",
                null);
        }

        var removed = await tags.CleanupUnusedAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        return new McpWriteResult(Guid.Empty, true, null, $"removed {removed} unused tag(s)", null);
    }

    // --- Manual prices (ADR-0070) — the write side of price_history. Wrap the
    // SecuritiesRepository price methods; a hand-entered price is manual-owned. ---

    [McpServerTool(Name = "add_price"), Description(
        "Add or replace the price of ONE security on ONE date (ADR-0070) - a hand-entered " +
        "price OWNS its day: if a price already exists for that security+date (from any " +
        "source) it is replaced and marked manual (protected from automated-fetch " +
        "overwrites). close is the price (must be > 0); high / low / volume optional (high < " +
        "low is rejected). currency is the price's ISO 4217 code (e.g. EUR); omitted, it is " +
        "the security's latest price's currency, else the currency of the accounts holding " +
        "it when they all agree, else the call fails with security-price-currency-required " +
        "and must be repeated with a currency (ADR-0114 D5). A currency that is not three " +
        "characters once trimmed fails with security-price-currency-invalid. A pence (or other minor-unit) " +
        "figure is entered in its major unit: pounds, not pence. The result names the " +
        "currency used. dryRun previews without writing, including the currency it would " +
        "use. Resolve securityId via list_securities; read current prices via price_history.")]
    public static async Task<McpWriteResult> AddPrice(
        McpWriteGuard guard,
        SecuritiesRepository securities,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Security id (GUID) from list_securities.")] Guid securityId,
        [Description("Price date, e.g. 2026-07-21.")] DateTime date,
        [Description("The price / close (must be > 0), in the major unit of its currency.")] decimal close,
        [Description("ISO 4217 currency of the price, e.g. USD or EUR (optional; see the tool description for the default).")] string? currency = null,
        [Description("Intraday high (optional).")] decimal? high = null,
        [Description("Intraday low (optional).")] decimal? low = null,
        [Description("Volume (optional).")] long? volume = null,
        [Description("Preview only - report without writing.")] bool dryRun = false,
        MarketSeriesWriteTrigger? starts = null,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        var priceDate = DateOnly.FromDateTime(date);
        if (dryRun)
        {
            // The same currency the write would store, so a preview never shows one
            // currency and the write another.
            if (await securities.GetByIdAsync(ledgerId, securityId, today: null, cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return McpWriteResult.Fail(securityId, "security-not-in-ledger");
            }
            var (preview, previewCurrency) = await securities
                .ResolvePriceCurrencyAsync(ledgerId, securityId, currency, cancellationToken)
                .ConfigureAwait(false);
            return preview == SecuritiesRepository.AddPriceResult.Ok
                ? new McpWriteResult(securityId, true, null,
                    $"would set {priceDate:yyyy-MM-dd} close={close} {previewCurrency}", null)
                : McpWriteResult.Fail(securityId, AddPriceFailureCode(preview));
        }

        // ADR-0114 D11: a price can add a position pair or move one's start earlier.
        var before = starts is null ? null : await starts.ReadAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var outcome = await securities.AddPriceAsync(ledgerId, securityId, new CreateSecurityPriceRequest
        {
            Price = close,
            PriceDate = priceDate,
            CurrencyCode = currency,
            High = high,
            Low = low,
            Volume = volume,
        }, cancellationToken).ConfigureAwait(false);
        if (outcome.Kind == SecuritiesRepository.AddPriceResult.Ok && before is not null)
            await starts!.StartIfWidenedAsync(ledgerId, before, cancellationToken).ConfigureAwait(false);

        return outcome.Kind == SecuritiesRepository.AddPriceResult.Ok && outcome.PriceId is { } pid
            ? new McpWriteResult(pid, true, null, $"{priceDate:yyyy-MM-dd} close={close} {outcome.CurrencyCode}", null)
            : McpWriteResult.Fail(securityId, AddPriceFailureCode(outcome.Kind));
    }

    /// <summary>The error an <c>add_price</c> refusal reports: the REST code for the two
    /// currency refusals, which a caller must act on, else the result's name as before.</summary>
    private static string AddPriceFailureCode(SecuritiesRepository.AddPriceResult kind) => kind switch
    {
        SecuritiesRepository.AddPriceResult.CurrencyInvalid => Errors.BusinessError.Codes.SecurityPriceCurrencyInvalid,
        SecuritiesRepository.AddPriceResult.CurrencyRequired => Errors.BusinessError.Codes.SecurityPriceCurrencyRequired,
        _ => kind.ToString(),
    };

    [McpServerTool(Name = "update_price"), Description(
        "Edit ONE existing price point (ADR-0070) - correct a close / high / low / volume or " +
        "its date. Pass only the fields to change; null leaves a field alone. Editing marks " +
        "the row manual (protected from automated fetch). A date change that collides with " +
        "another price for the same security is rejected. dryRun previews. Resolve securityId " +
        "+ priceId via price_history.")]
    public static async Task<McpWriteResult> UpdatePrice(
        McpWriteGuard guard,
        SecuritiesRepository securities,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Security id (GUID) from price_history.")] Guid securityId,
        [Description("Price point id (GUID) from price_history.")] Guid priceId,
        [Description("New close / price (> 0); null leaves it.")] decimal? close = null,
        [Description("New high; null leaves it.")] decimal? high = null,
        [Description("New low; null leaves it.")] decimal? low = null,
        [Description("New volume; null leaves it.")] long? volume = null,
        [Description("New date, e.g. 2026-07-21; null leaves it.")] DateTime? date = null,
        [Description("Preview only.")] bool dryRun = false,
        MarketSeriesWriteTrigger? starts = null,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        if (close is null && high is null && low is null && volume is null && date is null)
            return McpWriteResult.Fail(priceId, "nothing-to-change");
        if (dryRun) return new McpWriteResult(priceId, true, null, "would update price point", null);

        // ADR-0114 D11: a price moved earlier can move its pair's start earlier.
        var before = starts is null ? null : await starts.ReadAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var result = await securities.PatchPriceAsync(ledgerId, securityId, priceId, new PatchSecurityPriceRequest
        {
            Price = close,
            High = high,
            Low = low,
            Volume = volume,
            PriceDate = date is { } d ? DateOnly.FromDateTime(d) : null,
        }, cancellationToken).ConfigureAwait(false);
        if (result == SecuritiesRepository.PatchPriceResult.Ok && before is not null)
            await starts!.StartIfWidenedAsync(ledgerId, before, cancellationToken).ConfigureAwait(false);

        return result == SecuritiesRepository.PatchPriceResult.Ok
            ? new McpWriteResult(priceId, true, null, "updated", null)
            : McpWriteResult.Fail(priceId, result.ToString());
    }

    [McpServerTool(Name = "delete_price"), Description(
        "Delete ONE price point (ADR-0070). Removes just that security+date price; other " +
        "dates are untouched. dryRun previews. Resolve securityId + priceId via price_history.")]
    public static async Task<McpWriteResult> DeletePrice(
        McpWriteGuard guard,
        SecuritiesRepository securities,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Security id (GUID) from price_history.")] Guid securityId,
        [Description("Price point id (GUID) from price_history.")] Guid priceId,
        [Description("Preview only.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        guard.EnsureWritable();

        if (dryRun) return new McpWriteResult(priceId, true, null, "would delete price point", null);

        var result = await securities.DeletePriceAsync(ledgerId, securityId, priceId, cancellationToken).ConfigureAwait(false);
        return result == SecuritiesRepository.DeletePriceResult.Ok
            ? new McpWriteResult(priceId, true, null, "deleted", null)
            : McpWriteResult.Fail(priceId, result.ToString());
    }

    private static string FmtSecurity(SecurityDetailDto s) =>
        $"ticker={s.Ticker ?? "-"} cusip={s.Cusip ?? "-"} name={s.Name}";

    private static string FmtClassification(SecurityDetailDto s) => FmtClassification(
        s.AssetClass, s.VehicleType, s.Region, s.EquitySize, s.EquityStyle,
        s.FiDuration, s.FiCredit, s.TaxCharacter);

    private static string FmtClassification(
        string? assetClass, string? vehicleType, string? region, string? equitySize,
        string? equityStyle, string? fiDuration, string? fiCredit, string? taxCharacter) =>
        $"asset={assetClass ?? "-"} vehicle={vehicleType ?? "-"} region={region ?? "-"} " +
        $"size={equitySize ?? "-"} style={equityStyle ?? "-"} dur={fiDuration ?? "-"} " +
        $"credit={fiCredit ?? "-"} tax={taxCharacter ?? "-"}";
}

/// <summary>Outcome of a single-entity write primitive (ADR-0068): the id, whether
/// it succeeded, the value before/after, and an error code when it didn't.</summary>
public sealed record McpWriteResult(
    Guid Id, bool Ok, string? Before, string? After, string? Error)
{
    public static McpWriteResult Fail(Guid id, string error) => new(id, false, null, null, error);
}

/// <summary>Outcome of <c>set_transaction_tags</c>: a summary, the tag set, and per
/// transaction where the tags land (<c>side</c> or <c>transaction</c>) or the reason
/// they would not, which on a refusal names every transaction that refused the call.</summary>
public sealed record McpSetTransactionTagsResult(
    bool Ok, bool DryRun, string? After, IReadOnlyList<string> Tags,
    IReadOnlyList<SetTransactionTagsPlacement> Placements, string? Error)
{
    public static McpSetTransactionTagsResult Fail(string error, IReadOnlyList<SetTransactionTagsPlacement>? placements = null) =>
        new(false, false, null, [], placements ?? [], error);
}

/// <summary>One header a bulk recategorize could not move (ADR-0068): its id + why
/// (split / transfer / investment-transaction / not-in-ledger).</summary>
public sealed record McpRecategorizeReject(Guid HeaderId, string Reason);

/// <summary>Outcome of the bulk <c>set_transaction_category</c> (ADR-0068): how many
/// were recategorized / already-correct, the target category name, and the per-header
/// rejects. A batch-level failure (bad target / empty list) sets <see cref="Ok"/> false
/// with <see cref="Error"/>.</summary>
public sealed record McpBulkRecategorizeResult(
    bool Ok, int Recategorized, int Unchanged, string? Category,
    IReadOnlyList<McpRecategorizeReject> Rejects, bool DryRun, string? Error)
{
    public static McpBulkRecategorizeResult Fail(string error) =>
        new(false, 0, 0, null, Array.Empty<McpRecategorizeReject>(), false, error);
}

/// <summary>Outcome of the bulk <c>set_split_posting_category</c> (ADR-0068 slice E):
/// how many postings were (or would be) moved, how many headers were already correct
/// (no fromCategory posting to move / from == to), the target category name, and the
/// per-header rejects. A batch-level failure (bad target / empty list) sets
/// <see cref="Ok"/> false with <see cref="Error"/> — mirrors
/// <see cref="McpBulkRecategorizeResult"/>.</summary>
public sealed record McpBulkSplitPostingResult(
    bool Ok, int Moved, int Unchanged, string? Category,
    IReadOnlyList<McpRecategorizeReject> Rejects, bool DryRun, string? Error)
{
    public static McpBulkSplitPostingResult Fail(string error) =>
        new(false, 0, 0, null, Array.Empty<McpRecategorizeReject>(), false, error);
}
