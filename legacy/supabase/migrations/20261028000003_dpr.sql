-- CONSTRUCT DPR — Migration 3: daily progress reports, registers, approvals,
-- immutable report snapshots and synchronisation log.

create table public.dpr_headers (
  id uuid primary key default gen_random_uuid(),     -- may be generated on the device (offline)
  project_id uuid not null references public.projects (id) on delete restrict,
  seq int not null,                                   -- DPR serial within the project
  dpr_no text not null,                               -- e.g. BF2-DPR-0042
  revision int not null default 0,
  previous_revision_id uuid references public.dpr_headers (id),
  revision_reason text,
  report_date date not null,
  shift text not null default 'day',
  work_start time, work_end time,
  weather text,
  site_conditions text,
  site_id uuid references public.sites (id),
  area_id uuid references public.areas (id),
  prepared_by uuid not null references public.profiles (id),
  status public.dpr_status not null default 'draft',
  submitted_at timestamptz, submitted_by uuid references public.profiles (id),
  reviewed_at timestamptz, reviewed_by uuid references public.profiles (id),
  approved_at timestamptz, approved_by uuid references public.profiles (id),
  -- Step 6 summary counters
  toolbox_talks int not null default 0 check (toolbox_talks >= 0),
  toolbox_topics text,
  permits_issued int not null default 0 check (permits_issued >= 0),
  -- Step 7 narrative
  work_completed text, achievements text, work_not_completed text, delay_reasons text,
  weather_interruptions text, material_shortages text, equipment_constraints text,
  labour_constraints text, design_issues text, client_instructions text, decisions_required text,
  tomorrow_plan text, required_manpower text, required_equipment text, required_materials text,
  priority_actions text,
  idempotency_key uuid,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, seq, revision),
  check (revision = 0 or (previous_revision_id is not null and revision_reason is not null))
);
-- One original DPR per project/date/shift/area. Revisions share the same seq.
create unique index dpr_one_per_slot on public.dpr_headers
  (project_id, report_date, shift, coalesce(area_id, '00000000-0000-0000-0000-000000000000'::uuid))
  where revision = 0;
create index on public.dpr_headers (project_id, report_date desc);
create index on public.dpr_headers (project_id, status);

create table public.dpr_activity_entries (
  id uuid primary key default gen_random_uuid(),
  dpr_id uuid not null references public.dpr_headers (id) on delete cascade,
  project_id uuid not null references public.projects (id),
  activity_id uuid not null references public.activities (id),
  boq_item_id uuid references public.boq_items (id),
  contractor_id uuid references public.contractors (id),
  location text,
  actual_start date, actual_finish date,
  unit text,
  approved_total_qty numeric(18, 4) not null default 0,  -- copied from activity at save time
  prev_cum_qty numeric(18, 4) not null default 0,        -- accepted qty in earlier approved DPRs
  qty_today numeric(18, 4) not null default 0 check (qty_today >= 0),   -- reported
  qty_inspected numeric(18, 4) check (qty_inspected >= 0),
  qty_accepted numeric(18, 4) check (qty_accepted >= 0),              -- set by reviewer; defaults to reported on approval
  cum_qty numeric(18, 4) not null default 0,
  balance_qty numeric(18, 4) not null default 0,
  progress_method public.progress_method not null default 'quantity',
  manual_progress_pct numeric(7, 3) check (manual_progress_pct between 0 and 100),
  planned_progress_pct numeric(7, 3),
  actual_progress_pct numeric(7, 3),
  variance_pct numeric(8, 3),
  overrun_authorized boolean not null default false,
  overrun_reason text,
  delay_reason text,
  constraints text,
  next_day_target numeric(18, 4),
  responsible text,
  remarks text,
  sort_order int not null default 0,
  unique (dpr_id, activity_id),
  check (not overrun_authorized or overrun_reason is not null)
);
create index on public.dpr_activity_entries (project_id, activity_id);

