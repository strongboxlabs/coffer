# 0092 — The master KEK lives in a file, and its lifecycle is UI-driven

* Status: Accepted — D6's one-release `COFFER_MASTER_KEK_BASE64` window closed and the
  variable is gone; see [ADR-0094](0094-restore-is-ui-only-and-the-kek-has-no-env-channel.md), which also
  carries out D4's stated replacement of the `install.sh` prompt. D2's placement of the
  key inside the setup ceremony is superseded by [ADR-0095](0095-master-key-moves-to-a-welcome-screen.md):
  setup gates on the recovery codes alone and a welcome screen carries the key.
* Amended by: [ADR-0105](0105-a-cookie-alone-is-not-enough.md) (`FreshAssertionGate` requires user verification and a passkey older than the session, and guards nine more actions); [ADR-0110](0110-upgrade-from-the-app.md) (a private fork's upgrade token is wrapped material)
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

**File-first is a correctness requirement, not a preference.** Env-first looks right
for an upgrading operator, whose key is in `.env` and whose volume is blank, until
something else can write the file — and D4 introduces two such things. With env-first,
a UI rotation re-wraps the database under a new key, writes that key to the file, and
the next boot silently overwrites it with the stale env value, leaving the process
holding the **old** key over **new** wraps. Restore-adopt fails the same way and worse:
the boot after adoption clobbers the adopted source key, and D5 reconciliation then
clears the very secrets the operator supplied that key to preserve. Both silent, both
on the transition path D6 promises will work.

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

It gets **its own System → Encryption tab**, not a card under Backups or beside
Restore, although the two interact — a cross-install restore needs the source key,
and the fingerprint is what tells you whether it matches. The key wraps bank-feed
tokens, the backup passphrase *and* the Drive connection, so filing it under one of
the three turns "where is my master key?" into a hunt. Restore keeps a link to the
tab, which is the relationship that actually exists.

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

"Wrapped material" is the same columns rotation covers (see the 2026-10-27
amendment for the current list), probed on a service-role connection before
migrations and before the host is built. The probe
**fails closed** — an unreachable database is not evidence of a virgin install,
and the two error directions cost wildly different amounts: a false "virgin"
mints a key over live wrapped material and orphans it, while a false "not virgin"
only refuses to boot.

It must also survive a pre-migration database, where none of the probed tables
exist. The usual escape hatch for a schema question — a Postgres function bound
via `HasDbFunction` — is unavailable by definition here, since a function created
by a migration cannot exist before migrations. So each check is an ordinary LINQ
query with a `42P01 undefined_table` catch, which keeps the data-access layer 100%
EF (the no-raw-SQL rule for `src/Api`) at the cost of treating an expected exception as
a signal. Chosen over a raw-SQL exception deliberately: one narrow use of
exception-as-signal is cheaper than a second permanent hole in a project-wide rule.

Recovery is an audited, one-shot start flag, not a subsystem: place the saved key at the
configured path, or start once with `--adopt-new-kek` to mint a fresh one and abandon the
wrapped set, which D5's reconciliation then clears under the new key, listing what was
abandoned and recording it in `admin_audit_events` (2026-10-27 amendment). Abandoning is
irreversible: once cleared, the old key turning up later recovers nothing. Silently
self-generating over existing wrapped material is never allowed — a mis-mounted
volume must fail loudly, not quietly re-key.

### D4 — Restore and rotation move into the UI

Restore already lives there (ADR-0061); it gains a source-KEK field, replacing the
`install.sh` prompt and today's "set `COFFER_MASTER_KEK_BASE64` and re-upload"
advice. Supplying it means **adopt**: the source key is written to the resolved
path, so nothing is orphaned. Safe because the restore replaces the whole database
(since 2026-10-09 by building a new one and swapping it in — ADR-0061's amendment),
so no local wrapped material survives it. The previous key file is archived, not overwritten, so a mistaken
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
since D1 — it exits 2 without doing anything. `COFFER_MASTER_KEK_NEW_BASE64` and `_NEW_ID` go with it; rotation
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

`RestoreAsync` is a wholesale `pg_restore` with no crypto
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
`BackupException`, so it would escape the restore's catch filter and kill the boot
*after* the staging had been cleared, leaving the restore applied and reconciliation
skipped for good. A failure now logs at Error and lets the server start: what's left behind is
the state that existed before D5 — bad, but serving — whereas a crash loop is neither.

The reconciliation is automatic and audited, not a second prompt: the operator
already decided at the mismatch acknowledgement, and this only honors it. The
resulting debt surfaces as state (null ciphertext + flag) in the feeds, backups,
and Drive panels — not a one-shot message. The acknowledgement copy names feed
re-linking, which is the *first* casualty.

### D5b — The backup passphrase is revealable too

The table above names the backup passphrase as the genuinely unrecoverable secret,
but the server unseals it on **every scheduled backup**, so it is recoverable while
the server lives. Without a way to
reveal it, an operator who forgets it keeps taking backups that all succeed and that
they cannot restore, with nothing anywhere saying so. A dialog asserting it "cannot
be recovered" is a warning an operator can catch out, and those are the ones they
stop believing.

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

**A degraded auth-only boot for key-less recovery.** Dropped. It rests on a
mis-reading of the blast radius — that a lost KEK strands the operator's data.
Since the real cost is three re-establishable secrets, D3's acknowledged flag
covers it, and a request-serving boot path without a KEK would be new attack
surface bought for nothing.

**Store the KEK in the database.** Rejected: it would sit in the same artifact as
the material it wraps, so one dump carries both halves. The separation between
`coffer_data` and `postgres_data` is the point.

## Amendment — 2026-10-16: the setup-screen restore takes the source key too

D4 put the source-key field on the admin restore only. The setup screen is the
disaster-recovery path — the install the backup came from is gone and this one minted a new
key — so without a field there a DR restore through the UI clears bank feeds, the stored
passphrase, Drive and notification targets (FU-074), although operations.md and install.sh
tell the operator to paste the key there. Both endpoints now share one set of rules
(`RestoreSourceKey`): base64, exactly 32 bytes, and it must match the archive's KEK
fingerprint when the archive has one, with staging cleared on any refusal. Both also clear a
key left staged by an earlier, unfinished attempt when a new restore begins, so it cannot
be adopted by a restore that supplied none.

## Amendment — 2026-10-17: nothing is sealed under the master key while it rotates

A secret sealed while a rotation is re-wrapping is stranded under the retired key: the
re-wrap has already read the table, and the restart that follows loads the new key. That
secret is unusable, and since rotation refuses anything that does not open, it blocks
every later rotation; reconciliation runs only after a restore, so nothing heals it
(FU-078).

Every path that seals under the master key now holds a lease on one process-wide gate from
before it seals until the sealed value is saved. A rotation closes the gate, waits up to
ten seconds for the leases out, re-wraps, and on commit retires the gate for good: the
process holds the old key in memory until its restart, so nothing may seal under it again.
A seal attempted while the gate is closed is a 503 with `Retry-After` (problem code
`master-key-rotating`), never a 500. A drain that times out reopens the gate and refuses
the rotation as busy. A source scan holds every file that seals to taking the lease.

D5 also covers the OAuth server keys now (ADR-0063's 2026-10-17 amendment): a pair
that does not open is deleted, and the boot after reconciliation mints one, so every MCP
client re-authorizes. The mismatch warning now names that, and the notification URLs it
had left out. A catalog test classifies every `bytea` column in the schema and fails when
one sealed under the master key is missing from rotation or from reconciliation.

## Amendment — 2026-10-17: the key moves to a volume of its own

D1 put the key file on `coffer_data`, reasoning that what mattered was keeping it off
`postgres_data`. That volume also holds every `.cofferbak`, and rotation archived old keys
beside the backups they open, so read access to one volume (a host backup of it,
`docker cp`, an unencrypted disk) yields the key and every backup with only the backup
passphrase in the way, and a rotation does not contain an exposure (FU-077).

The compose file now mounts `coffer_keys` at `/app/keys` and sets `Api:MasterKey:Path` to
`/app/keys/master.key`; rotation's archives land there too, being written beside the key.
A volume rather than `/run/secrets`: the app writes the key itself — on first boot (D2),
on rotation (D4) and on a restore's adoption — and a mounted secret is read-only. An
operator who wants a secret mount can still point `COFFER_MASTER_KEY_PATH` at one, with
the caveat D1 already records.

An existing install's key moves on the first boot with the new compose file
(`MasterKeyRelocation`), before the key is resolved: copy, verify, delete, for the key and
for each archive, so neither copy is ever the only partial one; a pending restore's record
of the key its adoption archived is rewritten to the archive's new path. It fails closed.
In a container it moves the key only onto a mount that is neither the container's root
nor the data volume, read from `/proc/self/mountinfo`, since the writable layer is
discarded by the next `up`; and it refuses to choose between two different keys, naming
both fingerprints. Either refusal stops the boot rather than serving under a key that may
be wrong or minting one over sealed data. A native run keeps the code's default,
`data/master.key`. The restore drill upgrades a previous-release install and checks the
key arrived on `coffer_keys` unchanged with no copy left beside the backups.

Not changed: the source key a restore stages for adoption still waits in the restore
staging on `coffer_data` for the seconds until the next boot adopts it, beside the
passphrase that already opens every backup on that volume. Not chosen: a stronger
passphrase floor, and pruning archived keys, which still open pre-rotation backups.

## Amendment — 2026-10-27: the OAuth server keys count as wrapped material

D3's probe read five columns: `ledgers.wrapped_lek`,
`global_scheduled_jobs.passphrase_ciphertext`, `drive_sync.oauth_ciphertext`, and
`config_ciphertext` on both notification-target tables. It left out the OAuth server's key
pair in `oidc_server_keys` (migration 253), which rotation re-wraps and reconciliation
replaces (ADR-0063's 2026-10-17 amendment). An install whose only sealed material was that
pair read as virgin, so a key-less boot minted a fresh key and MCP sign-in could no longer
open its own keys.

The probe now counts the pair: a row with either ciphertext non-empty is wrapped material,
the same "non-empty" rule the notification targets use, since both columns are `NOT NULL`
and reconciliation deletes the row rather than emptying it.

- **Every install that has booted once now refuses a key-less boot.** Every boot seals a
  pair after migrations when none is stored, so only a database no boot has finished on is
  virgin. Supply the key at the configured path, or pass `--adopt-new-kek`.
- **After `--adopt-new-kek`** the stored pair does not open under the new key; the next
  amendment is how the adopt run clears it.
- **One guard holds the three lists equal.** The catalog test that classifies every
  `bytea` column now also fails when a master-sealed column is missing from the probe, so
  rotation, reconciliation and the probe cover the same columns. It matches the source
  with comments removed, so a comment naming a column does not satisfy it.

## Amendment — 2026-10-27: adopting a new key clears what the lost one sealed

D3's `--adopt-new-kek` minted a key and exited, leaving every secret the lost key sealed
in place. Reconciliation (D5), which clears exactly that, ran only after a restore. So
after an adopt run MCP sign-in stayed off (the stored OAuth pair did not open), and every
later rotation aborted on the first blob that did not open under the current key, which
was all of them.

The adopt run now ends with that same reconciliation (`KekReconciliationService`, not a
copy of it), under the key it minted, which opens none of the old material: ledger keys
are replaced and the feed tokens they sealed cleared, the backup passphrase cleared and
its schedule disabled, Drive disconnected, notification targets disabled with the reason,
and the OAuth pair deleted. The run saves the new key, stores OAuth keys under it (the key
files of a release before 0.121.0 if they are still on the data volume, otherwise a new
pair), lists how many of each kind it abandoned, and exits. A start given the flag never
serves, on any path: over nothing sealed (a first start, or a repeat after a wipe that
committed on an install without ledgers) it clears nothing, saves its key, says there was
nothing to wipe, and exits all the same.
Abandoning is irreversible: the old key turning up later opens nothing, because nothing
sealed under it is left.

- **Where it runs.** After migrations, as post-restore reconciliation does, because it
  reads through the current EF model, and through the host's own services, on the service
  role. The adopt run applies no staged restore and adopts no staged source key; a pending
  restore waits for the next start.
- **Audited.** A `master-key.adopted-new` row in `admin_audit_events`, with the new key's
  id and the counts and never key material, is written in the reconciliation's
  transaction, so it exists exactly when the clearing does.
- **One mint at a time.** Two starts that both find no key file could each mint: two adopt
  runs would each clear under their own key and the file could end with the key whose
  ledger keys were overwritten, and a start minting as virgin could write its key between
  an adopt run's commit and its key write. Every start that may mint now takes one
  Postgres session-level advisory lock (`MasterKeyMintLock`, the built-in
  `pg_try_advisory_lock` called through EF, so no migration is needed before it), and
  re-reads the key file once it holds it. A minting start, virgin or adopting, keeps the
  key in memory, stores its OAuth keys under it (its first sealed secret), and only then
  saves the key file and releases the lock; if the OAuth store fails it saves no key and
  exits non-zero, so a virgin start's restart retries a clean first start. (An install
  whose key file exists still serves without MCP when its OAuth keys cannot be loaded.) The
  database is the lock because it is the one thing every competing container shares. What
  that guarantees: starts that share the key file location end on one key, because a start
  that waited re-reads the file and uses the holder's key. A start that cannot see that
  file (another volume) finds the holder's OAuth keys sealed and refuses; given the flag, it
  adopts in its turn and clears the holder's secrets, which is what the flag asks for.
- **The lock's session.** It is connected to the maintenance database `postgres`, not the
  install's: a staged restore applied by a first start swaps its database in with
  `ALTER DATABASE ... RENAME`, which refuses while any session is connected to the database
  being renamed. Advisory locks are per database, so every competitor locks there, on a key
  derived from the install's database name. The connection is unpooled and sets Npgsql's
  `Keepalive` (a keepalive query every 30 s while idle) and `Tcp Keepalive`. A start checks
  on that connection that it still holds the lock immediately before the clearing, before
  adopting a staged source key, and before writing a key; a lock lost by then stops the
  step (non-zero exit, nothing saved, safe to run again; if another start has since saved a
  key, that key is the install's now).
- **The flag with a key in place refuses.** A start with `--adopt-new-kek` that finds a key
  exits non-zero and says to remove the flag. Delivered as a compose `command:` override,
  the flag is rerun by the service's `restart: unless-stopped` after a successful adoption;
  that start used to find the key and serve with the flag still set, so a later key loss
  would have been wiped silently. The documented delivery is now
  `docker compose run --rm api --adopt-new-kek`: `run` creates the container with restart
  policy `no` whatever the service sets, `--rm` removes it when it exits, and the run exits
  with the command's code, while `up` gives the same service `unless-stopped`. If the key
  file present is the wrong one (a mis-mounted volume, another install's key) and the right
  one is lost, the refusal says to move it aside, keeping it, and adopt then.
- **The key file is written last** (`NewKekAdoption`), under the lock: the clearing, then
  the OAuth keys, then the key. Reconciliation is one transaction. If it fails, no key is
  saved; when the failure came before the commit nothing changed, and when the commit's
  outcome is unknown the database may hold the cleared state. If the OAuth store, the lock
  check or the key write fails after the commit, no key is saved and the database holds the
  cleared state plus what the run sealed under the unsaved key: fresh ledger keys if there
  are ledgers, and the OAuth pair if its store got that far. With either, the next start
  without a key refuses; with neither, nothing sealed is left and a start without a key
  mints as a virgin install would, losing nothing because nothing is left for any key to
  open. In every case the run exits non-zero and a repeated adopt run replaces what this
  one sealed and loses nothing more. Writing the key first would turn a failed
  reconciliation into a saved key over uncleared material, which the next start would serve
  without entering this path.
- **Reasons that named a restore** on a disconnected Drive or a disabled target now say
  only that the value was sealed under a different master key, since an adopt run writes
  them too.
- **The probe's rule for the passphrase and the Drive token** is now rotation's and
  reconciliation's: a value counts only when it is non-empty. It counted an empty one, so
  a start without a key refused over bytes nothing could open.
- **Proved** by `NewKekAdoptionTests` (every kind seeded under the lost key; a rotation and
  the OAuth load succeed after adoption and the rotation aborts without it; the audit row;
  a failure after writes inside the transaction rolls all of them back; a lost lock session
  stops the key write; both failure orders, with and without ledgers), by
  `AdoptNewKekStartTests`, which starts the real entry point as a child process on a test
  database (the clearing, the OAuth keys stored, no serving, a staged restore untouched,
  the flag refused with a key in place, the flag over nothing sealed and on a first start,
  two adopt starts at once, and a plain start that waits on a held lock and then uses the
  holder's key), and by `kek-boot-drill.sh`.

## Amendment — 2026-10-31: a private fork's upgrade token is wrapped material

[ADR-0110](0110-upgrade-from-the-app.md) adds one more secret sealed under the master KEK: the
repository token the Upgrade tab reads a private fork's releases with
(`upgrade_repo_token.token_ciphertext`, migration 269). It is registered in the three places
this ADR names. Rotation re-wraps it and reports it. Reconciliation after a restore or an
adoption deletes a row this install cannot open, and the admin enters the token again. The probe
counts a non-empty row, so a start without the key refuses over it like any other. The shared
operator wording (`SealedSecrets.Described`) and the adoption output name it. It is covered by
`MasterKeySealedColumnsTests`, `KekRotationServiceTests`, `LostKeySeed` and
`NewKekAdoptionTests`. FU-102 still applies: the list is kept by hand.
