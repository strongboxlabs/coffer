# Glossary

Domain terms that appear in code, commit messages, and other docs. Add a term when you introduce it; don't let new vocabulary land unexplained.

---

**Account** — A row in the `accounts` table. Either a real account (`bank`, `credit_card`, `investment`, `asset`, `liability`, `loan`) **or** a budgeting category (`account_type='category'`, with `category_kind` distinguishing income vs expense). Categories are accounts in the unified data model. See [decisions/0002-unified-accounts-table.md](decisions/0002-unified-accounts-table.md) and [decisions/0017-account-discriminator.md](decisions/0017-account-discriminator.md).

**ADR** — Architecture Decision Record. A short markdown file under `docs/decisions/` capturing the context, decision, and consequences of a material design choice at the time it was made.

**Merge** — Folding a duplicate row into the one that survives: the loser keeps `is_merged_into` pointing at the survivor, which carries `is_merge_winner`. Always user-driven — the editor offers "possible matches" and the user picks. There is no auto-merge: the confidence-scored `merge_rules` pipeline this entry once described was never built and its table was dropped by migration 044.

**Balance after** — `txn_header_account_balances.balance_after`: the running account balance on a given account after a given header is applied. One row per `(header, account)`. Migration 090 drove this from triggers; **migration 102 dropped that family**, and it is now recomputed from application code — an EF `SaveChangesInterceptor` (`LegDerivedRecomputeInterceptor`) scans the change tracker and calls the Postgres walk. The only triggers left in the schema are the two `txn_headers` immutability guards. Read-side surfaces it through `resolved_transactions.balance_after`.

**Category** — Moneydance vocabulary for what is implemented here as `accounts.account_type='category'`, with `category_kind` ∈ {`income`, `expense`, `adjustment`} naming the flow direction. Categories support a `parent_id` hierarchy (a category may have child sub-categories, its own direct transactions, or both); other account types do not.

**`category_kind`** — The flow-direction discriminator on category rows: `income`, `expense`, or `adjustment`. Required when `account_type='category'`, NULL otherwise. **`adjustment`** was added by migration 224 for entries that are neither earned nor spent — a property revaluation booked as an expense made one month compute to negative total spend, and two whole years to negative income. Separated from `account_type` because *what kind of thing this row is* and *which flow direction* are orthogonal concepts (per [decisions/0017-account-discriminator.md](decisions/0017-account-discriminator.md)).

**Loan** — `account_type='loan'`. Distinct from `liability` because loans carry amortization metadata (APR, term, compounding, face value) in `loan_terms` (migration 127) and appear in their own UI section. Amortization shipped: ADR-0050 covers the account editor and schedule, ADR-0078 the managed loan-payment reminder whose principal / interest / escrow split is computed live per occurrence.

**Counterparty** — The other side of a flow. Pairing is STRUCTURAL, not a stored link: the two legs of a posting share a `txn_legs.posting_index` under one `txn_headers` row, so there is no denormalised `counterparty_id` to keep in sync and no symmetry trigger (both belonged to the pre-ADR-0022 `transactions` table, dropped by migration 025). See [decisions/0022-txn-headers-and-legs.md](decisions/0022-txn-headers-and-legs.md).

**Cursor pagination** — Register reads use a composite cursor instead of `OFFSET`, so the index range scan is bounded by the cursor rather than the offset and stays fast at 50k+ rows. The cursor is `(posted_at, header_seq, entry_key)` — `seq` replaced `created_at` as the tiebreaker in migration 097, and the entry key is asymmetric per ADR-0036. Sort column and direction are parameters of `register_entry_keys`, not fixed to date-descending.

**Double-entry / symmetric postings** — One user-facing event is a `txn_headers` row; its money movements are `txn_legs` rows paired by `posting_index`, each pair summing to zero. There are no `splits` or `inv_txn_securities` tables — security metadata (security_id, quantity, unit_price) lives on the holdings-side leg of each pair. See [decisions/0022-txn-headers-and-legs.md](decisions/0022-txn-headers-and-legs.md).

