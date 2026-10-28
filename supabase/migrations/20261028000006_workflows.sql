-- CONSTRUCT DPR — Migration 6: server-side workflow functions (RPC).
--
-- These run with the table owner's rights (security definer) so they can
-- write to tables that have no direct write policy — therefore EVERY
-- function checks the caller's permission itself before doing anything.
-- Errors use SQLSTATE PT4xx so PostgREST returns the matching HTTP status.

-- ---------------------------------------------------------------------------
-- Small helpers
-- ---------------------------------------------------------------------------
create or replace function public.require_perm(p_project uuid, p_module text, p_action text) returns void
language plpgsql stable security definer set search_path = public as $$
begin
  if auth.uid() is null then
    raise exception 'Not signed in' using errcode = 'PT401';
  end if;
  if not public.has_perm(p_project, p_module, p_action) then
    raise exception 'You do not have permission to % % in this project', p_action, p_module using errcode = 'PT403';
  end if;
end $$;

create or replace function public.sha256_hex(p text) returns text
language sql immutable as $$ select encode(digest(p, 'sha256'), 'hex') $$;

-- Linear planned progress (%) of an activity on a date, from the approved
-- baseline when one exists, otherwise from the current planned dates.
create or replace function public.planned_progress_pct(p_activity uuid, p_date date) returns numeric
language plpgsql stable security definer set search_path = public as $$
declare s date; f date;
begin
  select coalesce(b.start_date, a.planned_start), coalesce(b.finish_date, a.planned_finish)
    into s, f
  from activities a
  join projects p on p.id = a.project_id
  left join baseline_activity_dates b on b.baseline_id = p.approved_baseline_id and b.activity_id = a.id
  where a.id = p_activity;
  if s is null or f is null then return null; end if;
  if p_date < s then return 0; end if;
  if p_date >= f then return 100; end if;
  return round(((p_date - s + 1)::numeric * 100) / (f - s + 1), 3);
end $$;

-- Accepted cumulative quantity of each activity in DPRs that come before the
-- given position (date, serial). Only effective (latest approved) revisions count.
create or replace function public.prior_accepted_qty(p_project uuid, p_date date, p_seq int)
returns table (activity_id uuid, qty numeric)
language sql stable security definer set search_path = public as $$
  select e.activity_id, sum(coalesce(e.qty_accepted, e.qty_today))
  from dpr_effective_entries e
  join dpr_headers h on h.id = e.dpr_id
  where e.project_id = p_project
    and h.seq <> coalesce(p_seq, -1)
    and (h.report_date < p_date or (h.report_date = p_date and h.seq < coalesce(p_seq, 2147483647)))
  group by e.activity_id;
$$;

-- Read-only context for the DPR form (previous cumulative, planned %).
create or replace function public.dpr_activity_context(p_project uuid, p_date date, p_seq int default null)
returns table (activity_id uuid, prev_cum_qty numeric, planned_progress_pct numeric)
language plpgsql stable security definer set search_path = public as $$
begin
  perform public.require_perm(p_project, 'dpr', 'view');
  return query
  select a.id, coalesce(pq.qty, 0), public.planned_progress_pct(a.id, p_date)
  from activities a
  left join public.prior_accepted_qty(p_project, p_date, p_seq) pq on pq.activity_id = a.id
  where a.project_id = p_project;
end $$;

-- Recalculate previous/cumulative/balance/progress for all entries of a DPR.
-- p_use_accepted: true when approving (cumulative built from accepted qty).
create or replace function public.refresh_dpr_entries(p_dpr uuid, p_use_accepted boolean default false) returns void
language plpgsql security definer set search_path = public as $$
declare h dpr_headers;
begin
  select * into h from dpr_headers where id = p_dpr;
  update dpr_activity_entries e set
    approved_total_qty = s.total_qty,
    unit = coalesce(e.unit, s.unit),
    prev_cum_qty = s.prev,
    planned_progress_pct = public.planned_progress_pct(s.id, h.report_date)
  from (select a.id, a.total_qty, a.unit, coalesce(pq.qty, 0) as prev
          from activities a
          left join public.prior_accepted_qty(h.project_id, h.report_date, h.seq) pq on pq.activity_id = a.id
         where a.project_id = h.project_id) s
  where e.dpr_id = p_dpr and s.id = e.activity_id;

  update dpr_activity_entries e set
    cum_qty = e.prev_cum_qty + case when p_use_accepted then coalesce(e.qty_accepted, e.qty_today) else e.qty_today end,
    balance_qty = e.approved_total_qty - e.prev_cum_qty
                  - case when p_use_accepted then coalesce(e.qty_accepted, e.qty_today) else e.qty_today end
  where e.dpr_id = p_dpr;

  -- Zero approved quantity => no quantity-based percentage (never divide by zero).
  update dpr_activity_entries e set
    actual_progress_pct = case
      when e.progress_method = 'manual' then e.manual_progress_pct
      when e.approved_total_qty > 0 then round(e.cum_qty * 100 / e.approved_total_qty, 3)
      end
  where e.dpr_id = p_dpr;
  update dpr_activity_entries set variance_pct = actual_progress_pct - planned_progress_pct
   where dpr_id = p_dpr;
end $$;

-- Entries whose cumulative quantity exceeds the approved quantity without an
-- authorised overrun record.
create or replace function public.dpr_overruns(p_dpr uuid) returns text
language sql stable security definer set search_path = public as $$
  select string_agg(a.code || ' (' || e.cum_qty::text || ' > ' || e.approved_total_qty::text || ')', ', ')
  from dpr_activity_entries e join activities a on a.id = e.activity_id
  where e.dpr_id = p_dpr and e.progress_method = 'quantity'
    and e.cum_qty > e.approved_total_qty and not e.overrun_authorized;
$$;

-- ---------------------------------------------------------------------------
-- Organisation, project and membership
-- ---------------------------------------------------------------------------
create or replace function public.create_organization(p_name text) returns uuid
language plpgsql security definer set search_path = public as $$
declare v uuid;
begin
  if auth.uid() is null then raise exception 'Not signed in' using errcode = 'PT401'; end if;
  insert into organizations (name) values (trim(p_name)) returning id into v;
  insert into organization_members (organization_id, user_id, role) values (v, auth.uid(), 'org_admin');
  return v;
