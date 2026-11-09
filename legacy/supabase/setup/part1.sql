-- CONSTRUCT DPR database setup, PART 1 of 2
-- GENERATED from supabase/migrations by supabase/setup/build.sh. Do not edit.
-- Paste the whole file into Supabase > SQL Editor > New query, then press Run.


-- ===== 20261028000001_foundation.sql =====
-- CONSTRUCT DPR — Migration 1: foundation
-- Extensions, enumerations, users/organizations, permission catalogue,
-- and the generic audit / versioning triggers used by every table.

create extension if not exists pgcrypto;

-- ---------------------------------------------------------------------------
-- Enumerations
-- ---------------------------------------------------------------------------
create type public.app_role as enum (
  'org_admin', 'project_admin', 'project_manager', 'construction_manager',
  'planning_engineer', 'site_engineer', 'supervisor', 'storekeeper',
  'qaqc_engineer', 'safety_engineer', 'commercial_engineer',
  'client_reviewer', 'auditor'
);

create type public.dpr_status as enum (
  'draft', 'submitted', 'under_review', 'approved', 'rejected'
);

create type public.record_status as enum ('active', 'on_hold', 'closed', 'cancelled', 'archived');

create type public.dependency_type as enum ('FS', 'SS', 'FF', 'SF');

create type public.progress_method as enum ('quantity', 'manual');

-- ---------------------------------------------------------------------------
-- Shared trigger functions
-- ---------------------------------------------------------------------------

-- Keeps updated_at / updated_by / version current. `version` is used for
-- optimistic concurrency: clients send the version they loaded and the
-- update is rejected (0 rows) if somebody else saved first.
create or replace function public.touch_row() returns trigger
language plpgsql as $$
begin
  if tg_op = 'INSERT' then
    new.created_at := coalesce(new.created_at, now());
    new.updated_at := new.created_at;
    new.created_by := coalesce(new.created_by, auth.uid());
    new.updated_by := coalesce(new.updated_by, new.created_by);
    new.version := 1;
  else
    new.created_at := old.created_at;
    new.created_by := old.created_by;
    new.updated_at := now();
    new.updated_by := coalesce(auth.uid(), new.updated_by);
    new.version := old.version + 1;
  end if;
  return new;
end $$;

-- Organisations / users first; audit_events is defined below.
create table public.profiles (
  id uuid primary key references auth.users (id) on delete cascade,
  full_name text not null default '',
  email text,
  phone text,
  designation text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create or replace function public.handle_new_user() returns trigger
language plpgsql security definer set search_path = public as $$
begin
  insert into public.profiles (id, full_name, email)
  values (new.id,
          coalesce(new.raw_user_meta_data ->> 'full_name', new.raw_user_meta_data ->> 'name', split_part(new.email, '@', 1)),
          new.email)
  on conflict (id) do nothing;
  return new;
end $$;

create trigger on_auth_user_created
  after insert on auth.users
  for each row execute function public.handle_new_user();

create table public.organizations (
  id uuid primary key default gen_random_uuid(),
  name text not null check (length(trim(name)) > 0),
  drive_root_folder_id text,
  status public.record_status not null default 'active',
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id),
  updated_by uuid references public.profiles (id),
  version int not null default 1
);

create table public.organization_members (
  organization_id uuid not null references public.organizations (id) on delete cascade,
  user_id uuid not null references public.profiles (id) on delete cascade,
  role text not null default 'member' check (role in ('org_admin', 'member')),
  created_at timestamptz not null default now(),
  primary key (organization_id, user_id)
);
create index on public.organization_members (user_id);

-- ---------------------------------------------------------------------------
-- Audit log (append-only)
-- ---------------------------------------------------------------------------
create table public.audit_events (
  id bigint generated always as identity primary key,
  occurred_at timestamptz not null default now(),
  actor_id uuid,
  table_name text not null,
  record_id uuid,
  project_id uuid,
  action text not null,
  old_data jsonb,
  new_data jsonb
);
create index on public.audit_events (project_id, occurred_at desc);
create index on public.audit_events (table_name, record_id);

create or replace function public.audit_row() returns trigger
language plpgsql security definer set search_path = public as $$
declare
  rec jsonb := case when tg_op = 'DELETE' then to_jsonb(old) else to_jsonb(new) end;
