# Operations

Day-to-day operational procedures for Coffer. Updated as the system grows.

---

> **Running Coffer for development** (spin-up, the manual Vite fallback, first-run)
> lives in the [README](../README.md#running-it-development); the standards the code
> is held to live in [engineering-standards.md](engineering-standards.md). This
> doc is operator-facing: deploying, backing up, recovering, and diagnosing a
> running install.

## Setup & first run

### Schema application: DbUp, not initdb

`docker-entrypoint-initdb.d` mounts only `db/init/00-init-roles.sh`, which creates the two RLS roles (`coffer_service`, `coffer_app`) from `COFFER_SERVICE_PASSWORD_FILE` / `COFFER_APP_PASSWORD_FILE` (compose secrets under `./secrets/` — see [Database exposure and authentication](#database-exposure-and-authentication)), falling back to the `COFFER_SERVICE_PASSWORD` / `COFFER_APP_PASSWORD` env vars when no file is configured. The schema itself lands when the API starts and runs DbUp (`MigrationRunner`) against the `ServiceConnectionString` — `coffer_service` owns the tables it creates, which is the precondition for `ENABLE ROW LEVEL SECURITY` in migration 017.

To rebuild from scratch during development:

```powershell
docker compose down -v   # WARNING: drops the postgres volume
docker compose up -d postgres
# Then run the API once (or `dotnet run --project src/Api`) so DbUp
# applies every migration against the fresh DB.
```

### Provisioning the RLS roles for non-docker installs

The role-init step is bundled into `db/init/00-init-roles.sh` so docker-compose users don't see it. For bare-metal Postgres installs (k8s, homelab, …), provision the two roles manually before pointing the API at the DB:

```sql
-- As the superuser, before running migrations:
CREATE ROLE coffer_service LOGIN BYPASSRLS PASSWORD '<chosen>';
CREATE ROLE coffer_app     LOGIN NOBYPASSRLS PASSWORD '<chosen>';
GRANT CREATE, USAGE ON SCHEMA public TO coffer_service;
GRANT USAGE          ON SCHEMA public TO coffer_app;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
```

Then point the API at the DB with both connection strings configured:

```
COFFER_API__ConnectionString=Host=...;Username=coffer_app;Password=...
COFFER_API__ServiceConnectionString=Host=...;Username=coffer_service;Password=...
```

Or keep the passwords out of the environment by leaving `Password=` off and pointing at files instead — the docker path does this by default:

```
COFFER_API__ConnectionString=Host=...;Username=coffer_app
COFFER_API__AppPasswordFile=/run/secrets/coffer_app_password
COFFER_API__ServiceConnectionString=Host=...;Username=coffer_service
COFFER_API__ServicePasswordFile=/run/secrets/coffer_service_password
```

Either form works; the file wins if both are present. See [Database exposure and authentication](#database-exposure-and-authentication).

Migration 017 guards role existence up front — a half-provisioned install fails with a clear error message instead of silently granting privileges to roles that don't exist yet.

### First-run setup ceremony

A fresh DB has no users. The first time the API starts against an
empty `webauthn_credentials` table it mints a one-shot **bootstrap
token** and logs the plaintext URL exactly once at INFO level:

```
First-run bootstrap: open http://localhost:5173/setup/<token> in your
browser within 30 minutes to register the first passkey.
```

Operator copies the URL into a browser. The setup ceremony walks
through:

1. **Create or restore** (`/setup/<token>`) — confirms the token is valid
   and unconsumed, then offers "Set up a new install" or "Restore from a
   backup" (ADR-0061).
2. **Account form** — username, display name, passkey label, and one
   optional box: **Include a Demo ledger** (ADR-0088). No ledger is
   created unless it's ticked; there is no ledger picker.
3. **Passkey registration** — WebAuthn ceremony against the operator's
   authenticator, with origin pinned to `Api.Fido2.Origins` (see
   `appsettings.Development.json` for the dev allow-list).
4. **Recovery codes** — one-shot, never re-displayable. The
   ceremony gates "Continue" on an explicit acknowledgement
   checkbox.
5. **Continue** — drops the user at the ledger hub (`/`), which lists the
   ledgers they can access and offers **New ledger** / **Import from
   Moneydance**. With no Demo ledger that list is empty, which is the
   expected first-run state.

The token is single-use and time-boxed (30-minute default, configurable
via `Api.Bootstrap.TokenLifetime`). A consumed or expired token returns
a `bootstrap-token-invalid` ProblemDetails; restart the API with the
DB still empty to get a fresh token, or hand-mint one in
`bootstrap_tokens` if you know what you're doing.

Subsequent API starts no-op the bootstrap step — if any
`webauthn_credentials` row exists, no token is minted. Recovery from a
"locked out" state (lost device + lost recovery codes) is the
DB-superuser path: truncate `webauthn_credentials`, restart the API,
register a new passkey via a fresh ceremony.

---

## Diagnostics

### Verify + heal stored balances against the canonical recompute

When a register row is suspected to display a stale running balance, hit the per-ledger verify-and-heal endpoint:

```
POST /api/ledgers/{ledgerId}/balances/health
```

It snapshots every `txn_header_account_balances` row in the ledger, re-runs `fn_recompute_balances_for_account` for every account, and returns a `BalanceHealthReport`:

```json
{
  "healthy": false,
  "accountsChecked": 12,
  "rowsChecked": 9999,
  "driftedCount": 1,
  "drifted": [
    {
      "accountId": "00000000-0000-0000-0000-0000000000aa",
      "accountName": "Account A",
      "headerId": "…",
      "postedAt": "2026-05-31T…",
      "storedBefore": 12345.67,
      "recomputedAfter": 12245.67,
      "diff": -100.00
    }
  ]
}
```

The recompute is idempotent — it's both the diagnostic and the cure. A non-empty `drifted` array means drift WAS present at snapshot time AND has now been healed; the next call should return `healthy: true`.

---

## Backups

Coffer ships a built-in **whole-database** backup ([decisions/0060-whole-db-backup-and-admin-role.md](decisions/0060-whole-db-backup-and-admin-role.md)) — this is the backup procedure (it supersedes the per-ledger export / external-`pg_dump` sketches this section once held). Per-ledger *in-place* recovery is a separate concern handled by **snapshots** ([decisions/0037-snapshots-and-backups.md](decisions/0037-snapshots-and-backups.md)), surfaced in each ledger's Settings.

### Mechanics

| Aspect | Detail |
|---|---|
| Engine | `pg_dump --format=custom --no-owner` (as `coffer_service`, so it reads every row + keeps GRANTs) → chunked AES-256-GCM, keyed by Argon2id over a passphrase. |
| Passphrase (app/scheduled) | Set once by an admin in **System → Backups**; sealed under the master KEK and stored in the DB (never plaintext). Drives both on-demand and scheduled backups. Viewable again via **Show** behind a passkey prompt (ADR-0092 D5b) — the server unseals it on every scheduled run, so offering no way to look it up only meant a forgotten passphrase silently made every backup unrestorable. |
| Create | Admin panel (on demand), the daily schedule, or the CLI: `coffer-api backup --out <path>` with `COFFER_BACKUP_PASSPHRASE` set. |
| Storage | Encrypted `.cofferbak` artifacts under `data/backups/` (the Docker volume), pruned by a tiered GFS policy: daily for `Api:Backup:RetentionDailyDays` (7), then weekly for `RetentionWeeklyWeeks` (8), then monthly for `RetentionMonthlyMonths` (12). On-box is a rolling working set; download via the admin panel to keep long-term copies off-host. |
| Restore | Three paths. On a **fresh** install (pre-auth): the **bootstrap UI** ([decisions/0061-bootstrap-restore.md](decisions/0061-bootstrap-restore.md)) — the setup screen offers *Restore from a backup*, uploads the `.cofferbak` + passphrase, and applies it on the next boot; or the **operator CLI** `coffer-api restore --in <path> --force` with `COFFER_BACKUP_PASSPHRASE` set (use the CLI for headless DR or backups larger than the ~128 MB upload ceiling). On a **running** install, an **admin** can restore in-app — **System → Backups → Restore** (ADR-0071 D3): upload + passphrase behind a typed-confirmation gate; it stages + restarts like the bootstrap path and signs everyone out. This is the "migrate from another install" path. Migrations are skipped for `restore` — the dump carries the schema at the version it was taken from, and the next normal boot migrates it forward (a 188-era backup restored onto a 192 build applies 189→192 on that boot). **Every restore path wipes the schema to empty first**, dropping only what the service role owns and leaving install-managed extensions intact; pg_restore into a populated schema collides on every existing object and merges what it can, which is a hybrid of two installs rather than a restore. |

Bring the **same master key** to the recovery host so the restored `wrapped_lek` columns + the sealed backup passphrase decrypt as-is; WebAuthn login survives if the RP id/domain + authenticator are unchanged (see ADR-0060). A cross-install KEK mismatch is caught pre-flight by the **admin-UI restore** (ADR-0071 D4): backups carry a KEK fingerprint, and the restore warns before applying if the target install's KEK differs.

Since [ADR-0092](decisions/0092-kek-lifecycle-in-the-ui.md) D4 the restore form takes the **source install's master key** directly — paste it and its sealed secrets carry over, and the server checks it against the archive's fingerprint before anything destructive runs, so a wrong paste is refused up front rather than discovered afterwards. Leave it empty and the restore still succeeds: D5 reconciliation then clears what won't open and tells you what to re-establish (bank feeds, the backup passphrase, Google Drive).

Backup encryption is mandatory, per [decisions/0014-encryption-at-rest.md](decisions/0014-encryption-at-rest.md) Layer 2. Plaintext backups are not acceptable — the built-in `.cofferbak` is always passphrase-encrypted.

Restores are not real until tested. Run a periodic restore drill: `coffer-api restore` the latest artifact into a **throwaway** Postgres and confirm a sample of account balances against live. Never `--force`-restore into the live database — it replaces everything.

A drill into a throwaway install hits the cross-KEK guard, because that install minted its own master key: the restore refuses up front (it compares the backup's KEK fingerprint before decrypting anything, so this costs nothing). Two ways through, and they test different things:

- **Copy the live install's key into the throwaway's key file first.** Nothing is refused, and the sealed secrets — backup passphrase, feed tokens, Drive — restore usable. This is what a real migration to new hardware looks like, so it is the drill worth running.
- **Pass `--allow-kek-mismatch`.** The data and passkeys restore; reconciliation then clears every secret sealed under the other key and prints what it abandoned. Fine for checking balances, but it does not exercise the path you would actually use in a recovery.

### Provisioning a fresh install

Nothing to provision — bring the stack up and open the setup URL
([decisions/0088-setup-asks-one-question.md](decisions/0088-setup-asks-one-question.md)).
`scripts/dev-up-docker.sh` prints the one-shot `/setup/<token>` link on a fresh
DB; on an existing install you can reissue it with

```bash
docker compose exec api dotnet coffer-api.dll bootstrap-token
```

> **Invoking the operator CLI in the container.** Every `coffer-api <subcommand>`
> below (`bootstrap-token`, `backup`, `restore`) is reached as
> `docker compose exec api dotnet coffer-api.dll <subcommand>`. The image's
> ENTRYPOINT is `["dotnet","coffer-api.dll"]` and it ships no apphost binary, so
> `docker compose exec api coffer-api …` exits 127 with "executable file not
> found in $PATH".

The setup form asks for a username, display name and passkey label, plus one
optional box: **Include a Demo ledger**, which seeds a worked example through the
normal Moneydance import pipeline. Leave it unticked and setup creates **no
ledger at all** — you land on the ledger hub and pick **New ledger** or **Import
from Moneydance** there.

Every ledger created empty (the hub's "New ledger" dialog) seeds a starter
category tree by default (ADR-0071 D5). The Demo ledger doesn't — it brings its
own categories with the dataset.

> Removed in ADR-0088: `scripts/provision.sh` and the `coffer-api provision
> --mode <clean|demo>` subcommand. They shaped install state before the first
> user existed, which only made sense while migrations seeded placeholder
> `Default`/`Demo` ledgers. Migration 186 drops those, so there is nothing left
> to shape.

### Disaster recovery: restore onto a new machine

Validated end-to-end (backup on one install → restore into a fresh one → the
app boots on the restored DB). Compose-based; ordering matters — **restore
before the API server ever starts** on the fresh DB, or the server migrates the
empty DB and collides with the restore.

**Fast path — `install.sh` (recommended; handles Docker, the config files, the
KEK, and the start-order for you).** From your DR kit you need three things — the
`.cofferbak` artifact, its passphrase, and the backup's
`COFFER_MASTER_KEK_BASE64` (+ its id) — plus a GitHub PAT with `repo` +
`read:packages` (the repo + image are private; see the README's *private repo*
install one-liner for the exact command). On the fresh box:

1. **Run the installer** (README's one-liner). Answer its prompts: install Docker
   (yes, if asked); how you'll reach Coffer (**localhost**, or your **domain** —
   the RP id, which must match the backup's for passkeys to still validate); and
   **"restoring a backup?" → yes**, then paste the backup's
   `COFFER_MASTER_KEK_BASE64` + id (`v1`, or `v2`+ if you'd rotated). It writes
   `.env` with that KEK, pulls the image, and starts an empty install.
2. **Restore.** Open the setup URL it prints → **Restore from a backup** → upload
   your `.cofferbak` + passphrase. It restarts and applies; the data decrypts
   under the KEK you provided — no manual key-swap.
3. **Sign in.** A passkey works if the RP id is unchanged; otherwise **Use a
   recovery code**, then add a fresh passkey. Confirm ledgers + balances.

Backup over the ~128 MB UI upload cap, or a headless host? Use the compose-level
steps below — the same thing the installer does, by hand, with `coffer-api
restore` on the CLI instead of the upload.

**On the source install — export an artifact:**

1. Create a backup (admin **System → Backups → Create**, or the CLI:
   `docker compose run --rm -e COFFER_BACKUP_PASSPHRASE='…' api backup --out /app/data/dr.cofferbak`).
2. Get the `.cofferbak` off-box (the panel's **Download**, or
   `docker compose cp api:/app/data/dr.cofferbak ./`). Keep three things safe and
   **separate**: the artifact, its passphrase, and `COFFER_MASTER_KEK_BASE64`
   from `.env`.

**On the new machine — manual restore (compose-level fallback):**

1. Install Docker. You need `docker-compose.yml`, `db/init/`, and a `.env`
   carrying the **same `COFFER_MASTER_KEK_BASE64`** plus DB passwords. On a
   **private repo**, fetch `docker-compose.yml` + `db/init/00-init-roles.sh` with
   a `repo`-scoped PAT via the GitHub contents API (anonymous raw 404s) — the
   README's *private repo* install one-liner does exactly this via
   `install.sh` + `COFFER_GH_TOKEN`; the same PAT (add `read:packages`) covers
   the ghcr login below. The API image comes one of two ways:
   - **Pull the prebuilt image (preferred — no build toolchain).** It's a
     **private** ghcr package, so authenticate first, then pull:
     `docker login ghcr.io -u <user>` (a `read:packages` PAT — keep it in your
     DR kit), then `docker compose pull api`. Pin a version with
     `COFFER_IMAGE_TAG=<tag>` in `.env` if you don't want `latest`.
     **`COFFER_IMAGE` must be set** — compose has no default for it, deliberately.
     `.github/workflows/release.yml` publishes to
     `ghcr.io/<the building repo's owner>/coffer`, so whoever built the image owns
     the package and there is no one value that suits every fork; a hardcoded
     fallback only meant an install could quietly pull from a registry that wasn't
     its own. `install.sh` derives it from the repo it fetched your config from and
     writes it to `.env`. On a DR host built by hand, set it yourself — and confirm
     it *before* you need it, because a recovery is a bad time to discover the
     image name is wrong. Built + pushed
     by `.github/workflows/release.yml` on a `vX.Y.Z` tag (or manually —
     `docker build` + `docker push ghcr.io/<owner>/coffer:<tag>`).
   - **Or build from source:** also bring `Dockerfile` + `src/`; `docker compose
     build api` (needs the build toolchain — slower).
2. Start **only Postgres** (a fresh data dir runs `db/init` and mints the
   `coffer_app` / `coffer_service` roles + the `pg_trgm`/`pgcrypto` extensions):
   `docker compose up -d postgres`
   **The init script must be executable AND world-readable** —
   `chmod 755 db/init/00-init-roles.sh`. It's bind-mounted into the Postgres
   container, which runs as a different uid; a `0600` file (a common result of
   `scp`/`cat >`) is unreadable there, so the script silently fails and **no
   roles get created** (the restore then fails to connect — see Troubleshooting).
   Verify before continuing:
   `docker compose exec postgres psql -U coffer -d coffer -c "\du"` — you must see
   `coffer_service` and `coffer_app`.
3. Restore into it, before the API server runs (bind-mount the artifact into a
   one-off `api` container):
   `docker compose run --rm -v "$(pwd)/dr.cofferbak:/restore/dr.cofferbak:ro" -e COFFER_BACKUP_PASSPHRASE='…' api restore --in /restore/dr.cofferbak --force`
   Expect benign `pg_trgm` / `pgcrypto` comment-ownership warnings (extensions
   are install-managed), then `Restore complete.`
   **Or skip steps 3–4 and use the bootstrap UI** ([decisions/0061-bootstrap-restore.md](decisions/0061-bootstrap-restore.md)):
   bring up `api` on the empty DB, open the setup URL, pick *Restore from a
   backup*, and upload the `.cofferbak` + passphrase — the server stages it and
   restarts to apply. The CLI path above stays the choice for headless DR or
   artifacts over the ~128 MB upload ceiling.
4. Start the app: `docker compose up -d api`. It sees a fully-migrated DB from
   the dump (applies 0 migrations) and serves on `:8080`.
5. **Sign in — WebAuthn has hard requirements:**
   - The page must be a **secure context**: `http://localhost` (a browser *on
     the box*) or **HTTPS**. Plain `http://<lan-ip>` shows "WebAuthn is not
     supported" — that's the browser disabling the API, not a bug.
   - RP ids **cannot be IP addresses**. For network access, front the box with
     HTTPS at a **hostname** and set `COFFER_RP_ID` / `COFFER_WEB_URL` to it.
   - A **roaming key** (YubiKey) is the portable credential; a platform
     authenticator (Windows Hello / Touch ID) stays on its original machine.
   - The security key must be on the machine running the **browser** (it can't
     be used over SSH). On Linux, grant the key to your user — udev rule
     `KERNEL=="hidraw*", ATTRS{idVendor}=="1050", MODE="0660", GROUP="plugdev", TAG+="uaccess"`,
     add yourself to `plugdev`, replug — and use a **non-Snap** browser (Snap
     Chromium/Firefox can't reach USB keys).
   - If the RP id changed from the source install, the old credential won't
     validate (a passkey is bound to the RP id it was created under). Click
     **"Use a recovery code"** on the sign-in page, enter your username + one
     of the codes from the backed-up account, and you land on
     **Account → Security** — add a fresh passkey there (and remove the dead
     one). This recovery path ([decisions/0013-webauthn-passkey-auth.md](decisions/0013-webauthn-passkey-auth.md)) is why a
     restore onto a new domain isn't a lock-out.
   Then confirm your ledgers + balances; the data is intact regardless of the
   login mechanics.

`--force` is required and destructive (it replaces the target DB) — only ever on
the fresh recovery host, never the live one.

### Troubleshooting restore

| Symptom | Cause / fix |
|---|---|
| `IOException: Pipe is broken` / pg_restore exits immediately | pg_restore couldn't connect — usually the roles weren't created (init script not run). Check `docker compose logs postgres` for `Permission denied` on `00-init-roles.sh` (fix perms, `down -v`, re-up) or `role "coffer_service" does not exist`, and `\du`. |
| Restore errors beyond the two extension warnings | Wrong passphrase or a truncated `.cofferbak` (re-copy in binary mode); verify the file size matches the source. |
| App shows empty/garbled data after restore | `COFFER_MASTER_KEK_BASE64` doesn't match the source — it must be byte-identical. |
| "WebAuthn is not supported in this browser" | Insecure context — use `http://localhost` on the box or serve HTTPS (see step 5). |

### Off-host: Google Drive sync

An optional, off-by-default destination that copies each backup to a folder in **your own** Google Drive ([decisions/0062-google-drive-backup-sync.md](decisions/0062-google-drive-backup-sync.md)). Only the **already-encrypted** `.cofferbak` leaves the host — Google holds ciphertext; the passphrase and `COFFER_MASTER_KEK_BASE64` never go to Google. Connecting uses **your own** Google Cloud OAuth client (no Coffer-run broker), so there's a one-time project setup:

**One-time Google Cloud setup (the operator):**

1. At [console.cloud.google.com](https://console.cloud.google.com), create (or pick) a project.
2. **APIs & Services → Library → Google Drive API → Enable.**
3. **APIs & Services → OAuth consent screen:** user type **External**, fill the app name + your email, and add your Google account under **Test users** (a test-mode app is fine — it never needs Google verification because only you use it). No scopes need adding here.
4. **APIs & Services → Credentials → Create credentials → OAuth client ID**, application type **Web application** (you can also **reuse an existing Web client** — see below). Under **Authorized redirect URIs**, add your Coffer origin + the callback path, e.g. `https://coffer.example.org/api/admin/drive-sync/oauth/callback` (use the exact public origin you reach Coffer at — the same host as its HTTPS / WebAuthn domain). Copy the **Client ID** and **Client secret**.

> **Reusing a client you already have:** authorized redirect URIs are an additive list, so you can add Coffer's callback URI to an existing **Web application** client without affecting the apps that already use it — no new secret to manage. (Desktop / "TVs and Limited Input devices" client types can't redirect to an HTTPS origin, so they won't work here; use a Web client.)

> **Enable the Google Drive API** in the same project (APIs & Services → Library → **Google Drive API** → Enable). Without it the connect completes the OAuth dance but the first Drive call fails with `accessNotConfigured`. (Reusing a client from another product — e.g. Home Assistant — is fine, but that project may not have the Drive API turned on.)

**Connect (admin, in Coffer):** **System → Backups → Google Drive sync → Connect Google Drive**. Paste the client ID + secret and click **Continue to Google** — you're redirected to Google's consent screen, and on approval Google returns you to Coffer, which seals the refresh token under the master KEK, creates a folder named **`Coffer Backups [<install id>]`**, and shows the connected account + install id. **Sync now** pushes the latest local artifact; **Disconnect** forgets the token (backups already in Drive are left untouched).

> **Per-install folders:** each Coffer install gets a stable install id and its own `Coffer Backups [id]` folder, so multiple installs sharing one Google account/OAuth client don't commingle (and a later retention sweep won't prune across installs). The install id is shown on the card.

> The redirect URI registered in Google must **exactly** match `‹your Coffer origin›/api/admin/drive-sync/oauth/callback`. A mismatch shows a Google `redirect_uri_mismatch` error before consent. Coffer derives the origin from its first configured WebAuthn origin (`Api:Fido2:Origins`).

Notes:
- Scope is `drive.file` — Coffer can only see/manage files it created, never the rest of your Drive.
- The sealed token is re-wrapped automatically by a KEK rotation (see Encryption at rest), so rotating doesn't break uploads.
- **Automatic upload:** turn on **"Automatically upload each new backup to Google Drive"** and every backup (the daily scheduled run + manual creates) is uploaded as `{id}.cofferbak` into the per-install folder. A failure never fails the backup — it's recorded as the card's last-upload error, and a **"Not uploaded recently"** badge appears if auto-upload hasn't succeeded recently.
- **Drive retention** is independent of local retention (set the daily/weekly/monthly tiers on the card). Pruning is delete-only-once-replaced and never removes the newest artifact.
- **Pin a backup** ("never delete") from the backups list to exclude it from both local and Drive retention. **Upload all backups now** uploads every local backup not already on Drive (catch-up / first-time backfill).
- Backups are compressed with `pg_dump --compress=zstd` by default (`Api:Backup:Compress`; ~10% smaller than zlib). Set e.g. `zstd:19` for a smaller artifact at higher CPU; PG16 restore reads any of these.

---

## Encryption at rest

A layered model is in effect; full rationale in [decisions/0014-encryption-at-rest.md](decisions/0014-encryption-at-rest.md).

| Layer | Scope | Operator responsibility |
|---|---|---|
| 1 — Host disk | Whole machine | Required. Use LUKS / BitLocker / FileVault / ZFS native encryption on the host running Docker. The app does not enforce this; the operator must configure it before deploying production data. |
| 2 — Backups | Off-host backup files | Required when backups exist (see above). |
| 3 — Application envelope encryption | Bank-feed OAuth tokens and other high-value secrets | Required for the in-scope secrets only; not used for bulk transaction data. |
| 4 — KEK source | The master key that wraps the per-ledger LEKs (and the sealed backup passphrase) | A base64 key **file** at `Api:MasterKey:Path` (default `data/master.key` on the `coffer_data` volume), per ADR-0092 D1. Point `COFFER_MASTER_KEY_PATH` at `/run/secrets/…` or a projected Kubernetes Secret to keep it off the app's volume. `COFFER_MASTER_KEK_BASE64` is deprecated: honoured for one release, copied to the key file on first boot with a warning in the log, then removed. Can still graduate to a TPM-sealed or hardware-derived KEK without schema change. Rotated from the UI — **System → Encryption → Rotate** (see *Rotating the master KEK*); the `rotate-kek` CLI subcommand was removed by ADR-0092. |

PostgreSQL TDE is **not** in scope — the OSS Postgres distribution doesn't provide it, and Layers 1 + 3 cover the realistic threats. Whole-DB column encryption is **explicitly rejected** for bulk data because it breaks indexing and trigram search; see the ADR for the trade-off.

### Rotating the master KEK

Re-key without re-encrypting data ([decisions/0026-per-ledger-encryption-key.md](decisions/0026-per-ledger-encryption-key.md) §Rotation, as amended by [ADR-0092](decisions/0092-kek-lifecycle-in-the-ui.md) D4): envelope encryption means only the *wrappings* change — every `ledgers.wrapped_lek`, the backup passphrase, and the Drive token are re-wrapped under the new KEK in one all-or-nothing transaction.

**Rotation lives in the UI: System → Encryption → Rotate.**

1. Type `rotate` to confirm. There is no separate check step: rotation's own first action is the dry run — it verifies every wrapped value opens under the *current* key, writes nothing, and refuses before touching anything if any of them don't. If it refuses, the install is already in a mismatched state (a cross-KEK restore, say); fix that first. A check you could forget to run was worse than one you can't.
2. The new key id is assigned by the server, incrementing the current one (`v1` → `v2`). It isn't a prompt — it's a label nothing depends on, so choosing it was a decision with no consequence in the middle of an operation that has several.
3. The server generates the key, re-wraps everything, swaps the key file, and **restarts** to load it. The panel waits for the server to come back and confirms when it's running on the new key — no action needed while it does.
4. **Save the new key.** It's on screen, it's on disk, and it's viewable again later via **Show key** (ADR-0092 D2 — deliberately not show-once), but a server you've lost can't show you anything.
5. The previous key file is kept alongside the new one (`master.key.<timestamp>.bak`) so a mistaken rotation is reversible. Delete it once you've confirmed the new key works.
6. **Re-take backups** under the new key and retire the old ones — a `.cofferbak` carries the wrapped LEKs as they were at dump time, so each backup is bound to the KEK era it was taken under (archive the old key with old backups, or discard both).

If `COFFER_MASTER_KEK_BASE64` is still set in `.env`, **remove it**. The key file wins (ADR-0092 D1), so a stale value there is ignored — the startup log says so — and it only invites confusion after a rotation.

> The `rotate-kek` CLI subcommand was removed in ADR-0092. Rotation is routine
> hygiene rather than disaster recovery, so an operator who can't sign in needs
> recovery codes, not a rotation. (`restore` remains a CLI command because it
> genuinely can't be a UI one — it skips migrations, so it works on a schema too
> broken for the app to serve.)

---

## Secrets

- `.env` is gitignored. Real credentials live there only on the host.
- `.env.example` is committed and contains placeholder values exclusively.
- OAuth tokens (SimpleFIN, a major brokerage via SimpleFIN/MX) live in the database, **envelope-encrypted** (Layer 3 above) — not in env files.
- The master KEK lives in its own file (`data/master.key` on the `coffer_data` volume by default, `0600`), **not** in `.env` — ADR-0092 D1 retired the env var because its value is readable via `docker inspect`, `/proc/<pid>/environ`, child environments and crash dumps. Encrypted at rest by Layer 1, never written to backups in plaintext, never committed. Deliberately on a different volume from `postgres_data`, so one dump can't carry both the wrapped material and the key that opens it.
- Never log raw tokens, raw CSV/OFX uploads, or full transaction memos at INFO level.

### Database exposure and authentication

Two deliberate settings in `docker-compose.yml`, both about the database refusing
connections it has no reason to accept:

- **The published Postgres port is bound to `127.0.0.1`.** The application never
  uses it — the API resolves `postgres` over the compose network — so it exists
  only for an operator attaching `psql` or a GUI client from the host. Bound to
  `0.0.0.0` (the earlier default) it offered all three roles to anything that
  could route to the host. Drop the `ports:` stanza entirely if nothing on the
  host needs to connect.
- **Every auth path requires a password**, via
  `POSTGRES_INITDB_ARGS=--auth-local=scram-sha-256 --auth-host=scram-sha-256`.
  `initdb`'s defaults are `trust` for the unix socket, `127.0.0.1` and `::1`,
  which the entrypoint leaves in place — it only appends the `host all all all`
  rule. That trust isn't reachable from outside the container, but it makes
  anything running inside it superuser with no credential, which bypasses the
  RLS boundary the authorization model rests on (`coffer_app` NOBYPASSRLS vs
  `coffer_service` BYPASSRLS), and it would become a genuine hole if the
  deployment ever moved to `network_mode: host` or a bare VM.

**Upgrading an existing install:** `initdb` runs exactly once, so an install
created before this landed still has `trust` in its `PGDATA` — the compose
setting does nothing for it. Run the one-time remediation:

```bash
scripts/harden-pg-hba.sh          # or: PG_CONTAINER=… ENV_FILE=… scripts/harden-pg-hba.sh
```

It rewrites `pg_hba.conf` and reloads (no restart, no downtime), and it is
idempotent, so it is safe from an upgrade path. It refuses to change anything
unless `POSTGRES_PASSWORD` and `COFFER_APP_PASSWORD` are proven to authenticate
first — after the change those passwords are the only way in — and it rolls back
if verification fails afterward. A half-applied `pg_hba` is how an application
gets locked out of its own database.

Anything reaching Postgres over the container's socket needs a password once
this is applied. `scripts/backup-restore-roundtrip.sh` and
`scripts/harden-pg-hba.sh` therefore read the superuser password from
`secrets/postgres_password`, falling back to `POSTGRES_PASSWORD` in `.env` for an
install that predates the move to files. Override with `SECRETS_DIR` when
targeting a second stack. The schema-apply test lane is unaffected — it runs its
own ephemeral container, which never sees these settings.

#### The passwords themselves live in files, not the environment

All three — the superuser, `coffer_service` and `coffer_app` — are files under
`./secrets/`, mounted by compose at `/run/secrets/<name>`:

| File | Role | Read by |
|---|---|---|
| `secrets/postgres_password` | superuser | the Postgres entrypoint (`POSTGRES_PASSWORD_FILE`) |
| `secrets/coffer_service_password` | `coffer_service` (BYPASSRLS) | `db/init` at first boot; the API (`Api:ServicePasswordFile`) |
| `secrets/coffer_app_password` | `coffer_app` (NOBYPASSRLS) | `db/init` at first boot; the API (`Api:AppPasswordFile`) |

Same reasoning ADR-0092 D1 applied to the master KEK: an environment variable is
readable via `docker inspect`, `/proc/<pid>/environ`, any child process's
environment and crash dumps, and these authenticate every query the application
makes. The API injects each password into its connection string at startup
(`DbPasswordResolver`) before anything binds the configuration, so the dozen-odd
consumers across the API, the backup service and the importer are unchanged —
what they receive is a finished connection string. The connection *topology*
stays in compose in plain sight, because that is what you need to read when a
connection is refused and none of it is secret.

A configured-but-unreadable password file is a startup failure, not a fallback.
The alternative is an install that connects with no password — which against a
Postgres still on `trust` succeeds, and an install that authenticates by
accident is worse than one that refuses to boot.

Permissions are deliberately `0700` on the directory and `0644` on the files.
Outside swarm, compose ignores `uid`/`gid`/`mode` and the file keeps its host
ownership, and the Postgres entrypoint re-execs as `postgres` (uid 999) before
reading `POSTGRES_PASSWORD_FILE` — so a `0600` file owned by the installing user
is unreadable in-container. The directory is what keeps other local users out.

**Upgrading an existing install**, whose passwords are still in `.env`:

```bash
scripts/migrate-db-secrets.sh     # copies .env values into secrets/, comments the .env lines out
docker compose up -d              # recreates with the file-based secrets
```

It does not rotate anything — the credentials the database already knows keep
working, because rotating during a migration would mean two things could fail at
once with no way to tell which. `scripts/install.sh` does the same automatically
on its upgrade path. Both leave a `.env.pre-secrets` backup that still contains
the passwords; delete it once the stack is confirmed healthy.

#### Container privileges and connection logging

Both containers run with a reduced kernel privilege set. Nothing here needs
configuring — it is in `docker-compose.yml`, and `scripts/install.sh` rewrites
that file on upgrade, so an existing install picks it up on its next
`docker compose up -d` with no migration step.

| Setting | `postgres` | `api` |
|---|---|---|
| `no-new-privileges` | yes | yes |
| `cap_drop` | `ALL`, then five added back | `ALL`, nothing added back |
| `mem_limit` | `POSTGRES_MEM_LIMIT` (1g) | `COFFER_API_MEM_LIMIT` (1g) |

Postgres gets `CHOWN`, `DAC_OVERRIDE`, `FOWNER`, `SETUID` and `SETGID` back
because its entrypoint starts as root to fix ownership on `PGDATA` and the socket
directory, then re-execs as `postgres` via `gosu`. This is measured, not
inferred: with a bare `cap_drop: [ALL]` a fresh install dies during `initdb` with
`chmod: changing permissions of '/var/lib/postgresql/data': Operation not
permitted`, then `error: failed switching to 'postgres': operation not
permitted`. With the five restored, `initdb` completes, and `CapEff` on the
postmaster is `0` — the privilege drop still happens, and the running database
holds nothing. Everything else (`NET_ADMIN`, `SYS_ADMIN`, `SYS_PTRACE`, `MKNOD`,
`NET_RAW`, …) is gone.

The API needs none of them: it runs as root start to finish so there is no
privilege drop to perform, it owns `/app/data` on its own volume so it can write
the master key and the bootstrap URL without `DAC_OVERRIDE`, it binds 8080 so it
needs no `NET_BIND_SERVICE`, and the backup path's `pg_dump`/`pg_restore`/`psql`
are ordinary TCP clients. Verified against a fresh prod-shaped stack that
bootstrapped, took a backup and restored it with everything dropped.

If you need to debug inside either container with a tool that wants a capability
(`strace` and `gdb` need `SYS_PTRACE`), add it to that service's `cap_add`
temporarily rather than removing the stanza.

Postgres also runs with `log_connections=on` and `log_disconnections=on`. With
scram on every path and RLS as the authorization boundary, the database is the
last line of defence, and a successful login previously left no trace — there was
no way to answer "who connected, as which role, from where?" after the fact. The
log now names the role on every connection (`connection authorized: user=…
database=…`), which also makes a credential-stuffing attempt against the
loopback port visible, and records session duration on disconnect. Expect a
couple of lines per connection; the API pools, so steady-state volume is low.

---

## Importer environment variables

| Variable | Purpose |
|---|---|
| `COFFER_DB_CONNECTION` | Postgres connection string used by the Moneydance importer. CLI flag `--db <CONNECTION_STRING>` takes precedence when present. Required for any non-`--dry-run` run. |

---

## MCP client troubleshooting (ADR-0063)

### Calls time out but the server is healthy → the local `mcp-remote` proxy is wedged

Claude Desktop / Gemini reach the server through a local `npx mcp-remote` Node
proxy that occasionally hangs (dead socket to the CDN); then every MCP call times
out even though the server is fine. Recovery is to kill the stuck proxy and let the
client respawn a fresh one — scripted in
[`scripts/restart-coffer-mcp.sh`](../scripts/restart-coffer-mcp.sh) (Linux/macOS/WSL)
and [`scripts/restart-coffer-mcp.ps1`](../scripts/restart-coffer-mcp.ps1) (Windows).
Both kill the proxy, health-check the server's discovery endpoint (so you can tell a
hung proxy from a real outage), and — with `--clear-auth` / `-ClearAuth` — wipe the
cached `~/.mcp-auth` token. Then fully quit and reopen the client.

### CDN/WAF bot protection challenges the connector

**Symptom:** the web UI works, but MCP clients (Claude Desktop via `mcp-remote`,
Gemini CLI, the claude.ai connector) fail with "Server disconnected" / a 403, and
the client log shows an HTML challenge page (e.g. Cloudflare's "Just a moment…
Enable JavaScript and cookies") instead of JSON on `POST /mcp`.

**Cause:** bot protection in front of Coffer (e.g. Cloudflare **Bot Fight Mode**,
**AI bot blocking**, or a managed/Security-Level challenge) challenges non-browser
clients. MCP clients are non-browser HTTP clients (user-agent `undici`/node) that
can't solve a JS/cookie challenge, so they get the challenge page, not the API. A
browser passes it, which is why the URL "works in the browser" but the connector
doesn't. (Discovery `GET /.well-known/...` may slip through while `POST /mcp` is
challenged — confirm in the CDN's security-events log, which names the matched
service.)

**Affected paths** (all hit non-interactively by clients): `/mcp`, `/.well-known/*`,
and `/oauth/token` + `/oauth/register`. `/oauth/authorize` + `/login` + `/oauth/consent`
are browser-driven and usually pass.

**Fixes** (in order of preference):
- **Allowlist your client IP** (Cloudflare → IP Access Rules → *Allow*). Keeps bot
  protection on for everyone else. For a dynamic IP, automate it — point a small
  job at your DDNS hostname and PATCH the IP Access Rule via the Cloudflare API
  (e.g. a Home Assistant automation triggered off your WAN-IP sensor).
- **Pro+ plans:** a WAF custom rule with action *Skip* (Super Bot Fight Mode +
  managed rules) scoped to the paths above. Not available on free (Bot Fight Mode
  is global there).
- **Grey-cloud a dedicated API subdomain** (DNS-only, bypassing the CDN) routed to
  the same container; keep the web host proxied. WebAuthn on the subdomain needs
  two settings (both wired by `install.sh`, but a pre-MCP install predates the
  second — see below):
  - `COFFER_RP_ID` = the registrable parent domain (e.g. `example.org`) covering
    both hosts. A passkey registered under the parent works on the subdomain with
    **no re-registration** (the RpId is a valid suffix).
  - `COFFER_MCP_URL` = the subdomain origin (e.g. `https://mcp.example.org`),
    wired to `Api:Fido2:Origins__1`. The OAuth sign-in runs on the subdomain, so
    its origin must be an **allowed origin** — without it, sign-in there fails with
    `origin https://mcp.example.org … not equal to … https://example.org`. Set it in
    `.env` and `docker compose up -d`. (Installs created before this knob existed
    only have `Origins__0`; add the line to the compose or re-run `install.sh` to
    pick up the current one.)

    This is also what the admin UI shows as the address to give a client
    (ADR-0093). On a split-host install it is the only way for that panel to be
    right: unset, it falls back to the origin the admin happens to be browsing,
    which is the **web** host. `COFFER_WEB_ORIGIN_1` still works as a fallback for
    the origin, but does not feed the displayed address.
- **Disable the bot protection.** Coffer's real boundary is WebAuthn + RLS +
  off-by-default MCP + OAuth; bot protection is DDoS/noise reduction, not the auth
  layer. Acceptable for a personal, fully passkey-gated install.
