#!/usr/bin/env bash
#
# Coffer — self-contained Linux installer (ADR-0075).
#
#   Public repo:
#     bash <(curl -fsSL https://raw.githubusercontent.com/strongboxlabs/coffer/main/scripts/install.sh)
#
#   Private repo/image (classic PAT with `repo` + `read:packages`) — anonymous
#   raw 404s on a private repo, so fetch the script itself over the authenticated
#   API and pass the same token in so its internal fetches + ghcr login work:
#     T=<pat>; COFFER_GH_TOKEN=$T bash <(curl -fsSL -H "Authorization: Bearer $T" \
#       -H "Accept: application/vnd.github.raw" \
#       "https://api.github.com/repos/strongboxlabs/coffer/contents/scripts/install.sh?ref=main")
#
# Stands up Coffer (app + Postgres) on a fresh Linux host via Docker Compose:
# checks/installs Docker, fetches the canonical docker-compose.yml + db/init from
# the repo (no clone needed), generates a .env with fresh random secrets, pulls
# the published image, and starts it. Interactive, re-runnable, and offers a
# wipe-and-reinstall. TLS / reverse proxy is out of scope — for a networked
# (https) install you front it with your own proxy or tunnel.
#
# Overridable via env: COFFER_DIR (install dir, default ~/coffer),
# COFFER_IMAGE_TAG (default latest), COFFER_GH_TOKEN (private repo/image auth),
# COFFER_REPO (owner/name, default strongboxlabs/coffer), COFFER_REPO_REF (default main),
# COFFER_GH_USER (ghcr login user, default the repo owner), COFFER_REPO_RAW.
#
set -euo pipefail

REPO="${COFFER_REPO:-strongboxlabs/coffer}"
REPO_REF="${COFFER_REPO_REF:-main}"
REPO_RAW="${COFFER_REPO_RAW:-https://raw.githubusercontent.com/$REPO/$REPO_REF}"
GH_API="${COFFER_GH_API:-https://api.github.com}"
# Optional token for a PRIVATE repo/image. When set, the config files are fetched
# via the authenticated GitHub contents API (anonymous raw 404s on private repos)
# and we log in to ghcr before pulling. A classic PAT needs `repo` +
# `read:packages`; leave unset for a public repo (unchanged behaviour).
GH_TOKEN="${COFFER_GH_TOKEN:-}"
INSTALL_DIR="${COFFER_DIR:-$HOME/coffer}"
IMAGE_TAG="${COFFER_IMAGE_TAG:-latest}"
TTY_DEV=/dev/tty

info() { printf '\033[36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[!]\033[0m %s\n' "$*"; }
die()  { printf '\033[31m[x]\033[0m %s\n' "$*" >&2; exit 1; }

# fetch REPO-RELATIVE-PATH DEST — private-repo aware. With COFFER_GH_TOKEN, pull
# from the authenticated GitHub contents API (raw media type); without a token,
# anonymous raw (public repos — unchanged).
fetch() {
    local path=$1 dest=$2
    if [ -n "$GH_TOKEN" ]; then
        curl -fsSL -H "Authorization: Bearer $GH_TOKEN" \
                   -H "Accept: application/vnd.github.raw" \
             "$GH_API/repos/$REPO/contents/$path?ref=$REPO_REF" -o "$dest"
    else
        curl -fsSL "$REPO_RAW/$path" -o "$dest"
    fi
}

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

# validate_kek VALUE — true when VALUE is base64 decoding to exactly 32 bytes
# (the master-KEK contract, ADR-0014 / MasterKeyLoader). Rejects a malformed or
# wrong-length key at prompt time rather than in a post-start boot crash.
validate_kek() {
    local n
    n=$(printf '%s' "$1" | base64 -d 2>/dev/null | wc -c) || n=0
    [ "$n" -eq 32 ]
}

# ------------------------------------------------------------------ privileges
# Run this as your NORMAL user, not `sudo bash …`: under sudo $HOME becomes
# root's, so ~/coffer + your .env (which holds the master KEK) would land in
# /root, root-owned. We escalate with sudo only where it's actually needed —
# installing Docker, and docker itself when you're not in the 'docker' group.
if [ "$(id -u)" -eq 0 ]; then
    SUDO=""
    warn "Running as root — files go to $INSTALL_DIR (root's home unless COFFER_DIR is set). For a user-owned install, run as your normal user instead."