begin
  insert into public.audit_events (actor_id, table_name, record_id, project_id, action, old_data, new_data)
  values (auth.uid(), tg_table_name, (rec ->> 'id')::uuid,
          coalesce((rec ->> 'project_id')::uuid, case when tg_table_name = 'projects' then (rec ->> 'id')::uuid end),
          lower(tg_op),
          case when tg_op <> 'INSERT' then to_jsonb(old) end,
          case when tg_op <> 'DELETE' then to_jsonb(new) end);
  return null;
end $$;

create or replace function public.forbid_change() returns trigger
language plpgsql as $$
begin
  raise exception '% is append-only; % is not permitted', tg_table_name, lower(tg_op)
    using errcode = 'P0001';
end $$;

create trigger audit_events_append_only
  before update or delete on public.audit_events
  for each row execute function public.forbid_change();

-- Helper used by migrations to attach the standard triggers to a table.
create or replace function public.attach_standard_triggers(tbl regclass) returns void
language plpgsql as $$
begin
  execute format('create trigger touch before insert or update on %s for each row execute function public.touch_row()', tbl);
  execute format('create trigger audit after insert or update or delete on %s for each row execute function public.audit_row()', tbl);
end $$;

select public.attach_standard_triggers('public.organizations');

-- ---------------------------------------------------------------------------
-- Permission catalogue
-- ---------------------------------------------------------------------------
-- Modules and actions are text (validated by check constraints) so new ones
-- can be added without enum migrations.
create table public.role_permissions (
  role public.app_role not null,
  module text not null check (module in (
    'project', 'members', 'masters', 'schedule', 'boq', 'commercial', 'dpr',
    'resources', 'safety', 'quality', 'documents', 'reports')),
  action text not null check (action in (
    'create', 'view', 'edit', 'submit', 'approve', 'reject', 'export', 'delete')),
  primary key (role, module, action)
);

