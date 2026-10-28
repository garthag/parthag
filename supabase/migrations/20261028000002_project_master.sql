-- CONSTRUCT DPR — Migration 2: project master data
-- Organization → Client → Contract → Project → Site → Area → Unit/System → WBS → Activity

-- Standard audit columns are repeated per table (explicit beats clever).

create table public.clients (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations (id) on delete cascade,
  name text not null,
  contact text,
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (organization_id, name)
);

create table public.contractors (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations (id) on delete cascade,
  name text not null,
  kind text not null default 'subcontractor'
    check (kind in ('main_contractor', 'subcontractor', 'supplier', 'consultant', 'client', 'agency')),
  contact text,
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (organization_id, name)
);

create table public.contracts (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations (id) on delete cascade,
  client_id uuid not null references public.clients (id),
  contract_no text not null,
  title text,
  contract_value numeric(18, 2) check (contract_value >= 0),
  currency char(3) not null default 'INR',
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (organization_id, contract_no)
);

create table public.projects (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations (id) on delete restrict,
  client_id uuid references public.clients (id),
  contract_id uuid references public.contracts (id),
  code text not null check (code ~ '^[A-Za-z0-9._-]{1,30}$'),
  name text not null check (length(trim(name)) > 0),
  consultant text,
  main_contractor_id uuid references public.contractors (id),
  work_order_no text,
  site_address text,
  project_manager_id uuid references public.profiles (id),
  planning_engineer_id uuid references public.profiles (id),
  start_date date,
  completion_date date,
  approved_baseline_id uuid,                     -- FK added after baseline_revisions exists
  timezone text not null default 'Asia/Kolkata',
  currency char(3) not null default 'INR',
  measurement_system text not null default 'metric' check (measurement_system in ('metric', 'imperial')),
  units text[] not null default array['MT','kg','m','mm','m²','m³','nos','hours','days','set','lot','RM'],
  money_decimals smallint not null default 2 check (money_decimals between 0 and 4),
  qty_decimals smallint not null default 3 check (qty_decimals between 0 and 6),
  drive_root_folder_id text,
  logo_drive_file_id text,
  report_header text,
  report_footer text,
  allow_self_approval boolean not null default false,
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (organization_id, code),
  check (completion_date is null or start_date is null or completion_date >= start_date)
);
create index on public.projects (organization_id, status);

create table public.project_members (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  user_id uuid not null references public.profiles (id) on delete cascade,
  role public.app_role not null,
  active boolean not null default true,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, user_id, role)
);
create index on public.project_members (user_id) where active;

-- Project-level overrides of the default role matrix.
create table public.project_role_permissions (
  project_id uuid not null references public.projects (id) on delete cascade,
  role public.app_role not null,
  module text not null,
  action text not null,
  allowed boolean not null,
  primary key (project_id, role, module, action)
);

create table public.project_calendars (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  name text not null,
  work_days smallint[] not null default array[1,2,3,4,5,6]::smallint[], -- 0 = Sunday
  holidays date[] not null default '{}',
  hours_per_day numeric(5, 2) not null default 8 check (hours_per_day > 0 and hours_per_day <= 24),
  shifts jsonb not null default '[{"code":"day","name":"Day","start":"08:00","end":"17:00"},{"code":"night","name":"Night","start":"20:00","end":"05:00"}]',
  is_default boolean not null default false,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, name)
);
create unique index one_default_calendar on public.project_calendars (project_id) where is_default;

create table public.sites (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  code text not null,
  name text not null,
  address text,
  latitude numeric(9, 6), longitude numeric(9, 6),
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, code)
);

-- Areas, units and systems form one tree (kind distinguishes the level).
create table public.areas (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  site_id uuid references public.sites (id),
  parent_id uuid references public.areas (id),
  kind text not null default 'area' check (kind in ('area', 'unit', 'system')),
  code text not null,
  name text not null,
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, code)
);

create table public.wbs_nodes (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  parent_id uuid references public.wbs_nodes (id),
  code text not null,
  name text not null,
  sort_order int not null default 0,
  weight numeric(9, 4) check (weight >= 0),
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, code)
);
create index on public.wbs_nodes (project_id, parent_id);

create table public.change_orders (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  co_no text not null,
  title text not null,
  description text,
  value numeric(18, 2) not null default 0,
  status text not null default 'proposed' check (status in ('proposed', 'submitted', 'approved', 'rejected', 'cancelled')),
  approved_by uuid references public.profiles (id),
  approved_at timestamptz,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, co_no)
);

