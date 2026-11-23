using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Second step of the import pipeline: import every <c>acct</c> row that
/// belongs in <c>accounts</c>. Filters per ADR-0016 (root, security
/// sub-accounts, fake non-category placeholders) and writes in three
/// passes so category hierarchy can be wired up after every row exists,
/// and a system-managed Holdings sibling can be ensured for every
/// brokerage (ADR-0019). Moneydance's blank-named top-level leaf categories
/// resolve to the ledger's system Uncategorized instead of rows of their own.
/// </summary>
public sealed class AccountImportStep
{
    private readonly AccountsRepository _repository;
    private readonly LoanTermsRepository _loanTerms;
    private readonly LedgersRepository _ledgers;

    public AccountImportStep(AccountsRepository repository, LoanTermsRepository loanTerms, LedgersRepository ledgers)
    {
        _repository = repository;
        _loanTerms = loanTerms;
        _ledgers = ledgers;
    }

    public async Task<ImportStepResult> ExecuteAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var inputs = AccountMapper.ComputeInputs(context.Export);
        // ADR-0114 D7: a category has no currency of its own, so it takes the ledger's.
        var reportingCurrency = await _ledgers
            .CurrencyOfAsync(context.LedgerId, cancellationToken).ConfigureAwait(false);

        // Pass 1: build candidate rows + classify the skips for the summary.
        // Moneydance's blank-named top-level leaf categories (its
        // "uncategorized") are set aside: they resolve to the ledger's system
        // Uncategorized below rather than each becoming an "(unnamed)" category
        // of its own.
        var candidates = new List<(MdAcct Md, AccountRow Row)>();
        var blankLeafCategories = new List<MdAcct>();
        var read = 0;
        var skipped = 0;

        foreach (var item in context.Export.AllItems)
        {
            if (item.ObjType != "acct") continue;
            read++;

            var md = MdAcct.From(item);
            if (AccountMapper.IsBlankLeafCategory(md, inputs))
            {
                blankLeafCategories.Add(md);
                continue;
            }
            var result = AccountMapper.Map(md, inputs, context.LedgerId, context.Currencies, reportingCurrency);
            if (result.Row is null)
            {
                skipped++;
                continue;
            }
            candidates.Add((md, result.Row));
        }

        // Pass 2: seed new accounts (ADR-0050 D10). Existing accounts are
        // Coffer-owned — UpsertWithAdoptionAsync leaves their metadata untouched
        // and reports inserted=false. Track freshly-inserted MD ids so the
        // category parent-wiring pass only touches new rows.
        var written = 0;
        var insertedMdIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (md, row) in candidates)
        {
            var (persistedId, inserted) = await _repository
                .UpsertWithAdoptionAsync(row, source: "moneydance", cancellationToken)
                .ConfigureAwait(false);
            context.AccountByMdId[md.Id] = new AccountRef(
                persistedId,
                row.AccountType,
                HoldingsAccountId: null,
                OlbFi: md.OlbFi,
                OfxImportAcctNum: md.OfxImportAcctNum,
                // The places its Moneydance amounts are stored at (its MD currency's dec).
                AmountDecimals: context.Currencies.RequireAccount(md).Decimals);
            if (inserted)
            {
                written++;
                insertedMdIds.Add(md.Id);
            }
        }

        // Pass 2b: every blank leaf category resolves to ONE account, the
        // ledger's system Uncategorized (reused when a bank feed already made
        // it, created otherwise), so each split that pointed at one lands there.
        // It is never re-parented or given MD's junction row: it is the
        // Coffer-native row bank feeds use, not a Moneydance account. Like the
        // Holdings siblings below, a created one is not counted in Written,
        // which counts Moneydance accounts written.
        if (blankLeafCategories.Count > 0)
        {
            var (uncategorizedId, _) = await _repository
                .EnsureUncategorizedAsync(context.LedgerId, reportingCurrency, cancellationToken)
                .ConfigureAwait(false);
            foreach (var md in blankLeafCategories)
            {
                context.AccountByMdId[md.Id] = new AccountRef(uncategorizedId, "category",
                    AmountDecimals: context.Currencies.RequireAccount(md).Decimals);
                context.MdIdsOnUncategorized.Add(md.Id);
            }
        }

        // Pass 3: wire category hierarchy for freshly-inserted categories only
        // (seed-only, ADR-0050 D10 — re-import must not re-parent an existing,
        // Coffer-owned category). Only categories may have parent_id; a
        // non-category's MD parent (if any) is intentionally not preserved.
        foreach (var (md, row) in candidates)
        {
            if (row.AccountType != "category") continue;
            if (!insertedMdIds.Contains(md.Id)) continue;
            if (md.ParentId is null) continue;
            if (!context.AccountByMdId.TryGetValue(md.ParentId, out var parentRef)) continue;

            await _repository.UpdateParentByExternalIdAsync(context.LedgerId, md.Id, parentRef.Id, cancellationToken)
                             .ConfigureAwait(false);
        }

        // Pass 4: every investment (brokerage) account gets a system-managed
        // Holdings sibling that hosts the holdings-side legs of investment
        // transactions (ADR-0019). Idempotent: re-runs reuse the existing
        // sibling. The sibling's id rides on AccountRef so the investment
        // mapper can target it without a second DB round-trip.
        foreach (var (md, row) in candidates)
        {
            if (row.AccountType != "investment") continue;
            if (!context.AccountByMdId.TryGetValue(md.Id, out var brokerageRef)) continue;

            var holdingsId = await _repository.EnsureHoldingsSiblingAsync(
                brokerageRef.Id, row.Name, row.CurrencyCode, context.LedgerId, cancellationToken).ConfigureAwait(false);
            context.AccountByMdId[md.Id] = brokerageRef with { HoldingsAccountId = holdingsId };
        }

        // Pass 5: seed loan_terms for loan accounts (ADR-0050 D10, seed-only).
        // Resolve the interest/escrow MD account uuids to Coffer ids via the map
        // built in pass 2; skip a loan whose MD data lacks a usable amortization.
        foreach (var (md, row) in candidates)
        {
            if (row.AccountType != "loan" || md.Loan is not { } loan) continue;
            if (!context.AccountByMdId.TryGetValue(md.Id, out var loanRef)) continue;

            Guid? interestId = loan.InterestAccountMdId is { } iid
                && context.AccountByMdId.TryGetValue(iid, out var interestRef) ? interestRef.Id : null;
            Guid? escrowId = loan.EscrowAccountMdId is { } eid
                && context.AccountByMdId.TryGetValue(eid, out var escrowRef) ? escrowRef.Id : null;

            // The same resolved currency MdCurrencies.Problems checked the terms at.
            var terms = LoanMapper.Map(loanRef.Id, context.LedgerId, loan, interestId, escrowId,
                currency: context.Currencies.RequireAccount(md));
            if (terms is null) continue;
            await _loanTerms.SeedAsync(terms, cancellationToken).ConfigureAwait(false);
        }

        return new ImportStepResult(StepName: "accounts", Read: read, Written: written, Skipped: skipped);
    }

    public static async Task<ImportStepResult> RunAsync(
        NpgsqlConnection connection,
        ImportContext context,
        CancellationToken cancellationToken = default)
    {
        var step = new AccountImportStep(
            new AccountsRepository(connection), new LoanTermsRepository(connection), new LedgersRepository(connection));
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