-- Compact matrix: letters c v e s a r x d = create view edit submit approve
-- reject export delete. Documented in docs/PERMISSIONS.md.
with matrix (role, module, letters) as (
  values
  ('project_manager','project','cvex'), ('project_manager','members','cved'), ('project_manager','masters','cvexd'),
  ('project_manager','schedule','cvesarx'), ('project_manager','boq','cvesarx'), ('project_manager','commercial','cvesarx'),
  ('project_manager','dpr','cvesarx'), ('project_manager','resources','cvex'), ('project_manager','safety','cvesarx'),
  ('project_manager','quality','cvesarx'), ('project_manager','documents','cvexd'), ('project_manager','reports','cvx'),

  ('construction_manager','project','v'), ('construction_manager','members','v'), ('construction_manager','masters','cvex'),
  ('construction_manager','schedule','vex'), ('construction_manager','boq','vx'), ('construction_manager','commercial','vx'),
  ('construction_manager','dpr','cvesarx'), ('construction_manager','resources','cvex'), ('construction_manager','safety','cvesarx'),
  ('construction_manager','quality','cvesarx'), ('construction_manager','documents','cvex'), ('construction_manager','reports','cvx'),

  ('planning_engineer','project','v'), ('planning_engineer','members','v'), ('planning_engineer','masters','cvexd'),
  ('planning_engineer','schedule','cvesx'), ('planning_engineer','boq','vx'), ('planning_engineer','commercial','v'),
  ('planning_engineer','dpr','cvesx'), ('planning_engineer','resources','vx'), ('planning_engineer','safety','v'),
  ('planning_engineer','quality','v'), ('planning_engineer','documents','cvx'), ('planning_engineer','reports','cvx'),

  ('site_engineer','project','v'), ('site_engineer','members','v'), ('site_engineer','masters','v'),
  ('site_engineer','schedule','v'), ('site_engineer','boq','v'),
  ('site_engineer','dpr','cvesx'), ('site_engineer','resources','cve'), ('site_engineer','safety','cves'),
  ('site_engineer','quality','cves'), ('site_engineer','documents','cv'), ('site_engineer','reports','vx'),

  ('supervisor','project','v'), ('supervisor','members','v'), ('supervisor','masters','v'), ('supervisor','schedule','v'),
  ('supervisor','dpr','cves'), ('supervisor','resources','cve'), ('supervisor','safety','cves'),
  ('supervisor','quality','v'), ('supervisor','documents','cv'), ('supervisor','reports','v'),

  ('storekeeper','project','v'), ('storekeeper','members','v'), ('storekeeper','masters','v'),
  ('storekeeper','dpr','cvs'), ('storekeeper','resources','cvex'), ('storekeeper','documents','cv'), ('storekeeper','reports','v'),

  ('qaqc_engineer','project','v'), ('qaqc_engineer','members','v'), ('qaqc_engineer','masters','v'), ('qaqc_engineer','schedule','v'),
  ('qaqc_engineer','dpr','vs'), ('qaqc_engineer','resources','v'), ('qaqc_engineer','safety','v'),
  ('qaqc_engineer','quality','cvesarx'), ('qaqc_engineer','documents','cvx'), ('qaqc_engineer','reports','vx'),

  ('safety_engineer','project','v'), ('safety_engineer','members','v'), ('safety_engineer','masters','v'), ('safety_engineer','schedule','v'),
  ('safety_engineer','dpr','vs'), ('safety_engineer','resources','v'), ('safety_engineer','safety','cvesarx'),
  ('safety_engineer','quality','v'), ('safety_engineer','documents','cvx'), ('safety_engineer','reports','vx'),

  ('commercial_engineer','project','v'), ('commercial_engineer','members','v'), ('commercial_engineer','masters','v'),
  ('commercial_engineer','schedule','v'), ('commercial_engineer','boq','cvesx'), ('commercial_engineer','commercial','cvesx'),
  ('commercial_engineer','dpr','vx'), ('commercial_engineer','resources','v'), ('commercial_engineer','documents','cvx'),
  ('commercial_engineer','reports','vx'),

  ('client_reviewer','project','v'), ('client_reviewer','masters','v'), ('client_reviewer','schedule','v'),
  ('client_reviewer','boq','v'), ('client_reviewer','dpr','varx'), ('client_reviewer','resources','v'),
  ('client_reviewer','safety','v'), ('client_reviewer','quality','v'), ('client_reviewer','documents','v'),
  ('client_reviewer','reports','vx'),

  ('auditor','project','vx'), ('auditor','members','v'), ('auditor','masters','vx'), ('auditor','schedule','vx'),
  ('auditor','boq','vx'), ('auditor','commercial','vx'), ('auditor','dpr','vx'), ('auditor','resources','vx'),
  ('auditor','safety','vx'), ('auditor','quality','vx'), ('auditor','documents','vx'), ('auditor','reports','vx')
),
letters (l, action) as (
  values ('c','create'), ('v','view'), ('e','edit'), ('s','submit'),
         ('a','approve'), ('r','reject'), ('x','export'), ('d','delete')
)
insert into public.role_permissions (role, module, action)
select m.role::public.app_role, m.module, l.action
from matrix m join letters l on position(l.l in m.letters) > 0;

-- org_admin and project_admin receive every permission.
insert into public.role_permissions (role, module, action)
select r.role::public.app_role, mo.module, a.action
from (values ('org_admin'), ('project_admin')) r(role)
cross join (values ('project'),('members'),('masters'),('schedule'),('boq'),('commercial'),('dpr'),
                   ('resources'),('safety'),('quality'),('documents'),('reports')) mo(module)
cross join (values ('create'),('view'),('edit'),('submit'),('approve'),('reject'),('export'),('delete')) a(action);

-- ===== 20261028000002_project_master.sql =====
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

-- ===== 20261028000003_dpr.sql =====
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

-- ===== 20261028000004_documents_industrial.sql =====
-- CONSTRUCT DPR — Migration 4: Google Drive document register and
-- industrial work packages (fabrication, dispatch, erection, commissioning).

-- Folder registry. `path` is the logical path below the project root, e.g.
-- '04_DPR/2026/10/Photographs'. The unique key makes folder creation
-- idempotent across retries and devices.
create table public.drive_folders (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  path text not null,
  drive_folder_id text not null,
  parent_drive_folder_id text,
  created_at timestamptz not null default now(),
  created_by uuid references public.profiles (id),
  unique (project_id, path)
);

