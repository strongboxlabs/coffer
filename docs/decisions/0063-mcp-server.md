# 0063 — MCP server (AI report-building over Coffer data)

* Status: Accepted (v1 = v0.5.0, built + validated end-to-end with Claude Desktop);
  v2 shipped (v0.6.0 — see "## v2 slice" below) EXCEPT time-weighted return, which
  is unconditionally `null` pending a historical-valuation feed (IRR is the live
  figure; see the `returns` note below — tracked as Track-2 "historical valuations"
  in follow-ups.md). v0.7.0 (ADR-0065/0066/0067) added
  `find_in_kind_transfer_candidates`, allocation `dimension` + look-through, and
  tax_status / classification fields on the list tools. A later read-surface pass
  added fail-loud enum params (an unknown value errors with the valid list instead
  of silently defaulting), `list_upcoming_reminders`, `list_tags`, and a tag filter
  on `list_transactions` / `transaction_summary` (see follow-ups.md "MCP hardening
  + capabilities").
* Date: 2026-06-25 (v1); 2026-06-26 (v2 slice)
* Related: ADR-0013 (WebAuthn auth — reused for OAuth login/consent), ADR-0020
  (RLS / `app.user_id` — the real authorization boundary), ADR-0027/0029
  (investment model — the reporting target), ADR-0062 (we are the OAuth *provider*
  here, the mirror of being the Drive OAuth *client*)

## Context

Expose Coffer's data to AI clients (claude.ai HTTP connector, Claude Desktop,
Gemini) so a model can **build reports** over a user's finances — emphasis on
**investments** (holdings, allocation, income, gains). Coffer already computes the
hard parts (holdings + FIFO lots, securities + `asset_class`, OHLCV price
history), so most reporting is reads/joins over existing data, not new math.

The targets are a remote **HTTP connector** and **desktop clients on other
networks** — so stdio (same-machine) can't reach Coffer; it must be remote HTTP,
which forces OAuth. One HTTP+OAuth server serves all three.

## Decisions

### D1 — Transport: streamable HTTP at `/mcp`
Remote HTTP (not stdio). Reachability: the **hosted claude.ai connector** calls
from Anthropic's servers → needs **public** exposure; **desktop** clients call
from the user's machine → public **or** a private tunnel/VPN (Tailscale). The
build is identical either way; topology only changes where it's reachable.

### D2 — Auth: OAuth 2.1, Coffer as its own AS — don't hand-roll it
Coffer has no external IdP, so it is the Authorization Server, via **OpenIddict**
(auth-code + PKCE, Dynamic Client Registration RFC 7591, discovery RFC 8414/9728,
refresh, revocation) — not a hand-built AS. The "log in to authorize" step
**reuses the existing WebAuthn session**; we add a **consent screen** + scopes +
a "Connected apps" revoke page (mirrors passkey management). Every token resolves
to a Coffer user and sets `app.user_id`, so **RLS remains the real data boundary**
— OAuth only front-doors it. (A personal-access-token stays an optional dev/test
fallback; not the shipped path.)

### D3 — Read-only first
Scope `coffer.read` only. Write tools (categorize, etc.) are deferred behind an
explicit `coffer.write` scope + consent — an AI agent with write access to a
ledger is high-stakes, and reporting needs none of it.

### D4 — Financial math in tools, narration in the model
The model **must not** compute money. Tools return **authoritative, decimal-typed**
numbers (cost basis, gains, allocation %, income, returns); the LLM composes,
formats, and explains. If a tool would force the model to sum/divide/FIFO its way
to a figure, that's a missing tool, not a model job. Tools call the existing
repositories + the override-aware `resolved_transactions` view — never raw SQL
bypassing RLS/validation — and never expose secrets (KEK, passphrase, OAuth blob).

### D5 — v1 tool catalog (investment reporting)
Read tools, parameterized by as-of-date / period / ledger / account / asset-class;
amounts + share quantities as decimal strings:
holdings snapshot (qty, FIFO cost basis, latest price, market value, unrealized
gain $/%); allocation (market value %, by a `dimension` param — asset_class /
region / vehicle_type / account / security — with multi-asset **look-through**
decomposition via `security_components`, ADR-0067);
income (`divx` over a period, by security); realized gains (proceeds − consumed
FIFO-lot cost — Coffer-computed); price history; securities catalog (incl. the
ADR-0067 classification dimensions); an
activity tool (investment actions over a period, for the model to **narrate**,
not to do arithmetic on); and `find_in_kind_transfer_candidates` (ADR-0065 D4 —
read-only detection of sell+buy pairs that are really in-kind transfers; the user
converts each via the `POST .../in-kind-transfers/convert` write endpoint, which
is **not** an MCP tool since v1 MCP is read-only).

