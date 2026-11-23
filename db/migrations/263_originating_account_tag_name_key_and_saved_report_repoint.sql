-- =============================================================================
-- 263 — the originating account, the one tag-name key, and the saved-report tag
--       repoint (memo and tags per side)
-- =============================================================================
--
-- Four things the memo-and-tags rework stands on, none of which moves a memo or
-- a tag yet (264 does that, from what this migration records):
--
--   1. txn_headers.originating_account_id — the account a transaction was entered
--      from. A one-split has two sides that each hold their own memo and tags, and
--      the side that entered it holds them first; a multi-split is edited from its
--      split side only; an investment event belongs to its brokerage. Until now the
--      register inferred the side from posting counts, which cannot tell the two
--      sides of a one-split apart, nor the two sides of a split whose every line
--      goes to one category. The value is stored instead, and handed over by the
--      writers that reshape a transaction (a split, turning a row into an event, a
--      repoint that leaves no leg on it).
--
--      NULL EXACTLY WHEN THE HEADER HAS NO LEGS. Such headers can exist — no
--      constraint forbids them — and they name no account. No runtime check
--      enforces either half: the end of this script asserts it, and the tests do.
--
--   2. tag_name_key(text) — the only definition of "the same tag name": lower()
--      of the name with leading and trailing white space removed, where white space
--      is .NET's char.IsWhiteSpace set (the writers store string.Trim()'s result,
--      which uses the same set). Every name-to-id lookup goes through it. Its index
--      arrives with 264, after the existing case variants are merged.
--
--   3. Saved reports filter on a tag by id inside saved_reports.spec ("tagId"), the
--      one stored tag reference outside a foreign key. A tag merge must repoint it
--      or the report silently matches nothing. saved_report_spec_tag_id is its only
--      parser; saved_reports_repoint_tag is the narrow privileged function the
--      user's merge calls, because the merging member cannot reach other members'
--      private reports under RLS.
--
--   4. transfer_shares_source_brokerages(ledger) — the only definition of an in-kind
--      transfer's source brokerage, which owns it. This script's backfill and end
--      assertion, the Moneydance import check and the test-time invariant check all
--      read it, so they cannot disagree on which brokerage that is.
--
-- BEFORE THE BACKFILL, one shape of in-kind transfer that has no source brokerage
-- (a holdings leg and a transfer leg to a category or another non-brokerage
-- account, nothing else) is rebuilt as a sale with a transfer, priced at the cost
-- of the shares it takes so its realized gain is zero (section 4b). Every other header is
-- read as it stands.
--
-- THE BACKFILL derives each header's value from its CURRENT shape first, then
-- narrows a candidate set by a fixed chain. It never distinguishes a feed row from
-- a hand-entered one. The chain decides only where the shape leaves a choice, and
-- is used here only — never as a runtime rule.
--
--   Candidate set E:
--     transfer_shares      the source brokerage, as transfer_shares_source_brokerages
--                          (section 4) defines it.
--     other actioned       the brokerages carrying a leg in EVERY posting (the
--                          funding brokerage of a buy/sell/dividend transfer is on
--                          the transfer posting only).
--     action-less, N > 1   the accounts carrying a leg in every posting (the split
--                          side; two of them when every line goes to one category).
--     action-less, N = 1   every account on its legs.
--     empty                every account on the header's legs, counted apart. For an
--                          actioned header the end assertion then fails the upgrade.
--
--   Chain (a step that would keep none of E is skipped; the first step that leaves
--   one account decides):
--     1. the Moneydance home account (acctid), read as 260 reads it;
--     2. the reminder source: a template's series source_account_id; an occurrence's
--        series source_account_id, else its template's value (templates first);
--     3. a real account over a category;
--     4. the outflow side (legs on the header summing below zero);
--     5. the lower account id.
--
-- DETERMINISTIC. Every value is read off stored data; nothing reads the clock.
-- IDEMPOTENT. Only NULL values are filled, so a re-run finds nothing to do and never
-- overwrites a value a writer has set since.
-- -----------------------------------------------------------------------------

BEGIN;

