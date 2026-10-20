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
# COFFER_GH_API (tests).
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GH_API="${COFFER_GH_API:-https://api.github.com}"

die() { echo "upgrade: $*" >&2; exit 1; }
[ "$(id -u)" -ne 0 ] || die "run it as your normal user, not under sudo; install.sh escalates what needs it."
[ -f "$DIR/.env" ] || die "$DIR has no .env, so there is no install here to upgrade."
env_value() { sed -n "s/^$1=//p" "$DIR/.env" | tail -1; }

REPO="${COFFER_REPO:-$(env_value COFFER_REPO)}"
REPO="${REPO:-strongboxlabs/coffer}"
printf '%s' "$REPO" | grep -qE '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' || die "'$REPO' is not owner/name."

T="" R=""
code="$(curl -s -o /dev/null -w '%{http_code}' "$GH_API/repos/$REPO")" || code=000
case "$code" in
    200) ;;
    404)
        # The same two variables install.sh reads; prompted for when they are not set.
        T="${COFFER_GH_TOKEN:-}" R="${COFFER_REGISTRY_TOKEN:-}"
        if [ -z "$T" ] || [ -z "$R" ]; then
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
    V="$(api application/vnd.github+json releases/latest | sed -n 's/^ *"tag_name": *"v\{0,1\}\([^"]*\)".*/\1/p' | head -1)" \
        || die "could not read $REPO's latest release."
fi
V="${V#v}"
printf '%s' "$V" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+$' || die "'$V' is not a release version (x.y.z)."

current="$(env_value COFFER_IMAGE_TAG)"
echo "Upgrading $DIR from ${current:-an unknown version} to $V ($REPO)."
cp -a "$DIR/.env" "$DIR/.env.bak"

f="$(mktemp)"
trap 'rm -f "$f"' EXIT
api application/vnd.github.raw "contents/scripts/install.sh?ref=v$V" >"$f" \
    || die "could not fetch install.sh at v$V: no such release, or the token cannot read $REPO."
grep -q 'self-contained Linux installer' "$f" || die "what came back for v$V is not install.sh."

COFFER_DIR="$DIR" COFFER_REPO="$REPO" COFFER_VERSION="$V" COFFER_UPGRADE=1 \
    COFFER_GH_TOKEN="$T" COFFER_REGISTRY_TOKEN="$R" bash "$f"
unset T R

port="$(env_value API_PORT)"
code="$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:${port:-8080}/readyz")" || code=000
[ "$code" = 200 ] || die "the API is not ready (readyz $code). Look at: cd $DIR && sudo docker compose logs --tail 40 api"
echo "Upgraded to $V. The previous .env is $DIR/.env.bak; install.sh kept docker-compose.yml.bak."
