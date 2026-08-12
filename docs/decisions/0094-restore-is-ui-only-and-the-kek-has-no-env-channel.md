# 0094 — Restore is a UI operation; the master key has no environment channel

* Status: Accepted
* Date: 2026-08-12
* Amends: [ADR-0060](0060-whole-db-backup-and-admin-role.md) (the operator CLI),
  [ADR-0061](0061-bootstrap-restore.md) (which kept the CLI "for headless DR and very
  large backups"), [ADR-0075](0075-linux-install-script.md) (the installer's restore
  prompt), [ADR-0092](0092-kek-lifecycle-in-the-ui.md) (D1/D6, the env-var migration
  window)

## Context

Restore had three entry points — the bootstrap UI (ADR-0061), the authenticated admin
UI (ADR-0071 D3), and a `coffer-api restore` CLI (ADR-0060) — plus a fourth moving
part: `install.sh` asked "are you restoring a backup?" and, if so, took the source
install's master key and wrote it into `.env` as `COFFER_MASTER_KEK_BASE64`.

Each of the CLI's stated justifications was checked and none held:

| Claim | What it turned out to be |
|---|---|
| "Works on a schema too broken to serve" | Circular. Every restore path wipes the schema to empty first, so a broken schema is answered by reinstalling — which is a documented, cheap operation. |
| "It skips migrations" | A local detail of that code path, not a capability the UI lacked: migrations were skipped only for `args is ["restore", ..]` to avoid pg_restore colliding with objects migrations had just created. The UI paths run migrations and restore correctly. |
| "The UI caps at ~128 MB" | ASP.NET's default `FormOptions.MultipartBodyLengthLimit`. Ours to raise, and never raised. |
| "Headless DR" | Cannot exist. Setup enrols a WebAuthn passkey in a browser, so no install has ever come into being without browser access to it. |

One argument was left standing and then also rejected: a reverse proxy can cap request
bodies (nginx defaults `client_max_body_size` to 1 MB) and time out long uploads, which
a file-based path sidesteps. That is the operator's environment to configure — exactly
the position the Moneydance import upload already takes, with no CLI alternative
offered — and if a proxy genuinely cannot be configured, a localhost install with
nothing in front of it is the fallback.

The installer's key prompt was worse than redundant. It collected the source key at the
one moment when **no archive exists to validate it against**, so a typo or a wrong-era
key was accepted silently and surfaced only after the restore had already replaced
everything. ADR-0092 D4 had already given the restore form a source-KEK field that
validates against the archive's KEK fingerprint before anything destructive runs, and
said in as many words that it replaced the `install.sh` prompt. That removal never
happened.

Meanwhile `COFFER_MASTER_KEK_BASE64` was to be "honoured for one release" (D6) and then
removed. ADR-0092 shipped in 0.43.0; 0.44.0 through 0.44.6 shipped after it. Keeping it
alive meant a value in `.env` that looked authoritative, was silently ignored once the
key file existed, and went stale the moment anyone rotated from the UI — the exact
hazard D1 moved the key into a file to eliminate.

## Decision

**D1 — Remove the `restore` CLI subcommand**, with its `--in`, `--force` and
`--allow-kek-mismatch` flags and the migration-skip special case. The bootstrap UI
covers a fresh install; the admin UI covers a running one. Supplying no source key in
either is the old `--allow-kek-mismatch` behaviour: the restore proceeds and D5
reconciliation reports what it cleared.

**D2 — Raise `MultipartBodyLengthLimit` to 4 GiB.** The UI is now the only restore
path, so it must not be the narrower one. Raised rather than removed: the three
big-upload endpoints already null out Kestrel's per-request cap, leaving this as the
only backstop against an unbounded stream. Small uploads are unaffected — file ingest
caps itself at 5 MB with `RequestSizeLimitAttribute`, a body-size limit that bites
first.

**D3 — `install.sh` stops asking about restore and stops writing any master key.** A
virgin install mints its own on first boot (ADR-0092 D3) and the setup ceremony shows
it. The closing message tells anyone restoring to use *Restore from a backup* and paste
the source key there, where it gets checked.

**D4 — `COFFER_MASTER_KEK_BASE64` is removed from the loader, the compose file and
`.env.example`**, along with D6's write-through migration. The key file is the only
source. `COFFER_MASTER_KEK_ID` stays as the id fallback for a key file written by hand
without an `id=` line — which D5 below makes a documented recovery route, so it has to
keep working.

**D5 — `--adopt-new-kek` mints the key and exits instead of going on to serve.** The
flag has to arrive as a temporary compose `command:` override, because the container is
refusing to boot and `exec` has nothing to attach to. If the process then served
normally, that override would be left in place, and the next boot that found no key
would mint again *silently* — the one thing the D3 gate exists to prevent. Exiting
makes it a one-shot the operator must undo to get a running install. A virgin install
minting its first key is not this case: nothing was abandoned and there is no flag to
remove.

## Consequences

**An install whose key only ever lived in `.env` refuses to boot after upgrading.** That
is correct — minting over live wrapped material would orphan it — and the refusal names
both remedies. Neither loses ledger data or passkeys, which do not depend on the KEK:

1. **Write the key to the key file** (loses nothing). The file lives on the
   `coffer_data` volume, mounted at `/app/data`:

   ```bash
   printf '%s' "$KEY" > master.key
   docker compose cp master.key api:/app/data/master.key && rm master.key
   ```

   Or point `COFFER_MASTER_KEY_PATH` at an injected secret — with the caveat ADR-0092
   already records: a read-only mount makes rotation refuse, since adoption needs to
   write.

2. **`--adopt-new-kek`** — mints fresh and abandons the three sealed secrets (SimpleFIN
   feed tokens, the stored backup passphrase, the Drive connection), all
   re-establishable in the UI. Per D5 it exits, so the flag must then be removed.

All four states above are pinned by `scripts/maintainer/kek-boot-drill.sh`, which runs
the real boot path against a real Postgres: virgin mint, the refusal (including that it
writes no key while refusing and names both remedies), recovery via the key file, and
adopt-mint-exit. Upgrade behaviour is not covered by the test suite — every test starts
from a fresh install — which is the same gap `upgrade-drill.sh` was written for.

**A restore over ~4 GiB, or through a proxy that caps bodies, needs operator action** —
raise `client_max_body_size` or equivalent, or install on localhost with no proxy in
front. This is a deliberate narrowing: one restore path with a documented environmental
dependency beats two paths where the second exists to work around the first.

**A dev stack mints a new key whenever its volumes are wiped.** The well-known dev key
in `.env.example` is gone with the variable. Nothing depended on it being the same
across wipes; the integration suite points `Api:MasterKey:Path` at its own fixture file.

## Alternatives rejected

**Keep the CLI, rewrite its justification honestly** (large artifacts vs. proxy body
limits). Rejected because it makes the product own an environment problem it already
declines to own for the import path, and two restore paths is two things to keep
correct — the removed one had already drifted into claiming capabilities it didn't have.

**Keep the env var for one more release.** It is already seven releases past the window
D6 promised, every install that has booted since 0.43.0 has a key file, and the boot
refusal plus two documented remedies is a better outcome than a second source of truth
that silently goes stale.
