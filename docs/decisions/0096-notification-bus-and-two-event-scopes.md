# 0096 — A notification bus, and two event scopes that stay apart

* Status: Accepted
* Date: 2026-08-19
* Relates to: [ADR-0055](0055-generic-provider-run-audit.md) (`ledger_operations` run log), [ADR-0037](0037-snapshots-and-backups.md) (snapshots + backups), [ADR-0060](0060-whole-db-backup-and-admin-role.md) (whole-DB backup)

## Context

On 2026-08-13 the snapshot and backup jobs stopped. Snapshots were dead for
roughly 68 hours and backups for 47, and **nothing said anything**. The only
trace was `LogError` lines inside a container and an unrelated-looking 500 from
`/oauth/authorize`. Migration 194 later added `consecutive_failures`,
`last_error` and `last_failure_at`, and `SchedulerRunner` disables a job after
five consecutive failures — but none of that is surfaced or pushed anywhere, so
the raw material for an alert exists and no alert does.

On 2026-08-18 a second instance of the same shape surfaced. A one-off data scrub
had reshaped in-kind transfers on three accounts, correctly recomputed the FIFO
projections, and never recomputed balances. The register showed wrong figures on
those accounts for **months**. It came to light because somebody happened to run
a maintenance action by hand. Migration 206 made that state *checkable* without
writing to it, which is necessary and not sufficient: a check nobody runs is a
check nobody runs.

Both incidents are the same defect, and it is not a missing check. It is that
**Coffer has no way to tell anyone anything.** There is no SMTP path, no webhook,
no push of any kind. What exists:

* `ledger_operations` — a per-ledger run log (families `ingest`, `quote`,
  `snapshot`), with `status`, `error_message` and a `details` jsonb. Records
  history; announces nothing.
* `admin_audit_events`, `global_scheduled_jobs`, `system_settings`,
  `backup_settings` — deployment-scope tables.
* `/healthz` and `/readyz` — anonymous orchestrator probes.

## Decisions

### D1 — Two event SCOPES, two tables, and they never merge