-- ---------------------------------------------------------------------------
-- 1) tag_name_key
--
-- The body calls pg_catalog functions only, schema-qualified, so it means the same
-- under any search_path — including the restricted one maintenance operations use
-- when they evaluate 264's index on it. No SET clause: that would stop the planner
-- inlining it into the queries that compare keys.
--
-- The class is .NET's char.IsWhiteSpace set, written out character by character,
-- because [[:space:]] follows the server's locale and would not match it (the same
-- class as 260's blank test). lower() follows the database collation; a schema test
-- pins that collation, so a different one fails CI instead of folding differently.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.tag_name_key(p_name text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
AS $fn$
    SELECT pg_catalog.lower(pg_catalog.regexp_replace(
        p_name,
        '^[\u0009-\u000D \u0085   -     　]+'
        || '|[\u0009-\u000D \u0085   -     　]+$',
        '', 'g'));
$fn$;

COMMENT ON FUNCTION public.tag_name_key(text) IS
    'The one definition of "the same tag name" (mig 263): lower() of the name with leading '
    'and trailing .NET white space removed. Every name-to-id lookup compares these keys; '
    'the per-ledger unique index on it arrives with 264.';

GRANT EXECUTE ON FUNCTION public.tag_name_key(text) TO coffer_app;

-- The key of each of a list of names, in one call, so a writer resolving a request's
-- names (and the ones it has not saved yet) learns every key in the same round trip
-- that finds the matching tags. A row source the API composes in LINQ: EF cannot
-- root a query on a parameter array, and without this the request's own names
-- would have to be folded in .NET, a second definition of the rule. Not STRICT, so
-- the planner can inline it into that query.
CREATE OR REPLACE FUNCTION public.tag_name_keys(p_names text[])
RETURNS TABLE (name text, key text)
LANGUAGE sql IMMUTABLE PARALLEL SAFE
AS $fn$
    SELECT n.name, public.tag_name_key(n.name)
      FROM pg_catalog.unnest(p_names) AS n(name);
$fn$;

COMMENT ON FUNCTION public.tag_name_keys(text[]) IS
    'tag_name_key over a list of names, one row each (mig 263): the row source the API '
    'joins to tags so a request''s names are keyed in SQL, in one round trip.';

GRANT EXECUTE ON FUNCTION public.tag_name_keys(text[]) TO coffer_app;

-- ---------------------------------------------------------------------------
-- 2) saved_report_spec_tag_id — the only parser of a stored "tagId"
--
-- The SPA stores its ReportSpec verbatim, so "tagId" is a uuid string or absent.
-- Anything else (a number, null, a malformed string) is no tag. The cast sits in
-- the THEN branch, behind both tests: PostgreSQL does not order the operands of an
-- AND, but it does evaluate a CASE in order, so a malformed value is never cast.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.saved_report_spec_tag_id(p_spec jsonb) RETURNS uuid
LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
SET search_path = pg_catalog, public
AS $fn$
    SELECT CASE
             WHEN pg_catalog.jsonb_typeof(p_spec -> 'tagId') = 'string'
              AND (p_spec ->> 'tagId')
                  ~ '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$'
             THEN (p_spec ->> 'tagId')::uuid
           END;
$fn$;

COMMENT ON FUNCTION public.saved_report_spec_tag_id(jsonb) IS
    'The tag a saved report filters on: spec.tagId as a uuid when it is a hyphenated uuid '
    'string, else NULL (mig 263). The only parser of a stored tagId; both tag merges read '
    'it through this.';

-- Only its owner calls it: the repoint below runs as the owner, and so does 264.
REVOKE ALL ON FUNCTION public.saved_report_spec_tag_id(jsonb) FROM PUBLIC;

-- ---------------------------------------------------------------------------
-- 3) saved_reports_repoint_tag — the user's tag merge reaches every member's reports
--
-- Saved-report writes are owner-only under RLS (254), so a merge running as the
-- merging member would repoint that member's reports and leave every other
-- member's filtering on a tag that no longer exists. This runs as its owner, the
-- migration role (BYPASSRLS), and is therefore a second place that checks ledger
-- access. It checks first and then does exactly one thing:
--
--   * the caller (app.user_id) holds an owner or editor grant on the ledger — the
--     predicate of every role-gated write policy, and of the tags write the merge
--     already needs; otherwise insufficient_privilege. If that predicate changes,
--     this check must change with it;
--   * the two tags differ and are both tags of that ledger; otherwise
--     invalid_parameter_value;
--   * then one UPDATE of spec.tagId, from one tag to the other, on that ledger's
--     reports only. No other field: updated_at stays, since the owner did not edit
--     the report.
--
-- It returns the reports changed and, of those, the ones the caller can see (own or
-- shared). The merge reports only the second, so it never reveals how many private
-- reports another member keeps on the tag.
--
-- Pinned search_path and schema-qualified names, as 213/248 do for the other
-- definer. Grants minimal: PUBLIC's default EXECUTE is revoked and only coffer_app
-- may call it; the owner needs no grant.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.saved_reports_repoint_tag(
    p_ledger_id uuid, p_from_tag_id uuid, p_to_tag_id uuid)
RETURNS TABLE (repointed integer, repointed_visible integer)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, public
AS $fn$
DECLARE
    v_user uuid := public.current_app_user_id();
