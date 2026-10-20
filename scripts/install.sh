#!/usr/bin/env bash
#
# Coffer — self-contained Linux installer (ADR-0075).
#
#   Public repo:
#     bash <(curl -fsSL https://raw.githubusercontent.com/strongboxlabs/coffer/main/scripts/install.sh)
#
#   Private fork/image — anonymous raw 404s when the source is not public, so fetch
#   the script itself over the authenticated API and pass the tokens in for its own
#   fetches and the ghcr login. Two tokens, each with only what it is for (FU-085):
#   a fine-grained token with Contents: read on the fork, and a classic token with
#   read:packages and nothing else, which docker login stores on this host. Both are
#   prompted for and the header goes on stdin, so neither reaches shell history or
#   a command line. The canonical repo and image are PUBLIC; this is for a fork:
#     read -rsp 'Repository token: ' T; echo; read -rsp 'Registry token: ' R; echo
#     f=$(mktemp) && printf 'Authorization: Bearer %s\n' "$T" | curl -fsSL -H @- \
#       -H "Accept: application/vnd.github.raw" -o "$f" \
#       "https://api.github.com/repos/<owner>/<repo>/contents/scripts/install.sh?ref=main" \
#       && COFFER_GH_TOKEN=$T COFFER_REGISTRY_TOKEN=$R COFFER_REPO=<owner>/<repo> bash "$f"
#     rm -f "$f"; unset T R
#
#   Upgrading: fetch this script AGAIN, from the release you are moving to, and set
#   COFFER_VERSION=<version>. An old copy does not know that release's upgrade steps.
#   docs/upgrading.md has the commands.
#
#   Every install is one release: the config from that release's tag, and the image
#   by the digest the release recorded when it was built (FU-094).
#
# Stands up Coffer (app + Postgres) on a fresh Linux host via Docker Compose:
# checks/installs Docker, fetches the canonical docker-compose.yml + db/init from
# the repo (no clone needed), generates a .env with fresh random secrets, pulls
# the published image, and starts it. Interactive, re-runnable, and offers a
# wipe-and-reinstall. TLS / reverse proxy is out of scope — for a networked
# (https) install you front it with your own proxy or tunnel.
#
# Overridable via env: COFFER_DIR (install dir, default ~/coffer),
# COFFER_VERSION (the release, default the latest; `latest` for unpinned main),
# COFFER_GH_TOKEN (private fork config), COFFER_REGISTRY_TOKEN (private image),
# COFFER_REPO (owner/name, default strongboxlabs/coffer), COFFER_REPO_REF (default the
# release's tag), COFFER_GH_USER (ghcr login user, default the repo owner),
# COFFER_REPO_RAW.
#
set -euo pipefail

REPO="${COFFER_REPO:-strongboxlabs/coffer}"
GH_API="${COFFER_GH_API:-https://api.github.com}"
# Optional tokens for a PRIVATE fork. Not needed for the canonical source, which is
# public and pulls anonymously; leave both unset for it.
#
# COFFER_GH_TOKEN fetches the config files through the authenticated GitHub contents
# API (anonymous raw 404s on a private fork). A fine-grained token with Contents: read
# on that one repository is enough. It is used for this run only and stored nowhere.
GH_TOKEN="${COFFER_GH_TOKEN:-}"
# COFFER_REGISTRY_TOKEN logs in to ghcr before pulling, and `docker login` keeps it in
# the Docker client's config.json on this host. So it is a classic token with
# read:packages and nothing else; ghcr documents only classic tokens (FU-085). Unset,
# the login falls back to COFFER_GH_TOKEN, which is how an install from before the
# split worked: one classic token with `repo` + `read:packages`, stored where whoever
# reads that file can read every repository the token can.
REGISTRY_TOKEN="${COFFER_REGISTRY_TOKEN:-$GH_TOKEN}"
INSTALL_DIR="${COFFER_DIR:-$HOME/coffer}"
# The image is derived from the SAME repo this script fetches its config from,
# because .github/workflows/release.yml publishes to
# ghcr.io/${{ github.repository_owner }}/coffer — so whoever built the image owns
# the package. Deriving it means the config and the image cannot come from
# different accounts, which is exactly what happened when compose defaulted to a
# hardcoded owner: a private install pulled a public package. Override with
# COFFER_IMAGE for a mirror or a private registry.
IMAGE="${COFFER_IMAGE:-ghcr.io/${REPO%%/*}/coffer}"
TTY_DEV=/dev/tty

info() { printf '\033[36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[!]\033[0m %s\n' "$*"; }
die()  { printf '\033[31m[x]\033[0m %s\n' "$*" >&2; exit 1; }

# fetch REPO-RELATIVE-PATH DEST — private-repo aware. With COFFER_GH_TOKEN, pull
# from the authenticated GitHub contents API (raw media type); without a token,
# anonymous raw (public repos — unchanged).
#
# The token reaches curl in a header FILE, owner-only and removed on exit: as
# `-H "Authorization: Bearer $GH_TOKEN"` it was in curl's argv, which anyone listing
# this host's processes can read while a fetch runs (FU-085).
auth_header=""
if [ -n "$GH_TOKEN" ]; then
    auth_header="$(umask 077 && mktemp)"
    trap 'rm -f "$auth_header"' EXIT
    printf 'Authorization: Bearer %s\n' "$GH_TOKEN" >"$auth_header"
fi
fetch() {
    local path=$1 dest=$2
    if [ -n "$GH_TOKEN" ]; then
        curl -fsSL -H @"$auth_header" \
                   -H "Accept: application/vnd.github.raw" \
             "$GH_API/repos/$REPO/contents/$path?ref=$REPO_REF" -o "$dest"
    else
        curl -fsSL "$REPO_RAW/$path" -o "$dest"
    fi
}

# ------------------------------------------------------------------ which release
# An install is ONE release (FU-094): this script's config from the release's tag, and
# the image by the digest the release recorded when it was built. Both used to float:
# the config came from `main`, and the image by a tag that anyone who can push to the
# registry can move, so two installs a day apart could get different code and nothing
# would show it.
#
# COFFER_VERSION picks the release (x.y.z); unset, it is the repo's latest. An older
# instruction's COFFER_IMAGE_TAG is read as the same thing. COFFER_VERSION=latest opts
# out: config from main and the :latest image, pinned to nothing, as every install
# was before.
#
# Releases before 0.122.0 recorded no digest, so they install by tag. From 0.122.0 a
# release without one is refused: the release workflow records it on every release,
# and its absence means that workflow did not run as it should.
FIRST_RECORDED_RELEASE=0.122.0

