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
