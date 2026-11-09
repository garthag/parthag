-- Assertion helpers shared by the database test files (session-local functions).
create or replace function pg_temp.ok(cond boolean, msg text) returns void language plpgsql as $$
begin
  if cond is distinct from true then raise exception 'FAILED: %', msg; end if;
  raise notice 'ok - %', msg;
end $$;

create or replace function pg_temp.fails(sql text, expect text, msg text) returns void language plpgsql as $$
begin
  execute sql;
  raise exception 'FAILED (no error raised): %', msg;
exception when others then
  if sqlerrm like 'FAILED (no error raised)%' then raise; end if;
  if position(lower(expect) in lower(sqlerrm)) = 0 then
    raise exception 'FAILED: % — unexpected error: %', msg, sqlerrm;
  end if;
  raise notice 'ok - % (rejected: %)', msg, left(sqlerrm, 90);
end $$;

create or replace function pg_temp.login(p uuid) returns void language plpgsql as $$
begin
  perform set_config('request.jwt.claims', json_build_object('sub', p, 'role', 'authenticated')::text, false);
end $$;
grant execute on all functions in schema pg_temp to authenticated;