create table public.attachments (
  id uuid primary key default gen_random_uuid(),     -- generated on device before upload
  project_id uuid not null references public.projects (id) on delete restrict,
  dpr_id uuid references public.dpr_headers (id) on delete set null,
  activity_id uuid references public.activities (id) on delete set null,
  drive_file_id text,
  parent_folder_id text,
  web_view_link text,
  original_filename text not null,
  mime_type text not null,
  size_bytes bigint not null check (size_bytes >= 0),
  sha256 text check (sha256 ~ '^[0-9a-f]{64}$'),
  category text not null check (category in ('photo', 'video', 'audio', 'document', 'drawing', 'report')),
  caption text,
  transcript text,
  captured_at timestamptz,                -- device-reported; not proof of authenticity
  latitude numeric(9, 6), longitude numeric(9, 6), location_accuracy_m numeric(10, 2),
  location_source text check (location_source in ('device_gps', 'manual')),
  revision int not null default 0,
  upload_status text not null default 'pending'
    check (upload_status in ('pending', 'uploading', 'uploaded', 'failed')),
  upload_error text,
  upload_attempts int not null default 0,
  uploaded_at timestamptz,
  uploaded_by uuid references public.profiles (id),
  retention_status text not null default 'active'
    check (retention_status in ('active', 'archived', 'deletion_requested', 'deleted')),
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  check (upload_status <> 'uploaded' or drive_file_id is not null)
);
create unique index attachments_drive_file on public.attachments (drive_file_id) where drive_file_id is not null;
create index on public.attachments (project_id, dpr_id);
create index on public.attachments (project_id, sha256);
select public.attach_standard_triggers('public.attachments');

-- ---------------------------------------------------------------------------
-- Industrial work packages (Phase 6 — schema ready, UI on the roadmap)
-- ---------------------------------------------------------------------------
create table public.fabrication_items (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  wbs_id uuid references public.wbs_nodes (id),
  activity_id uuid references public.activities (id),
  boq_item_id uuid references public.boq_items (id),
  drawing_no text not null, drawing_rev text,
  e_mark text not null,
  material_grade text,
  qty numeric(18, 4) not null default 1, unit text not null default 'nos',
  weight_kg numeric(18, 3),
  stage text not null default 'not_started' check (stage in (
    'not_started', 'cutting', 'drilling', 'fit_up', 'welding', 'ndt', 'surface_prep', 'painting', 'ready', 'rejected')),
  inspection_status text not null default 'pending',
  rework_count int not null default 0,
  remarks text,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1,
  unique (project_id, drawing_no, e_mark)
);

create table public.dispatch_records (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  po_reference text,
  challan_no text, gate_pass_no text,
  vehicle_no text,
  planned_dispatch date, actual_dispatch date,
  weight_kg numeric(18, 3), package_count int,
  transit_status text not null default 'planned'
    check (transit_status in ('planned', 'ready', 'dispatched', 'in_transit', 'received', 'short', 'damaged')),
  received_on date,
  shortage_damage_notes text,
  items jsonb not null default '[]',   -- [{fabrication_item_id, e_mark, qty}]
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1
);

create table public.erection_records (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  activity_id uuid references public.activities (id),
  fabrication_item_id uuid references public.fabrication_items (id),
  tag text not null,
  work_front text,
  foundation_ready boolean not null default false,
  alignment_ok boolean, level_ok boolean,
  lifting_reference text,
  erected_qty numeric(18, 4) not null default 0, unit text,
  bolting_done boolean not null default false, welding_done boolean not null default false,
  inspection_status text not null default 'pending',
  completion_status text not null default 'not_started'
    check (completion_status in ('not_started', 'in_progress', 'erected', 'inspected', 'completed')),
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1
);

create table public.commissioning_checklists (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects (id) on delete cascade,
  area_id uuid references public.areas (id),
  system text not null,
  subsystem text,
  stage text not null check (stage in (
    'pre_commissioning', 'flushing_cleaning', 'loop_check', 'functional_test', 'trial_operation',
    'punch_list', 'performance_test', 'handover')),
  item text not null,
  status text not null default 'open' check (status in ('open', 'in_progress', 'passed', 'failed', 'not_applicable')),
  punch_category text check (punch_category in ('A', 'B', 'C')),
  remarks text,
  completed_on date,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  created_by uuid references public.profiles (id), updated_by uuid references public.profiles (id),
  version int not null default 1
);

