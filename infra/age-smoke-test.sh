#!/usr/bin/env bash
# ArchMind AGE smoke test.
#
# Verifies that the Apache AGE extension is installed and that the default
# `archmind_graph` is usable by running a tiny Cypher round-trip
# (create node, then delete it) against the running Postgres container.
#
# Usage:
#   1. Start the database:   docker compose up postgres -d
#   2. Wait for healthy:     docker compose ps
#   3. Run this script:      ./infra/age-smoke-test.sh
#
# Exit code 0 means AGE is healthy; any non-zero exit means something
# in the init pipeline did not run as expected.

set -euo pipefail

# Runs a Cypher query against the archmind_graph to verify AGE works.
docker compose exec -T postgres psql -U archmind -d archmind <<'SQL'
LOAD 'age';
SET search_path = ag_catalog, "$user", public;
SELECT * FROM cypher('archmind_graph', $$ CREATE (n:Test {hello: 'world'}) RETURN n $$) as (n agtype);
SELECT * FROM cypher('archmind_graph', $$ MATCH (n:Test) DELETE n $$) as (n agtype);
SQL

echo "AGE smoke test passed."
