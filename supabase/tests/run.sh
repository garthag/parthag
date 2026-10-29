#!/usr/bin/env bash
# Runs the database acceptance tests against a throw-away PostgreSQL database.
# Usage: supabase/tests/run.sh   (needs psql/createdb access to a local server;
# set PGHOST/PGUSER etc. or run as the postgres user)
set -euo pipefail
cd "$(dirname "$0")/.."
DB="${TEST_DB:-cdpr_test_$$}"
dropdb --if-exists "$DB" >/dev/null 2>&1 || true
createdb "$DB"
trap 'dropdb --if-exists "$DB" >/dev/null 2>&1 || true' EXIT
cat tests/supabase_shim.sql migrations/*.sql | psql -v ON_ERROR_STOP=1 -q -X "$DB" >/dev/null
for t in tests/workflow_tests.sql tests/integration_tests.sql; do
  psql -v ON_ERROR_STOP=1 -q -X "$DB" -f "$t" 2>&1 >/dev/null | sed "s/^psql:[^ ]* NOTICE:  //"
done
echo "ALL DATABASE TESTS PASSED"
