#!/usr/bin/env bash
#
# Recover a wedged Coffer MCP connection (Linux / macOS / WSL).
#
# Claude Desktop / Gemini reach the remote Coffer MCP server through a local
# `npx mcp-remote` Node proxy. That proxy occasionally hangs (dead socket to the
# CDN), and then every MCP call times out even though the server is fine. This:
#   1. lists + kills the mcp-remote proxy process(es) for this server,
#   2. health-checks the server's public OAuth discovery endpoint so you can tell
#      "hung proxy" (server answers) from "server actually down",
#   3. optionally (--clear-auth) wipes the cached OAuth token to force re-sign-in.
# Then fully quit and reopen the MCP client so it spawns a fresh proxy.
#
# Usage:
#   scripts/restart-coffer-mcp.sh [--server HOST] [--all-proxies] [--clear-auth]
#
# The host is YOUR deployment's, so it is not hardcoded here — set COFFER_MCP_SERVER
# in your shell profile, or pass --server. Keeping a real hostname out of the tree
# matters twice over: it is deployment-specific (useless to anyone else) and a
# hostname is an infrastructure detail that does not belong in source control.
#
# The PowerShell twin (Windows) is scripts/restart-coffer-mcp.ps1.

set -euo pipefail

SERVER="${COFFER_MCP_SERVER:-}"
ALL_PROXIES=0
CLEAR_AUTH=0

while [ $# -gt 0 ]; do
    case "$1" in
        --server)       SERVER="${2:?--server needs a host}"; shift 2 ;;
        --all-proxies)  ALL_PROXIES=1; shift ;;
        --clear-auth)   CLEAR_AUTH=1; shift ;;
        -h|--help)
            sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

if [ -z "$SERVER" ]; then
    echo "restart-coffer-mcp: no MCP host. Set COFFER_MCP_SERVER or pass --server HOST." >&2
    exit 2
fi

cyan() { printf '\033[36m%s\033[0m\n' "$1"; }
green() { printf '\033[32m%s\033[0m\n' "$1"; }
yellow() { printf '\033[33m%s\033[0m\n' "$1"; }

# 1) Kill the wedged mcp-remote proxy -------------------------------------
cyan "== 1. mcp-remote proxy processes =="
# `[m]cp-remote` so the matching line isn't the grep itself. Columns: pid, args.
mapfile -t rows < <(ps -eo pid=,args= 2>/dev/null | grep -i '[m]cp-remote' || true)

targets=()
for row in "${rows[@]}"; do
    pid="${row%% *}"
    args="${row#* }"
    if [ "$ALL_PROXIES" -eq 1 ] || printf '%s' "$args" | grep -qiF "$SERVER"; then
        targets+=("$pid")
        yellow "  PID ${pid}: ${args:0:150}"
    fi
done

if [ "${#rows[@]}" -gt 0 ] && [ "${#targets[@]}" -eq 0 ]; then
    yellow "  Found mcp-remote proxy(ies) but none naming '${SERVER}'. Re-run with --all-proxies to kill them all."
fi

if [ "${#targets[@]}" -eq 0 ]; then
    green "  Nothing to kill (no matching mcp-remote proxy running)."
else
    for pid in "${targets[@]}"; do
        if kill "$pid" 2>/dev/null; then
            green "  killed PID ${pid}"
        else
            yellow "  could not kill PID ${pid} (already gone?)"
        fi
    done
fi

# 2) Is the SERVER up, or just the proxy? ---------------------------------
# Any HTTP response (even 401/404) means the server + CDN are reachable, so the
# timeout was the local proxy. Only a connection failure points at the server.
cyan ""
cyan "== 2. server health =="
discovery="https://${SERVER}/.well-known/oauth-protected-resource"
body="$(mktemp)"
trap 'rm -f "$body"' EXIT
code="$(curl -sS -o "$body" -w '%{http_code}' --max-time 15 "$discovery" 2>/dev/null || echo "000")"

if [ "$code" = "000" ]; then
    yellow "  Connection FAILED to ${discovery} (timeout, DNS, or refused)."
    yellow "  -> server/network problem. Check Traefik / the container, not the proxy."
elif grep -qiE 'just a moment|enable javascript|challenge-platform' "$body"; then
    yellow "  Reachable (HTTP ${code}) but got a CDN bot-challenge page instead of JSON."
    yellow "  -> the MCP subdomain may have lost its DNS-only (grey-cloud) setting in Cloudflare."
else
    green "  Server reachable: HTTP ${code} from ${discovery}"
    green "  -> server + CDN are up; the timeout was the local proxy (now killed). A 401/404 here is fine."
fi

# 3) Optional: clear cached OAuth token -----------------------------------
auth_dir="${HOME}/.mcp-auth"
if [ "$CLEAR_AUTH" -eq 1 ]; then
    cyan ""
    cyan "== 3. clearing cached auth (~/.mcp-auth) =="
    if [ -d "$auth_dir" ]; then
        rm -rf "$auth_dir"
        green "  Removed ${auth_dir} - the next connect re-runs the OAuth sign-in."
    else
        green "  No ${auth_dir} present."
    fi
elif [ -d "$auth_dir" ]; then
    cyan ""
    cyan "(note) cached auth at ${auth_dir}. Re-run with --clear-auth if timeouts look auth-related."
fi

# 4) Next step ------------------------------------------------------------
cyan ""
cyan "== Done =="
cyan "Fully quit the MCP client (Claude Desktop: tray -> Quit) and reopen it"
cyan "so it spawns a fresh mcp-remote proxy against ${SERVER}."