### D6 — Phasing (v1 reporting · v2 returns · v3 FX)
- **v1** — read-only reporting (D5) over existing data.
- **v2** — **returns (IRR / TWR)**: real `Coffer.Domain.Investment` computation
  exposed as a tool. NOT LLM-derived from cash flows (it would be wrong).
- **v3** — **FX / multi-currency** conversion.

**Operating assumption: single base currency (USD).** The owner's data is
USD-exclusive, and any public / multi-user release is explicitly **post-v3** — so
no untrusted multi-currency user ever predates FX, and v1/v2 can total in one
currency safely without a guard. FX is therefore a genuine later enhancement, not
a deferred correctness bug. **Forward-compat (so v3 stays additive, per
"design for general release"):** tool outputs still carry the currency code even
though it's always USD today, and nothing hardcodes USD irreversibly — v3 adds
conversion, it doesn't unwind a baked-in assumption. (FX *would* be a correctness
dependency for a multi-currency portfolio; that's simply out of scope until v3.)

### D7 — Security posture
Public exposure (for the hosted connector) ⇒ HTTPS, rate-limiting, **audit every
token issue + tool call** (the `provider_runs` pattern), read-only default, a cap
on Dynamic Client Registration; VPN-only (desktop) is lighter. The AS discovery +
redirect metadata must emit the **correct public origin** (same `Fido2.Origins[0]`
derivation — the same class of bug as the Drive `redirect_uri` / `Origins[0]`
issue). **Validate each client end-to-end** (claude.ai, Claude Desktop, Gemini) —
MCP OAuth + DCR + discovery interop is new and not guaranteed by the RFCs.

