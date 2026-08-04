# Glossary

Domain terms that appear in code, commit messages, and other docs. Add a term when you introduce it; don't let new vocabulary land unexplained.

---

**Account** — A row in the `accounts` table. Either a real account (`bank`, `credit_card`, `investment`, `asset`, `liability`, `loan`) **or** a budgeting category (`account_type='category'`, with `category_kind` distinguishing income vs expense). Categories are accounts in the unified data model. See [decisions/0002-unified-accounts-table.md](decisions/0002-unified-accounts-table.md) and [decisions/0017-account-discriminator.md](decisions/0017-account-discriminator.md).

**ADR** — Architecture Decision Record. A short markdown file under `docs/decisions/` capturing the context, decision, and consequences of a material design choice at the time it was made.

**Auto-merge** — When the merge pipeline assigns a confidence score above `merge_rules.auto_merge_threshold` to a candidate match, the incoming transaction is folded into the existing one without human review. The losing-side row keeps `is_merged_into` pointing at the survivor.

**Balance after** — `txn_header_account_balances.balance_after`: the running account balance on a given account after a given transaction (header) is applied. One row per `(header, account)`. Maintained by the header-walk trigger family (ADR-0034 / mig 090); never computed in application code. Read-side surfaces it through `resolved_transactions.balance_after`.

**Category** — Moneydance vocabulary for what is implemented here as `accounts.account_type='category'`, with `category_kind` ∈ {`income`, `expense`} naming the flow direction. Categories support a `parent_id` hierarchy (a category may have child sub-categories, its own direct transactions, or both); other account types do not.

**`category_kind`** — The income-vs-expense discriminator on category rows. Required when `account_type='category'`, NULL otherwise. Separated from `account_type` because *what kind of thing this row is* and *which flow direction* are orthogonal concepts (per [decisions/0017-account-discriminator.md](decisions/0017-account-discriminator.md)).

**Loan** — `account_type='loan'`. Distinct from `liability` because loans carry amortization metadata in MD (APR, term, compounding, face value) and appear in their own UI section. We import the type and the static balance fields; full amortization support is a later phase.

**Counterparty** — Every `transactions` row pairs 1-1 with another `transactions` row that represents the other side of the flow. The link is `counterparty_id`, set on the initial INSERT and immutable thereafter; symmetry (A→B implies B→A) is enforced by a deferred constraint trigger that fires at COMMIT. See [decisions/0019-symmetric-postings.md](decisions/0019-symmetric-postings.md).

**Cursor pagination** — Register reads use `(posted_at, id)` as the cursor instead of `OFFSET`. This lets the register feel instant even at 50k+ rows because the index range scan is bounded by the cursor, not the offset.

**Double-entry / symmetric postings** — Every flow becomes a `transactions` row; every row pairs 1-1 with another row via `counterparty_id`; the database enforces pair symmetry at COMMIT. There are no `splits` or `inv_txn_securities` tables — security metadata (security_id, quantity, unit_price, commission) lives on the holdings-side row of each pair. See [decisions/0019-symmetric-postings.md](decisions/0019-symmetric-postings.md).

**External ID** — A feed-supplied identifier (`transactions.external_id`) used as the idempotency key during sync upserts. Unique per `(account_id, external_id)`. For Moneydance imports the format is `<md_txn_id>:<leg_index>` so each leg of a multi-split event has its own key per account.

**Feed** — A bank/brokerage data source. Currently SimpleFIN (chosen) and Plaid (rejected). Manual import counts as a degenerate feed.

**Inactive account** — `accounts.is_active = false`. Single lifecycle flag (mig 106): the user marked the account deactivated. Default views hide it from pickers and the sidebar; the sidebar's "Show inactive" toggle surfaces it greyed-out with a strikethrough. Mig 106 dropped the prior orthogonal `is_hidden` column and collapsed its 109 rows here; the MD importer maps both MD-side `is_inactive` AND `hide` flags to this column.

**Bootstrap token** — A 32-byte random one-shot token minted at API startup when no WebAuthn credentials exist (per ADR-0013). The plaintext is written to the API logs once; the SHA-256 hash lives in `bootstrap_tokens` until consumed by `/api/auth/setup/{token}`. Subsequent registrations require an authenticated session or a recovery code instead.

**Ledger** — A self-contained book of accounts/transactions/etc. (a row in `ledgers`). The unit of book-isolation introduced in Phase A per [decisions/0020-multi-ledger-row-scoped.md](decisions/0020-multi-ledger-row-scoped.md). Six anchor tables (`accounts`, `securities`, `feed_connections`, `tags`, `merge_rules`, `transaction_rules`) carry `ledger_id` directly; every other table inherits its ledger membership transitively via FK chain. The seeded "Default" ledger has the well-known id `00000000-0000-0000-0000-000000000001` and absorbs existing data on first migration.

**Recovery code** — One of 10 single-use codes minted at WebAuthn registration (ADR-0013). Stored as an Argon2id PHC string in `recovery_codes`; one code is good for one re-registration of a fresh credential when every authenticator has been lost. Regeneration invalidates all prior codes.