end $$;

create or replace function public.create_project(p_org uuid, p jsonb) returns uuid
language plpgsql security definer set search_path = public as $$
declare v uuid;
begin
  if not public.is_org_admin(p_org) then
    raise exception 'Only organisation administrators can create projects' using errcode = 'PT403';
  end if;
  insert into projects (organization_id, code, name, client_id, consultant, work_order_no, site_address,
                        start_date, completion_date, timezone, currency, report_header, report_footer)
  values (p_org, upper(trim(p->>'code')), trim(p->>'name'), nullif(p->>'client_id', '')::uuid, p->>'consultant',
          p->>'work_order_no', p->>'site_address', nullif(p->>'start_date', '')::date,
          nullif(p->>'completion_date', '')::date, coalesce(nullif(p->>'timezone', ''), 'Asia/Kolkata'),
          coalesce(nullif(p->>'currency', ''), 'INR'), p->>'report_header', p->>'report_footer')
  returning id into v;
  insert into project_members (project_id, user_id, role) values (v, auth.uid(), 'project_admin');
  insert into project_calendars (project_id, name, is_default) values (v, 'Standard 6-day', true);
  insert into approval_steps (project_id, step_no, name) values (v, 1, 'Project approval');
  return v;
end $$;

create or replace function public.add_project_member(p_project uuid, p_email text, p_role public.app_role) returns uuid
language plpgsql security definer set search_path = public as $$
declare v_user uuid; v uuid;
begin
  perform public.require_perm(p_project, 'members', 'create');
  if p_role = 'org_admin' then
    raise exception 'org_admin is an organisation role, not a project role' using errcode = 'PT400';
  end if;
  if p_role = 'project_admin' and not public.is_project_admin(p_project) then
    raise exception 'Only project administrators can appoint project administrators' using errcode = 'PT403';
  end if;
  select id into v_user from profiles where lower(email) = lower(trim(p_email));
  if v_user is null then
    raise exception 'No user with e-mail %. Ask them to sign in once first.', p_email using errcode = 'PT404';
  end if;
  insert into organization_members (organization_id, user_id)
  select organization_id, v_user from projects where id = p_project
  on conflict do nothing;
  insert into project_members (project_id, user_id, role) values (p_project, v_user, p_role)
  on conflict (project_id, user_id, role) do update set active = true
  returning id into v;
  return v;
end $$;

create or replace function public.set_project_member_active(p_member uuid, p_active boolean) returns void
language plpgsql security definer set search_path = public as $$
declare m project_members;
begin
  select * into m from project_members where id = p_member;
  if m.id is null then raise exception 'Member not found' using errcode = 'PT404'; end if;
  perform public.require_perm(m.project_id, 'members', 'delete');
  if m.role = 'project_admin' and not public.is_project_admin(m.project_id) then
    raise exception 'Only project administrators can change administrators' using errcode = 'PT403';
  end if;
  update project_members set active = p_active where id = p_member;
end $$;

-- ---------------------------------------------------------------------------
-- DPR: save (create/update draft), submit, review, revise
-- ---------------------------------------------------------------------------
create or replace function public.save_dpr(p jsonb) returns jsonb
language plpgsql security definer set search_path = public as $$
declare
  v_key uuid := nullif(p->>'idempotency_key', '')::uuid;
  v_project uuid := (p->>'project_id')::uuid;
  v_id uuid := coalesce(nullif(p->>'id', '')::uuid, gen_random_uuid());
  v_expected int := nullif(p->>'expected_version', '')::int;
  v_date date := (p->>'report_date')::date;
  h dpr_headers;
  v_code text;
  v_seq int;
  v_result jsonb;
  v_bad text;
