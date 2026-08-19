using Microsoft.EntityFrameworkCore;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Atomic per-test fixture builder. Each integration test calls
/// <see cref="CreateAsync"/> in its arrange step to mint a fresh ledger
/// (with a unique name + id), a fresh user, and any auxiliary rows the
/// test needs — all stamped with the new ids so concurrent tests can't
/// collide. Mirrors the user-pinned API engineering standard: integration
/// tests bootstrap a synthetic ledger atomically rather than re-using
/// shared state.
/// </summary>
/// <remarks>
/// <para>The <see cref="ApiCollection"/> applies migrations once via the
/// shared <see cref="PostgresFixture"/>, so the schema is in place. Each
/// test's <see cref="SyntheticLedger"/> instance carries the new ids; tests
/// reach the rest of the schema (transactions, holdings, …) via
/// repository methods seeded by these ids.</para>
///
/// <para>Cleanup is intentionally not implemented. Per-test ledgers
/// accumulate in the testcontainer for the lifetime of the run; the
/// container is destroyed at the end of the xUnit collection so leakage
/// stays bounded. Per-test unique ids give the same isolation guarantee
/// without the bookkeeping.</para>
/// </remarks>
public sealed class SyntheticLedger
{
    /// <summary>The ledger id minted for this test.</summary>
    public Guid LedgerId { get; }

    /// <summary>The user id minted for this test.</summary>
    public Guid UserId { get; }

    /// <summary>This user's username (random; used by tests asserting on it).</summary>
    public string Username { get; }

    private readonly PostgresFixture _fixture;

    private SyntheticLedger(
        Guid ledgerId, Guid userId, string username, PostgresFixture fixture)
    {
        LedgerId = ledgerId;
        UserId = userId;
        Username = username;
        _fixture = fixture;
    }

    /// <summary>
    /// Construct a fresh <see cref="AppDbContext"/> bound to the
    /// fixture's testcontainer. Caller owns disposal (typically via
    /// <c>await using</c>).
    /// </summary>
    public AppDbContext NewDbContext() => _fixture.NewDbContext();

    /// <summary>
    /// Build a synthetic ledger + user atomically: a row in
    /// <c>ledgers</c>, a fresh user with a random username, and an
    /// owner grant linking the two. The system user is also granted
    /// owner so service-role flows (importer-style work) keep working
    /// without further setup.
    /// </summary>
    public static async Task<SyntheticLedger> CreateAsync(
        PostgresFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var db = fixture.NewDbContext();

        var suffix = Guid.NewGuid().ToString("N");
        var ledgerName = $"synthetic-{suffix}";
        var username = $"test-user-{suffix}";

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                                                         .ConfigureAwait(false);

        var ledger = new LedgerRow { Id = Guid.NewGuid(), Name = ledgerName };
        db.Ledgers.Add(ledger);

        var user = new UserRow
        {
            Id = Guid.NewGuid(),
            DisplayName = username,
            Username = username,
            CreatedBy = "integration-test",
        };
        db.Users.Add(user);

        // Owner grant for both the test user and the bootstrap system
        // user so service-role-style code paths (importer, future sync
        // worker) also function under this ledger without extra wiring.
        db.UserLedgerGrants.AddRange(
            new UserLedgerGrantRow { UserId = user.Id, LedgerId = ledger.Id, Role = "owner" },
            new UserLedgerGrantRow { UserId = UserRow.SystemUserId, LedgerId = ledger.Id, Role = "owner" });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SyntheticLedger(ledger.Id, user.Id, username, fixture);
    }

    /// <summary>
    /// Add a SECOND user to this ledger with the given role (owner/editor/viewer)
    /// and return their user id — for multi-user / role-enforcement tests (ADR-0083).
    /// Inserts a fresh user + a <c>user_ledger_grants</c> row via the service context.
    /// </summary>
    public async Task<Guid> AddMemberAsync(string role, CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        var suffix = Guid.NewGuid().ToString("N");
        var user = new UserRow
        {
            Id = Guid.NewGuid(),
            DisplayName = $"member-{role}-{suffix}",
            Username = $"member-{role}-{suffix}",
            CreatedBy = "integration-test",
        };
        db.Users.Add(user);
        db.UserLedgerGrants.Add(new UserLedgerGrantRow { UserId = user.Id, LedgerId = LedgerId, Role = role });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user.Id;
    }

