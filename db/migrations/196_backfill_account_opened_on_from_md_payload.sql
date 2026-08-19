-- =============================================================================
-- 196 — backfill accounts.opened_on from the stored Moneydance payload
-- =============================================================================
--
-- accounts.opened_on has existed since migration 127 (ADR-0050) and the account
-- editor has always been able to set it, but the Moneydance importer never wrote
-- it. Every account bootstrapped from MD therefore has opened_on NULL, and the
-- schema doc claimed otherwise the whole time.
--
-- Teaching the importer to read the field (same change as this migration) fixes
-- FUTURE ledgers only. MD import is a one-shot bootstrap of a NEW ledger — there
-- is no re-import path onto an existing one — so an already-imported ledger would
-- keep its NULLs forever, and the returns engine would never get the Start Date
-- it wants for since-inception anchoring.
--
-- It does not have to. Migration 110 (ADR-0035 §3) persists the verbatim MD `acct`
-- JSON on accounts.provider_raw_payload precisely so later work can mine fields
-- the importer did not model at the time, in SQL, without the source file. The
-- creation stamp is already sitting in every imported row.
--
-- MD writes that stamp two ways and is inconsistent about which. Measured on a
-- real 781-account export:
--
--   date_created   yyyyMMdd int      64 accounts   (19 of 50 investment)
--   creation_date  epoch millis     181 accounts   (50 of 50 investment)
--
-- So prefer the int and fall back to the epoch value — taking either alone leaves
-- most accounts NULL.
--
-- WHY THE EPOCH VALUE CONVERTS AT UTC rather than a local zone: MD stamps it at
-- local NOON (16:00Z / 17:00Z for a US-Eastern file), the usual convention for
-- keeping a calendar day stable under conversion. On the 64 accounts carrying
-- BOTH fields the UTC date equals date_created in every case, and still does at
-- every offset from UTC-12 to UTC+2. No local timezone is needed, and assuming
-- one would be the riskier choice.
--
-- Idempotent and non-destructive: only rows where opened_on IS NULL are touched,
-- so a value the user has set in the editor is never overwritten and re-running
-- is a no-op. Categories are excluded — a CHECK forces their opening balance to
-- 0, so the as-of date of that balance means nothing.
--
-- Accounts created natively in Coffer have provider_raw_payload NULL and are
-- skipped; they have no MD stamp to recover.
-- -----------------------------------------------------------------------------

-- Parsing runs through a helper rather than inline CASE arms because BOTH reads
-- can raise, and a raise here aborts the whole migration — one account with a
-- typo'd stamp would block the deploy for everyone.
--
--   * to_date('20261301','YYYYMMDD') RAISES 22008 on a modern server. It does
--     NOT silently coerce, as older/looser configurations do; the round-trip
--     check below covers that other behaviour, and the exception block covers
--     this one. Verified both ways in the migration's tests.
--   * '…'::bigint raises on anything that overflows, so the digit guard is
--     length-bounded rather than open-ended.
--
-- Dropped at the end: this is scaffolding for one UPDATE, not new schema.
CREATE FUNCTION mig196_md_opened_on(payload jsonb) RETURNS date
LANGUAGE plpgsql IMMUTABLE AS $fn$
DECLARE
    raw text;
    parsed date;
    millis bigint;
BEGIN
    -- 1) yyyyMMdd — unambiguous, no conversion, so it wins when present.
    raw := payload->>'date_created';
    IF raw ~ '^\d{8}$' THEN
        BEGIN
            parsed := to_date(raw, 'YYYYMMDD');
            -- Rejects 20260230 → 2026-03-02 on servers that coerce instead of
            -- raising. A plausible-looking wrong date is worse than a NULL.
            IF to_char(parsed, 'YYYYMMDD') = raw THEN
                RETURN parsed;
            END IF;
        EXCEPTION WHEN others THEN
            NULL;   -- fall through to the epoch field
        END;
    END IF;

    -- 2) epoch millis → UTC date. 15 digits caps the value well inside bigint
    -- and to_timestamp's range while covering every realistic date.
    raw := payload->>'creation_date';
    IF raw ~ '^\d{1,15}$' THEN
        BEGIN
            millis := raw::bigint;
            IF millis > 0 THEN
                RETURN (to_timestamp(millis / 1000.0) AT TIME ZONE 'UTC')::date;
            END IF;
        EXCEPTION WHEN others THEN
            NULL;
        END;
    END IF;

    RETURN NULL;
END;
$fn$;

UPDATE accounts
   SET opened_on = mig196_md_opened_on(provider_raw_payload)
 WHERE opened_on IS NULL
   AND account_type <> 'category'
   AND provider_raw_payload IS NOT NULL
   AND mig196_md_opened_on(provider_raw_payload) IS NOT NULL;

DROP FUNCTION mig196_md_opened_on(jsonb);
