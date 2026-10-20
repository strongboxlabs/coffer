# 0102 — A problem you are not looking at

* Status: Accepted
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
out-of-band, and ADR-0096 D5 is right that this is the primary channel for the failure
that prompted it. But an alert the product sends and then does not acknowledge in its
own UI reads as a false alarm — the same observation that had already pulled the
consistency finding onto the General tab.

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

A ledger left drifting has one problem and hundreds of rows — the consistency monitor
publishes on every pass, and deliberately does not throttle. "312 problems" is a worse
answer than no number, and a number that climbs while nothing changes is how a reader
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
than `--color-state-*`. The first build did the obvious thing — two solid dots in the
existing state colours — and on screen they were the same dot. `state-warning`
(`#92400e`) and `state-danger` (`#b91c1c`) are tuned for WORDS on a surface, where the
word carries the meaning and the hue only tints it; as a small filled shape they are
two dark warm tones. Both cleared their contrast floor against the background, which is
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

A fifth surface came free: the style guide renders both severities and the
nothing-outstanding case side by side, which is where the fill-vs-outline decision was
actually made and where the next change to it will be checked.

The trail is the point: a dot on the app root leads to a marked ledger, which leads to
its Settings, which leads to the Notifications tab. Each step narrows, and the last one
lands on the list that explains it.

**Unrepaired drift is a standing condition, so an indicator can stay lit for weeks.**
That is correct — the problem really is still there — and it is also exactly how an
indicator becomes wallpaper, which is the cry-wolf failure ADR-0096 D4 names and the
one the notification cadence was reworked to avoid. D3 is the mitigation that exists
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
would not.

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
indicator, and it reuses the state tokens the rest of the app already speaks. This is
what was built first, and it is why D4 says what it says: side by side on the style
guide the two were indistinguishable. Rejected on the evidence rather than on
principle — and the evidence had to be the screen, because a table of contrast ratios
answers "is this visible" and the question here was "are these two different".

**Add a `--color-mark-warning` only, and leave critical on `state-danger`.** One new
token instead of two, and critical's red works as a fill already. Rejected: a lone mark
token beside a state one invites the next reader to wonder which applies where. The
pair makes the category — marks are shapes, states are words — legible without a
comment.