begin
  if auth.uid() is null then raise exception 'Not signed in' using errcode = 'PT401'; end if;
  if v_project is null or v_date is null then
    raise exception 'project_id and report_date are required' using errcode = 'PT400';
  end if;
  if v_date > (now() at time zone (select timezone from projects where id = v_project))::date + 1 then
    raise exception 'Report date % is in the future', v_date using errcode = 'PT400';
  end if;

  -- Idempotency: a replay of the same queued request returns the first result.
  if v_key is not null then
    select result into v_result from synchronization_jobs where idempotency_key = v_key;
    if found then return v_result || jsonb_build_object('replayed', true); end if;
  end if;

  select * into h from dpr_headers where id = v_id for update;
  if found then
    if h.project_id <> v_project then raise exception 'DPR belongs to another project' using errcode = 'PT400'; end if;
    if h.status not in ('draft', 'rejected') then
      raise exception 'DPR % is % and can no longer be edited', h.dpr_no, h.status using errcode = 'PT409';
    end if;
    if not (public.has_perm(v_project, 'dpr', 'edit') or (h.prepared_by = auth.uid() and public.has_perm(v_project, 'dpr', 'create'))) then
      raise exception 'You cannot edit this DPR' using errcode = 'PT403';
    end if;
    if v_expected is not null and v_expected <> h.version then
      raise exception 'CONFLICT: DPR % was changed by someone else (your version %, server version %). Reload and merge.',
        h.dpr_no, v_expected, h.version using errcode = 'PT409';
    end if;
  else
    perform public.require_perm(v_project, 'dpr', 'create');
    perform pg_advisory_xact_lock(hashtext('dpr_seq:' || v_project::text));
    select code into v_code from projects where id = v_project;
    select coalesce(max(seq), 0) + 1 into v_seq from dpr_headers where project_id = v_project;
    begin
      insert into dpr_headers (id, project_id, seq, dpr_no, report_date, shift, prepared_by, idempotency_key)
      values (v_id, v_project, v_seq, v_code || '-DPR-' || lpad(v_seq::text, 4, '0'), v_date,
              coalesce(nullif(p->>'shift', ''), 'day'), auth.uid(), v_key);
    exception when unique_violation then
      raise exception 'A DPR already exists for % (% shift) in this area. Open it instead of creating a duplicate.',
        v_date, coalesce(nullif(p->>'shift', ''), 'day') using errcode = 'PT409';
    end;
  end if;

  update dpr_headers set
    report_date = v_date,
    shift = coalesce(nullif(p->>'shift', ''), 'day'),
    work_start = nullif(p->>'work_start', '')::time, work_end = nullif(p->>'work_end', '')::time,
    weather = p->>'weather', site_conditions = p->>'site_conditions',
    site_id = nullif(p->>'site_id', '')::uuid, area_id = nullif(p->>'area_id', '')::uuid,
    toolbox_talks = coalesce(nullif(p->>'toolbox_talks', '')::int, 0), toolbox_topics = p->>'toolbox_topics',
    permits_issued = coalesce(nullif(p->>'permits_issued', '')::int, 0),
    work_completed = p->>'work_completed', achievements = p->>'achievements',
    work_not_completed = p->>'work_not_completed', delay_reasons = p->>'delay_reasons',
    weather_interruptions = p->>'weather_interruptions', material_shortages = p->>'material_shortages',
    equipment_constraints = p->>'equipment_constraints', labour_constraints = p->>'labour_constraints',
    design_issues = p->>'design_issues', client_instructions = p->>'client_instructions',
    decisions_required = p->>'decisions_required', tomorrow_plan = p->>'tomorrow_plan',
    required_manpower = p->>'required_manpower', required_equipment = p->>'required_equipment',
    required_materials = p->>'required_materials', priority_actions = p->>'priority_actions'
  where id = v_id;

  -- Replace child sections (the draft is edited as a whole document).
  delete from dpr_activity_entries where dpr_id = v_id;
  insert into dpr_activity_entries (dpr_id, project_id, activity_id, boq_item_id, contractor_id, location,
    actual_start, actual_finish, qty_today, qty_inspected, progress_method, manual_progress_pct,
    overrun_authorized, overrun_reason, delay_reason, constraints, next_day_target, responsible, remarks, sort_order)
  select v_id, v_project, r.activity_id, coalesce(r.boq_item_id, a.boq_item_id), coalesce(r.contractor_id, a.contractor_id),
         coalesce(r.location, a.location), r.actual_start, r.actual_finish, coalesce(r.qty_today, 0), r.qty_inspected,
         coalesce(r.progress_method, 'quantity'), r.manual_progress_pct, coalesce(r.overrun_authorized, false),
         nullif(r.overrun_reason, ''), r.delay_reason, r.constraints, r.next_day_target, r.responsible, r.remarks, coalesce(r.sort_order, 0)
  from jsonb_populate_recordset(null::dpr_activity_entries, coalesce(p->'activities', '[]')) r
  left join activities a on a.id = r.activity_id and a.project_id = v_project;

  select string_agg(coalesce(e.activity_id::text, 'null'), ', ') into v_bad
  from dpr_activity_entries e left join activities a on a.id = e.activity_id
  where e.dpr_id = v_id and (a.project_id is distinct from v_project);
  if v_bad is not null then raise exception 'Activities not in this project: %', v_bad using errcode = 'PT400'; end if;

  delete from manpower_entries where dpr_id = v_id;
  insert into manpower_entries (dpr_id, project_id, contractor_id, area_id, trade, shift, skilled, semi_skilled,
    unskilled, supervisors, engineers, safety_staff, others, absent, hours_per_person, overtime_hours, remarks, sort_order)
  select v_id, v_project, r.contractor_id, r.area_id, r.trade, coalesce(r.shift, 'day'), coalesce(r.skilled, 0),
         coalesce(r.semi_skilled, 0), coalesce(r.unskilled, 0), coalesce(r.supervisors, 0), coalesce(r.engineers, 0),
         coalesce(r.safety_staff, 0), coalesce(r.others, 0), coalesce(r.absent, 0), coalesce(r.hours_per_person, 8),
         coalesce(r.overtime_hours, 0), r.remarks, coalesce(r.sort_order, 0)
  from jsonb_populate_recordset(null::manpower_entries, coalesce(p->'manpower', '[]')) r;

  delete from equipment_entries where dpr_id = v_id;
  insert into equipment_entries (dpr_id, project_id, equipment_id, equipment_code, equipment_type, owner,
    rated_capacity, location, operator, operating_hours, idle_hours, breakdown_hours, fuel_consumed, fuel_unit,
    certification_expiry, breakdown_reason, remarks, sort_order)
  select v_id, v_project, eq.id, coalesce(r.equipment_code, eq.code), coalesce(r.equipment_type, eq.type), r.owner,
         coalesce(r.rated_capacity, eq.rated_capacity), r.location, r.operator, coalesce(r.operating_hours, 0),
         coalesce(r.idle_hours, 0), coalesce(r.breakdown_hours, 0), r.fuel_consumed, r.fuel_unit,
         coalesce(r.certification_expiry, eq.certification_expiry), nullif(r.breakdown_reason, ''), r.remarks, coalesce(r.sort_order, 0)
  from jsonb_populate_recordset(null::equipment_entries, coalesce(p->'equipment', '[]')) r
  left join equipment eq on eq.id = r.equipment_id and eq.project_id = v_project;

  -- Material rows: opening stock comes from the ledger, never from the client.
  delete from material_transactions where dpr_id = v_id;
  insert into material_transactions (dpr_id, project_id, material_id, txn_date, opening_qty, received_qty,
    accepted_qty, rejected_qty, issued_qty, consumed_qty, returned_qty, challan_no, heat_batch_no,
    inspection_status, storage_location, supplier, remarks, sort_order)
  select v_id, v_project, m.id, v_date,
         coalesce((select l.closing_qty from material_ledger l
                    where l.material_id = m.id and l.txn_date <= v_date
                    order by l.txn_date desc, l.sort_order desc limit 1), 0),
         coalesce(r.received_qty, 0), r.accepted_qty, coalesce(r.rejected_qty, 0), coalesce(r.issued_qty, 0),
         coalesce(r.consumed_qty, 0), coalesce(r.returned_qty, 0), r.challan_no, r.heat_batch_no,
         coalesce(r.inspection_status, 'pending'), r.storage_location, r.supplier, r.remarks, coalesce(r.sort_order, 0)
  from jsonb_populate_recordset(null::material_transactions, coalesce(p->'materials', '[]')) r
  join materials m on m.id = r.material_id and m.project_id = v_project;

  -- Registers raised from this DPR (they live on after approval with their own status).
  delete from safety_observations where dpr_id = v_id;
  insert into safety_observations (project_id, dpr_id, category, permit_type, permit_no, description, location,
    severity, corrective_action, preventive_action, responsible, target_date, status, observed_on)
  select v_project, v_id, r.category, r.permit_type, r.permit_no, r.description, r.location, r.severity,
         r.corrective_action, r.preventive_action, r.responsible, r.target_date, coalesce(r.status, 'open'), v_date
  from jsonb_populate_recordset(null::safety_observations, coalesce(p->'safety', '[]')) r;

  delete from inspection_records where dpr_id = v_id;
  insert into inspection_records (project_id, dpr_id, activity_id, request_no, inspection_type, description,
    is_hold_point, inspected_qty, outcome, inspector, inspected_on, remarks)
  select v_project, v_id, a.id, r.request_no, r.inspection_type, r.description, coalesce(r.is_hold_point, false),
         r.inspected_qty, coalesce(r.outcome, 'pending'), r.inspector, coalesce(r.inspected_on, v_date), r.remarks
  from jsonb_populate_recordset(null::inspection_records, coalesce(p->'inspections', '[]')) r
  left join activities a on a.id = r.activity_id and a.project_id = v_project;

  delete from quality_ncrs where dpr_id = v_id;
  insert into quality_ncrs (project_id, dpr_id, activity_id, ncr_no, kind, description, rework_required,
    corrective_action, responsible, target_date, status)
  select v_project, v_id, a.id, r.ncr_no, coalesce(r.kind, 'ncr'), r.description, coalesce(r.rework_required, false),
         r.corrective_action, r.responsible, r.target_date, coalesce(r.status, 'open')
  from jsonb_populate_recordset(null::quality_ncrs, coalesce(p->'ncrs', '[]')) r
  left join activities a on a.id = r.activity_id and a.project_id = v_project;

  delete from constraints where dpr_id = v_id;
  insert into constraints (project_id, dpr_id, activity_id, category, description, responsible, target_date, status)
  select v_project, v_id, a.id, r.category, r.description, r.responsible, r.target_date, coalesce(r.status, 'open')
  from jsonb_populate_recordset(null::constraints, coalesce(p->'constraints', '[]')) r
  left join activities a on a.id = r.activity_id and a.project_id = v_project;

  delete from action_items where dpr_id = v_id;
  insert into action_items (project_id, dpr_id, description, responsible, target_date, priority, status)
  select v_project, v_id, r.description, r.responsible, r.target_date, coalesce(r.priority, 'normal'), coalesce(r.status, 'open')
  from jsonb_populate_recordset(null::action_items, coalesce(p->'actions', '[]')) r;

  perform public.refresh_dpr_entries(v_id, false);

  select * into h from dpr_headers where id = v_id;
  v_result := jsonb_build_object('id', h.id, 'dpr_no', h.dpr_no, 'seq', h.seq, 'revision', h.revision,
                                 'status', h.status, 'version', h.version, 'updated_at', h.updated_at);
  if v_key is not null then
    insert into synchronization_jobs (idempotency_key, user_id, project_id, operation, result)
    values (v_key, auth.uid(), v_project, 'save_dpr', v_result);
  end if;
  return v_result;
