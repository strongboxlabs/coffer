# 0092 — The master KEK lives in a file, and its lifecycle is UI-driven

* Status: Accepted — D6's one-release `COFFER_MASTER_KEK_BASE64` window closed and the
  variable is gone; see [ADR-0094](0094-restore-is-ui-only-and-the-kek-has-no-env-channel.md), which also
  carries out D4's stated replacement of the `install.sh` prompt.
* Date: 2026-08-06
* Amends: [ADR-0014](0014-encryption-at-rest.md) §Layer 4 and
  [ADR-0026](0026-per-ledger-encryption-key.md) §Master KEK source + §Rotation
* Extends: [ADR-0061](0061-bootstrap-restore.md) (bootstrap-UI ceremony staging
  on the `coffer_data` volume)

## Context

The KEK reaches the API through one channel: `COFFER_MASTER_KEK_BASE64`, read
eagerly in [`Program.cs`](../../src/Api/Program.cs) before the host is built.
`install.sh` generates it into `.env` and prints a one-line warning. Three
problems.

**An env var is the leakiest channel available** — visible in `docker inspect`,
`/proc/<pid>/environ`, child environments, and crash dumps. ADR-0014 §Layer 4
already graded it "vulnerable to env dump," accepted as a Phase 5 starting point
on the understanding that graduating is a deployment change, not a schema change.
This is that graduation.

**Rotation's key swap is not atomic.** `RotateAsync` re-wraps everything in one
transaction — sound. But the CLI then tells the operator to edit `.env` and
restart. In between, the live container holds the **old** key while the database
is wrapped under the **new** one; a ledger created in that window gets the old
key, leaving mixed `lek_kek_id` values.

**The operator must safeguard a secret they were never shown.** It exists only in
a file the installer wrote, never surfaced in the app.

### What losing the KEK actually costs

Stated precisely, because it right-sizes everything below. Envelope encryption is
for high-value secrets only (ADR-0026 §Scope); bulk transaction data is
plaintext. The complete wrapped set, per `KekRotationService`:

| Wrapped material | Cost if lost |
|---|---|
| `ledgers.wrapped_lek` → `feed_connections.access_url_ciphertext` | Re-link SimpleFIN feeds |
| `global_scheduled_jobs.passphrase_ciphertext` | Re-set the backup passphrase |
| `drive_sync.oauth_ciphertext` | Reconnect Google Drive |

A `.cofferbak` is sealed under the operator's **backup passphrase**, not the KEK;
the KEK contributes only a fingerprint, and a mismatch is warn-and-acknowledge,
not a block ([`AdminBackupsEndpoints.cs`](../../src/Api/Endpoints/AdminBackupsEndpoints.cs)
pre-flight 1). KEK loss costs three re-establishable secrets — not data, not
passkeys, not backups. The backup passphrase, not the KEK, is the secret whose
loss is unrecoverable.

## Decision

### D1 — Read the KEK from a file; retire the env var

Resolution order, and **the file wins**: (1) the key file at `Api:MasterKey:Path`,
default `data/master.key` on the existing `coffer_data` volume, validated by the same
`MasterKeyLoader.LoadFromValueOrThrow` contract; (2) file empty and
`COFFER_MASTER_KEK_BASE64` set → migrate it into the file (D6); (3) neither, and no
wrapped material → generate in the first-run ceremony (D2); (4) neither, but wrapped
material present → refuse to serve (D3).

**File-first is a correctness requirement, not a preference.** This originally read
env-first, on the reasoning that an upgrading operator has their key in `.env` and a
blank volume. That is fine right up until something else can write the file — and D4
introduced two such things. With env-first, a UI rotation re-wrapped the database
under a new key, wrote that key to the file, and the next boot silently overwrote it
with the stale env value, leaving the process holding the **old** key over **new**
wraps. Restore-adopt failed the same way and worse: the boot after adoption clobbered
the adopted source key, and D5 reconciliation then cleared the very secrets the
operator supplied that key to preserve. Both silent, both on the transition path D6
promises will work.

When the env var is set to something the file doesn't match, it is ignored and the
startup log says so loudly — an operator who set that variable believes it is doing
something, and after a rotation it is simply stale.

A *configurable* path is what makes this better than the env var rather than
merely different: `/run/secrets/coffer_kek`, a projected k8s Secret, and a Key
Vault CSI mount are all file-shaped. One setting covers every injection story the
env var covered, on a channel that stays out of process listings. Written `0600`,
never logged, never in a trace attribute, never in a `GET` body.

### D2 — First run generates and shows the KEK; it stays viewable

**Startup** mints the key on a virgin install (D3) and persists it; the setup
ceremony **reads it back** and displays it behind an acknowledgement, after the
recovery codes. Two secrets, shown one at a time and in order of severity — the
codes are one-time and are the only way back in without the authenticator, so they
must not compete for attention with a key that can be viewed again.

