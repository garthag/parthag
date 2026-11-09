-- CONSTRUCT DPR — Migration 5: authorisation helpers, reporting views,
-- grants and Row Level Security.
--
-- Principle: the browser is never trusted. Every table has RLS enabled.
-- Workflow tables (DPRs, approvals, snapshots, baselines, memberships) have
-- NO direct write policies — they can only be changed through the
-- security-definer functions in migration 6, which validate everything.

-- ---------------------------------------------------------------------------
-- Authorisation helpers
-- ---------------------------------------------------------------------------
create or replace function public.is_org_admin(p_org uuid) returns boolean
language sql stable security definer set search_path = public as $$
  select exists (select 1 from organization_members
                 where organization_id = p_org and user_id = auth.uid() and role = 'org_admin');
$$;

create or replace function public.is_org_member(p_org uuid) returns boolean
language sql stable security definer set search_path = public as $$
  select exists (select 1 from organization_members
                 where organization_id = p_org and user_id = auth.uid());
$$;

create or replace function public.has_perm(p_project uuid, p_module text, p_action text) returns boolean
language sql stable security definer set search_path = public as $$
  select auth.uid() is not null and (
    exists (select 1 from projects p join organization_members om
              on om.organization_id = p.organization_id
             where p.id = p_project and om.user_id = auth.uid() and om.role = 'org_admin')
    or exists (
      select 1 from project_members pm
      where pm.project_id = p_project and pm.user_id = auth.uid() and pm.active
        and coalesce(
              (select o.allowed from project_role_permissions o
                where o.project_id = pm.project_id and o.role = pm.role
                  and o.module = p_module and o.action = p_action),
              exists (select 1 from role_permissions rp
                       where rp.role = pm.role and rp.module = p_module and rp.action = p_action))));
$$;

create or replace function public.is_project_member(p_project uuid) returns boolean
language sql stable security definer set search_path = public as $$
  select public.has_perm(p_project, 'project', 'view')
      or exists (select 1 from project_members where project_id = p_project and user_id = auth.uid() and active);
$$;

create or replace function public.is_project_admin(p_project uuid) returns boolean
language sql stable security definer set search_path = public as $$
  select exists (select 1 from projects p join organization_members om on om.organization_id = p.organization_id
                  where p.id = p_project and om.user_id = auth.uid() and om.role = 'org_admin')
      or exists (select 1 from project_members
                  where project_id = p_project and user_id = auth.uid() and active and role = 'project_admin');
$$;

-- Everything the current user may do in a project (used by the UI to hide
-- buttons; the database still enforces every rule independently).
create or replace function public.my_permissions(p_project uuid)
returns table (module text, action text)
language sql stable security definer set search_path = public as $$
  select m.module, a.action
  from (values ('project'),('members'),('masters'),('schedule'),('boq'),('commercial'),('dpr'),
               ('resources'),('safety'),('quality'),('documents'),('reports')) m(module)
  cross join (values ('create'),('view'),('edit'),('submit'),('approve'),('reject'),('export'),('delete')) a(action)
  where public.has_perm(p_project, m.module, a.action);
$$;

-- ---------------------------------------------------------------------------
-- Reporting views (security_invoker => RLS of the caller applies)
-- ---------------------------------------------------------------------------

-- The effective DPR for each serial number is its latest APPROVED revision.
-- Earlier approved revisions remain stored untouched for audit.
create view public.dpr_effective with (security_invoker = true) as
select distinct on (h.project_id, h.seq) h.*
from public.dpr_headers h
where h.status = 'approved'
order by h.project_id, h.seq, h.revision desc;

create view public.dpr_effective_entries with (security_invoker = true) as
select e.*, d.report_date, d.shift, d.dpr_no, d.revision
from public.dpr_activity_entries e
join public.dpr_effective d on d.id = e.dpr_id;

create view public.activity_progress with (security_invoker = true) as
select a.id as activity_id, a.project_id, a.code, a.name, a.unit, a.total_qty, a.weight,
       coalesce(sum(coalesce(e.qty_accepted, e.qty_today)), 0) as accepted_cum_qty,
       case when a.total_qty > 0
            then least(round(coalesce(sum(coalesce(e.qty_accepted, e.qty_today)), 0) * 100 / a.total_qty, 3), 999)
       end as progress_pct,
       max(e.report_date) as last_reported_on
from public.activities a
left join public.dpr_effective_entries e on e.activity_id = a.id
group by a.id;

create view public.boq_status with (security_invoker = true) as
select b.*,
       coalesce(b.revised_qty, b.tender_qty) as approved_qty,
       round(b.tender_qty * b.rate, 2) as tender_amount,
       round(coalesce(b.revised_qty, b.tender_qty) * b.rate, 2) as contract_amount,
       coalesce(x.executed_qty, 0) as executed_qty,
       round(coalesce(x.executed_qty, 0) * b.rate, 2) as executed_amount,
       round(b.certified_qty * b.rate, 2) as certified_amount,
       round(b.billed_qty * b.rate, 2) as billed_amount,
       coalesce(b.revised_qty, b.tender_qty) - coalesce(x.executed_qty, 0) as balance_qty,
       coalesce(b.revised_qty, b.tender_qty) - b.tender_qty as variation_qty,
       round((coalesce(b.revised_qty, b.tender_qty) - b.tender_qty) * b.rate, 2) as variation_amount
