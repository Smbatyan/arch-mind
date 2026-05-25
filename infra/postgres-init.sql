-- ArchMind Postgres initialization script.
--
-- This script runs automatically on the first startup of the Postgres
-- container (via /docker-entrypoint-initdb.d). It is intentionally
-- idempotent so that it can be safely re-applied to an existing database
-- without raising errors.
--
-- Responsibilities:
--   1. Enable the Apache AGE extension and load it for the current session.
--   2. Enable pgcrypto (used by EF Core migrations for gen_random_uuid()).
--   3. Create the default `archmind_graph` AGE graph if it doesn't exist.
--
-- App tables are NOT created here; EF Core migrations own that surface.

CREATE EXTENSION IF NOT EXISTS age;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

LOAD 'age';
SET search_path = ag_catalog, "$user", public;

-- AGE has no "CREATE GRAPH IF NOT EXISTS", so guard with a DO block.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM ag_catalog.ag_graph
        WHERE name = 'archmind_graph'
    ) THEN
        PERFORM ag_catalog.create_graph('archmind_graph');
    END IF;
END
$$;
