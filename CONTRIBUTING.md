# Contributing to Coffer

> **Outside contributions are not being accepted at present — please don't spend
> time on a pull request.** That isn't unfriendliness, it's mechanics: Coffer is
> developed privately and published as periodic source snapshots, so commits here
> share no ancestry with upstream development and there is no branch to merge into.
>
> What *is* useful: bug reports (if issues are enabled) with the version from
> `/api/meta/version`, what you did, and what you expected — describe money-math
> problems by transaction *shape*, and please keep real account numbers,
> institution names and statements out of a public issue. **Security issues go
> through GitHub's private vulnerability reporting, never a public issue** — see
> [SECURITY.md](SECURITY.md).
>
> Forking is explicitly fine under the [AGPL-3.0](LICENSE). Note its network
> clause: offer a modified version to others over a network and you must make your
> modified source available to those users.
>
> The rest of this document is the **internal** workflow reference. It is published
> because it describes the standards the code is held to, not because it is an
> invitation.

Coffer is a single-author personal project, but it is built and maintained to
professional standards. The rules in this document are the same whether the
contributor is the author, a future collaborator, or an AI assistant.

The full coding charter — testing, migrations, documentation, the "no hacks"
rule — is in [docs/engineering-standards.md](docs/engineering-standards.md).
This file is the short-form workflow reference.

## Workflow

1. **Read the architecture doc.** [docs/architecture.md](docs/architecture.md)
   is the source of truth. If your change disagrees with it, update the doc
   first (or write an ADR — see below).
2. **Branch.** Trunk-based. Branches are short-lived: `feat/...`, `fix/...`,
   `chore/...`, `docs/...`, `refactor/...`, `test/...`.
3. **Make the change.** Schema changes ship a migration file *and* an update to
   [docs/database-schema.md](docs/database-schema.md) in the same commit.
   Material design decisions ship an ADR in
   [docs/decisions/](docs/decisions/).
4. **Test it.** SQL changes get a verification script under
   [db/test/](db/test/). Code changes get unit/integration tests.
5. **Open a PR.** Use the template. CI must pass.
6. **Squash and merge.** `main` stays linear.

## Commit messages

Conventional Commits:

```
<type>(<scope>): <subject>

<body — explain WHY, not WHAT>

<footer — refs, breaking-change notes>
```

Types: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `build`, `ci`, `perf`.
Scopes (current): `db`, `importer`, `api`, `web`, `sync`, `infra`, `docs`.

## When to write an ADR

Write an Architecture Decision Record for any decision that:

- Adds, removes, or replaces a major dependency.
- Changes a documented invariant in [docs/architecture.md](docs/architecture.md).
- Introduces a deviation from the architecture doc.
- Picks one approach over a non-trivial alternative that future-you might second-guess.

