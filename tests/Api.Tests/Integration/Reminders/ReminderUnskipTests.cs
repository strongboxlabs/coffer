using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// Un-skip: undoing a suppressed occurrence.
/// </summary>
/// <remarks>
/// Three fire paths have rejected a suppressed slot with "That occurrence was skipped;
/// un-skip it before firing" since before any un-skip existed, so a mis-clicked Skip was
/// permanent and the message sent people looking for a control nobody had built.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderUnskipTests
{
    private readonly PostgresFixture _fixture;

    public ReminderUnskipTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateOnly Slot = new(2026, 4, 1);

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static async Task<Guid> NewSeriesAsync(
        HttpClient client, SyntheticLedger ledger, Guid bankId, Guid catId)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                Payee = "Rent",
                SourceAccountId = bankId,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = catId, Amount = -1500m } },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    private sealed record Fixture(SyntheticLedger Ledger, HttpClient Client, Guid SeriesId, string BaseUrl);

    private async Task<Fixture> SeriesWithSkippedSlotAsync(ApiFactory factory)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id);
        var baseUrl = $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}";

        var skip = await client.PostAsJsonAsync(
            $"{baseUrl}/skip", new SkipReminderRequest { OccurrenceDate = Slot });
        Assert.Equal(HttpStatusCode.OK, skip.StatusCode);

        return new Fixture(ledger, client, seriesId, baseUrl);
    }

    private async Task<int> ExceptionCountAsync(Guid seriesId, DateOnly? slot = null)
    {
        await using var db = _fixture.NewServiceDbContext();
        var q = db.RecurringOccurrenceExceptions.AsNoTracking()
            .Where(e => e.RecurringTransactionId == seriesId);
        if (slot is { } d) q = q.Where(e => e.OccurrenceDate == d);
        return await q.CountAsync();
    }

    /// <summary>
    /// The point of the feature: after un-skipping, the occurrence can be FIRED.
    /// </summary>
    /// <remarks>
    /// Asserting the exception row is gone would be the easy half and would pass for an
    /// implementation that deleted the row and left the cursor or the fire path believing
    /// the slot was still suppressed. What the three error messages promise is that
    /// firing works afterwards, so that is what this asserts.
    /// </remarks>
    [Fact]
    public async Task An_unskipped_occurrence_can_then_be_fired()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var f = await SeriesWithSkippedSlotAsync(factory);
        using var client = f.Client;

        // Precondition: firing is refused while it is skipped. Without this the test
        // could pass against a build where firing never checked skips at all.
        var refused = await client.PostAsJsonAsync(
            $"{f.BaseUrl}/fire", new FireReminderRequest { OccurrenceDate = Slot });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var unskip = await client.DeleteAsync($"{f.BaseUrl}/skip?occurrenceDate={Slot:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, unskip.StatusCode);

        var fired = await client.PostAsJsonAsync(
            $"{f.BaseUrl}/fire", new FireReminderRequest { OccurrenceDate = Slot });
        Assert.Equal(HttpStatusCode.OK, fired.StatusCode);
    }

    /// <summary>
    /// It removes the one slot asked for, and leaves the cascade's other rows alone.
    /// </summary>
    /// <remarks>
    /// Skipping April also swept the un-acted January, February and March slots. Undoing
    /// that sweep would mean guessing which of them the user wanted back; each is
    /// independently un-skippable instead. This pins that choice, because "un-skip"
    /// invites the opposite implementation.
    /// </remarks>
    [Fact]
    public async Task Unskip_touches_only_the_occurrence_asked_for()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var f = await SeriesWithSkippedSlotAsync(factory);
        using var client = f.Client;

        var before = await ExceptionCountAsync(f.SeriesId);
        Assert.True(
            before > 1,
            $"expected the skip to have cascaded over earlier slots, but only {before} "
            + "exception row(s) exist — the cascade assertion below would be vacuous");

        var unskip = await client.DeleteAsync($"{f.BaseUrl}/skip?occurrenceDate={Slot:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, unskip.StatusCode);

        Assert.Equal(0, await ExceptionCountAsync(f.SeriesId, Slot));
        Assert.Equal(before - 1, await ExceptionCountAsync(f.SeriesId));
    }

    /// <summary>
    /// The series cursor moves back to the un-skipped occurrence when it becomes the
    /// earliest un-acted one.
    /// </summary>
    /// <remarks>
    /// The cursor is what the agenda and the auto-post scan read. Deleting the exception
    /// row without recomputing it would leave the slot fireable by URL and invisible
    /// everywhere a person would look for it.
    /// </remarks>
    [Fact]
    public async Task Unskipping_the_earliest_slot_moves_the_cursor_back_to_it()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var f = await SeriesWithSkippedSlotAsync(factory);
        using var client = f.Client;

        // Clear the whole cascade so the target slot really is the earliest un-acted one.
        foreach (var month in new[] { 1, 2, 3 })
        {
            var d = new DateOnly(2026, month, 1);
            await client.DeleteAsync($"{f.BaseUrl}/skip?occurrenceDate={d:yyyy-MM-dd}");
        }

        var resp = await client.DeleteAsync($"{f.BaseUrl}/skip?occurrenceDate={Slot:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<UnskipReminderResponse>();
        Assert.NotNull(body);
        Assert.Equal(new DateOnly(2026, 1, 1), body!.NextDueDate);

        // And the stored cursor agrees with what the response claimed.
        await using var db = _fixture.NewServiceDbContext();
        var stored = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == f.SeriesId)
            .Select(r => r.NextDueDate)
            .SingleAsync();
        Assert.Equal(body.NextDueDate, stored);
    }

    /// <summary>
    /// Un-skipping a slot that is not skipped is a no-op, not an error.
    /// </summary>
    /// <remarks>
    /// Matches skip's own idempotence. A double-click, a retried request or two open tabs
    /// should not produce a failure for asking for a state the slot is already in.
    /// </remarks>
    [Fact]
    public async Task Unskipping_a_slot_that_is_not_skipped_is_a_no_op()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var f = await SeriesWithSkippedSlotAsync(factory);
        using var client = f.Client;

        var first = await client.DeleteAsync($"{f.BaseUrl}/skip?occurrenceDate={Slot:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.DeleteAsync($"{f.BaseUrl}/skip?occurrenceDate={Slot:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(0, await ExceptionCountAsync(f.SeriesId, Slot));
    }

    /// <summary>
    /// Another ledger's reminder cannot be un-skipped, even with its id.
    /// </summary>
    [Fact]
    public async Task Unskip_is_refused_across_ledgers()
    {
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var mine = await SeriesWithSkippedSlotAsync(factory);
        using var myClient = mine.Client;

        var other = await SyntheticLedger.CreateAsync(_fixture);
        using var otherClient = await AuthedClientAsync(factory, other);

        // Ledger B's holder, asking about ledger A's reminder id under their OWN ledger.
        var resp = await otherClient.DeleteAsync(
            $"/api/ledgers/{other.LedgerId}/reminders/{mine.SeriesId}/skip"
            + $"?occurrenceDate={Slot:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        // And A's skip is untouched.
        Assert.Equal(1, await ExceptionCountAsync(mine.SeriesId, Slot));
    }
}