create table public.manpower_entries (
  id uuid primary key default gen_random_uuid(),
  dpr_id uuid not null references public.dpr_headers (id) on delete cascade,
  project_id uuid not null references public.projects (id),
  contractor_id uuid references public.contractors (id),
  area_id uuid references public.areas (id),
  trade text not null,
  shift text not null default 'day',
  skilled int not null default 0 check (skilled >= 0),
  semi_skilled int not null default 0 check (semi_skilled >= 0),
  unskilled int not null default 0 check (unskilled >= 0),
  supervisors int not null default 0 check (supervisors >= 0),
  engineers int not null default 0 check (engineers >= 0),
  safety_staff int not null default 0 check (safety_staff >= 0),
  others int not null default 0 check (others >= 0),
  absent int not null default 0 check (absent >= 0),
  hours_per_person numeric(5, 2) not null default 8 check (hours_per_person between 0 and 24),
  overtime_hours numeric(8, 2) not null default 0 check (overtime_hours >= 0),
  headcount int generated always as (skilled + semi_skilled + unskilled + supervisors + engineers + safety_staff + others) stored,
  man_hours numeric(10, 2) generated always as
    ((skilled + semi_skilled + unskilled + supervisors + engineers + safety_staff + others) * hours_per_person + overtime_hours) stored,
  remarks text,
  sort_order int not null default 0
);
create index on public.manpower_entries (project_id);

create table public.equipment_entries (
  id uuid primary key default gen_random_uuid(),
  dpr_id uuid not null references public.dpr_headers (id) on delete cascade,
  project_id uuid not null references public.projects (id),
  equipment_id uuid references public.equipment (id),
  equipment_code text not null,
  equipment_type text not null,
  owner text,
  rated_capacity text,
  location text,
  operator text,
  operating_hours numeric(6, 2) not null default 0 check (operating_hours >= 0),
  idle_hours numeric(6, 2) not null default 0 check (idle_hours >= 0),
  breakdown_hours numeric(6, 2) not null default 0 check (breakdown_hours >= 0),
  fuel_consumed numeric(10, 2) check (fuel_consumed >= 0),
  fuel_unit text,
  certification_expiry date,
  breakdown_reason text,
  utilization_pct numeric(6, 2) generated always as (
    case when operating_hours + idle_hours + breakdown_hours = 0 then null
         else round(operating_hours * 100 / (operating_hours + idle_hours + breakdown_hours), 2) end) stored,
  remarks text,
  sort_order int not null default 0,
  check (operating_hours + idle_hours + breakdown_hours <= 24),
  check (breakdown_hours = 0 or breakdown_reason is not null)
);
create index on public.equipment_entries (project_id);

-- Store ledger. Closing = opening + received + returned − issued − rejected.
-- Consumption is recorded at the work front and must not exceed what was issued.
create table public.material_transactions (
  id uuid primary key default gen_random_uuid(),
  dpr_id uuid references public.dpr_headers (id) on delete cascade,
  project_id uuid not null references public.projects (id),
  material_id uuid not null references public.materials (id),
  txn_date date not null,
  opening_qty numeric(18, 4) not null default 0,
  received_qty numeric(18, 4) not null default 0 check (received_qty >= 0),
  accepted_qty numeric(18, 4) check (accepted_qty >= 0),
  rejected_qty numeric(18, 4) not null default 0 check (rejected_qty >= 0),
  issued_qty numeric(18, 4) not null default 0 check (issued_qty >= 0),
  consumed_qty numeric(18, 4) not null default 0 check (consumed_qty >= 0),
  returned_qty numeric(18, 4) not null default 0 check (returned_qty >= 0),
  closing_qty numeric(18, 4) generated always as
    (opening_qty + received_qty + returned_qty - issued_qty - rejected_qty) stored,
  challan_no text,
  heat_batch_no text,
  inspection_status text not null default 'pending'
    check (inspection_status in ('not_required', 'pending', 'accepted', 'rejected', 'partially_accepted')),
  storage_location text,
  supplier text,
  remarks text,
  sort_order int not null default 0,
  check (rejected_qty <= received_qty),
  check (opening_qty + received_qty + returned_qty - issued_qty - rejected_qty >= 0)
);
create index on public.material_transactions (project_id, material_id, txn_date);