**External ID** — A feed-supplied identifier (`txn_headers.external_id`) used as the idempotency key during sync upserts, unique per `(ledger_id, external_id)`. `ck_txn_headers_external_id_for_non_manual` requires one on every non-manual row, so CSV imports — which have no issuer-assigned id — emit a deliberately non-matching unique value rather than omitting it.

**Feed** — A bank/brokerage data source. Currently SimpleFIN (chosen) and Plaid (rejected). Manual import counts as a degenerate feed.

**Inactive account** — `accounts.is_active = false`. Single lifecycle flag (mig 106): the user marked the account deactivated. Default views hide it from pickers and the sidebar; the sidebar's "Show inactive" toggle surfaces it greyed-out with a strikethrough. Mig 106 dropped the prior orthogonal `is_hidden` column and collapsed its 109 rows here; the MD importer maps both MD-side `is_inactive` AND `hide` flags to this column.

**Bootstrap token** — A 32-byte random one-shot token minted at API startup when no WebAuthn credentials exist (per ADR-0013). The plaintext is written to the API logs once; the SHA-256 hash lives in `bootstrap_tokens` until consumed by `/api/auth/setup/{token}`. Subsequent registrations require an authenticated session or a recovery code instead.

**Ledger** — A self-contained book of accounts/transactions/etc. (a row in `ledgers`). The unit of book-isolation introduced in Phase A per [decisions/0020-multi-ledger-row-scoped.md](decisions/0020-multi-ledger-row-scoped.md). `ledger_id` is now **denormalised onto every ledger-scoped table** — migrations 071 and 072 did that deliberately, because RLS policies written as subqueries against an anchor table were multiplying recompute cost 180×. Coherence is held by a composite FK to the parent rather than by the subquery. There is no seeded ledger: migration 186 deleted the placeholder `Default` and `Demo` rows (ADR-0088).

**Recovery code** — One of 10 single-use codes minted at WebAuthn registration (ADR-0013). Stored as an Argon2id PHC string in `recovery_codes`; one code is good for one re-registration of a fresh credential when every authenticator has been lost. Regeneration invalidates all prior codes.