end $$;

create or replace function public.submit_dpr(p_id uuid, p_expected_version int default null) returns jsonb
language plpgsql security definer set search_path = public as $$
declare h dpr_headers; v_over text;
begin
  select * into h from dpr_headers where id = p_id for update;
  if h.id is null then raise exception 'DPR not found' using errcode = 'PT404'; end if;
  perform public.require_perm(h.project_id, 'dpr', 'submit');
  if h.status not in ('draft', 'rejected') then
    raise exception 'DPR % is already %', h.dpr_no, h.status using errcode = 'PT409';
  end if;
  if p_expected_version is not null and p_expected_version <> h.version then
    raise exception 'CONFLICT: DPR % changed since you loaded it. Reload before submitting.', h.dpr_no using errcode = 'PT409';
  end if;
  if not exists (select 1 from dpr_activity_entries where dpr_id = p_id)
     and coalesce(trim(h.work_completed), '') = '' then
    raise exception 'Add at least one activity entry or describe the work completed before submitting' using errcode = 'PT400';
  end if;
  perform public.refresh_dpr_entries(p_id, false);
  v_over := public.dpr_overruns(p_id);
  if v_over is not null then
    raise exception 'Quantity exceeds approved quantity for: %. Record an authorised overrun with a reason, or correct the quantity.', v_over
      using errcode = 'PT422';
  end if;
  insert into approval_actions (project_id, dpr_id, action, from_status, to_status, actor_id)
  values (h.project_id, p_id, 'submit', h.status, 'submitted', auth.uid());
  update dpr_headers set status = 'submitted', submitted_at = now(), submitted_by = auth.uid() where id = p_id
  returning * into h;
  return jsonb_build_object('id', h.id, 'status', h.status, 'version', h.version);
end $$;

