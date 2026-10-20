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
/// brokerage (ADR-0019).
/// </summary>
public sealed class AccountImportStep
{
    private readonly AccountsRepository _repository;
    private readonly LoanTermsRepository _loanTerms;

    public AccountImportStep(AccountsRepository repository, LoanTermsRepository loanTerms)
    {
        _repository = repository;
        _loanTerms = loanTerms;
    }

    public async Task<ImportStepResult> ExecuteAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var inputs = AccountMapper.ComputeInputs(context.Export);

        // Pass 1: build candidate rows + classify the skips for the summary.
        var candidates = new List<(MdAcct Md, AccountRow Row)>();
        var read = 0;
        var skipped = 0;

        foreach (var item in context.Export.AllItems)
        {
            if (item.ObjType != "acct") continue;
            read++;

            var md = MdAcct.From(item);
            var result = AccountMapper.Map(md, inputs, context.LedgerId);
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
                OfxImportAcctNum: md.OfxImportAcctNum);
            if (inserted)
            {
                written++;
                insertedMdIds.Add(md.Id);
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

            var terms = LoanMapper.Map(loanRef.Id, context.LedgerId, loan, interestId, escrowId);
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
            new AccountsRepository(connection), new LoanTermsRepository(connection));
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
