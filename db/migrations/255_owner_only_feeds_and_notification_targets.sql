-- 255 — Bank connections and notification targets are the owner's (FU-095)
--
-- Decided 2026-10-19: of what the editor role covered, connecting and disconnecting a bank
-- and choosing where a ledger's events are sent become the owner's. The API enforces it
-- (AsLedgerOwner on those routes); this is the RLS half, so a missed API check still
-- cannot let an editor do either (ADR-0083 D2).
--
--   * A notification target receives the ledger's events, and an editor could point them
--     at an address of their own. Every write to ledger_notification_subscribers is the
--     owner's. The publisher's health columns are written as the service role.
--   * A bank connection holds credentials to the owner's accounts at the bank. Creating or
--     deleting one is the owner's. UPDATE stays with owners and editors: a sync, which
--     editors keep, records its status and last-synced time on the row.
--
-- Deterministic: only the catalog changes.

DROP POLICY ledger_notification_subscribers_write ON ledger_notification_subscribers;
CREATE POLICY ledger_notification_subscribers_write ON ledger_notification_subscribers FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role = 'owner'))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role = 'owner'));

DROP POLICY feed_connections_write ON feed_connections;
CREATE POLICY feed_connections_create ON feed_connections FOR INSERT TO coffer_app
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role = 'owner'));
CREATE POLICY feed_connections_remove ON feed_connections FOR DELETE TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()
                            AND ulg.role = 'owner'));
CREATE POLICY feed_connections_update ON feed_connections FOR UPDATE TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));
