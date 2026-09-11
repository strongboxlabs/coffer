-- =============================================================================
-- 222 — a reusable, hand-editable description of one institution's delimited export.
-- =============================================================================
--
-- ADR-0031 Phase 5. A generic provider reads a file according to one of these rows
-- instead of a hand-written parser per institution.
--
-- WHY A DOCUMENT AND NOT TYPED COLUMNS. This was first written as a dozen typed
-- columns with CHECK constraints, and the first real target file argued it down. One
-- department-store export produced this many surprises on its own:
--
--     * TAB-delimited, despite a .csv extension — zero commas in the whole file
--     * NO header row: line 1 is already data
--     * UTF-8 WITH BOM
--     * a currency symbol inside the amount field
--     * amounts signed from the ISSUER's perspective (a purchase is POSITIVE
--       because it increases what you owe)
--     * a fixed-width, space-padded description packing merchant, city and state
--
-- Every one of those would have been a column, and that is one file. A schema that
-- needs a migration per institution is the wrong shape for a format nobody controls,
-- and the churn would land on every existing row as a nullable-with-default. The
-- definition is therefore a YAML document, versioned, which people can also edit by
-- hand — the same way a preset for their bank could be pasted in or shared.
--
-- YAML TEXT, NOT JSONB. The point of a hand-editable document is that it survives
-- being hand-edited: comments, key order and formatting are the author's, and
-- round-tripping through JSONB would silently discard all three. What is stored is
-- exactly what was written.
--
-- ===== WHAT THIS TABLE CANNOT ENFORCE, AND WHERE THE REAL GATE IS =====
--
-- Choosing a document gives up every constraint the column version had: that a
-- signed shape names an amount column, that an index is >= 1, that the delimiter is
-- one of a known set. Postgres cannot check any of it here. That work does not
-- disappear — it MOVES, to CsvMappingValidator, and it has to be stricter there than
-- the CHECKs were, not looser:
--
--   * UNKNOWN KEYS ARE REJECTED. A document parser's default is to ignore what it
--     does not recognise, so a mistyped `ammount_column` would leave the amount
--     unmapped and the import would read a different column as money. Silent
--     misconfiguration is the failure mode this whole repo keeps finding; a typo has
--     to be an error, not a no-op.
--   * Cross-field rules the CHECKs used to hold (shape <-> which columns) are
--     asserted in the validator instead.
--   * Column indexes are 1-BASED. A 0 would read the wrong field rather than fail,
--     and in an amount position that means importing a date as money.
--
-- `schema_version` exists so a stored document can be read by the validator that
-- understood it. A mapping written today must not start failing because version 2
-- added a required key.
--
-- The size cap is a guard, not a policy: this is a format description, and anything
-- approaching 64 KB is a paste accident or an attack, either of which is better
-- refused at the boundary than parsed.
--
-- `name` stays a real column rather than living inside the document: it is what the
-- picker lists and what uniqueness is enforced on, and a value that is both queried
-- and hand-editable inside a blob is a value with two sources of truth.
--
-- DDL ONLY.

BEGIN;

CREATE TABLE feed_csv_mappings (
    id              UUID        NOT NULL DEFAULT gen_random_uuid(),
    ledger_id       UUID        NOT NULL,
    -- Named, and reusable across accounts. The mapping describes a FILE SHAPE, not a
    -- destination: two cards from one issuer export identically, and binding the
    -- shape to an account would duplicate it and let the copies drift.
    name            TEXT        NOT NULL,

    -- The document, exactly as its author wrote it.
    definition_yaml TEXT        NOT NULL,
    -- Which validator understood it. Bumped only when a change would reject a
    -- document that was valid before.
    schema_version  INT         NOT NULL DEFAULT 1,

    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT pk_feed_csv_mappings PRIMARY KEY (id),
    CONSTRAINT fk_feed_csv_mappings_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE,
    -- The only two things the database can honestly assert about a document: that it
    -- is not empty, and that it is not enormous. Everything else is the validator's.
    CONSTRAINT ck_feed_csv_mappings_yaml_present
        CHECK (length(btrim(definition_yaml)) > 0),
    CONSTRAINT ck_feed_csv_mappings_yaml_size
        CHECK (length(definition_yaml) <= 65536),
    CONSTRAINT ck_feed_csv_mappings_name_present
        CHECK (length(btrim(name)) > 0),
    CONSTRAINT ck_feed_csv_mappings_schema_version
        CHECK (schema_version >= 1)
);

-- One name per ledger: the wizard offers these by name, and two "Store card" rows
-- would make the choice a coin flip. Case-insensitive, because "Store card" and
-- "store card" are the same choice to the person reading the list.
CREATE UNIQUE INDEX uq_feed_csv_mappings_ledger_name
    ON feed_csv_mappings (ledger_id, lower(name));

COMMENT ON TABLE feed_csv_mappings IS
    'Reusable, hand-editable description of one institution''s delimited export '
    '(ADR-0031 Phase 5). The definition is a YAML document rather than typed columns '
    'because one real file produced six format surprises and a schema that needs a '
    'migration per institution is the wrong shape. Validation lives in '
    'CsvMappingValidator, which is STRICTER than the CHECKs it replaced — unknown keys '
    'are rejected, because a mistyped key that is silently ignored would leave a field '
    'unmapped and import the wrong column as money.';

COMMENT ON COLUMN feed_csv_mappings.definition_yaml IS
    'YAML source as written, not a normalised re-render: comments, key order and '
    'formatting belong to whoever wrote it, and a document meant to be hand-edited has '
    'to survive being hand-edited.';

-- ---------------------------------------------------------------------------
-- RLS, role-aware per mig 174 / ADR-0083 D2. A table added after 174 gets no
-- protection unless it declares the pair itself, and ALTER DEFAULT PRIVILEGES
-- already grants coffer_app write — so "no policy" would mean "no refusal".
-- ---------------------------------------------------------------------------
ALTER TABLE feed_csv_mappings ENABLE ROW LEVEL SECURITY;

CREATE POLICY feed_csv_mappings_read
    ON feed_csv_mappings FOR SELECT TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

CREATE POLICY feed_csv_mappings_write
    ON feed_csv_mappings FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
               AND role IN ('owner', 'editor')
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
               AND role IN ('owner', 'editor')
        )
    );

COMMIT;
