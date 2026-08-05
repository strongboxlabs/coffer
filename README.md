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
- **[Exposed to the internet on purpose](#reaching-it-from-the-internet)** — passkeys
  only, and HTTPS is structural rather than advisory.
- **Double-entry underneath** — every flow is a balanced posting pair, so balances,
  FIFO cost basis and realized gains reconcile instead of drifting.
- **Your keys** — encryption at rest under a key you hold, encrypted backups,
  optional Drive sync, no third-party analytics.

Licensed [AGPL-3.0](LICENSE) — running a modified copy as a network service means
publishing your changes to its users. Published as periodic source snapshots from a
private development repository; **outside contributions aren't being accepted at
present** ([CONTRIBUTING.md](CONTRIBUTING.md)), though forking is explicitly fine.

## Documentation

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

## Install (Linux)

Stand up Coffer (app + Postgres) on a fresh Linux host:

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/strongboxlabs/coffer/main/scripts/install.sh)
```

**Installing from a private fork?** Anonymous `raw.githubusercontent.com` 404s when the source is not public, so fetch the script over the authenticated API and pass the same token in — a classic PAT with `repo` + `read:packages` (it also logs in to ghcr for the image pull):

```bash
T=<pat>
COFFER_GH_TOKEN=$T bash <(curl -fsSL -H "Authorization: Bearer $T" \
  -H "Accept: application/vnd.github.raw" \
  "https://api.github.com/repos/strongboxlabs/coffer/contents/scripts/install.sh?ref=main")
```

Run it as your **normal user** (not `sudo` — it escalates internally only where needed, so `~/coffer` + your secrets stay user-owned). Interactive: it checks for Docker (offers to install it), asks how you'll reach Coffer — **localhost** (single machine, `http`, no TLS) or a **domain** (`https`, you front the TLS/reverse-proxy) — and whether this is a fresh install or a restore, then writes `.env` under `~/coffer`, pulls the latest image, and starts it. Re-run any time to upgrade in place or wipe & reinstall. See [ADR-0075](docs/decisions/0075-linux-install-script.md).

> **Restoring an existing Coffer?** Answer **yes** to "restoring a backup" and paste that install's `COFFER_MASTER_KEK_BASE64` (+ its id — `v2` etc. if you rotated). Then use **Restore from a backup** on the setup screen; your data decrypts under that KEK with no post-install key-swap.

> A bare IP over `http` isn't offered: passkeys (Coffer's login) require `https` or `http://localhost`. For remote access without a domain, SSH-tunnel to localhost: `ssh -L 8080:localhost:8080 <host>`.
>
> **Back up `~/coffer/.env`'s `COFFER_MASTER_KEK_BASE64`** — without it, encrypted data can't be recovered even from a database backup.

## Reaching it from the internet

Coffer is meant to be reachable remotely — that is most of the point of replacing a
desktop app — and the login mechanism enforces the only sane way to do it.

**HTTPS isn't optional here, it's structural.** Login is WebAuthn passkeys, which
browsers only expose in a secure context. So Coffer *cannot* be served over plain
`http` to a remote browser and still be usable: either front it with HTTPS at a
hostname, or reach `http://localhost` on the box (SSH-tunnel from elsewhere). A
misconfigured public deployment fails closed at the login screen instead of quietly
serving your finances in the clear.

**You front the TLS.** The container listens on plain HTTP on `:8080` and expects a
reverse proxy — Caddy, Traefik, nginx, Cloudflare Tunnel — to terminate TLS. Bot
filtering and volumetric traffic are the proxy's job, not Coffer's.

**Passkeys bind to the hostname.** Credentials are scoped to `COFFER_RP_ID`, so the
localhost-vs-domain choice at install time isn't cosmetic: changing the hostname
later invalidates every enrolled passkey, and recovery codes become the only way
back in. Decide the public name before you enrol.

**What it has, honestly:** no passwords anywhere (so no password reset flow to
attack, and nothing reusable to leak), per-ledger PostgreSQL row-level security as
the authorization boundary, encryption at rest under a key you hold, encrypted
backups, and no third-party analytics in the UI — tracing stays local unless you
configure an exporter.

**What it hasn't had: an external security review.** This is one maintainer's work,
unaudited by anyone else. The precautions above are the boring, well-understood
ones, but nobody has seriously tried to break it. Put it behind a reverse proxy,
keep it updated, and weigh that as you see fit.

Setup details — `COFFER_RP_ID`, extra origins, proxy headers — are in
[operations.md](docs/operations.md).

## Running it (development)

Prerequisite: Docker Desktop, and a `.env` (`copy .env.example .env`, then set
`POSTGRES_PASSWORD`, `COFFER_SERVICE_PASSWORD`, `COFFER_APP_PASSWORD`).