The key rides back in the setup-completion response, for the same reason and under
the same gate as the recovery codes: the bootstrap token only exists before the
first user, so that response is a one-time first-run payload delivered the moment
the operator is present, verified, and paying attention. It is also the *less*
sensitive of the two — recovery codes bypass passkey auth entirely. Requiring a
second ceremony for the weaker secret would be incoherent, and would risk the
operator never seeing it at all if that ceremony failed. The disclosure is audited
under its own action so it stays distinguishable from a deliberate later reveal.

Minting at boot rather than inside the ceremony is deliberate. The alternative —
an initially-empty, mutable `MasterKey` the ceremony fills in — turns a non-null
immutable singleton into a holder that six consumers must null-check
(`LedgerKeyService`, `BackupService`, `BackupManager`, `DriveSyncService`,
`GoogleDriveBackupDestination`, `KekRotationService`) and defers key failures from
startup to first use, forfeiting the fail-fast property this ADR otherwise keeps.
Operator-visible behaviour is identical — they first see the key during setup
either way.

It gets **its own System → Encryption tab**, not a card under Backups. First cut
filed it beside Restore, on the reasoning that the two interact — a cross-install
restore needs the source key, and the fingerprint is what tells you whether it
matches. Dev testing made the mistake obvious: the key wraps bank-feed tokens, the
backup passphrase *and* the Drive connection, so filing it under one of the three
turns "where is my master key?" into a hunt. Restore keeps a link to the tab, which
is the relationship that actually existed.

**Not show-once.** An admin can re-view it from system settings behind a fresh
passkey assertion, audited. Recovery codes are show-once because re-display is an
authentication attack surface; the KEK is an encryption key, and an admin who can
already read every ledger in plaintext gains nothing from seeing it. Show-once
would add a failure mode — a browser dying after persistence but before the human
writes it down — for approximately no security. UI framing is *migration key*:
what carries sealed secrets to another install, not what stands between the
operator and their data.

### D3 — A key-less boot over wrapped material is an operator error

No KEK plus wrapped material present → refuse to serve. This keeps the invariant
`MasterKeyLoader` was written to protect (booting unconfigured means every new
wrap is under a key the operator lacks) while narrowing it to the dangerous case;
a virgin install mints one and carries on to D2.

"Wrapped material" is the same three columns rotation covers, probed on a
service-role connection before migrations and before the host is built. The probe
**fails closed** — an unreachable database is not evidence of a virgin install,
and the two error directions cost wildly different amounts: a false "virgin"
mints a key over live wrapped material and orphans it, while a false "not virgin"
only refuses to boot.

It must also survive a pre-migration database, where none of the three tables
exist. The usual escape hatch for a schema question — a Postgres function bound
via `HasDbFunction` — is unavailable by definition here, since a function created
by a migration cannot exist before migrations. So each check is an ordinary LINQ
query with a `42P01 undefined_table` catch, which keeps the data-access layer 100%
EF (`feedback_no_raw_sql_in_api`) at the cost of treating an expected exception as
a signal. Chosen over a raw-SQL exception deliberately: one narrow use of
exception-as-signal is cheaper than a second permanent hole in a project-wide rule.

Recovery is an audited restart flag, not a subsystem: place the saved key at the
configured path, or pass `--adopt-new-kek` to mint a fresh one and orphan the
wrapped set, logging which of the three items above are abandoned. Silently
self-generating over existing wrapped material is never allowed — a mis-mounted
volume must fail loudly, not quietly re-key.

### D4 — Restore and rotation move into the UI

Restore already lives there (ADR-0061); it gains a source-KEK field, replacing the
`install.sh` prompt and today's "set `COFFER_MASTER_KEK_BASE64` and re-upload"
advice. Supplying it means **adopt**: the source key is written to the resolved
path, so nothing is orphaned. Safe because `clean: true` already wiped any local
wrapped material. The previous key file is archived, not overwritten, so a mistaken
restore is reversible.

The key is validated at **upload**, against the archive's KEK fingerprint — the
same fingerprint the mismatch pre-flight already reads. A wrong paste is then
caught before anything destructive happens, rather than after the restore has
replaced everything and the install can no longer open its own secrets. A v1
archive carries no fingerprint, so it proceeds unverified; if the key turns out
wrong, D5 clears what won't open, which is the same outcome as supplying nothing.

Adoption needs the key file to be writable, and **refuses without taking the install
down** when it isn't — the documented read-only injection case. A throw there would
land before the staged key is shredded, so the next boot would retry forever: a
permanent crash loop rather than one failed operation. Instead the staging is cleared,
the restore proceeds under the existing key, and D5 reports what it had to abandon.

