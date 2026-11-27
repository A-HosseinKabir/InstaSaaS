'use client';

import Link from 'next/link';
import { api } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { faNum, relativeTime } from '@/lib/format';
import { eventLabel, logStatus } from '@/lib/labels';
import type { Account, Automation, LogItem, Subscription } from '@/lib/types';
import { IconCheck, IconPlus } from '@/components/icons';
import { Empty, ErrorBox, Skeleton, useLoad } from '@/components/ui';

export default function Home() {
  const { canEdit } = useAuth();
  const { data, loading, error, reload } = useLoad(async () => {
    const [accounts, subscription, automations, logs] = await Promise.all([
      api<Account[]>('/api/accounts'),
      api<Subscription>('/api/billing/subscription'),
      api<Automation[]>('/api/automations'),
      api<{ items: LogItem[] }>('/api/logs?pageSize=5'),
    ]);
    return { accounts, subscription, automations, logs: logs.items };
  });

  if (loading) return <><Skeleton h={110} /><Skeleton h={180} /><Skeleton h={160} /></>;
  if (error || !data) return <ErrorBox text={error ?? 'خطا'} onRetry={reload} />;

  const { accounts, subscription: sub, automations, logs } = data;
  const hasPage = accounts.length > 0;
  const hasAuto = automations.length > 0;
  const paid = sub.status === 'Active';
  const expired = sub.status === 'Expired' || sub.status === 'Cancelled';
  const allDone = hasPage && hasAuto && paid;
  const total = sub.status === 'Trial' ? 7 : 30;
  const pct = sub.daysLeft == null ? 100 : Math.min(100, Math.round((sub.daysLeft / total) * 100));
  const activeCount = automations.filter((a) => a.isActive).length;

  return (
    <>
      <header>
        <h1>سلام 👋</h1>
        <p className="muted">{hasAuto ? `${faNum(activeCount)} اتوماسیون فعال داری.` : 'بیا اولین اتوماسیونت را بسازیم.'}</p>
      </header>

      {expired && (
        <div className="notice notice-err">
          <span>اشتراکت تمام شده؛ پاسخ‌های خودکار فعلاً ارسال نمی‌شوند.</span>
          <Link href="/dashboard/billing" className="btn btn-sm btn-primary">تمدید</Link>
        </div>
      )}

      <section className="card bubble-card" aria-label="وضعیت اشتراک">
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <div>
            <div className="muted small">اشتراک</div>
            <h3>{sub.status === 'Trial' ? 'دورهٔ آزمایشی' : sub.plan ?? (expired ? 'منقضی' : '—')}</h3>
          </div>
          <span className={`badge ${expired ? 'badge-err' : 'badge-ok'}`}>
            {expired ? 'تمام شده' : sub.daysLeft == null ? 'فعال' : `${faNum(sub.daysLeft)} روز مانده`}
          </span>
        </div>
        {!expired && sub.daysLeft != null && <div className="progress" style={{ marginTop: 14 }}><i style={{ width: `${pct}%` }} /></div>}
      </section>

      {!allDone && (
        <section className="card" aria-label="مراحل شروع">
          <h3 style={{ marginBottom: 12 }}>برای شروع، این ۳ کار را انجام بده</h3>
          <div className="checklist">
            <Link href="/dashboard/accounts" className={`check ${hasPage ? 'done' : ''}`}>
              <span className="check-dot">{hasPage && <IconCheck />}</span>
              <span><b className="check-title">پیج اینستاگرام را وصل کن</b><br /><span className="muted small">امن و رسمی؛ رمزت را نمی‌گیریم</span></span>
            </Link>
            <Link href={hasPage ? '/dashboard/automations/new' : '/dashboard/accounts'} className={`check ${hasAuto ? 'done' : ''}`}>
              <span className="check-dot">{hasAuto && <IconCheck />}</span>
              <span><b className="check-title">اولین اتوماسیون را بساز</b><br /><span className="muted small">چند سؤال ساده، کمتر از ۲ دقیقه</span></span>
            </Link>
            <Link href="/dashboard/billing" className={`check ${paid ? 'done' : ''}`}>
              <span className="check-dot">{paid && <IconCheck />}</span>
              <span><b className="check-title">اشتراک را فعال نگه دار</b><br /><span className="muted small">بعد از دورهٔ رایگان</span></span>
            </Link>
          </div>
        </section>
      )}

      {canEdit && hasPage && (
        <Link href="/dashboard/automations/new" className="btn btn-primary btn-block"><IconPlus /> اتوماسیون جدید</Link>
      )}

      <section aria-label="آخرین فعالیت‌ها">
        <div className="row" style={{ justifyContent: 'space-between', marginBottom: 10 }}>
          <h3>آخرین فعالیت‌ها</h3>
          {logs.length > 0 && <Link href="/dashboard/logs" className="small">همه</Link>}
        </div>
        {logs.length === 0 ? (
          <Empty title="هنوز فعالیتی نیست" text="وقتی کسی به پیامی که اتوماسیون برایش تعریف کرده‌ای جواب بدهد، اینجا می‌بینی." />
        ) : (
          <ul className="list">
            {logs.map((l) => {
              const s = logStatus(l.status);
              return (
                <li key={l.id} className="card item">
                  <span className={`item-ico ${s.kind === 'ok' ? 'mint' : s.kind === 'err' ? 'coral' : ''}`}><IconCheck /></span>
                  <div className="grow">
                    <div className="item-title">{s.text}</div>
                    <div className="muted small">{eventLabel(l.eventType)} · {relativeTime(l.createdAt)}</div>
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </section>
    </>
  );
}
