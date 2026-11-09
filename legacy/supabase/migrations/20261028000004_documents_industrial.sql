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
