# 0068 — MCP write surface (AI-assisted data cleanup)

* Status: Accepted (slices A + B shipped 0.8.0 / 0.9.0)
* Date: 2026-06-27
* Related: ADR-0063 (MCP server — this reverses its "read-only first" stance, behind
  a gate), ADR-0067 (classification — the schema these writes populate), ADR-0065
  (transfer_shares + in-kind convert — the loop these writes close), ADR-0037
  (snapshots — the manual restore point), ADR-0050 (account editor — the REST
  writes some tools reuse)

## Context

0.7.0 shipped a rich classification schema (ADR-0067) + a strong MCP **read** layer,
but MCP has **no write path**. So every cleanup an assistant can *plan* over the read
tools (populate classifications, collapse duplicate/mis-tagged securities, merge
redundant categories left by stacked imports, fix mis-recorded rollovers) dead-ends
in manual work. The gap is almost entirely writes.

The product owner wants to drive cleanup with an AI assistant: the assistant reasons
over names/tickers/usage and produces the values; a human reviews; the change persists.

## Decisions

### D1 — MCP gains an OPTIONAL write surface, gated by a new system setting
Add `mcp.writes_enabled` to `system_settings` (mig 147's generic JSONB store — **no
migration**, just a bootstrap seed, default **false**). Read at **startup** (mirrors
`mcp.enabled`, ADR-0063 §D8). Writes require `mcp.enabled` too (writes imply the server
is on). When writes are off, the write tools are **not registered** at all (surface
absent, not present-but-rejecting — consistent with ADR-0063 §D7). The toggle lives in
the existing admin **System → MCP** panel.

### D2 — Safety = explicit user responsibility + one bold warning (NOT product ceremony)
Enabling MCP writes shows a **bold, unmistakable warning** in the System → MCP panel
(an AI agent can now mutate your financial data; review what you ask it to do). That is
the safety boundary. There is **no** auto-snapshot and **no** product-enforced
per-operation confirmation — a bulk cleanup (e.g. merging many categories) must not
demand a snapshot or a confirmation per item. The user owns the risk; snapshots
(ADR-0037) remain
available manually as a restore point if they want one before a big run.

RLS stays the *data* boundary: every write executes through the same repositories as the
SPA, under the caller's `app.user_id`, so an AI write cannot escape the user's ledgers
or bypass a DB-enforced invariant. The new risk is intent (bulk, hard-to-reverse edits),
addressed by D2's warning + D4's preview, not by access control.

