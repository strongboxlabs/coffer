# 0059 — Deployment packaging: single-container API + SPA image

Status: Accepted

## Context

The API ran only via `dotnet run --project src/Api` (dev); `docker-compose.yml`
provisioned Postgres + Redis but nothing for the app itself. There was no
Dockerfile and no real install story. The SPA is served by Vite in dev (which
proxies `/api`), and the API did not serve static files (no `wwwroot`,
no `UseStaticFiles`).

Two shapes were possible: a **single container** where the API serves the built
SPA, or **two containers** (API + an nginx/web container). This also gates the
bootstrap-token CLI, which needs a `docker compose exec api coffer-api …` surface.

## Decision

Ship a **single-container** image (multi-stage `Dockerfile` at the repo root):

1. **web** stage (`node:22-alpine`) — `npm ci` + `npm run build` → SPA `dist/`.
2. **api** stage (`dotnet/sdk:10.0`) — `dotnet publish src/Api/Api.csproj`
   (restores only the API's transitive project refs).
3. **runtime** stage (`dotnet/aspnet:10.0`) — carries the published binary, the
   SPA bundle copied into `wwwroot`, and `db/migrations` at `/app/db/migrations`
   (where `MigrationsDirectoryLocator` finds it by walking up from the binary).
   Listens on `:8080`.

The API serves both surfaces **same-origin**: `UseStaticFiles()` for the bundle
plus a guarded SPA fallback — a non-`/api` path that matches no endpoint returns
`index.html` (client routing); an unmatched `/api` path stays a genuine JSON 404
(never shadowed by the shell). In dev there is no `wwwroot`, so both are no-ops
and Vite keeps serving the SPA.

`docker-compose.yml` gains an `api` service: builds from the Dockerfile,
`depends_on` Postgres healthy (the API runs DbUp migrations itself at startup),
and reads config from env — `COFFER_API__ConnectionString` (coffer_app),
`COFFER_API__ServiceConnectionString` (coffer_service), `COFFER_MASTER_KEK_BASE64`,
and `COFFER_API__Fido2__RpId` / `…__Origins__0`. `.env.example` documents the new
knobs (`API_PORT`, `ASPNETCORE_ENVIRONMENT`, `COFFER_RP_ID`, `COFFER_WEB_ORIGIN_0`).

## Consequences

- One image to pull/deploy; same-origin means no CORS and the cookie + Fido2
  origin config is trivial (one host).
- An SPA change requires an API image rebuild — acceptable for a self-hosted
  single-tenant product; a separate web container can be split out later if
  independent deploy cadence or scale ever demands it.
- Dev is unchanged: `dotnet run` + Vite; the static-file middleware no-ops
  without a `wwwroot`.
- Secrets are env-injected by the operator (Docker secrets / K8s later); the
  API still fails fast when `COFFER_MASTER_KEK_BASE64` or the connection strings
  are absent.
- Unblocks the `ledger-api bootstrap-token` CLI subcommand (the `docker compose
  exec api` surface now exists).

## Amendment, 2026-10-19 — where the port listens, and as whom (FU-084)

**The port.** `"${API_PORT}:8080"` publishes on every interface, IPv4 and IPv6, and
Docker's rules for a published port are evaluated before ufw's and firewalld's, so the
host firewall does not close it. The README called the default "nothing published
beyond it", which was wrong. The default stays: an install reached from a LAN proxy or
another machine keeps working on upgrade. It is now stated plainly in the README,
operations.md and at the end of every `install.sh` run, and `API_BIND` in `.env` closes
it (`127.0.0.1`). The compose line is `"${API_BIND:+${API_BIND}:}${API_PORT:-8080}:8080"`,
which renders the old string exactly when `API_BIND` is unset. The obvious
`${API_BIND:-0.0.0.0}` would have silently dropped the IPv6 listener. Rejected: a
loopback default with an upgrade step writing `API_BIND=0.0.0.0`, and publishing nothing
in favour of a shared Docker network.

**The user.** The runtime stage ends `USER $APP_UID`, the base image's `app` user (uid
1654), and creates `/app/data` and `/app/keys` owned by it, so Docker seeds new volumes
with that ownership. Volumes a root-run version wrote are handed over once by
`scripts/own-api-volumes.sh`, which takes them from the container about to be replaced
with `--volumes-from` and runs `chown` from that container's own image. `install.sh`,
`dev-up-docker.sh` and the restore drill run it before `up`. At boot, `VolumeAccess`
refuses to start on a volume the API cannot use and names the fix, rather than failing
later on an access-denied master key. `RuntimeUserTests` pins all four. A read-only root
filesystem was not taken, because a restore decrypts the whole backup to temp.

## Amendment, 2026-10-20 — the refusal carries the fix itself (0.122.1)

The first production upgrade to 0.122.0 changed the tag in `.env` instead of running
`install.sh`. The API refused to start, as designed, but the fix it named was
`sudo bash scripts/own-api-volumes.sh`, and upgrading.md said the same. That script
reaches a host only with a 0.122.0 or later `install.sh`, so every host that can hit the
refusal is one that does not have it.

The refusal now prints the hand-over as one command for the host:
- a one-off container with `--volumes-from` and the API container's own image, running
  `chown` as root with `CHOWN` and `DAC_READ_SEARCH` only, and no network;
- then a restart.

It names the container by the hostname Docker gives it, the container's short id. A
hostname that isn't one falls back to the compose default, `coffer-api`, and says so.
upgrading.md prints the same command for `coffer-api`. `VolumeAccessTests` holds the two
equal, and holds the refusal to naming no script.
