-- CONSTRUCT DPR — Migration 11: material stock is enforced at approval, not at save.
-- Opening stock comes from APPROVED reports only. Requiring a non-negative
-- closing balance while saving a draft blocked storekeepers whenever an earlier
-- receipt was still awaiting approval. Drafts may now go negative (the app
-- warns); approval recalculates the opening balance and refuses to approve a
-- report that would make approved stock negative.
do $$
declare c text;
begin
  for c in select conname from pg_constraint
           where conrelid = 'public.material_transactions'::regclass and contype = 'c'
             and pg_get_constraintdef(oid) like '%issued_qty%rejected_qty%>=%'
  loop
    execute format('alter table public.material_transactions drop constraint %I', c);
  end loop;
end $$;

create or replace function public.check_stock_on_approval() returns trigger
language plpgsql security definer set search_path = public as $$
declare bad text;
begin
  if new.status = 'approved' and old.status is distinct from 'approved' then
    update material_transactions t set opening_qty = coalesce((
      select l.closing_qty from material_ledger l
      left join dpr_headers h on h.id = l.dpr_id
      where l.material_id = t.material_id
        and (case when h.id is null then l.txn_date <= new.report_date
                  else h.seq <> new.seq and (h.report_date < new.report_date or (h.report_date = new.report_date and h.seq < new.seq)) end)
      order by l.txn_date desc, h.seq desc nulls first, l.sort_order desc limit 1), 0)
    where t.dpr_id = new.id;
    select string_agg(m.code || ' (closing ' || t.closing_qty::text || ')', ', ') into bad
    from material_transactions t join materials m on m.id = t.material_id
    where t.dpr_id = new.id and t.closing_qty < 0;
    if bad is not null then
      raise exception 'Issue exceeds approved stock for: %. Approve the earlier receipt reports first, or correct the quantities.', bad
        using errcode = 'PT422';
    end if;
  end if;
  return new;
end $$;
create trigger stock_check before update of status on public.dpr_headers
  for each row execute function public.check_stock_on_approval();
revoke execute on function public.check_stock_on_approval() from public, anon, authenticated;