from public.boq_items b
left join (select boq_item_id, sum(coalesce(qty_accepted, qty_today)) as executed_qty
             from public.dpr_effective_entries where boq_item_id is not null group by boq_item_id) x
  on x.boq_item_id = b.id;

create view public.manpower_daily with (security_invoker = true) as
select m.project_id, d.report_date, d.shift, m.trade, m.contractor_id, m.area_id,
       sum(m.headcount) as headcount, sum(m.man_hours) as man_hours, sum(m.absent) as absent,
       sum(m.overtime_hours) as overtime_hours
from public.manpower_entries m join public.dpr_effective d on d.id = m.dpr_id
group by m.project_id, d.report_date, d.shift, m.trade, m.contractor_id, m.area_id;

create view public.equipment_daily with (security_invoker = true) as
select q.project_id, d.report_date, q.equipment_code, q.equipment_type,
       sum(q.operating_hours) as operating_hours, sum(q.idle_hours) as idle_hours,
       sum(q.breakdown_hours) as breakdown_hours
from public.equipment_entries q join public.dpr_effective d on d.id = q.dpr_id
group by q.project_id, d.report_date, q.equipment_code, q.equipment_type;

-- Store ledger rows that count: stand-alone store entries plus rows of
-- effective (approved) DPRs. Drafts and rejected reports do not move stock.
create view public.material_ledger with (security_invoker = true) as
select t.*
from public.material_transactions t
left join public.dpr_effective d on d.id = t.dpr_id
where t.dpr_id is null or d.id is not null;

-- Latest store position per material.
create view public.material_stock with (security_invoker = true) as
select m.id as material_id, m.project_id, m.code, m.description, m.unit, m.reorder_level,
       t.txn_date as last_txn_date, coalesce(t.closing_qty, 0) as stock_qty,
       (m.reorder_level is not null and coalesce(t.closing_qty, 0) < m.reorder_level) as below_reorder
from public.materials m
left join lateral (
  select l.txn_date, l.closing_qty from public.material_ledger l
  where l.material_id = m.id
  order by l.txn_date desc, l.sort_order desc limit 1) t on true;

-- ---------------------------------------------------------------------------
-- Grants
-- ---------------------------------------------------------------------------
revoke all on all tables in schema public from anon;
grant usage on schema public to authenticated;
grant select, insert, update, delete on all tables in schema public to authenticated;
revoke insert, update, delete on public.audit_events, public.role_permissions from authenticated;
revoke execute on all functions in schema public from public, anon;
grant execute on all functions in schema public to authenticated;

-- ---------------------------------------------------------------------------
-- Row Level Security
-- ---------------------------------------------------------------------------
do $$
declare t text;
begin
  for t in select tablename from pg_tables where schemaname = 'public' loop
    execute format('alter table public.%I enable row level security', t);
  end loop;
end $$;

-- Catalogue / identity
create policy read_catalogue on public.role_permissions for select to authenticated using (true);

create policy own_profile on public.profiles for select to authenticated using (
  id = auth.uid()
  or exists (select 1 from project_members a join project_members b on a.project_id = b.project_id
              where a.user_id = auth.uid() and b.user_id = profiles.id)
  or exists (select 1 from organization_members a join organization_members b on a.organization_id = b.organization_id
              where a.user_id = auth.uid() and b.user_id = profiles.id));
create policy update_own_profile on public.profiles for update to authenticated
  using (id = auth.uid()) with check (id = auth.uid());

create policy org_read on public.organizations for select to authenticated using (public.is_org_member(id));
create policy org_update on public.organizations for update to authenticated using (public.is_org_admin(id));
create policy orgm_read on public.organization_members for select to authenticated using (public.is_org_member(organization_id));

-- Organisation-scoped masters
create policy read on public.clients for select to authenticated using (public.is_org_member(organization_id));
create policy write on public.clients for insert to authenticated with check (public.is_org_member(organization_id));
create policy edit on public.clients for update to authenticated using (public.is_org_member(organization_id));
create policy read on public.contractors for select to authenticated using (public.is_org_member(organization_id));
create policy write on public.contractors for insert to authenticated with check (public.is_org_member(organization_id));
create policy edit on public.contractors for update to authenticated using (public.is_org_member(organization_id));
create policy read on public.contracts for select to authenticated using (public.is_org_member(organization_id));
create policy write on public.contracts for insert to authenticated with check (public.is_org_admin(organization_id));
create policy edit on public.contracts for update to authenticated using (public.is_org_admin(organization_id));

-- Projects and membership
create policy read on public.projects for select to authenticated using (public.has_perm(id, 'project', 'view'));
create policy edit on public.projects for update to authenticated
  using (public.has_perm(id, 'project', 'edit')) with check (public.has_perm(id, 'project', 'edit'));
