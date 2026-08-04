-- Server-side concurrency control for sync runs (slice 2c.2).
--
-- Slice 2c.1 wrote one `sync_runs` row per Sync now click, but
-- nothing prevented two concurrent syncs against the same
-- connection. Rapid Map clicks in the SPA — or any future client
-- that doesn't gate clicks — could fire overlapping syncs that
-- both pull the same transactions and race on the INSERT path.
--
-- This migration enforces "at most one running sync per
-- connection" at the database level. The SyncService catches the
-- resulting unique-violation as a typed `SyncInProgress` failure
-- the endpoint returns as 422 with code `feed-sync-in-progress`.
--
-- Per project memory feedback_server_side_concurrency: the API
-- must own race protection at the DB layer, not rely on the
-- SPA's mapBusy / button-disable state — that approach broke
-- when the SPA's busy-flag lifted between rapid mapping clicks
-- while the syncs themselves were still in flight.
--
-- Crash recovery: a process killed mid-sync leaves a `running`
-- row stranded forever; no future sync against that connection
-- could ever start. The SyncService implements a lazy reaper
-- that flips `running` rows older than 10 minutes to `failed`
-- on the next sync attempt, before its own INSERT. No background
-- worker required.

CREATE UNIQUE INDEX uq_sync_runs_one_running_per_connection
    ON sync_runs (feed_connection_id)
    WHERE status = 'running' AND feed_connection_id IS NOT NULL;

COMMENT ON INDEX uq_sync_runs_one_running_per_connection IS
    'Per-connection sync serialization (slice 2c.2). Two concurrent '
    'sync requests against the same feed_connection_id race on '
    'INSERT — one wins, the other raises a unique-violation that '
    'SyncService maps to FailureReason.SyncInProgress → 422 '
    'feed-sync-in-progress. Stale `running` rows (process crashed '
    'mid-sync) are swept by the lazy reaper in SyncService before '
    'each INSERT.';