do $$
declare t text;
begin
  foreach t in array array['fabrication_items','dispatch_records','erection_records','commissioning_checklists'] loop
    perform public.attach_standard_triggers(('public.' || t)::regclass);
  end loop;
end $$;

-- ===== 20261028000005_security.sql =====
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

-- ===== 20261028000006_workflows.sql =====
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

-- pgcrypto lives in `extensions` on Supabase and in `public` on plain PostgreSQL.
create or replace function public.sha256_hex(p text) returns text
language sql immutable set search_path = public, extensions as $$ select encode(digest(p, 'sha256'), 'hex') $$;

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

-- ===== 20261028000007_demo_project.sql =====
-- CONSTRUCT DPR — Migration 7: example project loader.
-- `select load_demo_project();` (or the "Load demo project" button) creates a
-- realistic industrial project owned by the caller, with masters, schedule,
-- baseline, BOQ and a week of approved DPRs created through the real workflow.

create or replace function public.load_demo_project() returns uuid
language plpgsql security definer set search_path = public as $$
declare
  v_org uuid; v_client uuid; v_proj uuid;
  c_main uuid; c_steel uuid; c_civil uuid; c_refr uuid;
  a_skb uuid; a_sth uuid; a_chs uuid;
  w1 uuid; w2 uuid; w3 uuid; w4 uuid; w5 uuid;
  b_exc uuid; b_rcc uuid; b_fab uuid; b_ere uuid; b_mech uuid; b_ref uuid;
  act record;
  v_start date := current_date - 21;
  d int; v_dpr jsonb; v_id uuid;
  m_steel uuid; m_cem uuid; m_bolt uuid;
  e_crane uuid; e_crawler uuid;
