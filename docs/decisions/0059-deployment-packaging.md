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
