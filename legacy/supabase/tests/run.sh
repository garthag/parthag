#!/usr/bin/env bash
# Runs the database acceptance tests against a throw-away PostgreSQL database.
# Usage: supabase/tests/run.sh   (needs psql/createdb access to a local server;
# set PGHOST/PGUSER etc. or run as the postgres user)
set -euo pipefail
cd "$(dirname "$0")/.."
DB="${TEST_DB:-cdpr_test_$$}"
dropdb --if-exists "$DB" >/dev/null 2>&1 || true
# The copy-paste setup files must match the migrations.
tmp=$(mktemp -d); cp setup/part1.sql setup/part2.sql "$tmp"/
setup/build.sh >/dev/null
if ! diff -q setup/part1.sql "$tmp/part1.sql" >/dev/null || ! diff -q setup/part2.sql "$tmp/part2.sql" >/dev/null; then
  echo "supabase/setup/*.sql were out of date and have been regenerated; commit them." >&2; exit 1
fi
createdb "$DB"
trap 'dropdb --if-exists "$DB" >/dev/null 2>&1 || true' EXIT
cat tests/supabase_shim.sql migrations/*.sql | psql -v ON_ERROR_STOP=1 -q -X "$DB" >/dev/null
for t in tests/workflow_tests.sql tests/integration_tests.sql tests/stock_tests.sql; do
  psql -v ON_ERROR_STOP=1 -q -X "$DB" -f "$t" 2>&1 >/dev/null | sed "s/^psql:[^ ]* NOTICE:  //"
done
echo "ALL DATABASE TESTS PASSED"
