'use client';

import { useState } from 'react';
import { api, errorText } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { faDate } from '@/lib/format';
import { useQuery } from '@/lib/useQuery';
import type { Account } from '@/lib/types';
import { IconInsta, IconTrash } from '@/components/icons';
import { ConfirmSheet, Empty, ErrorBox, Notice, PageHeader, Skeleton, useLoad, useToast } from '@/components/ui';

const REASONS: Record<string, string> = {
  session: 'مهلت اتصال تمام شد. دوباره از اول شروع کن.',
  boxapi: 'ارتباط با سرویس اتصال برقرار نشد. چند دقیقه بعد دوباره امتحان کن.',
  not_found: 'پیج پیدا نشد. مطمئن شو در مرحلهٔ آخر اتصال را تأیید کرده‌ای.',
  already_linked: 'این پیج قبلاً به یک حساب دیگر وصل شده است.',
};

export default function AccountsPage() {
  const { isOwner, canEdit } = useAuth();
  const { toast } = useToast();
  const q = useQuery();
  const { data, loading, error, reload } = useLoad(() => api<Account[]>('/api/accounts'));
  const [busy, setBusy] = useState(false);
  const [del, setDel] = useState<Account | null>(null);

  async function connect() {
    setBusy(true);
    try {
      const r = await api<{ url: string }>('/api/accounts/connect', { method: 'POST' });
      window.location.href = r.url;            // official Instagram login (we never see the password)
    } catch (e) { toast(errorText(e), 'err'); setBusy(false); }
  }

  async function remove() {
    if (!del) return;
    setBusy(true);
    try {
      await api(`/api/accounts/${del.id}`, { method: 'DELETE' });
      toast('پیج جدا شد'); setDel(null); reload();
    } catch (e) { toast(errorText(e), 'err'); }
    finally { setBusy(false); }
  }

  return (
    <>
      <PageHeader title="پیج‌های اینستاگرام" back="/dashboard/more" />

      {q.status === 'success' && <Notice kind="ok">پیج با موفقیت وصل شد 🎉 حالا می‌توانی اتوماسیون بسازی.</Notice>}
      {q.status === 'error' && <Notice kind="err">{REASONS[q.reason ?? ''] ?? 'اتصال انجام نشد. دوباره امتحان کن.'}</Notice>}

      {loading ? <Skeleton h={80} n={2} /> : error || !data ? <ErrorBox text={error ?? 'خطا'} onRetry={reload} /> : data.length === 0 ? (
        <>
          <Empty title="هنوز پیجی وصل نیست" text="با وصل کردن پیج، می‌توانیم از طرف تو به کامنت‌ها و دایرکت‌ها جواب بدهیم." />
          <section className="card">
            <h3 style={{ marginBottom: 10 }}>چطور وصل کنم؟</h3>
            <ol className="stack" style={{ paddingInlineStart: 20 }}>
              <li>دکمهٔ «وصل کردن پیج» را بزن.</li>
              <li>وارد صفحهٔ رسمی اینستاگرام می‌شوی؛ با حساب خودت وارد شو و اجازه بده.</li>
              <li>خودکار به همین‌جا برمی‌گردی.</li>
            </ol>
            <div className="tip" style={{ marginTop: 14 }}>
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="9" /><path d="M12 11v5M12 8h.01" /></svg>
              <div><b>مهم</b>پیج باید «حرفه‌ای» (Business یا Creator) باشد. از تنظیمات اینستاگرام می‌توانی رایگان تغییرش بدهی.</div>
            </div>
          </section>
        </>
      ) : (
        <ul className="list">
          {data.map((a) => (
            <li key={a.id} className="card item">
              <span className="item-ico"><IconInsta /></span>
              <div className="grow">
                <div className="item-title ltr">@{a.username}</div>
                <div className="muted small">{a.isActive ? 'متصل و فعال' : 'غیرفعال'}{a.expiresAt ? ` · اعتبار اتصال تا ${faDate(a.expiresAt)}` : ''}</div>
              </div>
              {isOwner && <button className="icon-btn" aria-label={`جدا کردن ${a.username}`} onClick={() => setDel(a)}><IconTrash /></button>}
            </li>
          ))}
        </ul>
      )}

      {canEdit && <button className="btn btn-primary btn-block" onClick={connect} disabled={busy}>{busy ? 'کمی صبر کن…' : 'وصل کردن پیج'}</button>}

      <ConfirmSheet open={!!del} onClose={() => setDel(null)} busy={busy} danger
        title="این پیج جدا شود؟" confirmLabel="بله، جدا کن" onConfirm={remove}
        text="اتوماسیون‌های این پیج هم پاک می‌شوند و این کار برگشت‌پذیر نیست. اگر دوباره بخواهی، باید پیج را از اول وصل کنی." />
    </>
  );
}