**The one dev path — the Docker stack:**

```bash
bash scripts/dev-up-docker.sh
```

Brings up the full `docker compose` stack — Postgres + the single-container
API/SPA on `:8080` — the exact deployment artifact (ADR-0059), built from the
working tree. It carries `postgresql-client-16` so the whole-DB **backups**
(ADR-0060) run, and it's prod-parity. `.env` supplies the role passwords, master
KEK, and feature toggles via `${VAR}` substitution. Idempotent: re-run after a
code change to rebuild + restart (layers cached). On first Postgres start the
role-init script (`db/init/00-init-roles.sh`) mints `coffer_service` +
`coffer_app`; the schema lands when the API runs DbUp.

**Manual fallback** (two terminals, for fast single-side iteration with Vite HMR —
Vite serves the SPA on `:5173` and proxies `/api/*` to the API on `:5000` so
cookies flow transparently). Postgres must be up first (`docker compose up -d
postgres`):

```powershell
# Terminal 1 — API
$env:ASPNETCORE_ENVIRONMENT      = "Development"
$env:COFFER_API__ConnectionString = "Host=localhost;Username=coffer_app;Password=$($env:COFFER_APP_PASSWORD);Database=coffer"
$env:COFFER_API__ServiceConnectionString = "Host=localhost;Username=coffer_service;Password=$($env:COFFER_SERVICE_PASSWORD);Database=coffer"
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

First-run setup uses `/setup/$token`, where `$token` is the one-shot bootstrap token
the API logs at first start (the line beginning `First-run bootstrap.`) —
`dev-up-docker.sh` also prints it for you on a fresh DB. The setup page registers a
passkey, offers a single optional **Include a Demo ledger** box (ADR-0088), shows the
recovery codes once (acknowledgement-gated), then lands you signed in on the ledger
hub with the `coffer.session` cookie (HttpOnly, SameSite=Strict) set. Leave the Demo
box unticked and you start with **no ledgers** — create one or import a Moneydance
export from the hub. Full operations notes — including how to reset the DB during
early development — are in [docs/operations.md](docs/operations.md).

## Moneydance import

**Use the in-app import wizard** (ADR-0071) — Ledgers → Import. Every UI import
creates a new ledger, and it's the only path that exists in the published container.

The CLI below is **not in the image**: the Dockerfile publishes `Api.csproj` only, so
`coffer-import-moneydance` requires a source checkout and the .NET SDK. It exists for
bulk work and the two read-only fidelity diagnostics, not as the normal route. It
connects as `coffer_service` (BYPASSRLS, for cross-ledger access):

```powershell
$env:COFFER_DB_CONNECTION = "Host=localhost;Port=5432;Database=coffer;Username=coffer_service;Password=$($env:COFFER_SERVICE_PASSWORD)"

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
   to your public domain — set `COFFER_RP_ID` / `COFFER_WEB_ORIGIN_0` to it.
3. **Use it:** ask for reports — e.g. *"top 10 spending categories by month in my
   main ledger"* or *"how are my investments doing?"* The numbers are computed by
   Coffer (RLS-scoped to you), never the model. Read tools can't change anything;
   the write tools (below) stay off unless you deliberately enable them.

**Tools.** Discovery: `list_ledgers`, `list_accounts`, `list_securities`.
Transactions: `transaction_summary` (income/expense/net by category·account·payee,
over time, with category-tree rollup), `list_transactions` (line drill — filter by
account/category/payee/amount/text, sort, page), `net_worth`. Investments:
`holdings_snapshot`, `account_portfolio`, `allocation` (by asset class / region /
vehicle, with multi-asset look-through), `investment_income`, `realized_gains`
(FIFO), `returns` (money-weighted IRR), `price_history`,
`find_in_kind_transfer_candidates`. All read-only, USD.

**AI-assisted writes (ADR-0081, off by default).** For cleanup, MCP can also expose
*write* tools — set / merge / delete / recategorize / convert, plus a bulk
`set_transaction_tags`. They stay off behind two keys: a deployment-wide **AI-writes**
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
│   ├── workflows/                   self-hosted sharded CI — schema + no-raw-sql audit + .NET/web tests + doc links
│   ├── ISSUE_TEMPLATE/
│   └── PULL_REQUEST_TEMPLATE.md
├── Coffer.slnx                       .NET solution
├── global.json                      Pins the .NET SDK version
├── NuGet.config                     Restricts package sources to nuget.org (hermetic builds)
├── docker-compose.yml               postgres 16 + single-container api/spa
├── .env.example                     Placeholder env values (real .env is gitignored)
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

The repository does not currently carry a license. All rights reserved by the
author. The code is hosted privately on GitHub; if it ever goes public, a
license file will be added at that time.
