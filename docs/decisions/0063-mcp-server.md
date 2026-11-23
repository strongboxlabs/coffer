# 0063 — MCP server (AI report-building over Coffer data)

* Status: Accepted (v1 = v0.5.0, built + validated end-to-end with Claude Desktop);
  v2 shipped (v0.6.0 — see "## v2 slice" below). Time-weighted return was
  unconditionally `null` here pending a historical-valuation feed; that feed has
  since landed, and `ReturnsCalculator.Twr` computes it over the window it can
  cover, reporting `TimeWeightedUnavailableReason` plus the covered range when it
  cannot. v0.7.0 (ADR-0065/0066/0067) added
  `find_in_kind_transfer_candidates`, allocation `dimension` + look-through, and
  tax_status / classification fields on the list tools. A later read-surface pass
  added fail-loud enum params (an unknown value errors with the valid list instead
  of silently defaulting), `list_upcoming_reminders`, `list_tags`, and a tag filter
  on `list_transactions` / `transaction_summary`.
* Amended by: [ADR-0105](0105-a-cookie-alone-is-not-enough.md) (consent from a server-held ticket, `form_post` removed, the redirect rule enforced); [ADR-0108](0108-memo-and-tags-per-side.md) (memo and tags are read as a register shows them); [ADR-0109](0109-overview-is-an-attention-first-home.md), 2026-11-06 (`transaction_summary` returns what `GET /reports/summary` returns: the display currency, and with `topN` the drawn rows and the rest); [ADR-0104](0104-reports-are-a-place-you-go.md), 2026-11-09 (`returns_series`, and an in-kind transfer valued at market in `returns`); [ADR-0114](0114-benchmarks-and-currency-conversion.md), 2026-11-13 (`returns_series` takes a `benchmark`; every figure that combines accounts converts into the ledger's reporting currency, and `mixedCurrency` is gone); [ADR-0104](0104-reports-are-a-place-you-go.md), 2026-11-21 (`returns_series`' money-weighted figure is the 12 months ending at each point, `mwrTrailingYear`)
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

**Operating assumption: single base currency (USD).** v1/v2 target
single-currency (USD) ledgers, and any public / multi-user release is explicitly **post-v3** — so
no untrusted multi-currency user ever predates FX, and v1/v2 can total in one
currency safely without a guard. FX is therefore a genuine later enhancement, not
a deferred correctness bug. **Forward-compat (so v3 stays additive, per
"design for general release"):** tool outputs still carry the currency code even
though it's always USD today, and nothing hardcodes USD irreversibly — v3 adds
conversion, it doesn't unwind a baked-in assumption. (FX *would* be a correctness
dependency for a multi-currency portfolio; that's simply out of scope until v3.)
*(Amended 2026-11-03: "always USD" no longer holds. An account carries its own
currency, and nothing converts (FU-106). Every money figure a tool returns is in its
account's own currency, named by `currencyCode` where the result carries one (a
`list_transactions` line, `account_portfolio`), and a sum across accounts in different
currencies is summed as it stands, so it is not one figure; no tool description says
USD. Conversion is still v3.)* *(Amended 2026-11-13, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D7 and D9:
conversion has shipped, per ledger. A figure for one account stays in that account's
currency. Every figure that combines accounts is converted into the ledger's reporting
currency, named by `currencyCode`, or is null with `rate_unavailable` naming the missing
pair and day; no figure adds currencies, and each tool's description says what it
converts and how.)*

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
  *(Conversion shipped 2026-11-13, [ADR-0114](0114-benchmarks-and-currency-conversion.md).)*

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
- **The issuer has no trailing slash.** OpenIddict serialises the issuer through
  `Uri.AbsoluteUri`, which appends `/` to a root URL. The protected-resource
  metadata names the authorization server without one, and RFC 8414 §3.3 requires
  the issuer in the discovery document to match that exactly — the MCP SDK
  enforces it, so `mcp-remote` aborted during auth. The discovery document's
  `issuer` and the RFC 9207 `iss` authorization-response parameter are trimmed to
  the slash-free origin. The `iss` claim inside id tokens is not: they are issued
  only for the `openid` scope and no MCP client validates them.
- **Stateful MCP transport.** Stateless mode closed the SSE stream `mcp-remote`
  keeps open, causing reconnect-drops; the default stateful transport is used.
- **Persistent OAuth keys** (RSA signing + AES encryption) under the data volume
  so tokens survive restarts. Reference access tokens (1h) + refresh (30d).
  *(Moved into the database, sealed under the master key: see the 2026-10-17
  amendment.)*