create table public.boq_items (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  item_no text not null,
  description text not null,
  specification text,
  unit text not null,
  tender_qty numeric(18, 4) not null default 0 check (tender_qty >= 0),
  revised_qty numeric(18, 4) check (revised_qty >= 0),  -- approved revised quantity; null = tender
  rate numeric(18, 2) not null default 0 check (rate >= 0),
  measured_qty numeric(18, 4) not null default 0 check (measured_qty >= 0),
  certified_qty numeric(18, 4) not null default 0 check (certified_qty >= 0),
  billed_qty numeric(18, 4) not null default 0 check (billed_qty >= 0),
  is_extra_item boolean not null default false,
  change_order_id uuid references public.change_orders (id),
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, item_no),
  check (billed_qty <= certified_qty)
);

-- History of authorised quantity / rate revisions.
create table public.boq_revisions (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  boq_item_id uuid not null references public.boq_items (id) on delete cascade,
  old_qty numeric(18, 4), new_qty numeric(18, 4) not null,
  old_rate numeric(18, 2), new_rate numeric(18, 2) not null,
  reason text not null check (length(trim(reason)) > 0),
  change_order_id uuid references public.change_orders (id),
  created_at timestamptz not null default now(),
  created_by uuid references public.profiles (id)
);

create table public.activities (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  wbs_id uuid references public.wbs_nodes (id),
  area_id uuid references public.areas (id),
  boq_item_id uuid references public.boq_items (id),
  contractor_id uuid references public.contractors (id),
  calendar_id uuid references public.project_calendars (id),
  code text not null,
  name text not null,
  location text,
  unit text,
  total_qty numeric(18, 4) not null default 0 check (total_qty >= 0),  -- approved total quantity
  weight numeric(12, 4) not null default 1 check (weight >= 0),          -- for weighted progress
  original_duration int not null default 1 check (original_duration >= 0),
  remaining_duration int check (remaining_duration >= 0),
  planned_start date, planned_finish date,
  actual_start date, actual_finish date,
  is_milestone boolean not null default false,
  constraint_type text check (constraint_type in ('SNET', 'FNLT', 'MSO', 'MFO')),
  constraint_date date,
  responsible text,
  status text not null default 'not_started' check (status in ('not_started', 'in_progress', 'completed', 'cancelled')),
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, code),
  check (planned_finish is null or planned_start is null or planned_finish >= planned_start),
  check (actual_finish is null or (actual_start is not null and actual_finish >= actual_start)),
  check (constraint_type is null or constraint_date is not null)
);
create index on public.activities (project_id, wbs_id);
create index on public.activities (project_id, status);

create table public.activity_dependencies (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  predecessor_id uuid not null references public.activities (id) on delete cascade,
  successor_id uuid not null references public.activities (id) on delete cascade,
  type public.dependency_type not null default 'FS',
  lag_days int not null default 0,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (predecessor_id, successor_id),
  check (predecessor_id <> successor_id)
);
create index on public.activity_dependencies (project_id);

-- Baselines are frozen copies of the plan. They are never edited after creation.
create table public.baseline_revisions (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  revision_no int not null,
  name text not null,
  notes text,
  created_at timestamptz not null default now(),
  created_by uuid references public.profiles (id),
  unique (project_id, revision_no)
);

create table public.baseline_activity_dates (
  baseline_id uuid not null references public.baseline_revisions (id) on delete cascade,
  activity_id uuid not null references public.activities (id) on delete cascade,
  project_id uuid not null references public.projects (id) on delete cascade,
  start_date date, finish_date date,
  duration int, total_qty numeric(18, 4), weight numeric(12, 4),
  primary key (baseline_id, activity_id)
);

alter table public.projects
  add constraint projects_approved_baseline_fk foreign key (approved_baseline_id)
  references public.baseline_revisions (id);

create trigger baseline_revisions_frozen before update on public.baseline_revisions
  for each row execute function public.forbid_change();
create trigger baseline_dates_frozen before update on public.baseline_activity_dates
  for each row execute function public.forbid_change();

-- Equipment and material masters (project scoped).
create table public.equipment (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  code text not null,
  type text not null,
  owner_contractor_id uuid references public.contractors (id),
  rated_capacity text,
  certification_expiry date,
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, code)
);

create table public.materials (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  code text not null,
  description text not null,
  specification text,
  grade text,
  unit text not null,
  reorder_level numeric(18, 4),
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, code)
);

do $$
declare t text;
begin
  foreach t in array array['clients','contractors','contracts','projects','project_members','project_calendars',
    'sites','areas','wbs_nodes','change_orders','boq_items','activities','activity_dependencies','equipment','materials']
  loop
    perform public.attach_standard_triggers(('public.' || t)::regclass);
  end loop;
end $$;

create trigger audit after insert or delete on public.baseline_revisions
  for each row execute function public.audit_row();
create trigger audit after insert or update or delete on public.boq_revisions
  for each row execute function public.audit_row();
create trigger boq_revisions_append_only before update or delete on public.boq_revisions
  for each row execute function public.forbid_change();