BEGIN
    IF v_user IS NULL OR NOT EXISTS (
           SELECT 1
             FROM public.user_ledger_grants g
            WHERE g.ledger_id = p_ledger_id
              AND g.user_id = v_user
              AND g.role IN ('owner', 'editor')) THEN
        RAISE EXCEPTION 'saved_reports_repoint_tag: the caller holds no owner or editor grant on this ledger'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF p_from_tag_id IS NULL OR p_to_tag_id IS NULL OR p_from_tag_id = p_to_tag_id
       OR (SELECT count(*)
             FROM public.tags t
            WHERE t.ledger_id = p_ledger_id
              AND t.id IN (p_from_tag_id, p_to_tag_id)) <> 2 THEN
        RAISE EXCEPTION 'saved_reports_repoint_tag: the two tags must differ and both belong to the ledger'
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    RETURN QUERY
    WITH changed AS (
        UPDATE public.saved_reports r
           SET spec = pg_catalog.jsonb_set(r.spec, '{tagId}', pg_catalog.to_jsonb(p_to_tag_id::text))
         WHERE r.ledger_id = p_ledger_id
           AND public.saved_report_spec_tag_id(r.spec) = p_from_tag_id
        RETURNING r.owner_user_id, r.visibility
    )
    SELECT count(*)::integer,
           (count(*) FILTER (WHERE c.owner_user_id = v_user OR c.visibility = 'ledger'))::integer
      FROM changed c;
END;
$fn$;

COMMENT ON FUNCTION public.saved_reports_repoint_tag(uuid, uuid, uuid) IS
    'SECURITY DEFINER (mig 263): rewrites saved_reports.spec.tagId across members on a tag '
    'merge, after the caller''s owner/editor grant check; the only field it writes. Returns '
    'the reports changed and those the caller can see. A second ledger-access check beside '
    'RLS: it must follow any change to the write-role predicate.';

REVOKE ALL ON FUNCTION public.saved_reports_repoint_tag(uuid, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.saved_reports_repoint_tag(uuid, uuid, uuid) TO coffer_app;

-- ---------------------------------------------------------------------------
-- 4) transfer_shares_source_brokerages — the source brokerage of an in-kind transfer
--
-- For every transfer_shares header of one ledger, the brokerage the shares leave:
--
--   strict    the brokerage whose leg sits in the same posting as a negative-quantity
--             leg on that brokerage's own holdings account. Pairing by posting keeps
--             the source unique when two brokerages name the same holdings account
--             (accounts.holdings_account_id is not unique);
--   fallback  only for a header on which no posting pairs them: any brokerage with a
--             leg on the header whose holdings account carries the negative leg.
--
-- Permanent, because three checks must agree on it: this script's backfill and end
-- assertion, the Moneydance import check, and the test-time invariant check. No
-- request path calls it. SECURITY INVOKER, not STRICT and no SET clause, so the
-- planner can inline it; the names are schema-qualified instead. Only its owner
-- calls it (migrations and the import run as the owner), so PUBLIC's default
-- EXECUTE is revoked.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.transfer_shares_source_brokerages(p_ledger_id uuid)
RETURNS TABLE (header_id uuid, account_id uuid)
LANGUAGE sql STABLE PARALLEL SAFE
AS $fn$
    WITH strict_form AS (
        SELECT DISTINCT s.header_id, p.account_id
          FROM public.txn_headers h
          JOIN public.txn_legs s ON s.header_id = h.id AND s.quantity < 0
          JOIN public.txn_legs p ON p.header_id = s.header_id
                                AND p.posting_index = s.posting_index
                                AND p.id <> s.id
          JOIN public.accounts b ON b.id = p.account_id AND b.holdings_account_id = s.account_id
         WHERE h.ledger_id = p_ledger_id
           AND h.action = 'transfer_shares'
    ),
    fallback_form AS (
        SELECT DISTINCT s.header_id, b.id AS account_id
          FROM public.txn_headers h
          JOIN public.txn_legs s ON s.header_id = h.id AND s.quantity < 0
          JOIN public.accounts b ON b.holdings_account_id = s.account_id AND b.ledger_id = h.ledger_id
          JOIN public.txn_legs p ON p.header_id = s.header_id AND p.account_id = b.id
         WHERE h.ledger_id = p_ledger_id
           AND h.action = 'transfer_shares'
           AND NOT EXISTS (SELECT 1 FROM strict_form f WHERE f.header_id = s.header_id)
    )
    SELECT header_id, account_id FROM strict_form
    UNION ALL
    SELECT header_id, account_id FROM fallback_form;
$fn$;

COMMENT ON FUNCTION public.transfer_shares_source_brokerages(uuid) IS
    'The source brokerage of each transfer_shares header of a ledger (mig 263): the '
    'brokerage paired in one posting with a negative-quantity leg on its own holdings '
    'account, else any brokerage on the header whose holdings account carries that leg. '
    'The one definition the backfill, the import check and the test invariant check share.';

REVOKE ALL ON FUNCTION public.transfer_shares_source_brokerages(uuid) FROM PUBLIC;