-- Full, self-contained JSON document of a DPR (used for snapshots and PDFs).
create or replace function public.dpr_document(p_id uuid) returns jsonb
language plpgsql stable security definer set search_path = public as $$
declare h dpr_headers; doc jsonb;
begin
  select * into h from dpr_headers where id = p_id;
  if h.id is null then raise exception 'DPR not found' using errcode = 'PT404'; end if;
  perform public.require_perm(h.project_id, 'dpr', 'view');
  select jsonb_build_object(
    'schema', 'construct-dpr/1',
    'project', (select jsonb_build_object('id', p.id, 'code', p.code, 'name', p.name, 'client', c.name,
                  'consultant', p.consultant, 'work_order_no', p.work_order_no, 'site_address', p.site_address,
                  'timezone', p.timezone, 'currency', p.currency, 'report_header', p.report_header,
                  'report_footer', p.report_footer, 'logo_drive_file_id', p.logo_drive_file_id)
                from projects p left join clients c on c.id = p.client_id where p.id = h.project_id),
    'header', to_jsonb(h) || jsonb_build_object(
                'prepared_by_name', (select full_name from profiles where id = h.prepared_by),
                'approved_by_name', (select full_name from profiles where id = h.approved_by),
                'area_name', (select name from areas where id = h.area_id)),
    'activities', coalesce((select jsonb_agg(to_jsonb(e) || jsonb_build_object('activity_code', a.code, 'activity_name', a.name,
                    'wbs_code', w.code, 'boq_item_no', b.item_no, 'contractor_name', ct.name) order by e.sort_order)
                  from dpr_activity_entries e join activities a on a.id = e.activity_id
                  left join wbs_nodes w on w.id = a.wbs_id left join boq_items b on b.id = e.boq_item_id
                  left join contractors ct on ct.id = e.contractor_id
                  where e.dpr_id = p_id), '[]'),
    'manpower', coalesce((select jsonb_agg(to_jsonb(m) || jsonb_build_object('contractor_name', ct.name) order by m.sort_order)
                  from manpower_entries m left join contractors ct on ct.id = m.contractor_id where m.dpr_id = p_id), '[]'),
    'equipment', coalesce((select jsonb_agg(to_jsonb(q) order by q.sort_order) from equipment_entries q where q.dpr_id = p_id), '[]'),
    'materials', coalesce((select jsonb_agg(to_jsonb(t) || jsonb_build_object('material_code', m.code, 'material_description', m.description, 'unit', m.unit) order by t.sort_order)
                  from material_transactions t join materials m on m.id = t.material_id where t.dpr_id = p_id), '[]'),
    'safety', coalesce((select jsonb_agg(to_jsonb(s) order by s.created_at) from safety_observations s where s.dpr_id = p_id), '[]'),
    'inspections', coalesce((select jsonb_agg(to_jsonb(i) order by i.created_at) from inspection_records i where i.dpr_id = p_id), '[]'),
    'ncrs', coalesce((select jsonb_agg(to_jsonb(n) order by n.created_at) from quality_ncrs n where n.dpr_id = p_id), '[]'),
    'constraints', coalesce((select jsonb_agg(to_jsonb(c) order by c.created_at) from constraints c where c.dpr_id = p_id), '[]'),
    'actions', coalesce((select jsonb_agg(to_jsonb(x) order by x.created_at) from action_items x where x.dpr_id = p_id), '[]'),
    'attachments', coalesce((select jsonb_agg(jsonb_build_object('id', f.id, 'drive_file_id', f.drive_file_id,
                      'category', f.category, 'caption', f.caption, 'mime_type', f.mime_type, 'original_filename', f.original_filename,
                      'activity_id', f.activity_id, 'captured_at', f.captured_at, 'latitude', f.latitude, 'longitude', f.longitude,
                      'sha256', f.sha256, 'transcript', f.transcript) order by f.created_at)
                  from attachments f where f.dpr_id = p_id and f.upload_status = 'uploaded' and f.retention_status = 'active'), '[]'),
    'approval_history', coalesce((select jsonb_agg(jsonb_build_object('action', x.action, 'comment', x.comment,
                      'from', x.from_status, 'to', x.to_status, 'by', pr.full_name, 'at', x.created_at) order by x.created_at)
                  from approval_actions x join profiles pr on pr.id = x.actor_id
                  where x.dpr_id in (with recursive chain as (
                      select id, previous_revision_id from dpr_headers where id = p_id
                      union all select d.id, d.previous_revision_id from dpr_headers d join chain on d.id = chain.previous_revision_id)
                    select id from chain)), '[]')
  ) into doc;
  return doc;
end $$;

create or replace function public.review_dpr(p_id uuid, p_decision text, p_comment text default null,
                                             p_accepted jsonb default null, p_expected_version int default null)