ADRs go in [docs/decisions/NNNN-kebab-case-title.md](docs/decisions/) and follow
the [MADR](https://adr.github.io/madr/) format. Don't edit a merged ADR — write a
new one and mark the old one `Superseded by ADR-NNNN`.

## When to update docs

Documentation updates ship in the **same commit** as the code change they
describe. Stale docs are bugs.

Specifically:

- Schema change → update [docs/database-schema.md](docs/database-schema.md).
- Operational change (run command, env var, port) → update
  [docs/operations.md](docs/operations.md).
- New domain term → add to [docs/glossary.md](docs/glossary.md).
- Material design change → ADR + update [docs/architecture.md](docs/architecture.md)
  to point at it.

## Code review

Even self-review counts. Before requesting/self-approving a PR:

- [ ] Tests added or updated for the behaviour change.
- [ ] Docs updated in the same PR.
- [ ] No commented-out code, no unused imports, no `// TODO` without a tracked
      issue.
- [ ] CI green.
- [ ] PR description explains *why*, not just *what*.

## CI

CI runs on a **self-hosted runner** on the dev host (label `coffer-dev`): the repo is
private, GitHub-*hosted* minutes are billed (and were exhausted), and self-hosted runners
consume **zero** hosted minutes. Runs are async — merge on local-green
(`scripts/preflight.sh`, the exact CI equivalent); CI is the automatic backstop and only
pings on red.

**Triggers:** `pull_request` only — no `push:main` (a PR's run already validated that exact
tree, so re-running on merge just doubles the work). `ci.yml` carries `paths-ignore` for
`**.md` / `docs/**` / `mockups/**`, so a docs-only PR skips the heavy suite; the
internal-markdown-link check then runs from its own lightweight **`doc-link.yml`** (triggered
on `**.md`), keeping that coverage without the .NET/web/schema cost.

| Job (`.github/workflows/ci.yml`) | What it does |
|---|---|
| Schema apply + trigger verification | Postgres 16 service container (host port **15432** — avoids the dev stack's 5432 and stays below Windows' 49152+ dynamic-exclusion range so a WinNAT reboot-reshuffle can't reserve it), applies `db/migrations/`, runs `db/test/` |
| API no-raw-sql audit | `scripts/audit-no-raw-sql.sh` |
| .NET test (× matrix) | Builds the solution, runs the integration suite **sharded 4 ways** by namespace (+ an importer entry) — each shard its own process + its own Testcontainers Postgres. The `rest` shard is the complement (`!~` of the others) so coverage is total — same partition as `scripts/preflight.sh`. |
| Web build + test | `npm ci` + typecheck + lint + vitest + production build |

A red CI never merges. If a check is flaky, fix the flake — never re-run blindly to make it green.

### The runner

Lives in **WSL2 Ubuntu** on the dev host (systemd service, Docker via Docker Desktop's WSL
integration). A bare runner needs provisioning a GitHub-hosted image has pre-baked — these
are required and were each a real failure during cutover:

- **RAM**: `.wslconfig` `memory` ≥ **16 GB** + a `swap` file. At the WSL default 6 GB / 0 swap
  the suite OOM-killed the runner (vitest fans out one worker per core; ~20 here).
- **`postgresql-client`** installed in the distro (the schema job's `psql`).
- **`DOTNET_INSTALL_DIR`** routed to `$RUNNER_TOOL_CACHE/dotnet` in `ci.yml` — the non-root
  runner user can't write `setup-dotnet`'s default `/usr/share/dotnet`.
- The custom label `coffer-dev` is declared in `.github/actionlint.yaml` so `actionlint` is clean.
- **ghcr package access for `release.yml`**: the `ghcr.io/<owner>/coffer` package must grant the
  **Coffer repo Write** under its *Manage Actions access* settings, or the Actions `GITHUB_TOKEN`
  gets `403 denied: write_package` on push. For user-owned packages this grant can need a retry
  in the GitHub UI. The fallback is the manual `docker build … && docker push` in *Releasing*
  below (with a fresh `write:packages` PAT via `docker login ghcr.io`).

**Add a runner** (more parallelism — jobs serialize per runner): register another in WSL2 with
the same label — `./config.sh --url https://github.com/<owner>/Coffer --token <T> --labels coffer-dev --name coffer-dev-N --unattended`, then `sudo ./svc.sh install && sudo ./svc.sh start`.
**Maintenance**: `runner-maintenance.yml` prunes Docker weekly. **Security**: only safe because
the repo is **private + solo** (dependabot PRs execute on the host); never make the repo public
with a runner attached.

## Releasing

Every published container image gets its **own semver** — the container tag must equal the
build version inside it ([docs/decisions/0044-version-surfacing.md](docs/decisions/0044-version-surfacing.md)).
Don't relabel an image with a higher tag than its `<Version>`; that's a lie the About panel
exposes.

1. **Bump the version** with `scripts/release.sh <semver>` (MINOR for a feature, PATCH for a
   fix, pre-1.0). It is the single writer of the version across all three manifests
   (`src/Api/Api.csproj` `<Version>`, `src/Web/package.json` `version`, and the two
   `package-lock.json` `version` fields) — `npm version` for the npm pair, `sed` for the
   csproj — then verifies they match (failing loudly on any drift), commits
   `chore(release): <semver>`, and tags `v<semver>`. Run from a clean `main`. Use
   `--dry-run` to preview. Don't hand-edit the version files — that reintroduces the drift
   the script exists to prevent (the container tag must equal the build's `<Version>`, else
   the About panel exposes the lie).
2. **Push** `git push origin main && git push origin v<semver>`. The tag triggers
   `release.yml` on the self-hosted runner to build + push the image to ghcr.
   (Land the change itself on `main` first — branch → PR → squash-merge on green — then run
   step 1 on the updated `main`.)
3. **Fallback — build + push by hand** if the runner is down (the dev host has Docker + ghcr
   write creds):
   `docker build --platform linux/amd64 -t ghcr.io/<owner>/coffer:<semver> -t ghcr.io/<owner>/coffer:latest . && docker push ghcr.io/<owner>/coffer:<semver> && docker push ghcr.io/<owner>/coffer:latest`
4. **Deploy**: on each host set `COFFER_IMAGE_TAG=<semver>` in `.env`, then
   `docker compose pull api && docker compose up -d api`.

Caveat: container images build `.git`-less (`.dockerignore` excludes it), so the
build-number / commit axes show `0` / `nogit` inside a shipped image — **semver is the
meaningful axis for a published artifact**, which is why it moves every build.

## Reporting security issues

See [SECURITY.md](SECURITY.md). Don't open public issues for security problems.