gh_api() {  # gh_api API-PATH [ACCEPT] — the response on stdout
    if [ -n "$GH_TOKEN" ]; then
        curl -fsSL -H @"$auth_header" -H "Accept: ${2:-application/vnd.github+json}" "$1"
    else
        curl -fsSL -H "Accept: ${2:-application/vnd.github+json}" "$1"
    fi
}

version_before() {  # version_before A B — A sorts strictly before B
    [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -1)" = "$1" ]
}

# The digest release TAG recorded as its image-digest.txt asset, given the release's
# JSON; empty when it has none. Read through the API either way: a private fork's
# release assets are not public URLs. (curl drops the Authorization header on the
# redirect to the asset's storage host.)
release_digest() {
    local json=$2 asset digest
    [ -n "$json" ] || return 0
    # An asset's "url" comes before its "name"; the release's own "url" is not an asset's.
    asset="$(printf '%s\n' "$json" | awk '
        /"url": *"[^"]*\/releases\/assets\/[0-9]+"/ { u = $2; gsub(/[",]/, "", u) }
        /"name": *"image-digest\.txt"/            { print u; exit }')"
    [ -n "$asset" ] || return 0
    digest="$(gh_api "$asset" application/octet-stream | tr -d '[:space:]')" \
        || die "Could not read the image digest release $1 records."
    printf '%s' "$digest" | grep -qE '^sha256:[0-9a-f]{64}$' \
        || die "Release $1 records '$digest' as its image digest, which is not one. Nothing was changed."
    printf '%s' "$digest"
}

VERSION="${COFFER_VERSION:-${COFFER_IMAGE_TAG:-}}"
VERSION="${VERSION#v}"
IMAGE_DIGEST=""
if [ "$VERSION" = latest ]; then
    IMAGE_TAG=latest
    REPO_REF="${COFFER_REPO_REF:-main}"
    warn "COFFER_VERSION=latest: the config from $REPO_REF and the :latest image, pinned to nothing."
else
    if [ -z "$VERSION" ]; then
        VERSION="$(gh_api "$GH_API/repos/$REPO/releases/latest" 2>/dev/null \
            | sed -n 's/^  "tag_name": *"v\{0,1\}\([^"]*\)".*/\1/p' | head -1 || true)"
        [ -n "$VERSION" ] || die "Could not find the latest release of $REPO.
    Name one with COFFER_VERSION=<x.y.z>, or COFFER_VERSION=latest for the unpinned
    main branch and :latest image."
    fi
    printf '%s' "$VERSION" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+$' \
        || die "COFFER_VERSION='$VERSION' is not a release (x.y.z), or 'latest'."
    IMAGE_TAG="$VERSION"
    REPO_REF="${COFFER_REPO_REF:-v$VERSION}"
    release_json="$(gh_api "$GH_API/repos/$REPO/releases/tags/v$VERSION" 2>/dev/null || true)"
    IMAGE_DIGEST="$(release_digest "v$VERSION" "$release_json")"
    if [ -z "$IMAGE_DIGEST" ]; then
        if ! version_before "$VERSION" "$FIRST_RECORDED_RELEASE"; then
            [ -n "$release_json" ] || die "$REPO has no release v$VERSION. Check COFFER_VERSION. Nothing was changed."
            die "Release v$VERSION of $REPO records no image digest, so there is nothing to pin the image
    to. Every release from $FIRST_RECORDED_RELEASE records one; this one's release workflow did
    not finish. Nothing was changed."
        fi
        # Older releases have no digest, and on a private fork often no GitHub Release
        # at all: only the tag, which the config fetch below needs.
        warn "Release v$VERSION predates image digests ($FIRST_RECORDED_RELEASE); its image is pulled by tag."
    fi
fi
REPO_RAW="${COFFER_REPO_RAW:-https://raw.githubusercontent.com/$REPO/$REPO_REF}"
info "Coffer $VERSION: config from $REPO@$REPO_REF${IMAGE_DIGEST:+, image pinned to ${IMAGE_DIGEST:0:19}…}."

# Prompts must read from the terminal, not stdin — stdin is the script itself
# when run as `bash <(curl …)` / `curl … | bash`.
[ -e "$TTY_DEV" ] || die "This installer is interactive — run it on a terminal, e.g. 'bash <(curl -fsSL $REPO_RAW/scripts/install.sh)'."

ask() {  # ask VAR "prompt" "default"
    local __var=$1 __prompt=$2 __def=${3:-} __ans
    if [ -n "$__def" ]; then printf '%s [%s]: ' "$__prompt" "$__def"
    else printf '%s: ' "$__prompt"; fi
    IFS= read -r __ans <"$TTY_DEV" || __ans=
    printf -v "$__var" '%s' "${__ans:-$__def}"
}
yesno() { local __a; printf '%s [y/N]: ' "$1"; IFS= read -r __a <"$TTY_DEV" || __a=; [ "$__a" = y ] || [ "$__a" = Y ]; }

# free_port FIRST — echo the first free TCP port at or above FIRST (up to +20).
#
# Why this exists: a second install on the same host collides on PUBLISHED PORTS, the
# same way it used to collide on container names. Compose scopes names and volumes per
# project but a host port is global, so a dev stack or a restore drill already holding
# 8080/5432 makes `up` fail with "port is already allocated" AFTER the install has
# written its config — which reads like a Coffer bug rather than an occupied port.
#
# bash's /dev/tcp probe rather than ss/lsof/netstat: those are variously absent on a
# minimal Ubuntu, differently-flagged on macOS, and one more thing to require. A
# successful connect means something is listening, so the port is taken.
free_port() {
    local candidate=$1 limit=$(( $1 + 20 ))
    while [ "$candidate" -lt "$limit" ]; do
        if ! (exec 3<>"/dev/tcp/127.0.0.1/$candidate") 2>/dev/null; then
            printf '%s' "$candidate"; return 0
        fi
        exec 3>&- 2>/dev/null || true
        candidate=$(( candidate + 1 ))
    done
    printf '%s' "$1"   # nothing free nearby: keep the default and let compose say so
}

# ------------------------------------------------------------------ privileges
# Run this as your NORMAL user, not `sudo bash …`: under sudo $HOME becomes
# root's, so ~/coffer + your secrets would land in /root, root-owned. We escalate with sudo only where it's actually needed —
# installing Docker, and docker itself when you're not in the 'docker' group.
if [ "$(id -u)" -eq 0 ]; then
    SUDO=""
    warn "Running as root — files go to $INSTALL_DIR (root's home unless COFFER_DIR is set). For a user-owned install, run as your normal user instead."
elif command -v sudo >/dev/null 2>&1; then
    SUDO="sudo"
else
    SUDO=""
fi

# ---------------------------------------------------------------- environment
# Three supported places to run this, and they differ ONLY in how Docker gets
# there. Everything after this point is identical, which is why this is a
# detection step rather than three scripts.
#
#   linux  — native. We can install Docker via get.docker.com.
#   wsl    — WSL2 on Windows. Docker comes from Docker Desktop on the WINDOWS
#            side via WSL integration; running get.docker.com in here would
#            install a second daemon that fights the first.
#   macos  — Docker Desktop, installed by hand (get.docker.com has no macOS
#            path). Homebrew could do it, but silently installing a GUI app that
#            wants privileges is not something a curl-to-bash should decide.
case "$(uname -s)" in
    Darwin) PLATFORM=macos ;;
    Linux)
        # WSL_DISTRO_NAME is set by WSL itself; /proc/version is the fallback for
        # a non-login shell where the environment hasn't been populated.
        if [ -n "${WSL_DISTRO_NAME:-}" ] || grep -qi microsoft /proc/version 2>/dev/null; then
            PLATFORM=wsl
        else
            PLATFORM=linux
        fi ;;
    *) die "Unsupported platform: $(uname -s). Coffer installs on Linux, WSL2 (Windows) or macOS." ;;