create policy read on public.project_members for select to authenticated
  using (user_id = auth.uid() or public.has_perm(project_id, 'members', 'view'));
create policy read on public.project_role_permissions for select to authenticated using (public.is_project_member(project_id));
create policy admin_write on public.project_role_permissions for all to authenticated
  using (public.is_project_admin(project_id)) with check (public.is_project_admin(project_id));

-- Generic project-scoped CRUD policies driven by the module matrix.
create or replace function pg_temp.crud(tbl text, module text, allow_delete boolean default true) returns void
language plpgsql as $$
begin
  execute format('create policy read on public.%I for select to authenticated using (public.has_perm(project_id, %L, ''view''))', tbl, module);
  execute format('create policy ins on public.%I for insert to authenticated with check (public.has_perm(project_id, %L, ''create''))', tbl, module);
  execute format('create policy upd on public.%I for update to authenticated using (public.has_perm(project_id, %L, ''edit'')) with check (public.has_perm(project_id, %L, ''edit''))', tbl, module, module);
  if allow_delete then
    execute format('create policy del on public.%I for delete to authenticated using (public.has_perm(project_id, %L, ''delete''))', tbl, module);
  end if;
end $$;

create or replace function pg_temp.read_only(tbl text, module text) returns void
language plpgsql as $$
begin
  execute format('create policy read on public.%I for select to authenticated using (public.has_perm(project_id, %L, ''view''))', tbl, module);
end $$;

select pg_temp.crud('sites', 'masters');
select pg_temp.crud('areas', 'masters');
select pg_temp.crud('wbs_nodes', 'masters');
select pg_temp.crud('activities', 'masters');
select pg_temp.crud('equipment', 'masters');
select pg_temp.crud('materials', 'masters');
select pg_temp.crud('project_calendars', 'schedule');
select pg_temp.crud('activity_dependencies', 'schedule');
select pg_temp.crud('change_orders', 'commercial', false);
select pg_temp.crud('safety_observations', 'safety');
select pg_temp.crud('inspection_records', 'quality');
select pg_temp.crud('quality_ncrs', 'quality');
select pg_temp.crud('constraints', 'dpr', false);
select pg_temp.crud('action_items', 'dpr', false);
select pg_temp.crud('fabrication_items', 'masters');
select pg_temp.crud('dispatch_records', 'masters');
select pg_temp.crud('erection_records', 'masters');
select pg_temp.crud('commissioning_checklists', 'quality');

-- BOQ quantities and rates change only through revise_boq_item(); measurement /
-- certification / billing fields are editable by users with boq.edit.
select pg_temp.read_only('boq_items', 'boq');
create policy ins on public.boq_items for insert to authenticated with check (public.has_perm(project_id, 'boq', 'create'));
create policy upd on public.boq_items for update to authenticated
  using (public.has_perm(project_id, 'boq', 'edit')) with check (public.has_perm(project_id, 'boq', 'edit'));
select pg_temp.read_only('boq_revisions', 'boq');

-- Workflow tables: read only through RLS; writes go through functions.
select pg_temp.read_only('baseline_revisions', 'schedule');
select pg_temp.read_only('baseline_activity_dates', 'schedule');
select pg_temp.read_only('dpr_headers', 'dpr');
select pg_temp.read_only('dpr_activity_entries', 'dpr');
select pg_temp.read_only('manpower_entries', 'dpr');
select pg_temp.read_only('equipment_entries', 'dpr');
select pg_temp.read_only('material_transactions', 'dpr');
select pg_temp.read_only('approval_steps', 'dpr');
select pg_temp.read_only('approval_actions', 'dpr');
select pg_temp.read_only('report_revisions', 'dpr');
select pg_temp.read_only('drive_folders', 'documents');

create policy read on public.attachments for select to authenticated using (public.has_perm(project_id, 'documents', 'view'));
create policy upd on public.attachments for update to authenticated
  using (public.has_perm(project_id, 'documents', 'edit') or (created_by = auth.uid() and upload_status <> 'uploaded'))
  with check (public.has_perm(project_id, 'documents', 'edit') or created_by = auth.uid());

create policy own_jobs on public.synchronization_jobs for select to authenticated using (user_id = auth.uid());
create policy read on public.audit_events for select to authenticated
  using (project_id is not null and public.has_perm(project_id, 'reports', 'view'));

-- The BOQ quantity/rate guard: direct updates may not alter contract fields.
create or replace function public.guard_boq_contract_fields() returns trigger
language plpgsql as $$
begin
  if current_setting('cdpr.boq_revision', true) = 'on' then
    return new;
  end if;
  if new.tender_qty is distinct from old.tender_qty or new.revised_qty is distinct from old.revised_qty
     or new.rate is distinct from old.rate then
    raise exception 'BOQ quantity/rate changes must go through revise_boq_item() with a reason';
  end if;
  return new;
end $$;
create trigger guard_contract_fields before update on public.boq_items
  for each row execute function public.guard_boq_contract_fields();
