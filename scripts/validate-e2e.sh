#!/usr/bin/env bash
#
# BE-045: end-to-end validation runner.
#
# Brings the docker compose stack up if it isn't already, then runs the
# ArchMind.Validation harness against the live HTTP API. Any flags passed to
# this script are forwarded verbatim to the harness — e.g.
#
#   ./scripts/validate-e2e.sh --timeout-minutes 45 --admin-password ...
#
# Exit code is the harness exit code (0 = all checkpoints passed, 1 = failure).

set -euo pipefail
cd "$(dirname "$0")/.."

# Start docker compose stack if backend isn't already running.
if ! docker compose ps backend --status running -q 2>/dev/null | grep -q .; then
  echo "Starting docker compose stack..."
  docker compose up -d
  # Give Postgres + AGE + the API time to settle before the harness starts
  # probing /health and /smoke. The harness itself fail-fasts on health, so
  # a short sleep here gives the orchestrator one shot before we bail.
  sleep 10
fi

cd backend
exec dotnet run --project ArchMind.Validation/ArchMind.Validation.csproj -- "$@"