returns jsonb
language plpgsql security definer set search_path = public as $$
declare h dpr_headers; v_from dpr_status; v_over text; v_doc jsonb; v_allow_self boolean;
begin
  select * into h from dpr_headers where id = p_id for update;
  if h.id is null then raise exception 'DPR not found' using errcode = 'PT404'; end if;
  v_from := h.status;
  if p_expected_version is not null and p_expected_version <> h.version then
    raise exception 'CONFLICT: DPR % changed since you loaded it.', h.dpr_no using errcode = 'PT409';
  end if;

  if p_decision = 'comment' then
    perform public.require_perm(h.project_id, 'dpr', 'view');
    if coalesce(trim(p_comment), '') = '' then raise exception 'Comment text is required' using errcode = 'PT400'; end if;
    insert into approval_actions (project_id, dpr_id, action, comment, from_status, to_status, actor_id)
    values (h.project_id, p_id, 'comment', p_comment, v_from, v_from, auth.uid());
    return jsonb_build_object('id', h.id, 'status', h.status, 'version', h.version);
  end if;

  if h.status not in ('submitted', 'under_review') then
    raise exception 'DPR % is % — only submitted reports can be reviewed', h.dpr_no, h.status using errcode = 'PT409';
  end if;

  if p_decision = 'start_review' then
    perform public.require_perm(h.project_id, 'dpr', 'approve');
    update dpr_headers set status = 'under_review', reviewed_at = now(), reviewed_by = auth.uid() where id = p_id returning * into h;

  elsif p_decision = 'reject' then
    perform public.require_perm(h.project_id, 'dpr', 'reject');
    if coalesce(trim(p_comment), '') = '' then
      raise exception 'A reason is required when rejecting a DPR' using errcode = 'PT400';
    end if;
    update dpr_headers set status = 'rejected', reviewed_at = now(), reviewed_by = auth.uid() where id = p_id returning * into h;

  elsif p_decision = 'approve' then
    perform public.require_perm(h.project_id, 'dpr', 'approve');
    select allow_self_approval into v_allow_self from projects where id = h.project_id;
    if (h.prepared_by = auth.uid() or h.submitted_by = auth.uid()) and not v_allow_self then
      raise exception 'You cannot approve a report you prepared or submitted (project policy)' using errcode = 'PT403';
    end if;
    -- Reviewer may accept less than reported: {"<entry id>": accepted_qty}
    if p_accepted is not null then
      update dpr_activity_entries e set qty_accepted = (p_accepted ->> e.id::text)::numeric
       where e.dpr_id = p_id and p_accepted ? e.id::text;
    end if;
    update dpr_activity_entries set qty_accepted = qty_today where dpr_id = p_id and qty_accepted is null;
    perform public.refresh_dpr_entries(p_id, true);
    v_over := public.dpr_overruns(p_id);
    if v_over is not null then
      raise exception 'Accepted quantity exceeds approved quantity for: %', v_over using errcode = 'PT422';
    end if;
    -- Move activity actual dates forward (never backwards) from the approved report.
    update activities a set
      actual_start = coalesce(a.actual_start, e.actual_start, h.report_date),
      actual_finish = coalesce(a.actual_finish, e.actual_finish),
      status = case when e.actual_finish is not null or (a.total_qty > 0 and e.cum_qty >= a.total_qty) then 'completed'
                    when a.status = 'not_started' then 'in_progress' else a.status end
    from dpr_activity_entries e
    where e.dpr_id = p_id and a.id = e.activity_id and (e.qty_accepted > 0 or e.actual_start is not null or e.actual_finish is not null)
      and a.status <> 'cancelled';

    update dpr_headers set status = 'approved', approved_at = now(), approved_by = auth.uid(),
           reviewed_at = coalesce(reviewed_at, now()), reviewed_by = coalesce(reviewed_by, auth.uid())
     where id = p_id returning * into h;
    insert into approval_actions (project_id, dpr_id, action, comment, from_status, to_status, actor_id)
    values (h.project_id, p_id, 'approve', p_comment, v_from, 'approved', auth.uid());
    v_doc := public.dpr_document(p_id);
    insert into report_revisions (project_id, dpr_id, dpr_no, revision, report_date, snapshot, snapshot_sha256, approved_by, approved_at)
    values (h.project_id, p_id, h.dpr_no, h.revision, h.report_date, v_doc, public.sha256_hex(v_doc::text), auth.uid(), h.approved_at);
    return jsonb_build_object('id', h.id, 'status', h.status, 'version', h.version);
  else
    raise exception 'Unknown decision %', p_decision using errcode = 'PT400';
  end if;

  insert into approval_actions (project_id, dpr_id, action, comment, from_status, to_status, actor_id)
  values (h.project_id, p_id, p_decision, p_comment, v_from, h.status, auth.uid());
  return jsonb_build_object('id', h.id, 'status', h.status, 'version', h.version);
end $$;

-- Corrections to an approved DPR create a new revision; the approved one is untouched.
create or replace function public.start_dpr_revision(p_id uuid, p_reason text) returns uuid
language plpgsql security definer set search_path = public as $$
declare h dpr_headers; v_new uuid; v_open uuid;
begin
  select * into h from dpr_headers where id = p_id;
  if h.id is null then raise exception 'DPR not found' using errcode = 'PT404'; end if;
  perform public.require_perm(h.project_id, 'dpr', 'create');
  if h.status <> 'approved' then
    raise exception 'Only approved DPRs are revised; edit the draft instead' using errcode = 'PT409';
  end if;
  if coalesce(trim(p_reason), '') = '' then raise exception 'A revision reason is required' using errcode = 'PT400'; end if;
  select id into v_open from dpr_headers where project_id = h.project_id and seq = h.seq and status <> 'approved';
  if v_open is not null then return v_open; end if;
  if exists (select 1 from dpr_headers where project_id = h.project_id and seq = h.seq and revision > h.revision) then
    raise exception 'A later revision of % already exists; revise the latest one', h.dpr_no using errcode = 'PT409';
  end if;

  insert into dpr_headers (project_id, seq, dpr_no, revision, previous_revision_id, revision_reason, report_date, shift,
    work_start, work_end, weather, site_conditions, site_id, area_id, prepared_by, toolbox_talks, toolbox_topics,
    permits_issued, work_completed, achievements, work_not_completed, delay_reasons, weather_interruptions,
    material_shortages, equipment_constraints, labour_constraints, design_issues, client_instructions,
    decisions_required, tomorrow_plan, required_manpower, required_equipment, required_materials, priority_actions)
  select project_id, seq, dpr_no, revision + 1, id, trim(p_reason), report_date, shift,
    work_start, work_end, weather, site_conditions, site_id, area_id, auth.uid(), toolbox_talks, toolbox_topics,
    permits_issued, work_completed, achievements, work_not_completed, delay_reasons, weather_interruptions,
    material_shortages, equipment_constraints, labour_constraints, design_issues, client_instructions,
    decisions_required, tomorrow_plan, required_manpower, required_equipment, required_materials, priority_actions
  from dpr_headers where id = p_id
  returning id into v_new;

  insert into dpr_activity_entries (dpr_id, project_id, activity_id, boq_item_id, contractor_id, location, actual_start,
    actual_finish, unit, approved_total_qty, prev_cum_qty, qty_today, qty_inspected, cum_qty, balance_qty, progress_method,
    manual_progress_pct, planned_progress_pct, actual_progress_pct, variance_pct, overrun_authorized, overrun_reason,
    delay_reason, constraints, next_day_target, responsible, remarks, sort_order)
  select v_new, project_id, activity_id, boq_item_id, contractor_id, location, actual_start, actual_finish, unit,
    approved_total_qty, prev_cum_qty, qty_today, qty_inspected, cum_qty, balance_qty, progress_method, manual_progress_pct,
    planned_progress_pct, actual_progress_pct, variance_pct, overrun_authorized, overrun_reason, delay_reason,
    constraints, next_day_target, responsible, remarks, sort_order
  from dpr_activity_entries where dpr_id = p_id;

  insert into manpower_entries (dpr_id, project_id, contractor_id, area_id, trade, shift, skilled, semi_skilled, unskilled,
    supervisors, engineers, safety_staff, others, absent, hours_per_person, overtime_hours, remarks, sort_order)
  select v_new, project_id, contractor_id, area_id, trade, shift, skilled, semi_skilled, unskilled, supervisors, engineers,
    safety_staff, others, absent, hours_per_person, overtime_hours, remarks, sort_order
  from manpower_entries where dpr_id = p_id;

  insert into equipment_entries (dpr_id, project_id, equipment_id, equipment_code, equipment_type, owner, rated_capacity,
    location, operator, operating_hours, idle_hours, breakdown_hours, fuel_consumed, fuel_unit, certification_expiry,
    breakdown_reason, remarks, sort_order)
  select v_new, project_id, equipment_id, equipment_code, equipment_type, owner, rated_capacity, location, operator,
    operating_hours, idle_hours, breakdown_hours, fuel_consumed, fuel_unit, certification_expiry, breakdown_reason, remarks, sort_order
  from equipment_entries where dpr_id = p_id;

  insert into material_transactions (dpr_id, project_id, material_id, txn_date, opening_qty, received_qty, accepted_qty,
    rejected_qty, issued_qty, consumed_qty, returned_qty, challan_no, heat_batch_no, inspection_status, storage_location,
    supplier, remarks, sort_order)
  select v_new, project_id, material_id, txn_date, opening_qty, received_qty, accepted_qty, rejected_qty, issued_qty,
    consumed_qty, returned_qty, challan_no, heat_batch_no, inspection_status, storage_location, supplier, remarks, sort_order
  from material_transactions where dpr_id = p_id;

  insert into approval_actions (project_id, dpr_id, action, comment, from_status, to_status, actor_id)
  values (h.project_id, v_new, 'revise', p_reason, 'approved', 'draft', auth.uid());
  return v_new;
