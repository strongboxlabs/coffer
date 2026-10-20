# 0011 — Target .NET 10 for the API and worker layer

* Status: Accepted
* Date: 2026-05-08

## Context

The architecture doc as originally written named ".NET 8/9 — Minimal API style"
as the API stack. Phase 3 starts the .NET work, and we need to commit to a
specific runtime/SDK before the first project file lands.

.NET 10 (released November 2025) is the current Long Term Support release. Real
properties that matter for Coffer:

- **Built-in OpenAPI document generator** (`Microsoft.AspNetCore.OpenApi` is
  promoted in 10), removing the Swashbuckle dependency for the common case.
- **`System.Net.ServerSentEvents` namespace** with first-class SSE support on
  both the server and the `HttpClient` side. This was previously
  hand-rolled — for a project where SSE is the streaming primitive
  ([0012-sse-and-plain-http-no-signalr.md](0012-sse-and-plain-http-no-signalr.md)),
  the in-box support is meaningful.
- **Minimal API improvements**: typed results everywhere, faster route table,
  better cancellation/`IAsyncEnumerable` interop.
- **Smaller and faster containers**: chiseled / Alpine-variant base images
  bring the deployable image well below 100 MB.
- **C# 14**: collection expressions, default lambda parameters, primary
  constructors used pragmatically. Marginal but compounding.
- **Long Term Support** through November 2028. We don't pay a forced-upgrade
  cost during the build-out window.

## Decision

The API and any .NET worker projects target **.NET 10** with **C# 14**, using:

- Minimal API style (per the architecture doc).
- The in-box OpenAPI generator (`Microsoft.AspNetCore.OpenApi`). Swashbuckle
  is added only if a feature gap appears.
- `System.Net.ServerSentEvents` for SSE wiring on both sides.
- Native AOT is **not** a requirement for Phase 3; the API is a long-running
  server, not a CLI. We compile JIT and revisit if cold-start ever matters.

## Consequences

**Positive**
- One LTS target through 2028; no SDK churn during initial build-out.
- Less third-party dependency surface (OpenAPI, SSE).
- Smaller container images.

**Negative**
- The dev environment requires the .NET 10 SDK. CI pulls the
  `mcr.microsoft.com/dotnet/sdk:10.0` image. Not a real cost.
- Anyone forking the project later inherits this floor; raising it later is
  cheap, lowering is hard.

## Alternatives considered

- **.NET 9 (STS).** Standard-Term Support; ~14-month lifecycle. We'd be
  forced to upgrade during Phase 4–5. Rejected.
- **.NET 8 (previous LTS).** Stable, supported through November 2026. Misses
  the in-box SSE support and OpenAPI promotion. Rejected.
- **Polyglot service split (Go/Rust for the sync worker, .NET for the API).**
  Operational and language-skill cost outweighs any throughput win at this
  scale. Rejected as YAGNI.
