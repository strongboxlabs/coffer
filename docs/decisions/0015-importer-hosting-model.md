# 0015 — Moneydance importer is a .NET 10 console app

* Status: Accepted
* Date: 2026-05-08

## Context

Phase 2 ([architecture.md](../architecture.md) §8) is the Moneydance JSON
importer. The architecture doc describes it as a backend ingestion component
but does not specify the implementation shape: language, runtime, deployment
mode, or relationship to the future API.

The realistic options are:

1. **A standalone Python or Node script** that talks to Postgres directly.
   Quick to write; lives outside the .NET solution; no shared types with the
   future API.
2. **A .NET 10 console app** in the same Visual Studio solution as the future
   API. Same language, same DTOs, same DB access layer.
3. **An `IHostedService` inside the future API project** that runs once.
   Couples the importer to the API's lifecycle; awkward for a one-shot
   migration tool.
4. **Inline `psql \copy` + raw SQL.** Insufficient — the JSON shape (embedded
   splits as `0.*`/`1.*` keys, nested investment-txn fields, etc.) is too
   complex to translate in pure SQL.

## Decision

The importer is **a .NET 10 console application**, hosted in the same
solution that will hold the future API and worker. Specifically:

- **Solution:** `Coffer.sln` at the repo root.
- **Project:** `src/Importer.Moneydance/Importer.Moneydance.csproj`,
  targeting `net10.0`.
- **Tests:** `tests/Importer.Moneydance.Tests/Importer.Moneydance.Tests.csproj`
  (xUnit).
- **Command parsing:** [Spectre.Console.Cli](https://spectreconsole.net/cli/)
  — composable subcommands, strongly-typed settings, well-supported. Adds
  one top-level dependency; rationale: hand-rolling CLI parsing for a tool
  with multiple subcommands (`import`, `validate`, `dry-run`, etc.) is the
  kind of thing where the right library pays for itself in the first PR.
- **Hosting:** `Microsoft.Extensions.Hosting` generic host so DI,
  configuration (env vars + appsettings), and logging are conventional from
  day one. The console app composes a host, runs the requested command, and
  exits.

Other projects (the API, the SimpleFIN sync worker, etc.) are added to
`Coffer.sln` **only when their phase begins**. We do not pre-create empty
project shells — that's the kind of premature abstraction
[engineering-standards.md §1](../engineering-standards.md) explicitly forbids.

## Consequences

**Positive**
- The importer's DTOs (Moneydance JSON shapes) and the soon-to-exist DB
  access types live in the same solution and compile together. The API will
  reuse `Coffer.Domain` types when it lands in Phase 3.
- One language story across all backend components.
- Unit and integration tests for the importer use the same xUnit/runner
  setup the API will adopt.
- A `dotnet publish` produces a self-contained executable — useful if the
  importer ever needs to run on a host without the SDK installed.

**Negative**
- Slightly more ceremony than a quick Python script. Acceptable cost for a
  tool that touches years of financial data and benefits from typed DTOs.
- A `Spectre.Console.Cli` dependency. Stable, mature, well-maintained; the
  alternative is hand-rolling argument parsing for what will eventually be
  multiple subcommands.

## Alternatives considered

- **Python script.** Fast to write but produces an island in a .NET-shaped
  codebase. Rejected.
- **Node/TypeScript script.** Same objection as Python plus the project
  doesn't otherwise need a Node runtime in the backend. Rejected.
- **.NET inside an `IHostedService` of the API project.** Couples a one-shot
  data-migration tool to the long-running API's lifecycle. The API would
  have to know about importer state, dependencies, and logging shape.
  Rejected.
- **Inline `\copy` + SQL.** The JSON normalization (embedded splits, nested
  investment fields, MD type-code translation) is genuinely program code,
  not transformation SQL. Rejected.
