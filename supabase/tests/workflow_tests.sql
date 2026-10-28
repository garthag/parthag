-- CONSTRUCT DPR database acceptance tests.
-- Run with supabase/tests/run.sh (creates a throw-away database).
-- Every test runs as the `authenticated` role with a JWT subject, exactly
-- like a browser request through PostgREST, so Row Level Security applies.
\set ON_ERROR_STOP 1
\set QUIET 1

create or replace function pg_temp.ok(cond boolean, msg text) returns void language plpgsql as $$
begin
  if cond is distinct from true then raise exception 'FAILED: %', msg; end if;
  raise notice 'ok - %', msg;
end $$;

create or replace function pg_temp.fails(sql text, expect text, msg text) returns void language plpgsql as $$
begin
  execute sql;
  raise exception 'FAILED (no error raised): %', msg;
exception when others then
  if sqlerrm like 'FAILED (no error raised)%' then raise; end if;
  if position(lower(expect) in lower(sqlerrm)) = 0 then
    raise exception 'FAILED: % — unexpected error: %', msg, sqlerrm;
  end if;
  raise notice 'ok - % (rejected: %)', msg, left(sqlerrm, 90);
end $$;

create or replace function pg_temp.login(p uuid) returns void language plpgsql as $$
begin
  perform set_config('request.jwt.claims', json_build_object('sub', p, 'role', 'authenticated')::text, false);
end $$;
grant execute on all functions in schema pg_temp to authenticated;

-- Users (Supabase Auth would create these on sign-up)
insert into auth.users (id, email, raw_user_meta_data) values
  ('00000000-0000-0000-0000-00000000000a', 'alice.pm@example.com', '{"full_name":"Alice (PM, project A)"}'),
  ('00000000-0000-0000-0000-00000000000b', 'bob.site@example.com', '{"full_name":"Bob (site engineer, project A)"}'),
  ('00000000-0000-0000-0000-00000000000c', 'carol.pm@example.com', '{"full_name":"Carol (admin, project B)"}'),
  ('00000000-0000-0000-0000-00000000000d', 'dan.auditor@example.com', '{"full_name":"Dan (auditor, project A)"}');

set role authenticated;

-- ===========================================================================
-- Setup: Alice creates organisation + project A; Carol creates project B.
-- ===========================================================================
select pg_temp.login('00000000-0000-0000-0000-00000000000a');
create temp table ids (k text primary key, v uuid);
grant all on ids to authenticated;
insert into ids select 'orgA', public.create_organization('Org A');
insert into ids select 'projA', public.create_project((select v from ids where k = 'orgA'),
  '{"code":"PA","name":"Project A","start_date":"2026-01-01","completion_date":"2026-12-31"}');
select public.add_project_member((select v from ids where k = 'projA'), 'bob.site@example.com', 'site_engineer');
select public.add_project_member((select v from ids where k = 'projA'), 'dan.auditor@example.com', 'auditor');

insert into boq_items (project_id, item_no, description, unit, tender_qty, rate)
select v, 'S-02', 'Steel erection', 'MT', 100, 18000.50 from ids where k = 'projA';
insert into activities (project_id, code, name, unit, total_qty, boq_item_id, planned_start, planned_finish, original_duration)
select (select v from ids where k = 'projA'), 'ER-1', 'Erect skip bridge', 'MT', 100,
       (select id from boq_items where item_no = 'S-02'), '2026-10-01', '2026-10-31', 31;
insert into ids select 'act', id from activities where code = 'ER-1';
insert into ids select 'boq', id from boq_items where item_no = 'S-02';

select pg_temp.login('00000000-0000-0000-0000-00000000000c');
insert into ids select 'orgB', public.create_organization('Org B');
insert into ids select 'projB', public.create_project((select v from ids where k = 'orgB'), '{"code":"PB","name":"Project B"}');
insert into activities (project_id, code, name, unit, total_qty) select v, 'B-1', 'Secret B work', 'm³', 10 from ids where k = 'projB';
insert into ids select 'actB', id from activities where code = 'B-1';

-- ===========================================================================
-- Test A — Daily reporting: create DPR, enter quantity, save, reload
-- ===========================================================================
select pg_temp.login('00000000-0000-0000-0000-00000000000b');
insert into ids values ('dpr1', gen_random_uuid()), ('key1', gen_random_uuid());
select public.save_dpr(jsonb_build_object(
  'id', (select v from ids where k = 'dpr1'), 'idempotency_key', (select v from ids where k = 'key1'),
  'project_id', (select v from ids where k = 'projA'), 'report_date', '2026-10-10', 'shift', 'day',
  'weather', 'Clear',
  'activities', jsonb_build_array(jsonb_build_object('activity_id', (select v from ids where k = 'act'), 'qty_today', 12)),
  'manpower', jsonb_build_array(jsonb_build_object('trade', 'Fitters', 'skilled', 20, 'unskilled', 4, 'hours_per_person', 8, 'overtime_hours', 6))));

