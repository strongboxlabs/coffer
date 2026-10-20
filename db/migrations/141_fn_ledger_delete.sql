-- =============================================================================
-- 141 — fn_ledger_delete: complete, FK-ordered wipe of one ledger
-- =============================================================================
--
-- Backs DELETE /api/ledgers/{id} (owner-only; ADR-0020). Every `ledger_id`
-- FK is ON DELETE RESTRICT by design (the cross-ledger isolation invariant,
-- ADR-0020 / mig 049/121), so a bare `DELETE FROM ledgers` fails — the
-- ledger's rows must be cleared first, in child→parent order.
--
-- The financial block mirrors the delete order in fn_ledger_snapshot_restore
-- (the latest amendment is mig 127) so the two stay in lockstep; this adds the
-- operational/audit tables the snapshot deliberately excludes. Tables that
-- CASCADE from `ledgers` (user_ledger_grants, ledger_snapshots, scheduled_jobs,
-- user_preferences) and from their parents (provider_run_errors /
-- _promotions ← provider_runs; feed_connection_accounts ← feed_connections /
-- accounts) are NOT listed — the final `DELETE FROM ledgers` (and the two
-- explicit parent deletes) carry them away. The >=1-owner trigger was dropped
-- in mig 087 (API-side enforcement), so the grant cascade raises nothing.
--
-- Per ADR-0032 the API never runs raw SQL: the `ledger_delete(uuid)` TVF
-- wrapper below is bound via HasDbFunction and invoked through LINQ
-- (LedgersRepository.DeleteAsync), mirroring ledger_snapshot_restore.
-- =============================================================================

CREATE OR REPLACE FUNCTION fn_ledger_delete(p_ledger_id uuid)
    RETURNS void
    LANGUAGE plpgsql
AS $$
BEGIN
    -- Operational/audit (RESTRICT → ledgers). provider_runs first so its
    -- CASCADE children (errors + promotions) — whose promotions.header_id
    -- references txn_headers — are gone before the financial block.
    DELETE FROM provider_runs                WHERE ledger_id = p_ledger_id;

    -- Financial footprint — same child→parent order as the snapshot restore
    -- (db/migrations/127…), kept in lockstep.
    DELETE FROM loan_terms                   WHERE ledger_id = p_ledger_id;
    DELETE FROM recurring_occurrence_exceptions WHERE ledger_id = p_ledger_id;
    DELETE FROM recurring_transactions       WHERE ledger_id = p_ledger_id;
    DELETE FROM security_splits              WHERE ledger_id = p_ledger_id;
    DELETE FROM lots                         WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_overrides            WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_overrides         WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_tags              WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_legs                     WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                  WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_group_members   WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_groups          WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                     WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices              WHERE ledger_id = p_ledger_id;
    DELETE FROM account_external_ids         WHERE ledger_id = p_ledger_id;
    DELETE FROM provider_security_mappings   WHERE ledger_id = p_ledger_id;
    DELETE FROM tags                         WHERE ledger_id = p_ledger_id;
    DELETE FROM securities                   WHERE ledger_id = p_ledger_id;
    DELETE FROM accounts                     WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_account_balances  WHERE ledger_id = p_ledger_id;

    -- feed_connections (RESTRICT → ledgers; cascades feed_connection_accounts).
    DELETE FROM feed_connections             WHERE ledger_id = p_ledger_id;

    -- The ledger row. CASCADEs user_ledger_grants, ledger_snapshots,
    -- scheduled_jobs, user_preferences.
    DELETE FROM ledgers                      WHERE id = p_ledger_id;
END;
$$;

COMMENT ON FUNCTION fn_ledger_delete(uuid) IS
    'ADR-0020: complete FK-ordered wipe of one ledger + its grants/row. '
    'Financial-table order mirrors fn_ledger_snapshot_restore.';

-- TVF wrapper so the API invokes the void worker via LINQ (HasDbFunction),
-- never raw SQL (ADR-0032). Returns the input id for a typed projection.
CREATE OR REPLACE FUNCTION ledger_delete(p_ledger_id uuid)
    RETURNS TABLE (ledger_id uuid)
    LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM fn_ledger_delete(p_ledger_id);
    RETURN QUERY SELECT p_ledger_id;
END;
$$;

COMMENT ON FUNCTION ledger_delete(uuid) IS
    'HasDbFunction wrapper over fn_ledger_delete; returns the deleted ledger id.';

-- Invoked by the API through the BYPASSRLS service role (owner-gated in the
-- endpoint), matching LedgersRepository.CreateWithOwnerAsync.
GRANT EXECUTE ON FUNCTION fn_ledger_delete(uuid) TO coffer_service;
GRANT EXECUTE ON FUNCTION ledger_delete(uuid) TO coffer_service;