create table public.safety_observations (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id),
  dpr_id uuid references public.dpr_headers (id) on delete set null,
  category text not null check (category in (
    'toolbox_talk', 'permit', 'lifting_plan', 'work_at_height', 'unsafe_condition', 'unsafe_act',
    'near_miss', 'incident', 'first_aid', 'good_practice', 'other')),
  permit_type text check (permit_type in ('hot_work', 'work_at_height', 'lifting', 'confined_space', 'excavation', 'electrical_isolation', 'general')),
  permit_no text,
  description text not null,
  location text,
  severity text check (severity in ('low', 'medium', 'high', 'critical')),
  corrective_action text,
  preventive_action text,
  responsible text,
  target_date date,
  status text not null default 'open' check (status in ('open', 'in_progress', 'closed')),
  closed_at timestamptz,
  observed_on date not null default current_date,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1
);
create index on public.safety_observations (project_id, status);

create table public.inspection_records (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id),
  dpr_id uuid references public.dpr_headers (id) on delete set null,
  activity_id uuid references public.activities (id),
  request_no text,
  inspection_type text not null,
  description text,
  is_hold_point boolean not null default false,
  inspected_qty numeric(18, 4),
  outcome text not null default 'pending'
    check (outcome in ('pending', 'accepted', 'accepted_with_comments', 'rejected')),
  inspector text,
  inspected_on date,
  remarks text,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1
);
create index on public.inspection_records (project_id, outcome);

create table public.quality_ncrs (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id),
  dpr_id uuid references public.dpr_headers (id) on delete set null,
  activity_id uuid references public.activities (id),
  ncr_no text,
  kind text not null default 'ncr' check (kind in ('ncr', 'observation')),
  description text not null,
  rework_required boolean not null default false,
  corrective_action text,
  responsible text,
  target_date date,
  status text not null default 'open' check (status in ('open', 'in_progress', 'closed')),
  closed_at timestamptz,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1
);
create index on public.quality_ncrs (project_id, status);

create table public.constraints (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id),
  dpr_id uuid references public.dpr_headers (id) on delete set null,
  activity_id uuid references public.activities (id),
  category text not null check (category in ('material', 'equipment', 'labour', 'design', 'client', 'weather', 'permit', 'access', 'other')),
  description text not null,
  responsible text,
  target_date date,
  status text not null default 'open' check (status in ('open', 'in_progress', 'closed')),
  closed_at timestamptz,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1
);
create index on public.constraints (project_id, status);

create table public.action_items (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id),
  dpr_id uuid references public.dpr_headers (id) on delete set null,
  description text not null,
  responsible text,
  target_date date,
  priority text not null default 'normal' check (priority in ('low', 'normal', 'high', 'urgent')),
  status text not null default 'open' check (status in ('open', 'in_progress', 'closed')),
  closed_at timestamptz,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1
);
create index on public.action_items (project_id, status);

-- ---------------------------------------------------------------------------
-- Approval workflow
-- ---------------------------------------------------------------------------
create table public.approval_steps (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  document_type text not null default 'dpr',
  step_no int not null,
  name text not null,
  approver_role public.app_role,  -- null = anyone with approve permission
  unique (project_id, document_type, step_no)
);

create table public.approval_actions (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id),
  dpr_id uuid not null references public.dpr_headers (id) on delete cascade,
  action text not null check (action in ('submit', 'start_review', 'approve', 'reject', 'comment', 'revise')),
  comment text,
  from_status public.dpr_status,
  to_status public.dpr_status,
  actor_id uuid not null references public.profiles (id),
  created_at timestamptz not null default now()
);
create index on public.approval_actions (dpr_id, created_at);
create trigger approval_actions_append_only before update on public.approval_actions
  for each row execute function public.forbid_change();

