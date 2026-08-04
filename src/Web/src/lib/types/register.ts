// Register read-surface types (ADR-0030 §2). The per-row contract is a
// `kind`-discriminated union — `BankRow | InvestmentRow` — so consumers
// narrow on `kind` and touch only the fields their domain carries,
// instead of wading through one bag of nullable per-domain fields.
//
// The discriminant is the owning ACCOUNT's domain (mig 119
// `account_type`), NOT a per-leg signal: an investment register renders
// every one of its rows with investment chrome (including cash deposits
// and fee legs that touch no security), so `kind` follows the account,
// not the leg's `postingRole`. The register endpoint is account-scoped,
// so a single response is homogeneous (one account = one kind); the
// union still lets the shared shell stay domain-agnostic while each
// page narrows via `Extract<RegisterRow, { kind: 'bank' }>` etc.
//
// Mirrors API `Coffer.Api.Contracts.RegisterRowDto` (+ `BankRowDto` /
// `InvestmentRowDto`). The API emits the `kind` discriminator as the
// first property of each row object.

/**
 * Reconciliation status. The normalized 3-state vocabulary stored
 * per-leg in `txn_leg_recon` (migration 171, ADR-0082; formerly
 * `txn_headers.status`, migration 030). UI cycles
 * `uncleared → reconciling → cleared → uncleared` on click of the
 * status badge in the register.
 *
 * Domain semantics:
 *   - `uncleared`   — default; not yet matched against a statement.
 *   - `reconciling` — user has tentatively marked it during a
 *                     reconciliation session (persists across
 *                     sessions; functionally still uncleared for
 *                     reporting, just a workflow / visual aid).
 *   - `cleared`     — matched against a statement.
 */
export type ReconStatus = 'uncleared' | 'reconciling' | 'cleared';

/**
 * The ~40 fields every register row carries regardless of domain.
 * Mirror of the universal members on API `RegisterRowDto`.
 *
 * `amount` and `balanceAfter` arrive as JSON numbers (decimal →
 * Number serialization). For DISPLAY (Intl.NumberFormat) this is
 * safe — the rounded printed value matches the DB. Don't perform
 * client-side arithmetic with these; the API is the source of truth.
 */
export interface RegisterRowBase {
    id: string;
    accountId: string;
    payee: string | null;
    memo: string | null;
    amount: number;
    postedAt: string;
    transactedAt: string | null;
    /** Normalized 3-state reconciliation vocabulary (migration 030). */
    status: ReconStatus;
    isHidden: boolean;
    hasOverrides: boolean;
    balanceAfter: number | null;
    origin: string;
    isPending: boolean;
    externalId: string | null;
    /** `transactions.check_number` projected directly (migration 018). */
    checkNumber: string | null;
    /** The paired posting per ADR-0019 symmetric model. */
    counterpartyId: string;
    /**
     * Non-null for multi-split MD txns; the SPA renders them as a single
     * collapsed row labelled "– N splits –".
     */
    txnGroupId: string | null;
    legIndex: number;
    /** The "other side" of the symmetric posting — the MD register's
     *  Category column; rendered as a colored Chip mapped from the type. */
    counterpartyAccountId: string | null;
    counterpartyAccountName: string | null;
    counterpartyAccountType: string | null;
    /** Always an array (possibly empty), never null. Deterministic
     *  order (alpha by tag name). */
    tags: string[];
    /**
     * Always-present owning-header id (migration 028). The SPA's
     * inline-edit POSTs use this directly; distinct from `txnGroupId`
     * which stays NULL for singles.
     */
    headerId: string;
    /**
     * Cleared-transition audit (migration 030). Non-null iff
     * `status === 'cleared'` (DB CHECK enforces).
     */
    clearedAt: string | null;
    clearedByUserId: string | null;
    /**
     * Leg-insertion timestamp (ISO-8601 UTC). All legs of a single
     * insert event share this value. The SPA compares it against the
     * active selection's `selectedAt` so a row created AFTER the user
     * clicked "select all" renders unchecked (ADR-0024).
     */
    createdAt: string;
    /**
     * Raw leg-level memo (migration 032, ADR-0025). Null when the leg
     * has no override AND no canonical `leg_memo` — distinct from the
     * COALESCEd `memo` field above. Split-leg rows display this
     * directly; the editor loads it into the per-posting memo input.
     */
    legMemo: string | null;
    /**
     * Raw header-level memo (migration 032, ADR-0025). Null when
     * neither the header override nor the canonical header row has a
     * memo. The editor's umbrella "Memo" input loads from this.
     */
    headerMemo: string | null;
    /**
     * OFX FITID — the bank's per-transaction unique id (migration 034).
     * Universal: both bank and investment rows can originate from OFX.
     * The composite `(onlineMatchFiId, onlineMatchFitid)` is the dedup
     * key for incoming feed items.
     */
    onlineMatchFitid: string | null;
    /** OFX FI id — identifies the issuing financial institution
     *  (migration 034). Composite with `onlineMatchFitid`. */
    onlineMatchFiId: string | null;
    /** Bank-feed review flag (migration 037). TRUE on rows a sync just
     *  landed; the register renders these with a distinct treatment
     *  until the user clicks Approve. */
    needsReview: boolean;
    /**
     * Verbatim provider JSON for this transaction (migration 078/079).
     * The register's right-click "Show raw provider data" modal
     * pretty-prints it. NULL on manual + MD-imported rows.
     */
    providerRawPayload: string | null;
    /**
     * ADR-0034 mig 098/100. Per-(header, account) net cash effect. Same
     * value on every leg of one (header, account); the SPA reads it once
     * per entry instead of summing legs. Null only when no balance row
     * exists yet (transient sync state).
     */
    headerAccountNetAmount: number | null;
    /** Mig 107: per-provider audit detail. One of `simplefin`,
     *  `mdplus`, `ofx`, `qif`, `csv`. NULL when origin='manual'. */
    providerKey: string | null;
    /** Mig 107: TRUE when at least one other row was merged into this
     *  row. Drives the merge-winner overlay on the provenance icon. */
    isMergeWinner: boolean;
    /** Mig 107: bootstrap-import marker. `'moneydance_export'` on rows
     *  from the MD JSON bootstrap; null on rows born in Coffer. */
    importSource: string | null;
    /** Mig 108 (ADR-0036): per-leg derived action. Equals the header
     *  action when set; falls back to `'Xfr'` on transfer-shape legs.
     *  Universal — cash-shape bank headers gain a per-leg 'Xfr'. */
    derivedAction: string | null;
    /** Mig 108 (ADR-0036): distinct posting_index values of this header
     *  that touch THIS row's account. With `headerTotalPostings`,
     *  distinguishes ORIGINATING (equal) from TARGET (less) rows. */
    accountPostingsOnHeader: number;
    /** Mig 108 (ADR-0036): total distinct posting_index values across
     *  the whole header (all accounts). */
    headerTotalPostings: number;
}

