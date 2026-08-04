<#
.SYNOPSIS
  Recover a wedged Coffer MCP connection.

.DESCRIPTION
  Claude Desktop / Gemini reach the remote Coffer MCP server through a local
  `npx mcp-remote` Node proxy. That proxy occasionally hangs (dead socket to the
  CDN), and then every MCP call times out even though the server is fine. This:
    1. lists + kills the mcp-remote proxy process(es) for this server,
    2. health-checks the server's public OAuth discovery endpoint so you can tell
       "hung proxy" (server 200) from "server actually down",
    3. optionally (-ClearAuth) wipes the cached OAuth token to force re-sign-in.
  Then fully quit and reopen Claude Desktop so it spawns a fresh proxy.

.PARAMETER Server
  MCP host. Defaults to $env:COFFER_MCP_SERVER — the host is your deployment's, so
  it is not hardcoded here (deployment-specific, and a hostname is an infrastructure
  detail that does not belong in source control). Set the variable or pass -Server.

.PARAMETER AllProxies
  Kill every mcp-remote proxy, not just ones whose command line names -Server.

.PARAMETER ClearAuth
  Also delete ~/.mcp-auth (the cached OAuth token). Use only if timeouts look
  auth-related (401/403) rather than a hung proxy. Forces a fresh sign-in.

.EXAMPLE
  .\Restart-CofferMcp.ps1
.EXAMPLE
  .\Restart-CofferMcp.ps1 -ClearAuth
#>
[CmdletBinding()]
param(
    [string]$Server = $env:COFFER_MCP_SERVER,
    [switch]$AllProxies,
    [switch]$ClearAuth
)

if ([string]::IsNullOrWhiteSpace($Server)) {
    Write-Error 'No MCP host. Set $env:COFFER_MCP_SERVER or pass -Server HOST.'
    exit 2
}

function Info($m) { Write-Host $m -ForegroundColor Cyan }
function Good($m) { Write-Host $m -ForegroundColor Green }
function Warn($m) { Write-Host $m -ForegroundColor Yellow }

# 1) Kill the wedged mcp-remote proxy -------------------------------------
Info "== 1. mcp-remote proxy processes =="
$all = Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
       Where-Object { $_.CommandLine -and $_.CommandLine -match 'mcp-remote' }

if ($AllProxies) {
    $targets = $all
} else {
    $targets = $all | Where-Object { $_.CommandLine -match [regex]::Escape($Server) }
    if (-not $targets -and $all) {
        Warn ("  Found {0} mcp-remote proxy(ies) but none naming '{1}'. Re-run with -AllProxies to kill them all:" -f @($all).Count, $Server)
        $all | ForEach-Object { Warn ("    PID {0}" -f $_.ProcessId) }
    }
}

if (-not $targets) {
    Good "  Nothing to kill (no matching mcp-remote proxy running)."
} else {
    foreach ($p in $targets) {
        $cl = $p.CommandLine
        $show = if ($cl.Length -gt 150) { $cl.Substring(0, 150) + '...' } else { $cl }
        Warn ("  PID {0}: {1}" -f $p.ProcessId, $show)
    }
    foreach ($p in $targets) {
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            Good ("  killed PID {0}" -f $p.ProcessId)
        } catch {
            Warn ("  could not kill PID {0}: {1}" -f $p.ProcessId, $_.Exception.Message)
        }
    }
}

# 2) Is the SERVER up, or just the proxy? ---------------------------------
# Any HTTP response (even 401/404) means the server + CDN are reachable, so the
# timeout was the local proxy. Only a connection failure (timeout/DNS/refused)
# points at the server. Note: in PS 5.1 a 4xx/5xx is thrown, so inspect the
# exception's Response before concluding "unreachable".
Info "`n== 2. server health =="
$discovery = "https://$Server/.well-known/oauth-protected-resource"
$status = $null; $body = ''; $reachable = $false
try {
    $r = Invoke-WebRequest -Uri $discovery -UseBasicParsing -TimeoutSec 15
    $status = [int]$r.StatusCode; $body = "$($r.Content)"; $reachable = $true
} catch {
    $resp = $_.Exception.Response
    if ($resp) {
        $reachable = $true
        try { $status = [int]$resp.StatusCode } catch { }
        try {
            $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $body = $sr.ReadToEnd(); $sr.Close()
        } catch { }
    } else {
        Warn ("  Connection FAILED to {0}:" -f $discovery)
        Warn ("    {0}" -f $_.Exception.Message)
        Warn "  -> server/network problem (timeout, DNS, refused). Check Traefik / the container, not the proxy."
    }
}
if ($reachable) {
    if ($body -match 'Just a moment|Enable JavaScript|challenge-platform') {
        Warn ("  Reachable (HTTP {0}) but got a CDN bot-challenge page instead of JSON." -f $status)
        Warn "  -> the MCP subdomain may have lost its DNS-only (grey-cloud) setting in Cloudflare."
    } else {
        Good ("  Server reachable: HTTP {0} from {1}" -f $status, $discovery)
        Good "  -> server + CDN are up; the timeout was the local proxy (now killed). A 401/404 here is fine."
    }
}

# 3) Optional: clear cached OAuth token -----------------------------------
$authDir = Join-Path $HOME '.mcp-auth'
if ($ClearAuth) {
    Info "`n== 3. clearing cached auth (~/.mcp-auth) =="
    if (Test-Path $authDir) {
        Remove-Item -Recurse -Force $authDir
        Good "  Removed $authDir - the next connect re-runs the OAuth sign-in."
    } else {
        Good "  No $authDir present."
    }
} elseif (Test-Path $authDir) {
    $age = ((Get-Date) - (Get-Item $authDir).LastWriteTime).TotalHours
    Info ("`n(note) cached auth at {0} (updated {1:N1}h ago). Re-run with -ClearAuth if timeouts look auth-related.)" -f $authDir, $age)
}

# 4) Next step ------------------------------------------------------------
Info "`n== Done =="
Info "Fully QUIT Claude Desktop (system tray -> Quit, not just close the window),"
Info "then reopen it so it spawns a fresh mcp-remote proxy against $Server."