-- ---------------------------------------------------------------------------
-- 4b) In-kind transfers with no source brokerage become zero-gain sales
--
-- An in-kind transfer whose source brokerage neither form of section 4 finds has
-- no owner to record, and assertion (c) would refuse the upgrade. One shape of it
-- is rebuilt here, before the backfill reads any header (ADR-0108 D18):
--
--   one posting of two legs: a negative-quantity leg carrying the security, role
--   'security' and an amount at or below zero (its carried cost), on a holdings
--   account exactly one brokerage of the ledger names; and a 'transfer' leg with
--   no security or quantity whose amount balances it, on an account that is
--   neither a brokerage nor a holdings account.
--
-- It becomes the sale with a transfer that the importer and the investment editor
-- write (action sellx): posting 0 pairs the holdings leg with a new brokerage cash
-- leg (role 'security'); posting 1 pairs a new brokerage cash leg (role
-- 'transfer') with the transfer leg, moved there. The sale is priced at the cost
-- the FIFO walk gives the shares it takes, at the 2 decimals an amount is stored
-- at, and the unit price is set as the editor sets it, round(|amount| / |quantity|,
-- 6). Its total realized gain is therefore zero, except that a cost ending in
-- exactly half a cent records a gain of one cent, as any sale at that cost does.
-- Where that cost differs from the carried cost (a purchase of the shares was
-- edited since), the transfer leg's amount moves to it. Where the lots straddle the
-- long-term line at different costs, the walk apportions the proceeds by share
-- count and the cost by lot, so the long- and short-term parts offset each other,
-- as on any sale at cost the importer writes. A hidden or merged header, which the
-- walk leaves out, keeps the carried cost and records no gain.
--
-- It reads only what the ledger holds now, never a provider payload. Any other
-- shape is left as it is, and assertion (c) still refuses it.
--
-- DETERMINISTIC: a new leg's id is derived from its header's id and its role, and
-- its created_at is the holdings leg's. IDEMPOTENT: a rebuilt header is a sellx,
-- so a re-run finds nothing to do.
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE mig263_inkind ON COMMIT DROP AS
WITH sourced AS (
    SELECT s.header_id
      FROM public.ledgers g
     CROSS JOIN LATERAL public.transfer_shares_source_brokerages(g.id) s
), unsourced AS (
    SELECT h.id AS header_id, h.ledger_id, h.posted_at
      FROM txn_headers h
     WHERE h.action = 'transfer_shares'
       AND EXISTS (SELECT 1 FROM txn_legs l WHERE l.header_id = h.id)
       AND NOT EXISTS (SELECT 1 FROM sourced s WHERE s.header_id = h.id)
)
SELECT u.header_id, u.ledger_id, u.posted_at,
       hl.id AS holdings_leg_id, hl.account_id AS holdings_id, hl.security_id,
       hl.quantity, hl.amount, hl.created_at,
       tl.id AS transfer_leg_id,
       b.id AS brokerage_id
  FROM unsourced u
  JOIN txn_legs hl ON hl.header_id = u.header_id AND hl.quantity < 0
  JOIN txn_legs tl ON tl.header_id = u.header_id AND tl.id <> hl.id
  JOIN accounts b  ON b.holdings_account_id = hl.account_id AND b.ledger_id = u.ledger_id
  JOIN accounts ta ON ta.id = tl.account_id
 WHERE (SELECT count(*) FROM txn_legs x WHERE x.header_id = u.header_id) = 2
   AND hl.posting_index = 0
   AND tl.posting_index = 0
   AND hl.security_id IS NOT NULL
   AND hl.posting_role = 'security'
   AND hl.amount <= 0
   AND tl.posting_role = 'transfer'
   AND tl.security_id IS NULL
   AND tl.quantity IS NULL
   AND tl.amount = -hl.amount
   AND ta.holdings_account_id IS NULL
   AND NOT EXISTS (SELECT 1 FROM accounts y WHERE y.holdings_account_id = ta.id)
   AND (SELECT count(*) FROM accounts y
         WHERE y.holdings_account_id = hl.account_id AND y.ledger_id = u.ledger_id) = 1;

DO $inkind$
DECLARE
    v_rebuilt bigint;