### D3 — Primitives, not intelligence
The server exposes mechanical write **primitives** — set / merge / delete / recategorize
/ convert — and nothing more. All judgment (which fund is what class, which records are
duplicates, which categories merge into which, a balanced fund's sleeve weights) lives in
the **assistant**; the human reviews; the primitive persists. The server takes on no
market-data / classification / licensing dependency. (Explicitly **no**
`classify_securities_auto` — classification is the assistant's job, the server just holds
the pen.)

### D4 — One entity per call; the assistant iterates; no per-op friction
Each write primitive operates on a **single entity** — one `securityId`, one
`(source,target)` category pair, one transaction, one account. There are **no
batch / array tools**. The assistant is the loop: a large category revamp is many
individual `merge_category` calls, made autonomously **without any per-call human
friction** (no per-call confirm, no per-call snapshot) — the safety boundary is the
single enablement warning (D2), not a prompt per call. Each call supports
**`dryRun`** (preview that one op's before/after) and setters take an **`overwrite`**
flag (default false = fill nulls only; `overwrite=true` makes a deliberate
correction, e.g. re-classing a commodity fund from equity to real_assets). Each call
**echoes the resulting state**
(or `{before, after}` under dryRun) and reports a clear error on a bad input.
Rationale: simple composable primitives the model invokes per item are cleaner than
batch tools (no array validation, no partial-batch result handling), and an MCP
client naturally iterates. There is no `confirm` parameter — it would be redundant
friction over the enablement warning.

### D5 — Reuse the 0.7.0 REST write layer; only merges/deletes are net-new
The field-setting tools wrap repositories that already exist (and back the SPA editors):
`set_security_classification` / `update_security` → `SecuritiesRepository.PatchAsync`;
`set_security_components` → `SecuritiesRepository.ReplaceComponentsAsync` (look-through
sleeves); `set_account_taxstatus` → `AccountsRepository.UpdateAsync`; `create_category` →
`AccountsRepository.CreateAsync`; `rename_category` → `AccountsRepository.UpdateAsync`;
`convert_in_kind_transfer` → `InvestmentTransactionsRepository.ConvertInKindTransferAsync`
(REST endpoint shipped in 0.7.0). **Net-new** repository methods: `merge_category`,
`delete_category`, `merge_securities`, and `reparent_category` (cycle-guarded —
`UpdateAsync` carries no `parentId`, and reparenting needs a loop check the plain field
setters don't).

The MCP tool is the *repository's* caller (each tool takes its RLS-scoped repo from the
request DI scope — the repo is the layer boundary, the same as the read tools and
`set_account_taxstatus`). A standalone REST endpoint for the net-new merges/deletes — so a
future SPA category-management surface can share them — is a **fast-follow built when that
UI lands** (no SPA consumer today; not built speculatively). The repo method is written so
the endpoint is a thin pass-through when needed.

**Merge semantics (`merge_category`):** repoint every leg referencing the source —
committed transactions *and* recurring-template legs (a reminder's category postings live
on its template `txn_legs`, so the one repoint covers both) — to the target; reparent the
source's child categories to the target; then **deactivate** the emptied source
(`is_active=false`, reversible — the row + history are preserved, *not* deleted). Guards:
both are categories of the same `category_kind` in the ledger, source ≠ target, source not
system-managed. Maintained balances for both categories are rebuilt; the whole thing is one
transaction. (`txn_leg_overrides` carry no category, and `transaction_rules` isn't yet
EF-modelled — and a deactivate, unlike a delete, leaves any dangling reference intact — so
neither needs touching here.) **Delete semantics (`delete_category`):** hard-delete only a
category with zero referencing legs, zero child categories, and not system-managed; else a
`category-in-use` error pointing at `merge_category`.

**Merge semantics (`merge_securities`):** repoint every reference to the source security —
`txn_legs`, `realized_gains`, and `provider_security_mappings` — to the target, rebuild
holdings + lots (ADR-0064 FIFO) for every affected `(account, security)` pair via
`HoldingsRecomputeService`, then **deactivate** the source (`is_active=false`, reversible —
mirrors `merge_category`). Guards: both securities in the ledger, source ≠ target. (A
provider mapping can't collide on the target: `provider_security_mappings` is unique on
`(ledger, provider_key, provider_security_id)`, so source and target never share a
provider-ticker — the repoint is always a clean move.) **Reparent guard
(`reparent_category`):** rejects a move that would close a parent/child cycle — it walks up
from the new parent looking for the node being moved (bounded hop count) — on top of the
same category / `category_kind` / not-system guards the other category tools use.

### D6 — Tool set (Tier 1) + deferred reads
Write tools: `set_security_classification`, `update_security`, `set_security_components`,
`merge_securities`, `create_category`, `rename_category`, `reparent_category`,
`merge_category`, `set_transaction_category`, `delete_category`, `set_account_taxstatus`,
`convert_in_kind_transfer`. (`reparent_category` is a dedicated cycle-guarded tool, not the
account PATCH — `UpdateAsync` has no `parentId`.) Tier-2 reads
(`net_worth_history`, holding-period/`tax_lots`, benchmark/TWR, target-allocation drift)
are real but **separate, later slices** — not blockers for cleanup. A per-call **audit**
of write tools (the deferred ADR-0063 §D7) is a fast-follow once writes ship.

## Slices

- **A (0.8.0, shipped):** the gate (`mcp.writes_enabled` + startup registration + bold
  warning UI) + `set_account_taxstatus` + the cleanups the owner asked for —
  `set_security_classification` (bulk classification backfill, `overwrite` flag),
  `merge_category` + `delete_category` (the category revamp), and
  `set_transaction_category` (single-transaction recategorize — single-posting bank-shape
  only; a split or transfer errors, since it reuses the posting-reshape transaction
  PATCH). Each single-entity; the assistant iterates.
- **B (0.9.0, shipped):** `update_security` + `set_security_components` (look-through
  sleeves, broken out as its own tool rather than folded into `update_security`),
  `merge_securities` (dedup/alias fixes), `convert_in_kind_transfer` (the rollover loop),
  and the category-lifecycle trio `create_category` / `rename_category` /
  `reparent_category` (cycle-guarded). Also widened the `set_security_classification`
  `vehicleType` guidance to the full mig-150 CHECK set (collective trust, separate account,
  529, option, CD, bond, …) — those values were already accepted by the DB; only the tool's
  description lagged.
- **C+:** Tier-2 reads + the per-call write audit (audit shipped in ADR-0081 / 0.30.0).
- **D (0.31.0):** tag-dictionary lifecycle on MCP — `rename_tag` / `merge_tags` /
  `delete_tag` / `cleanup_unused_tags`, wrapping the Tags-v1 `TagsRepository` that already
  backs the SPA (the direct parallel to the `merge_category` / `rename_category` /
  `delete_category` tools on the category side). Manual prices — `add_price` /
  `update_price` / `delete_price`, the write side of the `price_history` read tool, wrapping
  the ADR-0070 `SecuritiesRepository` price methods (a hand-entered price is manual-owned).
  And `set_transaction_category` **widened to BULK** (`headerIds[]`): best-effort — every
  recategorizable row moves; a split / transfer / investment / not-in-ledger row is returned
  in a structured `rejects: [{ headerId, reason }]` list, so one bad row never blocks the
  rest; the whole call fails only for a bad target category or an empty id list. This is a
  second deliberate exception to D4's one-entity-per-call rule (after `set_transaction_tags`,
  ADR-0081 D6): a single shared target + an up-front target check + per-row best-effort keeps
  the friction low and the reject list keeps partial failure loud. All still write-gated
  (kill-switch + `coffer.write`).
- **E — split-posting recategorize** (`set_split_posting_category`): closes the one gap the
  single + bulk recategorize tools leave — a SPLIT (multi-posting) transaction, which both
  refuse (they repoint a whole single-posting header). It repoints the posting(s) of one or
  MANY splits currently on `fromCategoryId` → `toCategoryId`, leaving every other posting
  untouched, by re-running each header's FULL posting set through `PatchAsync` (the same
  ADR-0025 reshape) with only the matched counterparties swapped, so the reshape + recompute
  stay on the canonical path. **Bank-shape only** — an investment header (`action` set) is
  rejected, as the reshape assumes the bank one-source/one-counterparty-per-posting invariant.
  **Bulk + best-effort** (`headerIds[]`, mirroring bulk `set_transaction_category`): a header
  that isn't a bank-shape split with a fromCategory posting is returned in a structured
  `rejects: [{ headerId, reason }]` list so one bad row never blocks the rest; the whole call
  fails only for a bad target category or an empty list. EVERY fromCategory posting in a header
  moves (a re-home — no per-posting amount targeting, which keeps the batch unambiguous; the
  surgical "split one same-category posting off" is left to full split-edit). A third
  deliberate exception to D4's one-entity rule (after `set_transaction_tags` + bulk
  `set_transaction_category`). `dryRun` previews the tally; write-gated. Full split create/edit
  (add/remove postings, re-amount) remains a larger follow-on. (Version stamped at release.)

## Consequences

- An assistant can apply a reviewed cleanup plan one entity at a time (it loops); the
  loop closes in one place.
- MCP is no longer read-only — mitigated by an off-by-default, admin-gated, loudly-warned
  toggle; RLS + DB invariants still hold; the assistant proposes + dry-runs, the human
  decides to apply.
- New net-new mechanics (`merge_category`/`merge_securities`/`delete_category`) also
  benefit the SPA. Built REST-first to keep the layer boundary (ADR-0030).