begin
  if auth.uid() is null then raise exception 'Not signed in' using errcode = 'PT401'; end if;

  v_org := public.create_organization('Demo Industrial Constructions Pvt Ltd');
  insert into clients (organization_id, name) values (v_org, 'Demo Steel Ltd') returning id into v_client;
  insert into contractors (organization_id, name, kind) values
    (v_org, 'Demo Industrial Constructions (Main)', 'main_contractor') returning id into c_main;
  insert into contractors (organization_id, name, kind) values (v_org, 'Sterling Steel Erectors', 'subcontractor') returning id into c_steel;
  insert into contractors (organization_id, name, kind) values (v_org, 'Bharat Civil Works', 'subcontractor') returning id into c_civil;
  insert into contractors (organization_id, name, kind) values (v_org, 'Heatshield Refractories', 'subcontractor') returning id into c_refr;

  v_proj := public.create_project(v_org, jsonb_build_object(
    'code', 'DEMO-BF2', 'name', 'Blast Furnace #2 — Skip Bridge & Stockhouse (Demo)',
    'client_id', v_client, 'consultant', 'Demo Engineering Consultants',
    'work_order_no', 'WO/BF2/2026/017', 'site_address', 'Plant Road, Demo Industrial Area, India',
    'start_date', v_start, 'completion_date', v_start + 180,
    'report_header', 'Demo Industrial Constructions Pvt Ltd — Daily Progress Report',
    'report_footer', 'Demo data. Generated by CONSTRUCT DPR.'));
  -- Demo policy only: the single demo user may approve their own reports.
  update projects set allow_self_approval = true, main_contractor_id = c_main,
         project_manager_id = auth.uid(), planning_engineer_id = auth.uid()
   where id = v_proj;

  insert into sites (project_id, code, name) values (v_proj, 'BF2', 'Blast Furnace #2 site');
  insert into areas (project_id, code, name, kind) values (v_proj, 'SKB', 'Skip Bridge', 'area') returning id into a_skb;
  insert into areas (project_id, code, name, kind) values (v_proj, 'STH', 'Stockhouse', 'area') returning id into a_sth;
  insert into areas (project_id, code, name, kind) values (v_proj, 'CHS', 'Cast House', 'area') returning id into a_chs;

  insert into wbs_nodes (project_id, code, name, sort_order) values (v_proj, '1', 'Civil works', 1) returning id into w1;
  insert into wbs_nodes (project_id, code, name, sort_order) values (v_proj, '2', 'Structural steel', 2) returning id into w2;
  insert into wbs_nodes (project_id, code, name, sort_order) values (v_proj, '3', 'Mechanical equipment', 3) returning id into w3;
  insert into wbs_nodes (project_id, code, name, sort_order) values (v_proj, '4', 'Refractory', 4) returning id into w4;
  insert into wbs_nodes (project_id, code, name, sort_order) values (v_proj, '5', 'Testing & commissioning', 5) returning id into w5;

  insert into boq_items (project_id, item_no, description, unit, tender_qty, rate) values
    (v_proj, 'C-01', 'Excavation in all soils incl. disposal', 'm³', 1200, 350) returning id into b_exc;
  insert into boq_items (project_id, item_no, description, unit, tender_qty, rate) values
    (v_proj, 'C-02', 'RCC M30 in foundations incl. formwork', 'm³', 800, 7800) returning id into b_rcc;
  insert into boq_items (project_id, item_no, description, unit, tender_qty, rate) values
    (v_proj, 'S-01', 'Fabrication of structural steel (E250/E350)', 'MT', 450, 85000) returning id into b_fab;
  insert into boq_items (project_id, item_no, description, unit, tender_qty, rate) values
    (v_proj, 'S-02', 'Erection of structural steel incl. alignment & bolting', 'MT', 450, 18000) returning id into b_ere;
  insert into boq_items (project_id, item_no, description, unit, tender_qty, rate) values
    (v_proj, 'M-01', 'Erection of mechanical equipment', 'MT', 120, 22000) returning id into b_mech;
  insert into boq_items (project_id, item_no, description, unit, tender_qty, rate) values
    (v_proj, 'R-01', 'Refractory lining incl. anchors', 'm³', 300, 45000) returning id into b_ref;

  -- Activities with planned dates laid out sequentially (refine with the scheduler).
  insert into activities (project_id, wbs_id, area_id, boq_item_id, contractor_id, code, name, location, unit, total_qty,
                          weight, original_duration, planned_start, planned_finish, is_milestone, responsible)
  values
    (v_proj, w1, a_skb, b_exc, c_civil, 'A1000', 'Excavation — skip bridge foundations', 'Grid A–D / 1–6', 'm³', 1200, 4, 12, v_start, v_start + 11, false, 'Site Engineer — Civil'),
    (v_proj, w1, a_skb, b_rcc, c_civil, 'A1010', 'RCC foundations — skip bridge', 'Grid A–D / 1–6', 'm³', 800, 12, 25, v_start + 8, v_start + 32, false, 'Site Engineer — Civil'),
    (v_proj, w2, a_skb, b_fab, c_steel, 'A2000', 'Fabrication — skip bridge trusses', 'Fabrication yard', 'MT', 300, 25, 40, v_start, v_start + 39, false, 'Fabrication In-charge'),
    (v_proj, w2, a_skb, b_ere, c_steel, 'A2010', 'Erection — skip bridge structure', 'Skip bridge EL +0 to +45 m', 'MT', 300, 15, 35, v_start + 14, v_start + 48, false, 'Erection Engineer'),
    (v_proj, w2, a_sth, b_ere, c_steel, 'A2020', 'Erection — stockhouse structure', 'Stockhouse bays 1–8', 'MT', 150, 8, 30, v_start + 30, v_start + 59, false, 'Erection Engineer'),
    (v_proj, w3, a_sth, b_mech, c_main, 'A3000', 'Erection — vibrating screens & feeders', 'Stockhouse EL +6.5 m', 'MT', 120, 10, 30, v_start + 55, v_start + 84, false, 'Mechanical Engineer'),
    (v_proj, w4, a_chs, b_ref, c_refr, 'A4000', 'Refractory lining — cast house runners', 'Cast house', 'm³', 300, 18, 40, v_start + 70, v_start + 109, false, 'Refractory Engineer'),
    (v_proj, w5, a_sth, null, c_main, 'A5000', 'No-load trials — stockhouse', 'Stockhouse', null, 0, 6, 10, v_start + 110, v_start + 119, false, 'Commissioning Engineer'),
    (v_proj, w5, null, null, c_main, 'M9000', 'Mechanical completion', null, null, 0, 0, 0, v_start + 120, v_start + 120, true, 'Project Manager');

  insert into activity_dependencies (project_id, predecessor_id, successor_id, type, lag_days)
  select v_proj, p.id, s.id, x.t::dependency_type, x.lag
  from (values ('A1000','A1010','SS',8), ('A1010','A2010','SS',6), ('A2000','A2010','SS',14),
               ('A2010','A2020','SS',16), ('A2020','A3000','FS',-5), ('A3000','A4000','SS',15),
               ('A4000','A5000','FS',0), ('A3000','A5000','FS',0), ('A5000','M9000','FS',0)) x(pc, sc, t, lag)
  join activities p on p.project_id = v_proj and p.code = x.pc
  join activities s on s.project_id = v_proj and s.code = x.sc;

  perform public.create_baseline(v_proj, 'Contract baseline (Rev 0)', 'Approved at kick-off', true);

  insert into materials (project_id, code, description, grade, unit, reorder_level) values
    (v_proj, 'STL-ISMB400', 'Rolled beam ISMB 400', 'E250', 'MT', 20) returning id into m_steel;
  insert into materials (project_id, code, description, grade, unit, reorder_level) values
    (v_proj, 'CEM-OPC53', 'Cement OPC 53 grade (50 kg bags)', 'OPC 53', 'bags', 500) returning id into m_cem;
  insert into materials (project_id, code, description, grade, unit, reorder_level) values
    (v_proj, 'BLT-M24', 'HSFG bolts M24 with nuts & washers', '8.8', 'nos', 1000) returning id into m_bolt;

  insert into equipment (project_id, code, type, owner_contractor_id, rated_capacity, certification_expiry) values
    (v_proj, 'CR-01', 'Mobile crane', c_steel, '100 T', current_date + 120) returning id into e_crane;
  insert into equipment (project_id, code, type, owner_contractor_id, rated_capacity, certification_expiry) values
    (v_proj, 'CR-02', 'Crawler crane', c_main, '250 T', current_date + 12) returning id into e_crawler;

  -- Seven days of DPRs, created, submitted and approved through the real workflow.
  for d in 0..6 loop
    v_dpr := jsonb_build_object(
      'project_id', v_proj, 'report_date', current_date - 7 + d, 'shift', 'day',
      'work_start', '08:00', 'work_end', '17:30', 'weather', case when d = 3 then 'Rain 10:00–12:00' else 'Clear, 34 °C' end,
      'site_conditions', case when d = 3 then 'Wet ground near grid C; work suspended 2 h' else 'Normal' end,
      'area_id', a_skb, 'toolbox_talks', 2, 'toolbox_topics', 'Working at height; crane signalling', 'permits_issued', 3,
      'work_completed', 'Excavation, RCC and steel work progressed as listed.',
      'tomorrow_plan', 'Continue erection of skip bridge trusses T3–T4; pour footing F6.',
      'delay_reasons', case when d = 3 then 'Rain stoppage' end,
      'activities', jsonb_build_array(
        jsonb_build_object('activity_id', (select id from activities where project_id = v_proj and code = 'A1000'),
                           'qty_today', case when d = 3 then 40 else 85 end, 'next_day_target', 90, 'sort_order', 1),
        jsonb_build_object('activity_id', (select id from activities where project_id = v_proj and code = 'A1010'),
                           'qty_today', case when d < 2 then 0 else 32 end, 'next_day_target', 35, 'sort_order', 2,
                           'delay_reason', case when d < 2 then 'Awaiting excavation front' end),
        jsonb_build_object('activity_id', (select id from activities where project_id = v_proj and code = 'A2000'),
                           'qty_today', 7.5, 'next_day_target', 8, 'sort_order', 3),
        jsonb_build_object('activity_id', (select id from activities where project_id = v_proj and code = 'A2010'),
                           'qty_today', case when d < 4 then 0 else 12 end, 'next_day_target', 12, 'sort_order', 4,
                           'location', 'Skip bridge trusses T1–T2')),
      'manpower', jsonb_build_array(
        jsonb_build_object('contractor_id', c_civil, 'trade', 'Civil', 'skilled', 18, 'semi_skilled', 12, 'unskilled', 30, 'supervisors', 2, 'engineers', 1, 'absent', 3, 'sort_order', 1),
        jsonb_build_object('contractor_id', c_steel, 'trade', 'Structural', 'skilled', 20 + d, 'semi_skilled', 8, 'unskilled', 10, 'supervisors', 2, 'engineers', 2, 'safety_staff', 1, 'overtime_hours', 12, 'sort_order', 2)),
      'equipment', jsonb_build_array(
        jsonb_build_object('equipment_id', e_crane, 'location', 'Skip bridge', 'operator', 'R. Kumar',
                           'operating_hours', 7, 'idle_hours', 2, 'breakdown_hours', 0, 'fuel_consumed', 65, 'fuel_unit', 'L', 'sort_order', 1),
        jsonb_build_object('equipment_id', e_crawler, 'location', 'Fabrication yard', 'operator', 'S. Patil',
                           'operating_hours', case when d = 5 then 3 else 6 end, 'idle_hours', 2,
                           'breakdown_hours', case when d = 5 then 4 else 0 end,
                           'breakdown_reason', case when d = 5 then 'Hydraulic hose failure' end, 'sort_order', 2)),
      'materials', jsonb_build_array(
        jsonb_build_object('material_id', m_cem, 'received_qty', case when d % 2 = 0 then 600 else 0 end,
                           'issued_qty', case when d < 2 then 0 else 220 end, 'consumed_qty', case when d < 2 then 0 else 210 end,
                           'challan_no', 'CH-' || (5100 + d), 'inspection_status', 'accepted', 'supplier', 'Demo Cements', 'sort_order', 1),
        jsonb_build_object('material_id', m_steel, 'received_qty', 15, 'issued_qty', 8, 'consumed_qty', 7.5,
                           'heat_batch_no', 'HT' || (88120 + d), 'inspection_status', 'accepted', 'supplier', 'Demo Steel Ltd', 'sort_order', 2)),
      'safety', case when d = 2 then jsonb_build_array(jsonb_build_object('category', 'unsafe_condition',
                  'description', 'Open edge at EL +12 m without guard rail', 'severity', 'high',
                  'corrective_action', 'Guard rail installed', 'responsible', 'Safety Officer', 'status', 'closed'))
                else '[]'::jsonb end,
      'constraints', case when d = 5 then jsonb_build_array(jsonb_build_object('category', 'design',
                  'description', 'Revised connection detail for truss T5 awaited from consultant', 'responsible', 'Design Coordinator',
                  'target_date', current_date + 3)) else '[]'::jsonb end);
    v_id := (public.save_dpr(v_dpr) ->> 'id')::uuid;
    perform public.submit_dpr(v_id);
    perform public.review_dpr(v_id, 'approve', 'Demo approval');
  end loop;

  return v_proj;