-- Immutable snapshot of each approved DPR revision (what the PDF is built from).
create table public.report_revisions (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id),
  dpr_id uuid not null unique references public.dpr_headers (id),
  dpr_no text not null,
  revision int not null,
  report_date date not null,
  snapshot jsonb not null,
  snapshot_sha256 text not null,
  approved_by uuid not null references public.profiles (id),
  approved_at timestamptz not null default now(),
  pdf_drive_file_id text,
  pdf_generated_at timestamptz,
  pdf_sha256 text,
  unique (project_id, dpr_no, revision)
);

-- Snapshots never change; only the PDF reference may be set once.
create or replace function public.guard_report_revision() returns trigger
language plpgsql as $$
begin
  if tg_op = 'DELETE' then
    raise exception 'Approved report revisions cannot be deleted';
  end if;
  if new.snapshot is distinct from old.snapshot or new.snapshot_sha256 is distinct from old.snapshot_sha256
     or new.dpr_id is distinct from old.dpr_id or new.revision is distinct from old.revision
     or new.approved_by is distinct from old.approved_by or new.approved_at is distinct from old.approved_at
     or (old.pdf_drive_file_id is not null and new.pdf_drive_file_id is distinct from old.pdf_drive_file_id) then
    raise exception 'Approved report revisions are immutable (only the first PDF reference can be attached)';
  end if;
  return new;
end $$;
create trigger guard before update or delete on public.report_revisions
  for each row execute function public.guard_report_revision();

-- ---------------------------------------------------------------------------
-- Immutability of approved DPRs
-- ---------------------------------------------------------------------------
create or replace function public.guard_approved_dpr() returns trigger
language plpgsql as $$
begin
  if old.status = 'approved' then
    raise exception 'DPR % rev % is approved and immutable. Start a revision instead.', old.dpr_no, old.revision
      using errcode = 'P0001';
  end if;
  return case when tg_op = 'DELETE' then old else new end;
end $$;
create trigger guard_approved before update or delete on public.dpr_headers
  for each row execute function public.guard_approved_dpr();

create or replace function public.guard_approved_dpr_child() returns trigger
language plpgsql as $$
declare st public.dpr_status;
begin
  select status into st from public.dpr_headers
   where id = case when tg_op = 'DELETE' then old.dpr_id else new.dpr_id end;
  if st = 'approved' then
    raise exception 'Rows of an approved DPR are immutable' using errcode = 'P0001';
  end if;
  if tg_op = 'UPDATE' and old.dpr_id is distinct from new.dpr_id then
    select status into st from public.dpr_headers where id = old.dpr_id;
    if st = 'approved' then raise exception 'Rows of an approved DPR are immutable'; end if;
  end if;
  return case when tg_op = 'DELETE' then old else new end;
end $$;

do $$
declare t text;
begin
  foreach t in array array['dpr_activity_entries','manpower_entries','equipment_entries','material_transactions'] loop
    execute format('create trigger guard_approved before insert or update or delete on public.%I
                    for each row execute function public.guard_approved_dpr_child()', t);
  end loop;
  foreach t in array array['dpr_headers','safety_observations','inspection_records','quality_ncrs','constraints','action_items'] loop
    perform public.attach_standard_triggers(('public.' || t)::regclass);
  end loop;
end $$;

create trigger audit after insert or update or delete on public.dpr_activity_entries
  for each row execute function public.audit_row();
create trigger audit after insert or update or delete on public.material_transactions
  for each row execute function public.audit_row();
create trigger audit after insert or update on public.report_revisions
  for each row execute function public.audit_row();

-- ---------------------------------------------------------------------------
-- Synchronisation log (server side of the offline queue / idempotency)
-- ---------------------------------------------------------------------------
create table public.synchronization_jobs (
  idempotency_key uuid primary key,
  user_id uuid not null references public.profiles (id),
  project_id uuid references public.projects (id),
  operation text not null,
  status text not null default 'completed' check (status in ('completed', 'failed')),
  result jsonb,
  created_at timestamptz not null default now()
);
create index on public.synchronization_jobs (user_id, created_at desc);