esac
info "Platform: $PLATFORM"

# On WSL, refuse to install onto the Windows filesystem. /mnt/c is a 9p mount:
# the exec bit on db/init/00-init-roles.sh does not survive it, so Postgres
# silently skips the role-init script and the API then fails to connect with a
# "role does not exist" that points nowhere near the cause. It is also markedly
# slower. The Linux-side home directory has neither problem.
if [ "$PLATFORM" = wsl ]; then
    case "$INSTALL_DIR" in
        /mnt/*) die "On WSL, install inside the Linux filesystem, not $INSTALL_DIR.
    A /mnt/... path drops the exec bit on db/init/00-init-roles.sh, so the database
    roles never get created and the API fails to connect. Use the default (~/coffer)
    or set COFFER_DIR to another path under \$HOME." ;;
    esac
fi

# --------------------------------------------------------------------- Docker
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    info "Docker + Compose present."
elif [ "$PLATFORM" = linux ]; then
    warn "Docker (with the Compose plugin) was not found."
    if yesno "Install Docker now via https://get.docker.com${SUDO:+ (uses sudo)}?"; then
        curl -fsSL https://get.docker.com | ${SUDO:+$SUDO }sh || die "Docker install failed."
        info "Docker installed."
    else
        die "Docker is required. Install it and re-run."
    fi
elif [ "$PLATFORM" = wsl ]; then
    die "Docker isn't reachable from this WSL distro.
    Install Docker Desktop for Windows, then in Docker Desktop:
      Settings -> General  : tick 'Use the WSL 2 based engine'
      Settings -> Resources -> WSL integration : enable '${WSL_DISTRO_NAME:-this distro}'
    Then re-run this script. Do NOT install Docker inside WSL separately — you
    would end up with two daemons and containers you can't see from Windows."
else
    die "Docker isn't installed.
    Install Docker Desktop for Mac (https://docs.docker.com/desktop/install/mac-install/),
    start it, wait for the whale icon to settle, then re-run this script."
fi

# Daemon access: prefer running docker WITHOUT sudo; fall back to sudo (e.g. a
# fresh install where your user isn't in the 'docker' group yet — that needs a
# re-login to take effect). Every docker call below goes through $DOCKER.
DOCKER="docker"
if ! docker info >/dev/null 2>&1; then
    if [ -n "$SUDO" ] && $SUDO docker info >/dev/null 2>&1; then
        DOCKER="$SUDO docker"
        warn "Using 'sudo docker' — your user isn't in the 'docker' group yet. To drop the sudo later: 'sudo usermod -aG docker $USER', then log out/in."
    elif [ "$PLATFORM" = linux ]; then
        die "Can't reach the Docker daemon. Start it ('sudo systemctl start docker') or add your user to the 'docker' group (log out/in), then re-run."
    else
        die "Can't reach the Docker daemon. Start Docker Desktop, wait for it to report
    'running', then re-run. (On WSL, also check Settings -> Resources -> WSL
    integration has '${WSL_DISTRO_NAME:-this distro}' enabled.)"
    fi
fi
command -v openssl >/dev/null 2>&1 || die "openssl is required (secret generation). Install it and re-run."
command -v curl    >/dev/null 2>&1 || die "curl is required. Install it and re-run."

# --------------------------------------------------------------- existing install
MODE=fresh
if [ "${COFFER_UPGRADE:-}" = 1 ]; then
    # upgrade.sh: an in-place upgrade, already chosen. Never a fresh install by accident.
    [ -f "$INSTALL_DIR/.env" ] || die "COFFER_UPGRADE=1, but $INSTALL_DIR has no install to upgrade."
    info "Upgrading the install at $INSTALL_DIR in place."
    MODE=upgrade
elif [ -f "$INSTALL_DIR/.env" ]; then
    warn "An existing Coffer install was found at $INSTALL_DIR."
    echo "  1) Upgrade in place  — keep your data, pull the latest image, restart"
    echo "  2) Wipe & reinstall  — DELETE all data + config, start fresh"
    echo "  3) Cancel"
    ask choice "Choose" "1"
    case "$choice" in
        1) MODE=upgrade ;;
        2) warn "This permanently deletes the Coffer database volume, $INSTALL_DIR/.env and $INSTALL_DIR/secrets/."
           ask wipe_confirm "Type 'wipe' to confirm" ""
           [ "$wipe_confirm" = wipe ] || die "Not confirmed — aborting."
           ( cd "$INSTALL_DIR" && $DOCKER compose down -v ) 2>/dev/null || true
           rm -f "$INSTALL_DIR/.env"
           # The role passwords live here now. Leaving them behind would hand the
           # fresh install the OLD passwords for a database that no longer exists,
           # which fails at first connection rather than obviously.
           rm -rf "$INSTALL_DIR/secrets"
           MODE=fresh ;;
        *) die "Cancelled." ;;
    esac
fi

# ------------------------------------------------- foreign-volume guard (fresh only)
#
# Compose derives its project name from the install DIRECTORY, and volumes are named
# <project>_postgres_data / <project>_coffer_data / <project>_coffer_keys. Two different installs whose
# directories share a basename therefore share volumes on the same Docker engine — and
# that is not hypothetical: a WSL install at ~/coffer met the volumes a Windows checkout
# of .../Coffer created weeks earlier, because Docker Desktop shares one engine across
# both.
#
# The failure that produces is genuinely hard to read. Postgres runs db/init only on an
# EMPTY data directory, so the adopted database keeps the other install's role passwords
# while this .env has freshly generated ones. The API then crash-loops on
# `28P01 password authentication failed for user "coffer_service"` — an authentication
# error that says nothing about the actual cause, three minutes after the install
# appeared to be going fine.
#
# So: on a FRESH install, if those volumes already exist, stop before touching anything.
container_prefix="$(basename "$INSTALL_DIR" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9_-')"
[ -n "$container_prefix" ] || container_prefix=coffer

if [ "$MODE" = fresh ]; then
    existing_vols=''
    # coffer_keys too: a fresh install that met another install's key volume would run
    # under that install's master key.
    for v in "${container_prefix}_postgres_data" "${container_prefix}_coffer_data" "${container_prefix}_coffer_keys"; do
        $DOCKER volume inspect "$v" >/dev/null 2>&1 && existing_vols="$existing_vols $v"
    done
    if [ -n "$existing_vols" ]; then
        warn "Docker already has volumes for the compose project '$container_prefix':"
        for v in $existing_vols; do echo "      $v"; done
        die "A fresh install here would REUSE that data instead of creating its own.
    Postgres only initialises roles on an empty data directory, so it would keep the
    other install's passwords while this one generates new ones — the API then fails
    with '28P01 password authentication failed'.

    Pick one:
      * Install somewhere else, which changes the project name and gets its own volumes:
            COFFER_DIR=\$HOME/coffer-$(date +%m%d) bash \$0
      * If those volumes are genuinely disposable, remove them first:
            $DOCKER volume rm$existing_vols
      * If this IS your install, keep its .env and secrets/ so re-running offers
        'upgrade in place' or 'wipe & reinstall' rather than treating it as fresh.

    Nothing was changed."
    fi
fi

# --------------------------------------------------------- fetch canonical files
#
# A token means the operator is installing from a PRIVATE fork — but REPO still
# defaults to the public mirror, so without COFFER_REPO the script fetches its
# own config from a different repository than it came from. That combination
# shipped a stale public compose onto a private-fork host, which then failed
# interpolation halfway through an upgrade. It is legal (a public repo with a
# private image is a real case), so this warns rather than refuses.
if [ -n "$GH_TOKEN" ] && [ "$REPO" = "strongboxlabs/coffer" ]; then
    warn "Fetching config from the PUBLIC repo ($REPO) while using an auth token."
    warn "  Installing from a private fork? Re-run with COFFER_REPO=<owner>/<repo>,"
    warn "  or the compose file you get will be the public mirror's, not yours."
fi

mkdir -p "$INSTALL_DIR/db/init" "$INSTALL_DIR/scripts"

# Keep the outgoing compose recoverable: it is about to be replaced, it may carry
# local edits, and it is the rollback if the new one turns out to be wrong for
# this host.
compose_backup=""
if [ -f "$INSTALL_DIR/docker-compose.yml" ]; then
    compose_backup="$INSTALL_DIR/docker-compose.yml.bak"
    cp -a "$INSTALL_DIR/docker-compose.yml" "$compose_backup"
fi

info "Fetching docker-compose.yml + db/init from the repo …"
fetch docker-compose.yml       "$INSTALL_DIR/docker-compose.yml"       || die "Could not fetch docker-compose.yml (private fork? set COFFER_GH_TOKEN)."
fetch db/init/00-init-roles.sh "$INSTALL_DIR/db/init/00-init-roles.sh" || die "Could not fetch db/init/00-init-roles.sh (private fork? set COFFER_GH_TOKEN)."
chmod +x "$INSTALL_DIR/db/init/00-init-roles.sh"

# Ship the pg_hba remediation to the host. It only applies to installs created
# before scram became the initdb default, and it cannot be run from a repo the
# host doesn't have — which is every install.sh host, since only compose and
# db/init land here. Best-effort: an older ref may not carry it.
fetch scripts/harden-pg-hba.sh "$INSTALL_DIR/scripts/harden-pg-hba.sh" 2>/dev/null \
    && chmod +x "$INSTALL_DIR/scripts/harden-pg-hba.sh" || true

# The grants a restore-by-swap needs (ADR-0061, amended 2026-10-09). Fresh installs
# get them from db/init; an existing install gets them here, after `up`. Not
# best-effort: without it, restoring a backup is refused.
fetch scripts/provision-restore-privileges.sh "$INSTALL_DIR/scripts/provision-restore-privileges.sh" \
    || die "Could not fetch scripts/provision-restore-privileges.sh (private fork? set COFFER_GH_TOKEN)."
chmod +x "$INSTALL_DIR/scripts/provision-restore-privileges.sh"

# From 0.122.0 the API runs unprivileged (FU-084), and an install upgraded from an earlier
# version has volumes that root wrote. Not best-effort: the new version cannot read its
# own master key until they are handed over. A release before 0.122.0 has no such script
# and needs none, since its image runs as root.
if fetch scripts/own-api-volumes.sh "$INSTALL_DIR/scripts/own-api-volumes.sh"; then
    chmod +x "$INSTALL_DIR/scripts/own-api-volumes.sh"
elif [ "$VERSION" != latest ] && version_before "$VERSION" 0.122.0; then
    rm -f "$INSTALL_DIR/scripts/own-api-volumes.sh"
else
    die "Could not fetch scripts/own-api-volumes.sh (private fork? set COFFER_GH_TOKEN)."
fi

# The upgrade command (bash ~/coffer/upgrade.sh), replaced on every run so it is always
# the current release's. By rename, never in place: this run may be its child, and bash
# reads a running script as it goes, so overwriting the file would corrupt that run. A
# release before 0.123.0 has none, and whatever copy is here stays.
if fetch scripts/upgrade.sh "$INSTALL_DIR/upgrade.sh.new"; then
    chmod +x "$INSTALL_DIR/upgrade.sh.new"
    mv -f "$INSTALL_DIR/upgrade.sh.new" "$INSTALL_DIR/upgrade.sh"
else
    rm -f "$INSTALL_DIR/upgrade.sh.new"
    if [ "$VERSION" = latest ] || ! version_before "$VERSION" 0.123.0; then
        die "Could not fetch scripts/upgrade.sh (private fork? set COFFER_GH_TOKEN)."
    fi
fi

# Does the compose file interpolate against this host's .env? Every mutation
# below is gated on this, because the failure it catches is the expensive one:
# a half-applied upgrade on a live host.
compose_resolves() { ( cd "$INSTALL_DIR" && $DOCKER compose config -q >/dev/null 2>&1 ); }

restore_compose() {
    [ -n "$compose_backup" ] && [ -f "$compose_backup" ] || return 0
    cp -a "$compose_backup" "$INSTALL_DIR/docker-compose.yml"
    warn "Restored the previous docker-compose.yml."
}

# ---------------------------------------------------------- config (fresh only)
if [ "$MODE" = fresh ]; then
    echo ""
    echo "How will you reach Coffer?"
    echo "  1) This machine only  — http://localhost (no TLS needed)"
    echo "  2) A domain over HTTPS — https://<domain> (you provide the TLS / reverse proxy)"
    #
    # A bare IP over http is deliberately NOT offered. Coffer's only login is
    # WebAuthn / passkeys, which require BOTH a secure context (https or
    # http://localhost) AND an RpId that is 'localhost' or a real domain. A
    # http://<ip> URL satisfies neither, so a passkey could never be created and
    # there would be no way to sign in. Use localhost for a single box, a domain
    # for networked access, or an SSH tunnel to localhost for ad-hoc remote use
    # (ssh -L 8080:localhost:8080 <host>, then browse http://localhost:8080).
    #
    ask mode "Choose" "1"
    if [ "$mode" = 2 ]; then
        ask domain "Domain (e.g. coffer.example.com)" ""
        [ -n "$domain" ] || die "A domain is required for option 2."
        ask port "Host port to publish the app on" "8080"
        web_origin="https://$domain"; rp_id="$domain"
        # MCP server (ADR-0063) — OAuth-gated read/report access for AI clients
        # (Claude, etc.). Its sign-in runs on the MCP origin, which MUST be an
        # allowed passkey origin (Fido2 Origins__1) or sign-in there fails with an
        # origin mismatch. Provision it here so a subdomain install works as-is.
        mcp_enabled=false; mcp_origin="$web_origin"
        echo ""
        if yesno "Enable the MCP server for AI clients (Claude, etc.)"; then
            mcp_enabled=true
            info "A dedicated grey-cloud (DNS-only) subdomain is recommended so the"
            info "main host's bot-protection can't block MCP clients (ADR-0063)."
            ask mcp_origin "MCP origin URL (the host MCP clients reach)" "https://mcp.$domain"
        fi
    else
        api_default="$(free_port 8080)"
        [ "$api_default" = 8080 ] ||             warn "8080 is in use on this host — suggesting $api_default instead."
        ask port "Port" "$api_default"
        web_origin="http://localhost:$port"; rp_id="localhost"
        mcp_enabled=false; mcp_origin="$web_origin"
    fi

    # No master KEK is written here. The API mints its own on first boot when the
    # database holds no wrapped material (ADR-0092 D3) and writes it to the key file
    # on the coffer_keys volume, which is the single source of truth; the setup
    # ceremony then shows it so the operator can back it up.
    #
    # The COFFER_MASTER_KEK_BASE64 prompt this replaced (ADR-0094) was wrong twice
    # over. It seeded a deprecated env var readable via `docker inspect` and
    # /proc/<pid>/environ, which went stale the moment anyone rotated from the UI --
    # so the value an operator was told to back up became the wrong one silently. And
    # for a RESTORE it took the source key at the one moment nothing could check it:
    # no archive is present at install time, so a typo or a wrong-era key was accepted
    # here and only discovered after the restore had replaced everything. The restore
    # form takes that key instead (ADR-0092 D4) and validates it at upload against the
    # archive's KEK fingerprint, refusing before anything destructive runs.

    # Database role passwords go to FILES, not .env — an env var is readable via
    # `docker inspect`, /proc/<pid>/environ, child environments and crash dumps,
    # and these authenticate every query the app makes. docker-compose mounts
    # them as secrets at /run/secrets/. Same reasoning ADR-0092 D1 applied to the
    # master KEK.
    #
    # The directory is 0700 so other local users can't traverse in; the files
    # themselves are 0644 because compose (outside swarm) keeps host ownership
    # and the Postgres entrypoint re-execs as uid 999 before reading
    # POSTGRES_PASSWORD_FILE — a 0600 file owned by the installing user is not
    # readable in-container.
    info "Generating database role passwords into $INSTALL_DIR/secrets/"
    mkdir -p "$INSTALL_DIR/secrets"
    chmod 700 "$INSTALL_DIR/secrets"
    for secret in postgres_password coffer_service_password coffer_app_password; do
        if [ -s "$INSTALL_DIR/secrets/$secret" ]; then
            info "  $secret already exists — keeping it."
        else
            printf '%s' "$(openssl rand -hex 24)" >"$INSTALL_DIR/secrets/$secret"
            chmod 644 "$INSTALL_DIR/secrets/$secret"
        fi
    done

    # Compose lowercases and strips the project name it derives from the directory;
    # mirror that so the prefix and the project agree.
    info "Generating secrets and writing $INSTALL_DIR/.env"
    umask 077
    cat >"$INSTALL_DIR/.env" <<EOF
# Coffer — generated by install.sh (ADR-0075) on $(date -u +%Y-%m-%dT%H:%M:%SZ).
# These secrets are unique to THIS install.
# The master key is NOT here (ADR-0094): the API mints it on first boot into its own
# file on the coffer_keys volume, and the setup ceremony shows it once so you can back
# it up. Read it later from System -> Encryption -> Show key.
#
# The three database role passwords are NOT here — they live in ./secrets/ and
# reach the containers as docker-compose secrets. See docker-compose.yml.
POSTGRES_USER=coffer
POSTGRES_DB=coffer
# Postgres is NOT published to the host by default: the API reaches it over the compose
# network, and an unpublished port is one less listening socket and one less thing to
# collide with a second install. For ad-hoc SQL you do not need it published at all:
#   docker compose exec postgres psql -U coffer -d coffer
# To publish it for a GUI client (pgAdmin, DataGrip), which cannot reach into a
# container, add the dev overlay and a port — see .env.example in the repo. Nothing
# here needs it.
ASPNETCORE_ENVIRONMENT=Production
API_PORT=$port
# Where that port listens. Unset, it is every interface of this machine, so anything
# that can route here reaches Coffer, and Docker's port rules bypass ufw and firewalld.
# Set 127.0.0.1 when a reverse proxy or tunnel on this machine is the only way in.
#API_BIND=127.0.0.1
# Container names are prefixed with this so a second install on the same Docker
# engine (a restore drill, a dev stack beside a real one) doesn't collide on a
# global container name while its volumes and network are properly scoped. Derived
# from the install directory, which is also what Compose derives the project name
# from — so ~/coffer keeps the historical coffer-api / coffer-postgres exactly.
COFFER_CONTAINER_PREFIX=$container_prefix
COFFER_IMAGE=$IMAGE
COFFER_IMAGE_TAG=$IMAGE_TAG
# The digest that release recorded for its image (FU-094). With it set, compose pulls
# exactly that image whatever the tag now points at; install.sh writes both on every
# run. Empty means unpinned.
COFFER_IMAGE_DIGEST=$IMAGE_DIGEST
COFFER_RP_ID=$rp_id
# Where Coffer is reached. Both are allowed passkey origins (Fido2 Origins__0 /
# __1 in docker-compose.yml); COFFER_MCP_URL additionally tells the admin UI what
# address to hand an MCP client. When MCP shares the main host these are the same
# value and dedupe at runtime. Re-run 'docker compose up -d' after changes.
#
# The older COFFER_WEB_ORIGIN_0 / _1 still work — compose falls back to them — but
# they name a slot rather than a role, and the slot's meaning was only ever a
# convention in a comment. For a third allowed origin, add COFFER_WEB_ORIGIN_2
# here plus a matching Origins__2.
COFFER_WEB_URL=$web_origin
COFFER_MCP_URL=$mcp_origin
# MCP server (ADR-0063): OAuth-gated read/report access for AI clients (Claude,
# etc.), off unless enabled. It signs in on COFFER_MCP_URL above.
COFFER_MCP_ENABLED=$mcp_enabled
EOF
    chmod 600 "$INSTALL_DIR/.env"
    info "The master key is minted by the app on first boot, not written here."
    info "  Setup hands it to you on the welcome screen; keep a copy (System →"
    info "  Encryption → Show key reads it again). Without it, bank-feed tokens, the"
    info "  stored backup passphrase and the Drive connection don't survive a move to"
    info "  another install — your ledgers and passkeys do not depend on it."
fi

# COFFER_IMAGE is REQUIRED by the compose file fetched above (it has no default —
# a default would be some specific owner's package, wrong for every other fork).
# An install created before that carries no such line, so backfill it from the
# repo this run fetched from. Without this the upgrade would fail interpolation on
# a variable the operator has never heard of.
if [ "$MODE" = upgrade ] && ! grep -qE '^COFFER_IMAGE=' "$INSTALL_DIR/.env"; then
    info "Recording the image ($IMAGE) in .env — compose now requires it explicitly."
    printf '%s\n' "COFFER_IMAGE=$IMAGE" >>"$INSTALL_DIR/.env"
fi

# The release this run installs (FU-094), so that every later compose command, not
# only this run's, uses that image and no other. Replaced where present, added where not.
set_env() {
    if grep -qE "^$1=" "$INSTALL_DIR/.env"; then
        sed -i.tmp -E "s|^$1=.*|$1=$2|" "$INSTALL_DIR/.env" && rm -f "$INSTALL_DIR/.env.tmp"
    else
        printf '%s\n' "$1=$2" >>"$INSTALL_DIR/.env"
    fi
}
if [ "$MODE" = upgrade ]; then
    set_env COFFER_IMAGE_TAG "$IMAGE_TAG"
    set_env COFFER_IMAGE_DIGEST "$IMAGE_DIGEST"
fi
# Where this install's releases come from, so upgrade.sh fetches the next one there.
set_env COFFER_REPO "$REPO"

# An install created before the role passwords moved into files has them in .env
# and no secrets/ directory. The compose file fetched above passes only the
# *_FILE form, so `up` would fail on the missing secret. Move them across, using
# the values the database already knows — this must not rotate anything.
#
# Idempotent, and it runs on the upgrade path specifically: MODE=fresh already
# generated the files above.
if [ "$MODE" = upgrade ] && [ ! -s "$INSTALL_DIR/secrets/coffer_app_password" ]; then
    info "Moving database role passwords out of .env into $INSTALL_DIR/secrets/ …"
    mkdir -p "$INSTALL_DIR/secrets"
    chmod 700 "$INSTALL_DIR/secrets"
    migrated_any=
    for pair in "POSTGRES_PASSWORD:postgres_password" \
                "COFFER_SERVICE_PASSWORD:coffer_service_password" \
                "COFFER_APP_PASSWORD:coffer_app_password"; do
        var="${pair%%:*}"; file="${pair##*:}"
        [ -s "$INSTALL_DIR/secrets/$file" ] && continue
        value="$(grep -E "^${var}=" "$INSTALL_DIR/.env" | head -1 | cut -d= -f2- || true)"
        [ -n "$value" ] || die "$var is not in $INSTALL_DIR/.env and $INSTALL_DIR/secrets/$file does not exist — cannot upgrade without it."
        printf '%s' "$value" >"$INSTALL_DIR/secrets/$file"
        chmod 644 "$INSTALL_DIR/secrets/$file"
        migrated_any=1
    done
    if [ -n "$migrated_any" ]; then
        # ORDER MATTERS. Writing secrets/ is additive — the old compose ignores
        # those files, so a host that stops here is still exactly as it was.
        # Editing .env is NOT reversible in the same way: it is what the old
        # compose reads, so commenting those lines out while the fetched compose
        # turns out to be incompatible strands the install with neither source of
        # passwords. That is precisely what happened on a real upgrade — the
        # passwords moved, then interpolation failed, and the host was left
        # half-migrated with `docker compose` unusable.
        #
        # So: prove the new compose resolves FIRST, and only then take the old
        # values away.
        if ! compose_resolves; then
            restore_compose
            info "Left $INSTALL_DIR/.env untouched — your passwords are still there."
            info "secrets/ was written but nothing reads it yet, so this install is unchanged."
            echo "" >&2
            ( cd "$INSTALL_DIR" && $DOCKER compose config 2>&1 | head -5 ) >&2
            echo "" >&2
            die "The fetched docker-compose.yml does not resolve against this host's .env (details above).
     Most likely the config came from a different repo than this script: re-run with
     COFFER_REPO=<owner>/<repo> pointing at the repo you installed from. An
     'invalid interpolation format' error means this host's Docker Compose is too old
     for it: update Docker ('docker compose version' shows which you have)."
        fi

        # Comment the old lines out rather than deleting them, so a value is
        # recoverable if something about the new arrangement is wrong. Compose no
        # longer reads them, so leaving them live would only mean two copies of
        # the same secret — the thing being fixed.
        cp "$INSTALL_DIR/.env" "$INSTALL_DIR/.env.pre-secrets"
        chmod 600 "$INSTALL_DIR/.env.pre-secrets"
        sed -E -i.tmp 's@^(POSTGRES_PASSWORD|COFFER_SERVICE_PASSWORD|COFFER_APP_PASSWORD)=@# moved to secrets/ by install.sh: \1=@' \
            "$INSTALL_DIR/.env"
        rm -f "$INSTALL_DIR/.env.tmp"
        warn "Passwords moved to secrets/. Backup at .env.pre-secrets still contains them — delete it once Coffer is confirmed healthy."
    fi
fi

# Belt and braces for the paths that skipped the migration above (fresh installs,
# and upgrades already carrying secrets/). Nothing has been mutated at this point
# except the compose file, so restoring it puts the host exactly back.
if ! compose_resolves; then
    restore_compose
    echo "" >&2
    ( cd "$INSTALL_DIR" && $DOCKER compose config 2>&1 | head -5 ) >&2
    echo "" >&2
    die "The fetched docker-compose.yml does not resolve against this host's .env (details above).
     Nothing else was changed. If you installed from a private fork, re-run with
     COFFER_REPO=<owner>/<repo>. An 'invalid interpolation format' error means this
     host's Docker Compose is too old for it: update Docker ('docker compose version'
     shows which you have)."
fi

# ----------------------------------------------------------------------- run
cd "$INSTALL_DIR"
# Private ghcr package: authenticate before pulling. Creds persist in
# ~/.docker/config.json, so later `docker compose pull` (upgrades) keep working.
if [ -n "$REGISTRY_TOKEN" ]; then
    if [ -z "${COFFER_REGISTRY_TOKEN:-}" ]; then
        warn "Logging in to ghcr.io with COFFER_GH_TOKEN, and docker stores it on this host."
        warn "  Pass COFFER_REGISTRY_TOKEN, a classic token with read:packages and nothing"
        warn "  else, so what is stored cannot read your repositories (docs/upgrading.md)."
    fi
    info "Logging in to ghcr.io (private image) …"
    printf '%s' "$REGISTRY_TOKEN" | $DOCKER login ghcr.io -u "${COFFER_GH_USER:-${REPO%%/*}}" --password-stdin \
        || die "ghcr login failed — the token needs read:packages (a classic token: ghcr takes no other kind)."
fi
info "Pulling $IMAGE:$IMAGE_TAG${IMAGE_DIGEST:+@$IMAGE_DIGEST} …"
$DOCKER compose pull
# Before `up` replaces the api container: the hand-over takes the volumes from it.
if [ "$MODE" = upgrade ] && [ -f "$INSTALL_DIR/scripts/own-api-volumes.sh" ]; then
    DOCKER="$DOCKER" bash "$INSTALL_DIR/scripts/own-api-volumes.sh" \
        || die "The API's volumes could not be handed to the user it now runs as (details above).
     The running Coffer was not touched. Fix that, then re-run this script."
fi
info "Starting Coffer …"
$DOCKER compose up -d

# --------------------------------------------------------------- wait for health
port="$(grep -E '^API_PORT=' .env | cut -d= -f2-)"; port="${port:-8080}"
# Either name: a host upgraded from an older install still uses the old one.
origin="$(grep -E '^COFFER_WEB_URL=' .env | cut -d= -f2-)"
[ -n "$origin" ] || origin="$(grep -E '^COFFER_WEB_ORIGIN_0=' .env | cut -d= -f2-)"
info "Waiting for the app to answer on http://localhost:$port …"
# Probe /readyz — the anonymous readiness endpoint (process up AND Postgres
# reachable). NOT /api/meta/version: that one is authenticated (ADR-0044), so
# `curl -fsS` on it always 401-fails on a fresh install and this wait would
# spuriously time out even though the app is serving.
up=
for _ in $(seq 1 60); do
    if curl -fsS -o /dev/null "http://localhost:$port/readyz" 2>/dev/null; then up=1; break; fi
    sleep 2
done

echo ""
if [ -n "$up" ]; then info "Coffer is up."; else
    warn "It didn't answer within ~2 min — check 'cd $INSTALL_DIR && $DOCKER compose logs -f api'."
fi
# First-run setup URL. The API logs a one-shot /setup/<token> link once and never
# again, and telling the operator to go grep it out of `compose logs` is not a
# one-command install — it is a one-command install followed by homework, at the
# only moment they have nothing to compare it against. dev-up-docker.sh has fetched
# and printed this for a while; there was no reason the user-facing path was the
# worse one.
#
# Silent on an install that already has a user: `bootstrap-token` refuses once setup
# is done, and its complaint would be noise on every upgrade re-run. NB
# `dotnet coffer-api.dll`, not `coffer-api` — the image ENTRYPOINT is
# ["dotnet","coffer-api.dll"] with no apphost on PATH. The subcommand also emits DbUp
# lines on stdout, hence grepping for the URL rather than taking the whole output.
setup_url=
if [ -n "$up" ]; then
    setup_url="$($DOCKER compose exec -T api dotnet coffer-api.dll bootstrap-token 2>/dev/null \
        | grep -oE 'https?://[^[:space:]]+/setup/[^[:space:]]+' | head -1 || true)"
fi

if [ -n "$setup_url" ]; then
    echo "  Open this ONCE to create your admin passkey:"
    echo "    $setup_url"
    echo ""
    echo "  Straight after that you'll get a welcome screen with this install's master"
    echo "  key to save, and a pointer to set up backups."
else
    echo "  Open:    ${origin:-http://localhost:$port}"
    # Either already set up, or the app never answered — in both cases the link is
    # not ours to print, so say where it lives.
    echo "  If it asks for a one-time setup token:"
    echo "    cd $INSTALL_DIR && $DOCKER compose logs api | grep -i bootstrap"
fi
echo "  Manage:  cd $INSTALL_DIR && $DOCKER compose ps | logs -f api | down | up -d"

# Where the port listens (FU-084). It is every interface unless API_BIND says otherwise,
# which is not what "http://localhost" above suggests, and the host firewall does not
# close it: Docker's port rules come before ufw's and firewalld's. Said on every run; an
# address chosen on purpose is left alone.
bind="$(grep -E '^API_BIND=' .env | cut -d= -f2- || true)"
case "$bind" in
    ""|0.0.0.0|"[::]"|"::")
        echo ""
        warn "Coffer listens on port $port on every interface of this machine, not only"
        warn "  on localhost: anything that can route here reaches it, and Docker's port"
        warn "  rules bypass ufw and firewalld. If a reverse proxy or tunnel on this machine"
        warn "  is the only way in, add API_BIND=127.0.0.1 to $INSTALL_DIR/.env, then:"
        echo "      cd $INSTALL_DIR && $DOCKER compose up -d"
        ;;
esac

# initdb runs exactly once, so an install created before scram became the default
# keeps `trust` on its socket and loopback no matter what the compose file now
# says. Detect it and print the fix here — an operator mid-upgrade will not go
# reading operations.md, and the remediation is useless if they never learn it
# applies to them.
if [ -x "$INSTALL_DIR/scripts/harden-pg-hba.sh" ] \
   && $DOCKER compose exec -T postgres sh -c 'grep -qE "\strust\s*$" /var/lib/postgresql/data/pg_hba.conf' 2>/dev/null; then
    echo ""
    info "This database still allows password-less connections from inside its"
    info "  container (initdb only runs on a fresh data directory, so the hardened"
    info "  default never applied to it). Applying the one-time fix now."

    # APPLY it, rather than print it. This was a warning, and a warning is the
    # wrong instrument: it scrolls past an operator who is mid-upgrade watching
    # for the URL, and the remediation it names is idempotent, needs no restart,
    # verifies the roles can still authenticate BEFORE changing anything, and
    # rolls the file back if they cannot. Something that safe, printed rather
    # than run, is a standing RLS bypass kept alive by the hope that someone
    # reads their terminal.
    #
    # DOCKER is passed through because this host may need `sudo docker`; the
    # script honours it and defaults to plain `docker` elsewhere.
    if DOCKER="$DOCKER" bash "$INSTALL_DIR/scripts/harden-pg-hba.sh"; then
        info "pg_hba hardened - password-less local connections are no longer accepted."
    else
        echo ""
        warn "Automatic pg_hba hardening did not complete, and this database still"
        warn "  accepts password-less connections from inside its container. Nothing is"
        warn "  half-applied: the script restores the original file if the roles cannot"
        warn "  authenticate afterwards. Run it by hand to see why:"
        echo "      sudo bash $INSTALL_DIR/scripts/harden-pg-hba.sh"
    fi
fi

# Restore-by-swap privileges. Idempotent, needs no restart, and a no-op on a fresh
# install (db/init already granted them); on an upgrade it is what makes restoring
# a backup possible at all. Run rather than printed, for the reason the pg_hba
# block above gives.
if DOCKER="$DOCKER" bash "$INSTALL_DIR/scripts/provision-restore-privileges.sh"; then
    info "Backup restore is provisioned."
else
    echo ""
    warn "Could not grant the privileges a backup restore needs; restoring will be"
    warn "  refused until it is done. Nothing else is affected. Run it by hand to see why:"
    echo "      sudo bash $INSTALL_DIR/scripts/provision-restore-privileges.sh"
fi

# Advice for the setup screen a fresh install is about to show. An upgrade has no setup
# screen and nothing to restore, and printing it there read as though it had to.
if [ "$MODE" = fresh ]; then
    echo "  Restoring an existing Coffer? Open the URL, choose 'Restore from a backup',"
    echo "  and upload the .cofferbak + its passphrase. Add that install's master key too if"
    echo "  you have it — it carries the sealed secrets across, and is checked against the"
    echo "  archive first. Without it the restore still works, but bank feeds, the backup"
    echo "  passphrase, Google Drive and notification URLs need setting up again. Either way"
    echo "  everyone signs in again and AI connectors are connected again."
fi