Adoption happens **before the restore is applied, and costs an extra restart.**
`MasterKey` is resolved during DI registration, so the boot that receives a staged
restore holds the *local* key — applying the restore there would leave D5
reconciling under the wrong key and clearing the very secrets the operator supplied
a key to keep. So that boot adopts the key, exits, and the next boot — whose
`MasterKey` is the adopted one — applies the still-pending restore and reconciles
against it. It exits rather than serving briefly with a key it has already
superseded.

Rotation becomes a UI action that generates, re-wraps, and swaps the file in one
operation, closing the window above; `RotateAsync` and its dry-run are retained
behind the endpoint.

**The `rotate-kek` CLI is removed rather than ported.** Rotation is routine hygiene,
not disaster recovery: an operator who can't sign in to reach the UI needs recovery
codes, not a re-key. It was also env-only, so on a file-based install — the default
since D1 — it exited 2 without doing anything, which is some evidence nobody was
reaching for it. `COFFER_MASTER_KEK_NEW_BASE64` and `_NEW_ID` go with it; rotation
generates the key server-side, so there is nothing to pre-stage in the environment.
`restore` stays a CLI command, because that one genuinely cannot be a UI action — it
skips migrations, so it works on a schema too broken or too old for the app to serve.

Two things fall out of implementing it. **The KEK id has to live in the key
file**, not the environment — rotation mints key and id together, and an id
sourced elsewhere would leave a rotation stamping `lek_kek_id = v2` on every row
while the next boot went on calling itself v1. The file therefore takes an
optional `id=` line; a bare single-line file (what a hand-written file or a
projected secret looks like) stays valid and falls back to the configured
default. **And the swap ends in a restart**, because `MasterKey` is deliberately
an immutable singleton (D2) — the same restart mechanism the bootstrap restore
uses. The remaining window is bounded by that restart rather than by however long
the operator takes to edit `.env`.

Order is chosen for crash-safety: archive the old file, write the new one, *then*
re-wrap. A crash between the write and the commit leaves the file ahead of the
database, which is recoverable because the old key sits in the archive; the
reverse order would leave the database ahead of the file with the new key existing
nowhere. A failed re-wrap rolls the file back explicitly.

Rotation refuses cleanly when the key file isn't writable — the documented
read-only injection case (`/run/secrets/…`, a projected Kubernetes Secret). That
check fires before the database is touched, so it is purely a refusal.

### D5 — A restore leaves no ciphertext the install cannot open

`RestoreAsync(clean: true)` is a wholesale `pg_restore` with no crypto
reconciliation, so a cross-KEK restore leaves the source install's wrapped
material under the local KEK. Nothing detects this: `lek_kek_id` is written
(`KekRotationService`, `LedgersRepository`) but never read. Failures are lazy and
land in background jobs long after the operator acknowledged a successful
restore — and one of them is unguarded
([`GoogleDriveBackupDestination.ResolveAsync`](../../src/Api/Backup/Drive/GoogleDriveBackupDestination.cs)
unwraps with no `CryptographicException` catch, unlike the ingest and backup
paths).

So restore ends with a reconciliation pass, in one transaction:

1. Trial-open each `ledgers.wrapped_lek`. On failure, mint a fresh LEK, stamp the
   local `lek_kek_id`, and null every secret sealed under the dead LEK
   (`feed_connections.access_url_ciphertext`), setting those connections to the
   `needs_reauth` status the schema already allows. Minting rather than nulling
   keeps the ledger functional: an unopenable `wrapped_lek` would fail every
   future seal/open for that ledger, so it would look healthy until someone
   connected a feed.
2. Null `global_scheduled_jobs.passphrase_ciphertext` where it won't open **and
   disable the scheduled backup** until a new passphrase is set — otherwise that
   job fails on every tick, forever.
3. Null `drive_sync.oauth_ciphertext` where it won't open and mark Drive
   disconnected.

**Detection is trial-decrypt, not the fingerprint.** `ReadKekFingerprintAsync`
returns empty for v1 artifacts and the pre-flight lets them through, so
reconciliation runs after *every* restore, not only an acknowledged mismatch. The
fingerprint stays a pre-flight courtesy; trial-decrypt is the authority.

**It runs after migrations, not inside the restore block.** A restored dump can
predate the running build, and reconciliation queries it through the current EF model,
so against the un-migrated schema it raises `42703 undefined_column` — not a
`BackupException`, so it escaped the restore's catch filter and killed the boot *after*
the staging had been cleared, leaving the restore applied and reconciliation skipped
for good. A failure now logs at Error and lets the server start: what's left behind is
the state that existed before D5 — bad, but serving — whereas a crash loop is neither.

