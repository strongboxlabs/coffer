# 0088 — Setup asks one question; the hub owns ledger creation

* Status: Accepted (amended 2026-11-18 by
  [ADR-0115](0115-the-demo-is-generated-imported-and-seeded-from-the-hub.md): setup asks
  no ledger question at all; `includeDemo` and the response's `ledgerId`/`ledgerName` are
  removed, and the Demo is added from the hub)
* Date: 2026-08-02
* Supersedes: [ADR-0071](0071-install-provisioning-ui-import-authed-restore.md) D1 (pre-user `provision` CLI) and narrows its D5 (starter categories)

## Context

A fresh install offered the first user a ledger choice that was a lie.

Migrations created two **empty placeholder ledgers** — `Default` (…0001) and,
from migration 055, `Demo` (…0002). They existed so `coffer-api provision
--mode <clean|demo>` could resolve them before the first user was created:
`clean` deleted both, `demo` seeded Demo from the bundled sample dataset.

That only works if `provision` runs, and `scripts/dev-up-docker.sh` — the
README's "one dev path" — never calls it, so every fresh dev install keeps both
placeholders. `GET /setup/{token}/info`
returned every row in `ledgers`, and the setup form rendered them in a dropdown
under **"Use an existing ledger"**, preselecting the first. The result:

- The pre-checked, zero-effort path was "join `Default`" — an empty shell.
- Nothing indicated the ledgers were empty; `SetupAvailableLedger` deliberately
  omitted account counts as *"irrelevant for the choice."*
- **"Demo"** promised a demo and delivered nothing.
- Joining an existing ledger skipped the starter-category seed, commented
  *"joining an existing ledger inherits its categories"* — true of a real
  ledger, false of a placeholder. So the ledger had no categories either.
- The only option that produced anything usable was **"Create a new ledger"**,
  which sounds like the empty one.

A user who picks "Demo" gets a ledger with no accounts, no categories, and no
transactions. The seeding does not fail; it is never invoked.

The mandatory ledger choice was itself justified by a constraint that has since
expired. Its comment warned that a user with no grants *"lands on a
permission-denied empty register."* That predates the ledger hub, which now
renders a proper empty state with **New ledger** and **Import from Moneydance**.

## Decision

**Setup asks exactly one question: do you want a Demo ledger? Everything else
about ledgers moves to the hub.**

*Amended 2026-11-18:* setup asks no question about ledgers, and the Demo moves to
the hub with the rest; see the amendment below.

1. **Migrations stop creating placeholder ledgers.** Migration 186 deletes
   `…0001`/`…0002`, guarded so it can only ever fire on a pre-first-user install
   (no `webauthn_credentials`) where the ledger is empty (no accounts, no
   transaction headers). On an install already in use it is a no-op — `Default`
   holds real data there and must never be touched.

2. **`/setup/{token}/info` returns no ledger list.** The endpoint remains; token
   validation was always its real job. `SetupAvailableLedger` is deleted.

3. **`/complete` takes `includeDemo: bool`** instead of the mutually-exclusive
   `existingLedgerId` / `newLedgerName` pair. The ledger-choice gate and its four
   error codes (`setup-ledger-choice-required`, `-conflict`, `-not-found`,
   `-name-required`) are retired. `ledgerId`/`ledgerName` in the response are now
   nullable — **no ledger is the normal outcome**.
   *Amended 2026-11-18:* `includeDemo` is removed, and so are `ledgerId` and
   `ledgerName`: no ledger is the only outcome.

4. **The Demo ledger is a normal import.** `ProvisioningService.ProvisionDemoAsync`
   creates a *new* ledger named `Demo` owned by the new user, via the same
   pipeline as the in-app Moneydance import — new-ledger-only, satisfying the
   ADR-0052 seed-once guard. It no longer writes into a fixed pre-existing id.
   It runs **post-commit and best-effort**: setup has already succeeded, so a
   slow or failing import costs the sample data, never the passkey registration.
   *Amended 2026-11-18:* `ProvisioningService` is deleted. The Demo is added from
   the hub as a background import job that reports its progress and its failure
   (ADR-0115 D2, D3).

