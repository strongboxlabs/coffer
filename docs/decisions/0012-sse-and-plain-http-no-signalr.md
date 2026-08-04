# 0012 — SSE for streaming, plain HTTP for commands; no SignalR

* Status: Accepted
* Date: 2026-05-08

## Context

The original architecture doc (§2.2) listed three streaming patterns:

1. Server-Sent Events (SSE) for sync-pipeline-to-UI push.
2. PostgreSQL `LISTEN`/`NOTIFY` for backend pub/sub.
3. WebSockets / SignalR for "bidirectional — user triggers manual re-sync from
   UI and watches progress in real time".

Pattern (3) is the one we're revisiting. SignalR's strengths — multi-client
fan-out, transport negotiation, backplane support, strongly-typed RPC — are
sized for apps with many users on flaky networks across multiple replicas.
Coffer is one user, two-or-three concurrent browsers, single backend.

The "bidirectional" need is also illusory. A button-click flow looks like:

1. Browser → server: "start a sync" (one HTTP request)
2. Server → browser: progress events (streaming)
3. Server → browser: "done" (one event)

That's not bidirectional. It's a command followed by streamed status. SSE
already covers (2) and (3); plain HTTP `POST` covers (1).

Reverse-proxy considerations were also weighed:

- **SSE through Traefik** is plain HTTP. One config knob (`X-Accel-Buffering=no`)
  prevents response buffering. Forward-auth (Authelia / Authentik / Cloudflare
  Access) re-validates on the GET, cookies flow naturally.
- **SignalR through Traefik** works (WebSocket upgrades are standard) but adds
  more config surface (idle timeouts, the long-polling fallback's sticky-session
  needs, the upgrade-time auth check).
- **The EventSource API can't set custom headers**, so SSE pushes you toward
  cookie-based auth — which is what every reverse-proxy auth solution defaults
  to anyway, and which our chosen WebAuthn flow
  ([0013-webauthn-passkey-auth.md](0013-webauthn-passkey-auth.md)) uses.

The proxy story therefore favours SSE slightly; nothing about it favours
SignalR.

## Decision

Drop SignalR from the stack.

The streaming/command surface becomes:

- **SSE (`text/event-stream`)** via `System.Net.ServerSentEvents` for every
  server-to-browser stream: sync progress, new-transaction notifications,
  pending-review-count updates.
- **Plain HTTP `POST`** (Minimal API endpoints, JSON in / JSON out) for every
  browser-to-server command, including manual sync triggers.
- **PostgreSQL `LISTEN`/`NOTIFY`** remains the in-process pub/sub between the
  sync worker and the SSE controller. Unchanged.

If a real bidirectional need ever materialises (it hasn't yet, and this app's
shape doesn't suggest one), this ADR is superseded rather than amended.

## Consequences

**Positive**
- One streaming pattern in the codebase, not two.
- No `Microsoft.AspNetCore.SignalR` dependency, no `@microsoft/signalr` client.
- Reverse-proxy config stays minimal: one TLS cert, one forwarding header for
  SSE, no WebSocket-upgrade timeouts to tune.
- The `System.Net.ServerSentEvents` + `IAsyncEnumerable<T>` combo in .NET 10
  makes the server-side stream a few lines.

**Negative**
- If we ever needed multi-instance with cross-replica fan-out, we'd add a
  Redis pub/sub on top of `LISTEN`/`NOTIFY` rather than getting a SignalR
  backplane for free. Acceptable; the multi-instance scenario isn't on the
  roadmap.
- We give up SignalR's typed-hub RPC ergonomics. The trade is one extra `fetch`
  call per command, against keeping the surface small. Worth it.

## Alternatives considered

- **Keep SignalR per the original spec.** Adds complexity that the use case
  doesn't justify. Rejected.
- **Plain WebSockets without SignalR.** More code than SSE for a strictly
  one-direction stream, no real win. Rejected.
- **Long-polling.** Outdated; SSE is the modern equivalent and is well-supported
  everywhere it matters. Rejected.
