# 0086 — MCP write observability: durable audit + application-log parity

* Status: Accepted
* Date: 2026-07-25
* Related: ADR-0081 (MCP write control-plane + `mcp_tool_invocations` audit, D3), ADR-0020 (dual-role RLS: `coffer_app` / `coffer_service`), the RFC 9457 ProblemDetails envelope + `RequestScopeMiddleware` correlation

## Context

Diagnosing a report of MCP write tools ("timing out") exposed that the write path is effectively **unobservable**, in two independent ways:

1. **The write-audit silently loses rows.** `McpAuditRecorder.RecordAsync` ran `SaveChangesAsync(cancellationToken)` on the *caller's* token, and `McpAuditFilter` invoked it — including on the exception path — with that same token. When a client cancels/times out, the token is already cancelled, so the audit write aborts and the `catch {}` swallows it. The audit is also written in a *separate* transaction *after* the tool returns, so a call that never returns (hang/timeout) records nothing at all. Result: the writes an oversight log most needs to capture — abandoned, timed-out, errored-under-cancellation — are exactly the ones dropped, with no trace.

2. **The application log is blind on the MCP path.** The synchronous HTTP path is adequately instrumented — `UseExceptionHandler` logs unhandled exceptions at Error, ProblemDetails returns a `traceId`, and `RequestScopeMiddleware` scopes `traceId`+`userId`. But MCP tool calls sit *outside* that: the SDK catches a tool's exception and converts it to a JSON-RPC `IsError` result **before** it reaches `UseExceptionHandler`, so it is never logged; there is no per-tool logging (start / outcome / duration / cancellation), and prod has no request access log (`Microsoft.AspNetCore` is pinned to `Warning`) and no tracing (OpenTelemetry is `IsDevelopment()`-gated). A tool that threw, errored, or was cancelled produced *nothing* on stdout.

These are distinct concerns — a durable DB **record of what changed** (oversight, user-facing) versus operator-facing **stdout diagnostics of what the server did** — and both must be trustworthy.

## Decision

### Track A — the audit records durably (no silent loss)

`mcp_tool_invocations` becomes a **two-phase** record, both phases written by the `coffer_service` role on `CancellationToken.None` (decoupled from the caller's cancellation):

- **Attempt (pre-call).** Before the tool runs, insert a row with `status = 'pending'` (tool, bounded args, user, `ledger_id`, `trace_id`). Because it is written *before* the mutation, **every committed change is guaranteed to already have a row** — the "change with no trail" hole is closed by construction, not by timing. A hang / timeout / crash leaves this `pending` row as a visible "started, outcome unknown" marker.
- **Finalize (post-call).** After the tool returns or throws, update the row to `status ∈ {ok, error, cancelled}` with a bounded result summary and `completed_at`. `cancelled` is a first-class terminal state distinct from `error`, so a client timeout is unambiguous.
- **The tool *outcome* is deliberately NOT captured inside the mutation transaction.** ok/error/result is only known *after* that transaction has committed, so transactional atomicity of the *outcome* is physically impossible; the pre-written `pending` row provides the change-integrity guarantee instead.
- **Auditing never breaks a tool call, but never fails silently.** A failure to record or finalize is logged at Error (with `traceId`), not swallowed into a bare `catch`.

Schema (migration 178): add `status TEXT NOT NULL DEFAULT 'pending'` (CHECK in `pending|ok|error|cancelled`), `completed_at TIMESTAMPTZ NULL`, `trace_id TEXT NULL`. `is_error` was initially retained and kept in sync (`is_error = (status = 'error')`) so the existing admin viewer stayed unchanged; existing rows backfill to a terminal state (`status = is_error ? 'error' : 'ok'`, `completed_at = created_at`). No RLS change: `coffer_service` already has `GRANT ALL`. **Follow-up (migration 184): `is_error` was dropped** and the admin viewer now reads `status` directly, so `pending`/`cancelled` are visible in the UI (see the follow-up-sweep note below).

### Track B — application-log parity across HTTP and MCP

- **The MCP tool layer logs to HTTP parity.** A CallTool concern opens a logger scope of `{tool, ledgerId, userId, traceId}` and logs: **start** at Debug (so a call that then hangs is visible), **completion** with duration + outcome at Information, an **`IsError` result** at Warning with the reason, an **exception at Error with the exception** *before* it is swallowed into an `IsError` protocol result, and **cancellation at Warning** (`cancelled after {duration}`). Never silent.
- **Request access log.** A middleware logs one Information line per request (method, path, status, duration) covering both the HTTP API and the `/mcp` transport, while framework categories stay at `Warning` (no Kestrel/routing spam).
- **Structured JSON console in production** (the built-in `AddJsonConsole` formatter — no new dependency) so `traceId`/`userId`/`tool`/`ledgerId` are queryable fields; dev keeps the human-readable formatter.
- **Correlation.** The existing `traceId` (`HttpContext.TraceIdentifier`) is added to the MCP tool scope and stored on the audit row, tying client response ⇄ app-log line ⇄ audit record.

### Explicitly out of scope (deferred to the follow-up sweep)

Prod OTLP tracing exporter (adds a dependency + infra decision); per-endpoint business-outcome logging on the native HTTP API; and a codebase-wide audit for the same two classes of gap (every mutating operation's audit trail; every failure path's app-log) across non-MCP surfaces.

**Update (follow-up sweep):** per-endpoint business-outcome logging shipped — `BusinessError.Problem` now stamps its stable `code` into `HttpContext.Items` as the result executes, and `RequestAccessLogMiddleware` appends it to the access line on a business rejection (`-> 422 (ledger-not-visible)`), covering every coded 422 uniformly (all flow through that one factory). The prod OTLP exporter remains deferred until a collector exists to receive spans.

## Consequences

- A cancelled / timed-out / hung write tool is now always visible — as a `cancelled`/`error` row or a `pending` row in the audit, and as a leveled, correlated line in the application log. The original silent-loss defect is eliminated.
- One extra pre-write per MCP write call (a `pending` insert). MCP writes are low-volume and admin-gated (ADR-0081), so the cost is immaterial.
- `is_error` and `status` were momentarily redundant (kept in sync); the follow-up sweep collapsed the admin viewer onto `status` and dropped `is_error` (migration 184), so the four lifecycle states — including `pending` and `cancelled` — now render in the admin UI.
- Residual: a process crash between a mutation's commit and the finalize update leaves a `pending` row. This is intentional — a visible unknown-outcome marker is strictly better than a lost record, and the outcome cannot be made transactional (see Decision).
