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

-- ---------------------------------------------------------------------------
-- Vertex labels (BE-020)
--
-- AGE creates labels implicitly on first node insert, but we eagerly create
-- them so the schema is self-describing and so that property indexes (below)
-- have a backing table to target. `create_vlabel` raises if the label already
-- exists, so each call is wrapped in an EXCEPTION block to keep this script
-- idempotent across re-runs.
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    vlabels TEXT[] := ARRAY[
        'Service', 'Endpoint', 'Event', 'Topic', 'Storage',
        'Dependency', 'Convention', 'Capability', 'Skill', 'Team'
    ];
    lbl TEXT;
BEGIN
    LOAD 'age';
    SET search_path = ag_catalog, "$user", public;
    FOREACH lbl IN ARRAY vlabels LOOP
        BEGIN
            PERFORM ag_catalog.create_vlabel('archmind_graph', lbl);
        EXCEPTION WHEN OTHERS THEN
            -- Label already exists (or AGE-version-specific error); ignore so
            -- the script remains idempotent.
            NULL;
        END;
    END LOOP;
END
$$;

-- ---------------------------------------------------------------------------
-- Edge labels (BE-020)
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    elabels TEXT[] := ARRAY[
        'PUBLISHES', 'CONSUMES', 'OWNS', 'READS', 'EXPOSES', 'CALLS',
        'PUBLISHED_TO', 'CONSUMED_FROM', 'DEPENDS_ON', 'FOLLOWS',
        'USES_CAPABILITY', 'APPLIES_TO', 'OWNED_BY'
    ];
    lbl TEXT;
BEGIN
    LOAD 'age';
    SET search_path = ag_catalog, "$user", public;
    FOREACH lbl IN ARRAY elabels LOOP
        BEGIN
            PERFORM ag_catalog.create_elabel('archmind_graph', lbl);
        EXCEPTION WHEN OTHERS THEN
            NULL;
        END;
    END LOOP;
END
$$;

-- ---------------------------------------------------------------------------
-- Property indexes (BE-020)
--
-- Every ArchMind node carries `workspace_id`; all reads MUST filter by it.
-- Index the `workspace_id` property on each vertex label so workspace-scoped
-- traversals stay cheap as the graph grows.
--
-- NOTE on AGE property-access syntax: this script uses the
-- `agtype_access_operator(properties, '"workspace_id"'::agtype)` form, which
-- works on AGE 1.5.x. Older / newer AGE builds may expose this as the
-- `properties -> '"workspace_id"'::agtype` operator instead. If index
-- creation fails on this AGE version, refer to the AGE docs for the current
-- property-access expression syntax (-> operator vs agtype_access_operator
-- function) and update accordingly.
-- ---------------------------------------------------------------------------
DO $$
DECLARE
    vlabels TEXT[] := ARRAY[
        'Service', 'Endpoint', 'Event', 'Topic', 'Storage',
        'Dependency', 'Convention', 'Capability', 'Skill', 'Team'
    ];
    lbl TEXT;
    idx_name TEXT;
    sql TEXT;
BEGIN
    LOAD 'age';
    SET search_path = ag_catalog, "$user", public;
    FOREACH lbl IN ARRAY vlabels LOOP
        idx_name := 'ix_' || lower(lbl) || '_workspace';
        sql := format(
            'CREATE INDEX IF NOT EXISTS %I ' ||
            'ON archmind_graph.%I ' ||
            '((ag_catalog.agtype_access_operator(properties, ''"workspace_id"''::ag_catalog.agtype)))',
            idx_name, lbl
        );
        BEGIN
            EXECUTE sql;
        EXCEPTION WHEN OTHERS THEN
            -- If the property-access expression isn't supported on this AGE
            -- build, swallow the error so the rest of init still succeeds.
            -- Operators can be added manually once the correct expression
            -- syntax is confirmed for the deployed AGE version.
            RAISE NOTICE 'Skipping index % on %: %', idx_name, lbl, SQLERRM;
        END;
    END LOOP;
END
$$;
