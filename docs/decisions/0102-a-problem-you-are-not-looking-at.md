# 0102 — A problem you are not looking at

* Status: Accepted; amended 2026-10-31 (D1: a switched-off job's own subject); amended 2026-10-31 by [ADR-0109](0109-overview-is-an-attention-first-home.md) (the Overview explains outstanding problems); amended 2026-11-05 (D1: a person's check and a repair record the verdict, a repair clears the problem only when nothing still disagrees, a repair that has begun runs to the end and is recorded whatever happens to the client, a verdict older than the stored one is not written, and a repair's event follows the verdict that stands; a verdict a repair cleared is cleared as of its rewrite, and a verdict and its event commit together; a repair whose rewrite throws partway answers its error and clears the verdict as of when the rewrite stopped, without stamping `last_repaired_at`; one repair of a ledger runs at a time, and one sent while another runs is refused before it rewrites anything); amended 2026-11-05 by [ADR-0112](0112-one-write-lock-per-ledger.md) (a repair and the saves of its ledger take turns at the ledger write lock, piece by piece, the balances and holdings rewrites one account or position at a time, and the later reads what the earlier committed; a piece refused past the wait answers 422 `ledger-write-busy`, and the pieces before it stand)
* Date: 2026-09-30

## Context

Coffer notices things. The consistency monitor compares projections against
transactions, the scheduler reports jobs that failed or were auto-disabled, the backup
age monitor reports an artifact that has gone stale — and ADR-0096 built the bus that
carries all of it, with `ledger_events` as the per-ledger record and a derived
"is this still true?" read over it.

None of that was visible unless you were already looking at the right screen. The
consistency verdict renders on Settings → General, the event list on Settings →
Notifications, and both of those live inside a ledger you have to have chosen first. So
the question an operator actually has — **which of my ledgers needs me?** — had no
answer anywhere in the product, and the narrower **does this one need me?** cost three
clicks and knowing where to look.

External delivery does not close this. A dead-man's switch or a webhook tells you
out-of-band, and ADR-0096 D5 is right that this is the primary channel for a silent
stop. But an alert the product sends and then does not acknowledge in its own UI reads
as a false alarm — the same reason the consistency finding sits on the General tab.

The constraint that shapes the design: the navigation renders on **every** page, so
whatever feeds an indicator is on the critical path of every navigation.

## Decision

**D1 — Four indicators on one trail, sourced from outstanding NOTIFICATIONS.**

A dot appears on the app root (any ledger), the Manage ledgers row (that ledger), the
Settings nav item (that ledger), and the Notifications tab (that ledger) — so a problem
can be followed down rather than hunted.

The source is unresolved events at warning or above, not the consistency verdict. An
indicator that lit only for drift would lie by omission the moment a bank feed failed
or a backup went stale, and the reader's question is "does this need me", not "is this
one check failing". `ledger_consistency_state` (migration 242) keeps its own narrower
job: the verdict, the monitor's cadence gate, and repair history.

Resolution is ADR-0096's existing derived rule — a later `info` event with the same
subject is the all-clear — so nothing mutates the append-only log and every
scheduled-job warning stays self-resolving.

*Amended 2026-10-31: a job that switches itself off is announced under its own
subject.* The disable is `<job type>.disabled`, so the job's next
`<job type>.succeeded` clears it once the job is re-enabled and runs. Under a shared
`scheduler.job-disabled` key there was no all-clear for it, and two disabled jobs
counted as one problem. Migration 267 moved the existing rows to their job's subject.

*Amended 2026-10-31 (ADR-0109 D4): the Overview explains.* The ledger's Overview is a
new surface, not a fifth stop on the trail. Its header sentence carries one dot, at the
highest severity, beside the count, which is this endpoint's own number for the ledger;
its Problems widget lists each outstanding subject with its severity as a word, its
latest summary, when it began, and a link to the page that fixes it. The list comes
from `GET /api/ledgers/{id}/notifications/outstanding`, fetched only when the count is
above zero, which runs the same outstanding-subjects query as D2 for one ledger, so the
list and the count cannot disagree. The rail's Overview item carries no dot: one
problem lighting two navigation items is the puzzle D4 rules out. Nothing on the
Overview acknowledges or dismisses a problem.

*Amended 2026-11-02 ([ADR-0110](0110-upgrade-from-the-app.md)): the install has an indicator
too.* The dots above answer for ledgers, from `ledger_events`. Events about the installation
(`system_events`: a failed backup, a job switched off, an available release) had no indicator
anywhere. The System-settings gear now carries the same dot, at the highest severity beneath it,
from the deployment-scope outstanding subjects (`GET /api/admin/notifications/attention`, admin
only; the same rule, the same dot, the same kind of sentence). The trail continues to the System
tabs, and a subject lights one tab: **Upgrade** shows the `upgrade` subject and **Notifications**
shows every other subject, so one subject never lights two. An available release is a subject like
any other: a warning, an outline, not dismissible, cleared by a later info event. The wordmark keeps
its meaning, a problem in some ledger, and does not light for the install. The gear's request is
the one read that lets an available release be noticed from any page.

*Amended 2026-11-05: every check records the verdict, and a repair clears the problem only
when nothing still disagrees.* The Overview's Problems widget reads both halves of the
consistency state: the outstanding `consistency.` subject from `ledger_events`, and the
stored verdict in `ledger_consistency_state` for its "consistency checked …" text. Every
consistency event shares that one subject per ledger, so the two have to agree.

- **A check a person runs** (Settings → General → Maintenance, `POST …/balances/consistency`)
  records its verdict exactly as a monitor pass does: the same stored row, through the same
  path (`ConsistencyVerdicts`), and the monitor's own `consistency.ok` or `consistency.drift`
  event, signal included. It heals nothing. The monitor announces on every pass, its cadence
  being its only rate control; a person's check has no cadence, so it announces only what the
  ledger does not already say: a verdict other than the stored one (the other state, another
  set of failing projections, or none stored), or one the ledger's notice contradicts (an
  outstanding problem on a healthy ledger, or none on an unhealthy one). Pressing it again
  with nothing changed records the time and sends nothing. Anyone who reads the ledger may
  run it, as before: what it writes is derived from the check alone, through the service
  context.
