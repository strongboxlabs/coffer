-- ---------------------------------------------------------------------------
-- 236 — delete the trade prices that were never prices.
--
-- Migration 235 stopped a placeholder share movement from PRICING a day. It could not undo
-- what had already been written: the rows were already in security_prices, and
-- once 235 excludes the legs behind them nothing re-derives them, so they stop
-- being reported by the consistency check as well. Left alone they would be
-- invisible AND wrong — valuing holdings at a nominal price of one per share,
-- forever, with nothing in the product able to see it.
--
-- WHAT THESE ARE. A Moneydance share movement can carry the transaction amount
-- set to the SHARE COUNT rather than a value (an MD record
-- carries a `1.pamt` that, read as money, is its `1.samt` share count rounded
-- to cents). The importer derives unit_price = amount / quantity and so
-- produced a price of about one, which then seeded a `trade` price — on a fund
-- that may trade far above one, a stored price of par.
--
-- THE SET IS CLOSED. After 235 no placeholder share movement prices a day, so
-- nothing can write a new row of this kind; what is left is what was written
-- before it. A set that cannot grow is why a one-time delete is the
-- proportionate fix rather than a new reason code and repair verb. (235's test
-- does not match placeholders whose amount is truncated rather than rounded to
-- cents; migration 237 closes that by excluding every share movement, and
-- deletes the nominal prices they wrote on the same terms as this one.)
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
--                         — the guard that keeps money-market funds out. A
--                           fund genuinely priced at par, on a day no surviving
--                           leg prices, matches every other clause.
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
