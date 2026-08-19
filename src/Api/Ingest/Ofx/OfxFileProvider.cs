using System.Globalization;

using OfxNet;
using OfxNet.Investments;
using OfxNet.Investments.Securities;
using OfxNet.Investments.Transactions;

namespace Coffer.Api.Ingest.Ofx;

/// <summary>
/// <see cref="IFileProvider"/> for OFX 1.x SGML and 2.x XML
/// uploads, including Intuit's QFX dialect (which is OFX with an
/// <c>INTU.BID</c> header). ADR-0031 Phase 4.
/// </summary>
/// <remarks>
/// <para>Slice 2 adds investment (<c>INVSTMTMSGSRSV1</c>): the
/// supported subset of OFX investment transaction types maps to
/// ADR-0027 actions (<c>buy</c> / <c>sell</c> / <c>dividend_cash</c> /
/// <c>dividend_reinvest</c> / <c>misc</c>). Unsupported types
/// (share-only transfers, journals, splits, options) surface as
/// preview warnings and are not inserted. <c>SECLIST</c> entries
/// in the same file resolve CUSIP/ISIN → ticker so
/// <see cref="IngestedTransaction.SecurityTickerHint"/> carries a
/// human-readable symbol when one is available.</para>
///
/// <para><b>Parser choice.</b> Delegates to <c>OfxNet</c> 1.8.1
/// (MIT, jim-dale/BankingTools) for the 1.x-SGML vs 2.x-XML dialect
/// handling. The library is wrapped behind this thin adapter — if it
/// ever stalls, MIT lets us vendor the source directly under
/// <c>src/OfxNet/</c>; the rest of the codebase only sees the
/// <see cref="IFileProvider"/> contract.</para>
///
/// <para><b>Multi-account files.</b> A single OFX file commonly
/// carries multiple accounts (Eastbank's exports typically bundle
/// checking + savings + a card). Every parsed
/// <see cref="IngestedTransaction"/> is tagged with the composite
/// <see cref="IngestedTransaction.ProviderAccountId"/> so the
/// orchestrator can dispatch rows to the right Coffer account based
/// on the user's confirmed mapping.</para>
///
/// <para><b>FITID semantics.</b> The OFX <c>FITID</c> lands on both
/// <c>txn_headers.external_id</c> (the universal provider id per mig
/// 105) AND <c>txn_headers.online_match_fitid</c> (the OFX-protocol
/// FITID — populated natively here since QFX/OFX are the
/// originators of that concept). <c>BANKID</c> lands on
/// <c>online_match_fi_id</c>. Together this lets future MD-imported
/// rows that preserved an OFX FITID dedup against incoming
/// QFX/OFX rows for the same bank — the cross-source case captured
/// in <c>docs/follow-ups.md</c>.</para>
/// </remarks>
public sealed class OfxFileProvider : IFileProvider
{
    public const string Key = "ofx";

    public string ProviderKey => Key;

    /// <summary>
    /// Composite key shape for <see cref="DiscoveredFileAccount.ProviderAccountId"/>
    /// and <see cref="IngestedTransaction.ProviderAccountId"/>:
    /// <c>{BANKID}:{ACCTID}</c> for bank rows, <c>card:{ACCTID}</c>
    /// for credit cards, <c>inv:{BROKERID}:{ACCTID}</c> for
    /// investments. Treated as opaque by orchestrator + SPA;
    /// only this provider constructs it.
    /// </summary>
    internal static string BankKey(string bankId, string acctId)
        => $"{bankId}:{acctId}";
    internal static string CardKey(string acctId)
        => $"card:{acctId}";
    internal static string InvestmentKey(string brokerId, string acctId)
        => $"inv:{brokerId}:{acctId}";

    public Task<FileResult> ParseAsync(
        Stream payload,
        FileIngestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);

        // OfxDocument.Load is synchronous + buffers internally. File
        // size cap is enforced at the endpoint layer (a few MB
        // ceiling); inline parse on the request thread is fine.
        OfxDocument doc;
        try
        {
            doc = OfxDocument.Load(payload);
        }
        catch (Exception ex) when (ex is OfxException
                                   || ex is System.Xml.XmlException
                                   || ex is System.IO.InvalidDataException
                                   || ex is FormatException)
        {
            // OfxNet's parse path can throw any of these depending on
            // how the file fails: OfxException for OFX-specific
            // shape problems, XmlException for malformed 2.x XML,
            // InvalidDataException for SGML structure faults,
            // FormatException for bad date/decimal fields in the
            // header. All are user-input parse errors → 422 at the
            // endpoint layer.
            throw new InvalidOperationException(
                "Failed to parse uploaded file as OFX/QFX. " + ex.Message, ex);
        }

