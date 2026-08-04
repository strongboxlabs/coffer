# 0081 — MCP write control-plane (making writes safe to enable)

* Status: Accepted
* Date: 2026-07-20
* Amends: [0068](0068-mcp-write-surface.md) (D4 — see D6 below)
* Relates to: [0063](0063-mcp-server.md) (MCP server, §D7 audit), [0068](0068-mcp-write-surface.md) (write surface + the `mcp.writes_enabled` gate)

## Context

The MCP write surface (ADR-0068) ships off by default behind `mcp.writes_enabled`.
Before it can be responsibly turned on, the control-plane around it needs real
access control. The load-bearing gap: **token scopes are decorative** — every MCP
token is minted `coffer.read`, the `"scope"` claim is written but never read, and the
write tools carry no authorization check — so the moment the global switch flips on,
*any* authenticated token can call *every* write tool. Secondary gaps: the gate is
read only at startup (off needs a restart); there is no per-call audit; anonymous
Dynamic Client Registration (`/oauth/register`) is unrate-limited; and OpenIddict
DCR clients can't be listed / revoked / pruned without direct DB access (and the
50-client cap is a hard block).

Everything here **strengthens** authentication, logging, and access control. Writes
remain **off by default**; this makes them *safe to enable*, it does not enable them.

## Decisions

### D1 — Real `coffer.write` scope + per-tool enforcement
Add a `coffer.write` scope. The MCP write tools require it; a token without it is
rejected with a clear error (not a silent no-op). Read remains the default; a token
carries `coffer.write` only via an **explicit opt-in**: the manual-bearer mint
(`McpTokensEndpoints`) takes a `Writable` flag, and an OAuth client requests the scope
at authorize with the user consenting. The gate on *actual* writes is the kill-switch
(D2) + `McpWriteGuard`, not the OAuth scope permission. A `coffer.read` token can never
write — a real check (`McpWriteGuard`, both auth paths), not the absence of the tool.
(The OAuth scope is NOT gated per-client — see the 0.30.1 revision below.)

### D2 — Hot kill-switch
The `writes_enabled` gate moves from startup-registration to a **per-request** check:
the write tools are always registered, but each rejects when writes are disabled,
reading a runtime flag the admin toggle updates immediately. Turning writes off in
Settings takes effect at once — no restart. (The master `mcp.enabled` switch may keep
its restart semantics.)

### D3 — Per-call write audit
A new `mcp_tool_invocations` table (the `mcp_access_tokens` pattern — service-role
writes + own-user RLS as defence-in-depth): user, tool name, bounded/serialized args,
outcome (a `status` lifecycle field + a bounded result summary; ADR-0086), a best-effort `ledger_id` lift, and a
timestamp. Recorded by `McpAuditRecorder` from a single **CallTool request filter**
(the SDK's `WithRequestFilters` → `AddCallToolFilter` pipeline) that wraps every tool
call and records the WRITE ones — matched by `McpWriteTools.ToolNames`, so a new write
tool is audited automatically. Reads are not recorded (high volume, low audit value).
The recorder's logic (arg summarization/bounding, ledgerId lift, scope) is unit-tested;
that the SDK routes registered-tool calls through the filter is validated end-to-end on
dev with a real MCP client — the project's MCP test convention (see McpEndpointTests),
since there is no in-process pipeline harness. Admin-viewable (D5 surfaces it). Satisfies
ADR-0063 §D7 / ADR-0068 D6.

### D4 — Rate-limit anonymous DCR
A per-IP fixed-window rate-limit policy on `/oauth/register` (the recovery-login
limiter is the template). The 50-client cap stays as a separate ceiling.

### D5 — OAuth/DCR client management
Admin endpoints (`/api/admin/mcp/*`, RequireAdmin) + a Settings surface to **list /
revoke / prune** OpenIddict applications (revoke deletes the app plus its
authorizations + tokens; prune removes clients with no authorizations, recovering the
50-cap) + the **D3 audit log** (view + clear). So a rogue or stale DCR client can be
seen and killed without DB access. (No per-client write grant — see the 0.30.1
revision below.) (Npgsql has no MARS, so each endpoint materializes an OpenIddict
enumeration before issuing nested store commands.)

### D6 — `set_transaction_tags` is BULK (amends ADR-0068 D4)
ADR-0068 D4 mandated "one entity per call, no batch." Tags are the deliberate
exception: `set_transaction_tags` accepts **multiple transactions** in one call.
Rationale — tag assignment is an idempotent replace-set on a junction table (low
blast radius, no cascade), and bulk tagging is the natural LLM use ("tag these as
X"). The riskier structural writes (category/security/transfer mutations) keep
one-entity-per-call. Backed by a new public `TransactionsRepository`
tag-assignment method extracted from the existing private replace-set logic (with a
header-in-ledger guard), run under RLS like the other write tools (ADR-0068 D5).

## Consequences

- MCP writes become safe to enable: read tokens can't write, off is immediate, every
  write is audited, DCR is bounded + its clients manageable.
- One PR (control-plane spans backend security + a settings UI + a schema + the tag
  tool); dev-validated before merge. Writes stay off by default.
- ADR-0068 D4's no-batch rule now reads "one entity per call, **except tags** (D6)."

## Revision — write gating is the kill-switch, not per-client (0.30.1)

D1/D5 originally gated OAuth write **per-client** (DCR clients read-only; an admin
grants `coffer.write` per client via D5). This did not survive contact with
`mcp-remote`, which requests **every** advertised scope: the moment 0.30.0 advertised
`coffer.write`, a read-only DCR client requesting it was rejected by OpenIddict
(ID2051) — breaking the *entire* connection, not just writes.

Reconciled in **0.30.1**: `IgnoreScopePermissions()` lets any client request
`coffer.write` without a per-client rejection, so the connection succeeds; the **sole
write gate is the runtime kill-switch (D2) + `McpWriteGuard`** — the token must carry
`coffer.write` (opted in via the OAuth request+consent, or the mint `Writable` flag)
AND writes must be enabled deployment-wide. The per-client write-grant endpoint +
Settings toggle were removed. `coffer.read`-only tokens still can't write, and writes
stay off by default — the opt-in is now consent/`Writable`, not an admin per-client
grant. Lesson: an advertised scope is one every client will request — don't advertise
what only some clients may use.

Follow-up in **0.30.2**: the OAuth consent screen (`ConsentPage`) now derives its copy
from the granted scope instead of hardcoding "read-only". When the token carries
`coffer.write` it says so plainly (full read+write access), with the caveat that writes
are subject to the global admin switch (off by default); a `coffer.read`-only token
still shows the read-only copy. This closes the gap where the screen claimed "no
changes" while handing out the write scope — consent is now accurate to what is
granted, and the kill-switch remains the runtime gate on whether writes execute.
