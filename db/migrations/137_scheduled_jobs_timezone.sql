-- =============================================================================
-- 137 — scheduled_jobs.timezone: interpret the daily time in the user's zone
-- =============================================================================
--
-- hour_local/minute_local were interpreted in the SERVER's timezone — wrong when
-- the API host and the user are in different zones (a VPS in UTC vs. the user in
-- Eastern). Store the IANA timezone (e.g. 'America/New_York') the SPA captured
-- from the user's browser at save time; the worker computes next_run in THAT
-- zone. IANA (not a fixed UTC offset) so DST stays correct year-round.
--
-- Nullable: when null (legacy rows / unset), the worker falls back to the
-- server's local timezone — the prior behavior.
-- =============================================================================

ALTER TABLE scheduled_jobs ADD COLUMN timezone TEXT NULL;

COMMENT ON COLUMN scheduled_jobs.timezone IS
    'IANA timezone (e.g. America/New_York) the hour_local/minute_local are '
    'interpreted in — the user''s browser tz at save. NULL = fall back to the '
    'server local tz.';