The reconciliation is automatic and audited, not a second prompt: the operator
already decided at the mismatch acknowledgement, and this only honors it. The
resulting debt surfaces as state (null ciphertext + flag) in the feeds, backups,
and Drive panels — not a one-shot message. Today's acknowledgement copy is also
corrected: it omits feed re-linking, which is the *first* casualty.

### D5b — The backup passphrase is revealable too

The table above names the backup passphrase as the genuinely unrecoverable secret.
That framing was half wrong, and the half that was wrong mattered: the server
unseals it on **every scheduled backup**, so it was always recoverable in
principle — the product just offered no way. An operator who forgot it kept taking
backups that all succeeded and were all unrestorable, with nothing anywhere saying
so. `SetBackupPassphraseDialog` even asserted it "cannot be recovered", which is a
warning an operator can catch out, and those are the ones they stop believing.

So it gets the same treatment as the KEK: revealable to an admin behind a fresh
assertion, audited under its own action, POST-only with `no-store`. The safety
argument is identical — the only caller who can reach it already reads every ledger
in plaintext and could mint a fresh backup under a passphrase of their choosing, so
disclosure grants nothing new. The set-time copy now draws the line where it
actually falls: losing the *server*, not forgetting the passphrase.

The step-up itself moves into a shared `FreshAssertionGate`, deliberately, because
it is a security check — two copies drift, and the weaker copy on any one surface
becomes the way to reach a secret with only a cookie. Each ceremony keeps its own
challenge flow (migration 192), so a challenge is good for exactly the ceremony it
was minted for. Cross-redemption between two admin step-ups would gain an attacker
nothing, but a flat invariant is cheaper to reason about than re-arguing the
exception every time a surface is added.

### D6 — Transition, not a flag day

For one release `COFFER_MASTER_KEK_BASE64`, if set **and the key file is empty**, is
written to the resolved path on first boot and logs a deprecation. Removed a release
later. Existing deployments upgrade with no `.env` edit and no downtime; from the
second boot on, the file is authoritative (D1) and a leftover value in `.env` is
ignored with a warning rather than being allowed to undo a rotation.

## Consequences

**`.env` no longer holds key material**, which is what makes a Windows/macOS
installer tractable — no `chmod 600` equivalent to get right for a file that
holds nothing sensitive.

**Three new sensitive paths in application code** — D1's file write, D4's rotation
swap, and D2's re-view (the first time the KEK crosses into a response body).
Writes need atomic write-and-rename plus `0600`; the read needs a fresh assertion,
`no-store`, and an audit row. All need coverage asserting the key reaches neither
logs nor traces.

**A new audit surface, `admin_audit_events` (migration 191).** Neither existing one
fits: `ledger_operations` is per-ledger and RLS-scoped, `mcp_tool_invocations` is
the MCP write audit. This one is for actions belonging to the *deployment* — key
reveal, rotation, adoption — and is deliberately **not** pruned by
`AuditRetentionService`, because those rows are rare and their value is their age.
`action` carries no CHECK constraint: the WebAuthn flow CHECK has needed widening
three times (140, 176, 190) purely to admit a string, and an audit vocabulary grows
by nature, so it lives in `AdminAuditActions` instead. The write gates the reveal
response — an unaudited reveal is worse than a failed one — but is best-effort on
the boot-time adopt path, where it runs before migrations and letting it throw
would strand the install in a boot loop.

**`lek_kek_id` becomes load-bearing.** D5 gives it its first reader — advisory
rather than authoritative, since trial-decrypt decides, but it stops being a
column nothing consults. The Drive unwrap also gains a guard it should have had
regardless, matching the ingest and backup paths.

**Tests lose their env seam.**
[`ApiFactory.cs`](../../tests/Api.Tests/Integration/Infra/ApiFactory.cs) moves to
writing a fixture key at the configured path; `MasterKeyLoaderTests` retargets
from the env contract to the file contract.

**The backup passphrase is the more critical secret, and D5b gives it the same
treatment** — revealable behind the same step-up, audited under its own action.
Which also means neither of this ADR's two secrets is now unrecoverable while the
install is alive; both are unrecoverable if the server is gone, and both say so.

## Alternatives considered

**Keep the env var as highest precedence.** Rejected once D4 moved restore and
rotation to the UI: the remaining consumers were the test harness (one line) and
ops injection, which a configurable file path serves strictly better.

**A degraded auth-only boot for key-less recovery.** Dropped. It was justified by
a mis-reading of the blast radius — that a lost KEK stranded the operator's data.
Since the real cost is three re-establishable secrets, D3's acknowledged flag
covers it, and a request-serving boot path without a KEK would be new attack
surface bought for nothing.

**Store the KEK in the database.** Rejected: it would sit in the same artifact as
the material it wraps, so one dump carries both halves. The separation between
`coffer_data` and `postgres_data` is the point.