**Synthetic ledger** — The atomic per-test arrange step used by integration tests. Each test calls `SyntheticLedger.CreateAsync` to mint a fresh ledger + user + owner grant in one transaction; the test then seeds further state under those ids. No shared fixture state between tests; no real-export data; per-anchor `(ledger_id, …)` uniqueness keeps tests parallel-safe. See [engineering-standards.md §5.2](engineering-standards.md#52-integration-tests-bootstrap-a-synthetic-ledger-atomically).

**`user_ledger_grant`** — A row in `user_ledger_grants` granting a user one of three roles on a ledger: `owner` (read+write+grant+delete), `editor` (read+write), or `viewer` (read-only). A constraint trigger enforces ≥1 owner per ledger, deferred to COMMIT so an "add new owner, remove old" swap never fails mid-transaction.

**`last_opened_ledger_id`** — `users.last_opened_ledger_id`. The ledger this user most recently switched to. UI auto-opens it on next login after re-validating the user still has a grant. NULL on first login → ledger picker.

**Hidden transaction** — `txn_headers.is_hidden = true`. Soft-deleted from registers and reports; the row remains. Canonical since migration 230 — there is no override layer left to resolve through.

**Holding** — A `(account, security)` rollup row. Aggregates lots into a single position for UI summary purposes. Lives on the **Holdings sibling** account, not the brokerage cash account.

**`share_decimals`** — Per-security precision for share quantities (Moneydance's `dec` field). Stocks/ETFs typically use 4, mutual funds 5. The investment mapper looks this up so a `samt` of 1,000,000 means 100 shares for `dec=4` and 10 shares for `dec=5`.

**Holdings sibling** — A system-managed `account_type='investment'` row at the root, paired 1-1 with a brokerage account via the brokerage's `holdings_account_id` self-FK. Hosts the holdings-side legs of every investment transaction (buys, sells, dividend reinvests, etc.) so the brokerage account itself stays purely a cash account. `is_system=TRUE` keeps the sibling out of normal account lists. See [decisions/0019-symmetric-postings.md](decisions/0019-symmetric-postings.md).

**Leg** — One row of a paired posting. Every Moneydance split decomposes into two legs (origin + counterparty). `leg_index` is the original MD split index, preserved for ordering inside a `txn_group_id` group.

**Lot** — A specific tranche of shares acquired at a known cost basis on a known date. Critical for capital-gains calculation (specific identification, FIFO, etc.).

**MD** — Internal shorthand for Moneydance, the predecessor app. Used in code comments, never in product UI.

**Merge candidate** — A settled row the row being edited could fold into, offered in the editor's "possible matches" panel. Computed per request, not stored — the `merge_candidates` table was dropped by migration 044.

**Origin** — `txn_headers.origin`. The icon-level source mechanism of a transaction (mig 107, ADR-0035): `manual` (typed), `online_import` (any live feed — SimpleFIN, MD+ Direct Connect, OFX online), `file_import` (any file upload — OFX/QFX, CSV, QIF). Drives the register provenance icon. Per-provider audit detail lives on `provider_key`.

**Provider key** — `txn_headers.provider_key`. Specific ingest provider that wrote a row: `simplefin`, `mdplus`, `ofx`, `qif`, `csv-generic`, `csv-fidelity`. (Note the last two — there is no bare `csv`; `provider_security_mappings` is keyed by the specific provider, so the distinction is load-bearing.) NULL when `origin='manual'` (DB CHECK enforces the bi-implication). Drives the per-provider hover label on the register provenance icon AND is the per-provider dedup scope in `IngestOrchestrator`.

**Merge winner** — `txn_headers.is_merge_winner = TRUE`. A row that another row was merged into (the loser carries `is_merged_into` pointing at this row's id; the loser is hidden from the register). Maintained atomically with `is_merged_into` in `TransactionsRepository.PatchAsync`. Monotonic — no unmerge surface today. Renders as a small overlay on the register's provenance icon. See ADR-0035.

**Original** (formerly *override*) — The FEED's values for a header, kept in `txn_header_originals`. ADR-0100 and migration 230 inverted the old arrangement: the canonical `txn_headers` row now holds the CURRENT values and the sidecar holds what the feed sent, captured once on the first edit. The old direction — an immutable feed row plus an overlay — could not represent a field being CLEARED, so a payee could never be emptied. `resolved_transactions.has_overrides` is simply "an original was captured".

**Pending transaction** — A feed-supplied row not yet settled, flagged by `txn_headers.is_pending`. There is no separate holding table: `pending_transactions` was dropped by migration 044, and a pending row lives in `txn_headers` like any other.

**Register** — The chronological list of transactions for one account. The primary read view of the app.

**Resolved view** — `resolved_transactions`. The read-side projection over `txn_headers` ⨝ `txn_legs` (plus accounts, securities and tags). All app/report queries go through this view. It no longer coalesces an overlay — since migration 230 the canonical row is already current.

**Reminder** — Moneydance term for what we call a `recurring_transaction`. Templates that produce future-dated transactions on a schedule.

**Rule** — *Not built.* Pattern-matching on feed payee/memo to auto-categorize at sync time is still an open backlog item; the `transaction_rules` table it was sketched around was dropped by migration 044. What exists instead is similar-payees RECALL: the editor offers the (payee, counterparty) pairs chosen on prior rows from the same feed with the same raw payee, and the user applies one.

**Ledger operation** (formerly *sync run* / *provider run*) — One recorded operation on a ledger: a feed sync (SimpleFIN pull), an OFX/QIF file import, a Moneydance bootstrap import, a quote refresh, or a snapshot restore. Logged in `ledger_operations` (was `sync_runs` → `provider_runs` → `ledger_operations`, migrations 038 → 132 → 185) for audit and the Settings → Activity timeline.

**Trigram** — `pg_trgm` extension; computes similarity between strings as overlap of three-character substrings. Used in payee fuzzy matching.

**Txn group** — A compatibility projection in `resolved_transactions`, not a stored column: the view computes `txn_group_id` as the header id when the header has more than one posting, else NULL. Grouping is structural (shared `header_id`); the UUID survives only because read-side callers were written against it. See [decisions/0022-txn-headers-and-legs.md](decisions/0022-txn-headers-and-legs.md).

**Uncategorized** — A reserved expense account that catches feed splits before the user (or a rule) categorizes them. Created during Phase 2 import.
