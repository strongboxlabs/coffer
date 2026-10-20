#!/usr/bin/env bash
# Hand the API's volumes to the unprivileged user it runs as (FU-084).
#
# Until 0.122.0 the API container ran as root, so everything it wrote to coffer_data
# and coffer_keys (the master key, the backups, the restore staging) belongs to root.
# From 0.122.0 it runs as the base image's `app` user, uid 1654, and cannot read its
# own master key until those files are its. A fresh install never needs this: Docker
# seeds a new volume from the image's directory, ownership included.
#
# How. The api container that is about to be replaced still has the volumes
# mounted, so a one-off container takes them from it (--volumes-from) and runs
# chown with the image that container was created from. Nothing is pulled, and
# there is no project or volume name to work out. It runs as root with CHOWN and
# DAC_READ_SEARCH only, and no network.
#
# Idempotent — install.sh runs it on every upgrade, before the new container
# starts, and a second run changes nothing. A no-op when there is no api
# container (a fresh install).
#
#   sudo bash scripts/own-api-volumes.sh
set -euo pipefail

# Git Bash (MSYS) rewrites in-container paths before they reach docker; disable
# that. No-op on Linux.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# install.sh passes `sudo docker` on hosts where the user is not in the docker
# group. Unquoted on use, so a two-word value splits the way it must.
DOCKER="${DOCKER:-docker}"
# The user the image's USER line names. Numeric: the old image has the same user, but
# the number is what the files must carry.
APP_UID=1654

die() { echo "[own-api-volumes] ERROR: $*" >&2; exit 1; }
info() { echo "[own-api-volumes] $*"; }

# The install directory this script lives in, through compose — whatever
# COFFER_CONTAINER_PREFIX named the container. API_CONTAINER names one directly
# instead (the restore drill's throwaway stack).
container="${API_CONTAINER:-$(cd "$repo_root" && $DOCKER compose ps -a -q api 2>/dev/null | head -1)}"
if [ -z "$container" ]; then
    info "no api container yet — nothing to hand over."
    exit 0
fi
# The image's id, not its tag: a pull or build may already have moved the tag, and the
# id is on this host for as long as the container exists.
image="$($DOCKER inspect -f '{{.Image}}' "$container" 2>/dev/null)" \
    || die "could not inspect the api container '$container'."

# Prints how many entries changed hands. -h: a symlink is given over itself, never
# what it points at.
changed="$($DOCKER run --rm --volumes-from "$container" --network none \
        --user 0 --cap-drop ALL --cap-add CHOWN --cap-add DAC_READ_SEARCH \
        --entrypoint sh "$image" -c '
            set -e
            n=0
            for dir in /app/data /app/keys; do
                [ -d "$dir" ] || continue
                n=$((n + $(find "$dir" \( ! -user '"$APP_UID"' -o ! -group '"$APP_UID"' \) -print | wc -l)))
                chown -R -h '"$APP_UID:$APP_UID"' "$dir"
            done
            echo "$n"')" \
    || die "could not change the volumes' owner. Nothing else was changed; the new version will not start until this is done."

if [ "$changed" = 0 ]; then
    info "the API's volumes already belong to the user it runs as."
else
    info "handed $changed file(s) and folder(s) on the API's volumes to the user it runs as (uid $APP_UID)."
fi