elif command -v sudo >/dev/null 2>&1; then
    SUDO="sudo"
else
    SUDO=""
fi

# --------------------------------------------------------------------- Docker
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    info "Docker + Compose present."
else
    warn "Docker (with the Compose plugin) was not found."
    if yesno "Install Docker now via https://get.docker.com${SUDO:+ (uses sudo)}?"; then
        curl -fsSL https://get.docker.com | ${SUDO:+$SUDO }sh || die "Docker install failed."
        info "Docker installed."
    else
        die "Docker is required. Install it and re-run."
    fi
fi

# Daemon access: prefer running docker WITHOUT sudo; fall back to sudo (e.g. a
# fresh install where your user isn't in the 'docker' group yet — that needs a
# re-login to take effect). Every docker call below goes through $DOCKER.
DOCKER="docker"
if ! docker info >/dev/null 2>&1; then
    if [ -n "$SUDO" ] && $SUDO docker info >/dev/null 2>&1; then
        DOCKER="$SUDO docker"
        warn "Using 'sudo docker' — your user isn't in the 'docker' group yet. To drop the sudo later: 'sudo usermod -aG docker $USER', then log out/in."
    else
        die "Can't reach the Docker daemon. Start it ('sudo systemctl start docker') or add your user to the 'docker' group (log out/in), then re-run."
    fi
fi
command -v openssl >/dev/null 2>&1 || die "openssl is required (secret generation). Install it and re-run."
command -v curl    >/dev/null 2>&1 || die "curl is required. Install it and re-run."

# --------------------------------------------------------------- existing install
MODE=fresh
restoring=            # set to 1 below when the operator chooses to restore a backup
if [ -f "$INSTALL_DIR/.env" ]; then
    warn "An existing Coffer install was found at $INSTALL_DIR."
    echo "  1) Upgrade in place  — keep your data, pull the latest image, restart"
    echo "  2) Wipe & reinstall  — DELETE all data + config, start fresh"
    echo "  3) Cancel"
    ask choice "Choose" "1"
    case "$choice" in
        1) MODE=upgrade ;;
        2) warn "This permanently deletes the Coffer database volume and $INSTALL_DIR/.env."
           ask wipe_confirm "Type 'wipe' to confirm" ""
           [ "$wipe_confirm" = wipe ] || die "Not confirmed — aborting."
           ( cd "$INSTALL_DIR" && $DOCKER compose down -v ) 2>/dev/null || true
           rm -f "$INSTALL_DIR/.env"
           MODE=fresh ;;
        *) die "Cancelled." ;;
    esac
fi

# --------------------------------------------------------- fetch canonical files
mkdir -p "$INSTALL_DIR/db/init"
info "Fetching docker-compose.yml + db/init from the repo …"
fetch docker-compose.yml       "$INSTALL_DIR/docker-compose.yml"       || die "Could not fetch docker-compose.yml (private repo? set COFFER_GH_TOKEN)."
fetch db/init/00-init-roles.sh "$INSTALL_DIR/db/init/00-init-roles.sh" || die "Could not fetch db/init/00-init-roles.sh (private repo? set COFFER_GH_TOKEN)."
chmod +x "$INSTALL_DIR/db/init/00-init-roles.sh"

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
        ask port "Port" "8080"
        web_origin="http://localhost:$port"; rp_id="localhost"
        mcp_enabled=false; mcp_origin="$web_origin"
    fi

    # Fresh install → a new random master KEK. RESTORING another install's data →
    # reuse THAT install's KEK; a fresh one can't decrypt restored data (ADR-0014),
    # which otherwise forces a manual key-swap after install. Take it up front.
    master_kek_id=v1
    echo ""
    if yesno "Are you RESTORING a backup from another Coffer install"; then
        restoring=1
        warn "Restored data opens ONLY under the exact master key it was wrapped with."
        while :; do
            ask master_kek "Paste that install's COFFER_MASTER_KEK_BASE64" ""
            validate_kek "$master_kek" && break
            warn "Not a 32-byte base64 key (what 'openssl rand -base64 32' produces). Try again."
        done
        ask master_kek_id "Its KEK id — COFFER_MASTER_KEK_ID ('v1' unless you rotated, then 'v2', …)" "v1"
    else
        master_kek="$(openssl rand -base64 32)"
    fi

    info "Generating secrets and writing $INSTALL_DIR/.env"
    umask 077
    cat >"$INSTALL_DIR/.env" <<EOF