- **The monitor's cadence counts announcements as well as checks.** A ledger is due when
  its last check or its last `consistency.ok`/`consistency.drift` is older than the
  cadence, so a person's check that announced nothing never holds back the ping a
  dead-man's switch waits for.
- **A repair** answers what it rewrote and every projection checked again once it has
  committed, records that verdict (with `last_repaired_at`) through the same path, and
  publishes one `consistency.repaired` event saying what it rewrote and what is still to
  fix. The event is info, the all-clear, only when that check finds nothing; otherwise it is
  a warning, so the subject stays outstanding with a summary naming what is left. Before
  this, the repair's info event cleared the subject while another projection still
  disagreed, and the stored verdict kept the last check's answer until the monitor's next
  pass. A repair is never suppressed: it changed the data, and each one is history.
- **A repair that has begun runs to the end and is recorded, whatever happens to the
  client.** Several rewrites commit in pieces (balances an account at a time, holdings and
  realized gains a position at a time, posting counts a header at a time), so a rewrite
  stopped partway leaves part of it committed, and the record is the only trace that a
  repair happened. So once the ledger is found, the rewrite, the check after it and the
  record run under no token of the request's, and a client that hangs up or a proxy that
  times out stops none of them. A check that fails still leaves
  the repair recorded: `last_repaired_at` is stamped, and the stored verdict, taken before
  the rewrite, is cleared as a restore clears it, so the ledger is due on the monitor's
  next tick. The event is a warning saying what was rewritten and that what still
  disagrees is not known, and the answer is what was rewritten with `after` null, not an
  error saying the repair failed.
- **A repair whose rewrite throws partway answers its error, and clears the verdict.** A
  rewrite can fail after some of its pieces have committed, for a reason other than its
  client (a database error, say). What it committed stands, so the stored verdict, taken
  before the rewrite, may no longer describe the data: it is cleared through the same path
  as for a repair whose check did not finish, as of when the rewrite stopped, and the
  ledger is due on the monitor's next tick. No repair completed, so `last_repaired_at` is
  left as it was and no event is recorded: an outstanding problem stays outstanding, and
  nothing says the ledger is healthy. The failure is logged, and the answer is still the
  error, since the repair did not happen in full.
- **One repair of a ledger runs at a time.** Since a repair that has begun runs to the end
  whatever happens to its client, a second one can arrive while it runs: from another tab,
  or pressed after an answer that never arrived. A repair sent while another repair of the
  ledger runs, of any projection, is refused with `consistency-repair-in-progress` before
  it rewrites anything, and the verdict is left as it was. Every piece of a rewrite takes
  the ledger's write lock before it reads, so a second one would only take turns with the
  first and then rewrite what the first had just rewritten; the rule is one for every
  projection rather than one per writer.
  The lock is held in the API process, the one process an install runs, until the repair
  has answered, its record included, however it ends.