/**
 * A register row on a bank-domain account (bank / credit_card / cash /
 * asset / liability / category). Carries only the universal fields —
 * the investment + ingest-prefill fields are absent by construction.
 * Mirror of API `BankRowDto`.
 */
export interface BankRow extends RegisterRowBase {
    kind: 'bank';
}

/**
 * A register row on an investment-domain account. Adds investment-leg
 * metadata + the OFX ingest-prefill carriers on top of the universal
 * base. Mirror of API `InvestmentRowDto`. Every leg from the
 * cross-account `/legs` endpoint is this shape (it serves the
 * investment editor's `legsToDraft`, which reads `postingRole` /
 * `securityId` / `quantity` off every leg regardless of account).
 */
export interface InvestmentRow extends RegisterRowBase {
    kind: 'investment';
    /** Header action (`buy` / `sell` / `div` / …). NULL on cash-shape
     *  rows of an investment account (deposits, plain transfers). */
    investmentAction: string | null;
    /** Investment-leg metadata (migration 045). Joined from `txn_legs`
     *  + `securities`. Null on the cash side of a posting. */
    securityId: string | null;
    securityTicker: string | null;
    securityName: string | null;
    quantity: number | null;
    unitPrice: number | null;
    /**
     * Investment posting role marker (migration 057). DB trigger
     * enforces `postingRole !== null ⇔ investmentAction !== null`. The
     * aggregator dispatches off this value (no category sniffing).
     */
    postingRole: 'security' | 'income' | 'transfer' | 'fee' | null;
    /**
     * Provider-classifier action hint (ADR-0031 Phase 3d.1). Set only
     * on feed rows whose description matched the classifier patterns;
     * the editor pre-fills the action picker from it on open.
     */
    ingestActionHint: string | null;
    /** Provider-classifier security hint resolved via
     *  provider_security_mappings at sync time (ADR-0031 Phase 3d.1). */
    ingestSecurityId: string | null;
    /**
     * Mig 113: per-row investment prefill carriers (OFX UNITS /
     * UNITPRICE / COMMISSION+Fees+Load+…). The editor's bank→investment
     * upgrade flow (`hintToDraft`) reads these to pre-fill the draft.
     */
    ingestShares: number | null;
    ingestUnitPrice: number | null;
    ingestFee: number | null;
    /**
     * Mig 114: persisted provider ticker hint, used by the Accept flow
     * to record a provider_security_mapping with the same identifier
     * the next ingest will look up.
     */
    ingestSecurityTickerHint: string | null;
    /**
     * ADR-0080: server-side investment-event aggregation. The register
     * returns one collapsed event per header, so these synthesized slot
     * fields — formerly computed client-side in investmentAggregator.ts —
     * are part of the contract. Null when the corresponding role leg is
     * absent. Mirror of API `InvestmentRowDto`.
     *   Category slot — the income-role leg's counterparty.
     *   Transfer slot — the transfer-role (or derived-Xfr) leg's counterparty.
     *   Fee          — the single fee-role leg: |amount| + its category.
     */
    categoryAccountId: string | null;
    categoryAccountName: string | null;
    categoryAccountType: string | null;
    transferAccountId: string | null;
    transferAccountName: string | null;
    transferAccountType: string | null;
    feeAmount: number | null;
    feeCategoryId: string | null;
    feeCategoryName: string | null;
}