# Coffer — generated by install.sh (ADR-0075) on $(date -u +%Y-%m-%dT%H:%M:%SZ).
# These secrets are unique to THIS install.
# !! BACK UP COFFER_MASTER_KEK_BASE64 — without it your encrypted data is
#    unrecoverable, even with a database backup. !!
POSTGRES_USER=coffer
POSTGRES_DB=coffer
POSTGRES_PASSWORD=$(openssl rand -hex 24)
COFFER_SERVICE_PASSWORD=$(openssl rand -hex 24)
COFFER_APP_PASSWORD=$(openssl rand -hex 24)
COFFER_MASTER_KEK_BASE64=$master_kek
COFFER_MASTER_KEK_ID=$master_kek_id
ASPNETCORE_ENVIRONMENT=Production
API_PORT=$port
COFFER_IMAGE_TAG=$IMAGE_TAG
COFFER_RP_ID=$rp_id
# Allowed passkey origins, one per numbered slot (wired to Fido2 Origins__0,
# __1, … in docker-compose.yml). Slot 0 = the main app. Slot 1 = a second host
# that runs its own WebAuthn sign-in — e.g. a separate MCP subdomain (ADR-0063);
# it defaults to the main origin (deduped) when MCP shares the host. For a 3rd,
# add COFFER_WEB_ORIGIN_2 here + a matching Origins__2. Re-run
# 'docker compose up -d' after changes.
COFFER_WEB_ORIGIN_0=$web_origin
COFFER_WEB_ORIGIN_1=$mcp_origin
# MCP server (ADR-0063): OAuth-gated read/report access for AI clients (Claude,
# etc.), off unless enabled. It signs in on COFFER_WEB_ORIGIN_1 above.
COFFER_MCP_ENABLED=$mcp_enabled
EOF
    chmod 600 "$INSTALL_DIR/.env"
    warn "Master key written to $INSTALL_DIR/.env — back up COFFER_MASTER_KEK_BASE64 now."
fi

# ----------------------------------------------------------------------- run
cd "$INSTALL_DIR"
# Private ghcr package: authenticate before pulling. Creds persist in
# ~/.docker/config.json, so later `docker compose pull` (upgrades) keep working.
if [ -n "$GH_TOKEN" ]; then
    info "Logging in to ghcr.io (private image) …"
    printf '%s' "$GH_TOKEN" | $DOCKER login ghcr.io -u "${COFFER_GH_USER:-${REPO%%/*}}" --password-stdin \
        || die "ghcr login failed — the token needs read:packages."
fi
info "Pulling ghcr.io/strongboxlabs/coffer:$IMAGE_TAG …"
$DOCKER compose pull
info "Starting Coffer …"
$DOCKER compose up -d

# --------------------------------------------------------------- wait for health
port="$(grep -E '^API_PORT=' .env | cut -d= -f2-)"; port="${port:-8080}"
origin="$(grep -E '^COFFER_WEB_ORIGIN_0=' .env | cut -d= -f2-)"
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
echo "  Open:    ${origin:-http://localhost:$port}"
echo "  First run creates your admin passkey. If it asks for a one-time setup token:"
echo "    cd $INSTALL_DIR && $DOCKER compose logs api | grep -i bootstrap"
echo "  Manage:  cd $INSTALL_DIR && $DOCKER compose ps | logs -f api | down | up -d"
if [ -n "$restoring" ]; then
    echo "  Restore: open the URL, choose 'Restore from a backup', upload your .cofferbak + passphrase."
    echo "           It decrypts under the KEK you provided — no post-install key swap."
fi