5. **Demo does not get starter categories.** The sample dataset carries its own
   category tree; layering the starter catalogue on top would duplicate it.
   ADR-0071 D5 still applies to ledgers created empty, from setup's replacement
   (the hub) or the "New ledger" dialog.

6. **Post-setup lands on the hub (`/`), not `/welcome`.** `WelcomePage` is
   deleted: it hardcoded *"{ledger} is empty"* (false for a seeded Demo) and
   advertised the shipped in-app importer as *"on the roadmap"*.

7. **The `provision` CLI and `scripts/provision.sh` are retired.** With no
   placeholders there is nothing to "clean," and the demo seed is an
   authenticated import triggered from setup. `dev-up-docker.sh` now surfaces the
   first-run `/setup` URL itself, so the one dev path is genuinely one command.
   *Amended 2026-11-18:* the Demo is an authenticated import started from the hub,
   not from setup (ADR-0115 D2).

8. **There is no well-known "default ledger" id any more.** `LedgerRow.DefaultId`
   is deleted from both the API and importer models, and
   `LedgersRepository.ResolveOrCreateAsync` / `ResolveForValidationAsync` no
   longer fall back to …0001 — they throw. **The importer CLI now requires
   `--ledger-name` or `--ledger-id`**, validated up front so the operator gets a
   usage error rather than a stack trace.

   This is deliberately a breaking change to the CLI. Deleting the row while
   leaving an implicit fallback pointing at it would have left the importer
   working only by accident on old installs and failing confusingly on new ones.
   And guessing a destination for a bulk financial import is the wrong default
   regardless: a mistyped flag would silently write someone's books into the
   wrong ledger.

   *Amended 2026-10-25:* the importer CLI's `import` command is removed (the in-app
   wizard is the only import path; ADR-0071, amendment of that date), so neither flag
   exists. `ResolveForValidationAsync` had no caller and is removed with it.
   `ResolveOrCreateAsync` is replaced by `CreateOwnedAsync`, which always creates
   the ledger and never resolves one by id or name (ADR-0052, amendment of that
   date); a blank name is still refused, so there is still no implicit target.

   Tests keep a stable ledger id, but as a fixture concern: `TestLedger.Id`
   (deliberately **not** …0001, so nothing can pass by depending on the old
   magic value) is seeded by `PostgresFixture` after migrations.

### Amendment, 2026-11-18 — setup asks nothing about ledgers ([ADR-0115](0115-the-demo-is-generated-imported-and-seeded-from-the-hub.md))

The one question goes too. Setup creates no ledger: `/complete` takes no
`includeDemo`, and its response carries no `ledgerId` or `ledgerName`. A client
still sending `includeDemo` is not refused; the field is ignored. Every install
starts on an empty hub, and the welcome panel after setup points there.

The Demo is offered by the hub instead, on every install and to any signed-in
user, and each press adds a new ledger, "Demo", "Demo 2" and so on. It is
generated for the day it is added and built as a background import job whose
progress and failure the hub shows (ADR-0115 D1-D3, D9). `ProvisioningService`
is deleted. The rest stands: there are still no placeholder ledgers and no
ledger list at setup, a Demo still brings its own categories rather than the
starter catalogue, post-setup still lands on the hub, and there is still no
default ledger id.

## Consequences

**A fresh install can no longer present an empty ledger as a real one.** Every
ledger a user ends up with is created through the app, so it always carries
either starter categories or an imported dataset. The failure mode the ADR fixes
is structurally impossible rather than patched.

**Zero ledgers is a supported state.** The hub's empty state is now load-bearing
rather than an edge case, and is covered by tests.

**Import-before-setup is no longer supported.** The CLI importer creates ledgers
owned by `system`; with the "join an existing ledger" affordance gone, such a
ledger would be invisible to the first human (the hub lists only ledgers you
hold a grant on). CLI imports belong *after* setup, into a ledger you own — the
in-app import is the front door. This is a deliberate narrowing: an unclaimed-
ledger affordance is more machinery than the case warrants. *Amended 2026-10-25:*
there is no CLI import any more, so every import is an in-app one, owned by the
person who ran it.

**`docker compose down -v` is required to see the new flow on an existing dev
install.** Migration 186 skips any install that already has a user, by design.