end $$;

-- ---------------------------------------------------------------------------
-- Planning: baselines, bulk date updates
-- ---------------------------------------------------------------------------
create or replace function public.create_baseline(p_project uuid, p_name text, p_notes text default null,
                                                  p_make_current boolean default false) returns uuid
language plpgsql security definer set search_path = public as $$
declare v uuid; n int;
begin
  perform public.require_perm(p_project, 'schedule', 'create');
  if p_make_current then perform public.require_perm(p_project, 'schedule', 'approve'); end if;
  if exists (select 1 from activities where project_id = p_project and (planned_start is null or planned_finish is null)) then
    raise exception 'Every activity needs planned start and finish dates before baselining. Run the scheduler first.'
      using errcode = 'PT422';
  end if;
  perform pg_advisory_xact_lock(hashtext('baseline:' || p_project::text));
  select coalesce(max(revision_no), -1) + 1 into n from baseline_revisions where project_id = p_project;
  insert into baseline_revisions (project_id, revision_no, name, notes, created_by)
  values (p_project, n, p_name, p_notes, auth.uid()) returning id into v;
  insert into baseline_activity_dates (baseline_id, activity_id, project_id, start_date, finish_date, duration, total_qty, weight)
  select v, id, project_id, planned_start, planned_finish, original_duration, total_qty, weight
  from activities where project_id = p_project;
  if p_make_current then
    update projects set approved_baseline_id = v where id = p_project;
  end if;
  return v;
end $$;

create or replace function public.set_current_baseline(p_project uuid, p_baseline uuid) returns void
language plpgsql security definer set search_path = public as $$
begin
  perform public.require_perm(p_project, 'schedule', 'approve');
  if not exists (select 1 from baseline_revisions where id = p_baseline and project_id = p_project) then
    raise exception 'Baseline not found in this project' using errcode = 'PT404';
  end if;
  update projects set approved_baseline_id = p_baseline where id = p_project;
end $$;

-- [{id, planned_start, planned_finish, version}] — versions give conflict detection.
create or replace function public.apply_schedule_dates(p_project uuid, p_rows jsonb) returns int
language plpgsql security definer set search_path = public as $$
declare n int; v_conflicts int;
begin
  perform public.require_perm(p_project, 'schedule', 'edit');
  select count(*) into v_conflicts
  from jsonb_to_recordset(p_rows) r(id uuid, version int)
  join activities a on a.id = r.id
  where a.project_id = p_project and r.version is not null and r.version <> a.version;
  if v_conflicts > 0 then
    raise exception 'CONFLICT: % activities changed since the schedule was calculated. Reload and recalculate.', v_conflicts
      using errcode = 'PT409';
  end if;
  update activities a set planned_start = r.planned_start, planned_finish = r.planned_finish
  from jsonb_to_recordset(p_rows) r(id uuid, planned_start date, planned_finish date)
  where a.id = r.id and a.project_id = p_project;
  get diagnostics n = row_count;
  return n;
end $$;

-- ---------------------------------------------------------------------------
-- Commercial: authorised BOQ quantity / rate revision
-- ---------------------------------------------------------------------------
create or replace function public.revise_boq_item(p_item uuid, p_new_qty numeric, p_new_rate numeric,
                                                  p_reason text, p_change_order uuid default null) returns void
language plpgsql security definer set search_path = public as $$
declare b boq_items;
begin
  select * into b from boq_items where id = p_item for update;
  if b.id is null then raise exception 'BOQ item not found' using errcode = 'PT404'; end if;
  perform public.require_perm(b.project_id, 'boq', 'approve');
  if coalesce(trim(p_reason), '') = '' then raise exception 'A reason is required' using errcode = 'PT400'; end if;
  if p_new_qty < 0 or p_new_rate < 0 then raise exception 'Quantity and rate must be non-negative' using errcode = 'PT400'; end if;
  insert into boq_revisions (project_id, boq_item_id, old_qty, new_qty, old_rate, new_rate, reason, change_order_id, created_by)
  values (b.project_id, b.id, coalesce(b.revised_qty, b.tender_qty), p_new_qty, b.rate, p_new_rate, trim(p_reason), p_change_order, auth.uid());
  perform set_config('cdpr.boq_revision', 'on', true);
  update boq_items set revised_qty = p_new_qty, rate = p_new_rate,
         change_order_id = coalesce(p_change_order, change_order_id) where id = p_item;
  perform set_config('cdpr.boq_revision', 'off', true);