- **A repair and the saves of its ledger take turns** (ADR-0112). The balances rewrite and
  the holdings rewrite (holdings, lots and realized gains) each delete a scope's stored rows
  and insert them again, and every save runs the same rewrite over what it changed. Every
  save that can recompute takes its ledger's write lock for its whole transaction, and every
  repair takes it too, one piece at a time: the balances and holdings rewrites one statement
  per account or position, through the recompute functions; the posting-count rewrite one
  transaction per header; the trade-price, holdings-currency and stored-notes rewrites one
  transaction for the whole rewrite. So a save waits for at most the one piece of a repair
  in flight, the repair's next piece waits for a save in flight, and the later one reads
  what the earlier committed. Two rewrites of the same rows at once would collide: the
  later insert would meet the rows the earlier committed, a unique violation that fails
  it, or the later would read past the earlier one's transaction and store a running
  balance, or posting counts, that leave it out. A piece that waits past the bound is
  refused as `ledger-write-busy`, a 422 whose detail says the repair stopped and that what
  it had already repaired stays repaired; the pieces before it stand, and the verdict is
  cleared as for a rewrite that throws partway.
- **A verdict older than the stored one is not written.** A check takes a while, and a
  repair, a person's check or a restore can record while it runs. A verdict's time is when
  its check began to read the ledger, and it is written, and announced, only when that is
  no earlier than the stored verdict's time. With no verdict stored, it must be no earlier
  than the time the clearing is as of: a restore's time; for a repair whose check did not
  finish, when its rewrite committed; or, for a rewrite that threw partway, when it
  stopped, after the last piece it committed; in each case the verdict cleared was taken
  before the rewrite. So a monitor pass that began before a repair committed cannot overwrite the
  repair's verdict, or announce drift on a ledger the repair made healthy, and a check or a
  repair that began after a repair's rewrite writes over the verdict that repair cleared,
  whenever it records. A repair is always recorded, but its verdict only under the same
  rule; one whose verdict is not written stamps `last_repaired_at` alone and moves nothing
  a later verdict is compared with. Its `consistency.repaired`
  event follows the verdict that stands once that is decided: when a check that began after
  the repair has recorded first, the event is info only when that check's verdict is healthy
  and says what that check found, and when a restore or a later repair has cleared the
  verdict since, it is a warning saying what still disagrees is not known. An event read
  off the repair's own, older check would clear the shared subject on a ledger stored as
  unhealthy, or reopen it on one stored as healthy. Every recorder saves the row
  against the version it read (Postgres's `xmin`). When another writer saved first, or
  created the row first, the recorder reads the row again and decides again, so two
  first checks at once do not fail. A person's check that decides again after losing that
  race announces only a verdict other than the stored one, since the writer that won held
  the same verdict and announces it. Two checks that find the same new verdict at once
  therefore announce it once. Each recorder saves the row and the event that follows it in
  one transaction, writing the row even when no field changes, so no writer saves between
  a verdict and its event: one that saves after it has read the row only once both had
  committed, and its own event is the later. Delivery to the ledger's targets follows the
  commit.

**D2 — One cross-ledger endpoint, bounded by SUBJECTS rather than by history.**

`GET /api/ledgers/attention` answers for all of the caller's ledgers at once. One fetch
per ledger would put N requests behind every page load.

It does not take the newest N events and resolve them in memory, which is what the
per-ledger panel does. Cross-ledger, any cap lets one noisy ledger crowd another out of
its own indicator — silently, and with a number that was read off today's data. Instead
the grouping happens in SQL: the latest warning-or-above event per ledger per subject,
and the latest `info` per ledger per subject. The result is at most one row per ledger
per subject per severity, whatever the log holds.

**D3 — The count is of distinct subjects, and a subject's severity is its LATEST
event.**

A ledger left drifting has one problem and a row for every pass — the consistency monitor
publishes on every pass, and deliberately does not throttle. A count of passes reported
as a count of problems is a worse answer than no number, and a number that climbs while nothing changes is how a reader
learns to stop reading it. Counting subjects also keeps the number stable while a
condition stands, which is what lets a reader notice it change.

Severity comes from the most recent event about a subject rather than the maximum over
all unresolved ones, for the same reason: taking the max would pin a ledger red forever
on the strength of one bad night, with no path down short of a full all-clear.

**D4 — Two severities, distinguished; one dot; never colour alone.**

The warning/critical split is load-bearing and predates this: drift is a warning
because it is slow damage with a repair button, while critical is reserved for the
irreversible — a missing backup is data that does not come back. An indicator that
cannot tell those apart makes the reader click through to learn whether to care, which
is most of what it was for.

One dot per surface, at the highest severity beneath it. Two dots side by side is a
puzzle, not information.

**Warning is an outline, critical is filled**, and they use `--color-mark-*` rather
than `--color-state-*`. The obvious choice — two solid dots in the existing state
colours — gives, on screen, the same dot twice. `state-warning`
(`#92400e`) and `state-danger` (`#b91c1c`) are tuned for WORDS on a surface, where the
word carries the meaning and the hue only tints it; as a small filled shape they are
two dark warm tones. Both clear their contrast floor against the background, which is
the point: a floor says a mark is visible, not that two marks are telling each other
apart. The `mark-*` tokens exist for a shape rather than a word, and outline-vs-fill is
a weight difference, which survives at 10px and does not depend on separating hues at
all.

On top of that, the same sentence — naming the ledger, the severity and the count —
rides along as a `title` for anyone who hovers and as screen-reader-only text for
anyone who cannot see the indicator at all.

**D5 — No count on the aggregate, and no third state for "never checked".**

A number on the app root would be a sum across ledgers, which answers a question nobody
asks. A dot there means "something, somewhere"; counts belong where they are actionable
and attached to a ledger the reader can open.

"Never checked" is a real state that migration 242 makes visible for the first time,
and it is deliberately not a third dot colour: three things to decode in navigation
chrome, and a brand-new ledger would light up as a problem on day one. Where it is
shown at all it belongs as text on the Manage ledgers row.

## Consequences

A fifth surface comes free: the style guide renders both severities and the
nothing-outstanding case side by side, which is where the fill-vs-outline distinction is
checked, now and on any change to it.

The trail is the point: a dot on the app root leads to a marked ledger, which leads to
its Settings, which leads to the Notifications tab. Each step narrows, and the last one
lands on the list that explains it.

**Unrepaired drift is a standing condition, so an indicator can stay lit indefinitely.**
That is correct — the problem really is still there — and it is also exactly how an
indicator becomes wallpaper, which is the cry-wolf failure ADR-0096 D4 names and the
one the notification cadence is designed to avoid. D3 is the mitigation that exists
today: the count stays still while nothing changes, so a change in it means something.
Nothing here escalates, ages out, or lets a reader acknowledge a condition they have
decided to live with, and if the indicator stops being read those are the levers to
reach for.

Two spellings of "the same subject" now exist — one in C# for the per-ledger panel, one
as a LINQ expression the database evaluates for the summary. Neither can use the
other's: EF translates an expression tree rather than a method body. The subject keeps
its trailing dot (`consistency.`, not `consistency`) because that is the only
formulation which does not become `substring(key, 1, -1)` on a key with no dot, which
Postgres refuses outright. A test runs both over the same keys against a real Postgres
and fails if they ever disagree; a comment asking the next reader to keep them in step
would not. *Since 2026-10-31 (ADR-0109)* the database's spelling is one projection,
`EventSubjects.WithSubject`, which the summary and the outstanding list both compose
on; the per-ledger panel still resolves in memory with the C# spelling.

The endpoint reads through the app-role context, so `ledger_events_read` (migration
208) is the backstop and the grant join the primary check. This is the one route in its
file with no `ledgerId` in the path, which makes it the one where a service-role context
copied from a neighbouring repository would serve every install's problems to every
caller with no error to notice.

## Alternatives considered

**Source the indicator from `ledger_consistency_state`.** The verdict is already per
ledger, already one row, and already gated to twice a day. Rejected on D1: it answers
"is this one check failing", and a badge that stays dark through a failed bank feed and
a stale backup is worse than no badge, because it is read as an all-clear.

**One fetch per ledger, reusing the existing events endpoint.** No new route, no new
SQL, and the resolution logic would exist exactly once. Rejected on D2: the navigation
renders on every page, so this is N requests behind every navigation, and it gets worse
with the number of ledgers — which is precisely the situation the feature is for.

**Count rows rather than subjects.** Simpler, and it is what the panel shows. Rejected
on D3: with an unthrottled twice-daily publish it reports hundreds for one standing
problem, and it changes constantly while nothing changes at all.

**A third dot state for "never checked".** It is a real distinction and migration 242
finally makes it available. Rejected on D5: a brand-new ledger would light up as a
problem on its first day, and the reader now has three colours to decode in the chrome
of every page.

**Keep both severities as solid dots and rely on the hue.** The smallest possible
indicator, and it reuses the state tokens the rest of the app already speaks. Rejected:
side by side on the style guide the two are indistinguishable, and only the rendered
screen shows it, because a table of contrast ratios answers "is this visible" and the
question here is "are these two different".

**Add a `--color-mark-warning` only, and leave critical on `state-danger`.** One new
token instead of two, and critical's red works as a fill already. Rejected: a lone mark
token beside a state one invites the next reader to wonder which applies where. The
pair makes the category — marks are shapes, states are words — legible without a
comment.