    /// <summary>
    /// Convenience: insert a credential under <see cref="UserId"/> with
    /// random bytes so tests that need an existing credential row don't
    /// duplicate the boilerplate. The returned <see cref="WebAuthnCredentialRow"/>
    /// carries the assigned id and timestamps.
    /// </summary>
    public async Task<WebAuthnCredentialRow> AddCredentialAsync(
        string nickname = "test-key", CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        var repo = new CredentialsRepository(_fixture.NewServiceFactory());
        return await repo.InsertAsync(
            userId: UserId,
            credentialId: RandomBytes(64),
            publicKey: RandomBytes(77),
            signatureCounter: 0,
            aaguid: null,
            transports: null,
            nickname: nickname,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Insert an unrevoked, unexpired <c>auth_sessions</c> row directly,
    /// returning a base64url-encoded cookie value the caller can attach
    /// to an <see cref="System.Net.Http.HttpClient"/> via the Cookie
    /// header. Bypasses the WebAuthn ceremonies — useful for tests that
    /// need a per-user authenticated client without driving the full
    /// /login flow (which is exercised separately in
    /// <c>LoginEndpointsTests</c>).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="SessionService.GenerateCookieValue"/> so the
    /// random-bytes / base64url / SHA-256 contract matches the
    /// production code exactly. <c>InternalsVisibleTo Api.Tests</c> on
    /// the API project (in <c>Api.csproj</c>) lets tests reach the
    /// internal static helper.
    /// </remarks>
    public async Task<string> IssueSessionCookieAsync(CancellationToken cancellationToken = default)
    {
        var (plaintext, hash) = SessionService.GenerateCookieValue();

        await using var db = NewDbContext();
        db.AuthSessions.Add(new SessionRow
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            SessionHash = hash,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return plaintext;
    }

    /// <summary>
    /// Insert a bank-type account in this ledger and return its row.
    /// Defaults satisfy the cross-column CHECK constraints on
    /// <c>accounts</c> (no parent, no category_kind, opening_balance 0).
    /// </summary>
    public async Task<AccountRow> AddBankAccountAsync(
        string name,
        decimal openingBalance = 0m,
        CancellationToken cancellationToken = default)
    {
        var row = new AccountRow
        {
            Id = Guid.NewGuid(),
            LedgerId = LedgerId,
            Name = name,
            AccountType = "bank",
            CurrencyCode = "USD",
            OpeningBalance = openingBalance,
            IsActive = true,
        };
        await using var db = NewDbContext();
        db.Accounts.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Insert an investment (brokerage) account plus its system-managed
    /// Holdings sibling (ADR-0019), linking the brokerage to the sibling
    /// via <c>accounts.holdings_account_id</c>. Returns the brokerage row;
    /// call <see cref="AddHoldingAsync"/> with its
    /// <see cref="AccountRow.HoldingsAccountId"/> to drop position rows.
    /// </summary>
    /// <param name="withHoldingsSibling">
    /// Pass <c>false</c> to mint a brokerage with <c>holdings_account_id</c> NULL —
    /// a cash-only investment account that has never held a position (an emptied
    /// rollover stub, a CD, a sweep account). It is still a brokerage: it takes
    /// transfers, so reporting must classify them. Returns a row whose
    /// <see cref="AccountRow.HoldingsAccountId"/> is null.
    /// </param>
    /// <param name="openingBalance">
    /// Starting balance, as of <paramref name="openedOn"/>. Moneydance ledgers
    /// usually leave this 0 and carry all history as transactions, but an account
    /// CAN be opened with a balance — and returns treats the two cases
    /// differently, so tests need both.
    /// </param>
    /// <param name="openedOn">
    /// The account's Start Date (mig 127 / ADR-0050) — the as-of date of
    /// <paramref name="openingBalance"/>.
    /// </param>
    public async Task<AccountRow> AddInvestmentAccountAsync(
        string name,
        bool withHoldingsSibling = true,
        decimal openingBalance = 0m,
        DateOnly? openedOn = null,
        CancellationToken cancellationToken = default)
    {
        var holdingsSibling = withHoldingsSibling
            ? new AccountRow
            {
                Id = Guid.NewGuid(),
                LedgerId = LedgerId,
                Name = $"{name} Holdings",
                AccountType = "investment",
                CurrencyCode = "USD",
                OpeningBalance = 0m,
                IsActive = true,
                IsSystem = true,
            }
            : null;
        var brokerage = new AccountRow
        {
            Id = Guid.NewGuid(),
            LedgerId = LedgerId,
            Name = name,
            AccountType = "investment",
            CurrencyCode = "USD",
            OpeningBalance = openingBalance,
            OpenedOn = openedOn,
            IsActive = true,
            HoldingsAccountId = holdingsSibling?.Id,
        };
        await using var db = NewDbContext();
        if (holdingsSibling is not null) db.Accounts.Add(holdingsSibling);
        db.Accounts.Add(brokerage);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return brokerage;
    }

    /// <summary>
    /// Seed an investment BUY under ADR-0022: one header (<c>action='buy'</c>)
    /// plus a holdings-side leg on the Holdings sibling (carrying
    /// <c>security_id</c> + <c>quantity</c> + <c>unit_price</c> +
    /// <c>posting_role='security'</c>) paired with a cash leg on the brokerage.
    /// Raw-SQL seed (bypasses the API interceptors). Returns the holdings-side
    /// leg id. Recomputes the brokerage + holdings running balances (as
    /// <see cref="AddTransactionPairAsync"/> does) so <c>account_balance_as_of</c>
    /// sees the cash leg; the holdings feeder self-replays legs regardless. Cost
    /// is rounded to 2dp (the mig-159 money CHECK); pass whole-cent qty×price for
    /// exact test math. <paramref name="postedAt"/> must be UTC.
    /// </summary>
    /// <summary>
    /// Seed an in-kind share transfer (<c>action='transfer_shares'</c>, ADR-0065):
    /// securities move between two brokerages with NO cash leg. Four legs, and
    /// every one of them faces an account inside its OWN brokerage — which is
    /// precisely why reporting could not see the value crossing accounts:
    /// <code>
    ///   source sibling   -> source brokerage    -(qty*price)   shares out
    ///   source brokerage -> source sibling                0
    ///   dest sibling     -> dest brokerage      +(qty*price)   shares in
    ///   dest brokerage   -> dest sibling                  0
    /// </code>
    /// The two accounts are joined only by the shared header. Raw-SQL seed, like
    /// the other investment helpers; recomputes both brokerages' balances.
    /// </summary>
    public async Task<Guid> AddTransferSharesAsync(
        Guid sourceBrokerageId,
        Guid sourceHoldingsId,
        Guid destBrokerageId,
        Guid destHoldingsId,
        Guid securityId,
        decimal quantity,
        decimal unitPrice,
        DateTime postedAt,
        CancellationToken cancellationToken = default)
    {
        var headerId = Guid.NewGuid();
        var value = decimal.Round(quantity * unitPrice, 2);

        await using var db = NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                                                       .ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_headers (id, ledger_id, origin, action, payee, posted_at, transacted_at, created_at)
            VALUES ({headerId}, {LedgerId}, 'manual', 'transfer_shares', 'in-kind transfer',
                    {postedAt}, {postedAt}, {postedAt});
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount,
                                  security_id, quantity, unit_price, posting_role)
            VALUES
                ({Guid.NewGuid()}, {headerId}, {LedgerId}, {sourceHoldingsId},  0, {-value},
                 {securityId}, {-quantity}, {unitPrice}, 'security'),
                ({Guid.NewGuid()}, {headerId}, {LedgerId}, {sourceBrokerageId}, 0, 0,
                 NULL, NULL, NULL, 'security'),
                ({Guid.NewGuid()}, {headerId}, {LedgerId}, {destHoldingsId},    0, {value},
                 {securityId}, {quantity}, {unitPrice}, 'security'),
                ({Guid.NewGuid()}, {headerId}, {LedgerId}, {destBrokerageId},   0, 0,
                 NULL, NULL, NULL, 'security');",
            cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT fn_recompute_balances_for_account({sourceBrokerageId}, '0001-01-01'::timestamptz);
               SELECT fn_recompute_balances_for_account({sourceHoldingsId},  '0001-01-01'::timestamptz);
               SELECT fn_recompute_balances_for_account({destBrokerageId},   '0001-01-01'::timestamptz);
               SELECT fn_recompute_balances_for_account({destHoldingsId},    '0001-01-01'::timestamptz);",
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return headerId;
    }

    public async Task<Guid> AddInvestmentBuyAsync(
        Guid brokerageAccountId,
        Guid holdingsAccountId,
        Guid securityId,
        decimal quantity,
        decimal unitPrice,
        DateTime postedAt,
        CancellationToken cancellationToken = default)
    {
        var headerId = Guid.NewGuid();
        var holdingsLegId = Guid.NewGuid();
        var cashLegId = Guid.NewGuid();
        var cost = decimal.Round(quantity * unitPrice, 2);

        await using var db = NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                                                       .ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_headers (id, ledger_id, origin, action, payee, posted_at, transacted_at, created_at)
            VALUES ({headerId}, {LedgerId}, 'manual', 'buy', 'buy', {postedAt}, {postedAt}, {postedAt});
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount,
                                  security_id, quantity, unit_price, posting_role)
            VALUES
                ({holdingsLegId}, {headerId}, {LedgerId}, {holdingsAccountId}, 0, {cost},
                 {securityId}, {quantity}, {unitPrice}, 'security'),
                ({cashLegId},     {headerId}, {LedgerId}, {brokerageAccountId}, 0, {-cost},
                 NULL, NULL, NULL, 'security');",
            cancellationToken).ConfigureAwait(false);

        // Balance triggers are retired (mig 102) and raw seeds bypass the EF
        // interceptor, so recompute the affected accounts' running balances
        // explicitly — account_balance_as_of reads txn_header_account_balances.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT fn_recompute_balances_for_account({brokerageAccountId}, '0001-01-01'::timestamptz);
               SELECT fn_recompute_balances_for_account({holdingsAccountId},  '0001-01-01'::timestamptz);",
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return holdingsLegId;
    }

    /// <summary>
    /// Seed one <see cref="Boundary.Position"/> as a real, projected position — the
    /// holdings row, the buy, and the recompute — in a single call.
    /// </summary>
    /// <remarks>
    /// The three steps are together because doing two of them is a trap that has
    /// already cost a debugging session: <see cref="RecomputeHoldingsAsync"/> REFRESHES
    /// existing <c>holdings</c> rows rather than discovering positions from legs, so
    /// seeding a buy and recomputing silently leaves the projection empty. A test that
    /// then compares a projection-reading path against a leg-replaying path fails on
    /// the fixture rather than on the invariant it meant to check.
    /// <para>
    /// <b>KNOWN LIMITATION — this seeds no <c>lots</c> rows.</b> Migration 202 made the
    /// FIFO walk pure, so <c>recompute_holdings_cost_basis</c> derives lots in memory
    /// and persists only quantity, cost basis and realized gains; the <c>lots</c> table
    /// is written by the investment-transaction WRITE PATH
    /// (<c>InvestmentTransactionsRepository</c>) and by snapshot restore. A position
    /// seeded here therefore has correct holdings and basis but nothing for a
    /// lot-consuming endpoint to find, and <c>transfer_shares</c> rejects it with
    /// <c>investment-txn-transfer-shares-insufficient</c>.
    /// </para>
    /// <para>
    /// So: use this for tests that READ a valuation (net worth, returns, cost basis).
    /// A test that exercises an endpoint consuming lots must buy through the API
    /// instead — see <c>InKindTransferBoundaryTests</c>, which does exactly that.
    /// </para>
    /// </remarks>
    public async Task<Guid> AddBoundaryPositionAsync(
        Guid brokerageAccountId,
        Guid holdingsAccountId,
        Guid securityId,
        Boundary.Position position,
        DateTime postedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(position);

        await AddHoldingAsync(holdingsAccountId, securityId, quantity: 0m, costBasis: 0m,
                              cancellationToken).ConfigureAwait(false);
        var legId = await AddInvestmentBuyAsync(
            brokerageAccountId, holdingsAccountId, securityId,
            position.Quantity, position.BuyPrice, postedAt, cancellationToken)
            .ConfigureAwait(false);
        await RecomputeHoldingsAsync(cancellationToken).ConfigureAwait(false);
        return legId;
    }

    /// <summary>
    /// Recompute the <c>holdings</c> / <c>lots</c> projection from the legs, the way
    /// an API write path does.
    /// </summary>
    /// <remarks>
    /// Necessary because migration 104 DROPPED the holdings triggers. In the app the
    /// projection is kept in step by an EF <c>SaveChangesInterceptor</c>
    /// (<c>HoldingsRecomputeInterceptor</c>), which fires once per <c>SaveChanges</c>
    /// — but these fixtures seed by raw SQL, which never reaches the ChangeTracker,
    /// so nothing fires. That is a property of the FIXTURES, not a hole in the app:
    /// the API writes only through EF, which the no-raw-SQL audit enforces.
    /// <para>
    /// It REFRESHES existing rows rather than discovering positions — the function
    /// loops over <c>holdings</c> and re-derives each from the legs, so a
    /// (holdings-account, security) pair with legs but no row is not picked up. The
    /// write path creates the row; a raw seed must too, via
    /// <see cref="AddHoldingAsync"/>. Seeding buys alone and calling this returns 0
    /// and changes nothing, which is exactly what a first attempt at it did.
    /// </para>
    /// </remarks>
    public async Task RecomputeHoldingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT recompute_holdings_cost_basis({LedgerId}, NULL, NULL);",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Seed a stock-split event (migration 060): one <c>security_splits</c> row
    /// with <paramref name="ratio"/> effective at <paramref name="splitAt"/>
    /// (2.0 = 2-for-1 forward; 0.5 = 1-for-2 reverse). Raw-SQL seed — the API
    /// side has no <c>security_splits</c> EF entity. <paramref name="splitAt"/>
    /// must be UTC.
    /// </summary>
    public async Task AddSecuritySplitAsync(
        Guid securityId, decimal ratio, DateTime splitAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO security_splits (id, ledger_id, security_id, split_at, ratio)
            VALUES ({Guid.NewGuid()}, {LedgerId}, {securityId}, {splitAt}, {ratio});",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Flip the per-account <c>is_trade_commission</c> flag on a
    /// brokerage row (migration 056). Tests use this to exercise both
    /// fee-into-basis and fee-as-expense-only behaviors via the
    /// recompute function. Direct EF update — bypasses the API
    /// endpoint that owns the user-facing flow.
    /// </summary>
    public async Task SetIsTradeCommissionAsync(
        Guid accountId, bool isOn, CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        await db.Accounts
            .Where(a => a.Id == accountId && a.LedgerId == LedgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsTradeCommission, isOn),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Flip the per-account <c>is_active</c> flag (PR #132). Direct EF
    /// update — tests use this to exercise the inactive-account 422
    /// gate without going through PATCH /accounts/{id}/active.
    /// </summary>
    public async Task SetIsActiveAsync(
        Guid accountId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        await db.Accounts
            .Where(a => a.Id == accountId && a.LedgerId == LedgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, isActive),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Insert a security in the global catalog and return its id. Optional
    /// <paramref name="ticker"/> is what the Portfolio View renders as the
    /// chip label; <paramref name="assetClass"/> matches the
    /// <c>securities.asset_class</c> CHECK list.
    /// </summary>
    public async Task<Guid> AddSecurityAsync(
        string name,
        string? ticker = null,
        string? assetClass = "equity",
        string? quoteSymbol = null,
        bool autoPrice = true,
        bool quoteSymbolPublic = true,
        CancellationToken cancellationToken = default)
    {
        var row = new SecurityRow
        {
            Id = Guid.NewGuid(),
            LedgerId = LedgerId,
            Ticker = ticker,
            Name = name,
            AssetClass = assetClass,
            IsActive = true,
            QuoteSymbol = quoteSymbol,
            AutoPrice = autoPrice,
            QuoteSymbolPublic = quoteSymbolPublic,
            ShareDecimals = 4,
        };
        await using var db = NewDbContext();
        db.Securities.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>
    /// Insert a holdings row on the Holdings sibling account. The Portfolio
    /// View reads from here, joined to <see cref="SecurityRow"/> for the
    /// label and (separately) to <see cref="SecurityPriceRow"/> for the
    /// current value.
    /// </summary>
    public async Task AddHoldingAsync(
        Guid holdingsAccountId,
        Guid securityId,
        decimal quantity,
        decimal costBasis,
        CancellationToken cancellationToken = default)
    {
        var row = new HoldingRow
        {
            Id = Guid.NewGuid(),
            LedgerId = LedgerId,
            AccountId = holdingsAccountId,
            SecurityId = securityId,
            Quantity = quantity,
            CostBasis = costBasis,
            AsOf = DateTime.UtcNow,
        };
        await using var db = NewDbContext();
        db.Holdings.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Insert a price snapshot for a security. The Portfolio View reads the
    /// row with the latest <c>price_date</c> per security; insert multiple
    /// rows in tests that exercise "stale price gets superseded".
    /// </summary>
    public async Task AddSecurityPriceAsync(
        Guid securityId,
        decimal price,
        DateTime priceDate,
        string source = PriceSource.Import,
        CancellationToken cancellationToken = default)
    {
        var row = new SecurityPriceRow
        {
            Id = Guid.NewGuid(),
            LedgerId = LedgerId,
            SecurityId = securityId,
            Price = price,
            CurrencyCode = "USD",
            PriceDate = DateOnly.FromDateTime(priceDate),
            Source = source,
        };
        await using var db = NewDbContext();
        db.SecurityPrices.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Insert a multi-asset look-through sleeve (ADR-0067) for a security.
    /// <paramref name="weight"/> is a percent (0-100) of the wrapper in the given
    /// <paramref name="assetClass"/> × optional <paramref name="region"/>. The
    /// owning security must have <c>asset_class = 'multi_asset'</c> for allocation
    /// to decompose through these (mig 153).
    /// </summary>
    public async Task AddSecurityComponentAsync(
        Guid securityId,
        string assetClass,
        decimal weight,
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        var row = new SecurityComponentRow
        {
            Id = Guid.NewGuid(),
            SecurityId = securityId,
            ComponentAssetClass = assetClass,
            ComponentRegion = region,
            Weight = weight,
        };
        await using var db = NewDbContext();
        db.SecurityComponents.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Insert a category-type account in this ledger and return its row.
    /// <paramref name="kind"/> is the <c>category_kind</c> column
    /// (<c>income</c> or <c>expense</c>); the cross-column CHECK pairs
    /// it with <c>account_type='category'</c>. Optional
    /// <paramref name="parentId"/> nests this category under another
    /// category to exercise the <c>account_path()</c> recursive walk.
    /// </summary>
    public async Task<AccountRow> AddCategoryAsync(
        string name,
        string kind = "expense",
        Guid? parentId = null,
        CancellationToken cancellationToken = default)
    {
        var row = new AccountRow
        {
            Id = Guid.NewGuid(),
            LedgerId = LedgerId,
            Name = name,
            AccountType = "category",
            CategoryKind = kind,
            CurrencyCode = "USD",
            OpeningBalance = 0m,
            IsActive = true,
            ParentId = parentId,
        };
        await using var db = NewDbContext();
        db.Accounts.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Insert a paired transaction across two accounts per ADR-0022: one
    /// <c>txn_headers</c> row + two <c>txn_legs</c> rows sharing
    /// <c>posting_index = 0</c>. <paramref name="amount"/> is applied
    /// positively on the "from" leg and negatively on the "to" leg so the
    /// pair sums to zero.
    /// </summary>
    /// <remarks>
    /// Returns <c>(FromTxnId, ToTxnId)</c> — the two leg ids of the
    /// posting. Callers that need to operate on header-level fields
    /// (hide, mark merged, set check number, tag) pass either leg id;
    /// the helper resolves it to the header internally.
    /// </remarks>
    /// <param name="postingRole">
    /// Optional <c>txn_legs.posting_role</c> for BOTH legs — <c>income</c>,
    /// <c>fee</c>, <c>transfer</c> or <c>security</c> (ADR-0027). Null leaves it
    /// unset, which is what a plain cash transfer carries. Reporting keys on this
    /// to tell a dividend from a contribution when both face a category, so tests
    /// covering that distinction must set it.
    /// </param>
    public async Task<(Guid FromTxnId, Guid ToTxnId)> AddTransactionPairAsync(
        Guid fromAccountId,
        Guid toAccountId,
        decimal amount,
        DateTime postedAt,
        string payee = "test-payee",
        string? postingRole = null,
        CancellationToken cancellationToken = default)
    {
        var headerId = Guid.NewGuid();
        var fromLegId = Guid.NewGuid();
        var toLegId = Guid.NewGuid();

        await using var db = NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                                                         .ConfigureAwait(false);

        // Pin created_at to postedAt — both columns become test-controlled
        // deterministic timestamps. Letting created_at fall through to
        // the column DEFAULT now() means it tracks the Postgres container
        // clock, which drifts forward of the test process's
        // DateTime.UtcNow under Docker; tests that pin selectedAt against
        // DateTime.UtcNow then race that drift on the predicate
        // created_at <= selectedAt and flake under suite contention.
        // origin='manual' + external_id NULL satisfies the mig-109
        // CHECK (external_id IS NOT NULL OR origin = 'manual'). The
        // is_user_defined column was dropped in mig 109; the manual
        // /transactions POST writes origin='manual' as the equivalent
        // signal.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_headers (id, ledger_id, origin, payee, posted_at, transacted_at, created_at)
            VALUES ({headerId}, {LedgerId}, 'manual', {payee}, {postedAt}, {postedAt}, {postedAt});
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount, posting_role)
            VALUES
                ({fromLegId}, {headerId}, {LedgerId}, {fromAccountId}, 0, {amount},  {postingRole}),
                ({toLegId},   {headerId}, {LedgerId}, {toAccountId},   0, {-amount}, {postingRole});",
            cancellationToken).ConfigureAwait(false);

        // Mig 102: balance triggers retired; the interceptor sits on
        // EF SaveChanges and doesn't see raw SQL. Seed helpers
        // explicitly recompute so test fixtures match the post-API-
        // write state — same shape the Moneydance importer (#4) uses.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT fn_recompute_balances_for_account({fromAccountId}, '0001-01-01'::timestamptz);
               SELECT fn_recompute_balances_for_account({toAccountId},   '0001-01-01'::timestamptz);",
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (fromLegId, toLegId);
    }

    /// <summary>
    /// Seed a multi-split transaction under ADR-0022: one
    /// <c>txn_headers</c> row + N postings × 2 legs. Each posting has
    /// <c>posting_index</c> = leg index (0..N-1), one leg on the
    /// primary account and one on the target.
    /// </summary>
    /// <remarks>
    /// Returns the list of origin-side leg ids (one per posting, in
    /// posting-index order) and the header id (the group identity under
    /// ADR-0022; legacy tests still refer to it as "groupId").
    /// </remarks>
    public async Task<(IReadOnlyList<Guid> OriginIds, Guid GroupId)> AddMultiSplitAsync(
        Guid primaryAccountId,
        IReadOnlyList<(Guid TargetAccountId, decimal Amount)> legs,
        DateTime postedAt,
        string payee = "split-payee",
        CancellationToken cancellationToken = default)
    {
        if (legs.Count < 2)
            throw new ArgumentException(
                "A multi-split transaction needs ≥2 legs.", nameof(legs));

        var headerId = Guid.NewGuid();
        var originIds = new Guid[legs.Count];
        var targetIds = new Guid[legs.Count];
        for (var i = 0; i < legs.Count; i++)
        {
            originIds[i] = Guid.NewGuid();
            targetIds[i] = Guid.NewGuid();
        }

        await using var db = NewDbContext();
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Pin created_at to postedAt — see AddTransactionPairAsync for
        // why the seeder owns this column instead of letting the DB
        // DEFAULT now() drift against the test wall clock.
        // origin='manual' + external_id NULL satisfies the mig-109
        // CHECK (external_id IS NOT NULL OR origin = 'manual').
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_headers (id, ledger_id, origin, payee, posted_at, transacted_at, created_at)
            VALUES ({headerId}, {LedgerId}, 'manual', {payee}, {postedAt}, {postedAt}, {postedAt});",
            cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            var originLegId = originIds[i];
            var targetLegId = targetIds[i];
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_legs
                    (id, header_id, ledger_id, account_id, posting_index, amount)
                VALUES
                    ({originLegId}, {headerId}, {LedgerId}, {primaryAccountId},   {i}, {leg.Amount}),
                    ({targetLegId}, {headerId}, {LedgerId}, {leg.TargetAccountId}, {i}, {-leg.Amount});",
                cancellationToken).ConfigureAwait(false);
        }

        // Mig 102: recompute every touched account so seed state
        // matches the post-API-write state (see AddTransactionPairAsync).
        var touched = new HashSet<Guid> { primaryAccountId };
        foreach (var leg in legs) touched.Add(leg.TargetAccountId);
        foreach (var accountId in touched)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT fn_recompute_balances_for_account({accountId}, '0001-01-01'::timestamptz);",
                cancellationToken).ConfigureAwait(false);
        }

        // Mig 120: the raw leg inserts above carry the DEFAULT-1 posting
        // counts; recompute them for the header so seed state matches
        // the post-API-write state (the LegDerivedRecomputeInterceptor
        // would do this after an EF-tracked save). Without this the
        // resolved_transactions view reports header_total_postings = 1
        // and the multi-posting target rows collapse incorrectly.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT fn_recompute_posting_counts_for_header({headerId});",
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (originIds, headerId);
    }

    /// <summary>
    /// Hide an entire event by inserting a
    /// <c>txn_header_overrides</c> row with <c>is_hidden=TRUE</c>.
    /// <paramref name="legOrHeaderId"/> accepts either a leg id (from
    /// <see cref="AddTransactionPairAsync"/>) or a header id (from
    /// <see cref="AddMultiSplitAsync"/>); the helper resolves to the
    /// header before writing the override.
    /// </summary>
    public async Task HideTransactionAsync(
        Guid legOrHeaderId, CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        var ledgerId = LedgerId;
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_header_overrides (header_id, ledger_id, is_hidden)
            VALUES (
                COALESCE(
                    (SELECT header_id FROM txn_legs WHERE id = {legOrHeaderId}),
                    {legOrHeaderId}
                ),
                {ledgerId},
                TRUE
            )
            ON CONFLICT (header_id) DO UPDATE SET is_hidden = TRUE;",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-run <c>fn_recompute_balances_for_account</c> over the given accounts from
    /// the beginning of time. The seed helpers recompute at insert, but the
    /// override helpers (<see cref="HideTransactionAsync"/>,
    /// <see cref="SetHeaderOverrideAsync"/>, <see cref="MarkTransactionMergedAsync"/>)
    /// write raw SQL that the BalanceRecomputeInterceptor never sees — so a test
    /// that hides or re-dates an event and then asserts on a BALANCE (rather than
    /// on the register) must call this in between. Migration 103 gave the recompute
    /// its <c>is_hidden</c> filter; it only takes effect on the next run.
    /// </summary>
    /// <summary>
    /// Rebuild the denormalised posting counts (mig 120) for every header in the
    /// ledger, the way an EF save would.
    /// </summary>
    /// <remarks>
    /// Raw-SQL seeds never reach <c>LegDerivedRecomputeInterceptor</c>, so the
    /// counts stay at the column default of 1 while the legs say otherwise. That
    /// is the same shape as the production incident these fixtures now help test
    /// for — a write that bypassed the ChangeTracker and left a projection stale —
    /// so a fixture that leaves it behind is producing data production could not.
    /// </remarks>
    public async Task RecomputePostingCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        var headerIds = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == LedgerId)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var headerId in headerIds)
        {
            _ = await db.RecomputePostingCountsForHeader(headerId)
                .Select(r => r.HeaderId)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task RecomputeBalancesAsync(
        IEnumerable<Guid> accountIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        await using var db = NewDbContext();
        foreach (var accountId in accountIds)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT fn_recompute_balances_for_account({accountId}, '0001-01-01'::timestamptz);",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Seed an arbitrary <c>txn_header_overrides</c> row for tests that
    /// need to assert the override layer in isolation (without going
    /// through the override-write endpoint). Mirrors the upsert
    /// semantics the API uses: missing args leave existing columns
    /// alone; <paramref name="legOrHeaderId"/> resolves to the header.
    /// </summary>
    public async Task SetHeaderOverrideAsync(
        Guid legOrHeaderId,
        string? payee = null,
        string? memo = null,
        DateTime? transactedAt = null,
        DateTime? postedAt = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        var ledgerId = LedgerId;
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_header_overrides (header_id, ledger_id, payee, memo, transacted_at, posted_at)
            VALUES (
                COALESCE(
                    (SELECT header_id FROM txn_legs WHERE id = {legOrHeaderId}),
                    {legOrHeaderId}),
                {ledgerId},
                {payee}, {memo}, {transactedAt}, {postedAt}
            )
            ON CONFLICT (header_id) DO UPDATE SET
                payee         = COALESCE(EXCLUDED.payee,         txn_header_overrides.payee),
                memo          = COALESCE(EXCLUDED.memo,          txn_header_overrides.memo),
                transacted_at = COALESCE(EXCLUDED.transacted_at, txn_header_overrides.transacted_at),
                posted_at     = COALESCE(EXCLUDED.posted_at,     txn_header_overrides.posted_at),
                updated_at    = now();",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve a leg id (or pass-through a header id) to the owning
    /// header id. Used by tests that need to address header-level
    /// rows but only have a leg id in hand from
    /// <see cref="AddTransactionPairAsync"/>.
    /// </summary>
    public async Task<Guid> ResolveHeaderIdAsync(
        Guid legOrHeaderId, CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        // EF Core's SqlQuery<T> binds primitive scalars by the column
        // name "Value" — alias the projection accordingly so the
        // single-column readback finds the right column.
        var result = await db.Database
            .SqlQuery<Guid>($@"
                SELECT COALESCE(
                    (SELECT header_id FROM txn_legs WHERE id = {legOrHeaderId}),
                    {legOrHeaderId}) AS ""Value""")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Set <c>txn_headers.is_merged_into</c> on the event that owns
    /// <paramref name="losingId"/> (leg or header id; resolved to header
    /// internally). The register query's
    /// <c>WHERE is_merged_into IS NULL</c> predicate drops every leg of
    /// the merged header.
    /// </summary>
    public async Task MarkTransactionMergedAsync(
        Guid losingId, Guid winnerId, CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE txn_headers
               SET is_merged_into = COALESCE(
                       (SELECT header_id FROM txn_legs WHERE id = {winnerId}),
                       {winnerId})
             WHERE id = COALESCE(
                       (SELECT header_id FROM txn_legs WHERE id = {losingId}),
                       {losingId});",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Set the check number on an event. <paramref name="legOrHeaderId"/>
    /// accepts either a leg or header id; the helper resolves to the
    /// header (check number is event-level under ADR-0022).
    /// </summary>
    public async Task SetCheckNumberAsync(
        Guid legOrHeaderId,
        string checkNumber,
        CancellationToken cancellationToken = default)
    {
        await using var db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE txn_headers
               SET check_number = {checkNumber}
             WHERE id = COALESCE(
                       (SELECT header_id FROM txn_legs WHERE id = {legOrHeaderId}),
                       {legOrHeaderId});",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Create a tag in this ledger and link it to the event that owns
    /// <paramref name="legOrHeaderId"/> (resolved to header internally,
    /// since tags live at the header level under ADR-0022).
    /// Idempotent on tag name — re-using the same name re-uses the
    /// existing tag via INSERT ... ON CONFLICT. Returns the tag id.
    /// </summary>
    public async Task<Guid> AddTagAsync(
        Guid legOrHeaderId,
        string tagName,
        CancellationToken cancellationToken = default)
    {
        var tagId = Guid.NewGuid();
        await using var db = NewDbContext();
        var rows = await db.Database
            .SqlQueryRaw<TagIdRow>(
                "INSERT INTO tags (id, ledger_id, name) VALUES ({0}, {1}, {2}) " +
                "ON CONFLICT (ledger_id, name) DO UPDATE SET name = EXCLUDED.name " +
                "RETURNING id AS \"Value\"",
                tagId, LedgerId, tagName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var resolvedTagId = rows[0].Value;
        var ledgerId = LedgerId;
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_header_tags (header_id, tag_id, ledger_id)
            VALUES (
                COALESCE(
                    (SELECT header_id FROM txn_legs WHERE id = {legOrHeaderId}),
                    {legOrHeaderId}),
                {resolvedTagId},
                {ledgerId})
            ON CONFLICT DO NOTHING",
            cancellationToken).ConfigureAwait(false);
        return resolvedTagId;
    }

    /// <summary>
    /// Insert a bare tag into this ledger's dictionary with NO assignments (an
    /// orphan) and an optional colour — for tests exercising cleanup-unused,
    /// recolor, or a usage count of zero. Returns the tag id. Names are unique
    /// per test, so the <c>ON CONFLICT</c> guard is only defensive.
    /// </summary>
    public async Task<Guid> AddBareTagAsync(
        string tagName, string? color = null, CancellationToken cancellationToken = default)
    {
        var tagId = Guid.NewGuid();
        var ledgerId = LedgerId;
        await using var db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO tags (id, ledger_id, name, color)
            VALUES ({tagId}, {ledgerId}, {tagName}, {color})
            ON CONFLICT (ledger_id, name) DO NOTHING",
            cancellationToken).ConfigureAwait(false);
        return tagId;
    }

    /// <summary>Internal projection for the AddTagAsync return value.</summary>
    private sealed class TagIdRow { public Guid Value { get; init; } }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
