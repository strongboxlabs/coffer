#!/usr/bin/env bash
# Upgrade this Coffer install to a release.
#
#   bash ~/coffer/upgrade.sh            # the latest release
#   bash ~/coffer/upgrade.sh 0.123.0    # that release
#
# install.sh puts this script in the install directory and replaces it on every run,
# so it is always the one the current release shipped. It does one thing: fetch the
# install.sh of the release being moved to and run it as an in-place upgrade. Every
# release's upgrade steps (the volume hand-over, the image digest, the compose file)
# live in that release's install.sh, so an old copy of install.sh is never reused.
#
# A private fork is told apart by the repo answering an anonymous request with 404.
# Then it prompts for the two tokens docs/upgrading.md describes: neither is stored by
# this script or put on a command line.
#
# Overridable via env: COFFER_REPO (else the install's .env, else the public repo),
# COFFER_GH_API (tests), COFFER_READY_TIMEOUT_SECONDS (how long to wait for the API to
# answer after the upgrade, default 600; install.sh reads it too).
#
# The host updater (app-upgrades.sh) runs it with COFFER_UNATTENDED=1 and the release as the
# argument: no prompt, no sudo, and the fetched install.sh must carry the contract marker
# (INSTALL_CONTRACT=1) or the run stops before anything is changed. Exit status, shared with
# install.sh: 0 done; 10 stopped before anything was changed; 11 and 12 install.sh failed
# before and after its first change; 13 the release's install.sh predates the contract; 14 the
# API did not answer in time (it may still be migrating). `coffer-phase:` lines from
# install.sh on stdout say where a run is.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GH_API="${COFFER_GH_API:-https://api.github.com}"

# Everything this script does before it hands over to install.sh changes nothing: 10.
die() { echo "upgrade: $*" >&2; exit "${DIE_CODE:-10}"; }
# Checked before anything is changed, so a mistyped value stops the run, not the wait.
READY_TIMEOUT="${COFFER_READY_TIMEOUT_SECONDS:-600}"
case "$READY_TIMEOUT" in
    ''|*[!0-9]*|0*) die "COFFER_READY_TIMEOUT_SECONDS must be a whole number of seconds above 0, not '$READY_TIMEOUT'." ;;
esac
UNATTENDED="${COFFER_UNATTENDED:-}"
if [ "$UNATTENDED" = 1 ]; then
    [ -n "${1:-}" ] || die "COFFER_UNATTENDED=1 needs the release as the argument: the updater names the one it checked."
fi
[ "$(id -u)" -ne 0 ] || die "run it as your normal user, not under sudo; install.sh escalates what needs it."
[ -f "$DIR/.env" ] || die "$DIR has no .env, so there is no install here to upgrade."
env_value() { sed -n "s/^$1=//p" "$DIR/.env" | tail -1; }

REPO="${COFFER_REPO:-$(env_value COFFER_REPO)}"
REPO="${REPO:-strongboxlabs/coffer}"
grep -qE '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' <<<"$REPO" || die "'$REPO' is not owner/name."

T="" R=""
code="$(curl -s -o /dev/null -w '%{http_code}' "$GH_API/repos/$REPO")" || code=000
case "$code" in
    200) ;;
    404)
        # The same two variables install.sh reads; prompted for when they are not set.
        T="${COFFER_GH_TOKEN:-}" R="${COFFER_REGISTRY_TOKEN:-}"
        if [ -z "$T" ] || [ -z "$R" ]; then
            [ "$UNATTENDED" != 1 ] || die "$REPO is private (or does not exist) and its tokens are not set; an unattended run cannot ask. The host command that stores them is app-upgrades.sh set-token."
            echo "$REPO is private (or does not exist); its tokens are needed."
            [ -n "$T" ] || { read -rsp 'Repository token (fine-grained, Contents: read): ' T </dev/tty; echo; }
            [ -n "$R" ] || { read -rsp 'Registry token (classic, read:packages only): ' R </dev/tty; echo; }
        fi
        [ -n "$T" ] && [ -n "$R" ] || die "both tokens are needed for a private fork." ;;
    *) die "GitHub answered $code for $REPO; check this host's network and DNS." ;;
esac

# GET from the repo's API. A token goes to curl on stdin, never in its argv.
api() {
    if [ -n "$T" ]; then
        printf 'Authorization: Bearer %s\n' "$T" | curl -fsSL -H @- -H "Accept: $1" "$GH_API/repos/$REPO/$2"
    else
        curl -fsSL -H "Accept: $1" "$GH_API/repos/$REPO/$2"
    fi
}

V="${1:-}"
if [ -z "$V" ]; then
    V="$(api application/vnd.github+json releases/latest | sed -n 's/^ *"tag_name": *"v\{0,1\}\([^"]*\)".*/\1/p' | sed -n 1p)" \
        || die "could not read $REPO's latest release."
fi
V="${V#v}"
grep -qE '^[0-9]+\.[0-9]+\.[0-9]+$' <<<"$V" || die "'$V' is not a release version (x.y.z)."

current="$(env_value COFFER_IMAGE_TAG)"
echo "Upgrading $DIR from ${current:-an unknown version} to $V ($REPO)."

f="$(mktemp)"
trap 'rm -f "$f"' EXIT
api application/vnd.github.raw "contents/scripts/install.sh?ref=v$V" >"$f" \
    || die "could not fetch install.sh at v$V: no such release, or the token cannot read $REPO."
grep -q 'self-contained Linux installer' "$f" || die "what came back for v$V is not install.sh."
if [ "$UNATTENDED" = 1 ] && ! grep -qx 'INSTALL_CONTRACT=1' "$f"; then
    DIE_CODE=13 die "install.sh at v$V predates the unattended contract, so the updater cannot run it. Upgrade by hand: bash $DIR/upgrade.sh $V"
fi

# install.sh keeps the outgoing .env and docker-compose.yml as .bak, at its first change.
COFFER_DIR="$DIR" COFFER_REPO="$REPO" COFFER_VERSION="$V" COFFER_UPGRADE=1 \
    COFFER_UNATTENDED="$UNATTENDED" COFFER_GH_TOKEN="$T" COFFER_REGISTRY_TOKEN="$R" bash "$f"
unset T R

# The API answers nothing until its migrations have run (MigrationRunner.Run, before it
# listens), and the first start of a release runs that release's migrations and data
# steps over the whole database. So this waits, as install.sh does, for as long as the
# API's own ceiling on one migration step (MigrationRunner's 10-minute execution
# timeout) unless COFFER_READY_TIMEOUT_SECONDS says otherwise. Giving up ends only the
# wait; the API carries on migrating.
port="$(env_value API_PORT)"
url="http://localhost:${port:-8080}/readyz"
echo "Waiting for the API on $url (up to ${READY_TIMEOUT}s) …"
start=$SECONDS
next=30
until curl -fsS --max-time 5 -o /dev/null "$url" 2>/dev/null; do
    waited=$((SECONDS - start))
    [ "$waited" -lt "$READY_TIMEOUT" ] \
        || DIE_CODE=14 die "the API did not answer /readyz within ${READY_TIMEOUT}s. It may still be running the
  release's database migrations, which the first start does before it answers. Follow it with:
  cd $DIR && sudo docker compose logs -f api
  To wait longer next time, set COFFER_READY_TIMEOUT_SECONDS."
    if [ "$waited" -ge "$next" ]; then
        echo "  still starting after ${waited}s (the first start of a release runs its database migrations first) …"
        next=$((next + 30))
    fi
    sleep 2
done
echo "Upgraded to $V. The previous .env is $DIR/.env.bak; install.sh kept docker-compose.yml.bak."
