# Coffer

A self-hosted personal finance application built on .NET + PostgreSQL.
Replaces Moneydance for users who want to keep years of financial history under
their own control — a browser UI you can reach from anywhere over HTTPS,
passkey-only login (no passwords to phish, guess or reuse), double-entry
bookkeeping, live bank-feed sync via SimpleFIN, and an MCP server that lets Claude
work with your ledger without letting a model near the arithmetic.

> *Not affiliated with The Infinite Kind or Moneydance. "Moneydance" is referenced
> solely to describe interoperability and migration paths.*

**What's distinctive**

- **A real migration path off Moneydance** — [imports the export](#moneydance-import)
  with history intact: accounts, transactions, investment lots, cost basis, splits,
  reminders. Not a CSV of last year's spending.
- **[AI access to your ledger over MCP](#mcp--ai-access-to-your-ledger-adr-0063)** —
  Claude and other clients get typed tools to *query* it and, opt-in, to *clean it up*
  (recategorize, merge duplicates, retag in bulk, convert in-kind transfers). Every
  number is computed by Coffer under row-level security, never by the model.
  Read-only by default; writes need two independent switches and every one is audited.
- **[Safe to expose, fine not to](#reaching-it)** — run it on one machine over
  localhost, or reach it from anywhere; passkeys mean the remote case can only be
  served over HTTPS, so a misconfigured deployment fails closed instead of quietly
  serving your finances in the clear.
- **Double-entry underneath** — every flow is a balanced posting pair, so balances,
  FIFO cost basis and realized gains reconcile instead of drifting.
- **Your keys** — encrypted backups and envelope-encrypted secrets (feed tokens, the
  backup passphrase) under a master key you hold and rotate; optional Drive sync, no
  third-party analytics. Transaction rows are not column-encrypted — host disk
  encryption is yours to configure ([the layered model](docs/operations.md#encryption-at-rest)).

Licensed [AGPL-3.0](LICENSE) — running a modified copy as a network service means
publishing your changes to its users. Published as periodic source snapshots from a
private development repository; **outside contributions aren't being accepted at
present** ([CONTRIBUTING.md](CONTRIBUTING.md)), though forking is explicitly fine.

## Who this is for

Two gates, and they're independent.

**The hosting gate.** If you're comfortable self-hosting a web application — Docker,
a `.env`, reading a log when something doesn't come up, deciding whether you want it
reachable from outside your house — you're the audience. Home Assistant is the fair
comparison for the level of comfort assumed. If that sounds like a weekend you'd
rather not have, this isn't for you, and that's a positioning statement rather than
an apology.

**The sign-in gate, which is stricter than Home Assistant's.** Coffer has no
passwords: you sign in with a passkey. That means the machine you *use* it from needs
somewhere to keep one — your operating system's authenticator, a browser extension
that stores passkeys, or a hardware key. On Windows and macOS you already have one.
**On Linux you don't**, and that changes the install: see
[Signing in](#signing-in) before you start.

## Documentation

**This README covers getting to a working, signed-in install** — choosing how you'll
reach it, installing on Linux, Windows or macOS, first run, and enrolling a passkey.
Everything after that, and anything that goes wrong, is in
[operations.md](docs/operations.md).

Full index — and how the docs stay in sync with the code — is in
**[docs/README.md](docs/README.md)**. Start with
[architecture.md](docs/architecture.md) for the design,
[operations.md](docs/operations.md) to run it, and
[follow-ups.md](docs/follow-ups.md) for open work.

## Status

Feature-complete across the original 10-phase build — PostgreSQL schema,
Moneydance importer, .NET API + RLS, React SPA, bank-feed + file ingest,
override/categorization, merge review, dashboard, investments, and single-container
ops/packaging — plus **multi-user access**: per-ledger owner/editor/viewer roles,
member + admin management, and invite links (ADR-0083).

The phase sequence is in [docs/architecture.md](docs/architecture.md) §8; open work
(the ordered *Next* slices + the backlog) is in
[docs/follow-ups.md](docs/follow-ups.md). Larger items still open: **budgets and
budget-vs-actual** (the biggest unbuilt feature), the generic **CSV ingest
provider**, and broader **in-app reports** — today's reporting strength is via MCP
rather than the UI.

## Install

One installer for all three platforms — it detects native Linux, WSL2 or macOS and
adjusts what it expects of Docker. Needs ~2 GB of RAM (the API and Postgres containers
cap at 1 GB each) and a few GB of disk. The published image is multi-arch, so Intel and
Apple Silicon both run natively.

| Platform | What you provide first |
|---|---|
| **Linux** | Docker — the installer offers to install it for you. |
| **Windows** | Docker Desktop with the WSL 2 engine, and a WSL distro (`wsl --install -d Ubuntu`) with WSL integration enabled. You then run the installer *inside* the distro. |
| **macOS** | Docker Desktop, running. |

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/strongboxlabs/coffer/main/scripts/install.sh)
```

Run it as your **normal user**, never `sudo bash …` — under `sudo` your home becomes
root's and `~/coffer` plus your secrets land in `/root`. It asks how you'll reach Coffer
and on which port, then writes `~/coffer/.env`, pulls the image, starts the stack and
prints the one-time setup link. Re-run it later to upgrade in place or to wipe and
reinstall.

**Read [Signing in](#signing-in) first if the host is Linux** — which authenticator
you'll enrol changes the install, and finding that out afterwards is the wrong order.

### Linux

1. **Docker.** If it isn't installed, the script offers to do it via
   `https://get.docker.com`. Say no and install it yourself if you'd rather.
2. **Run the installer:**
   ```bash
   bash <(curl -fsSL https://raw.githubusercontent.com/strongboxlabs/coffer/main/scripts/install.sh)
   ```
3. **Answer two questions:** how you'll reach it (`1` for this machine only, `2` for a
   domain over HTTPS) and the port (default `8080`).
4. **Open the `http://localhost:8080/setup/<token>` link it prints** at the end.
5. **Enrol a passkey.** On a Linux box that means a security key in this machine, or
   tunnelling in from a laptop — see [Signing in](#signing-in).
6. **Tick "Include a Demo ledger"** if you want something populated to look at, then
   **save the recovery codes** (shown once) and the master key from the welcome screen.

### Windows (Docker Desktop + WSL2)

The manual part is Docker Desktop and a WSL distro; the installer does the rest from
inside that distro. You end up browsing `http://localhost:8080` in Windows, so
**Windows Hello enrols the passkey** and none of the Linux authenticator problem
applies.

1. **Install Docker Desktop for Windows** and start it.
2. **Docker Desktop → Settings → General:** tick **Use the WSL 2 based engine**.
3. **Install a Linux distro**, in PowerShell:
   ```powershell
   wsl --install -d Ubuntu
   ```
   Set the UNIX username and password it asks for.
4. **Docker Desktop → Settings → Resources → WSL integration:** enable the **Ubuntu**
   distro, then **Apply & restart**.
5. **Open the Ubuntu shell and check Docker is visible from inside it:**
   ```bash
   docker version --format '{{.Server.Version}}'
   ```
   A version number means integration is working. An error means step 4 didn't take —
   fix that rather than installing Docker inside WSL, which would give you two daemons
   that can't see each other's containers.
6. **Run the installer in the Ubuntu shell:**
   ```bash
   bash <(curl -fsSL https://raw.githubusercontent.com/strongboxlabs/coffer/main/scripts/install.sh)
   ```
   Keep the default install directory (`~/coffer`, inside the distro). The installer
   **refuses** a path under `/mnt/c`: that's a 9p mount, the exec bit on the database
   init script doesn't survive it, and the roles then never get created.
7. **Answer `1`** (this machine only) and the port.
8. **Open the printed link in Windows** — Chrome or Edge, not a browser inside WSL.
   Docker Desktop publishes the port to Windows `localhost`, so the URL is the same
   one, and Windows Hello handles the passkey.

### macOS (Docker Desktop)

1. **Install Docker Desktop for Mac** — [Apple Silicon or
   Intel](https://docs.docker.com/desktop/install/mac-install/) — start it, and wait
   for it to report *running*.
2. **Check it in Terminal:**
   ```bash
   docker version --format '{{.Server.Version}}'
   ```
3. **Run the installer:**
   ```bash
   bash <(curl -fsSL https://raw.githubusercontent.com/strongboxlabs/coffer/main/scripts/install.sh)
   ```
   No `sudo` needed for Docker here — Docker Desktop runs as you.
4. **Answer `1`** (this machine only) and the port.
5. **Open the printed link in Safari or Chrome on the same Mac.** Touch ID (or your
   iCloud Keychain passkey) enrols the credential.

### Notes for all three

**Re-running the installer** is how you upgrade in place, and it also offers a
wipe-and-reinstall. See [ADR-0075](docs/decisions/0075-linux-install-script.md).

**Undoing it entirely:** `cd ~/coffer && docker compose down -v` removes the
containers and both volumes — the database and the master key with them.

**Restoring an existing Coffer?** Install normally; the installer asks nothing extra
and takes no key. Then pick **Restore from a backup** on the setup screen and paste
the source install's master key *there*, where it's checked against the archive before
anything is replaced ([ADR-0094](docs/decisions/0094-restore-is-ui-only-and-the-kek-has-no-env-channel.md)).

**Back up the master key.** The API mints it on first boot into its own file and the
welcome screen shows it once; read it again later from **System → Encryption → Show
key**. It is never in `.env` and there is no environment variable for it, so there's no
second copy to go stale after a rotation. Losing it costs the secrets sealed under it
— bank-feed tokens, the stored backup passphrase, the Drive connection — and nothing
else: your ledgers, passkeys and backups don't depend on it.

**Installing from a private fork?** Anonymous `raw.githubusercontent.com` 404s when the
source isn't public, so fetch the script over the authenticated API and pass the same
token in — a classic PAT with `repo` + `read:packages`, which also logs in to ghcr for
the image pull:

```bash
T='ghp_your_classic_pat'
COFFER_GH_TOKEN=$T bash <(curl -fsSL -H "Authorization: Bearer $T" \
  -H "Accept: application/vnd.github.raw" \
  "https://api.github.com/repos/strongboxlabs/coffer/contents/scripts/install.sh?ref=main")
```

## First run

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

**About that Demo box:** it seeds a worked example through the normal Moneydance
import pipeline, and it brings its own categories. A ledger you create empty later
(the hub's **New ledger** dialog) seeds a starter category tree instead
([ADR-0071](docs/decisions/0071-install-provisioning-ui-import-authed-restore.md) D5).

## Signing in

There are no passwords. You enrol a **passkey** at setup and sign in with it after
that, with one-time **recovery codes** as the way back if the authenticator is lost or
the hostname changes.

A passkey has to live somewhere, and where decides how smooth this is:

| Where it's stored | Works on | Notes |
|---|---|---|
| **Operating system** — Windows Hello, Touch ID / iCloud Keychain, Google Password Manager | Windows, macOS, iOS, Android | Nothing to install. Synced ones survive a dead device. **Linux has no equivalent.** |
| **Browser extension** — Bitwarden, 1Password, Proton Pass | Anywhere the extension runs, Linux included | The answer on Linux desktop if you don't own a hardware key. Verified with Bitwarden against a `localhost` install — it stores a credential for that hostname and signs in with it, so no udev setup and no second machine are needed. |
| **Hardware key** — YubiKey and similar | Everywhere, including Linux | The most portable: one key works across machines and operating systems. Best practice is to keep **two** and enrol both, which is how you avoid a lost key becoming a recovery-code event — though recovery codes cover you either way. |

**On Linux, plan this before installing.** Linux browsers have no OS authenticator, so
enrolling the first passkey needs one of:

- a **hardware key** plugged into the machine running the browser — it cannot be reached
  over SSH, and on Linux it needs one piece of setup that isn't obvious. See
  [Enrolling a hardware key on Linux](#enrolling-a-hardware-key-on-linux) below;
- a **passkey-capable browser extension** on that machine — Bitwarden and similar act as
  passkey providers where the OS doesn't. Verified with Bitwarden on a `localhost`
  install: it stores the credential and signs in with it, which makes this the least
  fiddly Linux route since it needs no device permissions at all. Enable
  *"Ask to save and use passkeys"* in the extension's settings, or it never intercepts
  the request and Chrome goes straight to its own dialog;
- **or an SSH tunnel from a Windows or Mac laptop**, using that machine's
  authenticator:
  ```bash
  ssh -L 8080:localhost:8080 <host>
  ```
  The browser sees `http://localhost:8080` either way, so the origin still matches.

A **headless** Linux host is the one combination to avoid walking into: no local browser
means no extension and no local hardware key, so without a tunnel there is nothing to
enrol with, and setup cannot complete. A Linux host with a desktop has two working routes
(extension or hardware key) and needs neither a second machine nor a phone.

### Enrolling a hardware key on Linux

Verified on Ubuntu with a YubiKey and Chrome. The failure it prevents looks like the
browser not detecting the key at all: Chrome offers *"Use your security key"*, you touch
it, and nothing happens.

The cause is device permissions, not detection. Chrome opens `/dev/hidraw*`, and the
default rules hand that device to whoever holds the **active local seat** via `uaccess`.
If the console is sitting at the login greeter, or the browser is running from a session
that isn't the local seat, the ACL belongs to `gdm` and your user cannot open the node:

```bash
getfacl /dev/hidraw0 | grep '^user:'
#   user:gdm:rw-     <- the problem: not you
```

Two ways out. **If you are physically at the machine**, log in graphically as yourself
and run the browser from that desktop — the seat is then yours and it works with no
configuration. **Otherwise**, grant access by group instead of by seat:

```bash
echo 'KERNEL=="hidraw*", SUBSYSTEM=="hidraw", ATTRS{idVendor}=="1050", MODE="0660", GROUP="plugdev"'   | sudo tee /etc/udev/rules.d/99-yubikey.rules
sudo udevadm control --reload && sudo udevadm trigger
sudo usermod -aG plugdev $USER
```

Then **log out of the desktop and back in** — group membership only applies to new
sessions, and an already-running browser keeps its old groups, so skipping this makes it
look as though the rule did nothing. Confirm both halves before retrying:

```bash
ls -l /dev/hidraw0            # want: crw-rw---- root plugdev
id -nG | tr ' ' '
' | grep plugdev
```

Two things that are *not* problems. A key reporting as `1050:0402` (U2F-only mode rather
than the default `0407`) enrols fine — Coffer requests no resident key and leaves
authenticator attachment unset, so both U2F and FIDO2 keys are acceptable. And if your
browser is snap-packaged, check `snap connections firefox | grep u2f` rather than assuming
it can't work; the `u2f-devices` interface exists, and only a missing connection is a
blocker.

**Your phone can only be enrolled on an HTTPS install at a real hostname.** The QR /
cross-device flow tunnels the ceremony to the phone, and phone credential managers
refuse to store a credential for `localhost`. It fails at the moment the phone tries to
save, which looks like a bug and isn't.

## Reaching it

Three shapes work, and one obvious-looking fourth cannot.

**1. This machine only** — the installer's default. `http://localhost:8080` on the host,
no TLS, no domain, nothing published beyond it. A complete deployment, not a starter
mode: every feature works, bank feeds and MCP included. Best when the machine you use
Coffer from is the machine it runs on.

**2. Tunnelled to localhost** — the install stays exactly as above, and you reach it
from elsewhere over SSH:

```bash
ssh -L 8080:localhost:8080 <host>
```

The browser still sees `http://localhost:8080`, so it remains a secure context and the
origin still matches. This is the answer for a headless box, and on Linux it's also how
you borrow a laptop's authenticator to enrol a passkey.

**3. A domain over HTTPS** — the same install behind your own reverse proxy at a real
hostname. This is the only shape where a **phone** can hold a passkey, and the only one
that gives an internet-hosted MCP client a URL to reach.

**What cannot work: a LAN address over plain `http`.** `http://192.168.1.50:8080` is
not a secure context, so browsers never offer WebAuthn at all and there is nothing to
sign in with. It's the natural second thing to try and it's a dead end — use the tunnel
or a hostname with HTTPS instead. A bare IP can't be an HTTPS relying-party id either.

**Switching later costs something.** Passkeys are scoped to the hostname
(`COFFER_RP_ID`), so moving from `localhost` to a domain invalidates every enrolled
credential: each user signs in once with a recovery code and re-enrols. Nothing is
lost — ledgers and history are untouched — but it burns a code per user, and it's
genuinely unrecoverable for anyone who never saved theirs. Worth choosing deliberately
before more than one person enrols.

**HTTPS isn't optional for shape 3, it's structural.** Login is WebAuthn, which
browsers only expose in a secure context. A misconfigured public deployment fails
closed at the login screen rather than quietly serving your finances in the clear.

**If you expose it, you front the TLS.** The container listens on plain HTTP on `:8080`
and expects a reverse proxy — Caddy, Traefik, nginx, Cloudflare Tunnel — to terminate TLS. Bot
filtering and volumetric traffic are the proxy's job, not Coffer's. The forwarded-header
behaviour, the allowed-origin variables and why `:8080` must not face the internet
directly are in
[operations.md → proxy and origin configuration](docs/operations.md#reaching-it-proxy-and-origin-configuration).

**Passkeys bind to the hostname.** Credentials are scoped to `COFFER_RP_ID`, so the
localhost-vs-domain choice at install time isn't cosmetic: changing the hostname
later invalidates every enrolled passkey, and recovery codes become the only way
back in. Decide the public name before you enrol.

**What it has, honestly:** no passwords anywhere (so no password reset flow to
attack, and nothing reusable to leak), per-ledger PostgreSQL row-level security as
the authorization boundary, encrypted backups, high-value secrets envelope-encrypted
under a master key you hold and rotate, and no third-party analytics in the UI —
tracing stays local unless you configure an exporter.

**What that doesn't include:** the application does not encrypt transaction rows.
Column encryption for bulk data was rejected deliberately (it breaks indexing and
trigram search) and OSS PostgreSQL has no TDE, so confidentiality of the ledger at
rest is host disk encryption — LUKS/BitLocker/FileVault — which you configure and
Coffer neither enforces nor checks. The layered model is spelled out in
[operations.md](docs/operations.md#encryption-at-rest).

**What it hasn't had: an external security review.** This is one maintainer's work,
unaudited by anyone else. The precautions above are the boring, well-understood
ones, but nobody has seriously tried to break it. Put it behind a reverse proxy,
keep it updated, and weigh that as you see fit.

Setup details — `COFFER_RP_ID`, extra origins, proxy headers — are in
[operations.md](docs/operations.md).

## Running it (development)

Prerequisites: Docker Desktop, a `.env` (`copy .env.example .env`), and the three
database passwords as **files** under `secrets/` — they are deliberately not env
vars (see [.env.example](.env.example) for why, and for a one-liner that generates
them). `scripts/migrate-db-secrets.sh` moves them across if you already have them
in `.env`.

**The one dev path — the Docker stack:**

```bash
bash scripts/dev-up-docker.sh
```

Brings up the full `docker compose` stack — Postgres + the single-container
API/SPA on `:8080` — the exact deployment artifact (ADR-0059), built from the
working tree. It carries `postgresql-client-16` so the whole-DB **backups**
(ADR-0060) run, and it's prod-parity. `.env` supplies ports, the master KEK and
feature toggles via `${VAR}` substitution; the role passwords come from
`secrets/` as compose secrets rather than the environment. Idempotent: re-run after a
code change to rebuild + restart (layers cached). On first Postgres start the
role-init script (`db/init/00-init-roles.sh`) mints `coffer_service` +
`coffer_app`; the schema lands when the API runs DbUp.

**Manual fallback** (two terminals, for fast single-side iteration with Vite HMR —
Vite serves the SPA on `:5173` and proxies `/api/*` to the API on `:5000` so
cookies flow transparently). Postgres must be up first, **published on the host** — the
base compose file doesn't publish it, so bring it up through the dev overlay:

```bash
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d postgres
```

Or uncomment the two `COMPOSE_FILE` lines in your `.env` (see
[.env.example](.env.example)) and a bare `docker compose up -d postgres` does the same.
`scripts/dev-up-docker.sh` sets them itself, so the scripted path needs nothing.

```powershell
# Terminal 1 — API
$env:ASPNETCORE_ENVIRONMENT      = "Development"
# The passwords live in files, so read them from there rather than from .env.
$app = Get-Content -Raw secrets/coffer_app_password
$svc = Get-Content -Raw secrets/coffer_service_password
$env:COFFER_API__ConnectionString = "Host=localhost;Username=coffer_app;Password=$app;Database=coffer"
$env:COFFER_API__ServiceConnectionString = "Host=localhost;Username=coffer_service;Password=$svc;Database=coffer"
dotnet run --project src/Api

# Terminal 2 — Web
cd src/Web
npm install            # first run only
npm run dev            # opens http://localhost:5173/
```

`ASPNETCORE_ENVIRONMENT=Development` is required — `appsettings.Development.json`
carries the Fido2 allowed-origins for the Vite dev server (`http://localhost:5173`).
Without it the API runs in Production mode, accepts only its own `:5000` origin, and
the WebAuthn ceremony fails with an origin-mismatch error.

First run is the same ceremony as any install — see [First run](#first-run) —
except that `dev-up-docker.sh` prints the `/setup/$token` link for you on a fresh DB,
and the session cookie it leaves is `coffer.session` (HttpOnly, SameSite=Strict).
Resetting the database during early development is in
[docs/operations.md](docs/operations.md).

## Moneydance import

**Use the in-app import wizard** (ADR-0071) — Ledgers → Import. Every UI import
creates a new ledger, and it's the only path that exists in the published container.

The CLI below is **not in the image**: the Dockerfile publishes `Api.csproj` only, so
`coffer-import-moneydance` requires a source checkout and the .NET SDK. It exists for
bulk work and the two read-only fidelity diagnostics, not as the normal route. It
connects as `coffer_service` (BYPASSRLS, for cross-ledger access):

```powershell
$env:COFFER_DB_CONNECTION = "Host=localhost;Port=5432;Database=coffer;Username=coffer_service;Password=$(Get-Content -Raw secrets/coffer_service_password)"

# A target ledger is required (ADR-0088 — there is no implicit default).
# --ledger-name targets an existing ledger or creates it; --ledger-id <UUID>
# imports into an existing one. --dry-run parses + validates without writing.
dotnet run --project src/Importer.Moneydance.Cli -- import path/to/export.json --ledger-name "Personal"
```

Import is idempotent and fails loudly on any dropped transaction. Two read-only
diagnostics — `audit` (which MD fields are dropped or lossy) and `reconcile` (what a
re-import would drop, run against an ephemeral rolled-back ledger) — are documented
in [docs/moneydance-import-fidelity.md](docs/moneydance-import-fidelity.md).

## MCP — AI access to your ledger (ADR-0063)

Coffer can expose typed tools to AI clients (Claude Desktop, claude.ai) over the
Model Context Protocol: **reading** — reports, holdings, allocation, returns,
transaction drill-down — and, opt-in, **writing** for data cleanup (ADR-0081).
Read-only by default; the whole surface is off by default.

The principle throughout: **financial math happens in deterministic tools, narration
happens in the model.** Cost basis, realized gains and returns are computed by
Coffer under row-level security; the model never calculates them, it only asks and
explains.

1. **Enable it:** flip **System → MCP** on (admin) and restart the API — or set
   `COFFER_MCP_ENABLED=true` in `.env`. Either turns on `/mcp` plus an OAuth 2.1
   authorization server (OpenIddict); both are absent (not just 404) when off
   (ADR-0063 §D7/D8). The UI toggle is a deployment-wide system setting read at
   startup, so it takes effect on the next restart.
2. **Connect a client:** point the connector at `https://<your-domain>/mcp`. The
   client discovers the OAuth server, registers itself (DCR), and walks you
   through sign-in (your existing passkey) + a consent screen granting `coffer.read`.
   Behind a reverse proxy (Traefik), forwarded headers make the OAuth URLs resolve
   to your public domain — set `COFFER_RP_ID` / `COFFER_WEB_URL` to it.
3. **Use it:** ask for reports — e.g. *"top 10 spending categories by month in my
   main ledger"* or *"how are my investments doing?"* The numbers are computed by
   Coffer (RLS-scoped to you), never the model. Read tools can't change anything;
   the write tools (below) stay off unless you deliberately enable them.

**Read tools (19).** Discovery: `list_ledgers`, `list_accounts`, `list_securities`,
`list_tags`. Transactions: `transaction_summary` (income/expense/net by
category·account·payee, over time, with category-tree rollup), `list_transactions`
(line drill — filter by account/category/payee/amount/text, sort, page), `net_worth`,
`net_worth_history`. Investments: `holdings_snapshot`, `account_portfolio`,
`allocation` (by asset class / region / vehicle, with multi-asset look-through),
`investment_income`, `realized_gains` (FIFO), `returns` (money-weighted IRR),
`activity`, `price_history`, `find_in_kind_transfer_candidates`. Reminders:
`list_upcoming_reminders`. All read-only, USD.

There is deliberately no general write API here: nothing over MCP can create or delete
a transaction, or change an amount or a date. The write surface is taxonomy and
metadata cleanup only.

**AI-assisted writes (21 tools, ADR-0081, off by default).** Categories:
`create_category`, `rename_category`, `reparent_category`, `merge_category`,
`delete_category`, `set_transaction_category`, `set_split_posting_category`. Tags:
`set_transaction_tags` (bulk), `rename_tag`, `merge_tags`, `delete_tag`,
`cleanup_unused_tags`. Securities: `update_security`, `merge_securities`,
`set_security_classification`, `set_security_components`. Prices: `add_price`,
`update_price`, `delete_price`. Accounts: `set_account_taxstatus`. Investments:
`convert_in_kind_transfer`. They stay off behind two keys: a deployment-wide **AI-writes**
switch (a hot kill-switch — turn it off and it takes effect immediately, no restart)
AND a `coffer.write` grant an admin opts a token/client into. A `coffer.read` token
can never write. Every write is audited under **System → MCP → AI write activity**
(kept 180 days); the OAuth clients that can connect are listed, write-grantable, and
revocable there too.

Manage your own granted access under **Account → Security → Connected apps** (revoke
any time). Tokens are revocable reference tokens; nothing is exposed without the
toggle on.

## Layout

```
Coffer/
├── data/                            Local Moneydance export (gitignored)
├── db/
│   ├── migrations/                  numbered SQL (001…), applied at API startup via DbUp
│   └── test/                        Manual verification scripts (NOT auto-run)
├── src/
│   ├── Api/                         .NET 10 web API — layered: Db/ (data), Auth/ (security), Endpoints/+Contracts/+Errors/ (API surface)
│   ├── Importer.Moneydance/         .NET 10 console app — Moneydance JSON → Coffer Postgres
│   └── Web/                         Vite + React SPA — login, registers, settings, etc.
├── tests/
│   ├── Api.Tests/                   xUnit (unit + integration via Testcontainers)
│   └── Importer.Moneydance.Tests/   xUnit (mappers + repositories)
├── docs/                            Design, reference, process, ADRs — index: docs/README.md
├── .github/
│   ├── workflows/                   CI — schema + no-raw-sql audit + .NET/web tests + doc links
│   ├── ISSUE_TEMPLATE/
│   └── PULL_REQUEST_TEMPLATE.md
├── Coffer.slnx                       .NET solution
├── global.json                      Pins the .NET SDK version
├── NuGet.config                     Restricts package sources to nuget.org (hermetic builds)
├── docker-compose.yml               postgres 16 + single-container api/spa
├── .env.example                     Placeholder env values (real .env is gitignored)
├── secrets/                         DB role passwords as files, not env vars (gitignored)
├── CONTRIBUTING.md                  Contribution policy + how to report bugs
├── SECURITY.md                      Vulnerability disclosure
└── README.md                        This file
```

## Contributing

Outside contributions aren't being accepted at present — see
[CONTRIBUTING.md](CONTRIBUTING.md), which also covers what *is* useful (bug reports,
security disclosure) and the fact that forking is explicitly fine. The standards the
code is held to are in
[docs/engineering-standards.md](docs/engineering-standards.md).

## License

[AGPL-3.0](LICENSE). Running a modified copy as a network service means publishing
your changes to its users. Published as periodic source snapshots from a private
development repository — see [Contributing](#contributing) for what that means for
patches.
