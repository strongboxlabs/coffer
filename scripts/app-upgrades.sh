#!/usr/bin/env bash
# The host updater for upgrades started from the app (ADR-0110).
#
#   bash ~/coffer/app-upgrades.sh enable      put the tick in this user's crontab (idempotent)
#   bash ~/coffer/app-upgrades.sh disable     stop acting on requests; the tick keeps reporting `disabled`
#   bash ~/coffer/app-upgrades.sh status      what the updater knows
#   bash ~/coffer/app-upgrades.sh ack         acknowledge a failure past the first change
#   bash ~/coffer/app-upgrades.sh set-token   store a private fork's tokens (0600 files, silent prompts)
#   bash ~/coffer/app-upgrades.sh probe       read-only self-check
#   bash ~/coffer/app-upgrades.sh tick        what cron runs every minute
#
# The app writes a request into the dedicated mailbox volume as an EMPTY FILE whose name is the
# request: req_v1_<32 hex id>_<x.y.z>_<10-digit epoch>. Each tick starts one hardened throwaway
# container from the API's image, lists the names, and decides everything itself: the version must
# be the latest release this host resolves with its own tokens, newer than what is installed,
# issued within the last 15 minutes (not in the future), and its id never seen before. A valid
# request starts `upgrade.sh <version>` detached; the tick writes status names and one replaced
# state name (hb_v1_<state>_<epoch>) back. Nothing in a request is trusted: the API can write any
# well-formed name. Runs as the install user (docker group, never root, never sudo).
#
# State lives in <install dir>/upgrades/ (0700): tick.log (0600), consumed, disabled, blocked,
# last-exit, history, secrets/ (the private-fork tokens).
set -uo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${COFFER_DIR:-$SELF_DIR}"
STATE="$INSTALL_DIR/upgrades"
DOCKER="${DOCKER:-$(command -v docker || echo /usr/bin/docker)}"
PROJECT="${COFFER_PROJECT:-$(basename "$INSTALL_DIR" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9_-')}"
GH_API="${COFFER_GH_API:-https://api.github.com}"
MBX_DEST=/app/mailbox
BOUND=4096          # bytes of names read per tick
EXPIRY=900          # a request older than this is refused
SKEW=60             # a request dated further ahead than this is refused
MARK="# coffer-upgrades $INSTALL_DIR"
RE_REQ='^req_v1_[0-9a-f]{32}_(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})_[1-9][0-9]{9}$'

die() { echo "app-upgrades: $*" >&2; exit 1; }
env_value() { sed -n "s/^$1=//p" "$INSTALL_DIR/.env" 2>/dev/null | tail -1; }
dk() { local t=$1; shift; timeout -k 3 "$t" "$DOCKER" "$@"; }
esc() { printf '%q' "${1:0:80}"; }
now() { date +%s; }

# vnewer A B: A is strictly newer than B (x.y.z, numeric).
vnewer() {
    local -a A B
    local i
    IFS=. read -r -a A <<<"$1"
    IFS=. read -r -a B <<<"$2"
    for i in 0 1 2; do
        [ "${A[i]:-0}" -gt "${B[i]:-0}" ] && return 0
        [ "${A[i]:-0}" -lt "${B[i]:-0}" ] && return 1
    done
    return 1
}

