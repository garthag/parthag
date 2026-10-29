#!/usr/bin/env bash
# Regenerates the two copy-paste setup files from the migrations.
# Part 2 is separate because a new role value (migration 9) must be saved
# before migration 10 can use it.
set -euo pipefail
cd "$(dirname "$0")/.."
header() { printf -- '-- CONSTRUCT DPR database setup, %s\n-- GENERATED from supabase/migrations by supabase/setup/build.sh. Do not edit.\n-- Paste the whole file into Supabase > SQL Editor > New query, then press Run.\n\n' "$1"; }
{ header "PART 1 of 2"; for f in migrations/*.sql; do [[ "$f" == *_integrations.sql ]] && continue; printf '\n-- ===== %s =====\n' "$(basename "$f")"; cat "$f"; done; } > setup/part1.sql
{ header "PART 2 of 2 (run after part 1)"; for f in migrations/*_integrations.sql; do printf '\n-- ===== %s =====\n' "$(basename "$f")"; cat "$f"; done; } > setup/part2.sql
echo "Wrote setup/part1.sql and setup/part2.sql"
