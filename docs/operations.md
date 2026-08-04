# Operations

Day-to-day operational procedures for Coffer. Updated as the system grows.

---

> **Running Coffer for development** (spin-up, the manual Vite fallback, first-run)
> lives in the [README](../README.md#running-it-development); contributor workflow,
> CI, and the release process live in [CONTRIBUTING.md](../CONTRIBUTING.md). This
> doc is operator-facing: deploying, backing up, recovering, and diagnosing a
> running install.

## Setup & first run

### Schema application: DbUp, not initdb

`docker-entrypoint-initdb.d` mounts only `db/init/00-init-roles.sh`, which creates the two RLS roles (`coffer_service`, `coffer_app`) from `COFFER_SERVICE_PASSWORD` / `COFFER_APP_PASSWORD`. The schema itself lands when the API starts and runs DbUp (`MigrationRunner`) against the `ServiceConnectionString` — `coffer_service` owns the tables it creates, which is the precondition for `ENABLE ROW LEVEL SECURITY` in migration 017.

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
| Passphrase (app/scheduled) | Set once by an admin in **Admin → Backups**; sealed under `COFFER_MASTER_KEK_BASE64` and stored in the DB (never plaintext). Drives both on-demand and scheduled backups. |
| Create | Admin panel (on demand), the daily schedule, or the CLI: `coffer-api backup --out <path>` with `COFFER_BACKUP_PASSPHRASE` set. |
| Storage | Encrypted `.cofferbak` artifacts under `data/backups/` (the Docker volume), pruned by a tiered GFS policy: daily for `Api:Backup:RetentionDailyDays` (7), then weekly for `RetentionWeeklyWeeks` (8), then monthly for `RetentionMonthlyMonths` (12). On-box is a rolling working set; download via the admin panel to keep long-term copies off-host. |
| Restore | Three paths. On a **fresh** install (pre-auth): the **bootstrap UI** ([decisions/0061-bootstrap-restore.md](decisions/0061-bootstrap-restore.md)) — the setup screen offers *Restore from a backup*, uploads the `.cofferbak` + passphrase, and applies it on the next boot; or the **operator CLI** `coffer-api restore --in <path> --force` with `COFFER_BACKUP_PASSPHRASE` set (use the CLI for headless DR or backups larger than the ~128 MB upload ceiling). On a **running** install, an **admin** can restore in-app — **System → Backups → Restore** (ADR-0071 D3): upload + passphrase behind a typed-confirmation gate; it stages + restarts like the bootstrap path and signs everyone out. This is the "migrate from another install" path. Migrations are skipped for `restore` (the dump rebuilds the schema). |

Bring the **same `COFFER_MASTER_KEK_BASE64`** to the recovery host so the restored `wrapped_lek` columns + the sealed backup passphrase decrypt as-is; WebAuthn login survives if the RP id/domain + authenticator are unchanged (see ADR-0060). A cross-install KEK mismatch is caught pre-flight by the **admin-UI restore** (ADR-0071 D4): backups carry a KEK fingerprint, and the restore warns before applying if the target install's KEK differs — set the source's KEK and re-upload for a clean migration, or acknowledge to proceed and re-set the backup passphrase + reconnect Google Drive afterward.

Backup encryption is mandatory, per [decisions/0014-encryption-at-rest.md](decisions/0014-encryption-at-rest.md) Layer 2. Plaintext backups are not acceptable — the built-in `.cofferbak` is always passphrase-encrypted.

Restores are not real until tested. Run a periodic restore drill: `coffer-api restore` the latest artifact into a **throwaway** Postgres and confirm a sample of account balances against live. Never `--force`-restore into the live database — it replaces everything.

### Provisioning a fresh install

Nothing to provision — bring the stack up and open the setup URL
([decisions/0088-setup-asks-one-question.md](decisions/0088-setup-asks-one-question.md)).
`scripts/dev-up-docker.sh` prints the one-shot `/setup/<token>` link on a fresh
DB; on an existing install you can reissue it with

```bash
docker compose exec api dotnet coffer-api.dll bootstrap-token
```

> **Invoking the operator CLI in the container.** Every `coffer-api <subcommand>`
> below (`bootstrap-token`, `backup`, `restore`, `rotate-kek`) is reached as
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
     `COFFER_IMAGE_TAG=<tag>` in `.env` if you don't want `latest`. Built + pushed
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
     HTTPS at a **hostname** and set `COFFER_RP_ID` / `COFFER_WEB_ORIGIN_0` to it.
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
- The sealed token is re-wrapped automatically by `rotate-kek` (see Encryption at rest), so a KEK rotation doesn't break uploads.
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
| 4 — KEK source | The master key that wraps the per-ledger LEKs (and the sealed backup passphrase) | Configured at deployment time via the `COFFER_MASTER_KEK_BASE64` env var; can graduate to a TPM-sealed or hardware-token-derived KEK without schema change. Rotatable via `coffer-api rotate-kek` (see *Rotating the master KEK*). |

PostgreSQL TDE is **not** in scope — the OSS Postgres distribution doesn't provide it, and Layers 1 + 3 cover the realistic threats. Whole-DB column encryption is **explicitly rejected** for bulk data because it breaks indexing and trigram search; see the ADR for the trade-off.

### Rotating the master KEK

Re-key without re-encrypting data ([decisions/0026-per-ledger-encryption-key.md](decisions/0026-per-ledger-encryption-key.md) §Rotation): envelope encryption means only the *wrappings* change — every `ledgers.wrapped_lek` and the backup passphrase are re-wrapped under the new KEK, in one all-or-nothing transaction. On the running deployment:

1. Generate a new key: `openssl rand -base64 32`.
2. Dry-run (verifies every blob opens under the *current* key; writes nothing):
   `COFFER_MASTER_KEK_NEW_BASE64=<new> docker compose run --rm api rotate-kek --dry-run`
3. Rotate for real:
   `COFFER_MASTER_KEK_NEW_BASE64=<new> docker compose run --rm api rotate-kek`
4. Set `COFFER_MASTER_KEK_BASE64=<new>` (and `COFFER_MASTER_KEK_ID=v2`) in `.env`, then `docker compose up -d` to restart on the new key.
5. **Re-take backups** under the new key and retire the old ones — a `.cofferbak` carries the wrapped LEKs as they were at dump time, so each backup is bound to the KEK era it was taken under (archive the old KEK with old backups, or discard both).

If rotation aborts ("does not open under the current KEK"), nothing was written — confirm `COFFER_MASTER_KEK_BASE64` is actually the current key.

---

## Secrets

- `.env` is gitignored. Real credentials live there only on the host.
- `.env.example` is committed and contains placeholder values exclusively.
- OAuth tokens (SimpleFIN, a major brokerage via SimpleFIN/MX) live in the database, **envelope-encrypted** (Layer 3 above) — not in env files.
- The master KEK (`COFFER_MASTER_KEK_BASE64`) lives in `.env` on the host — encrypted at rest by Layer 1, never written to backups in plaintext, never committed.
- Never log raw tokens, raw CSV/OFX uploads, or full transaction memos at INFO level.

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
  - `COFFER_WEB_ORIGIN_1` = the subdomain origin (e.g. `https://mcp.example.org`),
    wired to `Api:Fido2:Origins__1`. The OAuth sign-in runs on the subdomain, so
    its origin must be an **allowed origin** — without it, sign-in there fails with
    `origin https://mcp.example.org … not equal to … https://example.org`. Set it in
    `.env` and `docker compose up -d`. (Installs created before this knob existed
    only have `Origins__0`; add the line to the compose or re-run `install.sh` to
    pick up the current one.)
- **Disable the bot protection.** Coffer's real boundary is WebAuthn + RLS +
  off-by-default MCP + OAuth; bot protection is DDoS/noise reduction, not the auth
  layer. Acceptable for a personal, fully passkey-gated install.
