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
