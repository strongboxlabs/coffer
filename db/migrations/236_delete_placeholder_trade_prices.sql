-- ---------------------------------------------------------------------------
-- 236 — delete the trade prices that were never prices.
--
-- Migration 235 stopped a share movement from PRICING a day. It could not undo
-- what had already been written: the rows were already in security_prices, and
-- once 235 excludes the legs behind them nothing re-derives them, so they stop
-- being reported by the consistency check as well. Left alone they would be
-- invisible AND wrong — valuing holdings at a nominal price of one per share,
-- forever, with nothing in the product able to see it.
--
-- WHAT THESE ARE. Quicken wrote share movements into Moneydance with the
-- transaction amount set to the SHARE COUNT rather than a value (an MD record
-- carries a `1.pamt` that, read as money, is its `1.samt` share count rounded
-- to cents). The importer derives unit_price = amount / quantity and so
-- produced a price of about one, which then seeded a `trade` price. Found on a
-- real ledger: a fund with a three-figure price five weeks earlier has a stored
-- price of exactly one.
--
-- THE SET IS CLOSED. Checked across a whole real ledger: every nominal-priced
-- leg on a security that trades well above the band is a ShrsIn or ShrsOut,
-- every one of them carries a `qif_sn` marker, and all of them fall inside one
-- span of years that ended long ago. Every other leg with the same arithmetic —
-- including every recent one — is a money-market or stable-value fund
-- genuinely priced at par. This is a finished historical artifact of one
-- Quicken migration; it cannot grow, which is why a one-time delete is the
-- proportionate fix rather than a new reason code and repair verb for a small,
-- closed set.
--
-- THE PREDICATE, and why each clause is load-bearing:
--
--   source = 'trade'      — only rows this projection wrote. A `manual` or
--                           `fetch` price at par is someone's own figure or a
--                           real quote and is never in scope.
--   price in the BETWEEN band below
--                         — the nominal band around one. Rounding a share count
--                           to cents moves a tiny position's ratio a few percent
--                           off one (a count with a third decimal, rounded to
--                           cents, then divided by the unrounded count), so an
--                           exact test would miss them.
--   fn_trade_price_for_day IS NULL
--                         — after 235, NOTHING legitimately prices this day.
--                           If any real leg still does, the row is either right
--                           or is ordinary drift the consistency check reports
--                           and repairs; deleting it there would be destroying
--                           evidence rather than cleaning up.
--   the security trades well above the band somewhere in its own history
--   (the EXISTS clause below)
--                         — the guard that keeps money-market funds out. It is
--                           not theoretical: found on a real ledger, it protects
--                           a row that every other clause matches.
--
-- Safe to delete rather than correct: security_prices `trade` rows are DERIVED.
-- Removing one makes valuation fall back to the nearest real price or report
-- the position stale, which is the honest answer where no price is known — and
-- is strictly better than asserting par.
-- ---------------------------------------------------------------------------

DELETE FROM security_prices p
WHERE p.source = 'trade'
  AND p.price BETWEEN 0.9 AND 1.1
  AND fn_trade_price_for_day(p.ledger_id, p.security_id, p.price_date) IS NULL
  AND EXISTS (
        SELECT 1 FROM security_prices q
        WHERE q.security_id = p.security_id
          AND q.price > 5);
