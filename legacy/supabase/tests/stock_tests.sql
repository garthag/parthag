-- Stock rule: drafts may exceed approved stock (earlier receipts can be
-- pending approval); approval refuses to make approved stock negative.
\set ON_ERROR_STOP 1
\set QUIET 1
\i tests/helpers.sql

set role authenticated;
select pg_temp.login('00000000-0000-0000-0000-00000000000a');
insert into materials (project_id, code, description, unit)
select id, 'CEM', 'Cement', 'bags' from projects where code = 'PA';

-- Day 1: receipt of 100 bags (not yet approved). Day 2: issue 80 bags.
select public.save_dpr(jsonb_build_object('id', '10000000-0000-0000-0000-000000000001', 'project_id', (select id from projects where code = 'PA'),
  'report_date', '2026-10-20', 'shift', 'day', 'work_completed', 'Receipt',
  'materials', jsonb_build_array(jsonb_build_object('material_id', (select id from materials where code = 'CEM'), 'received_qty', 100))));
select public.save_dpr(jsonb_build_object('id', '10000000-0000-0000-0000-000000000002', 'project_id', (select id from projects where code = 'PA'),
  'report_date', '2026-10-21', 'shift', 'day', 'work_completed', 'Issue',
  'materials', jsonb_build_array(jsonb_build_object('material_id', (select id from materials where code = 'CEM'), 'issued_qty', 80))));
select pg_temp.ok(true, 'Stock: a draft may issue more than approved stock while the receipt is pending');
select public.submit_dpr('10000000-0000-0000-0000-000000000002');
select public.submit_dpr('10000000-0000-0000-0000-000000000001');

select pg_temp.fails($$select public.review_dpr('10000000-0000-0000-0000-000000000002', 'approve')$$, 'exceeds approved stock',
  'Stock: approving the issue before the receipt is refused');
select public.review_dpr('10000000-0000-0000-0000-000000000001', 'approve');
select public.review_dpr('10000000-0000-0000-0000-000000000002', 'approve');
select pg_temp.ok((select opening_qty = 100 and closing_qty = 20 from material_transactions where dpr_id = '10000000-0000-0000-0000-000000000002'),
  'Stock: after the receipt is approved, the issue is approved with opening 100 and closing 20');
select pg_temp.ok((select stock_qty = 20 from material_stock where code = 'CEM'), 'Stock: store balance is 20 bags');
reset role;
