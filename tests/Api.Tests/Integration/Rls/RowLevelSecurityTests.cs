using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Rls;

/// <summary>
/// Direct-against-the-DB checks that the RLS policies introduced in
/// migration 017 enforce per-user scoping at the row level, not just
/// at the endpoint-gate level. These tests bypass the HTTP pipeline
/// and the app-layer 422 gate so a missing/forgotten WHERE in some
/// future repository can't pass them.
/// </summary>
/// <remarks>
/// The fixture's <see cref="PostgresFixture.NewAppDbContextAsUser"/>
/// builds an <c>AppDbContext</c> bound to the <c>coffer_app</c>
/// connection string with <c>app.user_id</c> pre-set on connection
/// open — same pattern as production's
/// <see cref="Coffer.Api.Db.AppUserDbConnectionInterceptor"/>, just
/// with the user-id pinned rather than resolved from HTTP context.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RowLevelSecurityTests
{
    private readonly PostgresFixture _fixture;

    public RowLevelSecurityTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Accounts_query_as_alice_returns_only_alices_accounts()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceAccount = await alice.AddBankAccountAsync("alices-checking");
        var bobAccount = await bob.AddBankAccountAsync("bobs-checking");

        // coffer_app + app.user_id = alice → RLS filters accounts to
        // alice's ledger. No WHERE clause in the test query; only RLS
        // gates the result.
        await using var aliceDb = _fixture.NewAppDbContextAsUser(alice.UserId);
        var aliceVisible = await aliceDb.Accounts.AsNoTracking()
            .Select(a => a.Id)
            .ToListAsync();

        Assert.Contains(aliceAccount.Id, aliceVisible);
        Assert.DoesNotContain(bobAccount.Id, aliceVisible);
    }

    [Fact]
    public async Task Accounts_query_with_no_app_user_id_set_returns_empty()
    {
        // Service role doesn't apply RLS, but the App role with no
        // app.user_id GUC (pre-auth posture) should see nothing —
        // fail-closed.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await alice.AddBankAccountAsync("alices-checking");

        // Use the AppConnectionString directly via a raw DbContext
        // WITHOUT the user interceptor. Mirrors what happens if a
        // future code path forgets to set app.user_id.
        var options = new DbContextOptionsBuilder<Coffer.Api.Db.AppDbContext>()
            .UseNpgsql(_fixture.AppConnectionString)
            .Options;
        await using var db = new Coffer.Api.Db.AppDbContext(options);

        var visible = await db.Accounts.AsNoTracking()
            .Select(a => a.Id)
            .ToListAsync();

        Assert.Empty(visible);
    }

    [Fact]
    public async Task Transactions_inherit_account_policy_via_FK_chain()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var aliceBank = await alice.AddBankAccountAsync("alice-bank");
        var aliceCategory = await alice.AddCategoryAsync("alice-groceries");
        var bobBank = await bob.AddBankAccountAsync("bob-bank");
        var bobCategory = await bob.AddCategoryAsync("bob-groceries");

        await alice.AddTransactionPairAsync(
            aliceBank.Id, aliceCategory.Id, amount: -10m,
            postedAt: new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        await bob.AddTransactionPairAsync(
            bobBank.Id, bobCategory.Id, amount: -20m,
            postedAt: new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        // Query resolved_transactions (the view used by the register
        // query) as alice. The view has security_invoker=true so the
        // underlying transactions policy applies — alice sees only
        // her pair (2 rows), not bob's.
        await using var aliceDb = _fixture.NewAppDbContextAsUser(alice.UserId);
        var aliceVisibleAccountIds = await aliceDb.ResolvedTransactions.AsNoTracking()
            .Select(t => t.AccountId)
            .Distinct()
            .ToListAsync();

        Assert.Equal(2, aliceVisibleAccountIds.Count);
        Assert.Contains(aliceBank.Id, aliceVisibleAccountIds);
        Assert.Contains(aliceCategory.Id, aliceVisibleAccountIds);
        Assert.DoesNotContain(bobBank.Id, aliceVisibleAccountIds);
        Assert.DoesNotContain(bobCategory.Id, aliceVisibleAccountIds);
    }

    [Fact]
    public async Task Users_table_returns_only_callers_own_row()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        await using var aliceDb = _fixture.NewAppDbContextAsUser(alice.UserId);
        var visibleUsers = await aliceDb.Users.AsNoTracking()
            .Select(u => u.Id)
            .ToListAsync();

        // Even the bootstrap "system" user is hidden — the users_self
        // policy only admits id = current_app_user_id.
        Assert.Single(visibleUsers);
        Assert.Equal(alice.UserId, visibleUsers[0]);
        Assert.DoesNotContain(bob.UserId, visibleUsers);
        Assert.DoesNotContain(UserRow.SystemUserId, visibleUsers);
    }

    [Fact]
    public async Task Ledgers_table_returns_only_ledgers_caller_has_grant_on()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        await using var aliceDb = _fixture.NewAppDbContextAsUser(alice.UserId);
        var visibleLedgers = await aliceDb.Ledgers.AsNoTracking()
            .Select(l => l.Id)
            .ToListAsync();

        Assert.Contains(alice.LedgerId, visibleLedgers);
        Assert.DoesNotContain(bob.LedgerId, visibleLedgers);
    }

    [Fact]
    public async Task UserLedgerGrants_returns_only_callers_own_grants()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        // SyntheticLedger.CreateAsync grants both the per-test user
        // AND the system user owner on every test ledger. Alice
        // should see only her own grant row, not the system user's
        // grant on the same ledger.
        await using var aliceDb = _fixture.NewAppDbContextAsUser(alice.UserId);
        var visibleGrants = await aliceDb.UserLedgerGrants.AsNoTracking()
            .Select(g => new { g.UserId, g.LedgerId })
            .ToListAsync();

        Assert.All(visibleGrants, g => Assert.Equal(alice.UserId, g.UserId));
        Assert.Contains(visibleGrants, g => g.LedgerId == alice.LedgerId);
        Assert.DoesNotContain(visibleGrants, g => g.LedgerId == bob.LedgerId);
    }

    [Fact]
    public async Task AuthSessions_table_returns_only_callers_own_sessions()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await alice.IssueSessionCookieAsync();
        await bob.IssueSessionCookieAsync();

        await using var aliceDb = _fixture.NewAppDbContextAsUser(alice.UserId);
        var visibleSessions = await aliceDb.AuthSessions.AsNoTracking()
            .Select(s => s.UserId)
            .Distinct()
            .ToListAsync();

        Assert.Single(visibleSessions);
        Assert.Equal(alice.UserId, visibleSessions[0]);
    }

    [Fact]
    public async Task Service_role_sees_all_rows_across_users()
    {
        // Counterpart to the previous tests: coffer_service (BYPASSRLS)
        // is what the importer + pre-auth code paths use, and it MUST
        // see everything regardless of who set what. Verifies the
        // role split actually delivers two distinct visibility
        // regimes, not just one accidentally-locked-down view.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await alice.AddBankAccountAsync("alices-bank");
        await bob.AddBankAccountAsync("bobs-bank");

        await using var serviceDb = _fixture.NewServiceDbContext();
        var allAccountLedgerIds = await serviceDb.Accounts.AsNoTracking()
            .Select(a => a.LedgerId)
            .Distinct()
            .ToListAsync();

        Assert.Contains(alice.LedgerId, allAccountLedgerIds);
        Assert.Contains(bob.LedgerId, allAccountLedgerIds);
    }
}