BEGIN
    SELECT count(*) INTO v_rebuilt FROM mig263_inkind;
    IF v_rebuilt = 0 THEN
        RETURN;
    END IF;

    UPDATE txn_legs l
       SET posting_index = 1
      FROM mig263_inkind r
     WHERE l.id = r.transfer_leg_id;

    UPDATE txn_legs l
       SET unit_price = round(abs(r.amount) / abs(r.quantity), 6)
      FROM mig263_inkind r
     WHERE l.id = r.holdings_leg_id;

    INSERT INTO txn_legs (id, header_id, account_id, ledger_id, posting_index, amount,
                          posting_role, created_at)
    SELECT md5(r.header_id::text || ':263:sale-cash')::uuid, r.header_id, r.brokerage_id,
           r.ledger_id, 0, -r.amount, 'security', r.created_at
      FROM mig263_inkind r
    UNION ALL
    SELECT md5(r.header_id::text || ':263:transfer-cash')::uuid, r.header_id, r.brokerage_id,
           r.ledger_id, 1, r.amount, 'transfer', r.created_at
      FROM mig263_inkind r;

    UPDATE txn_headers h
       SET action = 'sellx'
      FROM mig263_inkind r
     WHERE h.id = r.header_id;

    -- The price: the cost the walk (pure; it reads the rebuilt legs) gives each sale.
    -- The cost a sale consumes does not depend on its proceeds, so one walk per
    -- position prices all of its sales.
    UPDATE txn_legs l
       SET amount = CASE WHEN l.id IN (r.holdings_leg_id,
                                       md5(r.header_id::text || ':263:transfer-cash')::uuid)
                         THEN -c.price ELSE c.price END,
           unit_price = CASE WHEN l.id = r.holdings_leg_id
                             THEN round(c.price / abs(r.quantity), 6) ELSE l.unit_price END
      FROM mig263_inkind r
      JOIN (SELECT g.sell_leg_id, round(g.cost_basis_sold, 2) AS price
              FROM (SELECT DISTINCT holdings_id, security_id FROM mig263_inkind) p
             CROSS JOIN LATERAL holdings_fifo_walk(p.holdings_id, p.security_id) w
             CROSS JOIN LATERAL unnest(w.o_gains) g) c ON c.sell_leg_id = r.holdings_leg_id
     WHERE l.header_id = r.header_id;

    -- What the save interceptors derive from legs, for the rebuilt headers only: the
    -- posting counts, the running balance of every account on them from the earliest
    -- rebuilt date, and the holdings.
    PERFORM fn_recompute_posting_counts_for_header(r.header_id)
       FROM mig263_inkind r;
    PERFORM fn_recompute_balances_for_account(b.account_id, b.from_posted_at)
       FROM (SELECT l.account_id, min(r.posted_at) AS from_posted_at
               FROM mig263_inkind r
               JOIN txn_legs l ON l.header_id = r.header_id
              GROUP BY l.account_id) b;
    PERFORM recompute_holdings_cost_basis(NULL, p.holdings_id, p.security_id)
       FROM (SELECT DISTINCT holdings_id, security_id FROM mig263_inkind) p;

    RAISE NOTICE '263: % in-kind transfer(s) with no source brokerage rebuilt as zero-gain sales',
        v_rebuilt;
END $inkind$;

-- ---------------------------------------------------------------------------
-- 5) The column
-- ---------------------------------------------------------------------------
ALTER TABLE txn_headers ADD COLUMN IF NOT EXISTS originating_account_id uuid;

COMMENT ON COLUMN txn_headers.originating_account_id IS
    'The account the transaction was entered from (mig 263): the entering side of a '
    'one-split, the split side of a multi-split, the brokerage of an investment event (the '
    'source brokerage of transfer_shares). A reshape hands it to the side that does it. '
    'NULL exactly when the header has no legs; asserted, never enforced at runtime.';

-- ---------------------------------------------------------------------------
-- 6) The backfill
-- ---------------------------------------------------------------------------

-- Helper, dropped at the end. Moneydance writes every field as a JSON string, and
-- the importer reads a field only when it IS one — as in 260.
CREATE OR REPLACE FUNCTION mig263_md_text(payload jsonb, key text) RETURNS text
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT CASE WHEN jsonb_typeof(payload -> key) = 'string' THEN payload ->> key END;
$fn$;

-- The source brokerage of every in-kind transfer, read once for the candidate set
-- and assertion (c) alike.
CREATE TEMP TABLE mig263_sources ON COMMIT DROP AS
SELECT s.header_id, s.account_id
  FROM public.ledgers g
 CROSS JOIN LATERAL public.transfer_shares_source_brokerages(g.id) s;

ANALYZE mig263_sources;

-- Every header still to fill, with its posting count (0 when legless). Of the
-- payload it keeps only the Moneydance home key the chain reads, not the payload.
CREATE TEMP TABLE mig263_hdr ON COMMIT DROP AS
SELECT h.id AS header_id, h.ledger_id, h.action, h.is_recurring_template,
       h.recurring_transaction_id, h.import_source, h.external_id,
       mig263_md_text(h.provider_raw_payload, 'acctid') AS md_acctid,
       count(DISTINCT l.posting_index)::integer AS postings
  FROM txn_headers h
  LEFT JOIN txn_legs l ON l.header_id = h.id
 WHERE h.originating_account_id IS NULL
 GROUP BY h.id;

-- One row per (header, account on its legs).
CREATE TEMP TABLE mig263_acct ON COMMIT DROP AS
SELECT l.header_id, l.account_id,
       count(DISTINCT l.posting_index)::integer AS postings_on,
       sum(l.amount) AS net,
       a.account_type = 'category' AS is_category,
       a.holdings_account_id IS NOT NULL AS is_brokerage
  FROM txn_legs l
  JOIN mig263_hdr hh ON hh.header_id = l.header_id
  JOIN accounts a ON a.id = l.account_id
 GROUP BY l.header_id, l.account_id, a.account_type, a.holdings_account_id;