end $$;

-- ---------------------------------------------------------------------------
-- Documents (Google Drive registry)
-- ---------------------------------------------------------------------------
create policy ins on public.attachments for insert to authenticated with check (
  created_by = auth.uid() and upload_status in ('pending', 'uploading')
  and (public.has_perm(project_id, 'documents', 'create') or public.has_perm(project_id, 'dpr', 'create')));

-- Idempotent: concurrent devices creating the same folder converge on one id.
create or replace function public.register_drive_folder(p_project uuid, p_path text, p_drive_folder_id text,
                                                        p_parent text default null) returns text
language plpgsql security definer set search_path = public as $$
declare v text;
begin
  if not (public.has_perm(p_project, 'documents', 'create') or public.has_perm(p_project, 'dpr', 'create')) then
    raise exception 'No permission to create project folders' using errcode = 'PT403';
  end if;
  insert into drive_folders (project_id, path, drive_folder_id, parent_drive_folder_id, created_by)
  values (p_project, p_path, p_drive_folder_id, p_parent, auth.uid())
  on conflict (project_id, path) do nothing;
  select drive_folder_id into v from drive_folders where project_id = p_project and path = p_path;
  if p_path = '' then
    update projects set drive_root_folder_id = v where id = p_project and drive_root_folder_id is distinct from v;
  end if;
  return v;
end $$;

create or replace function public.set_report_pdf(p_report_revision uuid, p_drive_file_id text, p_sha256 text) returns void
language plpgsql security definer set search_path = public as $$
declare r report_revisions;
begin
  select * into r from report_revisions where id = p_report_revision;
  if r.id is null then raise exception 'Report revision not found' using errcode = 'PT404'; end if;
  if not (public.has_perm(r.project_id, 'reports', 'create') or public.has_perm(r.project_id, 'dpr', 'approve')) then
    raise exception 'No permission to publish report PDFs' using errcode = 'PT403';
  end if;
  if r.pdf_drive_file_id is not null then
    raise exception 'A PDF is already registered for % rev %', r.dpr_no, r.revision using errcode = 'PT409';
  end if;
  update report_revisions set pdf_drive_file_id = p_drive_file_id, pdf_generated_at = now(), pdf_sha256 = p_sha256
   where id = p_report_revision;
end $$;

-- ---------------------------------------------------------------------------
-- Data portability: complete project export as JSON
-- ---------------------------------------------------------------------------
create or replace function public.export_project(p_project uuid) returns jsonb
language plpgsql stable security definer set search_path = public as $$
declare doc jsonb;
begin
  if not (public.has_perm(p_project, 'project', 'export') or public.has_perm(p_project, 'reports', 'export')) then
    raise exception 'No permission to export this project' using errcode = 'PT403';
  end if;
  select jsonb_build_object(
    'format', 'construct-dpr-export/1', 'exported_at', now(), 'exported_by', auth.uid(),
    'project', (select to_jsonb(p) from projects p where id = p_project),
    'calendars', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from project_calendars x where project_id = p_project),
    'sites', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from sites x where project_id = p_project),
    'areas', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from areas x where project_id = p_project),
    'wbs', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from wbs_nodes x where project_id = p_project),
    'activities', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from activities x where project_id = p_project),
    'dependencies', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from activity_dependencies x where project_id = p_project),
    'baselines', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from baseline_revisions x where project_id = p_project),
    'baseline_dates', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from baseline_activity_dates x where project_id = p_project),
    'boq_items', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from boq_items x where project_id = p_project),
    'boq_revisions', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from boq_revisions x where project_id = p_project),
    'change_orders', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from change_orders x where project_id = p_project),
    'equipment', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from equipment x where project_id = p_project),
    'materials', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from materials x where project_id = p_project),
    'dpr_headers', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from dpr_headers x where project_id = p_project),
    'dpr_activity_entries', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from dpr_activity_entries x where project_id = p_project),
    'manpower_entries', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from manpower_entries x where project_id = p_project),
    'equipment_entries', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from equipment_entries x where project_id = p_project),
    'material_transactions', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from material_transactions x where project_id = p_project),
    'safety_observations', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from safety_observations x where project_id = p_project),
    'inspection_records', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from inspection_records x where project_id = p_project),
    'quality_ncrs', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from quality_ncrs x where project_id = p_project),
    'constraints', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from constraints x where project_id = p_project),
    'action_items', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from action_items x where project_id = p_project),
    'approval_actions', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from approval_actions x where project_id = p_project),
    'report_revisions', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from report_revisions x where project_id = p_project),
    'attachments', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from attachments x where project_id = p_project),
    'drive_folders', (select coalesce(jsonb_agg(to_jsonb(x)), '[]') from drive_folders x where project_id = p_project)
  ) into doc;
  return doc;
end $$;

-- ---------------------------------------------------------------------------
-- Realtime: broadcast committed changes (Supabase Realtime honours RLS)
-- ---------------------------------------------------------------------------
alter publication supabase_realtime add table
  public.dpr_headers, public.dpr_activity_entries, public.activities, public.attachments,
  public.approval_actions, public.report_revisions, public.boq_items;

-- Function privileges: nothing for anonymous callers; internal helpers are
-- reachable only from inside the security-definer workflow functions.
alter default privileges in schema public revoke execute on functions from public;
revoke execute on all functions in schema public from public, anon;
grant execute on all functions in schema public to authenticated;
revoke execute on function public.refresh_dpr_entries(uuid, boolean) from authenticated;
revoke execute on function public.prior_accepted_qty(uuid, date, int) from authenticated;
revoke execute on function public.dpr_overruns(uuid) from authenticated;
revoke execute on function public.attach_standard_triggers(regclass) from authenticated;
