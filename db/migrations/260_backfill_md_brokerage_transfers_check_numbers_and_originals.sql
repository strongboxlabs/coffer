-- =============================================================================
-- 260 — bring Moneydance-imported ledgers up to what the importer now writes
-- =============================================================================
--
-- Three importer fixes land in the same change as this migration:
--
--   1. A one-posting cash move in or out of a brokerage is a 'transfer'. QIF-origin
--      brokerage cash rows (qif_invst_action XOut / XIn / Cash / ContribX /
--      MiscIncX with xfer_type xfrtp_bank, no invest.txntype) and plain brokerage
--      rows with no xfer_type fail MdTxn.IsInvestmentShape, so they take the BANK
--      route, which wrote action NULL and posting_role NULL. ADR-0027 already
--      names those QIF words 'transfer', and the investment route writes exactly
--      that for txntype "bank". Left action-less, a brokerage row whose other side
--      is a CATEGORY derives no action in the register (resolved_transactions:
--      'Xfr' only between two real accounts, migration 230) and the investment
--      editor opens on "Pick action...".
--   2. The investment route kept MD's `chk` nowhere — on a QIF-origin row it is
--      the QIF action word ("Buy", "ReinvLg"), the only place the distinction
--      between the reinvest variants survives.
--   3. FU-024: where MD's feed text (ol.orig-payee / ol.orig-memo) differs from
--      the curated desc / memo written on the header, the feed value belongs in
--      txn_header_originals — the state the API's first edit leaves behind — where
--      payee recall reads it and resolved_transactions.has_overrides reports it.
--
-- MD import is a one-shot bootstrap of a NEW ledger with no re-import onto an
-- existing one, so the importer change fixes future ledgers only. Migration 109 /
-- 110 (ADR-0035 §3) keep the verbatim MD JSON on provider_raw_payload precisely so
-- a later fix can be replayed in SQL without the source file; this is that replay.
-- The goal is EXACTLY what the new importer writes for the same MD data, and what
-- the API then does with it, so every predicate below mirrors the C# rule it names.
--
-- SCOPE. MD-imported rows are import_source = 'moneydance_export' (the importer's
-- MoneydanceImportService.ImportSource). Live rows carry the txn's JSON on
-- txn_headers.provider_raw_payload. Reminder TEMPLATE headers carry none, but their
-- series row does (recurring_transactions.source_payload, the reminder's JSON with
-- the embedded txn under "txn.*"), and ReminderMapper applies rule 1 too — so
-- templates are covered through it. Occurrences already FIRED from a template are
-- clones of it (RemindersRepository.FireAsync), so they follow their template.
--
-- NOT HERE, DELIBERATELY: the importer now lands MD's blank-named top-level leaf
-- categories on the system Uncategorized. That is for fresh imports only; existing
-- ledgers keep their "(unnamed)" category.
--
-- NEVER OVERWRITES THE USER. Each part writes only where the column still holds
-- what the OLD importer wrote: action IS NULL, check_number IS NULL, no originals
-- row. A value anyone has set since is left alone, and a re-run finds nothing left
-- to do. The one place an existing value moves is an originals row a first edit
-- captured after the old import: it holds MD's curated text and a dropped check
-- number, values the API snapshotted, not ones anyone chose (part 3).
--
-- DETERMINISTIC. Every value is read off stored data. The one column the importer
-- fills from the clock, txn_header_originals.captured_at, takes the header's own
-- created_at — the moment the importer wrote the row the sidecar describes —
-- never now(), so a restore drill replays it byte-identically.
-- -----------------------------------------------------------------------------

BEGIN;

-- Helpers, dropped at the end: scaffolding for the statements below, not schema.
--
-- Moneydance writes every field as a JSON string, and the importer reads a field
-- only when it IS one (MdItem.GetString returns null otherwise). `->>` would
-- instead turn a stray number into text — "3" as a txntype, "5" as a check
-- number — so every read goes through this. jsonb's extraction operators return
-- NULL rather than raising on a payload of the wrong shape, so nothing here can
-- abort the migration on one odd row.
CREATE OR REPLACE FUNCTION mig260_md_text(payload jsonb, key text) RETURNS text
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT CASE WHEN jsonb_typeof(payload -> key) = 'string' THEN payload ->> key END;
$fn$;

-- The importer's NullIfEmpty: string.IsNullOrWhiteSpace → NULL, anything else
-- verbatim (no trim). The class is .NET's char.IsWhiteSpace set, spelled out as
-- escapes, because [[:space:]] follows the server's locale and would not match it.
CREATE OR REPLACE FUNCTION mig260_blank_to_null(value text) RETURNS text
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT CASE
             WHEN value ~ '^[\u0009-\u000D \u0085   -     　]*$'
             THEN NULL
             ELSE value
           END;
$fn$;