### D8 — Runtime enablement: an admin System setting, applied at restart
v1 gates MCP at **startup** via `Api:Mcp:Enabled` config (when false the scheme /
policy / OAuth AS / `/mcp` / token endpoints are never registered — "surface
absent, not 404", D7). v2 adds a deployment-wide **`system_settings`** store
(generic key/value, admin-writable) with `mcp.enabled` (default **false**), read
at startup as the effective gate alongside config: **effective = config OR DB
setting**, so the env flag remains a valid bootstrap/test/headless override and
the DB setting is the UI path. Toggling in the System-settings UI persists intent
but **takes effect on the next API restart** — a runtime gate would leave the
OAuth/MCP endpoints present-but-404, contradicting D7, so we keep registration a
startup decision rather than weaken the hardening. The startup read is defensive
(table absent on a fresh install → false), so default-off always holds. The
toggle is admin-only (`RequireAdmin`, the ADR-0060 boundary); UI gating is UX,
the endpoint is the boundary.

## Consequences

- A new internet-facing (or VPN-facing) surface on a finance app: an OAuth AS +
  the MCP resource server. Mitigated by OpenIddict (vetted) + WebAuthn reuse +
  read-only + audit; still a real security commitment for the public-connector case.
- Natural-language finance/investment reporting over your own self-hosted data,
  with the numbers staying authoritative (computed by Coffer, not the model).
- New external dependency: OpenIddict + the C# MCP SDK.

## Slices

- **v1** — OpenIddict AS (WebAuthn-backed consent, `coffer.read`, Connected-apps
  revoke UI, DCR cap) + `/mcp` resource server (C# MCP SDK) binding tokens to
  `app.user_id` + the D5 read tools + audit. Validate on claude.ai connector +
  Claude Desktop + Gemini.
- **v2** (this slice, v0.6.0) — investment analytics (income/realized-gains/returns) +
  holdings↔account attribution + account/category drill-down + a runtime admin
  toggle (D8). The originally-separate "deferred v1 tools" and "v2 returns" are
  combined into one slice. Detailed below in "## v2 slice".
- **v3** — FX/multi-currency conversion (and lift the v1 cross-currency guard).

## Implementation notes (v0.5.0)

Built as v1 on `feat/mcp-v1`, validated end-to-end against Claude Desktop (via
`mcp-remote`). Deviations + specifics worth recording:

- **Dynamic Client Registration is hand-rolled.** OpenIddict 7.5 has no built-in
  RFC 7591 endpoint (slated for 7.6), so `/oauth/register` is implemented over
  `IOpenIddictApplicationManager` — which keeps the client cap
  (`Api:Mcp:MaxDynamicClients`, default 50) and redirect-URI validation (https or
  loopback only) under our control. The discovery doc's `registration_endpoint`
  is injected via response middleware (OpenIddict omits it).
- **The RFC 8707 `resource` parameter is stripped.** MCP clients send the MCP
  server URI as `resource`; OpenIddict rejects unknown resources (ID2190). This
  AS protects a single resource (its own `/mcp`), so audience-binding adds
  nothing — the access token is an opaque, revocable, scoped reference token only
  our `/mcp` accepts. Stripping it (pre-validation handlers) keeps the flow
  working across environments without registering a per-deployment resource URL.
- **Reverse-proxy aware.** `UseForwardedHeaders` (X-Forwarded-Proto/Host) so the
  issuer, discovery endpoints, resource metadata, and login redirect resolve to
  the external `https://<domain>` behind Traefik; the in-app HTTPS requirement is
  relaxed (TLS terminates upstream).
- **Stateful MCP transport.** Stateless mode closed the SSE stream `mcp-remote`
  keeps open, causing reconnect-drops; the default stateful transport is used.
- **Persistent OAuth keys** (RSA signing + AES encryption) under the data volume
  so tokens survive restarts. Reference access tokens (1h) + refresh (30d).
- **Off by default** behind `Api:Mcp:Enabled` (compose `COFFER_MCP_ENABLED`): the
  bearer scheme, OAuth AS, MCP server, and all endpoints are absent unless enabled.
- **Gotcha fixed:** the consent form's decision field must not be named `submit`
  (a control named `submit` shadows `HTMLFormElement.submit()`, so the post threw).
- **Deferred to later increments:** investment income (divx) + realized-gains
  (FIFO) tools; a System-settings admin toggle for MCP (today it's config-gated);
  the manual revocable bearer tokens (Connected apps) remain as the no-OAuth path.
  *(These deferred items are picked up in the v2 slice below.)*

## v2 slice (v0.6.0) — analytics + drill-down + runtime toggle

One combined slice: finish investment analytics, attach holdings to accounts, add
account/category drill-down, and move enablement to an admin UI toggle (D8). All
**read-only** (`coffer.read`); RLS is the boundary; money/quantity math runs in
repositories or `Coffer.Domain.*`, never the model (D4); LINQ/EF only, no raw SQL;
every money figure carries a currency code (USD today, D6 forward-compat).

Shared list convention: `limit` default 200 / max 500, **keyset cursor** (opaque,
stable under the chosen sort) with `hasMore` + `nextCursor`.

**Tools**

1. `account_portfolio(ledgerId, accountId)` — per-brokerage portfolio (cash +
   positions + total), a thin wrapper over the existing
   `HoldingsRepository.GetByBrokerageAsync`. The "investment balance by account"
   answer; its `total` (cash + market value) is what makes `list_accounts` /
   `net_worth` correct.
2. `holdings_snapshot` (enrich) — keep the ledger-wide per-security rollup, add
   per-row `heldIn:[{accountId,accountName,quantity}]` and an optional `accountId`
   filter. Fixes "holdings are detached from accounts."
3. `investment_income(ledgerId, fromUtc?, toUtc?, accountId?, securityId?, groupBy=security|account|period)`
   — dividend/interest (`divx`) over the period. *(deferred v1)*
4. `realized_gains(ledgerId, fromUtc?, toUtc?, accountId?, securityId?)` — proceeds
   − consumed FIFO-lot cost, from the existing cost-basis engine's lot
   consumption. *(deferred v1)*
5. `returns(ledgerId, scope=ledger|account, accountId?, fromUtc?, toUtc?, method=irr|twr|both)`
   — a `Coffer.Domain.Investment` computation, **not** LLM-derived. Both figures
   value the portfolio the same way at each boundary: brokerage cash
   (`account_balance_as_of`) + split-adjusted holdings market value (the
   migration-172 feeder, item 10), so a contribution moves value and flow together
   and never distorts the return. IRR (XIRR over the dated external flows +
   start/end value) is the headline. TWR chains sub-period returns across the
   external-flow instants, valued the same way. **Either figure returns `null` +
   its own reason** rather than a wrong number — TWR when no sub-period had an
   invested base at all, IRR when
   the window has no elapsed time, the flows are single-signed, or they offset
   exactly. A null rate always carries a reason and a non-null rate never does;
   consumers must not read `null` as zero.

   TWR is annualized over the time actually **invested**. Sub-periods with a
   non-positive base are skipped rather than fatal: a stretch holding nothing has
   no return to contribute, so voiding the chain over one discards a well-defined
   answer. Voiding it was the original rule, and on a real ledger it blanked every
   account funded after the window opened or emptied before it closed — six of
   nine, including both sides of every rollover. The covered span therefore ships
   with the rate (`timeWeightedCoveredYears`, plus its outer bounds), because
   annualizing a short stretch magnifies it and a ten-month figure must never be
   readable as a five-year one. A total market loss is NOT a skipped period: the
   base stays positive and the ending value is 0, which is a real −100% factor.

   **There is no boundary cap.** `MaxReturnsBoundaries` (400) refused a
   time-weighted figure past 400 external-flow instants, which on a real five-year
   ledger meant the headline whole-portfolio TWR was unavailable — not slow,
   unavailable. It existed because a boundary valuation replayed the ledger twice,
   once for holdings and once for cash. Migrations 200 and 201 made both batched:
   the requested instants are merged into the leg, price and balance streams as
   pseudo-rows, so each is one sort plus one window pass and a whole report costs
   two queries rather than two per boundary. On the stress lane, 390 boundaries
   went from **10,889 ms to 159 ms**, and per-boundary cost now FALLS as boundaries
   grow (0.41 ms at 390 against 0.83 ms at 100) because it no longer scales per
   boundary. The constant was deleted rather than raised — it had been set from a
   bad measurement three times, and a boundary count cannot express a time budget
   anyway, since per-boundary cost scales with accounts in scope.

   `returns_cost_estimate` remains, reporting the flow-instant count from the same
   scope resolution the real call uses, in well under a second. It no longer
   reports a ceiling because there is none. A per-request cap override was asked
   for and declined: it would have promoted an internal constant into public API
   that outlived it, and it never bought what it appeared to — over the old ceiling
   the engine refused TWR outright rather than approximating it, so a lower cap
   bought a faster null, not a coarser number.

   Two further fields exist because a summary that forces the consumer to
   reconstruct its parts gets reconstructed wrongly. At ledger scope, `accounts[]`
   lists every brokerage in scope with its start and end value — the rows sum to
   the report's own totals — so a caller never has to guess which accounts a window
   spans; guessing by current balance drops precisely the accounts a rollover
   emptied. And `netContributions` ships with `contributionsIn` +
   `contributionsOut` (which add to it exactly) and a per-source split, because a
   net is equally consistent with one large movement and with two offsetting ones,
   and a reader holding one salient event will bind the number to it.

   Every reporting response carries `computedAt` and `engineVersion`
   (`semver+sha` — real in published images since the container stamping fix
   recorded in ADR-0044; it read `semver+nogit` when first shipped, which named
   the release but not the commit and so could not tell two builds apart). A consumer assembling one report from several calls has no
   other way to tell a fresh figure from one carried over, and a published report
   showed four accounts as "n/a" for a figure the engine had returned minutes
   earlier. A stamp does not prevent that reuse; it makes it detectable.

   `allocation` takes an `asOfUtc` and values through the SAME feeder `returns`
   uses, rather than the current holdings projection. Its total is securities
   only, so it also reports `excludedBrokerageCash` — cash has no asset class to
   bucket — and the identity `totalMarketValue + excludedBrokerageCash ==
   returns.endValue` holds at the same instant. Those two totals differed by
   $5,339 across two published reports with nothing in either response to explain
   it. It further reports `undecomposedMultiAssets`: a `multi_asset` security with
   no `security_components` cannot be looked through, and one such fund at 66% of
   a portfolio showed equity at 8.5% against a true 35% — silently, because the
   chart looked plausible. The flag is dimension-independent by design; a warning
   that appears and vanishes as the caller switches dimensions is one nobody
   trusts. Both are **live** as of Track-2
   historical valuations. Since-inception anchors on the first external flow —
   value is 0 before it — or, when the scope has no external flow at all, on the
   first activity anywhere in scope. Falling back to the window END instead
   collapsed the window to zero length, where every figure is undefined; assets
   that arrive without a cash flow (in-kind transfer, category posting) are
   absorbed into the start value rather than read as return. Only a scope with
   no activity at all yields a zero-length window.

   **External cash flow is scope-relative** — a brokerage leg facing a real
   account *outside the reported scope's perimeter* (the scoped brokerages plus
   their holdings siblings). The same rollover between two brokerages is therefore
   INTERNAL at ledger scope, where it nets to zero, and EXTERNAL at account scope,
   where it is a real withdrawal from the source and a real contribution to the
   destination. Classifying by counterparty *account type* instead — treating
   every `investment` counterparty as internal at both scopes — made an account
   funded entirely by rollover report the whole balance step as performance, and
   is the bug this rule replaces. A "brokerage" here is any investment account
   that is not some other account's holdings sibling, whether or not it has a
   sibling of its own: a cash-only account that never held a position still takes
   transfers. Internally-reinvested dividends and in-brokerage trades face the
   holdings sibling, which is inside the perimeter at every scope, so they are
   never external. An **in-kind share transfer** (`transfer_shares`) is external
   when its HEADER spans the perimeter, which is a header-level test rather than
   a counterparty one: every leg of such a header faces an account inside its own
   brokerage, so the value crosses accounts while each leg looks internal. The
   flow is the sum of the header's in-scope legs — a contribution at the
   destination, a withdrawal at the source, zero at ledger scope. Without it one
   real transfer put an account at +258%/yr and its counterpart at -10.9%/yr
   simultaneously. A **category** counterparty is external only when the leg's
   `posting_role` is `transfer`. Roles `income` (dividends, interest — and
   investment expenses, since ADR-0027 stamps both `inc` and `exp` splittypes
   `income` and puts direction in the sign) and `fee` are the portfolio's own
   earnings and costs, so they stay inside the return on a net-of-fees basis.
   Treating the counterparty TYPE as the answer is wrong whichever way it is set:
   excluding every category made an employer retirement contribution read as
   investment skill, while including them would have reclassified a real ledger's
   entire $879k dividend-and-interest history as contributed money. ADR-0027
   already draws the line — `posting_role` is the marker and the truth.
6. `transaction_summary` (generalize) — add `groupBy=category|account|payee`
   (default `category`, the v1 behavior) and `rollup` for category/account
   **trees** (parent total includes descendants); rows carry `parentId`.
   `groupBy=account` groups the **cash-leg account** of each categorized posting.
7. `list_transactions(ledgerId, accountId?, categoryId?, payeeId?, minAmount?, maxAmount?, text?, direction=inflow|outflow|all, fromUtc?, toUtc?, sort{field=date|amount|absAmount,dir}, limit, cursor?)`
   — line-level drill over `resolved_transactions`. No forced date window.
8. `list_accounts(ledgerId, includeCategories=false, includeInactive=false, type?)`
   — catalog + **Overview-consistent** balances (investment = cash + holdings MV),
   `parentId` tree, `class` (asset|liability|none), and `taxStatus` (ADR-0066:
   taxable / tax_deferred / tax_free / other) so the model can distinguish a
   taxable brokerage from an IRA.
9. `net_worth(ledgerId, asOf?)` — assets/liabilities/net worth reusing
   `OverviewRepository`; guaranteed to match the Overview screen.
10. `net_worth_history(ledgerId, fromUtc, toUtc, interval=month|quarter|year)` —
    net worth as of the END of each interval period in the window (the final point
    clamped to `toUtc`), assembled from the migration-172 as-of feeder: cash
    balance as of the instant + split-adjusted holdings market value, with the same
    Overview-consistent classification as item 9 (investment accounts fold in
    holdings value; holdings-sibling shadow accounts are never double-counted).
    Each point carries `unpricedSecurityCount` — held securities with no price at
    all at that date (valued at 0, so the figure is understated by those). Capped
    at 600 points (widen the interval over long ranges). Track-2 historical
    valuations; the same feeder unlocks `returns` TWR (item 5). Computed live —
    the ADR-0008 materialized view stays deferred.

**Enablement (D8):** `system_settings` table (migration 147, key/value,
admin-writable), `mcp.enabled` default false; effective gate = `Api:Mcp:Enabled`
config OR the DB setting; read at startup (defensive: absent → false); admin
`GET/PUT /api/admin/system-settings` + a System-settings **MCP** tab with the
toggle, labeled "takes effect after the server restarts."

**Audit:** every new tool call is recorded (the `provider_runs` pattern, D7).

**Out of scope (unchanged):** `coffer.write` tools.