/**
 * The register-row discriminated union (ADR-0030 §2). Narrow on `kind`:
 * `row.kind === 'investment'` gives access to the investment fields.
 */
export type RegisterRow = BankRow | InvestmentRow;

/**
 * Mirror of API `Coffer.Api.Contracts.RegisterEntryDto`. One logical
 * entry in the register — either a single transaction or a multi-
 * split group with its legs nested. The server paginates by entry,
 * so a page of N entries always shows N "things" regardless of how
 * many legs each split contains.
 *
 * The entry's `kind` (`txn` / `group`) is distinct from each row's
 * own `kind` (`bank` / `investment`).
 */
export type RegisterEntry =
    | { kind: 'txn'; txn: RegisterRow; groupId: null; legs: null }
    | {
          kind: 'group';
          txn: null;
          groupId: string;
          legs: RegisterRow[];
      };

/**
 * Mirror of API `Coffer.Api.Contracts.RegisterPage`. One page of
 * register entries plus two opaque cursors — one for each scroll
 * direction (migration 031). Page boundaries always fall between
 * entries, never inside a group.
 *
 * - `cursorForOlder`: pass back with `direction='before'` on the
 *   next call to load entries older than this page's oldest.
 *   `null` when there are no older entries (timeline tail).
 * - `cursorForNewer`: pass back with `direction='after'` on the
 *   next call to load entries newer than this page's newest.
 *   `null` when there are no newer entries (timeline head — the
 *   canonical most-recent first page).
 */
export interface RegisterPage {
    entries: RegisterEntry[];
    cursorForOlder: string | null;
    cursorForNewer: string | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.SetReconStatusRequest`. Body for
 * `PUT /api/ledgers/{ledgerId}/transactions/{headerId}/recon-status`.
 * Universal: any register row (bank, investment, future asset/loan)
 * can be set cleared / uncleared / reconciling. The server enforces
 * validity + manages the paired audit columns (`cleared_at` /
 * `cleared_by_user_id`).
 */
export interface SetReconStatusRequest {
    status: ReconStatus;
    /** ADR-0082: reconciliation is per-account, so the register sends the
     *  account it's showing; the status applies to that account's leg. */
    accountId: string;
}

/**
 * Mirror of API `Coffer.Api.Contracts.DeleteTransactionResponse`.
 * Universal across domains — bank, investment, and future row types
 * all go through the same delete endpoint. The server picks
 * hard-delete vs soft-hide based on the header's `external_id`
 * presence (manual entries get hard-deleted, feed / import-keyed
 * rows are soft-hidden so re-source doesn't resurrect them). The
 * SPA surfaces a different toast / confirmation copy per kind.
 */
export interface DeleteTransactionResponse {
    kind: 'hard-deleted' | 'soft-hidden';
}

/**
 * Mirror of API `Coffer.Api.Contracts.IndexBucketDto`. One bucket on
 * the date-aware scroll-track (ADR-0024 follow-up) — every month
 * with at least one visible entry on the requested account. Months
 * with no entries are absent from the array.
 *
 * The track renders each bucket at uniform pixel height, so years
 * with sparse activity cluster visually (Google Photos pattern).
 *
 * - `yearMonth`: ISO `yyyy-MM`, sortable as a string. The track's
 *   year-gutter labels are derived from the year part; the full
 *   key is the cache identity.
 * - `count`: distinct register entries (header count) in this month.
 *   Used by hover tooltips ("Mar 2024 — 87 entries").
 * - `sampleHeaderId`: most-recent header in the bucket by canonical
 *   `(posted_at, seq)`. Used as the seek anchor:
 *   `register.refresh(sampleHeaderId)` opens a window with that
 *   entry visible at the top.
 */
export interface IndexBucketDto {
    yearMonth: string;
    count: number;
    sampleHeaderId: string;
}

/**
 * Mirror of API `Coffer.Api.Contracts.HeaderBalanceDto`. One row
 * per (header, account) returned by the bulk
 * `POST /transactions/balances` endpoint. Used by the SPA's
 * after-save in-place refresh path — patches balance + net-amount
 * on every entry in the loaded window without re-fetching the
 * page (which would cause virtuoso to data-swap and jerk the
 * scroll position).
 */
export interface HeaderBalanceDto {
    headerId: string;
    balanceAfter: number;
    netAmount: number;
}