-- MdTxn.IsInvestmentShape: a non-empty <prefix>invest.txntype, or one of the
-- five investment <prefix>xfer_types. Its complement is the bank route. The
-- prefix is '' for a live txn and 'txn.' for a reminder's embedded one
-- (MdReminderTxn.IsInvestmentShape, the same test).
CREATE OR REPLACE FUNCTION mig260_md_is_investment_shape(payload jsonb, prefix text) RETURNS boolean
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT COALESCE(mig260_md_text(payload, prefix || 'invest.txntype'), '') <> ''
        OR COALESCE(mig260_md_text(payload, prefix || 'xfer_type'), '') IN
           ('xfrtp_buysell', 'xfrtp_buysellxfr', 'xfrtp_dividend',
            'xfrtp_dividendxfr', 'xfrtp_miscincexp');
$fn$;

-- The postings the mapper emitted: MdTxn.ExtractSplits walks <prefix>N.acctid
-- from 0, at most 32, stopping at the first gap; the self-reference filter then
-- drops splits on the home account (<prefix>acctid).
--
-- This compares MD ids; the importer compares the Coffer ids they resolve to. The
-- two agree only while every MD account has a Coffer account of its own, which
-- held for every ledger imported before blank categories shared one account (the
-- system Uncategorized, this change) — so for every ledger 260 can meet.
CREATE OR REPLACE FUNCTION mig260_md_emittable_splits(payload jsonb, prefix text) RETURNS integer
LANGUAGE plpgsql IMMUTABLE AS $fn$
DECLARE
    home   text := mig260_md_text(payload, prefix || 'acctid');
    target text;
    n      integer := 0;
BEGIN
    FOR i IN 0..31 LOOP
        target := mig260_md_text(payload, prefix || i || '.acctid');
        EXIT WHEN target IS NULL;
        IF target IS DISTINCT FROM home THEN
            n := n + 1;
        END IF;
    END LOOP;
    RETURN n;
END;
$fn$;

-- ---------------------------------------------------------------------------
-- 1a. Brokerage cash moves on the bank route → 'transfer'
--     (BrokerageCashTransfer.Classify: home account is a brokerage AND exactly
--     one emittable posting; for a template, ReminderMapper also requires the
--     embedded txn not to be investment-shaped).
--
--     Two shape checks, both fail-closed. The PAYLOAD must say one emittable
--     posting — what the importer counted. The LEGS must still be that one
--     posting: two legs sharing one posting_index, no security, one of them on
--     the brokerage the payload names as home, no role other than 'transfer'. A
--     row the user has since reshaped in either direction is no longer the row
--     the rule classified, so it is left as it is.
--
--     Header and legs change in ONE statement, so no interruption can leave an
--     action without its roles.
-- ---------------------------------------------------------------------------
WITH sources AS (
    -- Live rows: the txn JSON, bank-routed.
    SELECT h.id AS header_id, h.provider_raw_payload AS payload, ''::text AS prefix
      FROM txn_headers h
     WHERE h.import_source = 'moneydance_export'
       AND h.provider_raw_payload IS NOT NULL
       AND NOT h.is_recurring_template
       AND h.action IS NULL
       AND NOT mig260_md_is_investment_shape(h.provider_raw_payload, '')
    UNION ALL
    -- Reminder templates: the reminder JSON, txn under "txn.*". ReminderMapper
    -- gates the rule on the embedded txn not being investment-shaped, as the
    -- bank route is only ever handed such txns.
    SELECT h.id, rt.source_payload, 'txn.'::text
      FROM txn_headers h
      JOIN recurring_transactions rt
        ON rt.template_header_id = h.id AND rt.ledger_id = h.ledger_id
     WHERE h.import_source = 'moneydance_export'
       AND h.is_recurring_template
       AND h.action IS NULL
       AND rt.origin = 'moneydance_import'
       AND rt.source_payload IS NOT NULL
       AND h.external_id = 'mdreminder:' || rt.external_id
       AND NOT mig260_md_is_investment_shape(rt.source_payload, 'txn.')
),
candidates AS (
    SELECT s.header_id, mig260_md_text(s.payload, s.prefix || 'acctid') AS home
      FROM sources s
     WHERE mig260_md_emittable_splits(s.payload, s.prefix) = 1
),
one_posting AS (
    SELECT c.header_id
      FROM candidates c
      JOIN txn_legs l ON l.header_id = c.header_id
      JOIN accounts a ON a.id = l.account_id
     GROUP BY c.header_id, c.home
    HAVING count(*) = 2
       AND count(DISTINCT l.posting_index) = 1
       AND bool_and(l.security_id IS NULL)
       AND bool_and(l.posting_role IS NULL OR l.posting_role = 'transfer')
       AND count(*) FILTER (
             WHERE a.account_type = 'investment'
               AND mig260_md_text(a.provider_raw_payload, 'id') = c.home) = 1
),
stamped AS (
    UPDATE txn_headers h
       SET action = 'transfer'
      FROM one_posting p
     WHERE h.id = p.header_id
       AND h.action IS NULL
    RETURNING h.id
)
UPDATE txn_legs l
   SET posting_role = 'transfer'
  FROM stamped s
 WHERE l.header_id = s.id
   AND l.posting_role IS NULL;

