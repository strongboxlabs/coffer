using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Errors;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// Editing a bank reminder whose source or counterparty account has since been
/// deactivated.
/// </summary>
/// <remarks>
/// <para>The rule is the transaction edit's: an inactive account may stay where it
/// already is, and may not be newly added. A postings edit resends every line, so a
/// validator that sees only the payload cannot tell a line left on a deactivated
/// account from one moved onto it, and refuses both. That froze the reminder: none
/// of its postings could be edited, not even an amount.</para>
/// <para>The handling is to give the validator the accounts the template's legs
/// already sit on, exactly as the transaction PATCH supplies its header's.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderInactiveAccountEditTests : DatabaseTest
{
    public ReminderInactiveAccountEditTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage resp)
    {
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>Retire an account through the endpoint a user uses.</summary>
    private static async Task DeactivateAsync(HttpClient client, Guid ledgerId, Guid accountId)
    {
        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledgerId}/accounts/{accountId}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    private static async Task<Guid> CreateReminderAsync(
        HttpClient client, Guid ledgerId, Guid sourceId, Guid counterpartyId)
    {
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                SourceAccountId = sourceId,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = counterpartyId, Amount = -40m } },
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    private static Task<HttpResponseMessage> EditPostingsAsync(
        HttpClient client, Guid ledgerId, Guid reminderId, Guid sourceId,
        params (Guid Counterparty, decimal Amount)[] lines) =>
        client.PatchAsJsonAsync($"/api/ledgers/{ledgerId}/reminders/{reminderId}",
            new EditReminderRequest
            {
                Postings = new PatchReminderPostings
                {
                    SourceAccountId = sourceId,
                    Items = lines
                        .Select(l => new TransactionPosting { CounterpartyAccountId = l.Counterparty, Amount = l.Amount })
                        .ToArray(),
                },
            });

    private async Task<decimal[]> SourceAmountsAsync(Guid reminderId, Guid sourceId)
    {
        await using var db = Db.NewDbContext();
        var templateId = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId).Select(r => r.TemplateHeaderId!.Value).SingleAsync();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == templateId && l.AccountId == sourceId)
            .OrderBy(l => l.PostingIndex)
            .Select(l => l.Amount)
            .ToArrayAsync();
    }

    [Fact]
    public async Task A_line_kept_on_a_deactivated_counterparty_can_be_edited()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var retired = await ledger.AddCategoryAsync("Old Subscription", "expense");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var reminderId = await CreateReminderAsync(client, ledger.LedgerId, bank.Id, retired.Id);
        await DeactivateAsync(client, ledger.LedgerId, retired.Id);

        // The line resent unchanged: the account is already on the template.
        var unchanged = await EditPostingsAsync(client, ledger.LedgerId, reminderId, bank.Id, (retired.Id, -40m));
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);

        // The same line with a new amount: still the account it already sits on.
        var reAmounted = await EditPostingsAsync(client, ledger.LedgerId, reminderId, bank.Id, (retired.Id, -55m));
        Assert.Equal(HttpStatusCode.OK, reAmounted.StatusCode);
        Assert.Equal([-55m], await SourceAmountsAsync(reminderId, bank.Id));
    }

    [Fact]
    public async Task A_new_line_on_a_deactivated_account_is_refused()
    {
        // The other half of the rule: a fix that dropped the activity check would
        // pass the test above and allow exactly what the gate exists to stop.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var live = await ledger.AddCategoryAsync("Groceries", "expense");
        var retired = await ledger.AddCategoryAsync("Old Subscription", "expense");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var reminderId = await CreateReminderAsync(client, ledger.LedgerId, bank.Id, live.Id);
        await DeactivateAsync(client, ledger.LedgerId, retired.Id);

        var added = await EditPostingsAsync(client, ledger.LedgerId, reminderId, bank.Id,
            (live.Id, -40m), (retired.Id, -10m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, added.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountInactive, await ErrorCodeAsync(added));

        // Re-targeting the existing line onto it is the same move and is refused too.
        var retargeted = await EditPostingsAsync(client, ledger.LedgerId, reminderId, bank.Id, (retired.Id, -40m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retargeted.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountInactive, await ErrorCodeAsync(retargeted));

        // Nothing was written by either refusal.
        Assert.Equal([-40m], await SourceAmountsAsync(reminderId, bank.Id));
    }

    [Fact]
    public async Task A_deactivated_source_keeps_its_reminder_editable_but_takes_no_new_inactive_line()
    {
        // The source cannot change on an edit, so a deactivated source is always one
        // the template already sits on.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var live = await ledger.AddCategoryAsync("Groceries", "expense");
        var retired = await ledger.AddCategoryAsync("Old Subscription", "expense");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var reminderId = await CreateReminderAsync(client, ledger.LedgerId, bank.Id, live.Id);
        await DeactivateAsync(client, ledger.LedgerId, bank.Id);

        var reAmounted = await EditPostingsAsync(client, ledger.LedgerId, reminderId, bank.Id, (live.Id, -65m));
        Assert.Equal(HttpStatusCode.OK, reAmounted.StatusCode);
        Assert.Equal([-65m], await SourceAmountsAsync(reminderId, bank.Id));

        // The source being already present excuses only the source.
        await DeactivateAsync(client, ledger.LedgerId, retired.Id);
        var added = await EditPostingsAsync(client, ledger.LedgerId, reminderId, bank.Id,
            (live.Id, -65m), (retired.Id, -5m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, added.StatusCode);
        Assert.Equal(BusinessError.Codes.AccountInactive, await ErrorCodeAsync(added));
    }
}