select pg_temp.ok((select dpr_no = 'PA-DPR-0001' and status = 'draft' from dpr_headers where id = (select v from ids where k = 'dpr1')),
  'A: DPR saved with generated number PA-DPR-0001');
select pg_temp.ok((select qty_today = 12 and prev_cum_qty = 0 and cum_qty = 12 and balance_qty = 88 and actual_progress_pct = 12
                   from dpr_activity_entries where dpr_id = (select v from ids where k = 'dpr1')),
  'A: cumulative 12, balance 88, progress 12 % computed by the server');
select pg_temp.ok((select planned_progress_pct = round(10 * 100.0 / 31, 3) from dpr_activity_entries
                   where dpr_id = (select v from ids where k = 'dpr1')), 'A: planned progress from planned dates (10 of 31 days)');
select pg_temp.ok((select headcount = 24 and man_hours = 198 from manpower_entries
                   where dpr_id = (select v from ids where k = 'dpr1')), 'A: manpower headcount 24, man-hours 24×8+6 = 198');

-- ===========================================================================
-- Test J — Idempotent replay (offline queue retry) creates no duplicate
-- ===========================================================================
select pg_temp.ok((select (public.save_dpr(jsonb_build_object(
  'id', gen_random_uuid(), 'idempotency_key', (select v from ids where k = 'key1'),
  'project_id', (select v from ids where k = 'projA'), 'report_date', '2026-10-10')) ->> 'replayed')::boolean),
  'J: replaying the same idempotency key returns the first result');
select pg_temp.ok((select count(*) = 1 from dpr_headers where project_id = (select v from ids where k = 'projA')),
  'J: still exactly one DPR');
select pg_temp.fails($$select public.save_dpr(jsonb_build_object('project_id', (select v from ids where k = 'projA'),
  'report_date', '2026-10-10', 'shift', 'day'))$$, 'already exists', 'J: second DPR for same date/shift/area refused');

-- Optimistic concurrency
select pg_temp.fails($$select public.save_dpr(jsonb_build_object('id', (select v from ids where k = 'dpr1'),
  'project_id', (select v from ids where k = 'projA'), 'report_date', '2026-10-10', 'expected_version', 0))$$,
  'CONFLICT', 'Conflict: stale version is rejected');

-- Overrun protection
select public.save_dpr(jsonb_build_object('id', (select v from ids where k = 'dpr1'),
  'project_id', (select v from ids where k = 'projA'), 'report_date', '2026-10-10',
  'activities', jsonb_build_array(jsonb_build_object('activity_id', (select v from ids where k = 'act'), 'qty_today', 130))));
select pg_temp.fails($$select public.submit_dpr((select v from ids where k = 'dpr1'))$$, 'exceeds approved quantity',
  'Overrun: 130 > 100 MT blocked without an authorised overrun record');
select public.save_dpr(jsonb_build_object('id', (select v from ids where k = 'dpr1'),
  'project_id', (select v from ids where k = 'projA'), 'report_date', '2026-10-10', 'weather', 'Clear',
  'activities', jsonb_build_array(jsonb_build_object('activity_id', (select v from ids where k = 'act'), 'qty_today', 12))));

-- ===========================================================================
-- Test H — Project isolation
-- ===========================================================================
select pg_temp.ok((select count(*) = 0 from activities where project_id = (select v from ids where k = 'projB')),
  'H: Bob (project A) cannot read project B activities');
select pg_temp.ok((select count(*) = 0 from projects where id = (select v from ids where k = 'projB')),
  'H: Bob cannot see project B at all');
select pg_temp.fails($$select public.save_dpr(jsonb_build_object('project_id', (select v from ids where k = 'projB'),
  'report_date', '2026-10-10'))$$, 'permission', 'H: Bob cannot create a DPR in project B');
select pg_temp.fails($$select public.save_dpr(jsonb_build_object('project_id', (select v from ids where k = 'projA'),
  'report_date', '2026-10-11', 'activities', jsonb_build_array(jsonb_build_object('activity_id',
  (select v from ids where k = 'actB')))))$$, 'not in this project', 'H: project B activity id cannot be smuggled into a project A DPR');
update activities set name = 'hacked' where code = 'ER-1';
select pg_temp.ok((select name = 'Erect skip bridge' from activities where code = 'ER-1'),
  'H: site engineer cannot edit the activity master (RLS update filtered)');