-- ---------------------------------------------------------------------------
-- 1b. Occurrences already fired from those templates → 'transfer'
--
--     FireAsync clones the template's header and legs, action and roles
--     included, into a live header carrying recurring_transaction_id. One fired
--     before the upgrade cloned an action-less template, so it is action-less
--     too. Its series qualifies when the template now holds 'transfer' (1a just
--     stamped it, or the importer did) AND its payload meets 1a's template rule,
--     so a template a user set to 'transfer' some other way pulls nothing along.
--     The occurrence must still have 1a's leg shape, its brokerage leg on the
--     template's home account (the payload's txn.acctid, as in 1a). Read AFTER
--     1a, as a statement of its own.
-- ---------------------------------------------------------------------------
WITH series AS (
    SELECT rt.id AS series_id, mig260_md_text(rt.source_payload, 'txn.acctid') AS home
      FROM recurring_transactions rt
      JOIN txn_headers t
        ON t.id = rt.template_header_id AND t.ledger_id = rt.ledger_id
     WHERE rt.origin = 'moneydance_import'
       AND rt.source_payload IS NOT NULL
       AND t.import_source = 'moneydance_export'
       AND t.is_recurring_template
       AND t.external_id = 'mdreminder:' || rt.external_id
       AND t.action = 'transfer'
       AND NOT mig260_md_is_investment_shape(rt.source_payload, 'txn.')
       AND mig260_md_emittable_splits(rt.source_payload, 'txn.') = 1
),
one_posting AS (
    SELECT h.id AS header_id
      FROM series s
      JOIN txn_headers h ON h.recurring_transaction_id = s.series_id
      JOIN txn_legs l ON l.header_id = h.id
      JOIN accounts a ON a.id = l.account_id
     WHERE NOT h.is_recurring_template
       AND h.action IS NULL
     GROUP BY h.id, s.home
    HAVING count(*) = 2
       AND count(DISTINCT l.posting_index) = 1
       AND bool_and(l.security_id IS NULL)
       AND bool_and(l.posting_role IS NULL OR l.posting_role = 'transfer')
       AND count(*) FILTER (
             WHERE a.account_type = 'investment'
               AND mig260_md_text(a.provider_raw_payload, 'id') = s.home) = 1
),
stamped AS (
    UPDATE txn_headers h
       SET action = 'transfer'
      FROM one_posting p
     WHERE h.id = p.header_id
       AND h.action IS NULL
    RETURNING h.id
)
UPDATE txn_legs l
   SET posting_role = 'transfer'
  FROM stamped s
 WHERE l.header_id = s.id
   AND l.posting_role IS NULL;

-- ---------------------------------------------------------------------------
-- 2a. The investment route's check number (InvestmentTransactionMapper's
--     MapCtx.CheckNumber: chk, blank → NULL, otherwise verbatim).
-- ---------------------------------------------------------------------------
UPDATE txn_headers h
   SET check_number = mig260_blank_to_null(mig260_md_text(h.provider_raw_payload, 'chk'))
 WHERE h.import_source = 'moneydance_export'
   AND h.provider_raw_payload IS NOT NULL
   AND NOT h.is_recurring_template
   AND h.check_number IS NULL
   AND mig260_md_is_investment_shape(h.provider_raw_payload, '')
   AND mig260_blank_to_null(mig260_md_text(h.provider_raw_payload, 'chk')) IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 2b. The same check number on an originals row a first edit captured after the
--     old import. The capture snapshotted the row as that importer left it —
--     check_number NULL — and the feed's value was chk all along, so that NULL is
--     the importer's drop, not a user's choice. It is corrected even where the
--     header's check number has since been set: that one IS the user's (2a left
--     it), the sidecar's is not.
-- ---------------------------------------------------------------------------
UPDATE txn_header_originals o
   SET check_number = mig260_blank_to_null(mig260_md_text(h.provider_raw_payload, 'chk'))
  FROM txn_headers h
 WHERE o.header_id = h.id
   AND h.import_source = 'moneydance_export'
   AND h.provider_raw_payload IS NOT NULL
   AND NOT h.is_recurring_template
   AND o.check_number IS NULL
   AND mig260_md_is_investment_shape(h.provider_raw_payload, '')
   AND mig260_blank_to_null(mig260_md_text(h.provider_raw_payload, 'chk')) IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 3a. FU-024 on an originals row a first edit captured after the old import.
