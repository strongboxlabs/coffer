# 0044 — Version surfacing across three layers (DB / API / UI)

* Status: Accepted
* Date: 2026-06-09
* Related: ADR-0037 (snapshots already stamp a schema version), the
  layer-independence principle, the `__schema_migrations` DbUp journal

## Context

There was no way to see what's deployed. Each layer ships on its own
cadence — DbUp migrations, the .NET API process, the SPA bundle — and
when they drift (most often: the SPA hot-reloads after a merge but the
API wasn't restarted) there was no signal. We want to *see* the running
versions, and tell at a glance whether one build is newer than another.

## Decision

Surface **three independent version axes — DB, API, UI** — matching the
layer-independence principle (they version independently because they
deploy independently). Each is shown in an app-level **About** dialog,
opened from the sidebar footer.

Each axis carries a **progression** the user can eyeball, not just an
opaque identity:

| Axis | Progression | Identity | Source |
|------|-------------|----------|--------|
| **DB** | migration number (`118`) | script name | latest row in `__schema_migrations` (LINQ over the mapped `SchemaMigrationRow`; same journal ADR-0037 stamps onto snapshots) |
| **API** | build number (`224`) | semver + short SHA + commit date | assembly attributes stamped by an MSBuild target |
| **UI** | build number | semver + short SHA + commit date | Vite `define` constants baked into the bundle |

**Build number = git commit count** (`git rev-list --count HEAD`): a
strictly-increasing integer that needs zero manual bookkeeping and rises
by one per commit on the deploy branch. The **short SHA** pins the exact
build; **semver** is the human-friendly handle. We use the **commit
date**, not the build date — it's deterministic (tied to the SHA) and
more meaningful.

**Semver bump policy (from 0.2.0):** the semver baseline is bumped **for
every build we publish a container image for** — not just occasional
"releases." The single source of truth is `<Version>` in
[`src/Api/Api.csproj`](../../src/Api/Api.csproj) and `version` in
[`src/Web/package.json`](../../src/Web/package.json) (+ its
`package-lock.json`); they must always match each other and the pushed
image tag (`ghcr.io/<owner>/coffer:<semver>`). MAJOR.MINOR.PATCH by impact
(feature → MINOR, fix → PATCH while pre-1.0). This keeps the container
tag, the About-panel version, and the git tag (`v<semver>`) in lockstep,
so "what's deployed" is never ambiguous. See the maintainer release process.
Rationale: a container tag that doesn't match the build version inside it
is a lie — the image says `0.2.0` while About says `0.1.0`. Semver must
therefore move on every build.

**Superseded caveat (fixed 2026-08-17).** This decision originally accepted
that container images build `.git`-less — `.dockerignore` excludes it, and
rightly so — leaving the build-number and SHA axes at `0` / `nogit` in every
shipped image, with semver as the only axis carrying meaning. That held for
as long as the axes were only feeding an About panel. It stopped being
acceptable once reporting responses began carrying `engineVersion`
(ADR-0063) to let a consumer tell a stale figure from a current one: an
identity that reads `0.62.0+nogit` names the release but not the commit, so
every image of a release is indistinguishable — which is the one job that
field has. The values are now PASSED IN rather than discovered: the
stamping target skips a git call when the corresponding property is already
set, the Dockerfile takes `SOURCE_COMMIT_SHA` / `_COUNT` / `_DATE` as build
args, and the release workflow supplies them from the tagged commit (with
`fetch-depth: 0`, since a shallow clone reports a commit count of 1). A
local `docker build` that passes nothing still degrades exactly as before.

**Transport.** `GET /api/meta/version` returns the two server-side axes
(API + DB). It is **authenticated** — unlike the anonymous `/healthz` /
`/readyz` probes, the version payload is for a logged-in user's About
panel, so there's no reason to disclose build/schema state to anonymous
callers. The SPA supplies its own (UI) axis from the build-time
constants; it needs no fetch for that row.

**Build-time stamping** is guarded on both sides: every git call has a
fallback (build `0` / commit `nogit` / `dev`), so a `.git`-less build that
supplies nothing degrades gracefully instead of failing the build. Each
value is also OVERRIDABLE — set `GitShortSha`, `GitCommitCount` or
`GitCommitDate` and that git call is skipped — which is how a container
build gets a real identity without `.git` in its context. The fallbacks
deliberately test the VALUE, not the git exit code: a skipped `Exec` leaves
its exit code empty, and testing `!= '0'` would overwrite a supplied SHA
with `nogit`.

## Consequences

### Positive
- Clear "what's running" across all three layers, with an eyeball-able
  progression (build 412 > build 408).
- **Free skew check:** UI and API build from the same monorepo HEAD, so
  matching build numbers confirm they were built from the same commit; a
  mismatch is the "restart the API after the merge" signal.
- Zero new schema, zero write-path: the DB axis reuses the existing
  DbUp journal; API/UI axes are build artifacts.

### Negative / trade-offs
- The build runs three `git` commands. Negligible cost; guarded so it
  never breaks a `.git`-less build.
- Semver is bumped by hand — but the SHA + build number always reflect
  reality regardless, so a stale semver is cosmetic, not misleading.

## Alternatives considered
- **SHA only.** Rejected: a SHA says *which* build, not *whether it's
  newer*. The commit-count build number restores the progression.
- **A compact always-visible footer string.** Rejected in favour of a
  discoverable dialog — less chrome on every screen, room to grow.
- **Nesting About under the per-ledger Settings page.** Rejected:
  versions are installation-wide (one DB, one API process), so hosting
  them under a single ledger reads wrong. The sidebar footer is
  app-level and reachable everywhere.
- **An automatic UI↔API skew *warning*.** Deferred: the matching build
  numbers already expose drift; an active warning can come later if the
  manual read proves insufficient.
