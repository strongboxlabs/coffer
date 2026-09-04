using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// One fire, one occurrence — enforced by the database (migration 218), not by a
/// read-then-write in application code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are deterministic rather than concurrent.</b> The obvious test is "fire
/// the same slot from two tasks and assert one header". It is also the test that can
/// pass while proving nothing: if the two tasks do not actually interleave — and at
/// READ COMMITTED with a fast local database they usually do not — it passes with the
/// unique index dropped and the lock deleted. So the index is proved by provoking the
/// violation directly, and the lock by observing that it BLOCKS, with a statement
/// timeout turning "blocked" into a deterministic, fast assertion.
/// </para>
/// <para>
/// Raw SQL appears below only to set <c>statement_timeout</c> and to take a lock from
/// the test's own second connection. The no-raw-SQL rule governs <c>src/Api</c>; a test
/// that needs to hold a lock from outside the code under test has no other way to do it.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderOccurrenceUniquenessTests
{
    private readonly PostgresFixture _fixture;

    public ReminderOccurrenceUniquenessTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    /// <summary>Create a monthly bank reminder and return its series id.</summary>
    private static async Task<Guid> NewSeriesAsync(HttpClient client, SyntheticLedger ledger, Guid bankId, Guid catId)
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

    /// <summary>
    /// The index itself: a second committed header for the same (series, occurrence) is
    /// refused by Postgres.
    /// </summary>
    /// <remarks>
    /// Provoked directly rather than through two racing fires, so it fails the moment
    /// the index is missing instead of only when an interleaving happens to occur.
    /// </remarks>
    [Fact]
    public async Task A_second_committed_header_for_one_occurrence_is_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id);

        var slot = new DateOnly(2026, 1, 1);

        await using var db = _fixture.NewServiceDbContext();
        db.Add(NewOccurrenceHeader(ledger.LedgerId, seriesId, slot));
        await db.SaveChangesAsync();

        db.Add(NewOccurrenceHeader(ledger.LedgerId, seriesId, slot));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
        Assert.Equal("uq_txn_headers_recurring_occurrence", pg.ConstraintName);
    }

    /// <summary>
    /// The template header is not what the first fire collides with.
    /// </summary>
    /// <remarks>
    /// Written believing the template carried its series id, which would have made
    /// <c>NOT is_recurring_template</c> the predicate keeping every series fireable. It
    /// does not: the link runs the other way, through
    /// <c>recurring_transactions.template_header_id</c>, and the template's own
    /// <c>recurring_transaction_id</c> is NULL — so the index's <c>IS NOT NULL</c>
    /// predicate already excludes it. The assertion is kept, inverted, because that null
    /// is now load-bearing: if templates are ever stamped to make the link symmetric,
    /// this fails and points at the index rather than at a series that has mysteriously
    /// become unfireable.
    /// </remarks>
    [Fact]
    public async Task The_template_header_carries_no_occurrence_stamp_and_does_not_block_the_first_fire()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id);

        await using (var probe = _fixture.NewServiceDbContext())
        {
            var template = await probe.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.IsRecurringTemplate);
            Assert.True(
                template.RecurringTransactionId is null,
                "the template header now carries a recurring_transaction_id. The unique "
                + "index excludes templates only via NOT is_recurring_template in that "
                + "case — confirm that predicate is still present before changing this.");
        }

        var fire = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire",
            new FireReminderRequest { OccurrenceDate = new DateOnly(2026, 1, 1) });

        Assert.Equal(HttpStatusCode.OK, fire.StatusCode);
    }

    /// <summary>
    /// Firing a slot that is already committed still answers OK with the existing
    /// header, rather than surfacing a duplicate-key failure.
    /// </summary>
    [Fact]
    public async Task Firing_an_already_committed_slot_is_idempotent()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewSeriesAsync(client, ledger, bank.Id, cat.Id);

        var slot = new DateOnly(2026, 1, 1);
        var url = $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire";

        var first = await client.PostAsJsonAsync(url, new FireReminderRequest { OccurrenceDate = slot });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync(url, new FireReminderRequest { OccurrenceDate = slot });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var db = _fixture.NewServiceDbContext();
        var committed = await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.RecurringTransactionId == seriesId
                             && h.OccurrenceDate == slot
                             && !h.IsRecurringTemplate);
        Assert.Equal(1, committed);
    }

    /// <summary>
    /// The slot lock EXCLUDES: while one transaction holds it, a second attempt on the
    /// same slot waits rather than proceeding.
    /// </summary>
    /// <remarks>
    /// "Waits" is asserted as a statement timeout, which makes it deterministic and
    /// bounded instead of a sleep-and-hope. This is the guarantee no constraint can
    /// give, because fire and skip write to different tables.
    /// </remarks>
    [Fact]
    public async Task The_slot_lock_blocks_a_second_holder_of_the_same_slot()
    {
        var seriesId = Guid.NewGuid();
        var slot = new DateOnly(2026, 5, 1);

        await using var holder = new NpgsqlConnection(_fixture.ServiceConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await TakeLockAsync(holder, holderTx, seriesId, slot);

        await using var contender = new NpgsqlConnection(_fixture.ServiceConnectionString);
        await contender.OpenAsync();
        await using var contenderTx = await contender.BeginTransactionAsync();
        await SetShortTimeoutAsync(contender, contenderTx);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => TakeLockAsync(contender, contenderTx, seriesId, slot));

        Assert.Equal(PostgresErrorCodes.QueryCanceled, ex.SqlState);
    }

    /// <summary>
    /// ...and it excludes the SLOT, not the series. A catch-up run posting a backlog
    /// must not serialise against itself.
    /// </summary>
    /// <remarks>
    /// Without this, keying the lock on the series alone passes the exclusion test above
    /// and quietly turns every multi-occurrence run into a queue.
    /// </remarks>
    [Fact]
    public async Task The_slot_lock_does_not_block_a_different_occurrence_of_the_same_series()
    {
        var seriesId = Guid.NewGuid();

        await using var holder = new NpgsqlConnection(_fixture.ServiceConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await TakeLockAsync(holder, holderTx, seriesId, new DateOnly(2026, 5, 1));

        await using var other = new NpgsqlConnection(_fixture.ServiceConnectionString);
        await other.OpenAsync();
        await using var otherTx = await other.BeginTransactionAsync();
        await SetShortTimeoutAsync(other, otherTx);

        // No throw: a different date in the same series is a different slot.
        await TakeLockAsync(other, otherTx, seriesId, new DateOnly(2026, 6, 1));
    }

    private static TxnHeaderRow NewOccurrenceHeader(Guid ledgerId, Guid seriesId, DateOnly slot) =>
        new()
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Origin = "manual",
            Payee = "Rent",
            PostedAt = slot.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            TransactedAt = slot.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CreatedAt = slot.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            IsRecurringTemplate = false,
            RecurringTransactionId = seriesId,
            OccurrenceDate = slot,
        };

    private static async Task TakeLockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid seriesId, DateOnly slot)
    {
        await using var command = new NpgsqlCommand(
            "SELECT locked FROM reminder_occurrence_lock(@s, @d)", connection, transaction);
        command.Parameters.AddWithValue("s", seriesId);
        command.Parameters.AddWithValue("d", slot);
        await command.ExecuteScalarAsync();
    }

    /// <summary>One second, so a blocked contender fails fast instead of hanging the suite.</summary>
    private static async Task SetShortTimeoutAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            "SET LOCAL statement_timeout = '1s'", connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
