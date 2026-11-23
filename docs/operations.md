# Operations

What an operator does to a real install, in the order you need it: install, reach
it, protect it, recover it, keep it running, diagnose it. Reference material that
only matters for edge deployments is at the bottom, deliberately out of the path of
someone standing an install up for the first time.

> **Where the line is.** The [README](../README.md) covers the path to a working,
> signed-in install: choosing how you'll reach it, the per-platform install procedures,
> first run, and enrolling a passkey. **This document covers everything after that, plus
> anything that goes wrong.** If a subject appears in both, one of them is wrong.
>
> Also elsewhere: development (the Docker dev stack, the Vite fallback) in the
> [README](../README.md#running-it-development); version-to-version moves in
> [upgrading.md](upgrading.md); the standards the code is held to in
> [engineering-standards.md](engineering-standards.md).

---

## Installing

Installing is in the **[README](../README.md#install)** — the three per-platform
procedures, the first-run ceremony and enrolling the first passkey. This document picks
up once you are signed in.

What the installer leaves behind, for orientation: `~/coffer/.env` (ports, hostname,
image, feature toggles), `~/coffer/secrets/` (the three database role passwords as
files), `~/coffer/docker-compose.yml` + `db/init/`, and three Docker volumes — the
database, `coffer_data` where backups live, and `coffer_keys` with the master key.

---

## Reaching it: proxy and origin configuration

**Which shape to run — this machine, tunnelled, or a domain over HTTPS — is a decision,
and it is in the [README](../README.md#reaching-it) with the trade-offs.** What follows
is how to configure the third one correctly once you have chosen it.

**You terminate TLS; the container does not.** It listens on plain HTTP on `:8080` and
expects a reverse proxy in front — Caddy, Traefik, nginx, a Cloudflare Tunnel. Bot
filtering and volumetric traffic are the proxy's job.

### Where the port listens

Compose publishes `API_PORT` (8080) on **every interface of the host, IPv4 and IPv6**,
unless `API_BIND` in `.env` names one. That is the default, and it is not "this machine
only": on a VPS, `http://<ip>:8080` skips the proxy's TLS, its access rules and its
limits. The host firewall does not help. Docker writes its own packet-filter rules for a
published port, and they are evaluated before ufw's and firewalld's. `install.sh` says
this at the end of every run while the port is open.

| What reaches Coffer | `API_BIND` |
|---|---|
| A browser, tunnel or proxy on this machine, outside a container | `127.0.0.1` |
| A proxy in a container on this machine (cloudflared, Caddy) | `127.0.0.1`, and join the proxy to Coffer's compose network (`<install dir>_default`, so `coffer_default` for `~/coffer`). It reaches `http://api:8080` there, not the host port. |
| A proxy on another machine | that proxy's route to this host, such as the LAN address, plus a rule in Docker's `DOCKER-USER` chain admitting only the proxy |

Set it in `.env`, then run `docker compose up -d`. The installer leaves `.env` alone on
upgrade, so the setting survives it. With `API_BIND=127.0.0.1` the forwarded-header
defaults below need nothing changed: a proxy on this machine connects from loopback.

### Proxy headers

The API honours `X-Forwarded-Proto` and `X-Forwarded-Host` so that request-derived
URLs are the external `https://<domain>` the client actually used rather than the
container's internal host. The OAuth issuer and discovery documents, the RFC 9728
resource metadata, the DCR `registration_endpoint`, and the `/login?returnUrl`
redirect all depend on this behind a proxy ([ADR-0063](decisions/0063-mcp-server.md)).
It also honours `X-Forwarded-For`, so the client's address — what the recovery-code and
client-registration rate limits count per — is the client's rather than the proxy's;
otherwise everyone behind the proxy would share one bucket. With no proxy setting those
headers it is a no-op.

**All three are believed only from a trusted proxy:** by default loopback and the
private ranges (`127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `fc00::/7`, `fe80::/10`),
which is where a proxy on the same host (through Docker's bridge), a cloudflared
container or a proxy on the LAN connects from. A client reaching the port from the
internet arrives from its own public address, so what it claims is ignored. If your
proxy connects from a public address, or you want to trust it alone, list it in
`COFFER_TRUSTED_PROXIES` in `.env` (addresses and CIDR ranges, comma separated).

**Security headers come from the app,** on every response: a Content-Security-Policy that
allows only this origin, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Referrer-Policy: same-origin`, and on HTTPS `Strict-Transport-Security` for a year (without
`includeSubDomains`). The proxy does not need to add them. Fonts are bundled, so a page load
asks nothing of a third party.

> **Do not expose `:8080` directly to the internet.** A direct connection gets none of
> the proxy's TLS, access rules or limits, even though it can no longer pose as the
> proxy.

### Outbound requests to addresses users enter

A notification target (a webhook — ntfy, Discord, Slack — or a Healthchecks ping) and a
SimpleFIN setup token are URLs a user enters, and the server connects to them. It connects
only to **public addresses over HTTPS**: loopback, the private ranges, link-local (cloud
metadata included), carrier-grade NAT and Docker's own networks are refused — checked on
the addresses the name resolves to, at the moment of connecting, so a name that later
points inside the network is caught too. Redirects are not followed. A refused or failed
delivery is shown as a category ("Could not connect", "The destination answered HTTP 404"),
never the underlying error, which names hosts and ports; the full error is in the API log.

To reach something on your own network — a self-hosted ntfy or Healthchecks — list it in
`COFFER_OUTBOUND_ALLOWED_NETWORKS` in `.env` (addresses and CIDR ranges, comma separated),
as narrowly as you can. Plain `http://` is then allowed to what is listed, and to nothing
else.

### Where Coffer connects on its own

Beyond the addresses users enter, Coffer makes outbound requests to four named
destinations. Each is off until someone turns it on:

| Destination | What is sent | Turned on by | Decision |
|---|---|---|---|
| Yahoo Finance (`query1.finance.yahoo.com`) | The public quote symbols of the securities a ledger holds and of its benchmark funds; once, those of the securities with fetched prices whose currency Yahoo has not yet confirmed | Each user's Yahoo opt-in on Settings → Quotes | [ADR-0054](decisions/0054-market-data-quote-provider.md), [ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) |
| Google Drive (`googleapis.com`) | Backups, already encrypted | An admin connecting a Drive account | [ADR-0062](decisions/0062-google-drive-backup-sync.md) |
| GitHub API (`api.github.com`) | A request for the install repository's latest release | An admin turning on the automatic check, or pressing Check now | [ADR-0110](decisions/0110-upgrade-from-the-app.md) |
| Frankfurter (`api.frankfurter.dev`, or `COFFER_FX_RATES_URL`) | Currency codes and the days a ledger needs exchange rates for | Each user's Frankfurter opt-in on Settings → Quotes | [ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) |

A bank sync never asks Yahoo or Frankfurter: they are asked only by the daily quote
refresh, its Refresh button, the backfills a user's own action starts, and the
`quote_currency` repair.

To keep exchange-rate requests on your own network, run a Frankfurter container and set
`COFFER_FX_RATES_URL` in `.env` to its address (for example
`http://frankfurter:8080`). Unset, the public service is used. The URL is yours, not a
user's, so the limits in the section above do not apply to it.

### Hostname and allowed origins

| Setting | Purpose |
|---|---|
| `COFFER_RP_ID` | The WebAuthn relying-party id — the hostname credentials bind to. Changing it invalidates every enrolled passkey. |
| `COFFER_WEB_URL` | Where the UI is reached. Becomes the first allowed passkey origin (`Fido2:Origins__0`). |
| `COFFER_MCP_URL` | Where the MCP endpoint is reached, when it differs from the UI. Becomes the second allowed origin. |
| `COFFER_WEB_ORIGIN_2` | A third origin, if you genuinely need one. Requires a matching `Origins__2` entry in the compose file. |

`install.sh` writes the first three for you from the answer you gave it. If you move
Coffer to a different hostname, change `COFFER_RP_ID` **and** re-enrol passkeys — the
recovery-code path exists for exactly that transition.

---

---

## Encryption at rest

A layered model is in effect; full rationale in
[decisions/0014-encryption-at-rest.md](decisions/0014-encryption-at-rest.md).

| Layer | Scope | Operator responsibility |
|---|---|---|
| 1 — Host disk | Whole machine | Required. Use LUKS / BitLocker / FileVault / ZFS native encryption on the host running Docker. The app does not enforce this; the operator must configure it before deploying production data. |
| 2 — Backups | Off-host backup files | Required when backups exist (see above). |
| 3 — Application envelope encryption | Bank-feed OAuth tokens and other high-value secrets | Required for the in-scope secrets only; not used for bulk transaction data. |
| 4 — KEK source | The master key that wraps the per-ledger LEKs (and the sealed backup passphrase) | A base64 key **file** at `Api:MasterKey:Path` (`/app/keys/master.key` on its own `coffer_keys` volume, apart from the backups), per ADR-0092 D1. Point `COFFER_MASTER_KEY_PATH` at `/run/secrets/…` or a projected Kubernetes Secret to keep it off the app's volume. There is no environment variable for the key: `COFFER_MASTER_KEK_BASE64` was removed by ADR-0094, so the file is the only source. A virgin install mints its own on first boot and the setup ceremony shows it once. Can still graduate to a TPM-sealed or hardware-derived KEK without schema change. Rotated from the UI — **System → Encryption → Rotate** (see *Rotating the master KEK*); the `rotate-kek` CLI subcommand was removed by ADR-0092. |

PostgreSQL TDE is **not** in scope — the OSS Postgres distribution doesn't provide it,
and Layers 1 + 3 cover the realistic threats. Whole-DB column encryption is **explicitly
rejected** for bulk data because it breaks indexing and trigram search; see the ADR for
the trade-off.

### Rotating the master KEK

Re-key without re-encrypting data
([decisions/0026-per-ledger-encryption-key.md](decisions/0026-per-ledger-encryption-key.md)
§Rotation, as amended by [ADR-0092](decisions/0092-kek-lifecycle-in-the-ui.md) D4):
envelope encryption means only the *wrappings* change — every `ledgers.wrapped_lek`, the
backup passphrase, and the Drive token are re-wrapped under the new KEK in one
all-or-nothing transaction.

**Rotation lives in the UI: System → Encryption → Rotate.**

1. Type `rotate` to confirm. There is no separate check step: rotation's own first
   action is the dry run — it verifies every wrapped value opens under the *current*
   key, writes nothing, and refuses before touching anything if any of them don't. If it
   refuses, the install is already in a mismatched state (a cross-KEK restore, say); fix
   that first. A check you can forget to run is worse than one you can't.
2. The new key id is assigned by the server, incrementing the current one (`v1` → `v2`).
   It isn't a prompt — it's a label nothing depends on, so choosing it would be a
   decision with no consequence in the middle of an operation that has several.
3. The server generates the key, re-wraps everything, swaps the key file, and
   **restarts** to load it. The panel waits for the server to come back and confirms
   when it's running on the new key — no action needed while it does.
4. **Save the new key.** It's on screen, it's on disk, and it's viewable again later via
   **Show key** (ADR-0092 D2 — deliberately not show-once), but a server you've lost
   can't show you anything.
5. The previous key file is kept alongside the new one (`master.key.<timestamp>.bak`, on
   the keys volume, not beside the backups) so a mistaken rotation is reversible. Delete
   it once you've confirmed the new key works.
6. **Re-take backups** under the new key and retire the old ones — a `.cofferbak`
   carries the wrapped LEKs as they were at dump time, so each backup is bound to the
   KEK era it was taken under (archive the old key with old backups, or discard both).

Nothing to clean up in `.env` afterwards: no environment variable carries the key
(ADR-0094). The key file is written in place, and **System → Encryption → Show key** is
how you read the current one — which is the value to back up after a rotation, since a
`.cofferbak` is bound to the KEK era it was taken under.

> The `rotate-kek` CLI subcommand was removed in ADR-0092, and `restore` in
> ADR-0094. Rotation is routine hygiene rather than disaster recovery, so an
> operator who can't sign in needs recovery codes, not a rotation. The restore
> CLI's justification — "it skips migrations, so it works on a schema too broken
> to serve" — is circular: a restore never reads the old schema
> (it builds a new database and swaps it in), so a broken schema is answered by
> reinstalling.

### If the master key file is lost

A start that finds no key file over a database holding sealed material **refuses**, and
says where the key belongs. Put the saved key back there (the value from **System →
Encryption → Show key**) and start again; nothing is lost.

Only if the key is gone for good, adopt a new one. That clears everything the old key
sealed ([ADR-0092](decisions/0092-kek-lifecycle-in-the-ui.md) D3): the bank-feed tokens,
the stored backup passphrase, the Google Drive connection, a private fork's upgrade repository
token, the notification targets' URLs and tokens and the OAuth server keys MCP sign-in uses.
**It is irreversible:** the old key turning up
afterwards recovers nothing. Ledger data and passkeys are unaffected. Run it in a one-shot
container, which has no restart policy and is removed when it exits:

```bash
sudo docker compose stop api
sudo docker compose run --rm api --adopt-new-kek
sudo docker compose up -d api
```

It lists how many of each kind it abandoned, records the adoption in the admin audit log,
and stores OAuth server keys under the new key: the key files of a release before 0.121.0
if they are still on the data volume (MCP clients stay signed in), otherwise a new pair
(every MCP client connects again). Then reconnect bank feeds and Drive, set the backup
passphrase, re-enter the upgrade token (a private fork only) and re-enter notification targets. Given the flag, a start never serves: with
nothing sealed to wipe it saves its key, says so, and exits. A start with `--adopt-new-kek`
while a key file exists refuses, so the flag cannot be left armed; if that file is the
wrong key (a mis-mounted volume, another install's key) and the right one is lost, move it
aside, keeping it, and adopt then. If the adoption fails it exits non-zero, saves no key,
and is safe to run again once the cause is fixed.

---

## Backups and recovery

Coffer ships a built-in **whole-database** backup
([decisions/0060-whole-db-backup-and-admin-role.md](decisions/0060-whole-db-backup-and-admin-role.md))
— this is the backup procedure. Per-ledger *in-place* recovery is a separate concern
handled by **snapshots**
([decisions/0037-snapshots-and-backups.md](decisions/0037-snapshots-and-backups.md)),
surfaced in each ledger's Settings.

### Mechanics

| Aspect | Detail |
|---|---|
| Engine | `pg_dump --format=custom --no-owner` (as `coffer_service`, so it reads every row + keeps GRANTs) → chunked AES-256-GCM, keyed by Argon2id over a passphrase. |
| Passphrase (app/scheduled) | Set once by an admin in **System → Backups**; sealed under the master KEK and stored in the DB (never plaintext). Drives both on-demand and scheduled backups. Viewable again via **Show** behind a passkey prompt (ADR-0092 D5b) — the server unseals it on every scheduled run anyway, and a passphrase nobody can look up makes every backup silently unrestorable once it is forgotten. |
| Create | Admin panel (on demand), the daily schedule, or the CLI: `coffer-api backup --out <path>` with `COFFER_BACKUP_PASSPHRASE` set. |
| Storage | Encrypted `.cofferbak` artifacts under `data/backups/` (the Docker volume), pruned by a tiered GFS policy: 7 daily, then 8 weekly, then 12 monthly. **These are admin-editable in System → Backups and persisted in the `backup_settings` table (ADR-0074) — they are not startup options.** There are no `Api:Backup:Retention*` keys, so setting such keys in `.env` silently changes nothing. The only backup key left in `ApiOptions` is `Compress`. On-box is a rolling working set; download via the admin panel to keep long-term copies off-host. |
| Restore | Two paths, both in the UI (ADR-0094 removed the `coffer-api restore` CLI). On a **fresh** install (pre-auth): the **bootstrap UI** ([decisions/0061-bootstrap-restore.md](decisions/0061-bootstrap-restore.md)) — the setup screen offers *Restore from a backup*, uploads the `.cofferbak` + passphrase, and applies it on the next boot. On a **running** install, an **admin** can restore in-app — **System → Backups → Restore** (ADR-0071 D3): upload + passphrase behind a typed-confirmation gate; **rolling THIS install back needs no upload at all** — pick one of its own stored backups from the list and it is read off disk ([decisions/0101-a-backup-too-big-to-send.md](decisions/0101-a-backup-too-big-to-send.md) D1); it stages + restarts like the bootstrap path and **signs everyone out**: every session, MCP token and OAuth grant in the backup is revoked and every pending invite cancelled, while users with their admin and disabled flags, passkeys and recovery codes come back as backed up — so access revoked after the backup stays revoked. This is the "migrate from another install" path. **A restore never touches the live database until it has a complete replacement**: the boot builds the backup as a new database, migrates it to this build (a 188-era backup restored onto a 192 build applies 189→192 there), signs everyone out and resets the install's Drive identity, and only then swaps it in — two renames in one transaction. Any failure drops the new database and leaves the live one exactly as it was. The replaced database is kept as `coffer_pre_restore_<utc>` until the next restore ([Undoing a restore](#undoing-a-restore)). The browser shows the outcome the boot recorded — applied, failed with the reason, or unconfirmed — never inferring success from the server answering. Needs the [restore privileges](#restore-privileges). Ledger snapshots are not in a backup, so a restored install starts without any. |

Bring the **same master key** to the recovery host so the restored `wrapped_lek` columns
+ the sealed backup passphrase decrypt as-is; WebAuthn login survives if the RP
id/domain + authenticator are unchanged (see ADR-0060). A cross-install KEK mismatch is
caught pre-flight by the **admin-UI restore** (ADR-0071 D4): backups carry a KEK
fingerprint, and the restore warns before applying if the target install's KEK differs.

Since [ADR-0092](decisions/0092-kek-lifecycle-in-the-ui.md) D4 the restore form takes
the **source install's master key** directly — paste it and its sealed secrets carry
over, and the server checks it against the archive's fingerprint before anything
destructive runs, so a wrong paste is refused up front rather than discovered
afterwards. Leave it empty and the restore still succeeds: reconciliation (ADR-0092 D5) then clears
what won't open and tells you what to re-establish (bank feeds, the backup passphrase,
Google Drive).

Backup encryption is mandatory, per
[decisions/0014-encryption-at-rest.md](decisions/0014-encryption-at-rest.md) Layer 2.
Plaintext backups are not acceptable — the built-in `.cofferbak` is always
passphrase-encrypted.

Restores are not real until tested. Run a periodic drill: stand up a **throwaway**
install (a second host, or the same host with a different install directory, port and
`COFFER_CONTAINER_PREFIX` — the prefix matters, because container names are global to
a Docker engine even though Compose scopes volumes and networks per project),
restore the latest artifact into it through the setup screen, and confirm a sample of
account balances against live. Never restore into the live install as a test — every
restore path replaces everything.

A drill into a throwaway install hits the cross-KEK guard, because that install minted
its own master key: the restore refuses up front (it compares the backup's KEK
fingerprint before decrypting anything, so this costs nothing). Two ways through, and
they test different things:

- **Copy the live install's key into the throwaway's key file first.** Nothing is
  refused, and the sealed secrets — backup passphrase, feed tokens, Drive — restore
  usable. This is what a real migration to new hardware looks like, so it is the drill
  worth running.
- **Leave the source-key field empty.** The data and passkeys restore; reconciliation
  (ADR-0092 D5) then clears every secret sealed under the other key and reports what it abandoned.
  Fine for checking balances, but it does not exercise the path you would actually use
  in a recovery.

### Disaster recovery: restore onto a new machine

Validated end-to-end (backup on one install → restore into a fresh one → the
app boots on the restored DB). Compose-based; ordering matters — **restore
before the API server ever starts** on the fresh DB, or the server migrates the
empty DB and collides with the restore.

**Fast path — `install.sh` (recommended; it handles Docker, the config files and the
start order for you).**

Your DR kit needs **two** things: the `.cofferbak` artifact and its passphrase. That is
the whole requirement — the archive is encrypted under the passphrase, and your ledgers,
accounts, transactions and passkeys all come back with it.

The source install's **master key** is a *third, optional* item, and it is worth keeping
because of what it saves rather than what it rescues: supply it and the secrets sealed
under it come across intact — the SimpleFIN feed tokens, the stored backup passphrase, the
Google Drive connection, a private fork's upgrade repository token, the notification targets' URLs
and tokens, and the OAuth server keys MCP sign-in uses. Without it the restore still completes and reconciliation tells you which of
those to re-establish. No data depends on it.

**No credentials are needed** — the repo and its ghcr image are public, so the installer
fetches its config anonymously and pulls the image without a login. (Recovering an install
built from a *private fork* is the one exception: it needs a fine-grained token with
*Contents: read* and a classic one with `read:packages` only — see
[upgrading.md](upgrading.md#upgrading-from-a-private-fork).) On
the fresh box:

1. **Run the installer** (README's one-liner). Answer its prompts: install Docker
   (yes, if asked) and how you'll reach Coffer (**localhost**, or your **domain** —
   the RP id, which must match the backup's for passkeys to still validate). It writes
   `.env`, pulls the image, and starts an empty install. It does not ask about
   restoring and takes no key: the install mints a throwaway one, which the restore
   replaces (ADR-0094).
2. **Restore.** Open the setup link the installer printed → **Restore from a backup** → upload your
   `.cofferbak` + passphrase, and paste the source install's **master key** in the
   source-key field. A backup larger than `COFFER_BACKUP_PART_SIZE_MB` (49 by
   default) is stored off-host as several `….cofferbak.001-of-003` files
   (ADR-0101) — **select all of them together**; the form orders them and sends
   them one at a time, so nothing in front of Coffer sees a body big enough to
   refuse. It tells you by name if the set is short. The same applies on the
   **setup screen** of a fresh install, which is where a real recovery starts. It is validated against the archive's KEK fingerprint *before*
   anything destructive runs, then adopted, so the sealed secrets come across too. Leave
   it empty and the restore still succeeds — reconciliation (ADR-0092 D5) clears what won't open
   and tells you what to re-establish.
3. **Sign in.** A passkey works if the RP id is unchanged; otherwise **Use a
   recovery code**, then add a fresh passkey. Confirm ledgers + balances.

Not using `install.sh` — k8s, your own orchestration, a host you'd rather configure by
hand? The compose-level steps below are the same sequence, done manually. The upload
ceiling is not a reason to prefer them: Coffer's own limit is 4 GiB (ADR-0094),
and a *proxy* that caps the request body is handled by sending the artifact in parts
(ADR-0101) rather than by reconfiguring the proxy — which matters because Cloudflare's
100 MB cap on Free and Pro is not a setting at all. Set
`COFFER_BACKUP_PART_SIZE_MB` in `.env` if something in front of Coffer is stricter
than the 49 MB default.

A restore upload must leave `COFFER_RESTORE_FREE_SPACE_RESERVE_MB` (1024 by default)
free on the data volume, which also holds the stored backups; one
that would not is refused before anything is written. Backups made from 0.121.0 on are
format v3, which an earlier Coffer cannot read — restore them with this version or later
(every earlier backup still restores here).

**On the source install — export an artifact:**

1. Create a backup (admin **System → Backups → Create**, or the CLI:
   `docker compose run --rm -e COFFER_BACKUP_PASSPHRASE='…' api backup --out
   /app/data/dr.cofferbak`).
2. Get the `.cofferbak` off-box (the panel's **Download**, or
   `docker compose cp api:/app/data/dr.cofferbak ./`). Keep the artifact and its
   passphrase safe and **separate** — those two are what a restore requires. Keep the
   **master key** too if you want the sealed secrets to survive the move: read the
   current one from **System → Encryption → Show key**, since a rotation changes it and
   each archive is bound to the KEK era it was taken under.

**On the new machine — manual restore (compose-level fallback):**

1. Install Docker. You need `docker-compose.yml`, `db/init/`, and a `.env` with the DB
   passwords. The master key is **not** part of `.env` (ADR-0094) — the restore form
   takes it. Fetch the two
   files straight from the public repo (`raw.githubusercontent.com`). Recovering a
   *private fork* is the exception: fetch them with a fine-grained token (*Contents:
   read*) via the GitHub contents API, since anonymous raw 404s there
   ([upgrading.md](upgrading.md#upgrading-from-a-private-fork) has the exact form), and
   log in to ghcr with a classic token that has `read:packages` and nothing else. The API
   image comes one of two ways:
   - **Pull the prebuilt image (preferred — no build toolchain).** The canonical
     package is public, so `docker compose pull api` needs no login. (A fork's
     package is private by default: `docker login ghcr.io -u <user> --password-stdin`
     with a classic token that has `read:packages` only — keep it in your DR kit.) Pin a version with
     `COFFER_IMAGE_TAG=<tag>` in `.env` if you don't want `latest`, and to pin the image
     itself, `COFFER_IMAGE_DIGEST=` the `image-digest.txt` on that version's GitHub
     Release; `install.sh` writes both for you.
     **`COFFER_IMAGE` must be set** — compose has no default for it, deliberately.
     `.github/workflows/release.yml` publishes to
     `ghcr.io/<the building repo's owner>/coffer`, so whoever built the image owns
     the package and there is no one value that suits every fork; a hardcoded
     fallback would let an install quietly pull from a registry that isn't its own. `install.sh` derives it from the repo it fetched your config from and
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
3. Bring up the API on the empty DB — `docker compose up -d api` — then restore through
   the **bootstrap UI**
   ([decisions/0061-bootstrap-restore.md](decisions/0061-bootstrap-restore.md)): open the
   setup URL it logs, pick *Restore from a backup*, upload the `.cofferbak` + passphrase,
   and paste the source install's master key. The server stages it and restarts to apply.
   Expect benign `pg_trgm` / `pgcrypto` comment-ownership warnings in the log
   (extensions are install-managed).

   There is no CLI alternative (ADR-0094 removed it). If the artifact is too large for the
   upload to survive your reverse proxy, raise the proxy's body limit —
   `client_max_body_size` on nginx — or reach the install on `http://localhost` with
   nothing in front of it, which is also the shortest DR path.
4. After the restart it serves on `:8080` with a fully-migrated schema from the dump.
5. **Sign in — WebAuthn has hard requirements:**
   - The requirements are the same as any other install — secure context, no IP
     addresses as RP ids, and on Linux no OS authenticator to fall back on. See
     [Signing in](../README.md#signing-in) rather than a second copy of them here.
   - If the RP id changed from the source install, the old credential won't
     validate (a passkey is bound to the RP id it was created under). Click
     **"Use a recovery code"** on the sign-in page, enter your username + one
     of the codes from the backed-up account, and you land on
     **Account → Security** — add a fresh passkey there (and remove the dead
     one). This recovery path
     ([decisions/0013-webauthn-passkey-auth.md](decisions/0013-webauthn-passkey-auth.md))
     is why a
     restore onto a new domain isn't a lock-out.
   Then confirm your ledgers + balances; the data is intact regardless of the
   login mechanics.

### Troubleshooting restore

| Symptom | Cause / fix |
|---|---|
| `IOException: Pipe is broken` / pg_restore exits immediately | pg_restore couldn't connect — usually the roles weren't created (init script not run). Check `docker compose logs postgres` for `Permission denied` on `00-init-roles.sh` (fix perms, `down -v`, re-up) or `role "coffer_service" does not exist`, and `\du`. |
| Restore errors beyond the two extension warnings | Wrong passphrase or a truncated `.cofferbak` (re-copy in binary mode); verify the file size matches the source. |
| App shows empty/garbled data after restore | The master key doesn't match the source's — it must be byte-identical. Supply it in the restore form's source-key field, where it is checked against the archive's fingerprint first. |
| "WebAuthn is not supported in this browser" | Insecure context — use `http://localhost` on the box or serve HTTPS (see step 5). |
| Scanning the QR with a phone fails when the phone tries to save the passkey | Expected on a `localhost` install: the cross-device flow needs a real HTTPS hostname as the RP id. On Linux there is no platform authenticator to fall back on — use a USB security key, or tunnel to `localhost` from a laptop and use its Hello / Touch ID, or move to the domain install (see step 5). |

### Off-host: Google Drive sync

An optional, off-by-default destination that copies each backup to a folder in **your
own** Google Drive
([decisions/0062-google-drive-backup-sync.md](decisions/0062-google-drive-backup-sync.md)).
Only the **already-encrypted** `.cofferbak` leaves the host — Google holds ciphertext;
the passphrase and the master key never go to Google. Connecting uses **your
own** Google Cloud OAuth client (no Coffer-run broker), so there's a one-time project
setup:

**One-time Google Cloud setup (the operator):**

1. At [console.cloud.google.com](https://console.cloud.google.com), create (or pick) a project.
2. **APIs & Services → Library → Google Drive API → Enable.**
3. **APIs & Services → OAuth consent screen:** user type **External**, fill the app name
   + your email, and add your Google account under **Test users** (a test-mode app is
   fine — it never needs Google verification because only you use it). No scopes need
   adding here.
4. **APIs & Services → Credentials → Create credentials → OAuth client ID**, application
   type **Web application** (you can also **reuse an existing Web client** — see below).
   Under **Authorized redirect URIs**, add your Coffer origin + the callback path, e.g.
   `https://coffer.example.org/api/admin/drive-sync/oauth/callback` (use the exact
   public origin you reach Coffer at — the same host as its HTTPS / WebAuthn domain).
   Copy the **Client ID** and **Client secret**.

> **Reusing a client you already have:** authorized redirect URIs are an additive list,
> so you can add Coffer's callback URI to an existing **Web application** client without
> affecting the apps that already use it — no new secret to manage. (Desktop / "TVs and
> Limited Input devices" client types can't redirect to an HTTPS origin, so they won't
> work here; use a Web client.)

> **Enable the Google Drive API** in the same project (APIs & Services → Library →
> **Google Drive API** → Enable). Without it the connect completes the OAuth dance but
> the first Drive call fails with `accessNotConfigured`. (Reusing a client from another
> product — e.g. Home Assistant — is fine, but that project may not have the Drive API
> turned on.)

**Connect (admin, in Coffer):** **System → Backups → Google Drive sync → Connect Google
Drive**. Paste the client ID + secret and click **Continue to Google** — you're
redirected to Google's consent screen, and on approval Google returns you to Coffer,
which seals the refresh token under the master KEK, creates a folder named **`Coffer
Backups [<install id>]`**, and shows the connected account + install id. **Sync now**
pushes the latest local artifact; **Disconnect** forgets the token (backups already in
Drive are left untouched).

> **Per-install folders:** each Coffer install gets a stable install id and its own
> `Coffer Backups [id]` folder, so multiple installs sharing one Google account/OAuth
> client don't commingle (and a later retention sweep won't prune across installs). The
> install id is shown on the card.

> The redirect URI registered in Google must **exactly** match `‹your Coffer
> origin›/api/admin/drive-sync/oauth/callback`. A mismatch shows a Google
> `redirect_uri_mismatch` error before consent. Coffer derives the origin from its first
> configured WebAuthn origin (`Api:Fido2:Origins`).

Notes:
- Scope is `drive.file` — Coffer can only see/manage files it created, never the rest of your Drive.
- The sealed token is re-wrapped automatically by a KEK rotation (see Encryption at
  rest), so rotating doesn't break uploads.
- **Automatic upload:** turn on **"Automatically upload each new backup to Google
  Drive"** and every backup (the daily scheduled run + manual creates) is uploaded as
  `{id}.cofferbak` into the per-install folder. A failure never fails the backup — it's
  recorded as the card's last-upload error, and a **"Not uploaded recently"** badge
  appears if auto-upload hasn't succeeded recently.
- **Drive retention** is independent of local retention (set the daily/weekly/monthly
  tiers on the card). Pruning is delete-only-once-replaced and never removes the newest
  artifact.
- **Pin a backup** ("never delete") from the backups list to exclude it from both local
  and Drive retention. **Upload all backups now** uploads every local backup not already
  on Drive (catch-up / first-time backfill).
- Backups are compressed with `pg_dump --compress=zstd` by default
  (`Api:Backup:Compress`; ~10% smaller than zlib). Set e.g. `zstd:19` for a smaller
  artifact at higher CPU; PG16 restore reads any of these.

---

---

## Upgrades

`bash ~/coffer/upgrade.sh` (the latest release) or `bash ~/coffer/upgrade.sh <version>`,
as your normal user. It fetches the `install.sh` of the release you are moving to and
runs it as an in-place upgrade: that keeps `~/coffer/.env` and your data volumes, fetches
that release's compose file, pulls the image by its recorded digest and restarts. Each
release's `install.sh` carries that release's upgrade steps, so an old copy is never
reused, and each run replaces `upgrade.sh` with the current one. An install from before
0.123.0 has no `upgrade.sh` yet; [upgrading.md](upgrading.md#the-upgrade) has the one
by-hand upgrade that puts it there. Schema migrations apply themselves on the next boot
(DbUp), so there is no separate migration step. The API answers nothing, `/readyz`
included, until they have run, and the scripts wait for `/readyz` for up to
`COFFER_READY_TIMEOUT_SECONDS`, default 600 seconds. A timeout ends only the wait, not the
migration; [upgrading.md](upgrading.md#the-upgrade) has how to follow it.

Two things it does *not* decide for you, both covered in
**[upgrading.md](upgrading.md)**: the order of operations for a live install, and the
handful of changes that need an operator action rather than a restart. Read it before
a version jump — a rollback is a restore, not a downgrade, so the pre-upgrade backup
is the safety net.

### Upgrading from the app

An admin can start the same upgrade from **System → Upgrade** ([ADR-0110](decisions/0110-upgrade-from-the-app.md)).
The tab does not touch Docker. It leaves a request in a volume of its own, `coffer_mailbox`,
and **a small updater on the host**, `~/coffer/app-upgrades.sh`, run every minute from the
install user's crontab, runs `upgrade.sh` for it.

**Where it works.** Linux with a cron daemon, where the install user can run `docker`
without `sudo`. Anywhere else (macOS, WSL2, a host whose docker needs `sudo`, no cron) the
tab says why and shows the manual command. `install.sh` records the reason in
`COFFER_UPGRADE_HOST_NOTE` in `.env`, and puts the cron line in place on every install and
upgrade that can have it. An install from before this feature gets the updater with its next
upgrade by hand.

**What an admin does.** The tab shows the installed and the latest release. Press **Check
now** to look, or switch on the automatic check under Settings (off by default; it sends a
request to the GitHub API every few hours while a page is open, and nothing otherwise).
**Upgrade** needs the typed word `upgrade` and a passkey tap, then takes a fresh backup, reads
it back to check it, and hands the request to the host. The backup is labelled "before upgrade
X to Y" in the backup list. The newest two are kept; one that belonged to an attempt that
changed nothing goes back to normal retention at once. A checked backup can be read back,
which does not prove it restores.

**Knowing there is one.** A known release newer than the install lights the same dot a problem does,
on the **System settings** gear in the sidebar and on the **Upgrade** tab (an outline, because it is
a warning; a failed backup or a disabled job lights the gear too, and a critical problem fills it).
It is the same whether the release was found by Check now or by the automatic check, and it
survives a restart, because the last result is stored. It clears when the install reaches that
release, with no check needed; nothing here is dismissible, so an install that stays behind on
purpose keeps the dot. Only admins see it. With the automatic check on, any page an admin has open
can check, at most every six hours; with it off nothing is sent except by Check now.

**Telling something outside the app.** The release is also a notification, in System →
Notifications, under the topic **Upgrade**. It is not sent anywhere unless a destination asks: a
destination set to "everything" hears every topic *except* this one. Tick **Also send upgrade
notices** when adding a webhook or push target, and that target hears it. There is no edit for an
existing destination, so to add it to one, remove it and add it again. A dead-man's switch never
receives it.

**What the host does.** It runs the upgrade only if the version is the latest release it
resolved itself, newer than what is installed, asked for within the last 15 minutes, and not
asked for before. The API cannot make it do anything else: it names a version and nothing more.
While the upgrade runs the app is unavailable. The tab reconnects by itself and shows the result.

**Commands, on the host, as the install user:**

| Command | What it does |
|---|---|
| `bash ~/coffer/app-upgrades.sh status` | The updater's state, the last run and its exit status, and the last ticks. The authoritative word on what happened. |
| `bash ~/coffer/app-upgrades.sh probe` | A read-only self-check: docker, the cron line, the mailbox volume. |
| `bash ~/coffer/app-upgrades.sh disable` / `enable` | Stop or resume acting on requests. Disabled, the tick keeps reporting `disabled`, so the tab can tell it from broken. `enable` also writes a missing cron line. |
| `bash ~/coffer/app-upgrades.sh ack` | After a run failed **after it began changing the install** (exit 12 or 14), allow new upgrades again once you have looked. |
| `bash ~/coffer/app-upgrades.sh set-token` | For a private fork, store the repository token and the registry token in `~/coffer/upgrades/secrets/` (0600). Prompts without echo. |

State is in `~/coffer/upgrades/` (0700): `tick.log` (capped), one `run-<id>.log` per run, the
list of consumed request ids, and the flags. If the tab says the updater is not responding,
check `crontab -l` for the line ending `# coffer-upgrades <install dir>`, and run `probe`.

**When a run fails.** The exit status says where: 10 the preflight failed, 11 it failed before
the first change, 13 the release predates this feature, so the host did not run it (all three:
nothing was changed, and the tab offers the form again); 12 it failed after the first change; 14 the
readiness wait timed out, which ends the wait and not the migration (Following it:
`docker compose logs -f api`). After 12 or 14 nothing is retried or rolled back, and the host
refuses new requests until `ack`. While it refuses, its heartbeat says `blocked`: the tab shows
"Needs attention" with the commands, keeps its upgrade button off, and drops the failure once
`ack` has been run. A request the host cannot act on (it is blocked, disabled or busy, or it
cannot read the release with its own token) is refused as `expired` after 15 minutes. A host whose
`COFFER_IMAGE_TAG` is not a version (an install tracking `latest`) says `unpinned` in its heartbeat,
so the tab tells you to upgrade by hand, and refuses any request as `unknowninst`. A run that ends with no exit status at all, because the host
restarted or the process was killed, is recorded as 12 by the next tick, with a line saying so at
the end of its `run-<id>.log`: nothing says how far it got. Rollback is the restore of the labelled backup on the release
it came from, and it loses changes made after it ([upgrading.md](upgrading.md#rollback)).

**A private fork** needs two tokens on the host (`set-token`), and the repository token in
the tab too, under Settings, for the release check. The tab's copy is sealed under the master
key like the Drive token: backups carry it, rotation re-wraps it, and a restore onto another
install discards it, after which the tab says it is not stored.

**The mailbox can be emptied by hand.** The API can fill the mailbox with names the host
ignores, and a flood of them can hide a real request until it is cleared. There is no backoff
and no clear button (accepted in ADR-0110). To empty it, as the install user:

```bash
V=$(docker inspect coffer-api --format '{{range .Mounts}}{{if eq .Destination "/app/mailbox"}}{{.Name}}{{end}}{{end}}')
docker run --rm --user 1654:1654 --cap-drop ALL --network none --pull never --read-only \
  --mount "type=volume,source=$V,target=/mbx" --entrypoint find \
  "$(docker inspect coffer-api --format '{{.Image}}')" /mbx -mindepth 1 -maxdepth 1 -delete
```

(With `COFFER_CONTAINER_PREFIX` set the container is `<prefix>-api`.) It mounts the mailbox
volume alone, so it never sees the data or the keys.

---

## Day-2 operation

### What runs on its own

Two schedulers, deliberately separate because their scope differs.

| Scope | Table | Jobs |
|---|---|---|
| Per ledger | `scheduled_jobs` | `quote-refresh` (security prices and, under their opt-ins, benchmark growth and exchange rates, [ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D11), `snapshot` (per-ledger point-in-time snapshot), `feed-sync` (migration 215), **`reminder-auto-post`** (migration 219) |
| Deployment-wide | `global_scheduled_jobs` | `backup` (whole-DB encrypted backup, [ADR-0060](decisions/0060-whole-db-backup-and-admin-role.md)) |

> **`reminder-auto-post` writes financial transactions.** It is the only scheduled
> job that does, which is why it is called out here: a Day-2 "what runs on its own"
> table that omits the one job that can post money is the wrong one to be incomplete.
>
> Nobody has to switch it on. A ledger's job is created, enabled at 05:00 local, the
> first time one of its reminders asks to be auto-posted: a reminder saved with
> Auto-post ticked, or a Moneydance import that brings one set to auto-commit. An
> existing row is never changed by either. A ledger an earlier version left without the
> job, while one of its reminders asks for it, is reported by the consistency check
> (`reminder_auto_post`), whose Repair creates it the same way.
>
> It sets aside a reminder it cannot post rather than failing the run: an investment
> reminder, one whose template has no lines, a bank reminder with a line on an
> inactive account, a managed loan payment that would use an account in another
> currency than the loan's ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md)
> D13), and a reminder with a figure that has more decimal places than its currency
> uses, on a template line or in its loan's escrow or fixed payment (ADR-0114 D15).
> Each run publishes one notice, to the configured webhooks as well: either that
> everything due was posted, or how many occurrences wait on the calendar. That notice
> names the reminders with no lines, those on an inactive account, the loan payments
> across currencies, and those with a figure that does not fit, up to three of each (by
> payee, else "Untitled reminder"), with what to do: give the reminder its lines or
> delete it; reactivate the account or edit the reminder; use accounts in the loan's
> currency or enter each payment as a transfer with both amounts; or edit the reminder,
> or its loan's terms, so every figure fits its currency. Its structured detail carries
> counts only, never dates.

Both run **daily at a local time-of-day you choose**, stored as hour + minute plus an
IANA timezone id (the tz database's `Area/Location` form) rather than a fixed offset, so the run time
stays put across DST. A blank or unrecognised timezone falls back to the server's.
Nothing runs on an interval or a cron string.

The split is enforced rather than conventional: a per-ledger schedule endpoint will not
accept `backup`, and the global one will not accept `quote-refresh`.

### Audit retention

One more job runs outside both schedulers: `AuditRetentionService`, a hosted service with
no schedule row. It runs a minute after the API starts and then once a day, and it cannot
be paused from the UI. Each pass deletes:

| Setting | Default | What goes |
|---|---|---|
| `Api:AuditRetentionDays` | 180 | **Settings → Activity** entries (`ledger_operations`) and **System → MCP → AI write activity** rows (`mcp_tool_invocations`) older than this many days. |
| `Api:EventRetentionDays` | 365 | Notification history (`system_events`, `ledger_events`) older than this many days. |

A value of 0 or less turns that half off and keeps its rows indefinitely. The audit of
deployment key actions (`admin_audit_events`) is never pruned ([ADR-0092](decisions/0092-kek-lifecycle-in-the-ui.md)).

When an import's or a sync's Activity entry is pruned, the import can no longer be undone
as a unit. Its transactions stay, and still show where they came from; they lose only the
stamp that tied them to the entry. A sync's records of holds that cleared at a different
amount go with its entry. A snapshot holding such rows restores them the same way
([ADR-0037](decisions/0037-snapshots-and-backups.md), restore invariant 3).

A Demo's examples entry (Demo examples) is pruned the same way. Once it has gone, the hub
no longer names any examples that Demo lacks, and the Demo lists as a plain ledger; nothing
else about it changes ([ADR-0115](decisions/0115-the-demo-is-generated-imported-and-seeded-from-the-hub.md)).

To change either window on the docker install, add it to the api service's `environment:`
in a `docker-compose.override.yml`:

```yaml
services:
  api:
    environment:
      COFFER_API__AuditRetentionDays: "365"
```

Putting it in `.env` changes nothing: the compose file passes the api container only the
variables it lists. On a non-docker host, set `COFFER_API__AuditRetentionDays` /
`COFFER_API__EventRetentionDays` in the API's environment.

### Watching it

| Surface | What it tells you |
|---|---|
| `GET /healthz` | **Liveness** — the process is up. No database I/O, so it stays green while Postgres is down. This is the one an orchestrator should restart on. |
| `GET /readyz` | **Readiness** — up *and* able to reach Postgres (`SELECT 1`). Returns 503 when it cannot. Gate traffic on this, not on liveness. |
| `GET /api/meta/version` | The running build's version ([ADR-0044](decisions/0044-version-surfacing.md)). Authenticated, unlike the two probes. |
| **Settings → Activity** | Per-ledger operation log (`ledger_operations`): feed syncs, file imports, the Moneydance import, a Demo's examples ("Demo examples", with how many of its steps were added), quote refreshes, snapshot restores — with status, counters, timing and who triggered it. |
| **System → Backups** | Backup history, the retention ladder, and on-demand create/restore. |
| **System → MCP → Connected apps** | Every app registered to reach the MCP server, with the people signed in through it, and personal tokens as their own group ([ADR-0113](decisions/0113-a-connection-sits-under-its-app.md)). Each app shows one status (Active, No use recorded, No live sign-in), where it runs (on a person's device, or a hosted service and its host) and, for apps registered from this release on, when it registered. Each sign-in shows whose it is, when it was connected, **when it was last used** and when it expires; a sign-in expiring within a week is flagged. An OAuth sign-in from before use was recorded reads "no use recorded" until it is next used. Rename or revoke a sign-in to end only that person's access; revoking someone else's is recorded in the admin audit ([ADR-0107](decisions/0107-connections-are-what-people-hold.md)). **Disconnect app…** ends every sign-in through the app, and the confirmation says how many and whose. **Remove never-used apps** removes every app nobody holds a live sign-in through, naming each first. |
| **System → MCP → AI write activity** | Every AI-initiated write, kept 180 days by default ([Audit retention](#audit-retention)). Empty unless you turned writes on. |

Both probes are anonymous and sit outside the auth pipeline on purpose: a probe should
fail because the API is unhealthy, not because something in the auth layer is.

### Logs

Read them with `docker compose logs api` or `docker compose logs postgres`. Docker rotates
each container's log at `COFFER_LOG_MAX_SIZE` (10m) and keeps `COFFER_LOG_MAX_FILE` (5)
files, so each container keeps at most 50 MB. Docker's own default keeps everything for as
long as the container exists. The compose file names the `json-file` driver, because these
options belong to it. If your daemon ships logs through another driver, override `logging:`
for both services in a `docker-compose.override.yml`.

The API's log is written so that it holds no credential (FU-083):

- The access line (`GET /path -> 200 in 12ms`) replaces the token in an invite, setup or
  consent link with `{redacted}`. Identifiers in other paths are kept.
- Outbound requests are not logged below Warning. A Slack or Discord webhook, a
  Healthchecks ping URL and a SimpleFIN access URL all carry their secret in the path,
  and HttpClient's Information lines name the full URL. If you raise
  `Logging__LogLevel__System.Net.Http.HttpClient` to diagnose a delivery, those URLs are
  written. Put it back afterwards.
- A failed restore logs the same summary the browser is shown, plus the exception type
  and stack. pg_restore's own text can quote rows of the backup, and is left out.

Postgres logs its errors with the statement and, for a constraint, the key that failed.
Rotation bounds how long that stays.

### Routine hygiene

- **Keep backups off-box.** On-box retention is a rolling working set (7 daily, 8
  weekly, 12 monthly). Download from the admin panel, or configure
  [Drive sync](#off-host-google-drive-sync).
- **Drill a restore periodically.** An untested backup is a hope, and the drill has a
  wrinkle worth knowing about — see [Backups & recovery](#backups-and-recovery).
- **Keep the master key backed up and current.** The setup ceremony shows it once; a
  rotation replaces it. Read the live one from **System → Encryption → Show key** — it
  lives in a file, never in `.env` (ADR-0094), so there is no second copy to go stale.
  It lives on its own volume, `coffer_keys`, so a host-level copy of `coffer_data`
  holds the backups but not the key: keep the key, or a copy of `coffer_keys`,
  somewhere the backups are not.
- **Keep the image current.** `bash ~/coffer/upgrade.sh`; see Upgrades above.

### Passkey taps, and signing people out

A session cookie alone can't do the things that would hand someone else the keys
([ADR-0105](decisions/0105-a-cookie-alone-is-not-enough.md)). Each of these asks for a
passkey tap, made for that one request:

| Who | Action |
|---|---|
| Anyone | Add or remove a passkey, regenerate recovery codes, create an MCP token |
| Admin | Make someone an admin (directly or by an admin invite), restore a backup, set or change the backup passphrase, delete a backup, turn on MCP writes, start an upgrade from the app; reveal the master key or the passphrase (as before) |

A tap is refused in two cases, and the message says which:

- **The passkey was added in this session.** A tap must come from a passkey you had
  before you signed in; otherwise a stolen cookie could add its own and approve
  anything. Sign out and sign in with that passkey, then retry.
- **The authenticator didn't verify you** (no PIN, fingerprint or face). Step-up
  requires user verification; plain sign-in doesn't. A security key with no PIN set
  needs one before it can approve these actions.

**Lost every passkey?** Sign in with a recovery code. That session may add one passkey
without a tap, within 15 minutes of signing in.

**Ending sessions.**

- *Account → Security → Sessions* lists your sessions, with the one you're using marked.
  **Sign out** ends one; **Sign out other sessions** ends all but yours. Removing a
  passkey or regenerating recovery codes also ends every other session.
- *Account → Security → Connected apps* lists the MCP tokens you made and the apps you
  signed in to, with when each was last used. **Rename** names one, so two laptops running
  the same app can be told apart. An app's connection lasts 90 days from when you approved
  it, then the app asks again. **Revoke** ends a token or an app at once.
- *System → Users → Sign out* (admin) ends every session, MCP token and connected app
  of that user, without disabling them.
- *Disable* does the same and also blocks sign-in. It takes effect immediately: a
  disabled user's existing sessions and tokens stop working at once, not when they
  expire. An invite stops working when its issuer is disabled, or loses the authority
  it was sent with: admin for an admin or deployment invite, ownership of the ledger
  for a ledger invite. Only an owner of a ledger can invite someone into it, admins
  included.

**A write refused with "This request came from another site".** A state-changing request
that carries the session cookie must come from Coffer's own origin. Browsers mark this
themselves (`Sec-Fetch-Site`); an older browser that sends only `Origin` must send this
deployment's own origin or one listed in `COFFER_API__Fido2__Origins__N`. Open Coffer
directly rather than through another site, and check the origins if it happens there.

---

## Diagnostics and troubleshooting

### Attaching psql

The database is not published to the host — the API reaches it over the compose network
(see the note in `docker-compose.yml`). Exec into it instead:

```bash
cd ~/coffer && docker compose exec postgres   sh -c 'PGPASSWORD=$(cat /run/secrets/postgres_password) psql -U coffer -d coffer'
```

The password is read from the docker secret **inside** the container, so it never
reaches your shell history. On an install created before the scram hardening above,
initdb left `trust` on the local socket and a bare `psql -U coffer -d coffer` still
works — `scripts/harden-pg-hba.sh` is the one-time fix, and `install.sh` now runs it
for you on the upgrade path. Use the `coffer` superuser for
diagnostics: `coffer_app` is `NOBYPASSRLS`, so outside a request `app.user_id` is
unset and every scoped table returns **zero rows with no error**.

A GUI client can't reach into a container and needs a published port; the repo's
`docker-compose.dev.yml` overlay is the supported way to add one, bound to 127.0.0.1.


### The setup link expired, or was consumed before anyone signed in

`install.sh` prints the one-shot `/setup/<token>` link itself on a fresh install: the API
logs that line once and never again, so the installer asks the running container for it
rather than leaving you to find it. If it printed a bare URL instead, the install either
already has a user or never answered — then the line is in the log:

```bash
docker compose logs api | grep -i bootstrap
```

On an install whose token has expired or been consumed while there is still no user,
mint a fresh one:

```bash
docker compose exec api dotnet coffer-api.dll bootstrap-token
```

Once the first user exists, setup is complete for good: the command refuses, and
deleting every passkey does not reopen setup
([ADR-0105](decisions/0105-a-cookie-alone-is-not-enough.md) D7). A user who has lost
every passkey signs in with a recovery code and adds a new one.

> **Invoking the operator CLI in the container.** Every `coffer-api <subcommand>`
> below (`bootstrap-token`, `backup`, `reset-oauth-keys`) is reached as
> `docker compose exec api dotnet coffer-api.dll <subcommand>`. The image's
> ENTRYPOINT is `["dotnet","coffer-api.dll"]` and it ships no apphost binary, so
> `docker compose exec api coffer-api …` exits 127 with "executable file not
> found in $PATH".

### Verify stored balances against the canonical recompute — then repair, separately

When a register row is suspected to display a stale running balance, ASK first. The
stored running balances are the `balances` projection, one of ten checked projections
(`balances`, `holdings`, `realized_gains`, `posting_counts`, `trade_prices`,
`stored_notes`, `holdings_currency`, `quote_currency`, `category_amounts`,
`reminder_auto_post`). The check covers all of them and changes no figure.
It also lists, as an advisory not counted in its verdict and with no repair, each stored figure with
more places than its currency allows, each category line out of step on a posting the
`category_amounts` repair leaves, and each stored account or ledger currency code that would now be
refused (see *Figures that do not fit their currency* below). A projection's cure is its one repair:

```
POST /api/ledgers/{ledgerId}/balances/consistency                          # the check
POST /api/ledgers/{ledgerId}/balances/consistency/{projection}/repair      # the deliberate fix
```

> **The check does not repair.** An unhealthy projection means drift exists, not that it
> has been healed: an operator who reads drift, believes it is repaired and never calls
> the repair leaves the ledger stale. Migration 206 split calculation from persistence so
> that asking the question never rewrites the answer — a diagnostic that writes silently
> rewrites every row it finds drifted.

For `balances`, the check walks every account with the pure walk and compares each row it
produces with its stored `txn_header_account_balances` row. A stored row that disagrees is
drift, and so is a walked row with no stored row at all: the recompute stores every row the
walk produces, so a missing one is a recompute that did not run. So is a stored row the walk
does not produce: a running balance on a header the walk skips (a reminder's template, a
hidden or merged-away transaction, one with no line on the account) or on an account with
no line at all. No write path leaves one, and every reader that follows the walk ignores
it; the set is the stored rows less the walked ones, so the check keeps no list of its own
of the headers the walk skips. The stored rows and the walks read one snapshot, so a
transaction saved while the check runs is not reported as missing. Each drifted row is a
mismatch, listed up to the report's cap (below): `scope` names the account and the day,
`stored` is the stored `balance_after`, `expected` the walk's value for the same row, and
`diff` is `expected` less `stored`, with the row's `accountId` and `headerId`. `reason` is
`value` when a stored row holds another balance, `missing` when no row is stored, and
`extra` when a row is stored that the walk does not produce. A missing row's `stored` reads
as 0, so its `diff` is the whole walked balance, and its `field` says it is not stored; an
extra row's `expected` reads as 0, its `field` says it is not derived, and the Maintenance
panel shows it as "stored *x*, none expected", with no link, since no register can show the
row it sits on.

`POST …/balances/consistency/balances/repair` rebuilds every stored running balance of the
ledger from the legs, recomputing each account with a line or a stored row, and reports
what it rewrote, missing rows included, and the extra rows it removed, listed up to the
same cap. Since migration 277 every account recompute, a save's as much as the repair's,
clears every stored row in its window, on any header, before it writes the walk's, so what
remains there is exactly what the walk writes; and it takes its seed, the last stored
running balance before its window, only from a row the walk writes, so an extra row never
seeds a save. The balances
repair is not isolated: a transaction saved while it runs can appear in its report as
drift, a changed value or a missing row, and a repair run again afterwards reports nothing
for it. Every repair takes write access deliberately — under a read policy a viewer's
rewrite would be silently no-op'd by RLS and still report success. A viewer's repair is
refused (422 `ledger-not-writable`) before anything is rewritten, and the Maintenance panel
offers Repair only to an owner or an editor. The links that lead there from an unresolved
drift finding follow the same rule. For an owner or an editor they carry `?check=true`, which
runs the check on arrival: "Check and repair →" on the Overview's Problems and "Check and
repair" in Settings → Notifications' Recent problems. For a viewer they carry no
`?check=true` and read "Details →" and "Details".

#### The check and the repair

The check and the repair are the same for every projection: the check changes no figure,
and there is one repair per projection so a reader is never told about a problem the
product cannot fix. The check is a POST because it records its verdict, as the scheduled
check does: the ledger's stored verdict, which the Overview's "consistency checked …"
reads, and, when the verdict is not already what the ledger says, the scheduled check's
own `consistency.drift` or `consistency.ok` notice, which the Overview's Problems widget
lists. Running it again with nothing changed sends nothing. Anyone who can read the ledger
can run it.

Each projection's `mismatches` lists at most 100 rows, so a badly drifted ledger does not
send the client every one; its `mismatchedCount` counts them all, and `mismatchedCount`
above the length of `mismatches` means some are not listed. The repair rewrites every one
whatever the cap, and its `repaired` lists at most 100 of what it rewrote, with the full
count in `mismatchedCount`. Nothing pages through the rest: run the repair, and check again.

A repair answers `{ projection, repaired, after }`: `repaired` is the check it worked from,
what it rewrote; `after` is every projection checked again once it has committed, the
verdict it records. Its `consistency.repaired` notice says what it rewrote and what is still
to fix, and clears the ledger's consistency problem only when nothing disagrees; otherwise
it is a warning, and the Problems widget keeps the problem with that summary. When a check
that began after the repair has recorded its verdict first, that verdict stands, and the
notice follows it: it says what that check found, and clears the problem only when that
check found nothing. Once a repair has begun, the rewrite, the check after it and the
record run to the end even if the client hangs up: several rewrites commit in pieces
(balances per account, holdings and realized gains per position, posting counts per
header), and a rewrite stopped partway would leave part of it committed with no record. If
the check after it fails, the rewrite still stands and is still recorded: the answer has
`after: null`, `lastRepairedAt` is stamped, and the stored verdict is cleared, as a restore
clears it, so the ledger is due on the scheduled check's next tick. The notice is a warning
saying what was rewritten and that what still disagrees is not known. The failure is logged
as a warning from `Coffer.Api.Consistency.Repair`.

If the rewrite itself throws partway, for a reason other than the client (a database error,
say; a refusal because another write held the ledger is described below), the request
answers the error, and whatever pieces it had committed stand. The stored
verdict was taken before the rewrite, so it is cleared all the same, as of when the rewrite
stopped, and the ledger is due on the scheduled check's next tick. `lastRepairedAt` is left
as it was, since no repair completed, and no notice is sent: a problem the ledger already
showed stays outstanding until a check says otherwise. The failure is logged as an error
from `Coffer.Api.Consistency.Repair`. The Maintenance panel drops the report taken before
the repair, with its Repair buttons: run the check once the cause is fixed, and repair what
it still reports.

When no answer comes back from a repair (the connection drops, or a proxy in front of Coffer
gives up and answers in its place with 502, 503, 504 or a timeout status of its own), the
repair carries on to the end on the server, as above, and its verdict and notice are
recorded once it finishes. The Maintenance panel drops the report taken before the repair
and says that no answer came back, that the repair may still be running and that it is
recorded when it finishes; Check consistency shows where things stand. It reads only a 500
as a rewrite that failed partway.

One repair of a ledger runs at a time. A repair sent while another repair of the same
ledger runs, of any projection, answers 422 `consistency-repair-in-progress` and rewrites
nothing: it would only wait for the running one and then rewrite what that one had just
rewritten. That includes a repair pressed after one whose answer never came back, while
that one still runs. The lock lives in the API process, and is released once the running
repair has answered, however it ended.

Writes to one ledger take turns. Every write that can recompute the ledger's running
balances or positions (a save, a delete or restore of a row, a bulk change, an import's or
a feed sync's save, a reminder posting, a merge, a snapshot restore) takes the ledger's
write lock first (migration 275, [ADR-0112](decisions/0112-one-write-lock-per-ledger.md))
and holds it until its transaction ends, and so does every piece of a repair: one account
of the balances repair, one position of the holdings or realized-gains repair, one header
of the posting-count repair, and the whole of the trade-price, holdings-currency,
stored-notes, category-amounts and reminder auto-post repairs. The quote-currency repair
alone takes no write lock: it re-stamps the currency of fetched prices, which recomputes
no balance or position, so it runs in a plain transaction. A save made while a repair
runs waits for at most the piece in flight, not for the whole repair, and the repair's
next piece waits for the save.
The write that comes second reads what the first committed, so neither fails and no
figure is stored that leaves the other out. A tag merge, delete or clean-up, a budget's
Copy last month or Fill from average, a feed connection's delete and a ledger's delete
recompute nothing, but lock or write rows such a write goes on to need, so they take the
lock first too and take their turn. Two writes to one ledger wait for each other even when
they touch different accounts. Reads, snapshot creates and writes to other ledgers do not
wait.

A write waits at most half its database command timeout, 15 seconds by default. Past that
it is refused with 422 `ledger-write-busy` ("Another change to this ledger is still being
saved. Nothing was changed; try again in a moment.") and changes nothing; trying again once
the other write has finished succeeds. Where the write is not a single request, the refusal
lands as follows:

- **A repair** stops at the piece that was refused and keeps every piece it rewrote before
  it, so its 422 reads "Another change to this ledger is still being saved, so the repair
  stopped. Anything it had already repaired stays repaired; repair again to finish." The
  stored verdict is cleared, as for a rewrite that throws partway, and `lastRepairedAt` is
  left as it was. The Maintenance panel keeps its report, with its Repair buttons, beside
  that sentence; repairing again finishes the repair. The refusal is logged as a warning
  from `Coffer.Api.Consistency.Repair`.
- **A feed sync or a file import** saves nothing it fetched or parsed, closes its run as
  failed, and moves no account's sync watermark, so the next sync fetches the same rows
  again. A sync or an import a person sends answers the 422. `POST …/sync-all` names the
  connection with `failureCode` `ledger-write-busy` and carries on to the next one. The
  scheduled sync reports the connection as not synced (the run is degraded, not failed),
  carries on to the next connection and fetches the refused one again on its next run.
- **A scheduled reminder auto-post** leaves the occurrence due, counts it in the backlog
  notice (its `ledger_busy` count), and posts it on its next run. The run is degraded, or
  did nothing when nothing posted, and is not counted toward the job's auto-disable. When
  another occurrence of the same run also failed, the job's message counts both, for
  example "1 of 3 due occurrence(s) failed, and 2 were deferred because another change to
  this ledger was still being saved (the next run posts them)."
- **An MCP write** answers an error result whose text is `ledger-write-busy: ` followed by
  the 422's sentence, "Another change to this ledger is still being saved. Nothing was
  changed; try again in a moment."
- **MCP `set_transaction_category` and `set_split_posting_category`** save each transaction
  on its own, so a refusal stops only that one: it is listed in `rejects` with reason
  `ledger-write-busy`, unchanged, and the call goes on to the next. The transactions moved
  before it stay moved, and the answer counts them.

A feed sync whose save of what it fetched fails for any other reason closes its run as
failed ("The sync could not save what it fetched, so nothing it fetched was saved; the next
sync fetches it again."), drops what it fetched and moves no watermark. A sync a person
sent answers 500 as before; the scheduled sync counts the failure toward the job's
auto-disable, and records it. A file import whose save fails that way closes its run as
failed too ("The import could not save what it read, so nothing it read was saved; import
the file again.") and saves nothing.

A repair refused before it starts answers a 4xx and rewrites nothing: a role that cannot
write, a projection the server does not know, a ledger not visible to the caller, a session
that has expired, a `?today=` that does not bind, or another repair of the ledger still
running. The stored verdict is left as it was, and the Maintenance panel keeps the report
on screen, with its Repair buttons, beside the refusal.

In the Maintenance panel, Check consistency waits while a repair is in flight, as every
Repair does. A check that began during the rewrite would read the ledger partway through
it, and its report, answering after the repair, would replace what the repair answered.
The error of a repair before it goes as the check starts, with the report or the status
it stood beside, and the check's result, its report or its own error, takes their place.

`holdings` is each position's quantity and cost basis, compared with the FIFO walk the
recompute stores. The recompute creates a position's row the first time a write
touches it and keeps it, at quantity 0, once the position closes; the walk returns
only the positions holding shares. So a position the walk returns with no stored row
is drift, `reason` `missing`: one mismatch for its quantity and one for its cost
basis, each `stored` 0, with a `field` that says it is not stored. A stored row the
walk does not return must hold quantity 0; its cost basis is not compared, as a
position oversold and bought back to 0 keeps the purchase's cost. Every other
holdings mismatch is `reason` `value`. The stored rows and the walk read one snapshot.
Every save recomputes the positions it can move: those of the investment lines it
writes, and those of every investment line of a transaction whose date, action, hidden
or merged state it changes, so a delete that hides an imported row, the restore of one,
a merge and a re-date each recompute theirs. Deleting a transaction another was merged
into un-merges that one, and the delete recomputes its balances, positions and trade
days too. A limit: the delete reads the rows merged into it before it deletes, so a
merge into the same transaction that commits between that read and the delete is
un-merged by the database with nothing recomputed; since a merged-away trade prices no
day and an un-merged one does, the check reports its balances, position or day's trade
price, and each repair clears its own. The repair recomputes each position listed and writes a missing row
back.

The holdings repair restores a position's quantity, cost basis and realized gains, and
brings its existing lots to what the walk derives, closing those of hidden or merged
transactions. It does not restore a lot that is missing: a limit, not a gap a writer
leaves. Lots are created only by the writes that record or edit an acquisition, each
with the line it belongs to, so a lot goes missing only when its row is deleted by
hand; the repair then leaves the position's quantity and cost basis right and the lot
missing.

`trade_prices` is each day's trade-derived price, compared with what the rule derives:
the day's last execution of the security (ADR-0084). A hidden transaction (an imported
row someone deleted), one merged into another and a reminder's template are not trades,
for this as for every valuation (migration 273). The day's price is in the currency of
the brokerage that made the picked trade (migration 274), whatever currency that
brokerage's holdings account holds.
`reason` is `missing` when the day's trades priced nothing and `value` when a stored
trade, import or SimpleFIN price disagrees, at another price or in another currency; a
currency mismatch names both codes in its `field`, with `stored` and `expected` 0, and
a row that differs in both is two mismatches. A day a feed close or a manual price owns
is not compared, whatever its currency. Every save
re-derives the days its executions arrive on and the days they leave: a delete, a
restore, a merge, a re-date, an action change, a change to a line's security, quantity
or price, the bulk delete and restore, and a security merge each leave a day priced by
the executions that remain, and deleting a transaction another was merged into prices
that one's days again. A day with no execution left keeps its price, which is not a
finding: the panel lists it under prices no trade backs. The repair writes each listed
day through the same rank-gated writer, at the trade's price and in its brokerage's
currency.

`holdings_currency` is the currency of each brokerage's holdings account, the system
account its shares sit on, compared with the brokerage's own (ADR-0019 Rule 4,
2026-11-04 amendment). An edit of a brokerage's currency writes its holdings account's
in the same transaction, and re-derives the trade prices of the days its trades sit on,
so a difference is a write that went past the edit, such as a statement run by hand,
or one an earlier version left. One mismatch per holdings account, named by its
brokerage, with the `field` `its holdings are recorded in X, but the brokerage is in Y`
and `stored` and `expected` 0. The Maintenance panel counts them as "Holdings currency —
N of M brokerages out of step" (`brokerage` when M is 1), and the notifications'
summaries as `holdings_currency: N of M brokerages out of step`. The repair records each
listed brokerage's holdings in its own currency and writes no price: a trade price is in
its brokerage's currency already, whatever the holdings account holds. Nothing converts:
it relabels, and changes no amount or transaction. The two projections are independent,
so either repair can run first.

`reminder_auto_post` is the ledger's `reminder-auto-post` job, compared with its reminders
(ADR-0097, 2026-11-18 amendment). A reminder asks for the job when its
`auto_commit_days_before` is set, whether or not it is active, and a reminder saved with
Auto-post or a Moneydance import that brings one creates the job. A ledger with a reminder
that asks and no job row is one whose reminders show as auto-posting while nothing posts
them: an import, or a reminder save, by an earlier version that did not create the job.
The check examines the ledger, so `checked` is 1; a finding is one mismatch, scoped
`Auto-post job`, whose `field` says how many reminders it leaves unposted, with `stored`
and `expected` 0. The Maintenance panel says "Reminder auto-post — the auto-post job is not
set up", and the notifications' summaries
`reminder_auto_post: the auto-post job is not set up, so reminders set to auto-post are never posted`.
A job row that exists is never a finding, whatever its state: the job has no off switch and
the scheduler never disables it, so a disabled row is one an earlier version left. The
repair creates the job as a reminder save does, enabled at 05:00 local, configured by
whoever repairs, under the ledger's write lock, and leaves a row that exists as it is. It posts nothing itself; the job's next run
does, and that run also posts the occurrences already past due within its catch-up window
that nobody posted or skipped, so post or skip those on the Reminders page first where they
were entered by hand. The Reminders page sets its time.

`quote_currency` is the currency of each security's fetched prices (`security_prices`
rows with `source = 'fetch'`), compared with what Yahoo names (ADR-0114 D5, amendment,
migration 300). Every fetch before the currency fix was stamped USD. A quote refresh
that reads a security's currency stamps `securities.quote_currency_confirmed_at` and
re-stamps that security's fetch rows in another currency, and the refresh asks Yahoo
once for each unconfirmed security with fetched prices, held or not. Two kinds of
mismatch, one per security, with `stored` and `expected` 0: `unconfirmed` (`N fetched
prices in X, a currency Yahoo has not confirmed`), and `value` for a confirmed security
with fetched prices in another currency than its latest fetched price's (`N fetched
prices in X, but Yahoo quotes it in Y`). The notifications' summaries say
`quote_currency: N of M securities with fetched prices in a currency Yahoo has not
confirmed`, M counting the securities with fetched prices. The repair asks Yahoo for each
listed security's currency, so it is egress: it needs the repairing user's Yahoo opt-in on
Settings → Quotes, and without it is refused with `consistency-repair-needs-yahoo` before
anything is written. It re-stamps fetch rows only, and touches no manual, trade, import
or SimpleFIN row. For a symbol quoted in its major unit it changes the currency alone. For
one quoted in a minor unit (`GBp`) the rows from before the fix hold pence, so price, high
and low are divided by the factor and rounded once to 4dp, as a new fetch is stored, and
the major code is stamped; volume is kept, and a row already in pounds is not touched. The
repair's answer lists under `notRepaired` each security it could not rewrite, as a
mismatch whose `reason` says why: the lookup's code (`fetch-failed`, `rate-limited`,
`currency-unknown`, …), or `quote-symbol-not-public` for one it could not ask.

`stored_notes` is the memo-and-tags presentation each register reads (migration 264):
what the rule computed for every leg, compared with what it computes from the header's
rows now. Every writer recomputes it in its own transaction, so a mismatch means a write
that went past them, such as a statement run by hand. It is reported as one mismatch per
header, naming the header and no memo or tag. The background monitor only warns; the
panel's Repair action recomputes the headers listed, under the same ordered header lock
every writer takes, and changes no memo, tag or amount.

`category_amounts` is the rule that a category line carries the real side's amount
negated ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md) D13, D15): a
category holds no money of its own. It reports each posting of exactly one real line and
one category line whose two lines do not sum to zero, a reminder's template and a hidden
or merged-away row included, and it is counted in the verdict like every other
projection. Unlike every other repair it rewrites a figure a person or an import
entered, not a derived one, so it touches only that shape: it reads the postings again
under the ledger write lock and sets each category line to the real line negated. No
real line changes, so no account's balance moves. A posting of any other shape whose
category line is out of step (both lines on categories, or more than one real or
category line) is not repaired; it is listed in the advisory below with its reason,
`category-line-between-categories` or `category-line-many-lines`, and the cure is an
edit.

**Figures that do not fit their currency** ([ADR-0114](decisions/0114-benchmarks-and-currency-conversion.md)
D15). Every money figure is held at its currency's ISO 4217 minor units, 0, 2 or 3 places.
A stored figure written before that rule with more places than its currency allows is
kept, and an edit that leaves it unchanged passes. The check lists each such figure,
except on a transaction merged into another, which no screen shows or opens; a currency
change or a move does not count that one either. Deleting the winner, where the delete
removes it rather than hiding it (a feed or import row is only hidden), un-merges it, and a
figure it holds that does not fit its account's currency, including one a currency
change made while it was merged away, is then listed (ADR-0114 D15, decided by the
owner, 2026-11-17). It
also lists each stored account or ledger currency code that would now be refused, which
keeps working at 2 places, as today; this holds for any reason it is refused (not an ISO
4217 currency with an exponent, an "N.A." entry, or the 4-place fund units `CLF` and
`UYW`) (settled by Claude, 2026-11-16).
These listings are an advisory, as the unbacked-price advisory is (ADR-0084 D6): shown
beside the check's verdict and not counted in it, so they raise no `consistency.drift`,
no Overview Problem and no ADR-0102 dot (settled by Claude, 2026-11-16). The stored value
is kept by decision, so no repair rounds a figure or replaces a code, and the cure, where
one is wanted, is an edit by an owner or an editor. A
realized gain is different: it is derived, so on an account whose currency has 0 or 3
places a row the earlier walk rounded to 2 places is `realized_gains` drift like any
other, and that projection's repair re-derives it (settled by Claude, 2026-11-16).

---

### MCP client troubleshooting (ADR-0063)

#### Calls time out but the server is healthy → the local `mcp-remote` proxy is wedged

Claude Desktop / Gemini reach the server through a local `npx mcp-remote` Node
proxy that occasionally hangs (dead socket to the CDN); then every MCP call times
out even though the server is fine. Recovery is to kill the stuck proxy and let the
client respawn a fresh one — scripted in
[`scripts/restart-coffer-mcp.sh`](../scripts/restart-coffer-mcp.sh) (Linux/macOS/WSL)
and [`scripts/restart-coffer-mcp.ps1`](../scripts/restart-coffer-mcp.ps1) (Windows).
Both kill the proxy, health-check the server's discovery endpoint (so you can tell a
hung proxy from a real outage), and — with `--clear-auth` / `-ClearAuth` — wipe the
cached `~/.mcp-auth` token. Then fully quit and reopen the client.

#### CDN/WAF bot protection challenges the connector

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
    only have `Origins__0`; add the line to the compose, or upgrade by
    [upgrading.md](upgrading.md#the-upgrade) to pick up the current one.)

    This is also what the admin UI shows as the address to give a client
    (ADR-0093). On a split-host install it is the only way for that panel to be
    right: unset, it falls back to the origin the admin happens to be browsing,
    which is the **web** host. `COFFER_WEB_ORIGIN_1` still works as a fallback for
    the origin, but does not feed the displayed address.
- **Disable the bot protection.** Coffer's real boundary is WebAuthn + RLS +
  off-by-default MCP + OAuth; bot protection is DDoS/noise reduction, not the auth
  layer. Acceptable for a personal, fully passkey-gated install.
---

## Reference

*Edge deployments, config detail, and the internals behind the procedures above.*

### Secrets

- `.env` is gitignored. Real credentials live there only on the host.
- `.env.example` is committed and contains placeholder values exclusively.
- Bank-feed tokens (SimpleFIN), the Google Drive token and a private fork's repository
  token for the Upgrade tab (`upgrade_repo_token`) live in the database,
  **envelope-encrypted** (Layer 3 above) — not in env files. The host updater's two tokens
  for a private fork are the exception: they are files in `~/coffer/upgrades/secrets/`
  (0600), never mounted into a container (ADR-0110).
- The master KEK lives in its own file (`/app/keys/master.key` on the `coffer_keys`
  volume, `0600`), **not** in `.env` — ADR-0092 D1 retired the env var because its
  value is readable via `docker inspect`, `/proc/<pid>/environ`, child environments and
  crash dumps. Encrypted at rest by Layer 1, never written to backups in plaintext,
  never committed. Deliberately on a volume of its own: not `postgres_data`, so one dump
  can't carry both the wrapped material and the key that opens it, and not
  `coffer_data`, so a copy of the volume that holds every backup doesn't carry the key
  that opens their secrets. Releases before 0.121.0 kept it at `data/master.key` on
  `coffer_data`; the first start with the current `docker-compose.yml` moves it, and the
  keys rotation archived beside it, across. Inside a container it moves them only onto
  a volume of its own, and it stops with instructions rather than choose between two
  different keys. A native run keeps the old default unless `Api:MasterKey:Path` says
  otherwise.
- Never log raw tokens, raw CSV/OFX uploads, or full transaction memos at INFO level.
- **OpenIddict token keys** — the keys MCP clients' OAuth tokens are signed and
  encrypted with — are minted per install and kept in the database (`oidc_server_keys`),
  **sealed under the master KEK** like the other secrets. Every backup carries them, a
  master-key rotation re-wraps them, and a restore without the source install's master
  key replaces them. (A restore revokes every OAuth grant either way, so MCP clients
  connect again after any restore.)

  Earlier releases kept them as two files, `data/openiddict/signing.pem` and
  `encryption.key` (under `src/Api/data/` for a native run, on the data volume in a
  container), which no backup carried. The first start of a release with the table moves
  them into the database and deletes them, so clients stay signed in across the upgrade.
  A key file that cannot be read as a key (a torn write) is replaced by a fresh key
  instead. The path stays gitignored, and the key-material check still fails preflight if
  anything under it becomes tracked.

  Those two files were tracked and published in earlier snapshots, so **that key pair is
  public**. Container installs were never affected (own volume, own keys). A native
  install that started from a public clone signed and validated OAuth tokens with a key
  anyone can read; the upgrade recognises that pair and mints a fresh one rather than
  moving it in, so its MCP clients re-authorize once.

  **On a key compromise**, replace the pair:

  ```bash
  docker compose exec api dotnet coffer-api.dll reset-oauth-keys
  docker compose restart api
  ```

  This invalidates outstanding OpenIddict access and refresh tokens, so MCP clients must
  re-authorize — web sign-in is unaffected, because cookie sessions are database-backed
  and independent of these keys. If the boot log ever says the keys do not open under the
  master key (a master key file swapped outside a restore), MCP sign-in stays off until
  the right master key is back or the pair is reset this way.

#### Database exposure and authentication

Two deliberate settings in `docker-compose.yml`, both about the database refusing
connections it has no reason to accept:

- **Postgres publishes no port.** The application never needs one — the API
  resolves `postgres` over the compose network — and an operator attaching `psql`
  execs into the container ([Attaching psql](#attaching-psql)). A GUI client on the
  host gets a port from the `docker-compose.dev.yml` overlay, bound to `127.0.0.1`:
  bound to `0.0.0.0` it would offer all three roles to anything that can route to
  the host.
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
created before this landed still has `trust` in its `PGDATA` — the compose setting
does nothing for it. **`install.sh` applies the remediation itself** when it sees
those rules, so an upgraded install is hardened without anyone deciding to. Run it
directly only if that failed, or on a host that was never installed by the script:

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
this is applied. `scripts/harden-pg-hba.sh` and
`scripts/provision-restore-privileges.sh` therefore read the superuser password from
`secrets/postgres_password`, falling back to `POSTGRES_PASSWORD` in `.env` for an
install that predates the move to files. Override with `SECRETS_DIR` when
targeting a second stack. The schema-apply test lane is unaffected — it runs its
own ephemeral container, which never sees these settings.

#### Restore privileges

A backup restore builds the backup as a new database and swaps it in
([decisions/0061-bootstrap-restore.md](decisions/0061-bootstrap-restore.md),
amendment of 2026-10-09). That needs `coffer_service` to **own the `coffer`
database** and to hold **`CREATEDB`** — `ALTER DATABASE … RENAME` requires both.
A fresh install gets them from `db/init`. An install created earlier gets them
from:

```bash
sudo bash ~/coffer/scripts/provision-restore-privileges.sh
```

`install.sh` runs it on every upgrade. It is idempotent, needs no restart, and
verifies its own result. Until it has run, both restore screens refuse with this
command named, before anything is uploaded or staged.

#### Undoing a restore

The database a restore replaced is kept as `coffer_pre_restore_<utc>` (the name
is shown when the restore completes, and in the API log). Only the most recent one
is kept: the next restore drops it. To go back to it, with the API stopped:

```bash
cd ~/coffer && sudo docker compose stop api
sudo docker exec -i coffer-postgres sh -c 'PGPASSWORD="$(cat "$POSTGRES_PASSWORD_FILE")" psql -U "$POSTGRES_USER" -d postgres' <<'SQL'
BEGIN;
ALTER DATABASE coffer RENAME TO coffer_undone_restore;
ALTER DATABASE "coffer_pre_restore_<utc>" RENAME TO coffer;
COMMIT;
SQL
sudo docker compose start api
```

The two renames are one transaction, so either both happen or neither does. Drop
`coffer_undone_restore` when you no longer need it.

This block is not just advice: the restore drill reads the SQL above out of this file and
runs it after every restore it tests, then checks that the live database is the replaced
one, exactly, and that the API serves. Edit it here and the drill tests the edit. There is
deliberately no undo button: a second destructive boot path costs more than this saves.

If that restore **adopted another install's master key** (you supplied the source
key), the previous database's secrets are sealed under the key it replaced, which
was archived beside `master.key` (`master.key.<utc>.bak`, named in the API log). Put
that file back as `master.key` before starting the API, or the feed tokens, stored
backup passphrase and Drive token will not open. A restore that FAILS after adopting
puts the original key back itself.

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
on its upgrade path. Both leave a `.env.pre-secrets` backup that still contains the
passwords — and both remove it on their NEXT run, overwriting it first, once
`secrets/` holds all three. The one-run delay is deliberate: that copy is the way
back if the file-based arrangement turns out to be wrong, so it survives exactly
as long as the migration might still need undoing.

#### Container privileges and connection logging

Both containers run with a reduced kernel privilege set. Nothing here needs
configuring — it is in `docker-compose.yml`, and `scripts/install.sh` rewrites
that file on upgrade, so an existing install picks it up on its next
`docker compose up -d` with no migration step.

| Setting | `postgres` | `api` |
|---|---|---|
| Runs as | `postgres`, after the entrypoint drops root | `app` (uid 1654) from the start |
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

The API needs none of them. Its image runs as the unprivileged `app` user from the start
(FU-084), so there is no privilege drop to perform. That user owns `/app/data` and
`/app/keys`, each on its own volume, so it writes the bootstrap URL and the master key
without `DAC_OVERRIDE`. It binds 8080, so it needs no `NET_BIND_SERVICE`, and the backup
path's `pg_dump`/`pg_restore`/`psql` are ordinary TCP clients. Verified against a fresh
prod-shaped stack that bootstrapped, took a backup and restored it with everything
dropped. Before 0.122.0 the API ran as root, so a parser bug was root inside the container.
An install upgraded from then has its volumes handed over once; see
[upgrading.md](upgrading.md#the-api-runs-unprivileged-01220).

The root filesystem stays writable. A read-only root would need a tmpfs large enough for
a restore, which decrypts the whole backup to temp, and that is the worse trade.

If you need to debug inside either container with a tool that wants a capability
(`strace` and `gdb` need `SYS_PTRACE`), add it to that service's `cap_add`
temporarily rather than removing the stanza.

Postgres also runs with `log_connections=on` and `log_disconnections=on`. With
scram on every path and RLS as the authorization boundary, the database is the
last line of defence, and without connection logging a successful login leaves no
trace — no way to answer "who connected, as which role, from where?" after the fact.
The log names the role on every connection (`connection authorized: user=…
database=…`), which also makes a credential-stuffing attempt against the
loopback port visible, and records session duration on disconnect. Expect a
couple of lines per connection; the API pools, so steady-state volume is low.

### Provisioning the RLS roles for non-docker installs

The role-init step is bundled into `db/init/00-init-roles.sh` so docker-compose users
don't see it. For bare-metal Postgres installs (k8s, homelab, …), provision the two
roles manually before pointing the API at the DB:

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

Or keep the passwords out of the environment by leaving `Password=` off and pointing at
files instead — the docker path does this by default:

```
COFFER_API__ConnectionString=Host=...;Username=coffer_app
COFFER_API__AppPasswordFile=/run/secrets/coffer_app_password
COFFER_API__ServiceConnectionString=Host=...;Username=coffer_service
COFFER_API__ServicePasswordFile=/run/secrets/coffer_service_password
```

Either form works; the file wins if both are present. See [Database exposure and
authentication](#database-exposure-and-authentication).

The service connection string's TLS settings (`SSL Mode`, `Root Certificate`,
`SSL Certificate`, `SSL Key`) govern backups and restores too: `pg_dump`, `pg_restore` and
`psql` connect with the same mode and certificates as the API. Before 0.122.0 they connected
with libpq's default, `prefer`, which falls back to plaintext and checks no certificate.
`SSL Password` is refused, because libpq cannot take a key's passphrase from the
environment. Use an unencrypted key that only the API's user can read. The tools get the
database password in an owner-only file under temp, which is removed when each run ends.

Migration 017 guards role existence up front — a half-provisioned install fails with a
clear error message instead of silently granting privileges to roles that don't exist
yet.

### Schema application: DbUp, not initdb

`docker-entrypoint-initdb.d` mounts only `db/init/00-init-roles.sh`, which creates the
two RLS roles (`coffer_service`, `coffer_app`) from `COFFER_SERVICE_PASSWORD_FILE` /
`COFFER_APP_PASSWORD_FILE` (compose secrets under `./secrets/` — see [Database exposure
and authentication](#database-exposure-and-authentication)), falling back to the
`COFFER_SERVICE_PASSWORD` / `COFFER_APP_PASSWORD` env vars when no file is configured.
The schema itself lands when the API starts and runs DbUp (`MigrationRunner`) against
the `ServiceConnectionString` — `coffer_service` owns the tables it creates, which is
the precondition for `ENABLE ROW LEVEL SECURITY` in migration 017.

Some migrations carry a step written in C#, journaled under its own name directly after
the SQL script it belongs to (`MigrationRunner.CSharpSteps`): a step that has to run the
C# memo-and-tags rule, which has no SQL definition. The first is
`264_stored_leg_notes_backfill`, which fills the stored memo-and-tags presentation for
every existing transaction. It runs inside the same startup migration pass as the SQL,
before the API answers anything, and logs `Stored notes computed for N transaction(s).`
when it finishes. A failure stops the start and journals nothing, so the next start runs
it again.

### Moneydance diagnostics environment variables

Importing a Moneydance export is done in the app (Ledgers → Import), which uses the
API's own connection. The source-checkout tool `coffer-import-moneydance` keeps only
the read-only `audit` and `reconcile` diagnostics
([moneydance-import-fidelity.md](moneydance-import-fidelity.md)).

| Variable | Purpose |
|---|---|
| `COFFER_DB_CONNECTION` | Postgres connection string used by `reconcile`. The `--db <CONNECTION_STRING>` flag takes precedence when present. `audit` reads only the export file and needs no database. |

