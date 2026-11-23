using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Editing a transaction whose account has since been deactivated.
/// </summary>
/// <remarks>
/// <para>The rule: "new transactions and re-targeting PATCHes cannot land on
/// accounts that have been deactivated. Editing other fields on existing legs
/// whose account is already inactive is still allowed (historical
/// preservation)."</para>
///
/// <para>A memo edit resends the unchanged counterparty, so a PATCH path that
/// validates every account in the payload sees an inactive account and refuses —
/// deactivating an account would FREEZE its entire history and a typo in a
/// ten-year-old memo would become uncorrectable. A validator that sees only the
/// payload cannot tell "re-targeting onto a deactivated account" from "leaving
/// one where it already was".</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InactiveAccountEditTests : DatabaseTest
{
    public InactiveAccountEditTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime May = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>Retire an account the way a user does — through the endpoint,
    /// not by writing `is_active` from the test. A hand-written flag would
    /// skip whatever that route does besides flip the column.</summary>
    private static async Task DeactivateAsync(HttpClient client, Guid ledgerId, Guid accountId)
    {
        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledgerId}/accounts/{accountId}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    /// <summary>A bank account, two categories, one posting — then the
    /// counterparty category is retired.</summary>
    private async Task<(SyntheticLedger Ledger, Guid HeaderId, Guid BankId, Guid RetiredId, Guid LiveId)>
        SeedAsync(HttpClient client, SyntheticLedger ledger)
    {
        var bank = await ledger.AddBankAccountAsync("Checking");
        var retired = await ledger.AddCategoryAsync("Old Subscription", "expense");
        var live = await ledger.AddCategoryAsync("Groceries", "expense");

        var pair = await ledger.AddTransactionPairAsync(retired.Id, bank.Id, 40m, May);
        var headerId = await ledger.ResolveHeaderIdAsync(pair.FromTxnId);
        await DeactivateAsync(client, ledger.LedgerId, retired.Id);
        return (ledger, headerId, bank.Id, retired.Id, live.Id);
    }

    private static object PatchBody(Guid sourceAccountId, Guid counterpartyId, string memo) => new
    {
        notes = new { side = sourceAccountId, memo },
        postings = new
        {
            sourceAccountId,
            items = new[] { new { counterpartyAccountId = counterpartyId, amount = 40m } },
        },
    };

    [Fact]
    public async Task A_memo_edit_survives_the_counterparty_being_deactivated()
    {
        // THE ONE THIS EXISTS FOR, and the behaviour the rule always claimed.
        var ledger0 = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger0);
        var (ledger, headerId, bankId, retiredId, _) = await SeedAsync(client, ledger0);

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            PatchBody(bankId, retiredId, "corrected memo"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The writing side's own memo, on its leg.
        await using var db = Db.NewDbContext();
        Assert.Equal("corrected memo", await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == bankId)
            .Select(l => l.LegMemo).SingleAsync());
    }

    [Fact]
    public async Task Re_targeting_ONTO_a_deactivated_account_is_still_refused()
    {
        // The other half of the rule, and the pair matters: a fix that simply
        // dropped the activity check would pass the test above and silently
        // allow exactly what the gate exists to stop.
        // Start from the LIVE category so the move is genuinely onto the
        // retired one rather than a no-op.
        var ledger0 = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger0);
        var (ledger, headerId, bankId, retiredId, liveId) = await SeedAsync(client, ledger0);

        var moveToLive = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            PatchBody(bankId, liveId, "now groceries"));
        Assert.Equal(HttpStatusCode.OK, moveToLive.StatusCode);

        // ...and back onto the retired one, which must now be refused.
        var backToRetired = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            PatchBody(bankId, retiredId, "back again"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, backToRetired.StatusCode);
        Assert.Contains("inactive", await backToRetired.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bulk_move_refuses_a_deactivated_TARGET()
    {
        // Only the picker enforced this, so MCP — or any caller that is not
        // the SPA — could move rows onto an account the user had retired,
        // which is the one direction the rule exists to prevent.
        var ledger0 = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger0);
        var (ledger, _, bankId, retiredId, _) = await SeedAsync(client, ledger0);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-move-account",
            new
            {
                selection = new
                {
                    kind = "all",
                    accountId = bankId,
                    statusFilter = "all",
                    selectedAt = DateTime.UtcNow.AddMinutes(1),
                    excludeIds = Array.Empty<Guid>(),
                    headerIds = Array.Empty<Guid>(),
                },
                targetAccountId = retiredId,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("inactive", await resp.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bulk_move_OFF_a_deactivated_account_stays_legal()
    {
        // Deliberately not symmetric. Moving rows off a retired account is how
        // one is cleaned up; refusing it would strand them there forever.
        var ledger0 = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger0);
        var (ledger, _, _, retiredId, liveId) = await SeedAsync(client, ledger0);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-move-account",
            new
            {
                selection = new
                {
                    kind = "all",
                    accountId = retiredId,
                    statusFilter = "all",
                    selectedAt = DateTime.UtcNow.AddMinutes(1),
                    excludeIds = Array.Empty<Guid>(),
                    headerIds = Array.Empty<Guid>(),
                },
                targetAccountId = liveId,
            });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
