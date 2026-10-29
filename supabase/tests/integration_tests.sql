-- Integration layer tests: API role, event feed. Runs after workflow_tests.sql
-- in the same database (reuses its users and projects).
\set ON_ERROR_STOP 1
\set QUIET 1
\i tests/helpers.sql

insert into auth.users (id, email, raw_user_meta_data) values
  ('00000000-0000-0000-0000-0000000000e1', 'erp-bot@example.com', '{"full_name":"ERP integration"}');

set role authenticated;
select pg_temp.login('00000000-0000-0000-0000-00000000000a');
select public.add_project_member((select id from projects where code = 'PA'), 'erp-bot@example.com', 'integration');

-- Events produced by the workflow tests (submit/reject/approve/revision/BOQ)
select pg_temp.ok((select array_agg(event_type order by id) @> array['dpr.submitted','dpr.rejected','dpr.approved','report.snapshot_created','dpr.revision_started','boq.revised']
                   from integration_events where project_id = (select id from projects where code = 'PA')),
  'Events: DPR lifecycle, snapshots, revisions and BOQ revisions are published');
select pg_temp.ok((select (payload ->> 'dpr_no') = 'PA-DPR-0001' and payload ? 'report_date'
                   from integration_events where event_type = 'dpr.approved' order by id limit 1),
  'Events: payload carries DPR number and date');
select pg_temp.ok((select count(*) = 0 from integration_events where event_type like 'dpr.draft%'),
  'Events: draft saves are not published');

select pg_temp.login('00000000-0000-0000-0000-0000000000e1');
select pg_temp.ok((select count(*) > 0 from integration_events), 'Integration account reads the event feed');
select pg_temp.ok((select count(*) = 0 from integration_events where project_id <> (select id from projects where code = 'PA')),
  'Integration account sees only its own project events');
select pg_temp.ok((select count(*) = 0 from projects where code = 'PB'), 'Integration account cannot see other projects');

-- Master-data sync from an external system (e.g. ERP/P6) via upsert
insert into activities (project_id, code, name, unit, total_qty)
values ((select id from projects where code = 'PA'), 'ERP-100', 'Synced from ERP', 'MT', 50)
on conflict (project_id, code) do update set name = excluded.name, total_qty = excluded.total_qty;
select pg_temp.ok((select total_qty = 50 from activities where code = 'ERP-100'), 'Integration account can sync activities');
select pg_temp.ok((select count(*) = 1 from integration_events where event_type = 'activity.created' and payload ->> 'code' = 'ERP-100'),
  'Events: activity.created published for synced activity');
select pg_temp.ok((jsonb_array_length(public.export_project((select id from projects where code = 'PA')) -> 'activities') > 0),
  'Integration account can export the project');

select pg_temp.login('00000000-0000-0000-0000-00000000000b');
select public.submit_dpr((select id from dpr_headers where dpr_no = 'PA-DPR-0002' and revision = 0));
select pg_temp.login('00000000-0000-0000-0000-0000000000e1');
select pg_temp.fails($$select public.review_dpr((select id from dpr_headers where dpr_no = 'PA-DPR-0002' and revision = 0), 'approve')$$,
  'permission', 'Integration account cannot approve reports');
select pg_temp.fails($$select public.save_dpr(jsonb_build_object('project_id', (select id from projects where code = 'PA'), 'report_date', '2026-10-20'))$$,
  'permission', 'Integration account cannot create DPRs');
select pg_temp.fails($$select public.revise_boq_item((select id from boq_items where item_no = 'S-02'), 1, 1, 'x')$$,
  'permission', 'Integration account cannot authorise BOQ revisions');

reset role;
select pg_temp.fails($$delete from integration_events$$, 'append-only', 'Event feed is append-only');
