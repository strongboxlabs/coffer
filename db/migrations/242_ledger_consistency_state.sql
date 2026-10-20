-- ---------------------------------------------------------------------------
-- 242 — remember what the consistency check found, per ledger.
--
-- THE GAP. The check has always been computed and thrown away: the monitor runs
-- it, publishes a notification if something drifted, and keeps nothing. A repair
-- likewise left no trace, because repairing makes the stored rows agree with the
-- transactions — indistinguishable from their having always agreed.
--
-- So "done" was invisible. An operator with several ledgers could not tell one
-- that had been repaired from one nobody had opened, and a report expected to be
-- unhealthy on some unremembered subset trains people to ignore the one signal
-- that matters (FU-012). Migration 209 created that situation deliberately: it
-- fixed the realized-gains writer and did NOT backfill existing rows, on the
-- sound grounds that a blanket recompute at migration time would rewrite
-- holdings and lots for every pair. The consequence was left unowned.
--
-- WHAT THIS ANSWERS. Three states that were previously one:
--
--   no row            nobody has ever checked this ledger
--   healthy IS NULL   there is something to remember about this ledger, but no
--                     current verdict — repaired before any check ever ran, or
--                     invalidated because a restore replaced the data underneath
--   healthy = true    checked at last_checked_at, and it agreed
--   healthy = false   checked, and failing_projections says what did not
--
-- plus last_repaired_at, so "I repaired this in March" survives the next check.
--
-- "Unknown" is a real state and gets a real NULL rather than a sentinel. The
-- first draft used healthy = false with an empty failing list and an epoch
-- timestamp to mean it, which is wrong in the direction that matters: anything
-- rendering the verdict reads false as UNHEALTHY and would show a red mark for a
-- ledger nobody has examined. A CHECK keeps the pair honest, so "checked but no
-- verdict" and "a verdict from no particular time" cannot be written at all.
--
-- IT ALSO GATES THE CADENCE. ConsistencyMonitor ran on every scheduler tick —
-- 15 minutes, 96 full checks a day, to announce at most once (the publish is
-- throttled to 24h, the check behind it was not). On a real ledger that is
-- every balance row and every position re-verified every quarter hour — work
-- that grows with the ledger, for an answer that rarely changes. With
-- last_checked_at recorded, the monitor can simply skip a ledger checked
-- recently, which is a cheaper fix than restructuring the check and does not
-- foreclose doing that too (FU-045 — the N+1 in CheckRealizedGainsAsync stands
-- on its own).
--
-- Not a log. One row per ledger, overwritten. The HISTORY of repairs lives in
-- ledger_events as `consistency.repaired`, which is append-only and already has
-- retention; duplicating it here would be two sources of truth for the same
-- fact, and the one that drifts is always the copy.
-- ---------------------------------------------------------------------------

CREATE TABLE ledger_consistency_state (
    -- The ledger IS the key. A surrogate id would permit two verdicts for one
    -- ledger, which is exactly the ambiguity this table exists to remove.
    ledger_id           uuid        NOT NULL,

    -- When the projections were last compared against the transactions. Written
    -- on EVERY check, automatic or on demand: a verdict without a date cannot
    -- distinguish "fine ten minutes ago" from "fine last August".
    --
    -- NULL means no current verdict, and it is also what makes the ledger due:
    -- the monitor's gate is `last_checked_at > cutoff`, which is NULL — not true
    -- — for an unknown, so setting this to NULL is how anything invalidates a
    -- verdict without having to know the cadence.
    last_checked_at     timestamptz NULL,

    -- The verdict at that moment, NULL when there is none.
    healthy             boolean     NULL,

    -- Which projections disagreed, by their ConsistencyProjections name. Empty
    -- when healthy. Stored rather than derived so the list survives without
    -- re-running the check that produced it.
    failing_projections text[]      NOT NULL DEFAULT '{}',

    -- The last time someone repaired anything on this ledger. Deliberately NOT
    -- cleared by a later healthy check: "this was repaired once" is a fact about
    -- the ledger's history that a green check does not retract.
    last_repaired_at    timestamptz NULL,

    updated_at          timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT pk_ledger_consistency_state PRIMARY KEY (ledger_id),

    -- The two halves of a verdict travel together or not at all.
    CONSTRAINT ck_ledger_consistency_state_verdict
        CHECK ((last_checked_at IS NULL) = (healthy IS NULL)),

    -- A ledger with no verdict has nothing to have failed. Without this, an
    -- invalidation that cleared the verdict and forgot the list would leave a
    -- stale set of projection names attached to an unknown.
    CONSTRAINT ck_ledger_consistency_state_failing_empty_when_unknown
        CHECK (healthy IS NOT NULL OR cardinality(failing_projections) = 0),

    -- CASCADE for the lifecycle reason ADR-0096 D1 gives ledger_events: a
    -- verdict about a deleted ledger is meaningless.
    CONSTRAINT fk_ledger_consistency_state_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE
);

COMMENT ON TABLE ledger_consistency_state IS
    'Latest consistency verdict per ledger (FU-012). One row per ledger, '
    'overwritten — the append-only history of repairs is in ledger_events as '
    'consistency.repaired. last_checked_at also gates the monitor cadence.';

COMMENT ON COLUMN ledger_consistency_state.last_repaired_at IS
    'Not cleared by a later healthy check: that a ledger was once repaired is '
    'history, and a green check does not retract it.';

COMMENT ON COLUMN ledger_consistency_state.healthy IS
    'NULL means no current verdict — repaired before any check ran, or '
    'invalidated by a restore. Not a sentinel: false means CHECKED and wrong.';

-- The monitor asks "which ledgers are due a check?" on every tick, which is the
-- only query shaped by anything other than the primary key.
CREATE INDEX ix_ledger_consistency_state_last_checked
    ON ledger_consistency_state (last_checked_at);

ALTER TABLE ledger_consistency_state ENABLE ROW LEVEL SECURITY;

-- READ ONLY for the app role, and no write policy at all — the same treatment
-- mig 208 gave ledger_events and mig 174 gave the other service-written logs.
-- Every writer here is the monitor or an endpoint going through the SERVICE
-- context (BYPASSRLS); no app-role path writes it, and RLS is default-deny for
-- anything without a permissive policy, so an app-role INSERT/UPDATE/DELETE is
-- refused outright rather than relying on nobody trying.
CREATE POLICY ledger_consistency_state_read
    ON ledger_consistency_state FOR SELECT TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

GRANT SELECT ON ledger_consistency_state TO coffer_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ledger_consistency_state TO coffer_service;