--
--     The old importer never wrote the feed text, so that capture holds MD's
--     CURATED payee and memo. Under the new importer the sidecar holds the
--     feed's from the import on, and CaptureAsync's once-per-header rule keeps it
--     through any later edit. So a side whose non-blank feed value differs from
--     the capture takes the feed value; a side with no feed value keeps the
--     capture, as the importer's seed would have kept the header's.
-- ---------------------------------------------------------------------------
UPDATE txn_header_originals o
   SET payee = COALESCE(f.payee, o.payee),
       memo  = COALESCE(f.memo,  o.memo)
  FROM txn_headers h
 CROSS JOIN LATERAL (
       SELECT mig260_blank_to_null(mig260_md_text(h.provider_raw_payload, 'ol.orig-payee')) AS payee,
              mig260_blank_to_null(mig260_md_text(h.provider_raw_payload, 'ol.orig-memo'))  AS memo
       ) f
 WHERE o.header_id = h.id
   AND h.import_source = 'moneydance_export'
   AND h.provider_raw_payload IS NOT NULL
   AND NOT h.is_recurring_template
   AND (   (f.payee IS NOT NULL AND f.payee IS DISTINCT FROM o.payee)
        OR (f.memo  IS NOT NULL AND f.memo  IS DISTINCT FROM o.memo));

-- ---------------------------------------------------------------------------
-- 3b. FU-024 originals seed (FeedOriginals.SeedFor + the repository's insert).
--
--     Seeded only where a non-blank feed value differs, ordinally, from the value
--     written; a side with no feed value carries the written one. The payee
--     written is the header's. The memo written is the transaction's wherever it
--     sits: the header's, else (one posting) the leg on the Moneydance home
--     account, where a one-split or a one-posting event keeps its own value.
--     Memos are stored trimmed, so the memo comparison is over trimmed text on
--     both sides, and a seed that carries the written memo carries it trimmed;
--     the seed keeps the feed value as Moneydance holds it. Reading
--     the header first gives the same answer on a ledger the old importer wrote
--     (its memo sat on the header) as on one written since. posted_at,
--     transacted_at and check_number are the header's own, read AFTER part 2 —
--     the whole-row snapshot the API's first edit takes. A header that already
--     has an originals row was edited; 3a brought its capture in line.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION mig260_trim_to_null(value text) RETURNS text
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT NULLIF(regexp_replace(
               value,
               '^[\u0009-\u000D \u0085   -     　]+'
               || '|[\u0009-\u000D \u0085   -     　]+$',
               '', 'g'), '');
$fn$;

INSERT INTO txn_header_originals
    (header_id, ledger_id, payee, memo, posted_at, transacted_at, check_number, captured_at)
SELECT h.id, h.ledger_id,
       COALESCE(f.payee, h.payee), COALESCE(f.memo, mig260_trim_to_null(w.memo)),
       h.posted_at, h.transacted_at, h.check_number,
       h.created_at
  FROM txn_headers h
 CROSS JOIN LATERAL (
       SELECT mig260_blank_to_null(mig260_md_text(h.provider_raw_payload, 'ol.orig-payee')) AS payee,
              mig260_blank_to_null(mig260_md_text(h.provider_raw_payload, 'ol.orig-memo'))  AS memo
       ) f
 CROSS JOIN LATERAL (
       SELECT CASE
                WHEN h.memo IS NOT NULL THEN h.memo
                WHEN (SELECT count(DISTINCT l.posting_index) FROM txn_legs l WHERE l.header_id = h.id) = 1
                THEN (SELECT l.leg_memo
                        FROM txn_legs l
                        JOIN accounts a ON a.id = l.account_id
                       WHERE l.header_id = h.id
                         AND mig260_md_text(a.provider_raw_payload, 'id')
                             = mig260_md_text(h.provider_raw_payload, 'acctid')
                       ORDER BY l.posting_index
                       LIMIT 1)
              END AS memo
       ) w
 WHERE h.import_source = 'moneydance_export'
   AND h.provider_raw_payload IS NOT NULL
   AND NOT h.is_recurring_template
   AND NOT EXISTS (SELECT 1 FROM txn_header_originals o WHERE o.header_id = h.id)
   AND (   (f.payee IS NOT NULL AND f.payee IS DISTINCT FROM h.payee)
        OR (f.memo  IS NOT NULL
            AND mig260_trim_to_null(f.memo) IS DISTINCT FROM mig260_trim_to_null(w.memo)))
ON CONFLICT (header_id) DO NOTHING;

DROP FUNCTION mig260_trim_to_null(text);
DROP FUNCTION mig260_md_emittable_splits(jsonb, text);
DROP FUNCTION mig260_md_is_investment_shape(jsonb, text);
DROP FUNCTION mig260_blank_to_null(text);
DROP FUNCTION mig260_md_text(jsonb, text);

COMMIT;