CREATE TEMP TABLE mig263_cand (
    header_id  uuid NOT NULL,
    account_id uuid NOT NULL,
    PRIMARY KEY (header_id, account_id)
) ON COMMIT DROP;

-- Which case built E, and which step decided, for the counts.
CREATE TEMP TABLE mig263_case (
    header_id uuid PRIMARY KEY,
    e_case    text NOT NULL,
    decided   text
) ON COMMIT DROP;

ANALYZE mig263_hdr;
ANALYZE mig263_acct;

-- E, case by case. Each insert skips headers an earlier one gave candidates to.
INSERT INTO mig263_cand (header_id, account_id)
SELECT s.header_id, s.account_id
  FROM mig263_sources s
  JOIN mig263_hdr hh ON hh.header_id = s.header_id
ON CONFLICT DO NOTHING;
INSERT INTO mig263_case (header_id, e_case)
SELECT DISTINCT c.header_id, 'transfer_shares' FROM mig263_cand c;

INSERT INTO mig263_cand (header_id, account_id)
SELECT a.header_id, a.account_id
  FROM mig263_acct a
  JOIN mig263_hdr hh ON hh.header_id = a.header_id
 WHERE hh.action IS NOT NULL AND hh.action <> 'transfer_shares'
   AND a.is_brokerage AND a.postings_on = hh.postings;
INSERT INTO mig263_case (header_id, e_case)
SELECT DISTINCT c.header_id, 'actioned' FROM mig263_cand c
ON CONFLICT DO NOTHING;

INSERT INTO mig263_cand (header_id, account_id)
SELECT a.header_id, a.account_id
  FROM mig263_acct a
  JOIN mig263_hdr hh ON hh.header_id = a.header_id
 WHERE hh.action IS NULL AND hh.postings > 1
   AND a.postings_on = hh.postings;
INSERT INTO mig263_case (header_id, e_case)
SELECT DISTINCT c.header_id, 'multi_split' FROM mig263_cand c
ON CONFLICT DO NOTHING;

INSERT INTO mig263_cand (header_id, account_id)
SELECT a.header_id, a.account_id
  FROM mig263_acct a
  JOIN mig263_hdr hh ON hh.header_id = a.header_id
 WHERE hh.action IS NULL AND hh.postings = 1;
INSERT INTO mig263_case (header_id, e_case)
SELECT DISTINCT c.header_id, 'one_posting' FROM mig263_cand c
ON CONFLICT DO NOTHING;

-- No candidate from its shape: every account on its legs. No current writer
-- produces this; for an actioned header assertion (b) or (c) then fails.
INSERT INTO mig263_cand (header_id, account_id)
SELECT a.header_id, a.account_id
  FROM mig263_acct a
 WHERE NOT EXISTS (SELECT 1 FROM mig263_case k WHERE k.header_id = a.header_id);
INSERT INTO mig263_case (header_id, e_case)
SELECT DISTINCT c.header_id, 'empty' FROM mig263_cand c
ON CONFLICT DO NOTHING;

ANALYZE mig263_cand;
ANALYZE mig263_case;

-- The candidates one chain step keeps.
CREATE TEMP TABLE mig263_keep (header_id uuid NOT NULL, account_id uuid NOT NULL) ON COMMIT DROP;

-- The chain, over the template headers or the others. Each step names the
-- candidates it keeps; a header with one candidate left is decided by that step.
CREATE OR REPLACE FUNCTION mig263_narrow(p_templates boolean) RETURNS void
LANGUAGE plpgsql AS $fn$
DECLARE
    step text;