end $$;

revoke execute on function public.load_demo_project() from public, anon;
grant execute on function public.load_demo_project() to authenticated;

-- ===== 20261028000008_drive_sessions.sql =====
-- CONSTRUCT DPR — Migration 8: server-only storage for Google Drive
-- resumable-upload sessions. A session URI is a bearer capability for one
-- upload, so it is never exposed to browsers: RLS is enabled with no
-- policies and the API roles have no privileges. Only the `drive` edge
-- function (service role) reads and writes it.
create table public.drive_upload_sessions (
  attachment_id uuid primary key references public.attachments (id) on delete cascade,
  session_uri text not null,
  total_bytes bigint not null,
  folder_id text not null,
  created_at timestamptz not null default now()
);
alter table public.drive_upload_sessions enable row level security;
revoke all on public.drive_upload_sessions from anon, authenticated;

-- Allowed upload types and limits (checked by the edge function before any
-- bytes are accepted). Adjust per organisation policy.
alter table public.attachments add constraint attachments_mime_allowed check (
  mime_type ~ '^(image/(jpeg|png|webp|heic|heif)|video/(mp4|webm|quicktime)|audio/(webm|ogg|mpeg|mp4|wav|x-m4a|aac)|application/pdf|application/vnd\.openxmlformats-officedocument\.(spreadsheetml\.sheet|wordprocessingml\.document)|application/vnd\.ms-excel|text/csv|image/vnd\.dwg|application/acad|application/dxf|application/octet-stream)(;.*)?$');
alter table public.attachments add constraint attachments_size_limit check (size_bytes <= 1073741824);

-- ===== 20261029000009_integration_role.sql =====
-- CONSTRUCT DPR — Migration 9: machine/integration role.
-- (Enum values must be committed before use, so this sits in its own migration.)
alter type public.app_role add value if not exists 'integration';