**Synthetic ledger** — The atomic per-test arrange step used by integration tests. Each test calls `SyntheticLedger.CreateAsync` to mint a fresh ledger + user + owner grant in one transaction; the test then seeds further state under those ids. No shared fixture state between tests; no real-export data; per-anchor `(ledger_id, …)` uniqueness keeps tests parallel-safe. See [engineering-standards.md §5.2](engineering-standards.md#52-integration-tests-bootstrap-a-synthetic-ledger-atomically).

**`user_ledger_grant`** — A row in `user_ledger_grants` granting a user one of three roles on a ledger: `owner` (read+write+grant+delete), `editor` (read+write), or `viewer` (read-only). A constraint trigger enforces ≥1 owner per ledger, deferred to COMMIT so an "add new owner, remove old" swap never fails mid-transaction.

**`last_opened_ledger_id`** — `users.last_opened_ledger_id`. The ledger this user most recently switched to. UI auto-opens it on next login after re-validating the user still has a grant. NULL on first login → ledger picker.

**Hidden transaction** — `transaction_overrides.is_hidden = true`. Soft-deleted from registers and reports; raw row remains.

**Holding** — A `(account, security)` rollup row. Aggregates lots into a single position for UI summary purposes. Lives on the **Holdings sibling** account, not the brokerage cash account.

**`share_decimals`** — Per-security precision for share quantities (Moneydance's `dec` field). Stocks/ETFs typically use 4, mutual funds 5. The investment mapper looks this up so a `samt` of 1,000,000 means 100 shares for `dec=4` and 10 shares for `dec=5`.

**Holdings sibling** — A system-managed `account_type='investment'` row at the root, paired 1-1 with a brokerage account via the brokerage's `holdings_account_id` self-FK. Hosts the holdings-side legs of every investment transaction (buys, sells, dividend reinvests, etc.) so the brokerage account itself stays purely a cash account. `is_system=TRUE` keeps the sibling out of normal account lists. See [decisions/0019-symmetric-postings.md](decisions/0019-symmetric-postings.md).

**Leg** — One row of a paired posting. Every Moneydance split decomposes into two legs (origin + counterparty). `leg_index` is the original MD split index, preserved for ordering inside a `txn_group_id` group.

**Lot** — A specific tranche of shares acquired at a known cost basis on a known date. Critical for capital-gains calculation (specific identification, FIFO, etc.).

**MD** — Internal shorthand for Moneydance, the predecessor app. Used in code comments, never in product UI.

**Merge candidate** — A pair of transactions (incoming + existing) that may represent the same real-world event. Lives in `merge_candidates`. Resolved by auto-merge, manual confirm, or manual reject.

**Origin** — `txn_headers.origin`. The icon-level source mechanism of a transaction (mig 107, ADR-0035): `manual` (typed), `online_import` (any live feed — SimpleFIN, MD+ Direct Connect, OFX online), `file_import` (any file upload — OFX/QFX, CSV, QIF). Drives the register provenance icon. Per-provider audit detail lives on `provider_key`.

**Provider key** — `txn_headers.provider_key`. Specific ingest provider that wrote a row: `simplefin`, `mdplus`, `ofx`, `qif`, `csv`. NULL when `origin='manual'` (DB CHECK enforces the bi-implication). Drives the per-provider hover label on the register provenance icon AND is the per-provider dedup scope in `IngestOrchestrator`.

**Merge winner** — `txn_headers.is_merge_winner = TRUE`. A row that another row was merged into (the loser carries `is_merged_into` pointing at this row's id; the loser is hidden from the register). Maintained atomically with `is_merged_into` in `TransactionsRepository.PatchAsync`. Monotonic — no unmerge surface today. Renders as a small overlay on the register's provenance icon. See ADR-0035.

**Override** — A user edit that lives in `transaction_overrides`, never in the immutable `transactions` row. The `resolved_transactions` view coalesces overrides over feed values.

**Pending transaction** — A feed-supplied transaction not yet settled. Lives in `pending_transactions`, never in `transactions`. When it settles (or is matched to a settled one) it moves over.

**Register** — The chronological list of transactions for one account. The primary read view of the app.

**Resolved view** — `resolved_transactions`. The read-side projection of `transactions` ⨝ `transaction_overrides`. All app/report queries go through this view.

**Reminder** — Moneydance term for what we call a `recurring_transaction`. Templates that produce future-dated transactions on a schedule.

**Rule** — `transaction_rules` row. Pattern-matches on feed payee/memo/amount and writes an override row at sync time (e.g. normalize `WHOLEFDS` → `Whole Foods`, category Groceries).

**Ledger operation** (formerly *sync run* / *provider run*) — One recorded operation on a ledger: a feed sync (SimpleFIN pull), an OFX/QIF file import, a Moneydance bootstrap import, a quote refresh, or a snapshot restore. Logged in `ledger_operations` (was `sync_runs` → `provider_runs` → `ledger_operations`, migrations 038 → 132 → 185) for audit and the Settings → Activity timeline.

**Trigram** — `pg_trgm` extension; computes similarity between strings as overlap of three-character substrings. Used in payee fuzzy matching.

**Txn group** — A `txn_group_id` UUID shared across the **origin-side** rows of one user-facing event when that event has multiple legs (a "split transaction"). The counterparty rows on target accounts are *not* grouped — they belong to their respective register's chronological flow. Origin-only grouping is a UI convention, not a structural commitment. See [decisions/0019-symmetric-postings.md](decisions/0019-symmetric-postings.md).

**Uncategorized** — A reserved expense account that catches feed splits before the user (or a rule) categorizes them. Created during Phase 2 import.
