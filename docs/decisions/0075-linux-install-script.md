# 0075 — Self-contained Linux install script

Status: Accepted — amended by [ADR-0094](0094-restore-is-ui-only-and-the-kek-has-no-env-channel.md): the installer no
longer asks "restoring a backup?" and no longer writes a master key into `.env`.
Date: 2026-07-10
Relates: [ADR-0059](0059-deployment-packaging.md), [ADR-0060](0060-whole-db-backup-and-admin-role.md), [ADR-0071](0071-install-provisioning-ui-import-authed-restore.md)

## Context

There was no "stand up Coffer on a fresh Linux host" path. `provision.sh` seeded
ledger *state* (clean/demo) and `scripts/dev-up-docker.sh` builds the dev stack
from source — neither is a public installer. An operator had to hand-write a
`.env`, know the compose layout, and generate secrets themselves.

> `provision.sh` was retired by [ADR-0088](0088-setup-asks-one-question.md);
> ledger state is now chosen on the setup form. Nothing else here changes — this
> ADR's installer never called it.

## Decisions

### D1 — Self-contained, no repo clone

`scripts/install.sh` runs via `bash <(curl -fsSL …/scripts/install.sh)`. It does
NOT assume a repo checkout: it fetches the two host-side files it needs — the
canonical `docker-compose.yml` and `db/init/00-init-roles.sh` (Postgres mints its
roles from it on first start) — from the repo raw URL into an install dir
(`~/coffer`, override `COFFER_DIR`). Fetching the canonical files (rather than
embedding copies in the script) avoids drift. The app itself is the published
image; no source is needed.

**Private fork/image.** The canonical repo and its ghcr image are public and need
no credentials. For a private fork, anonymous `raw.githubusercontent.com` 404s
(and the fork's ghcr package is private too), so when `COFFER_GH_TOKEN` is set the
script fetches its two files via the authenticated GitHub **contents API** (raw
media type) and `docker login`s to ghcr before pulling. Unset ⇒ anonymous raw +
no login (public repo, unchanged). The bootstrap one-liner itself must likewise
be fetched over the API for a private fork — the README shows the exact form.
`COFFER_REPO` / `COFFER_REPO_REF` / `COFFER_GH_USER` override the owner/name, ref,
and ghcr login user.

### D2 — Interactive; localhost or domain only (no bare IP)

Prompts read from `/dev/tty` so piping through `bash` still works. Access options:

1. **This machine only** → `http://localhost:PORT` (`RpId=localhost`) — no TLS.
2. **A domain over HTTPS** → `https://<domain>` (`RpId=<domain>`) — operator
   provides TLS / reverse proxy (out of scope, per ADR-0059).

A **bare IP over http is intentionally not offered** (with an in-script comment
explaining why): Coffer's only login is WebAuthn/passkeys, which require BOTH a
secure context (`https` or `http://localhost`) AND an RpId that is `localhost` or
a real domain. `http://<ip>` satisfies neither, so no passkey could be created
and there would be no way to sign in. The SSH-tunnel-to-localhost trick covers
ad-hoc remote use without a domain.

**MCP (ADR-0063).** The domain path also offers to enable the MCP server and
takes its origin (default `https://mcp.<domain>`), written to `.env` as
`COFFER_MCP_ENABLED` + `COFFER_WEB_ORIGIN_1`. The origin is wired to
`Api:Fido2:Origins__1` (compose) so the MCP OAuth sign-in — which runs on that
host — is an allowed WebAuthn origin; without it, sign-in on an MCP subdomain
fails with an origin mismatch even though `RpId` (the parent domain) already
covers the subdomain. The subdomain's DNS / reverse-proxy stays the operator's
job (ADR-0059). Provisioning it here is the fix for that gap — the first cut only
wired `Origins__0`.

### D3 — Local secret generation + wipe option

Secrets are generated on the host (`openssl`): the master KEK
(`rand -base64 32`) and the DB passwords (`rand -hex 24` — hex can't contain the
`$svc$`/`$app$` dollar-quote tags `db/init` forbids). `.env` is written `chmod
600` with a loud "back up `COFFER_MASTER_KEK_BASE64`" warning. Re-running detects
an existing install and offers **upgrade in place** or **wipe & reinstall**
(`down -v` + typed `wipe` confirm). Image tag defaults to `latest`
(`COFFER_IMAGE_TAG`).

**Restore branch.** A fresh install mints a random KEK, but restoring another
install's data needs THAT install's KEK (a fresh one can't decrypt it, ADR-0014)
— otherwise a manual `.env` key-swap after install. So the config step asks
"restoring a backup?"; if yes it takes the existing `COFFER_MASTER_KEK_BASE64`
(validated as 32-byte base64) + its id (default `v1`, `v2` after a rotation) and
writes those instead of generating fresh. The operator then uses the setup UI's
**Restore from a backup** (ADR-0061) and the data decrypts under the provided KEK
with no post-install swap.

**Privilege model:** run as the normal (non-root) user so `~/coffer` + `.env`
stay user-owned. The script escalates with `sudo` only where required — the
Docker install, and `docker` itself when the user isn't in the `docker` group
(all docker calls go through a `$DOCKER` prefix that becomes `sudo docker` in that
case). Running the whole script under `sudo` is discouraged (it warns) because it
would put the install + secrets in root's home.

## Consequences

- TLS/reverse-proxy stays the operator's responsibility; the installer only
  records the origin/RpId.
- Networked access without a domain isn't supported for passkeys — localhost or a
  hostname is required (documented, with the SSH-tunnel workaround).
- The installer tracks `main` (fetches the canonical compose + init + `:latest`
  image); it isn't version-pinned.
