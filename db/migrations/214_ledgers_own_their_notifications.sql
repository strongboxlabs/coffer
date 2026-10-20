-- =============================================================================
-- 214 — every ledger owns its own notification setup; 'inherit' is retired.
-- =============================================================================
--
-- WHY: 'inherit' routed a ledger's events to EVERY deployment target with no ledger
-- filter, and it is the default for every ledger ever created (mig 208). On a shared
-- install that hands one ledger's activity to whoever watches the deployment's channel
-- — the publisher's own test calls this a leak in the case it guards against. Two scopes
-- that overlap by default are not two scopes.
--
-- After this migration a ledger's events go only to that ledger's targets. Wanting one
-- destination for several ledgers is served by pointing them at the same URL, which now
-- works because the delivered payload names its ledger.
--
-- THE HAZARD THIS MIGRATION EXISTS TO AVOID: every ledger in the field is 'inherit', and
-- almost none have their own targets. Retiring the mode without moving anything would
-- mute ledger scope on every existing install — reproducing ADR-0096's founding incident
-- inside the subsystem built to end it. So the targets move first, in the same
-- transaction, and an install that was reporting somewhere keeps reporting there.
--
-- WHY THIS IS PURE SQL: both tables' config_ciphertext are sealed with the same master
-- KEK by the same primitive (LedgerKeyService.SealWithMasterKey — one call site per
-- scope, one OpenConfig for both), and KekRotationService rotates them in a single pass
-- with identical code. So the ciphertext BYTES are portable between the tables verbatim.
-- No key material is needed at migration time, which is what makes this safe to run
-- before the app has a master key in hand.
--
-- WHAT IS DELIBERATELY *NOT* COPIED:
--   * Heartbeat targets (monitors IS NOT NULL). One healthchecks URL is ONE check, so
--     copying a deployment switch into N ledgers would create N ledgers pinging one
--     check — any single ledger's job would hold it green and mask every other ledger's
--     dead one. That is the exact false-coverage failure the monitor binding was built
--     to eliminate. The deployment's backup switch also watches a deployment job, which
--     no ledger runs. It stays where it is and keeps working.
--   * Disabled targets. A target an admin switched off must not come back on as N copies.
--   * Ledgers already in 'own'. They opted out explicitly; their targets are the answer
--     already, and adding the deployment's would silently re-create the leak per ledger.
--
-- The mode column and its CHECK are dropped rather than pinned to a single value. A
-- one-valued column reads as configurable and is not, and leaving it would let a future
-- call site reintroduce the branch — the same argument mig 212's header makes about
-- single-valued schema enumerations.
-- =============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Move the deployment's MESSAGE targets into every ledger that was inheriting.
-- ---------------------------------------------------------------------------
-- Ordered before the DROP on purpose: the mode column is what tells us which ledgers
-- were inheriting, and after this statement that information has served its only
-- remaining purpose.
INSERT INTO ledger_notification_subscribers
    (ledger_id, subscriber_key, display_name, is_enabled,
     min_severity, topics, monitors, config_ciphertext)
SELECT l.id,
       s.subscriber_key,
       s.display_name,
       TRUE,
       s.min_severity,
       s.topics,
       NULL,               -- message target: bound to no monitor, by construction below
       s.config_ciphertext
  FROM ledgers l
  CROSS JOIN notification_subscribers s
 WHERE l.notification_mode = 'inherit'
   AND s.is_enabled
   AND s.monitors IS NULL
   -- Idempotence against a hand-run of this logic: never a second copy of the same
   -- provider for the same ledger. Compares subscriber_key, not ciphertext — two seals
   -- of the same URL differ byte for byte because SealWithMasterKey draws a fresh nonce
   -- per call, so ciphertext equality can never be tested.
   AND NOT EXISTS (
       SELECT 1
         FROM ledger_notification_subscribers x
        WHERE x.ledger_id = l.id
          AND x.subscriber_key = s.subscriber_key
   );

-- ---------------------------------------------------------------------------
-- 2. Retire the mode.
-- ---------------------------------------------------------------------------
ALTER TABLE ledgers
    DROP CONSTRAINT IF EXISTS ck_ledgers_notification_mode;

ALTER TABLE ledgers
    DROP COLUMN IF EXISTS notification_mode;

COMMIT;
