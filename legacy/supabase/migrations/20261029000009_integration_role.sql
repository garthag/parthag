-- CONSTRUCT DPR — Migration 9: machine/integration role.
-- (Enum values must be committed before use, so this sits in its own migration.)
alter type public.app_role add value if not exists 'integration';
