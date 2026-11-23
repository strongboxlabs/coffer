# 0075 — Self-contained Linux install script

Status: Accepted — amended by [ADR-0094](0094-restore-is-ui-only-and-the-kek-has-no-env-channel.md): the installer no
longer asks "restoring a backup?" and no longer writes a master key into `.env`; amended by
[ADR-0110](0110-upgrade-from-the-app.md): `install.sh` is a callable contract, and a private
fork's tokens may be held on the host.
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
>
> *Amended 2026-11-19:* setup no longer chooses any ledger state. Every install starts on an empty hub, where a Demo is added ([ADR-0115](0115-the-demo-is-generated-imported-and-seeded-from-the-hub.md)). The installer is unaffected.

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

*Amended 2026-10-19 (FU-085):* two tokens, each limited to its job. `COFFER_GH_TOKEN`
fetches, and a fine-grained token with *Contents: read* on the fork is enough; it is used
for the run and stored nowhere. `COFFER_REGISTRY_TOKEN` logs in to ghcr, and `docker
login` stores it on the host, so it is a classic token with `read:packages` and nothing
else (ghcr documents no other kind). The single classic `repo` + `read:packages` token
this used to ask for left, in the host's Docker config, a key to every repository the
token could read. Unset, the login still falls back to `COFFER_GH_TOKEN`, with a warning,
so an existing install upgrades unchanged. The script passes its fetch header from an
owner-only file rather than `-H "Authorization: …"`, which put the token in curl's argv.
The documented bootstrap prompts for both tokens with `read -rsp` and pipes the header
to `curl -H @-`, so neither reaches shell history or a command line.

*Amended 2026-10-19 (FU-094):* an install is one release. `COFFER_VERSION` names it
(unset: the repo's latest release, from the Releases API); the config is fetched from
its tag, and the image is pulled by the digest the release recorded, which the release
workflow uploads as the release's `image-digest.txt` and `install.sh` writes into
`.env` as `COFFER_IMAGE_DIGEST`. Before, the config came from `main` and the image by a
mutable tag, so two installs a day apart could run different code with nothing showing
it. From 0.122.0 a release with no digest is refused; older ones install by tag with a
warning, and `COFFER_VERSION=latest` keeps the old unpinned behaviour on purpose.
Signing (cosign) was considered and not taken: the digest pins what was built, and the
release record lives beside the tag it describes. A weekly `base-refresh` workflow
rebuilds the latest release on fresh base images and replaces its record, so patched
runtimes reach installs between releases.

*Amended 2026-10-20 (0.123.0):* the upgrade is one command, `bash ~/coffer/upgrade.sh
[version]`. Without it, upgrading means fetching that release's `install.sh` by hand, with
a token for a private fork. Changing the tag in `.env` instead skips the volume hand-over
the release's `install.sh` does, and from 0.122.0 the API then refuses to start and the
container restarts in a loop ([ADR-0059](0059-deployment-packaging.md), 2026-10-20
amendment).
- `install.sh` now puts `scripts/upgrade.sh` in the install directory on every run, by
  rename, so the running copy is never overwritten. It also records `COFFER_REPO` in
  `.env`.
- `upgrade.sh` resolves the release (latest unless named), fetches its `install.sh` from
  the tag, and runs it with `COFFER_VERSION` and `COFFER_UPGRADE=1`. That preselects the
  in-place upgrade and refuses to install fresh.
- A repo that answers an anonymous request with 404 is a private fork. `upgrade.sh` then
  takes the two tokens from the environment, or prompts for them.

An install from before 0.123.0 has no `upgrade.sh`, and is upgraded once by hand.

*Amended 2026-10-31 ([ADR-0110](0110-upgrade-from-the-app.md)):* the upgrade can also be started
from the app. A host script, `app-upgrades.sh`, runs from the install user's crontab and runs
`upgrade.sh` for a request the app left in a mailbox volume.
- `install.sh` is a callable contract. `COFFER_UNATTENDED=1` runs an in-place upgrade to the
  named release with no prompt and no sudo. The run checks everything before it changes
  anything (the fetched compose resolves, the image pulls) and exits 10 when that fails with
  nothing changed, 11 on a failure before the first change, 12 after it and 14 when the
  readiness wait times out. `upgrade.sh` adds 13, a fetched `install.sh` without the contract
  marker. `coffer-phase:` lines say where a run is.
- `.env.bak` and `docker-compose.yml.bak` stay one generation, but a run that did not finish no
  longer replaces them, so repeating a failed run keeps the state before the first attempt.
- `install.sh` puts `app-upgrades.sh` in the install directory, installs its cron line when the
  host can run it, and never re-enables a host where it was disabled.
- The two tokens above ("stored nowhere") may now be stored on the host, in `upgrades/secrets/`,
  by `app-upgrades.sh set-token`, for a fork that wants upgrades from the app.

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
job (ADR-0059). Provisioning it here closes that gap; wiring only `Origins__0`
leaves it open.

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
- The installer installs one release, pinned by the image digest it recorded (D1,
  amended 2026-10-19). Until 0.122.0 it tracked `main` and the `:latest` image;
  `COFFER_VERSION=latest` still does, on purpose.
- Upgrades go through `upgrade.sh`, so each one runs the target release's `install.sh`
  (D1, amended 2026-10-20).
