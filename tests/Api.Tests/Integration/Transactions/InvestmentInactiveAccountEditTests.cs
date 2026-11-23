using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Errors;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Editing an investment event, live or a reminder's template, whose brokerage,
/// category, transfer or fee account has since been deactivated.
/// </summary>
/// <remarks>
/// <para>The rule is the bank edit's: an inactive account may stay where it already
/// is, and may not be newly added. An investment edit resends the whole shape, so a
/// validator that sees only the payload cannot tell an account left in its role from
/// one moved onto it, and refuses both. That froze the event: nothing on it could be
/// edited, not even its payee.</para>
/// <para>The handling is to give the validator the accounts the event's legs already
/// sit on. A newly added inactive account is still refused with its role's code, and
/// creates and fires, which have nothing already present, stay strict.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InvestmentInactiveAccountEditTests : DatabaseTest
{
    private static readonly DateTime Posted = new(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly SeriesStart = new(2026, 1, 15);

    public InvestmentInactiveAccountEditTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // ----- setup -----

    private sealed record World(SyntheticLedger Ledger, Guid Brokerage, Guid Security);

    private async Task<World> WorldAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("Index Fund C", ticker: "IDXC");
        return new World(ledger, brokerage.Id, security);
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    /// <summary>An account that can fill <paramref name="role"/>.</summary>
    private static async Task<Guid> RoleAccountAsync(SyntheticLedger ledger, string role, string name) => role switch
    {
        "brokerage" => (await ledger.AddInvestmentAccountAsync(name)).Id,
        "category" => (await ledger.AddCategoryAsync(name, kind: "income")).Id,
        "transfer" => (await ledger.AddBankAccountAsync(name)).Id,
        "fee" => (await ledger.AddCategoryAsync(name, kind: "expense")).Id,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>An event that puts <paramref name="roleAccount"/> in <paramref name="role"/>.</summary>
    private static CreateInvestmentTransactionRequest Shape(
        World w, string role, Guid roleAccount, string payee) => role switch
    {
        "brokerage" => new()
        {
            Action = "buy", BrokerageAccountId = roleAccount, SecurityId = w.Security,
            Shares = 10m, Price = 100m, Payee = payee, PostedAt = Posted,
        },
        "category" => new()
        {
            Action = "dividend_cash", BrokerageAccountId = w.Brokerage, SecurityId = w.Security,
            CategoryAccountId = roleAccount, Amount = 25m, Payee = payee, PostedAt = Posted,
        },
        "transfer" => new()
        {
            Action = "transfer", BrokerageAccountId = w.Brokerage, TransferAccountId = roleAccount,
            Amount = 1000m, Payee = payee, PostedAt = Posted,
        },
        "fee" => new()
        {
            Action = "buy", BrokerageAccountId = w.Brokerage, SecurityId = w.Security,
            Shares = 10m, Price = 100m, FeeAccountId = roleAccount, FeeAmount = 4.95m,
            Payee = payee, PostedAt = Posted,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static string InactiveCode(string role) => role switch
    {
        "brokerage" => BusinessError.Codes.InvestmentTxnBrokerageInactive,
        "category" => BusinessError.Codes.InvestmentTxnCategoryInactive,
        "transfer" => BusinessError.Codes.InvestmentTxnTransferInactive,
        "fee" => BusinessError.Codes.InvestmentTxnFeeAccountInactive,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>Retire an account through the endpoint a user uses.</summary>
    private static async Task DeactivateAsync(HttpClient client, Guid ledgerId, Guid accountId)
    {
        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledgerId}/accounts/{accountId}/active",
            new PatchAccountActiveRequest { Active = false });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage resp)
    {
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<(string? Payee, Guid[] Accounts)> StoredAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        var payee = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == headerId).Select(h => h.Payee).SingleAsync();
        var accounts = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId).Select(l => l.AccountId)
            .Distinct().OrderBy(a => a).ToArrayAsync();
        return (payee, accounts);
    }

    // ----- live event -----

    private static async Task<Guid> CreateEventAsync(HttpClient client, Guid ledgerId, CreateInvestmentTransactionRequest shape)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/investment-transactions", shape);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
    }

    private static Task<HttpResponseMessage> PatchEventAsync(
        HttpClient client, Guid ledgerId, Guid headerId, CreateInvestmentTransactionRequest shape) =>
        client.PatchAsJsonAsync(
            $"/api/ledgers/{ledgerId}/investment-transactions/{headerId}?account_id={shape.BrokerageAccountId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = shape.BrokerageAccountId,
                PostedAt = shape.PostedAt,
                Action = shape.Action,
                Payee = shape.Payee,
                SecurityId = shape.SecurityId,
                Shares = shape.Shares,
                Price = shape.Price,
                Amount = shape.Amount,
                CategoryAccountId = shape.CategoryAccountId,
                TransferAccountId = shape.TransferAccountId,
                FeeAccountId = shape.FeeAccountId,
                FeeAmount = shape.FeeAmount,
            });

    [Theory]
    [InlineData("brokerage")]
    [InlineData("category")]
    [InlineData("transfer")]
    [InlineData("fee")]
    public async Task An_event_kept_on_a_deactivated_account_can_be_edited(string role)
    {
        var w = await WorldAsync();
        var retired = await RoleAccountAsync(w.Ledger, role, "Retired Account");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, w.Ledger);

        var headerId = await CreateEventAsync(client, w.Ledger.LedgerId, Shape(w, role, retired, "Original"));
        await DeactivateAsync(client, w.Ledger.LedgerId, retired);
        var before = await StoredAsync(headerId);

        // The same shape with a new payee: the account stays in the role it already fills.
        var resp = await PatchEventAsync(client, w.Ledger.LedgerId, headerId, Shape(w, role, retired, "Edited"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = await StoredAsync(headerId);
        Assert.Equal("Edited", after.Payee);
        Assert.Equal(before.Accounts, after.Accounts);
        Assert.Contains(retired, after.Accounts);
    }

    [Theory]
    [InlineData("brokerage")]
    [InlineData("category")]
    [InlineData("transfer")]
    [InlineData("fee")]
    public async Task Moving_an_event_onto_a_deactivated_account_is_refused(string role)
    {
        // The other half of the rule: a fix that dropped the activity check would pass
        // the test above and allow exactly what the gate exists to stop.
        var w = await WorldAsync();
        var live = await RoleAccountAsync(w.Ledger, role, "Live Account");
        var retired = await RoleAccountAsync(w.Ledger, role, "Retired Account");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, w.Ledger);

        var headerId = await CreateEventAsync(client, w.Ledger.LedgerId, Shape(w, role, live, "Original"));
        await DeactivateAsync(client, w.Ledger.LedgerId, retired);
        var before = await StoredAsync(headerId);

        var moved = await PatchEventAsync(client, w.Ledger.LedgerId, headerId, Shape(w, role, retired, "Edited"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, moved.StatusCode);
        Assert.Equal(InactiveCode(role), await ErrorCodeAsync(moved));

        // Nothing was written by the refusal.
        var after = await StoredAsync(headerId);
        Assert.Equal("Original", after.Payee);
        Assert.Equal(before.Accounts, after.Accounts);

        // A create has nothing already present, so it refuses the same account.
        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions", Shape(w, role, retired, "New"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, created.StatusCode);
        Assert.Equal(InactiveCode(role), await ErrorCodeAsync(created));
    }

    // ----- reminder template -----

    private static Task<HttpResponseMessage> CreateReminderAsync(
        HttpClient client, Guid ledgerId, CreateInvestmentTransactionRequest shape) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=15",
                StartDate = SeriesStart,
                Transaction = shape,
            });

    private static async Task<Guid> CreatedReminderAsync(
        HttpClient client, Guid ledgerId, CreateInvestmentTransactionRequest shape)
    {
        var resp = await CreateReminderAsync(client, ledgerId, shape);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    private static Task<HttpResponseMessage> EditReminderAsync(
        HttpClient client, Guid ledgerId, Guid reminderId, CreateInvestmentTransactionRequest shape) =>
        client.PatchAsJsonAsync($"/api/ledgers/{ledgerId}/reminders/{reminderId}/investment",
            new EditInvestmentReminderRequest { Transaction = shape });

    private async Task<Guid> TemplateIdAsync(Guid reminderId)
    {
        await using var db = Db.NewDbContext();
        return await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId).Select(r => r.TemplateHeaderId!.Value).SingleAsync();
    }

    private async Task<int> FiredCountAsync(Guid reminderId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.RecurringTransactionId == reminderId && !h.IsRecurringTemplate);
    }

    [Theory]
    [InlineData("brokerage")]
    [InlineData("category")]
    [InlineData("transfer")]
    [InlineData("fee")]
    public async Task A_reminder_kept_on_a_deactivated_account_can_be_edited_but_not_fired(string role)
    {
        var w = await WorldAsync();
        var retired = await RoleAccountAsync(w.Ledger, role, "Retired Account");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, w.Ledger);

        var reminderId = await CreatedReminderAsync(client, w.Ledger.LedgerId, Shape(w, role, retired, "Original"));
        var templateId = await TemplateIdAsync(reminderId);
        await DeactivateAsync(client, w.Ledger.LedgerId, retired);
        var before = await StoredAsync(templateId);

        var edit = await EditReminderAsync(client, w.Ledger.LedgerId, reminderId, Shape(w, role, retired, "Edited"));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var after = await StoredAsync(templateId);
        Assert.Equal("Edited", after.Payee);
        Assert.Equal(before.Accounts, after.Accounts);
        Assert.Contains(retired, after.Accounts);

        // Firing posts NEW activity, so the account being on the template does not
        // excuse it there.
        var fire = await client.PostAsJsonAsync(
            $"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminderId}/fire/investment",
            new FireInvestmentReminderRequest
            {
                OccurrenceDate = SeriesStart,
                Transaction = Shape(w, role, retired, "Edited"),
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fire.StatusCode);
        Assert.Equal(InactiveCode(role), await ErrorCodeAsync(fire));
        Assert.Equal(0, await FiredCountAsync(reminderId));
    }

    [Theory]
    [InlineData("brokerage")]
    [InlineData("category")]
    [InlineData("transfer")]
    [InlineData("fee")]
    public async Task A_reminder_created_on_a_deactivated_account_is_refused(string role)
    {
        var w = await WorldAsync();
        var retired = await RoleAccountAsync(w.Ledger, role, "Retired Account");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, w.Ledger);
        await DeactivateAsync(client, w.Ledger.LedgerId, retired);

        var resp = await CreateReminderAsync(client, w.Ledger.LedgerId, Shape(w, role, retired, "New"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(InactiveCode(role), await ErrorCodeAsync(resp));
    }

    // A reminder's brokerage is its source and cannot change on an edit, so moving
    // the brokerage role is refused as source-immutable before any account check.
    [Theory]
    [InlineData("category")]
    [InlineData("transfer")]
    [InlineData("fee")]
    public async Task Moving_a_reminder_onto_a_deactivated_account_is_refused(string role)
    {
        var w = await WorldAsync();
        var live = await RoleAccountAsync(w.Ledger, role, "Live Account");
        var retired = await RoleAccountAsync(w.Ledger, role, "Retired Account");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, w.Ledger);

        var reminderId = await CreatedReminderAsync(client, w.Ledger.LedgerId, Shape(w, role, live, "Original"));
        var templateId = await TemplateIdAsync(reminderId);
        await DeactivateAsync(client, w.Ledger.LedgerId, retired);
        var before = await StoredAsync(templateId);

        var moved = await EditReminderAsync(client, w.Ledger.LedgerId, reminderId, Shape(w, role, retired, "Edited"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, moved.StatusCode);
        Assert.Equal(InactiveCode(role), await ErrorCodeAsync(moved));

        // Nothing was written by the refusal.
        var after = await StoredAsync(templateId);
        Assert.Equal("Original", after.Payee);
        Assert.Equal(before.Accounts, after.Accounts);
    }
}
