-- Phase 1: minimal seed data.
-- A single merge_rules row with the defaults from §5.3 of the architecture doc.
-- Tune auto_merge_threshold downward if false-positive duplicates appear after import.

INSERT INTO merge_rules (date_window_days, amount_tolerance, payee_similarity_min, auto_merge_threshold, auto_reject_threshold)
SELECT 3, 0.0000, 0.4, 0.95, 0.2
WHERE NOT EXISTS (SELECT 1 FROM merge_rules);