# decide NAME NOW INSTALLED LATEST CONSUMED_FILE: accept:<id> or refuse:<why>; status 0 only on accept.
decide() {
    local name=$1 t=$2 installed=$3 latest=$4 consumed=$5 rest id ver epoch
    rest=${name#req_v1_}; id=${rest%%_*}
    rest=${rest#*_};      ver=${rest%%_*}
    epoch=${rest#*_}
    [ "$ver" = "$latest" ]             || { echo refuse:notlatest; return 1; }
    vnewer "$ver" "$installed"         || { echo refuse:notnewer;  return 1; }
    [ "$epoch" -le $((t + SKEW)) ]     || { echo refuse:future;    return 1; }
    [ $((t - epoch)) -le "$EXPIRY" ]   || { echo refuse:expired;   return 1; }
    if grep -qx -- "$id" "$consumed" 2>/dev/null; then echo refuse:consumed; return 1; fi
    echo "accept:$id"
}

# pre_refuse NAME NOW INSTALLED: the refusal that needs nothing from GitHub, printed, or nothing and
# status 1 when the request is still live. It runs before the release is resolved, so a request the
# host cannot decide (GitHub unreachable, a token it lacks) still expires and is removed, and an
# install it cannot name (COFFER_IMAGE_TAG=latest) is refused, not run on a guess.
pre_refuse() {
    local name=$1 t=$2 installed=$3
    [[ $installed =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo unknowninst; return 0; }
    [ $((t - ${name##*_})) -le "$EXPIRY" ] || { echo expired; return 0; }
    return 1
}

# hb_state INSTALLED: the word the heartbeat carries. blocked is a run that failed after its first
# change and has not been acknowledged; unpinned is an install whose COFFER_IMAGE_TAG is not a
# version (it tracks :latest), which this host cannot upgrade because it cannot tell what is
# installed. The app shows both and keeps its button off, instead of finding out the slow way, after
# a passkey tap, a backup and a request that is refused or expires.
hb_state() {
    if [ -e "$STATE/disabled" ]; then echo disabled
    elif [ -e "$STATE/blocked" ]; then echo blocked
    elif [[ ${1:-} =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then echo running
    else echo unpinned; fi
}

# read_names: NUL-delimited names on stdin; valid ones one per line on stdout. Anything else, and an
# unterminated tail, is dropped and counted in REJ_FILE. Needs LC_ALL=C.
read_names() {
    local name
    while IFS= read -r -d '' name; do
        if [ "${#name}" -le 68 ] && [[ $name =~ $RE_REQ ]]; then printf '%s\n' "$name"
        else echo x >>"${REJ_FILE:-/dev/null}"; fi
    done
}

# ------------------------------------------------------------------ the mailbox
# resolve: the one api container and its named mailbox volume, fresh every tick. Sets R_ID, R_IMAGE
# (an image id), R_VOL. Status: 10 a docker call failed, 11 not exactly one api container,
# 12 no named volume at /app/mailbox, 13 that volume does not exist. Label filters, not `compose
# ps`: a one-off `compose run` container is not the api.
resolve() {
    local ids n m
    R_ID=; R_IMAGE=; R_VOL=
    ids=$(dk 20 ps -a -q --filter "label=com.docker.compose.project=$PROJECT" \
        --filter label=com.docker.compose.service=api --filter label=com.docker.compose.oneoff=False) || return 10
    n=$(printf '%s\n' "$ids" | grep -c .)
    [ "$n" -eq 1 ] || return 11
    R_ID=$ids
    m=$(dk 20 inspect -f '{{range .Mounts}}{{if eq .Destination "'"$MBX_DEST"'"}}{{.Type}} {{.Name}}{{end}}{{end}}' "$R_ID") || return 10
    [ "${m%% *}" = volume ] && [ -n "${m#* }" ] || return 12
    R_VOL=${m#* }
    R_IMAGE=$(dk 20 inspect -f '{{.Image}}' "$R_ID") || return 10
    dk 20 volume inspect "$R_VOL" >/dev/null 2>&1 || return 13
}

# The tick's one container: detached, used through docker exec, removed at the end. Its own
# deadline removes it if this script dies. uid 1654 is the API's, and the volume is 0700 to it.
mbx_start() {
    C_NAME="coffer-mbx-$(now)-$$-$RANDOM"
    timeout -k 3 25 "$DOCKER" run -d --rm --name "$C_NAME" --init --pull never --network none --read-only \
        --cap-drop ALL --security-opt no-new-privileges:true --user 1654:1654 --memory 64m --memory-swap 64m \
        --pids-limit 32 --log-driver none --mount "type=volume,source=$R_VOL,target=/mbx" \
        --entrypoint bash "$R_IMAGE" -c 'exec timeout -k 2 60 sleep infinity' >/dev/null </dev/null
}
mbx_exec() { local s=$1; shift; timeout -k 3 20 "$DOCKER" exec "$C_NAME" bash -c "$s" bash "$@" </dev/null; }
mbx_stop() {
    [ -n "${C_NAME:-}" ] || return 0
    dk 15 rm -f "$C_NAME" >/dev/null 2>&1
    [ -z "$(dk 10 ps -a -q --filter "name=^/${C_NAME}\$")" ]
}

# Regular files named req_v1_*, NUL-delimited, cut at $2 bytes inside the container. Exit 3 when the
# directory is missing or a symlink, 4 when the output was cut, anything else non-zero is a failure.
LIST_CMD='[ -d "$1" ] && [ ! -L "$1" ] || exit 3
exec 3< <(timeout -k 2 8 find "$1" -mindepth 1 -maxdepth 1 -type f -name "req_v1_*" -printf "%f\0")
fp=$!
n=0
while IFS= read -r -d "" name; do
    n=$((n + ${#name} + 1))
    [ "$n" -le "$2" ] || exit 4
    printf "%s\0" "$name"
done <&3
wait "$fp"'

# rm (one regular file), put (exclusive create), hb (create the state name, drop the older ones).
OPS_CMD='cd /mbx || exit 3
op=$1; shift
case "$op" in
  rm)  [ -f "$1" ] && [ ! -L "$1" ] || exit 4
       rm -f -- "$1" ;;
  put) set -o noclobber
       : > "$1" ;;
  hb)  rm -f -- "$1"                  # the host'"'"'s own name, replaced; two ticks in one second share it
       set -o noclobber
       : > "$1" || exit 5
       for f in hb_v1_*; do [ -e "$f" ] || continue; [ "$f" = "$1" ] || rm -f -- "$f"; done ;;
  *)   exit 2 ;;
esac'

# ------------------------------------------------------------- GitHub, tokens
token() { [ -s "$STATE/secrets/$1" ] && cat "$STATE/secrets/$1"; }
latest_release() {  # the latest release's x.y.z, resolved here with this host's own token
    local repo t body
    repo="$(env_value COFFER_REPO)"; repo="${repo:-strongboxlabs/coffer}"
    grep -qE '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' <<<"$repo" || return 1
    t="$(token repo-token || true)"
    if [ -n "$t" ]; then
        body="$(printf 'Authorization: Bearer %s\n' "$t" | timeout 30 curl -fsSL -H @- -H 'Accept: application/vnd.github+json' "$GH_API/repos/$repo/releases/latest")" || return 1
    else
        body="$(timeout 30 curl -fsSL -H 'Accept: application/vnd.github+json' "$GH_API/repos/$repo/releases/latest")" || return 1
    fi
    sed -n 's/^ *"tag_name": *"v\{0,1\}\([^"]*\)".*/\1/p' <<<"$body" | sed -n 1p
}

# ------------------------------------------------------------------- the tick
log() { printf '%s %s\n' "$(date -u +%FT%TZ)" "$*"; }

# The process that last read $STATE/running as a run still alive: same boot, same process, the
# start time it had then (a reused PID has another). A marker the run has not written to yet is
# alive for two minutes, which covers the moment between the tick creating it and the run starting.
proc_start() { local s; s="$(cat "/proc/$1/stat" 2>/dev/null)" || return 1; s=${s##*) }; cut -d' ' -f20 <<<"$s"; }

run_alive() {
    local boot pid st
    [ -e "$STATE/running" ] || return 1
    read -r boot pid st _ <"$STATE/running" 2>/dev/null
    if [ -z "${pid:-}" ]; then
        [ $(( $(now) - $(stat -c %Y "$STATE/running" 2>/dev/null || echo 0) )) -lt 120 ]
        return
    fi
    [ "$boot" = "$(cat /proc/sys/kernel/random/boot_id 2>/dev/null)" ] || return 1
    [ "$(proc_start "$pid")" = "$st" ]
}

# A marker whose run is gone ended without writing its exit: the host restarted, or the run was
# killed. Nothing says how far it got, so it is recorded as exit 12, past the first change: the
# admin is told, and the next upgrade waits for `ack`. A run that did write its exit is left alone.
reap_dead_run() {
    [ -e "$STATE/running" ] && ! run_alive || return 0
    local boot pid st id ver from lid=""
    read -r boot pid st id ver from <"$STATE/running" 2>/dev/null
    if [ -n "${id:-}" ]; then
        [ ! -s "$STATE/last-exit" ] || read -r lid _ <"$STATE/last-exit"
        if [ "$lid" != "$id" ]; then
            printf '%s %s %s 12 %s\n' "$id" "$from" "$ver" "$(now)" >"$STATE/last-exit.new" \
                && mv -f "$STATE/last-exit.new" "$STATE/last-exit"
            echo "The run ended without a result: the host restarted or the process was killed." >>"$STATE/run-$id.log"
            log "dead run id=$id recorded as exit 12"
        fi
    fi
    rm -f "$STATE/running"
}

reconcile() { report_last_exit; reap_dead_run; report_last_exit; }

# A finished run, read from last-exit, becomes a status name and, past the first change, a block.
report_last_exit() {
    [ -s "$STATE/last-exit" ] || return 0
    local id ver from code t
    read -r id from ver code t <"$STATE/last-exit" || return 0
    [ -z "$id" ] || [ -e "$STATE/reported-$id" ] && return 0
    case "$code" in
        0)  mbx_exec "$OPS_CMD" put "st_v1_${id}_done_${from}_${ver}_${t}" || return 0 ;;
        *)  mbx_exec "$OPS_CMD" put "st_v1_${id}_exit${code}_${from}_${ver}_${t}" || return 0
            # Only a run that got past its first change blocks the next one: 10, 11 and 13 changed
            # nothing, and the app tells the admin to try again.
            case "$code" in 12|14) : >"$STATE/blocked" ;; esac ;;
    esac
    : >"$STATE/reported-$id"
    log "reported id=$id exit=$code"
}

# install.sh's `docker login` keeps the registry token where docker keeps its config. A run with
# a stored token gets a config of its own, so the login cannot outlive the run: the run logs out
# and deletes it, and the tick sweeps one a killed run left. The user's Compose plugin and
# contexts are linked in, since a config directory of its own would hide them.
run_docker_config() {  # run_docker_config ID — prints the directory
    local dc="$STATE/docker-cfg.$1" src="${DOCKER_CONFIG:-$HOME/.docker}" d
    ( umask 077; mkdir -p "$dc" ) || return 1
    for d in cli-plugins contexts; do [ ! -e "$src/$d" ] || ln -sfn "$src/$d" "$dc/$d"; done
    printf '%s' "$dc"
}

sweep_run_configs() { run_alive || rm -rf -- "$STATE"/docker-cfg.*; }

start_run() {  # start_run ID VERSION INSTALLED — upgrade.sh detached; its exit is recorded for reconcile
    local id=$1 ver=$2 from=$3 gh reg dc=""
    gh="$(token repo-token || true)"; reg="$(token registry-token || true)"
    if [ -n "$gh$reg" ]; then
        # Fails closed: without a config of its own the tokens are not handed over, and the
        # upgrade stops in its preflight (exit 10) rather than leave a login behind.
        dc="$(run_docker_config "$id")" || { dc=""; gh=""; reg=""; log "warn: no private docker config; tokens withheld"; }
    fi
    : >"$STATE/running"
    (
        umask 077
        setsid env -i HOME="$HOME" PATH="$PATH" LC_ALL=C COFFER_DIR="$INSTALL_DIR" COFFER_UNATTENDED=1 \
            COFFER_GH_TOKEN="$gh" COFFER_REGISTRY_TOKEN="$reg" ${dc:+"DOCKER_CONFIG=$dc"} \
            bash -c 's=$(cat /proc/$$/stat); s=${s##*) }
                     printf "%s %s %s %s %s %s\n" "$(cat /proc/sys/kernel/random/boot_id)" "$$" "$(cut -d" " -f20 <<<"$s")" "$3" "$2" "$4" >"$1/upgrades/running.new"
                     mv -f "$1/upgrades/running.new" "$1/upgrades/running"
                     bash "$1/upgrade.sh" "$2" >"$1/upgrades/run-$3.log" 2>&1; rc=$?
                     [ -z "$5" ] || { timeout 20 "$6" logout ghcr.io >/dev/null 2>&1; rm -rf -- "$5"; }
                     printf "%s %s %s %s %s\n" "$3" "$4" "$2" "$rc" "$(date +%s)" >"$1/upgrades/last-exit.new"
                     mv -f "$1/upgrades/last-exit.new" "$1/upgrades/last-exit"; rm -f "$1/upgrades/running"' \
            bash "$INSTALL_DIR" "$ver" "$id" "$from" "$dc" "$DOCKER" </dev/null >/dev/null 2>&1 9>&- &
    )
    printf '%s %s %s\n' "$id" "$ver" "$(now)" >>"$STATE/history"
}

tick() {
    export PATH=/usr/local/bin:/usr/bin:/bin LC_ALL=C
    [ -n "${HOME:-}" ] || { echo "fail: HOME is not set"; return 20; }
    [ -x "$DOCKER" ]   || { echo "fail: no docker binary"; return 21; }
    [ -d "$STATE" ]    || { echo "fail: no $STATE"; return 22; }
    umask 077
    exec >>"$STATE/tick.log" 2>&1
    # Keep the log bounded: the last 2000 lines when it passes 1 MB.
    if [ "$(stat -c %s "$STATE/tick.log" 2>/dev/null || echo 0)" -gt 1048576 ]; then
        tail -n 2000 "$STATE/tick.log" >"$STATE/tick.log.new" && mv -f "$STATE/tick.log.new" "$STATE/tick.log"
    fi
    exec 9>"$STATE/tick.lock"
    flock -n 9 || { log "skip: previous tick still running"; return 0; }

    local c rc state raw lrc truncated=0 installed from latest t name verdict id ver tag accepted=0 refused=0
    resolve; rc=$?
    [ "$rc" -eq 0 ] || { log "fail: resolve status $rc"; return $((50 + rc)); }
    for c in $(dk 10 ps -a -q --filter 'name=^/coffer-mbx-'); do dk 15 rm -f "$c" >/dev/null 2>&1; done
    mbx_start || { log "fail: could not start the tick container"; return 41; }
    trap 'mbx_stop || log "warn: tick container not confirmed gone"' RETURN

    reconcile
    sweep_run_configs
    state=running; [ ! -e "$STATE/disabled" ] || state=disabled
    raw=$(mktemp "$STATE/raw.XXXXXX")
    if [ "$state" = running ]; then
        mbx_exec "$LIST_CMD" /mbx "$BOUND" >"$raw"; lrc=$?
        if [ "$lrc" -eq 4 ]; then truncated=1
        elif [ "$lrc" -ne 0 ]; then rm -f "$raw"; log "fail: listing status $lrc"; return 42; fi
        : >"$STATE/rej"
        REJ_FILE="$STATE/rej" read_names <"$raw" | sort -t_ -k5,5n >"$raw.names"
        installed="$(env_value COFFER_IMAGE_TAG)"
        t=$(now)
        # Refuse what needs no release lookup first (see pre_refuse); a status name carries the
        # installed version, so an unparseable one is written as 0.0.0.
        from=$installed; [[ $installed =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || from=0.0.0
        : >"$raw.live"
        while IFS= read -r name; do
            if tag=$(pre_refuse "$name" "$t" "$installed"); then
                id=${name#req_v1_}; id=${id%%_*}; ver=${name#req_v1_*_}; ver=${ver%%_*}
                mbx_exec "$OPS_CMD" rm "$name" || log "warn: rm failed for $(esc "$name")"
                mbx_exec "$OPS_CMD" put "st_v1_${id}_${tag}_${from}_${ver}_${t}" || log "warn: status write failed"
                log "refused id=$id version=$ver reason=$tag"
                refused=$((refused + 1))
            else
                printf '%s\n' "$name" >>"$raw.live"
            fi
        done <"$raw.names"
        mv -f "$raw.live" "$raw.names"
        if [ -s "$raw.names" ]; then
            latest="$(latest_release || true)"
            if [ -z "$latest" ]; then log "warn: could not resolve the latest release; requests wait"; : >"$raw.names"; fi
        fi
        while IFS= read -r name; do
            verdict=$(decide "$name" "$t" "$installed" "$latest" "$STATE/consumed")
            id=${name#req_v1_}; id=${id%%_*}; ver=${name#req_v1_*_}; ver=${ver%%_*}; tag=${verdict#*:}
            if [ "${verdict%%:*}" = accept ]; then
                if [ -e "$STATE/disabled" ] || [ -e "$STATE/blocked" ] || run_alive || [ "$accepted" -gt 0 ]; then
                    refused=$((refused + 1)); continue            # busy or blocked: the request waits, or expires
                fi
                printf '%s\n' "$id" >>"$STATE/consumed"; sync "$STATE/consumed"
                mbx_exec "$OPS_CMD" rm "$name" || log "warn: rm failed for $(esc "$name")"
                mbx_exec "$OPS_CMD" put "st_v1_${id}_started_${installed}_${ver}_${t}" || log "warn: status write failed"
                start_run "$id" "$ver" "$installed"
                log "accepted id=$id version=$ver; upgrade.sh started"
                accepted=$((accepted + 1))
            else
                mbx_exec "$OPS_CMD" rm "$name" || log "warn: rm failed for $(esc "$name")"
                mbx_exec "$OPS_CMD" put "st_v1_${id}_${tag}_${installed}_${ver}_${t}" || log "warn: status write failed"
                log "refused id=$id version=$ver reason=$tag"
                refused=$((refused + 1))
            fi
        done <"$raw.names"
    fi
    rm -f "$raw" "$raw.names" "$raw.live"
    state=$(hb_state "$(env_value COFFER_IMAGE_TAG)")
    mbx_exec "$OPS_CMD" hb "hb_v1_${state}_$(now)" || log "warn: state write failed"
    log "tick ok state=$state accepted=$accepted refused=$refused truncated=$truncated"
}

# ------------------------------------------------------------------- commands
cron_line() {
    printf '* * * * * /usr/bin/env -i PATH=/usr/local/bin:/usr/bin:/bin HOME=%s LC_ALL=C DOCKER=%s COFFER_DIR=%s /bin/bash %s/app-upgrades.sh tick >/dev/null 2>&1 %s\n' \
        "$HOME" "$DOCKER" "$INSTALL_DIR" "$INSTALL_DIR" "$MARK"
}
cron_present() { local cur; cur="$(crontab -l 2>/dev/null || true)"; grep -qF -- "$MARK" <<<"$cur"; }

cmd_enable() {
    command -v crontab >/dev/null 2>&1 || die "no crontab command on this host, so the updater cannot be enabled here."
    [ "$(id -u)" -ne 0 ] || die "run it as your normal user, not root."
    [ "$(uname -s)" = Linux ] || die "the updater is for Linux with a cron daemon; use bash $INSTALL_DIR/upgrade.sh here."
    dk 10 info >/dev/null 2>&1 || die "docker does not answer for this user without sudo, so the updater cannot run here."
    ( umask 077; mkdir -p "$STATE/secrets" ) || die "cannot create $STATE"
    chmod 700 "$STATE" "$STATE/secrets" || die "cannot restrict $STATE"
    # install.sh passes --keep-flag: it never re-enables a host that was disabled.
    [ "${1:-}" = --keep-flag ] || rm -f "$STATE/disabled"
    local cur
    cur="$(crontab -l 2>/dev/null || true)"
    { printf '%s\n' "$cur" | grep -vF -- "$MARK" | sed '/^$/d'; cron_line; } | crontab - || die "could not write the crontab."
    echo "enabled: the tick runs every minute from this user's crontab."
    tick_once_note
}
tick_once_note() {
    local rc
    ( cmd_tick ) >/dev/null 2>&1; rc=$?
    case "$rc" in
        0)  echo "self-test: a tick ran and reported." ;;
        62|63) echo "self-test: this install has no mailbox volume yet (rc $rc); the tick starts working once the API image that carries it is running." ;;
        *)  echo "self-test: the tick exited $rc; see $STATE/tick.log" ;;
    esac
}
cmd_disable() {
    ( umask 077; mkdir -p "$STATE" ) && : >"$STATE/disabled"
    echo "disabled: the tick keeps running and reports it, and acts on no request. 'enable' turns it back on."
}
cmd_ack() {
    rm -f "$STATE/blocked"
    # One tick now, so the heartbeat the app reads stops saying blocked at once and not up to a
    # minute later. Best effort: the next cron tick does it anyway.
    ( cmd_tick ) >/dev/null 2>&1 || true
    echo "acknowledged: new upgrades are allowed again."
}
cmd_set_token() {
    local t r
    ( umask 077; mkdir -p "$STATE/secrets" ) || die "cannot create $STATE/secrets"
    read -rsp 'Repository token (fine-grained, Contents: read): ' t </dev/tty; echo
    read -rsp 'Registry token (classic, read:packages only): ' r </dev/tty; echo
    [ -n "$t" ] && [ -n "$r" ] || die "both tokens are needed."
    ( umask 077; printf '%s' "$t" >"$STATE/secrets/repo-token"; printf '%s' "$r" >"$STATE/secrets/registry-token" )
    echo "stored in $STATE/secrets (0600). They are never mounted into a container."
}
cmd_status() {
    echo "install:   $INSTALL_DIR (compose project $PROJECT)"
    if cron_present; then echo "cron:      enabled"; else echo "cron:      not in this user's crontab"; fi
    if [ -e "$STATE/disabled" ]; then echo "state:     disabled"; else echo "state:     enabled"; fi
    [ ! -e "$STATE/blocked" ] || echo "blocked:   a run failed after its first change; 'ack' once you have looked"
    ! run_alive || echo "running:   an upgrade is in progress (see $STATE/run-*.log)"
    [ ! -s "$STATE/last-run" ] || echo "last run:  $(cat "$STATE/last-run")"
    [ ! -s "$STATE/last-exit" ] || echo "last exit: $(cat "$STATE/last-exit")  (id from to code epoch)"
    [ ! -s "$STATE/tick.log" ] || { echo "last ticks:"; tail -n 5 "$STATE/tick.log" | sed 's/^/  /'; }
}
cmd_probe() {
    local rc=0
    dk 10 info >/dev/null 2>&1 && echo "docker:    ok" || { echo "docker:    does not answer"; rc=1; }
    if cron_present; then echo "cron:      line present"; else echo "cron:      line missing"; rc=1; fi
    resolve; local r=$?
    case "$r" in 0) echo "mailbox:   volume $R_VOL" ;; *) echo "mailbox:   resolve status $r"; rc=1 ;; esac
    return "$rc"
}
cmd_tick() { tick; }

# Sourced (by the install-contract check) it only defines its functions.
[[ "${BASH_SOURCE[0]}" == "$0" ]] || return 0
case "${1:-}" in
    enable)    cmd_enable "${2:-}" ;;
    disable)   cmd_disable ;;
    status)    cmd_status ;;
    ack)       cmd_ack ;;
    set-token) cmd_set_token ;;
    probe)     cmd_probe ;;
    tick)      tick ;;
    *) echo "usage: app-upgrades.sh enable | disable | status | ack | set-token | probe | tick"; exit 2 ;;
esac
