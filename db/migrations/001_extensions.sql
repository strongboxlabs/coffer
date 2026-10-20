-- Phase 1: required PostgreSQL extensions.
-- pg_trgm  - trigram similarity for fuzzy payee matching in the merge pipeline
-- pgcrypto - exposes digest() and crypt(); gen_random_uuid() is built-in on PG13+

CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
