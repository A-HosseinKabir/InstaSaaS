'use client';

import Link from 'next/link';
import { useState } from 'react';
import { api, errorText } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { faNum } from '@/lib/format';
import { triggerLabel } from '@/lib/labels';
import type { Account, Automation, ButtonContent } from '@/lib/types';
import { IconComment, IconDm, IconPlus } from '@/components/icons';
import { ConfirmSheet, Empty, ErrorBox, PageHeader, Skeleton, Switch, useLoad, useToast } from '@/components/ui';

function snippet(a: Automation): string {
  const r = a.responses[0];
  if (!r) return '';
  if (r.messageType === 'Image') return '🖼 تصویر';
  if (r.messageType === 'Button') { try { return (JSON.parse(r.content) as ButtonContent).message; } catch { return 'پیام دکمه‌دار'; } }
  return r.content;
}

export default function AutomationsPage() {
  const { canEdit } = useAuth();
  const { toast } = useToast();
  const { data, setData, loading, error, reload } = useLoad(async () => {
    const [automations, accounts] = await Promise.all([api<Automation[]>('/api/automations'), api<Account[]>('/api/accounts')]);
    return { automations, accounts };
  });
  const [del, setDel] = useState<Automation | null>(null);
  const [busy, setBusy] = useState(false);

  async function toggle(a: Automation, v: boolean) {
    if (!data) return;
    setData({ ...data, automations: data.automations.map((x) => (x.id === a.id ? { ...x, isActive: v } : x)) });   // optimistic
    try { await api(`/api/automations/${a.id}/active`, { method: 'PATCH', body: { isActive: v } }); }
    catch (e) { toast(errorText(e), 'err'); reload(); }
  }

  async function remove() {
    if (!del) return;
    setBusy(true);
    try { await api(`/api/automations/${del.id}`, { method: 'DELETE' }); toast('حذف شد'); setDel(null); reload(); }
    catch (e) { toast(errorText(e), 'err'); }
    finally { setBusy(false); }
  }

  const noPage = data && data.accounts.length === 0;
  const name = (id: string) => data?.accounts.find((a) => a.id === id)?.username;

  return (
    <>
      <PageHeader title="اتوماسیون‌ها" sub="هر اتوماسیون یعنی: وقتی کسی این کلمه را گفت، این جواب را بده."
        action={canEdit && !noPage ? <Link href="/dashboard/automations/new" className="btn btn-primary btn-sm"><IconPlus style={{ width: 18, height: 18 }} /> جدید</Link> : undefined} />

      {loading ? <Skeleton h={110} n={3} /> : error || !data ? <ErrorBox text={error ?? 'خطا'} onRetry={reload} /> :
        noPage ? (
          <Empty title="اول باید پیج وصل کنی" text="اتوماسیون روی یک پیج کار می‌کند. وصل کردن پیج یک دقیقه طول می‌کشد."
            action={canEdit ? <Link href="/dashboard/accounts" className="btn btn-primary">وصل کردن پیج</Link> : undefined} />
        ) : data.automations.length === 0 ? (
          <Empty title="هنوز اتوماسیونی نداری" text="مثلاً: هر کس زیر پست نوشت «قیمت»، قیمت‌ها را برایش در دایرکت بفرست."
            action={canEdit ? <Link href="/dashboard/automations/new" className="btn btn-primary">ساخت اولین اتوماسیون</Link> : undefined} />
        ) : (
          <ul className="list">
            {data.automations.map((a) => {
              const kws = a.keywords.split(',').filter(Boolean);
              return (
                <li key={a.id} className="card bubble-card">
                  <div className="item" style={{ alignItems: 'flex-start' }}>
                    <span className="item-ico">{a.triggerType === 'Comment' ? <IconComment /> : <IconDm />}</span>
                    <div className="grow">
                      <div className="row" style={{ justifyContent: 'space-between', gap: 8 }}>
                        <span className="item-title">{triggerLabel(a.triggerType)}</span>
                        <Switch checked={a.isActive} onChange={(v) => toggle(a, v)} disabled={!canEdit} label={`فعال بودن اتوماسیون ${kws[0] ?? ''}`} />
                      </div>
                      <div className="kw">{kws.slice(0, 4).map((k) => <span key={k}>{k}</span>)}{kws.length > 4 && <span>+{faNum(kws.length - 4)}</span>}</div>
                      <p className="muted small" style={{ marginTop: 8, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>{snippet(a)}</p>
                      <div className="row" style={{ marginTop: 10, justifyContent: 'space-between' }}>
                        <span className="muted small ltr">{data.accounts.length > 1 && name(a.accountId) ? `@${name(a.accountId)}` : ''}</span>
                        {canEdit && (
                          <span className="row" style={{ gap: 8 }}>
                            <button className="btn btn-ghost btn-sm" onClick={() => setDel(a)}>حذف</button>
                            <Link href={`/dashboard/automations/${a.id}`} className="btn btn-sm">ویرایش</Link>
                          </span>
                        )}
                      </div>
                    </div>
                  </div>
                </li>
              );
            })}
          </ul>
        )}

      <ConfirmSheet open={!!del} onClose={() => setDel(null)} busy={busy} danger title="این اتوماسیون حذف شود؟"
        text="بعد از حذف، دیگر به این کلمات جواب خودکار داده نمی‌شود. اگر فقط می‌خواهی موقتاً متوقف شود، به‌جای حذف آن را خاموش کن."
        confirmLabel="حذف" onConfirm={remove} />
    </>
  );
}
