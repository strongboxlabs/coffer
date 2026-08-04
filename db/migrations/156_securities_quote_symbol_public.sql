-- 156_securities_quote_symbol_public.sql
-- ADR-0054 D2. A ticker is always a public market symbol, but the quote-symbol
-- OVERRIDE may be a private / feed-internal identifier — e.g. a 529 plan's MD
-- portfolio number ("8918"). Such a symbol must be matchable by the no-egress
-- SimpleFIN holdings provider (which recovers it from the feed payload) but must
-- NEVER be sent to an external provider (Yahoo), which would 404 or, worse,
-- mis-resolve the bare number to a foreign listing and overwrite the feed price.
--
-- quote_symbol_public marks whether the quote symbol is a public ticker (default
-- true = today's behavior). The CHECK codifies the model: something can only be
-- marked NOT public when there IS a quote symbol — a bare ticker is always public.
BEGIN;

ALTER TABLE securities
    ADD COLUMN quote_symbol_public boolean NOT NULL DEFAULT true;

ALTER TABLE securities
    ADD CONSTRAINT ck_securities_quote_symbol_public
    CHECK (quote_symbol_public OR quote_symbol IS NOT NULL);

COMMIT;
