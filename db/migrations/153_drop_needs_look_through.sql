-- =============================================================================
-- 153 — drop securities.needs_look_through (ADR-0067 refinement)
-- =============================================================================
--
-- needs_look_through (mig 150) was a separate boolean driving allocation
-- decomposition. It's redundant with asset_class = 'multi_asset': a multi-asset
-- wrapper is exactly the thing that needs look-through. Make asset_class the
-- single signal — allocation decomposes any 'multi_asset' security that has
-- security_components sleeves; the look-through sleeve editor is gated on
-- asset_class = 'multi_asset' in the security editor.
--
-- Carry intent forward before dropping: any row flagged needs_look_through but
-- not yet classed multi_asset becomes multi_asset (no-op on a correctly-classed
-- ledger; the flag is brand-new in this unreleased branch so there's little/no
-- real data to carry).
-- =============================================================================

UPDATE securities
SET asset_class = 'multi_asset'
WHERE needs_look_through = TRUE
  AND asset_class IS DISTINCT FROM 'multi_asset';

ALTER TABLE securities DROP COLUMN needs_look_through;