Events divide into **ledger scope** (a sync completed, quotes refreshed, a
snapshot was taken, this ledger's projections disagree) and **deployment scope**
(a backup succeeded or didn't, the master key was rotated, a scheduled job
auto-disabled, the worker is not running).

The schema already has this grain — `ledger_operations` on one side,
`admin_audit_events` and `global_scheduled_jobs` on the other — so this decision
follows it rather than cutting across it. Deployment-scope notifications get a
new `system_events` table; ledger-scope notifications extend `ledger_operations`
rather than duplicating it.

They stay apart for four reasons, in ascending order of how much they cost to get
wrong:

1. **Authorization is structurally different.** A ledger event is gated by grant,
   which is an RLS question. A system event has no ledger to gate on; the
   question is whether the caller is an admin. One table with a nullable
   `ledger_id` puts both under one policy, and a NULL scope is exactly where
   fail-open and fail-closed bugs live. This codebase's authorization boundary
   *is* RLS (`coffer_app` NOBYPASSRLS vs `coffer_service` BYPASSRLS), and the
   incident that prompted this ADR was invisible state. A nullable-scope policy
   on the table meant to make things visible would be a poor joke.
2. **Lifecycle differs.** A ledger event belongs to its ledger and should die
   with it. "The backup failed" is a fact about the *installation* and must
   outlive deleting a ledger. A shared table with a foreign key means deleting a
   ledger eats system history.
3. **Snapshots.** A ledger snapshot captures per-ledger tables (ADR-0037).
   System events must never ride inside one, or restoring last week's snapshot
   resurrects stale system history — or captures part of it, which is worse.
4. **Audience.** System events go to whoever operates the install. Ledger events
   go to the people who hold that ledger.

### D2 — One bus, one subscriber contract

Storage and authorization are per-scope; **publication is not**. A producer
publishes an event; subscribers declare what they want by scope, severity and
topic, and the bus routes. The alternative — each delivery path re-deriving "is
this system-wide?" from a shared stream — is the same conflation D1 rejects,
moved into the subscribers.

### D3 — Severity and topic are orthogonal

An event carries both:

* **Severity** — `info` (routine and expected: a backup succeeded, a sync
  finished), `warning` (working but degraded: a partial sync, drift found),
  `critical` (a human is needed: no successful backup in 48 hours, a job
  auto-disabled).
* **Topic** — `backup`, `snapshot`, `sync`, `quotes`, `consistency`,
  `scheduler`.

These are independent, and collapsing them into one enum is a trap: "successful
backup" and "quote update" are both routine but belong to different topics, while
"backup failed" and "quote provider unreachable" share a severity and want very
different handling. Subscribers filter on both — the activity feed takes
everything for a ledger, an in-app badge takes `warning` and `critical`, an
external monitor takes named topics.

### D4 — Publishing is not persisting

`ledger_operations` records every four-hourly quote refresh, and it should keep
doing so. **A log is not a notification.** If every activity row becomes a
notification the channel becomes noise and stops being read — the same
cry-wolf failure that makes a badly-specified consistency check worthless. The
log keeps everything; the bus carries what warrants attention; severity is the
selector.

### D5 — For deployment scope, the external heartbeat is PRIMARY and the table is history

This is the decision the other four exist to support, and it inverts the obvious
design.

**A system event about the system being broken cannot be written by the broken
system.** If Postgres is unreachable or the container is dead, nothing writes
`system_events`, no subscriber fires, and no in-app cue lights up. That is
precisely what happened for 68 hours: the app was not reporting a problem
because the app was not running the job.

So absence-detection has to live **outside** the thing it monitors. A
dead-man's-switch monitor — healthchecks.io or equivalent — is pinged on success
and alerts when the pings *stop*. Nothing inside the deployment can offer that
guarantee, however many tables it writes.

Consequences that follow:

* The healthchecks.io subscriber is **not** a message sink. It needs heartbeat
  semantics (`/ping` on success, `/fail` on a critical event), so the subscriber
  contract must express "this event is a heartbeat for topic X", not only "here
  is a message".
* Its URL is a **secret** and belongs with the existing secret handling (the
  `secrets/` docker-secret pattern), not in a settings column.
* Ledger scope takes the opposite trade deliberately: DB-backed and pull-based,
  surfaced in-app. A dead app means nobody is looking at a ledger anyway, so
  there is nothing to miss.

### D6 — `/readyz` does not report data inconsistency

Readiness gates traffic. Making it fail because a posting count disagrees would
take the install offline over a cosmetic discrepancy. Consistency belongs to
`system_events` and a heartbeat topic, not to a probe an orchestrator uses to
decide whether to route requests.

### D7 — Subscribers are PROVIDERS, and they come in two capability classes

Delivery is pluggable, on the `IQuotePullProvider` pattern already proven here
(ADR-0033): an `INotificationSubscriber` declares a `SubscriberKey` and a
`DisplayName`, the registry is DI, and the publisher routes. There is no reason to
invent a second provider pattern for the same shape of problem.

The part that is NOT cosmetic is that providers divide by capability:

| class | examples | detects absence |
|---|---|---|
| **heartbeat** | healthchecks.io (self-hosted or SaaS), Uptime Kuma push | **yes** — pinged on success, alerts on silence |
| **message** | ntfy, Discord, Slack, generic webhook | no — delivers what it is given |

Only a heartbeat provider delivers the guarantee D5 exists for. A message provider
cannot notice that nothing arrived, because noticing requires something outside the
deployment to be counting.

So the interface **declares its class**, and the configuration surface **warns when
no heartbeat-capable subscriber is configured**. Without that warning, provider
choice becomes a way to opt out of the entire point of this ADR by accident:
someone wires up a Discord webhook, sees test messages arrive, believes they are
covered, and has silently kept the 68-hour failure mode.

### D8 — Subscriber credentials are sealed in the database, not docker secrets

A provider URL or token is stored in a `notification_subscribers` row, sealed with
the master key via `LedgerKeyService.OpenWithMasterKey` — the pattern Drive sync
already uses for its outbound OAuth blob.

Not the `secrets/` docker-secret pattern, despite that being where the database
passwords live, and the distinction is worth stating because it looked like the
obvious answer: **docker secrets are for credentials the application needs BEFORE
it can read a database.** Postgres passwords qualify. A webhook URL does not — it
is a user-configurable integration, added and changed at runtime, and one file per
provider neither scales past the first nor survives adding a second without a
container restart.

### D9 — Coverage is one-directional, so a switch on a stopped job is reported separately

The ledger coverage map answers one question: *of the jobs this ledger runs, which
have a switch watching them?* It is keyed on the ledger's ENABLED jobs on purpose
(D5's cry-wolf rule — a ledger with snapshots off must not be warned that nothing
watches its snapshots).

That keying has a blind spot, and it is not a bug in the map so much as the shape
of the question. A switch bound to a job that is **not** enabled matches no key.
It does not appear as uncovered; it does not appear at all. And nothing else
reports it either, because the job never runs and therefore never pings. The
result is a check that can only ever read "never", with no surface anywhere
saying why.

This is not hypothetical: a real deployment's bank-feed check sat at "never" for
months. The heartbeat URL was configured correctly, the handler was registered,
the monitor keys matched on both sides — the feed-sync schedule had simply never
been switched on, and every part of the system was silent about the combination.

So the API returns both halves. `LedgerMonitorStatus` carries the coverage map
*and* `WatchingNothing` — monitors a switch is bound to whose job is not enabled
here — and the settings panel renders a warning for each, with opposite advice:
one says *add a switch*, the other says *turn the job on or remove the switch*.

`consistency` is excluded from the second list. It is the declared non-job
monitor (it runs every tick and cannot be disabled), so testing it for an enabled
`scheduled_jobs` row would find none and flag the one monitor that always runs —
precisely the false warning D5 exists to prevent.

The general rule this instance teaches: a coverage metric computed over one set
cannot report the elements that fall outside that set, and those elements are
often the interesting ones. Asking "what did the denominator exclude?" is worth
doing every time a system reports coverage.

## Consequences

* Two tables and one bus: more moving parts than a single stream, bought
  deliberately with D1's four reasons.
* The first alert to build is **backup age**, not consistency drift. Drift is
  slow damage that a repair fixes; a missing backup is data that does not come
  back. Consistency wires into the same bus afterwards.
* An external dependency (healthchecks.io) becomes load-bearing for the one
  guarantee nothing internal can provide. Self-hosters who want no external
  service keep in-app cues and lose absence-detection — that trade should be
  stated plainly in the docs rather than papered over.
* `ledger_operations` keeps its shape, so nothing existing has to be migrated to
  land D2–D4.
* Provider choice is the user's, but the heartbeat warning (D7) means the trade is
  visible rather than silent: configure only message providers and the product says
  what has been given up.
* Two subscribers ship first — `healthchecks` and a generic `webhook` — because
  building one of each class immediately exercises the D7 split rather than leaving
  it theoretical until the second provider arrives.

## Alternatives considered

**One `notifications` table with a nullable `ledger_id`.** Fewer tables, one
publish path. Rejected on D1: it forces one RLS policy to answer two different
authorization questions, and the NULL case is the one that silently fails open or
closed. Every other objection (lifecycle, snapshot capture) follows from the same
root.

**Make `ledger_operations` the single event spine, with system events as a family.**
Attractive because the table already has `family`/`status`/`details`. Rejected:
`ledger_id` is `NOT NULL` and load-bearing for RLS, so deployment events do not
fit without exactly the nullable-scope hole above.

**In-app notifications only, no external monitor.** No new dependency and no
secret to manage. Rejected on D5: it cannot detect the failure that prompted the
ADR. It is the design that was already in place, and it produced 68 hours of
silence.

**A single hardcoded provider (healthchecks.io).** Less machinery, and it covers
the guarantee. Rejected: a self-hoster may already run Uptime Kuma, ntfy or nothing
at all, and hardcoding one SaaS into the only alerting path makes the product's
reliability story depend on someone else's business. The provider pattern costs an
interface and a registry, both of which already exist for quotes.

**Email as the first channel.** More familiar than a heartbeat service, and it
pushes. Deferred rather than rejected: it needs SMTP configuration and
credentials, and it still cannot detect absence — a dead container sends no mail.
It is a good second subscriber once the bus exists.
