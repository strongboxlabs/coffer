using System.Net;
using System.Text.Json;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// A ledger's notification settings are readable and writable by its holders only.
/// </summary>
/// <remarks>
/// This is the practical payoff of ADR-0096 D1's scope split, and the part with teeth:
/// a ledger holder configures their own delivery without being an admin, and someone
/// with no grant on the ledger cannot read where its notifications go — a delivery URL
/// is a credential, and the list of them is a map of someone's integrations.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerNotificationsEndpointTests
{
    private readonly PostgresFixture _fixture;

    public LedgerNotificationsEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task A_new_ledger_starts_with_no_targets_of_its_own()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var initial = await client.GetFromJsonAsync<LedgerNotificationSettingsDto>(
            $"/api/ledgers/{ledger.LedgerId}/notifications");

        // Replaces A_holder_reads_inherit_by_default_and_can_switch_to_own. There is no
        // mode: migration 214 retired 'inherit' because it routed a ledger's events to
        // EVERY deployment target with no ledger filter, and it was the default — so on a
        // shared install one ledger's activity reached whoever watched the deployment
        // channel until someone opted out.
        //
        // The old test's comment argued inherit-by-default was right because "a new
        // ledger must not be silent until someone configures it". That trade is now made
        // the other way, and the cost is real: a NEW ledger genuinely does report nowhere
        // until its owner adds a target. That is why the panel says so out loud, and why
        // migration 214 moves existing ledgers' inherited targets in rather than leaving
        // installs in the field silent.
        Assert.Empty(initial!.Subscribers);
    }

    [Fact]
    public async Task A_target_is_added_and_its_url_is_never_returned()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        const string secretUrl = "https://hook.invalid/ledger-token-in-the-path";
        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                // A message provider, not a dead-man's switch: this scope refuses those
                // outright — see Heartbeat_targets_are_refused_at_ledger_scope below.
                // The leak this test is about is not capability-specific.
                SubscriberKey = "webhook",
                Url = secretUrl,
                MinSeverity = "warning",
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        // Asserting on the raw body, not the DTO: the leak that matters is whatever
        // actually crosses the wire.
        var body = await (await client.GetAsync(
                $"/api/ledgers/{ledger.LedgerId}/notifications"))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain("ledger-token-in-the-path", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_switch_can_watch_one_of_this_ledgers_own_jobs()
    {
        // Replaces Heartbeat_targets_are_refused_at_ledger_scope, added earlier in this
        // same branch. That refusal was right about the code as it stood — every monitor
        // was a deployment job, so a ledger switch could never fire — and wrong about the
        // design. quote-refresh and snapshot ARE per-ledger scheduled jobs, and the
        // scheduler now emits a Success/Failure signal per run for each, so a ledger
        // switch is exactly the right instrument.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "healthchecks",
                DisplayName = "Quote refresh switch",
                Url = "https://hc.invalid/quotes",
                MinSeverity = "warning",
                Monitors = NotificationMonitors.QuoteRefresh,
            });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var settings = await client.GetFromJsonAsync<LedgerNotificationSettingsDto>(
            $"/api/ledgers/{ledger.LedgerId}/notifications");
        var target = Assert.Single(settings!.Subscribers);
        Assert.Equal(NotificationMonitors.QuoteRefresh, target.Monitors);

        // And the dropdown offers it, so the write path is a backstop rather than the
        // only thing standing between a holder and a switch they cannot create.
        var providers = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/ledgers/{ledger.LedgerId}/notifications/providers");
        Assert.Contains(providers!, p =>
            p.GetProperty("subscriberKey").GetString() == "healthchecks"
            && p.GetProperty("detectsAbsence").GetBoolean());
    }

    [Fact]
    public async Task A_switch_cannot_watch_a_job_no_ledger_runs()
    {
        // The deployment's backup is not a per-ledger job. A ledger switch bound to it
        // would sit there enabled, reporting no error, and never fire — the exact state a
        // dead-man's switch exists to detect, manufactured by the screen offering it.
        // This is why NotificationMonitors.IsKnown takes a scope.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "healthchecks",
                Url = "https://hc.invalid/backup",
                MinSeverity = "warning",
                Monitors = NotificationMonitors.Backup,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, created.StatusCode);
    }

    [Fact]
    public async Task An_unbound_switch_is_refused_because_it_would_watch_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "healthchecks",
                Url = "https://hc.invalid/unbound",
                MinSeverity = "warning",
            });

        // One URL is one check, so an unbound switch has nothing to be the check FOR.
        // Accepted, it would look configured and receive nothing.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, created.StatusCode);
    }

    [Fact]
    public async Task Coverage_names_only_the_jobs_this_ledger_has_enabled()
    {
        // The cry-wolf boundary, at the API. A ledger with no scheduled jobs must not be
        // told that nothing is watching jobs it does not run.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var before = await client.GetFromJsonAsync<LedgerNotificationSettingsDto>(
            $"/api/ledgers/{ledger.LedgerId}/notifications");
        Assert.Empty(before!.MonitorCoverage);

        await using (var db = _fixture.NewDbContext())
        {
            db.ScheduledJobs.Add(new Db.Entities.ScheduledJobRow
            {
                LedgerId = ledger.LedgerId,
                JobType = NotificationMonitors.QuoteRefresh,
                Enabled = true,
                HourLocal = 3,
                MinuteLocal = 0,
                ConfiguredByUserId = ledger.UserId,
            });
            db.ScheduledJobs.Add(new Db.Entities.ScheduledJobRow
            {
                LedgerId = ledger.LedgerId,
                JobType = NotificationMonitors.Snapshot,
                Enabled = false,
                HourLocal = 4,
                MinuteLocal = 0,
                ConfiguredByUserId = ledger.UserId,
            });
            await db.SaveChangesAsync();
        }

        var after = await client.GetFromJsonAsync<LedgerNotificationSettingsDto>(
            $"/api/ledgers/{ledger.LedgerId}/notifications");

        // The enabled job appears and is uncovered; the DISABLED one is absent entirely,
        // rather than present-and-uncovered.
        Assert.False(Assert.Contains(NotificationMonitors.QuoteRefresh, after!.MonitorCoverage));
        Assert.DoesNotContain(NotificationMonitors.Snapshot, after.MonitorCoverage.Keys);
    }

    [Fact]
    public async Task A_switch_bound_to_a_job_this_ledger_does_not_run_says_so()
    {
        // The exact state a real bank-feed check sat in: a healthchecks URL bound to
        // feed-sync on a ledger that never enabled feed-sync. It read "Never" for months
        // and NEITHER half of this response could say why. The job does not run, so
        // nothing pings it; and MonitorCoverage is keyed on the jobs that DO run, so the
        // switch matches no key and vanishes from the answer rather than showing up
        // uncovered. Silence from both directions is what made it invisible.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "healthchecks",
                Url = "https://hc.invalid/bank-feed",
                MinSeverity = "warning",
                Monitors = NotificationMonitors.FeedSync,
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var before = await client.GetFromJsonAsync<LedgerNotificationSettingsDto>(
            $"/api/ledgers/{ledger.LedgerId}/notifications");

        Assert.Contains(NotificationMonitors.FeedSync, before!.MonitorsWatchingNothing);

        // Pinned deliberately: this is WHY the new list has to exist. If coverage could
        // report the case, the list would be redundant.
        Assert.DoesNotContain(NotificationMonitors.FeedSync, before.MonitorCoverage.Keys);

        await using (var db = _fixture.NewDbContext())
        {
            db.ScheduledJobs.Add(new Db.Entities.ScheduledJobRow
            {
                LedgerId = ledger.LedgerId,
                JobType = NotificationMonitors.FeedSync,
                Enabled = true,
                HourLocal = 5,
                MinuteLocal = 0,
                ConfiguredByUserId = ledger.UserId,
            });
            await db.SaveChangesAsync();
        }

        var after = await client.GetFromJsonAsync<LedgerNotificationSettingsDto>(
            $"/api/ledgers/{ledger.LedgerId}/notifications");

        // Turning the job on clears the complaint and moves it into coverage as watched.
        // Both halves are asserted: a version that never cleared the warning would still
        // pass the first assertion alone.
        Assert.DoesNotContain(NotificationMonitors.FeedSync, after!.MonitorsWatchingNothing);
        Assert.True(Assert.Contains(NotificationMonitors.FeedSync, after.MonitorCoverage));
    }

    [Fact]
    public async Task The_consistency_switch_is_never_called_idle_because_it_is_not_a_job()
    {
        // Consistency is the declared non-job monitor: it runs on every scheduler tick
        // and cannot be switched off, which is the entire reason it is worth binding.
        // Testing "is there an enabled scheduled_jobs row" against it would find none and
        // report the one monitor that ALWAYS runs as watching nothing — a warning telling
        // its owner to turn on a job that does not exist. That is the cry-wolf failure
        // this subsystem keeps designing against, so the exclusion is pinned here.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "healthchecks",
                Url = "https://hc.invalid/consistency",
                MinSeverity = "warning",
                Monitors = NotificationMonitors.Consistency,
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var settings = await client.GetFromJsonAsync<LedgerNotificationSettingsDto>(
            $"/api/ledgers/{ledger.LedgerId}/notifications");

        Assert.DoesNotContain(NotificationMonitors.Consistency, settings!.MonitorsWatchingNothing);
    }

    [Fact]
    public async Task Someone_without_a_grant_cannot_read_or_change_another_ledgers_settings()
    {
        var mine = await SyntheticLedger.CreateAsync(_fixture);
        var theirs = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await ClientAsync(factory, mine);

        // Reading another ledger's targets would expose a map of someone's
        // integrations; adding one would redirect their alerts to a host they chose.
        var read = await client.GetAsync($"/api/ledgers/{theirs.LedgerId}/notifications");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, read.StatusCode);

        var add = await client.PostAsJsonAsync(
            $"/api/ledgers/{theirs.LedgerId}/notifications/subscribers",
            new CreateNotificationSubscriberRequest
            {
                SubscriberKey = "webhook", Url = "https://evil.invalid/x",
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, add.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_read_a_ledgers_targets_but_not_change_them()
    {
        // The DB backstop for ADR-0083 D2, proved the way LedgerRoleTests proves it:
        // under an RLS-scoped coffer_app context, a viewer's write must match ZERO
        // rows. Migration 208 originally copied the retired mig-017 shape — one
        // FOR ALL policy gated on the mere PRESENCE of a grant — which migration 174
        // removed from every ledger table precisely because it let a viewer write. A
        // delivery URL is a credential and its target list is a map of someone's
        // integrations, so a viewer silently redirecting a ledger's alerts is the
        // worst version of this bug.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var viewerId = await ledger.AddMemberAsync("viewer");
        var editorId = await ledger.AddMemberAsync("editor");

        Guid targetId;
        await using (var seed = ledger.NewDbContext())
        {
            var row = new LedgerNotificationSubscriberRow
            {
                LedgerId = ledger.LedgerId,
                SubscriberKey = "webhook",
                DisplayName = "seeded",
                ConfigCiphertext = [1, 2, 3],
            };
            seed.LedgerNotificationSubscribers.Add(row);
            await seed.SaveChangesAsync();
            targetId = row.Id;
        }

        await using (var viewerDb = _fixture.NewAppDbContextAsUser(viewerId))
        {
            // Reading is allowed for any grant — the _read policy.
            Assert.True(await viewerDb.LedgerNotificationSubscribers
                .AnyAsync(t => t.Id == targetId));

            Assert.Equal(0, await viewerDb.LedgerNotificationSubscribers
                .Where(t => t.Id == targetId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsEnabled, false)));

            Assert.Equal(0, await viewerDb.LedgerNotificationSubscribers
                .Where(t => t.Id == targetId)
                .ExecuteDeleteAsync());
        }

        // Same statement, editor grant: it must land, or the policy is simply broken
        // rather than correctly restrictive.
        await using (var editorDb = _fixture.NewAppDbContextAsUser(editorId))
        {
            Assert.Equal(1, await editorDb.LedgerNotificationSubscribers
                .Where(t => t.Id == targetId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsEnabled, false)));
        }
    }
}