- **Off by default** behind `Api:Mcp:Enabled` (compose `COFFER_MCP_ENABLED`): the
  bearer scheme, OAuth AS, MCP server, and all endpoints are absent unless enabled.
- **Consent form field name:** the decision field must not be named `submit`
  (a control named `submit` shadows `HTMLFormElement.submit()`, so the post throws).
- **Deferred to later increments:** investment income (divx) + realized-gains
  (FIFO) tools; a System-settings admin toggle for MCP (today it's config-gated);
  the manual revocable bearer tokens (Connected apps) remain as the no-OAuth path.
  *(These deferred items are picked up in the v2 slice below.)*

## v2 slice (v0.6.0) — analytics + drill-down + runtime toggle

One combined slice: finish investment analytics, attach holdings to accounts, add
account/category drill-down, and move enablement to an admin UI toggle (D8). All
**read-only** (`coffer.read`); RLS is the boundary; money/quantity math runs in
repositories or `Coffer.Domain.*`, never the model (D4); LINQ/EF only, no raw SQL;
every money figure carries a currency code (USD today, D6 forward-compat; since
2026-11-03 the account's own, see D6).

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
   answer: voiding it blanks every account funded after the window opened
   or emptied before it closed, including both sides of every rollover. The covered span therefore ships
   with the rate (`timeWeightedCoveredYears`, plus its outer bounds), because
   annualizing a short stretch magnifies it and a ten-month figure must never be
   readable as a five-year one. A total market loss is NOT a skipped period: the
   base stays positive and the ending value is 0, which is a real −100% factor.

   **There is no boundary cap.** A cap on external-flow instants (formerly
   `MaxReturnsBoundaries`, 400) makes the headline whole-portfolio TWR
   unavailable — not slow, unavailable — for any scope with more flows than the
   cap, which years of regular contributions across several accounts exceed.
   Migrations 200 and 201 remove the reason for one: a boundary valuation no
   longer replays the ledger twice, once for holdings and once for cash. The
   requested instants are merged into the leg, price and balance streams as
   pseudo-rows, so each is one sort plus one window pass and a whole report costs
   two queries rather than two per boundary. A boundary count cannot express a
   time budget anyway, since per-boundary cost scales with accounts in scope.

   `returns_cost_estimate` remains, reporting the flow-instant count from the same
   scope resolution the real call uses, in well under a second. It reports no
   ceiling because there is none. There is no per-request cap override either: it
   would promote an internal constant into public API, and it would not buy what it
   appears to — over a ceiling the engine refuses TWR outright rather than
   approximating it, so a lower cap buys a faster null, not a coarser number.

   *(Amended 2026-11-09.)* **`returns_series`** answers the question `returns` cannot,
   how the scope did over time: `returns_series(ledgerId, accountId?, includeAccounts?,
   interval=month|quarter|year, fromUtc?, toUtc?)`, the same result as
   `GET /reports/returns-series` (ADR-0104 D6). Per period end it returns a cumulative
   time-weighted index (100 at the first invested point, not annualised), the covered
   days, the cumulative gain and the money-weighted return to date. It is the chain
   `returns` computes, over flow instants only, with period ends read as marks, so the
   last point equals `returns`' time-weighted growth. A point with no figure is null
   with a reason code, and a consumer must not read it as zero or carry the previous
   value; the money-weighted return is null with a reason until 365 days are covered.
   Each account is its own series, in its own currency; the ledger-wide series is in the
   ledger's reporting currency, unhedged, each brokerage's value and flows converted at
   each instant's rate before the chain runs (amended 2026-11-13, ADR-0114 D7). A day interval is refused, as is a request
   for more than 600 period ends in one series (400). The read runs in one repeatable-read
   snapshot. `returns` and `returns_cost_estimate` are unchanged.

   *(Amended 2026-11-21, ADR-0104 D6.)* The money-weighted figure is `mwrTrailingYear`,
   the money-weighted return of the 12 months ending at each point: what `returns` gives
   for that year as its window, read from the history before `fromUtc` where the year reaches
   back past it. It replaces `mwrToDate`, the annualised rate from the window's
   start, which left a range's whole first year empty whatever history lay before it.
   It is null with `under_a_year_covered` when nothing was invested as its year opened,
   and a rate missing only for the year before the window leaves it alone null, with the
   point naming the rate.

   *(Amended 2026-11-13, ADR-0114 D3.)* `returns_series` takes an optional `benchmark`,
   one of the ledger's benchmark symbols. Every point of every series then carries that
   fund's total return over the series' own invested time: 100 at the series' first
   invested point, paused wherever nothing is invested and resumed from the value it
   held, converted into the series' currency at each day's rate, unhedged, and already
   chained, so no consumer chains it. A null benchmark carries its reason, and a missing
   growth day or rate is never chained across. A symbol that is not one of the ledger's
   is refused with `benchmark-not-in-ledger`, and a value that is not a symbol with
   `benchmark-symbol-invalid`. `GET /reports/returns-series` answers a symbol not in the
   ledger with the `benchmark_not_in_ledger` reason instead, so a saved report keeps its
   pick after the symbol is removed (ADR-0114 D1).

   Two further fields exist because a summary that forces the consumer to
   reconstruct its parts gets reconstructed wrongly. At ledger scope, `accounts[]`
   lists every brokerage in scope with its start and end value — the rows sum to
   the report's own totals — so a caller never has to guess which accounts a window
   spans; guessing by current balance drops precisely the accounts a rollover
   emptied. And `netContributions` ships with `contributionsIn` +
   `contributionsOut` (which add to it exactly) and a per-source split, because a
   net is equally consistent with one large movement and with two offsetting ones,
   and a reader holding one salient event will bind the number to it. `gain`
   ships for the same reason, derived from fields already in the result: it is
   `endValue − startValue − netContributions` exactly, the window's change in
   value that contributions and withdrawals do not explain (price movement,
   realized or not, plus dividends and interest, less fees and investment
   expenses; not annualized; for the whole scope). Measured from the start value,
   it is neither the FIFO realized gain nor the unrealized gain against cost, and
   like `netContributions` it depends on the scope, so the `accounts[]` rows do
   not carry it.

   Every reporting response carries `computedAt` and `engineVersion`
   (`semver+sha`; container builds pass the git values in, ADR-0044, so two
   builds of one release stay distinguishable). A consumer assembling one report from several calls has no
   other way to tell a fresh figure from one carried over, and a report assembled
   that way can show an account as "n/a" for a figure the engine returned minutes
   earlier. A stamp does not prevent that reuse; it makes it detectable.

   `allocation` takes an `asOfUtc` and values through the SAME feeder `returns`
   uses, rather than the current holdings projection. Its total is securities
   only, so it also reports `excludedBrokerageCash` — cash has no asset class to
   bucket — and the identity `totalMarketValue + excludedBrokerageCash ==
   returns.endValue` holds when both value the same instant: with no instant given,
   the end of the caller's today; for a returns window with an explicit `toUtc`,
   which is exclusive, the instant one microsecond before it, where that window's
   end is valued. Without it, two reports could
   disagree on those totals with nothing in either response to explain it. It
   further reports `undecomposedMultiAssets`: a `multi_asset` security with no
   `security_components` cannot be looked through, so a portfolio held mostly
   through one such fund shows equity at a fraction of its true share —
   silently, because the chart looks plausible. The flag is dimension-independent by design; a warning
   that appears and vanishes as the caller switches dimensions is one nobody
   trusts. Both are **live** as of Track-2
   historical valuations. Since-inception anchors on the earlier of a funded
   opening balance's start date and the first external flow, or, when the scope
   has neither, on the first activity anywhere in scope, and is valued at the
   anchor. Falling back to the window END instead
   would collapse the window to zero length, where every figure is undefined; assets
   that arrive without a cash flow (in-kind transfer, category posting) are
   absorbed into the start value rather than read as return. Only a scope with
   no activity at all yields a zero-length window.

   *(Amended 2026-11-21.)* A funded opening balance is its brokerage's money from the
   instant it arrives, never before: the start of its start date, or the brokerage's (or
   its holdings') first live posting when that is earlier, since the running balance
   stands on the opening balance whatever the dates and the register shows it from that
   posting. The cash read carries an account's opening balance at every instant, so every
   returns figure, scalar and series, values a brokerage without it before it arrives;
   the brokerage then holds exactly nothing, never less. A window valued before the
   arrival takes the balance as a flow at that instant, under the contribution source
   `opening_balances`, signed as the balance is (a negative one, a debt carried in, is
   money out). That includes an explicit window that opens on the arrival, which is
   valued at the instant before it, as for any contribution dated at its start. Without
   this, a ledger window that opened before a brokerage did carried its balance idle from
   the window's start, pulling the index and both rates toward zero until it opened. A
   window that opens later holds the balance in its start value, as before, and so does
   since-inception, which anchors no later than the first balance to arrive; a funded
   start date after the window's end is no anchor, since that balance is never held in
   the window. So a brokerage's own since-inception figures are unchanged, unless it has a
   posting dated before its start date that is not an external flow: its window then
   starts there, not on the start date. `gain` is unchanged too, save that a ledger no
   longer counts the currency move on a foreign brokerage's balance before that balance
   arrived. A balance with no start date has no day it arrived and is held at every
   instant.

   **The window's edges.** `fromUtc` is inclusive and `toUtc` exclusive, compared
   at the microsecond, timestamptz's resolution. An explicit window's start is
   valued at the last instant before `fromUtc`, and an explicit end at the last
   instant before `toUtc`; with no `toUtc` the end is the last instant of the
   caller's today as the register counts it, which is itself counted. An event
   dated on an edge therefore lands in exactly one window, and every flow valued
   into `endValue` is one of the window's flows: a dividend, interest or fee dated
   the first day is that window's return, a contribution dated `fromUtc` is that
   window's flow, and one dated `toUtc` is the next window's. A register date is
   midnight UTC, so valuing the start AT `fromUtc` would fold the first day's
   events into `startValue`, where no window's return counts them. Since-inception
   keeps its rule: valued at the anchor, with a flow landing there taken back out
   of `startValue`, so it counts once, as a flow.

   **External cash flow is scope-relative** — a brokerage leg facing a real
   account *outside the reported scope's perimeter* (the scoped brokerages plus
   their holdings siblings). The same rollover between two brokerages is therefore
   INTERNAL at ledger scope, where it nets to zero, and EXTERNAL at account scope,
   where it is a real withdrawal from the source and a real contribution to the
   destination. Classifying by counterparty *account type* instead — treating
   every `investment` counterparty as internal at both scopes — makes an account
   funded entirely by rollover report the whole balance step as performance. A "brokerage" here is any investment account
   that is not some other account's holdings sibling, whether or not it has a
   sibling of its own: a cash-only account that never held a position still takes
   transfers. Internally-reinvested dividends and in-brokerage trades face the
   holdings sibling, which is inside the perimeter at every scope, so they are
   never external. An **in-kind share transfer** (`transfer_shares`) is external
   when its HEADER spans the perimeter, which is a header-level test rather than
   a counterparty one: every leg of such a header faces an account inside its own
   brokerage, so the value crosses accounts while each leg looks internal. The
   flow is the sum of the header's in-scope legs — a contribution at the
   destination, a withdrawal at the source, zero at ledger scope — with each share
   leg valued at **market** at the transfer instant, by the rule every boundary
   valuation uses (the latest close on or before the instant's date, else the
   security's last priced trade, restated onto the instant's split basis), not at
   the carried cost basis the legs hold (ADR-0065 D5). `contributionsBySource`
   reports that market amount under `in_kind_transfers`. A security with neither a
   close nor a priced trade is carried at cost by every valuation, so there the
   flow is the carried basis. Without it an
   in-kind transfer puts the destination at an absurd positive annual return and
   the source at a negative one simultaneously. A **category** counterparty is external only when the leg's
   `posting_role` is `transfer`. Roles `income` (dividends, interest — and
   investment expenses, since ADR-0027 stamps both `inc` and `exp` splittypes
   `income` and puts direction in the sign) and `fee` are the portfolio's own
   earnings and costs, so they stay inside the return on a net-of-fees basis.
   Treating the counterparty TYPE as the answer is wrong whichever way it is set:
   excluding every category makes an employer retirement contribution read as
   investment skill, while including them would reclassify a ledger's
   entire dividend-and-interest history as contributed money. ADR-0027
   already draws the line — `posting_role` is the marker and the truth.
6. `transaction_summary` (generalize) — add `groupBy=category|account|payee`
   (default `category`, the v1 behavior) and `rollup` for category/account
   **trees** (parent total includes descendants); rows carry `parentId`.
   `groupBy=account` groups the **cash-leg account** of each categorized posting.
   *(2026-11-06, [ADR-0109](0109-overview-is-an-attention-first-home.md)'s third
   slice: it returns what `GET /reports/summary` returns, through the same
   `ReportingRepository.SummaryAsync` and `ReportSummaryDto.From`, so it adds
   `currencyCode`, and with `topN` each row's `drawn`, a
   rolled-up kept parent's `isDirect` rows and `rest { count, total, byPeriod,
   only }`. Since 2026-11-13 (ADR-0114 D7, D9) every amount is converted into the
   ledger's reporting currency, and a figure that needs a missing rate is null with
   `rateUnavailable`; there is no `mixedCurrency`. It
   passes no local date, so a sliding window resolves against the clock, as
   before. Its description says how to read them.)*
7. `list_transactions(ledgerId, accountId?, categoryId?, payeeId?, minAmount?, maxAmount?, text?, direction=inflow|outflow|all, fromUtc?, toUtc?, sort{field=date|amount|absAmount,dir}, limit, cursor?)`
   — line-level drill over `resolved_transactions`. No forced date window.
   *(2026-10-27: each line's memo and tags are read as one register shows them;
   see the amendment of that date. 2026-11-03: each line also carries `legId`, its
   own leg, and `currencyCode`, the currency its amount is in; see D6.)*
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

## Amendment — 2026-10-17: the OAuth keys live in the database, sealed under the master key

The signing and encryption keys were two plaintext files under `data/openiddict`, on the
data volume but outside the database, so no backup carried them. Restored onto a new host,
every token issued before fails with ID2004 and every MCP client has to re-authorize; and
files written 0644 and non-atomically let a crash part-way through writing `signing.pem`
leave an install that will not boot (FU-069).

They are now one row of `oidc_server_keys` (migration 253), each key sealed under the
master KEK like the backup passphrase and the Drive token. So a backup carries them, a
master-key rotation re-wraps them, and post-restore reconciliation deletes a pair sealed
under another install's key (ADR-0092 D5), after which a fresh pair is minted. They also
count as wrapped material for ADR-0092 D3 (its 2026-10-27 amendment), so a key-less boot
of an install that holds a pair refuses rather than minting a key that cannot open it. The keys
load once at boot, after migrations and reconciliation and before the server serves, and
reach OpenIddict through an options callback rather than `AddSigningKey`, which wants the
key while services are still being registered.

- **Upgrade.** The first boot with the table moves existing files in and deletes them, so
  connected clients stay signed in. A file that is not a readable key (a torn write) is
  replaced by a fresh key. So is a pair anyone can read, recognised by the fingerprints in
  `PublishedKeyFingerprints`: an install still holding it re-authorizes its
  clients once rather than carrying a public key into the database. A file the API cannot
  read at all (permissions) stops the move and is left alone, since it may hold a good key.
- **Not fatal.** A stored pair that does not open under the master key (a key file swapped
  outside a restore) is neither loaded nor overwritten: MCP sign-in is off, the boot log
  says why, and the rest of the app serves. Overwriting would destroy the right pair the
  moment the operator mounts the right key.
- **Compromise.** The remedy was "delete the files and restart"; it is now
  `coffer-api reset-oauth-keys` and a restart (operations.md, Secrets). Web sign-in does
  not depend on these keys.
- **Always on.** The row is kept whether or not MCP is enabled, so every backup carries
  it and the old files go away on the first boot regardless.

## Amendment — 2026-10-19: grants end, users see them, and `coffer.read` is required

D2 listed revocation and a "Connected apps" revoke page. Until now the revoke page held
only MCP tokens, and no revocation endpoint was served. Refresh tokens are sliding, so an
OAuth grant never ended.
- An OAuth grant now lasts 90 days from consent, however often its app refreshes.
- *Account → Security → Connected apps* lists the user's grants beside their MCP tokens,
  and disconnects them.
- `/oauth/revoke` is served (RFC 7009).
- `/mcp` requires `coffer.read`. A token without it gets 403 `insufficient_scope`.

`prompt=none` and `prompt=login` now behave as OpenID Connect says. Details are in
[ADR-0105](0105-a-cookie-alone-is-not-enough.md)'s 2026-10-19 amendment (FU-087, FU-095).

## Amendment — 2026-10-27: memo and tags are read as a register shows them (ADR-0108)

Memo and tags are per side of a transaction (ADR-0108 D1), so a tool has to say whose side it
reads. Every read tool reads the stored presentation, the same values the registers show:

- **`list_transactions` reads each line as one register shows it** (ADR-0108 D17): the register of
  `accountId` when given; else of `categoryId`; else the line's own account's. A line
  between two real accounts is read from the posting's outflow leg, and for a zero amount
  from the leg of the account the transaction belongs to, so both of its lines read the
  same. Each line carries `memo` (what that register shows), `transactionMemo` (the
  transaction's memo, only where that register is the account that split it) and `tags`
  (the tags in play there). The tag filter and the text search match only what the line
  is shown with.
- **`transaction_summary` reads the real account's perspective** (ADR-0108 D2). A posting counts the
  tags the paying account's register shows for it, and on a split entered from that
  account the transaction's tags count for every line; a posting between two categories
  counts the tags of the category it is grouped under.
- **`list_upcoming_reminders`** shows each occurrence's memo as the originating account's
  register shows it, from the stored values; a template with no lines shows its own.

The write side (`set_transaction_tags` requiring `accountId`) is ADR-0081's amendment of
this date.