select pg_temp.fails($$insert into dpr_headers (project_id, seq, dpr_no, report_date, prepared_by)
  values ((select v from ids where k = 'projA'), 99, 'X', '2026-10-01', auth.uid())$$, 'row-level security',
  'H: direct table writes to DPRs are blocked (must use workflow functions)');

select pg_temp.login('00000000-0000-0000-0000-00000000000c');
select pg_temp.ok((select count(*) = 0 from dpr_headers where project_id = (select v from ids where k = 'projA')),
  'H: Carol (project B) cannot read project A DPRs');
select pg_temp.fails($$select public.dpr_document((select v from ids where k = 'dpr1'))$$, 'permission',
  'H: Carol cannot fetch a project A DPR document via RPC');

-- ===========================================================================
-- Test I — Submit, reject with comment, revise, approve; revisions intact
-- ===========================================================================
select pg_temp.login('00000000-0000-0000-0000-00000000000b');
select public.submit_dpr((select v from ids where k = 'dpr1'));
select pg_temp.fails($$select public.review_dpr((select v from ids where k = 'dpr1'), 'approve')$$, 'permission',
  'I: site engineer cannot approve');

select pg_temp.login('00000000-0000-0000-0000-00000000000a');
select pg_temp.fails($$select public.review_dpr((select v from ids where k = 'dpr1'), 'reject')$$, 'reason is required',
  'I: rejection needs a comment');
select public.review_dpr((select v from ids where k = 'dpr1'), 'reject', 'Weather details missing for afternoon');
select pg_temp.ok((select status = 'rejected' from dpr_headers where id = (select v from ids where k = 'dpr1')), 'I: DPR rejected');

select pg_temp.login('00000000-0000-0000-0000-00000000000b');
select public.save_dpr(jsonb_build_object('id', (select v from ids where k = 'dpr1'),
  'project_id', (select v from ids where k = 'projA'), 'report_date', '2026-10-10', 'weather', 'Clear; 34 °C; light wind PM',
  'activities', jsonb_build_array(jsonb_build_object('activity_id', (select v from ids where k = 'act'), 'qty_today', 12))));
select public.submit_dpr((select v from ids where k = 'dpr1'));

select pg_temp.login('00000000-0000-0000-0000-00000000000a');
-- Reviewer accepts only 10 MT of the 12 MT reported.
select public.review_dpr((select v from ids where k = 'dpr1'), 'approve', 'OK',
  jsonb_build_object((select id::text from dpr_activity_entries where dpr_id = (select v from ids where k = 'dpr1')), 10));
select pg_temp.ok((select qty_today = 12 and qty_accepted = 10 and cum_qty = 10 from dpr_activity_entries
                   where dpr_id = (select v from ids where k = 'dpr1')), 'I: reported 12 vs accepted 10 kept separately');
select pg_temp.ok((select count(*) = 1 from report_revisions where dpr_id = (select v from ids where k = 'dpr1')),
  'I: approval created an immutable report snapshot');
select pg_temp.ok((select actual_start = '2026-10-10' and status = 'in_progress' from activities where code = 'ER-1'),
  'I: approval moved activity actual start and status');

select pg_temp.fails($$select public.save_dpr(jsonb_build_object('id', (select v from ids where k = 'dpr1'),
  'project_id', (select v from ids where k = 'projA'), 'report_date', '2026-10-10'))$$, 'no longer be edited',
  'I: approved DPR cannot be edited');
reset role;
select pg_temp.fails($$update dpr_headers set weather = 'tampered' where id = (select v from ids where k = 'dpr1')$$,
  'immutable', 'I: even the database owner cannot alter an approved DPR');
select pg_temp.fails($$update dpr_activity_entries set qty_today = 99 where dpr_id = (select v from ids where k = 'dpr1')$$,
  'immutable', 'I: approved DPR entries are immutable');
select pg_temp.fails($$update report_revisions set snapshot = '{}' where dpr_id = (select v from ids where k = 'dpr1')$$,
  'immutable', 'I: report snapshots are immutable');
set role authenticated;

-- Second DPR builds on the approved cumulative
select pg_temp.login('00000000-0000-0000-0000-00000000000b');
insert into ids values ('dpr2', gen_random_uuid());
select public.save_dpr(jsonb_build_object('id', (select v from ids where k = 'dpr2'),
  'project_id', (select v from ids where k = 'projA'), 'report_date', '2026-10-11',
  'activities', jsonb_build_array(jsonb_build_object('activity_id', (select v from ids where k = 'act'), 'qty_today', 15))));
select pg_temp.ok((select prev_cum_qty = 10 and cum_qty = 25 and balance_qty = 75 from dpr_activity_entries
                   where dpr_id = (select v from ids where k = 'dpr2')), 'A: next DPR previous cumulative = accepted 10, cumulative 25');

