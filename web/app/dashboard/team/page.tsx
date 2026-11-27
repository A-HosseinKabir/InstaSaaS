'use client';

import { useRouter } from 'next/navigation';
import { useState } from 'react';
import { api, errorText } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { faDate, normalizePhone } from '@/lib/format';
import { roleHelp, roleLabel } from '@/lib/labels';
import type { Member, MyInvite, PendingInvite, Role } from '@/lib/types';
import { IconPlus } from '@/components/icons';
import { ConfirmSheet, ErrorBox, Notice, PageHeader, Sheet, Skeleton, useLoad, useToast } from '@/components/ui';

export default function TeamPage() {
  const { isOwner, claims, login, logout } = useAuth();
  const router = useRouter();
  const { toast } = useToast();
  const { data, loading, error, reload } = useLoad(async () => {
    const [members, mine] = await Promise.all([api<Member[]>('/api/team/members'), api<MyInvite[]>('/api/team/my-invites')]);
    const pending = isOwner || claims?.role === 'Admin' ? await api<PendingInvite[]>('/api/team/invites').catch(() => []) : [];
    return { members, mine, pending };
  }, [isOwner, claims?.role]);

  const [busy, setBusy] = useState(false);
  const [invite, setInvite] = useState(false);
  const [phone, setPhone] = useState('');
  const [role, setRole] = useState<Role>('Admin');
  const [formErr, setFormErr] = useState('');
  const [edit, setEdit] = useState<Member | null>(null);
  const [remove, setRemove] = useState<Member | null>(null);
  const [leave, setLeave] = useState(false);

  async function run(fn: () => Promise<void>, okMsg?: string) {
    setBusy(true);
    try { await fn(); if (okMsg) toast(okMsg); reload(); } catch (e) { toast(errorText(e), 'err'); }
    finally { setBusy(false); }
  }

  async function sendInvite() {
    const p = normalizePhone(phone);
    if (!p) { setFormErr('شمارهٔ موبایل را درست وارد کن.'); return; }
    setBusy(true); setFormErr('');
    try {
      await api('/api/team/invites', { method: 'POST', body: { phone: p, role } });
      toast('دعوت‌نامه ارسال شد'); setInvite(false); setPhone(''); reload();
    } catch (e) { setFormErr(errorText(e)); }
    finally { setBusy(false); }
  }

  async function accept(i: MyInvite) {
    setBusy(true);
    try {
      const r = await api<{ token: string }>(`/api/team/my-invites/${i.id}/accept`, { method: 'POST' });
      login(r.token); toast(`به «${i.tenant}» پیوستی`); router.replace('/dashboard');
    } catch (e) { toast(errorText(e), 'err'); }
    finally { setBusy(false); }
  }

  return (
    <>
      <PageHeader title="تیم" sub="دیگران را دعوت کن تا کمکت کنند." back="/dashboard/more"
        action={isOwner ? <button className="btn btn-primary btn-sm" onClick={() => { setInvite(true); setFormErr(''); }}><IconPlus style={{ width: 18, height: 18 }} /> دعوت</button> : undefined} />

      {loading ? <Skeleton h={76} n={3} /> : error || !data ? <ErrorBox text={error ?? 'خطا'} onRetry={reload} /> : (
        <>
          {data.mine.map((i) => (
            <div key={i.id} className="card bubble-card stack">
              <div><b>دعوت به «{i.tenant}»</b><p className="muted small">با نقش {roleLabel(i.role)} · اعتبار تا {faDate(i.expiresAt)}</p></div>
              <div className="row-2">
                <button className="btn btn-ghost" disabled={busy} onClick={() => run(() => api(`/api/team/my-invites/${i.id}`, { method: 'DELETE' }), 'دعوت رد شد')}>رد کردن</button>
                <button className="btn btn-primary" disabled={busy} onClick={() => accept(i)}>قبول می‌کنم</button>
              </div>
            </div>
          ))}

          <ul className="list">
            {data.members.map((m) => (
              <li key={m.userId} className="card item">
                <span className="item-ico">{m.phone.slice(-2)}</span>
                <div className="grow">
                  <div className="item-title ltr">{m.phone}</div>
                  <div className="muted small">{roleLabel(m.role)}{m.userId === claims?.sub ? ' · شما' : ''}</div>
                </div>
                {isOwner && !m.isOwner && (
                  <span className="row" style={{ gap: 6 }}>
                    <button className="btn btn-sm" onClick={() => { setEdit(m); setRole(m.role); }}>نقش</button>
                    <button className="btn btn-ghost btn-sm" onClick={() => setRemove(m)}>حذف</button>
                  </span>
                )}
              </li>
            ))}
          </ul>

          {data.pending.length > 0 && (
            <section>
              <h3 style={{ marginBottom: 10 }}>دعوت‌های باز</h3>
              <ul className="list">
                {data.pending.map((p) => (
                  <li key={p.id} className="card item">
                    <div className="grow"><div className="item-title ltr">{p.phone}</div><div className="muted small">{roleLabel(p.role)} · تا {faDate(p.expiresAt)}</div></div>
                    {isOwner && <button className="btn btn-ghost btn-sm" disabled={busy} onClick={() => run(() => api(`/api/team/invites/${p.id}`, { method: 'DELETE' }), 'دعوت لغو شد')}>لغو</button>}
                  </li>
                ))}
              </ul>
            </section>
          )}

          {!isOwner && <button className="btn btn-ghost btn-block" onClick={() => setLeave(true)}>ترک این حساب</button>}
          {!isOwner && <Notice kind="info">نقش شما: {roleLabel(claims?.role ?? 'Viewer')}. {roleHelp(claims?.role ?? 'Viewer')}.</Notice>}
        </>
      )}

      <Sheet open={invite} onClose={() => setInvite(false)} title="دعوت عضو جدید"
        footer={<button className="btn btn-primary btn-block" disabled={busy} onClick={sendInvite}>{busy ? 'در حال ارسال…' : 'ارسال دعوت‌نامه'}</button>}>
        <div className="field">
          <label htmlFor="ph">شمارهٔ موبایل</label>
          <input id="ph" className="input ltr" inputMode="tel" placeholder="09123456789" value={phone} onChange={(e) => setPhone(e.target.value)} aria-invalid={!!formErr} />
          {formErr && <p className="field-err" role="alert">{formErr}</p>}
        </div>
        <div className="field">
          <span className="label">نقش</span>
          <div className="seg" role="group" aria-label="نقش">
            {(['Admin', 'Viewer'] as Role[]).map((r) => <button key={r} aria-pressed={role === r} onClick={() => setRole(r)}>{roleLabel(r)}</button>)}
          </div>
          <p className="hint">{roleHelp(role)}</p>
        </div>
        <p className="muted small">برای او پیامک می‌رود. بعد از ورود، دعوت را در همین صفحه می‌بیند و باید خودش قبول کند.</p>
      </Sheet>

      <Sheet open={!!edit} onClose={() => setEdit(null)} title="تغییر نقش"
        footer={edit && <div className="row-2">
          <button className="btn btn-ghost" onClick={() => setEdit(null)}>انصراف</button>
          <button className="btn btn-primary" disabled={busy} onClick={() => run(async () => { await api(`/api/team/members/${edit.userId}/role`, { method: 'PATCH', body: { role } }); setEdit(null); }, 'نقش تغییر کرد')}>ذخیره</button>
        </div>}>
        {edit && (
          <>
            <p className="ltr">{edit.phone}</p>
            <div className="seg" role="group" aria-label="نقش">
              {(['Admin', 'Viewer'] as Role[]).map((r) => <button key={r} aria-pressed={role === r} onClick={() => setRole(r)}>{roleLabel(r)}</button>)}
            </div>
            <p className="hint">{roleHelp(role)}</p>
          </>
        )}
      </Sheet>

      <ConfirmSheet open={!!remove} onClose={() => setRemove(null)} busy={busy} danger title="این عضو حذف شود؟"
        text="دسترسی او فوراً قطع می‌شود." confirmLabel="حذف"
        onConfirm={() => run(async () => { await api(`/api/team/members/${remove!.userId}`, { method: 'DELETE' }); setRemove(null); }, 'عضو حذف شد')} />
      <ConfirmSheet open={leave} onClose={() => setLeave(false)} busy={busy} danger title="این حساب را ترک می‌کنی؟"
        text="دسترسی‌ات به این حساب قطع می‌شود و برای برگشتن باید دوباره دعوت شوی." confirmLabel="ترک کن"
        onConfirm={() => run(async () => { await api('/api/team/leave', { method: 'POST' }); logout(); router.replace('/login'); })} />
    </>
  );
}