BEGIN
    UPDATE mig263_case k SET decided = 'shape'
      FROM mig263_hdr hh
     WHERE hh.header_id = k.header_id AND hh.is_recurring_template = p_templates
       AND (SELECT count(*) FROM mig263_cand c WHERE c.header_id = k.header_id) = 1;

    FOREACH step IN ARRAY ARRAY['md_home', 'reminder_source', 'real_over_category', 'outflow', 'lower_id']
    LOOP
        DELETE FROM mig263_keep;

        IF step = 'md_home' THEN
            INSERT INTO mig263_keep
            SELECT c.header_id, c.account_id
              FROM mig263_cand c
              JOIN mig263_hdr hh ON hh.header_id = c.header_id
              JOIN accounts a ON a.id = c.account_id
             WHERE hh.import_source = 'moneydance_export'
               AND NOT hh.is_recurring_template
               AND mig263_md_text(a.provider_raw_payload, 'id') = hh.md_acctid
            UNION ALL
            SELECT c.header_id, c.account_id
              FROM mig263_cand c
              JOIN mig263_hdr hh ON hh.header_id = c.header_id
              JOIN recurring_transactions rt
                ON rt.template_header_id = hh.header_id AND rt.ledger_id = hh.ledger_id
              JOIN accounts a ON a.id = c.account_id
             WHERE hh.import_source = 'moneydance_export'
               AND hh.is_recurring_template
               AND rt.origin = 'moneydance_import'
               AND rt.source_payload IS NOT NULL
               AND hh.external_id = 'mdreminder:' || rt.external_id
               AND mig263_md_text(a.provider_raw_payload, 'id')
                   = mig263_md_text(rt.source_payload, 'txn.acctid');
        ELSIF step = 'reminder_source' THEN
            INSERT INTO mig263_keep
            SELECT c.header_id, c.account_id
              FROM mig263_cand c
              JOIN mig263_hdr hh ON hh.header_id = c.header_id
              JOIN recurring_transactions rt
                ON rt.template_header_id = hh.header_id AND rt.ledger_id = hh.ledger_id
             WHERE hh.is_recurring_template
               AND rt.source_account_id = c.account_id
            UNION ALL
            SELECT c.header_id, c.account_id
              FROM mig263_cand c
              JOIN mig263_hdr hh ON hh.header_id = c.header_id
              JOIN recurring_transactions rt
                ON rt.id = hh.recurring_transaction_id AND rt.ledger_id = hh.ledger_id
              LEFT JOIN txn_headers t ON t.id = rt.template_header_id
             WHERE NOT hh.is_recurring_template
               AND COALESCE(rt.source_account_id, t.originating_account_id) = c.account_id;
        ELSIF step = 'real_over_category' THEN
            INSERT INTO mig263_keep
            SELECT c.header_id, c.account_id
              FROM mig263_cand c
              JOIN mig263_acct a ON a.header_id = c.header_id AND a.account_id = c.account_id
             WHERE NOT a.is_category;
        ELSIF step = 'outflow' THEN
            INSERT INTO mig263_keep
            SELECT c.header_id, c.account_id
              FROM mig263_cand c
              JOIN mig263_acct a ON a.header_id = c.header_id AND a.account_id = c.account_id
             WHERE a.net < 0;
        ELSE
            INSERT INTO mig263_keep
            SELECT DISTINCT ON (c.header_id) c.header_id, c.account_id
              FROM mig263_cand c
             ORDER BY c.header_id, c.account_id;
        END IF;

        ANALYZE mig263_keep;

        -- Narrow only undecided headers of this pass, and only where the step keeps
        -- at least one of their candidates.
        DELETE FROM mig263_cand c
         USING mig263_case k, mig263_hdr hh
         WHERE k.header_id = c.header_id
           AND hh.header_id = c.header_id
           AND k.decided IS NULL
           AND hh.is_recurring_template = p_templates
           AND EXISTS (SELECT 1 FROM mig263_keep x WHERE x.header_id = c.header_id)
           AND NOT EXISTS (SELECT 1 FROM mig263_keep x
                            WHERE x.header_id = c.header_id AND x.account_id = c.account_id);

        UPDATE mig263_case k SET decided = step
          FROM mig263_hdr hh
         WHERE hh.header_id = k.header_id
           AND k.decided IS NULL
           AND hh.is_recurring_template = p_templates
           AND (SELECT count(*) FROM mig263_cand c WHERE c.header_id = k.header_id) = 1;
    END LOOP;

    UPDATE txn_headers h
       SET originating_account_id = c.account_id
      FROM mig263_cand c
      JOIN mig263_hdr hh ON hh.header_id = c.header_id
     WHERE h.id = c.header_id
       AND hh.is_recurring_template = p_templates
       AND h.originating_account_id IS NULL;
END;
$fn$;

-- Templates first: an occurrence whose series has no source reads its template's.
SELECT mig263_narrow(TRUE);
SELECT mig263_narrow(FALSE);

DO $counts$
DECLARE
    r record;
BEGIN
    RAISE NOTICE '263: % header(s) without legs keep NULL',
        (SELECT count(*) FROM mig263_hdr WHERE postings = 0);
    FOR r IN SELECT e_case, count(*) AS n FROM mig263_case GROUP BY e_case ORDER BY e_case LOOP
        RAISE NOTICE '263: candidate set %: % header(s)', r.e_case, r.n;
    END LOOP;
    FOR r IN SELECT decided, count(*) AS n FROM mig263_case GROUP BY decided ORDER BY decided LOOP
        RAISE NOTICE '263: decided by %: % header(s)', r.decided, r.n;
    END LOOP;
END $counts$;