        var root = doc.GetRoot();
        if (root is null)
        {
            throw new InvalidOperationException(
                "OFX/QFX file parsed to an empty root. The file may be empty or use an unsupported dialect.");
        }

        var transactions = new List<IngestedTransaction>();
        var discovered = new List<DiscoveredFileAccount>();
        var errors = new List<IngestError>();

        // OfxDocument.GetStatements() yields the base OfxStatement
        // type; the actual instances are the typed subclasses
        // (OfxBankStatement / OfxCreditCardStatement /
        // OfxInvestmentStatement). Pattern-match to dispatch — same
        // shape used by SimpleFinPullProvider's transaction
        // classification. The single-arg GetBankStatements overload
        // also widens its return type to OfxStatement (lib API
        // quirk); pattern-match cleaner than casting.
        // GetStatements() yields bank + credit-card statements only
        // (typed as the OfxStatement base; subclasses match via
        // pattern). Investment statements live in a sibling type
        // hierarchy (OfxNet.Investments) and need their own
        // iteration via GetInvestmentStatements().
        foreach (var stmt in doc.GetStatements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (stmt)
            {
                case OfxBankStatement bank:
                    ProcessBankStatement(bank, transactions, discovered, errors, cancellationToken);
                    break;
                case OfxCreditCardStatement card:
                    ProcessCreditCardStatement(card, transactions, discovered, errors, cancellationToken);
                    break;
            }
        }
        // Slice 2: investment statements. CUSIP/ISIN → ticker
        // resolution is per-document (the SECLIST block sits above
        // the per-statement transaction lists), so the index is
        // built once and shared across all investment statements
        // in the same upload.
        var secListIndex = BuildSecListIndex(doc);
        foreach (var inv in doc.GetInvestmentStatements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessInvestmentStatement(
                inv, secListIndex, transactions, discovered, errors, cancellationToken);
        }