-- Revision of the approved DPR #1
select pg_temp.login('00000000-0000-0000-0000-00000000000a');
insert into ids select 'dpr1r1', public.start_dpr_revision((select v from ids where k = 'dpr1'), 'Quantity re-measured: 11 MT');
select public.save_dpr(jsonb_build_object('id', (select v from ids where k = 'dpr1r1'),
  'project_id', (select v from ids where k = 'projA'), 'report_date', '2026-10-10', 'weather', 'Clear',
  'activities', jsonb_build_array(jsonb_build_object('activity_id', (select v from ids where k = 'act'), 'qty_today', 11))));
select public.submit_dpr((select v from ids where k = 'dpr1r1'));
update projects set allow_self_approval = true where id = (select v from ids where k = 'projA');
select public.review_dpr((select v from ids where k = 'dpr1r1'), 'approve', 'Revision accepted');
select pg_temp.ok((select revision = 1 and status = 'approved' and dpr_no = 'PA-DPR-0001'
                   from dpr_headers where id = (select v from ids where k = 'dpr1r1')), 'I: revision 1 approved with same DPR number');
select pg_temp.ok((select status = 'approved' and weather = 'Clear; 34 °C; light wind PM' from dpr_headers
                   where id = (select v from ids where k = 'dpr1')), 'I: original approved revision 0 still intact');
select pg_temp.ok((select (snapshot -> 'activities' -> 0 ->> 'qty_accepted')::numeric = 10 from report_revisions
                   where dpr_id = (select v from ids where k = 'dpr1')), 'I: revision 0 snapshot still shows 10 MT');
select pg_temp.ok((select accepted_cum_qty = 11 from activity_progress where activity_id = (select v from ids where k = 'act')),
  'I: effective progress now uses revision 1 (11 MT), not both revisions');

-- ===========================================================================
-- Test K — Financial integrity: BOQ changes never alter approved reports
-- ===========================================================================
select pg_temp.fails($$update boq_items set rate = 1 where id = (select v from ids where k = 'boq')$$, 'revise_boq_item',
  'K: BOQ rate cannot be changed by a plain update');
create temp table snap as select snapshot_sha256 from report_revisions where dpr_id = (select v from ids where k = 'dpr1');
select public.revise_boq_item((select v from ids where k = 'boq'), 120, 18250.75, 'CO-01: additional platform steel');
select pg_temp.ok((select revised_qty = 120 and rate = 18250.75 from boq_items where id = (select v from ids where k = 'boq')),
  'K: authorised BOQ revision applied');
select pg_temp.ok((select count(*) = 1 from boq_revisions where boq_item_id = (select v from ids where k = 'boq')),
  'K: BOQ revision history recorded with reason');
select pg_temp.ok((select r.snapshot_sha256 = s.snapshot_sha256 and r.snapshot_sha256 = public.sha256_hex(r.snapshot::text)
                   from report_revisions r, snap s where r.dpr_id = (select v from ids where k = 'dpr1')),
  'K: approved report snapshot unchanged and checksum verifies');
select pg_temp.ok((select contract_amount = 2190090.00 and executed_amount = round(11 * 18250.75, 2)
                   from boq_status where id = (select v from ids where k = 'boq')),
  'K: BOQ amounts are exact decimals (120 × 18,250.75 = 21,90,090.00)');

-- ===========================================================================
-- Read-only auditor, audit trail, export
-- ===========================================================================
select pg_temp.login('00000000-0000-0000-0000-00000000000d');
select pg_temp.ok((select count(*) >= 2 from dpr_headers where project_id = (select v from ids where k = 'projA')), 'Auditor can read DPRs');
select pg_temp.fails($$select public.save_dpr(jsonb_build_object('project_id', (select v from ids where k = 'projA'),
  'report_date', '2026-10-12'))$$, 'permission', 'Auditor cannot create DPRs');
select pg_temp.ok((select count(*) > 5 from audit_events where project_id = (select v from ids where k = 'projA')),
  'Audit trail records project changes');
select pg_temp.ok((select jsonb_array_length(public.export_project((select v from ids where k = 'projA')) -> 'dpr_headers') = 3),
  'Full project export includes all DPR revisions');

-- ===========================================================================
-- Demo loader runs end-to-end through the same workflow
-- ===========================================================================
select pg_temp.login('00000000-0000-0000-0000-00000000000c');
insert into ids select 'demo', public.load_demo_project();
select pg_temp.ok((select count(*) = 7 from dpr_headers where project_id = (select v from ids where k = 'demo') and status = 'approved'),
  'Demo project has 7 approved DPRs');
select pg_temp.ok((select stock_qty > 0 from material_stock where code = 'CEM-OPC53' and project_id = (select v from ids where k = 'demo')),
  'Demo material ledger has positive cement stock');

reset role;
