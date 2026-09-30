-- ---------------------------------------------------------------------------
-- 236 — delete the trade prices that were never prices.
--
-- Migration 235 stopped a share movement from PRICING a day. It could not undo
-- what had already been written: the rows were already in security_prices, and
-- once 235 excludes the legs behind them nothing re-derives them, so they stop
-- being reported by the consistency check as well. Left alone they would be
-- invisible AND wrong — valuing holdings at about a dollar, forever, with
-- nothing in the product able to see it.
--
-- WHAT THESE ARE. Quicken wrote share movements into Moneydance with the
-- transaction amount set to the SHARE COUNT rather than a value (an MD record
-- carries `1.pamt = 50631` — $506.31 — against `1.samt = -5063090`, i.e.
-- 506.309 shares). The importer derives unit_price = amount / quantity and
-- produced ~$1.00, which then seeded a `trade` price. One real example: a fund
-- priced $257.03 five weeks earlier has a stored price of $1.0000.
--
-- THE SET IS CLOSED. Measured across the whole ledger: every nominal-priced leg
-- on a security that trades above $5 is a ShrsIn or ShrsOut, every one of them
-- carries a `qif_sn` marker, and they run 1995–2014. The 380 other legs with
-- the same arithmetic — including all 327 dated after 2012 — are money-market
-- and stable-value funds genuinely worth a dollar. This is a finished historical
-- artifact of one Quicken migration; it cannot grow, which is why a one-time
-- delete is the proportionate fix rather than a new reason code and repair verb
-- for a set of 26.
--
-- THE PREDICATE, and why each clause is load-bearing:
--
--   source = 'trade'      — only rows this projection wrote. A `manual` or
--                           `fetch` dollar price is someone's own figure or a
--                           real quote and is never in scope.
--   price 0.90 .. 1.10    — the nominal band. Rounding a share count to cents
--                           puts a tiny position slightly off 1.00 (0.02 / 0.021
--                           = 0.952), so an exact test would miss them.
--   fn_trade_price_for_day IS NULL
--                         — after 235, NOTHING legitimately prices this day.
--                           If any real leg still does, the row is either right
--                           or is ordinary drift the consistency check reports
--                           and repairs; deleting it there would be destroying
--                           evidence rather than cleaning up.
--   the security trades above $5 somewhere in its own history
--                         — the guard that keeps money-market funds out. It is
--                           not theoretical: on the dev dataset it protects one
--                           row that every other clause matches.
--
-- Safe to delete rather than correct: security_prices `trade` rows are DERIVED.
-- Removing one makes valuation fall back to the nearest real price or report
-- the position stale, which is the honest answer where no price is known — and
-- is strictly better than asserting a dollar.
-- ---------------------------------------------------------------------------

DELETE FROM security_prices p
WHERE p.source = 'trade'
  AND p.price BETWEEN 0.9 AND 1.1
  AND fn_trade_price_for_day(p.ledger_id, p.security_id, p.price_date) IS NULL
  AND EXISTS (
        SELECT 1 FROM security_prices q
        WHERE q.security_id = p.security_id
          AND q.price > 5);