        return Task.FromResult(new FileResult(
            Transactions: transactions,
            DiscoveredAccounts: discovered,
            Errors: errors));
    }

    private static void ProcessBankStatement(
        OfxBankStatement stmt,
        List<IngestedTransaction> transactions,
        List<DiscoveredFileAccount> discovered,
        List<IngestError> errors,
        CancellationToken cancellationToken)
    {
        var acct = stmt.Account;
        if (acct is null
            || string.IsNullOrWhiteSpace(acct.BankId)
            || string.IsNullOrWhiteSpace(acct.AccountNumber))
        {
            errors.Add(new IngestError(
                Code: "ofx_bank_account_missing",
                Message: "Bank statement is missing BANKID or ACCTID; skipped.",
                ConnectionId: null,
                AccountId: null));
            return;
        }
        var providerKey = BankKey(acct.BankId, acct.AccountNumber);
        var txList = stmt.TransactionList?.Transactions ?? new List<OfxStatementTransaction>();
        foreach (var txn in txList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mapped = MapTransaction(txn, providerKey, fiId: acct.BankId);
            if (mapped is not null) transactions.Add(mapped);
        }
        discovered.Add(new DiscoveredFileAccount(
            ProviderAccountId: providerKey,
            AccountType: "bank",
            Currency: stmt.DefaultCurrency,
            TransactionCount: txList.Count));
    }

    private static void ProcessCreditCardStatement(
        OfxCreditCardStatement stmt,
        List<IngestedTransaction> transactions,
        List<DiscoveredFileAccount> discovered,
        List<IngestError> errors,
        CancellationToken cancellationToken)
    {
        var acct = stmt.Account;
        if (acct is null || string.IsNullOrWhiteSpace(acct.AccountNumber))
        {
            errors.Add(new IngestError(
                Code: "ofx_card_account_missing",
                Message: "Credit card statement is missing ACCTID; skipped.",
                ConnectionId: null,
                AccountId: null));
            return;
        }
        var providerKey = CardKey(acct.AccountNumber);
        var txList = stmt.TransactionList?.Transactions ?? new List<OfxStatementTransaction>();
        foreach (var txn in txList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // OFX credit-card statements don't carry a BANKID
            // equivalent; the FI id stays null on these rows.
            var mapped = MapTransaction(txn, providerKey, fiId: null);
            if (mapped is not null) transactions.Add(mapped);
        }
        discovered.Add(new DiscoveredFileAccount(
            ProviderAccountId: providerKey,
            AccountType: "credit_card",
            Currency: stmt.DefaultCurrency,
            TransactionCount: txList.Count));
    }

    private static void ProcessInvestmentStatement(
        OfxInvestmentStatement stmt,
        IReadOnlyDictionary<string, OfxSecurity> secListIndex,
        List<IngestedTransaction> transactions,
        List<DiscoveredFileAccount> discovered,
        List<IngestError> errors,
        CancellationToken cancellationToken)
    {
        var acct = stmt.Account;
        if (acct is null || string.IsNullOrWhiteSpace(acct.AccountNumber))
        {
            errors.Add(new IngestError(
                Code: "ofx_investment_account_missing",
                Message: "Investment statement is missing BROKERID or ACCTID; skipped.",
                ConnectionId: null,
                AccountId: null));
            return;
        }
        var brokerId = acct.BrokerId ?? string.Empty;
        var providerKey = InvestmentKey(brokerId, acct.AccountNumber);
        var txList = stmt.Transactions;
        var inserted = 0;
        // INVTRANLIST splits its children into two collections:
        // BankTransactions (INVBANKTRAN — cash deposits / withdrawals
        // in the brokerage cash sub-account) and InvestmentTransactions
        // (the typed investment rows: BUYSTOCK, SELLMF, INCOME, etc.).
        // Walk both; the mapper dispatches by runtime type.
        if (txList is not null)
        {
            // INVBANKTRAN rows are wire-shape OfxStatementTransaction
            // wrapped in OfxInvestmentBankTransaction — NOT a subclass
            // of OfxInvestmentTransaction (separate hierarchy). They
            // carry FitId + Name/Memo + Amount and route through the
            // bank-shape mapper unchanged.
            foreach (var txn in txList.BankTransactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mapped = MapTransaction(txn, providerKey, fiId: brokerId);
                if (mapped is not null)
                {
                    transactions.Add(mapped);
                    inserted++;
                }
            }
            foreach (var txn in txList.InvestmentTransactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mapped = MapInvestmentTransaction(
                    txn, providerKey, fiId: brokerId, secListIndex, errors);
                if (mapped is not null)
                {
                    transactions.Add(mapped);
                    inserted++;
                }
            }
        }
        // TransactionCount reflects how many rows the orchestrator
        // will actually try to insert (skipped types subtract from
        // the count so the SPA's "import N txns" button is accurate
        // for this slice's supported subset).
        discovered.Add(new DiscoveredFileAccount(
            ProviderAccountId: providerKey,
            AccountType: "investment",
            Currency: stmt.DefaultCurrency,
            TransactionCount: inserted));
    }

    /// <summary>
    /// Build a (id, idType) → <see cref="OfxSecurity"/> lookup from
    /// the document's <c>SECLIST</c>. Investment transactions reference
    /// securities by (UNIQUEID, UNIQUEIDTYPE) — usually CUSIP — and
    /// SECLIST is the only place in the file where that key resolves
    /// to a human-readable ticker / name. The dictionary key normalises
    /// idType to upper-case so subsequent lookups are case-insensitive.
    /// </summary>
    internal static IReadOnlyDictionary<string, OfxSecurity> BuildSecListIndex(OfxDocument doc)
    {
        var index = new Dictionary<string, OfxSecurity>(StringComparer.Ordinal);
        foreach (var sec in doc.GetSecurities())
        {
            if (string.IsNullOrWhiteSpace(sec.Id) || string.IsNullOrWhiteSpace(sec.IdType))
                continue;
            // Last entry wins on duplicate (id, idType) — OFX doesn't
            // forbid duplicates but real-world files don't ship them.
            index[SecListKey(sec.Id, sec.IdType)] = sec;
        }
        return index;
    }

    private static string SecListKey(string id, string idType)
        => $"{idType.ToUpperInvariant()}:{id}";

    /// <summary>
    /// Map a single OFX investment transaction to an
    /// <see cref="IngestedTransaction"/>. Returns null when the row
    /// is a type slice 2 does not yet support — the caller surfaces
    /// a warning in the preview so the user knows what didn't import.
    /// </summary>
    /// <remarks>
    /// Supported types per ADR-0027 action catalog:
    /// <list type="bullet">
    ///   <item><c>BUYSTOCK</c> / <c>BUYMF</c> / <c>BUYDEBT</c> / <c>BUYOTHER</c> → <c>buy</c></item>
    ///   <item><c>SELLSTOCK</c> / <c>SELLMF</c> / <c>SELLDEBT</c> / <c>SELLOTHER</c> → <c>sell</c></item>
    ///   <item><c>REINVEST</c> → <c>dividend_reinvest</c></item>
    ///   <item><c>INCOME</c> (DIV / INTEREST / CGSHORT / CGLONG) → <c>dividend_cash</c></item>
    ///   <item><c>RETOFCAP</c> → <c>dividend_cash</c> (return of capital as cash distribution)</item>
    ///   <item><c>INVEXPENSE</c> / <c>MARGININTEREST</c> → <c>misc</c> (expense)</item>
    ///   <item><c>INVBANKTRAN</c> → bank-shape (cash-only; uses statement-transaction map)</item>
    /// </list>
    /// Skipped (with preview warning):
    /// <list type="bullet">
    ///   <item><c>TRANSFER</c>, <c>JRNLSEC</c>, <c>JRNLFUND</c> — share-only / inter-subaccount moves</item>
    ///   <item><c>CLOSUREOPT</c> — options (ADR-0027 declined)</item>
    ///   <item><c>SPLIT</c> — stock splits route through the security-splits surface, not the txn editor</item>
    /// </list>
    /// <c>BUYOPT</c> / <c>SELLOPT</c> are the exception ADR-0027's
    /// "options declined" wording doesn't cover: OfxNet models them as
    /// <c>OfxBuyInvestment</c> / <c>OfxSellInvestment</c> subclasses,
    /// so the buy/sell arms below claim them before any options check
    /// could run, and they import as plain buys/sells. Left as-is —
    /// no option data has been seen in a real file, and changing it
    /// is an ADR-0027 amendment, not a mapper tweak.
    /// </remarks>
    private static IngestedTransaction? MapInvestmentTransaction(
        OfxInvestmentTransaction txn,
        string providerAccountId,
        string? fiId,
        IReadOnlyDictionary<string, OfxSecurity> secListIndex,
        List<IngestError> errors)
    {
        // OfxInvestmentBankTransaction lives in a separate hierarchy
        // (it extends OfxStatementTransaction); the caller iterates
        // INVTRANLIST.BankTransactions through the bank-shape mapper
        // directly. This method only sees the investment-shape
        // hierarchy rooted at OfxInvestmentTransaction.
        if (string.IsNullOrWhiteSpace(txn.InstitutionId)) return null;

        var (action, securityRef) = ClassifyInvestmentTransaction(txn);
        if (action is null)
        {
            // Skipped type — surface a warning the SPA preview shows
            // so the user knows N rows weren't imported and why.
            errors.Add(new IngestError(
                Code: "ofx_investment_type_unsupported",
                Message: DescribeUnsupported(txn, secListIndex),
                ConnectionId: null,
                AccountId: null));
            return null;
        }

        var ticker = ResolveTicker(securityRef, secListIndex);

        // OFX investment rows don't carry a payee-vs-memo split like
        // bank rows do — there's only MEMO + the security identity.
        // Use the security's resolved name as the payee fallback so
        // the register's Payee column shows something meaningful;
        // MEMO carries the optional free-form text from the file.
        string? payee = null;
        if (securityRef is not null
            && secListIndex.TryGetValue(SecListKey(securityRef.Id, securityRef.IdType), out var sec))
        {
            payee = NullIfEmpty(sec.Name) ?? NullIfEmpty(sec.Ticker);
        }
        if (payee is null && ticker is not null)
            payee = ticker;

        return new IngestedTransaction(
            ExternalId: txn.InstitutionId,
            PostedAt: txn.TradeDate.UtcDateTime,
            // SettlementDate populates TransactedAt only when it
            // actually differs from TradeDate — mirrors the bank
            // path's posted/transacted distinction.
            TransactedAt: txn.SettlementDate is { } settle
                && settle.UtcDateTime != txn.TradeDate.UtcDateTime
                ? settle.UtcDateTime
                : null,
            Amount: ExtractAmount(txn),
            Payee: payee,
            Description: NullIfEmpty(txn.Memo),
            Pending: false,
            Action: action,
            SecurityTickerHint: ticker,
            RawProviderPayload: null,
            ProviderAccountId: providerAccountId,
            OnlineMatchFiId: fiId,
            // OFX FITID lands on the OFX-protocol online_match_fitid
            // column too (== ExternalId here) — the cross-source dedup
            // substrate. Only the OFX provider populates it.
            OnlineMatchFitid: txn.InstitutionId,
            Shares: ExtractShares(txn),
            UnitPrice: ExtractUnitPrice(txn),
            Fee: ExtractFee(txn));
    }

    /// <summary>
    /// Build the preview warning for a row whose OFX aggregate this
    /// slice doesn't import. The user has to find the row on their
    /// statement to judge whether the skip matters, so the message
    /// leads with the wire tag and the row's identity — security,
    /// units, trade date. It deliberately drops two things the old
    /// message led with: the OfxNet runtime class name, which is an
    /// implementation detail no statement shows, and the FITID, which
    /// some institutions assemble out of a hundred characters of
    /// concatenated internal keys (a 401(k) recordkeeper in the wild
    /// emits contract number + amount + date + CUSIP + units + price
    /// + a DB2 timestamp) and which is unreadable in a preview list.
    /// </summary>
    private static string DescribeUnsupported(
        OfxInvestmentTransaction txn,
        IReadOnlyDictionary<string, OfxSecurity> secListIndex)
    {
        var (tag, reason) = UnsupportedKind(txn);

        var identity = new List<string>(3);
        if (ResolveTicker(UnsupportedSecurity(txn), secListIndex) is { } security)
            identity.Add(security);
        if (UnsupportedUnits(txn) is { } units)
        {
            // 12dp matches txn_legs.quantity's scale; the format trims
            // trailing zeros so whole-share rows stay readable.
            identity.Add($"{units.ToString("0.############", CultureInfo.InvariantCulture)} units");
        }
        identity.Add(txn.TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        return $"OFX {tag} row skipped ({string.Join(", ", identity)}). {reason}";
    }

    /// <summary>
    /// The OFX wire tag and the human reason this slice skips it, for
    /// each aggregate <see cref="ClassifyInvestmentTransaction"/>
    /// leaves unclassified. Note that <c>BUYOPT</c> / <c>SELLOPT</c>
    /// are NOT here: OfxNet models them as
    /// <c>OfxBuyInvestment</c> / <c>OfxSellInvestment</c> subclasses,
    /// so the classifier's buy/sell arms already claim them.
    /// </summary>
    private static (string Tag, string Reason) UnsupportedKind(
        OfxInvestmentTransaction txn) => txn switch
    {
        OfxTransfer transfer => (
            transfer.TransferAction is { Length: > 0 } direction
                ? $"TRANSFER ({direction})"
                : "TRANSFER",
            "Share-only moves aren't imported in this slice."),
        OfxJournalSecurity => (
            "JRNLSEC",
            "Share-only moves between sub-accounts aren't imported in this slice."),
        OfxJournalFund => (
            "JRNLFUND",
            "Cash moves between sub-accounts aren't imported in this slice."),
        OfxOptionClosure => (
            "CLOSUREOPT",
            "Options are outside the ADR-0027 action catalog."),
        OfxSplit => (
            "SPLIT",
            "Stock splits are recorded on the security, not in the register."),
        _ => (
            txn.GetType().Name,
            "This OFX aggregate is outside the ADR-0027 action catalog."),
    };

    /// <summary>
    /// Security reference on a skipped row, for the warning text.
    /// Separate from <see cref="ClassifyInvestmentTransaction"/>,
    /// which returns null for these types precisely because they
    /// produce no transaction.
    /// </summary>
    private static OfxSecurityId? UnsupportedSecurity(
        OfxInvestmentTransaction txn) => txn switch
    {
        OfxTransfer transfer      => transfer.Security,
        OfxJournalSecurity jrnl   => jrnl.Security,
        OfxOptionClosure closure  => closure.Security,
        OfxSplit split            => split.Security,
        _                         => null,
    };

    /// <summary>
    /// Share count on a skipped row, for the warning text. Null for
    /// the aggregates that carry no <c>UNITS</c> (JRNLFUND is a cash
    /// move; SPLIT carries ratio fields instead).
    /// </summary>
    private static decimal? UnsupportedUnits(
        OfxInvestmentTransaction txn) => txn switch
    {
        OfxTransfer transfer      => transfer.Units,
        OfxJournalSecurity jrnl   => jrnl.Units,
        OfxOptionClosure closure  => closure.Units,
        _                         => null,
    };

    /// <summary>
    /// Classify an OFX investment transaction into an ADR-0027
    /// action string. Returns <c>(null, null)</c> when the type is
    /// not supported in this slice — the caller surfaces a warning
    /// and skips the row.
    /// </summary>
    private static (string? action, OfxSecurityId? security) ClassifyInvestmentTransaction(
        OfxInvestmentTransaction txn)
    {
        return txn switch
        {
            OfxBuyInvestment buy       => ("buy",                buy.Security),
            OfxSellInvestment sell     => ("sell",               sell.Security),
            OfxReinvest reinvest       => ("dividend_reinvest",  reinvest.Security),
            OfxIncome income           => ("dividend_cash",      income.Security),
            OfxCapitalReturn retCap    => ("dividend_cash",      retCap.Security),
            OfxInvestmentExpense exp   => ("misc",               exp.Security),
            OfxMarginInterest          => ("misc",               null),
            _                          => (null,                 null),
        };
    }

    /// <summary>
    /// Resolve a security identifier to a human-readable ticker via
    /// the SECLIST index. Preference order: matching SECLIST entry's
    /// <c>TICKER</c>, then its <c>FIID</c>, then the raw
    /// (id, idType) as a last-resort opaque key. Returns null when
    /// the input is null (e.g. <c>MARGININTEREST</c> has no security).
    /// </summary>
    private static string? ResolveTicker(
        OfxSecurityId? securityRef,
        IReadOnlyDictionary<string, OfxSecurity> secListIndex)
    {
        if (securityRef is null
            || string.IsNullOrWhiteSpace(securityRef.Id)
            || string.IsNullOrWhiteSpace(securityRef.IdType))
            return null;
        if (secListIndex.TryGetValue(SecListKey(securityRef.Id, securityRef.IdType), out var sec))
        {
            var hit = NullIfEmpty(sec.Ticker) ?? NullIfEmpty(sec.FinancialInstitutionId);
            if (hit is not null) return hit;
        }
        // No SECLIST entry (or SECLIST entry had no ticker / FIID).
        // Fall back to the raw UNIQUEID — provider_security_mappings
        // is opaque-keyed, so the user can still hand-map a CUSIP
        // to a Coffer security in the editor and the mapping sticks.
        return securityRef.Id;
    }

    /// <summary>
    /// Extract the signed cash-flow amount that lands on the
    /// brokerage-side leg. Most types expose a <c>Total</c> property
    /// (already-signed per OFX convention — buy negative, sell
    /// positive, income positive, etc.).
    /// </summary>
    /// <remarks>
    /// <para><b>REINVEST is a special case.</b> The OFX <c>REINVEST</c>
    /// entry's <c>TOTAL</c> is the dollar value of the reinvested
    /// dividend (negative — the buy cost). It is NOT a cash movement
    /// on the brokerage: the dividend income IS the buy funding;
    /// no cash ever lands in the account. Reporting <c>reinvest.Total</c>
    /// as the bank-shape leg amount makes the user's cash balance
    /// walk down by the dividend amount on every reinvest, which is
    /// wrong (real cash balance is unchanged). The pending row's
    /// magnitude is recoverable from <c>Shares × UnitPrice</c>
    /// (persisted via the mig-113 prefill carriers), so the editor
    /// pre-fills the buy leg on Accept without the misleading cash
    /// leg. On upgrade to the investment shape (ADR-0028), the
    /// income + buy legs net to zero on the brokerage cash side —
    /// matching the bank-shape contract emitted here.</para>
    /// </remarks>
    private static decimal ExtractAmount(OfxInvestmentTransaction txn) => txn switch
    {
        OfxBuyInvestment buy           => buy.Total,
        OfxSellInvestment sell         => sell.Total,
        OfxReinvest                    => 0m,
        OfxIncome income               => income.Total,
        OfxCapitalReturn retCap        => retCap.Total,
        OfxInvestmentExpense exp       => exp.Total,
        OfxMarginInterest margin       => margin.Total,
        _                              => 0m,
    };

    /// <summary>
    /// Extract the share count from an OFX investment transaction.
    /// Returns null for types with no shares (Income / CapitalReturn /
    /// Expense / MarginInterest — those are pure cash flows against
    /// a security). The OFX wire convention keeps <c>Units</c>
    /// positive regardless of buy-vs-sell direction; the transaction
    /// subtype (Buy* vs Sell*) carries the direction. The editor's
    /// shares input is positive on both sides too, so no sign flip
    /// is needed here — the editor's action field handles direction
    /// on save.
    /// </summary>
    private static decimal? ExtractShares(OfxInvestmentTransaction txn) => txn switch
    {
        OfxBuyInvestment buy           => buy.Units,
        OfxSellInvestment sell         => sell.Units,
        OfxReinvest reinvest           => reinvest.Units,
        _                              => null,
    };

    /// <summary>
    /// Extract the per-share unit price. Same population set as
    /// <see cref="ExtractShares"/> — only Buy/Sell/Reinvest carry it.
    /// </summary>
    private static decimal? ExtractUnitPrice(OfxInvestmentTransaction txn) => txn switch
    {
        OfxBuyInvestment buy           => buy.UnitPrice,
        OfxSellInvestment sell         => sell.UnitPrice,
        OfxReinvest reinvest           => reinvest.UnitPrice,
        _                              => null,
    };

    /// <summary>
    /// Extract the aggregated fee. Sums every fee-shaped field the
    /// OFX type exposes — Commission + Fees + Load + Markup (buy
    /// only) + Markdown (sell only) + Taxes. Null when the wire had
    /// none of them OR they all summed to zero. Taxes are bundled
    /// in because from the user's editor perspective they're a cost
    /// against the trade, no different from a commission; ADR-0029's
    /// editor uses ONE aggregated fee field (no per-kind breakdown).
    /// </summary>
    private static decimal? ExtractFee(OfxInvestmentTransaction txn)
    {
        decimal total = 0m;
        switch (txn)
        {
            case OfxBuyInvestment buy:
                total = (buy.Commission ?? 0m) + (buy.Fees ?? 0m)
                      + (buy.Load ?? 0m) + (buy.Markup ?? 0m) + (buy.Taxes ?? 0m);
                break;
            case OfxSellInvestment sell:
                total = (sell.Commission ?? 0m) + (sell.Fees ?? 0m)
                      + (sell.Load ?? 0m) + (sell.Markdown ?? 0m) + (sell.Taxes ?? 0m);
                break;
            case OfxReinvest reinvest:
                total = (reinvest.Commission ?? 0m) + (reinvest.Fees ?? 0m)
                      + (reinvest.Load ?? 0m) + (reinvest.Taxes ?? 0m);
                break;
            // Income / CapitalReturn / Expense / MarginInterest have
            // no fee-shaped fields; null.
            default:
                return null;
        }
        return total == 0m ? null : total;
    }

    /// <summary>
    /// Map a single OFX statement transaction to the
    /// provider-neutral <see cref="IngestedTransaction"/>. Returns
    /// null when the row lacks the minimum identity fields
    /// (FITID / amount / posted date).
    /// </summary>
    private static IngestedTransaction? MapTransaction(
        OfxStatementTransaction txn, string providerAccountId, string? fiId)
    {
        if (string.IsNullOrWhiteSpace(txn.FitId)) return null;
        // OFX uses a separate NAME field for the merchant string and
        // MEMO for free-form notes. NAME is closer to SimpleFIN's
        // `payee` (cleaned merchant); MEMO is the raw bank text.
        // Both lift through the same payee/description split as
        // SimpleFIN.
        var payee = NullIfEmpty(txn.Name);
        var description = NullIfEmpty(txn.Memo);
        var postedAt = txn.DatePosted.UtcDateTime;
        var transactedAt = txn.DateUser?.UtcDateTime;
        return new IngestedTransaction(
            ExternalId: txn.FitId,
            PostedAt: postedAt,
            TransactedAt: transactedAt == postedAt ? null : transactedAt,
            Amount: txn.Amount,
            Payee: payee,
            Description: description,
            Pending: false,                            // OFX has no per-row pending flag (statements are post-clear)
            Action: null,                              // bank shape; investment-action lands in slice 2
            SecurityTickerHint: null,
            RawProviderPayload: null,                  // OFX parse path doesn't preserve raw element text (yet)
            ProviderAccountId: providerAccountId,
            OnlineMatchFiId: fiId,
            // OFX FITID == ExternalId == online_match_fitid (OFX-protocol
            // cross-source dedup substrate; OFX provider only).
            OnlineMatchFitid: txn.FitId);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
