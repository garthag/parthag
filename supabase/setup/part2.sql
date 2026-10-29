-- CONSTRUCT DPR database setup, PART 2 of 2 (run after part 1)
-- GENERATED from supabase/migrations by supabase/setup/build.sh. Do not edit.
-- Paste the whole file into Supabase > SQL Editor > New query, then press Run.


-- ===== 20261029000010_integrations.sql =====
-- CONSTRUCT DPR — Migration 10: integration layer for other software
-- (ERP/SAP, Python or Java services, BI tools, spreadsheets).
--
-- * `integration` role: an API account per external system. It can read and
--   export everything and keep masters/schedule/BOQ in sync, but can never
--   approve, reject or submit reports.
-- * `integration_events`: an append-only, ordered event feed. Consumers keep
--   the last `id` they processed and ask for `id > cursor`, so no event is
--   missed or delivered out of order, even after downtime.

with matrix (module, letters) as (
  values ('project','vx'), ('members','v'), ('masters','cvex'), ('schedule','cvex'), ('boq','cvex'),
         ('commercial','vx'), ('dpr','vx'), ('resources','vx'), ('safety','vx'), ('quality','vx'),
         ('documents','vx'), ('reports','vx')
),
letters (l, action) as (
  values ('c','create'), ('v','view'), ('e','edit'), ('s','submit'),
         ('a','approve'), ('r','reject'), ('x','export'), ('d','delete')
)
insert into public.role_permissions (role, module, action)
select 'integration'::public.app_role, m.module, l.action
from matrix m join letters l on position(l.l in m.letters) > 0
on conflict do nothing;

create table public.integration_events (
  id bigint generated always as identity primary key,   -- the consumer's cursor
  project_id uuid not null references public.projects (id) on delete cascade,
  event_type text not null,
  entity text not null,
  entity_id uuid,
  actor_id uuid,
  occurred_at timestamptz not null default now(),
  payload jsonb not null default '{}'
);
create index on public.integration_events (project_id, id);
create trigger integration_events_append_only before update or delete on public.integration_events
  for each row execute function public.forbid_change();

create or replace function public.emit_event(p_project uuid, p_type text, p_entity text, p_entity_id uuid, p_payload jsonb)
returns void language sql security definer set search_path = public as $$
  insert into integration_events (project_id, event_type, entity, entity_id, actor_id, payload)
  values (p_project, p_type, p_entity, p_entity_id, auth.uid(), coalesce(p_payload, '{}'));
$$;

-- DPR lifecycle: dpr.submitted / dpr.under_review / dpr.approved / dpr.rejected / dpr.revision_started
create or replace function public.events_dpr() returns trigger
language plpgsql security definer set search_path = public as $$
declare base jsonb;
begin
  base := jsonb_build_object('dpr_no', new.dpr_no, 'revision', new.revision, 'report_date', new.report_date,
                             'shift', new.shift, 'status', new.status);
  if tg_op = 'INSERT' and new.revision > 0 then
    perform emit_event(new.project_id, 'dpr.revision_started', 'dpr', new.id,
                       base || jsonb_build_object('previous_revision_id', new.previous_revision_id, 'reason', new.revision_reason));
  elsif tg_op = 'UPDATE' and new.status is distinct from old.status and new.status <> 'draft' then
    perform emit_event(new.project_id, 'dpr.' || new.status, 'dpr', new.id, base || jsonb_build_object('from_status', old.status));
  end if;
  return null;
end $$;
create trigger integration_events after insert or update of status on public.dpr_headers
  for each row execute function public.events_dpr();

-- Approved snapshots and published PDFs
create or replace function public.events_report() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  if tg_op = 'INSERT' then
    perform emit_event(new.project_id, 'report.snapshot_created', 'report_revision', new.id,
      jsonb_build_object('dpr_id', new.dpr_id, 'dpr_no', new.dpr_no, 'revision', new.revision,
                         'report_date', new.report_date, 'snapshot_sha256', new.snapshot_sha256));
  elsif new.pdf_drive_file_id is not null and old.pdf_drive_file_id is null then
    perform emit_event(new.project_id, 'report.pdf_published', 'report_revision', new.id,
      jsonb_build_object('dpr_id', new.dpr_id, 'dpr_no', new.dpr_no, 'revision', new.revision,
                         'drive_file_id', new.pdf_drive_file_id, 'pdf_sha256', new.pdf_sha256));
  end if;
  return null;
end $$;
create trigger integration_events after insert or update on public.report_revisions
  for each row execute function public.events_report();

-- Commercial and planning changes
create or replace function public.events_misc() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  if tg_table_name = 'boq_revisions' then
    perform emit_event(new.project_id, 'boq.revised', 'boq_item', new.boq_item_id,
      jsonb_build_object('item_no', (select item_no from boq_items where id = new.boq_item_id),
                         'old_qty', new.old_qty, 'new_qty', new.new_qty, 'old_rate', new.old_rate,
                         'new_rate', new.new_rate, 'reason', new.reason));
  elsif tg_table_name = 'baseline_revisions' then
    perform emit_event(new.project_id, 'baseline.frozen', 'baseline', new.id,
      jsonb_build_object('revision_no', new.revision_no, 'name', new.name));
  elsif tg_table_name = 'activities' then
    if tg_op = 'INSERT' then
      perform emit_event(new.project_id, 'activity.created', 'activity', new.id, jsonb_build_object('code', new.code, 'name', new.name));
    elsif new.status is distinct from old.status then
      perform emit_event(new.project_id, 'activity.status_changed', 'activity', new.id,
        jsonb_build_object('code', new.code, 'from', old.status, 'to', new.status,
                           'actual_start', new.actual_start, 'actual_finish', new.actual_finish));
    end if;
  elsif tg_table_name in ('quality_ncrs', 'safety_observations') then
    -- Draft DPR saves rewrite these rows, so only direct register entries and
    -- status changes are published; DPR-raised items arrive with dpr.approved.
    if (tg_op = 'INSERT' and new.dpr_id is null) or (tg_op = 'UPDATE' and new.status is distinct from old.status) then
      perform emit_event(new.project_id,
        (case tg_table_name when 'quality_ncrs' then 'ncr' else 'safety' end) || '.' || (case when tg_op = 'INSERT' then 'opened' else new.status end),
        tg_table_name, new.id, jsonb_build_object('description', left(new.description, 200), 'status', new.status));
    end if;
  end if;
  return null;
end $$;
create trigger integration_events after insert on public.boq_revisions for each row execute function public.events_misc();
create trigger integration_events after insert on public.baseline_revisions for each row execute function public.events_misc();
create trigger integration_events after insert or update of status on public.activities for each row execute function public.events_misc();
create trigger integration_events after insert or update of status on public.quality_ncrs for each row execute function public.events_misc();
create trigger integration_events after insert or update of status on public.safety_observations for each row execute function public.events_misc();

alter table public.integration_events enable row level security;
grant select on public.integration_events to authenticated;
create policy read on public.integration_events for select to authenticated
  using (public.has_perm(project_id, 'reports', 'export') or public.has_perm(project_id, 'project', 'export'));

-- Live push for consumers that prefer websockets over polling.
alter publication supabase_realtime add table public.integration_events;

revoke execute on function public.emit_event(uuid, text, text, uuid, jsonb) from public, anon, authenticated;
revoke execute on function public.events_dpr(), public.events_report(), public.events_misc() from public, anon, authenticated;