-- ---------------------------------------------------------------------------
-- 7) The reference and its index
--
-- Composite, so the account is always in the header's own ledger (the 049/121
-- pattern). RESTRICT: the originating account carries a leg of its header, and legs
-- already refuse their account's delete; a writer that removes an account's last
-- leg hands ownership over first, and one that forgets fails here.
-- ---------------------------------------------------------------------------
DO $fk$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                    WHERE conname = 'txn_headers_originating_account_ledger_fkey'
                      AND conrelid = 'public.txn_headers'::regclass) THEN
        ALTER TABLE txn_headers
            ADD CONSTRAINT txn_headers_originating_account_ledger_fkey
            FOREIGN KEY (originating_account_id, ledger_id)
            REFERENCES accounts (id, ledger_id) ON DELETE RESTRICT;
    END IF;
END $fk$;

CREATE INDEX IF NOT EXISTS idx_txn_headers_originating_account
    ON txn_headers (originating_account_id, ledger_id);

-- ---------------------------------------------------------------------------
-- 8) The invariants, over every header — the ones filled above and any a writer
--    filled before a re-run. A violation fails the upgrade.
--
--    (a) the value is the account of a leg of its header, and NULL exactly when the
--        header has no legs;
--    (b) every actioned event other than transfer_shares: a brokerage carrying a
--        leg in every posting;
--    (c) every transfer_shares event: its source brokerage;
--    (d) every action-less header of several postings on which some account
--        carries a leg in every posting: one of those accounts.
--
--    One pass, set-based: each fact a clause reads is aggregated once over the
--    whole table and joined to its header by key, and each clause counts the
--    headers whose joined row breaks it. It runs over every header of every
--    ledger, so a clause written as a subquery per header would cost the
--    ledger's size once per header; this is the shape the Moneydance import
--    check runs per ledger.
-- ---------------------------------------------------------------------------
DO $assert$
DECLARE
    v_a bigint;
    v_b bigint;
    v_c bigint;
    v_d bigint;
BEGIN
    WITH per_account AS (
        -- Each account on a header's legs, and how many postings it has a leg in.
        SELECT l.header_id, l.account_id, count(DISTINCT l.posting_index) AS postings_on
          FROM txn_legs l
         GROUP BY l.header_id, l.account_id
    ), shape AS (
        SELECT l.header_id, count(DISTINCT l.posting_index) AS postings
          FROM txn_legs l
         GROUP BY l.header_id
    ), widest AS (
        -- The posting count exactly when some account has a leg in every posting.
        SELECT p.header_id, max(p.postings_on) AS most_on
          FROM per_account p
         GROUP BY p.header_id
    ), sources AS (
        SELECT DISTINCT s.header_id, s.account_id FROM mig263_sources s
    ), facts AS (
        SELECT h.action,
               h.originating_account_id IS NOT NULL         AS has_owner,
               s.header_id IS NOT NULL                      AS has_legs,
               COALESCE(s.postings, 0)                      AS postings,
               COALESCE(w.most_on = s.postings, FALSE)      AS some_on_every,
               o.header_id IS NOT NULL                      AS owner_on_leg,
               COALESCE(o.postings_on = s.postings, FALSE)  AS owner_on_every,
               a.holdings_account_id IS NOT NULL            AS owner_is_brokerage,
               x.header_id IS NOT NULL                      AS owner_is_source
          FROM txn_headers h
          LEFT JOIN shape s       ON s.header_id = h.id
          LEFT JOIN widest w      ON w.header_id = h.id
          LEFT JOIN per_account o ON o.header_id = h.id AND o.account_id = h.originating_account_id
          LEFT JOIN accounts a    ON a.id = h.originating_account_id
          LEFT JOIN sources x     ON x.header_id = h.id AND x.account_id = h.originating_account_id
    )
    SELECT count(*) FILTER (WHERE (NOT f.has_owner AND f.has_legs)
                               OR (f.has_owner AND NOT f.owner_on_leg)),
           count(*) FILTER (WHERE f.action IS NOT NULL AND f.action <> 'transfer_shares' AND f.has_legs
                              AND NOT (f.owner_on_every AND f.owner_is_brokerage)),
           count(*) FILTER (WHERE f.action IS NOT NULL AND f.action = 'transfer_shares' AND f.has_legs
                              AND NOT f.owner_is_source),
           count(*) FILTER (WHERE f.action IS NULL AND f.postings > 1 AND f.some_on_every
                              AND NOT f.owner_on_every)
      INTO v_a, v_b, v_c, v_d
      FROM facts f;

    IF v_a + v_b + v_c + v_d > 0 THEN
        RAISE EXCEPTION
            'Migration 263 found headers whose originating account breaks an invariant: '
            '(a) not on a leg, or NULL with legs: %; (b) actioned event not owned by a brokerage '
            'on every posting: %; (c) transfer_shares not owned by its source brokerage: %; '
            '(d) multi-split not owned by an account on every line: %. Nothing was changed; '
            'see docs/upgrading.md (0.128.0).',
            v_a, v_b, v_c, v_d;
    END IF;
END $assert$;

DROP FUNCTION mig263_narrow(boolean);
DROP FUNCTION mig263_md_text(jsonb, text);

COMMIT;
