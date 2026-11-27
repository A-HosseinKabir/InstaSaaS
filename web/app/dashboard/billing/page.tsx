'use client';

import { useState } from 'react';
import { api, errorText } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { faDate, faNum, toman } from '@/lib/format';
import { useQuery } from '@/lib/useQuery';
import type { Invoice, Plan, Subscription } from '@/lib/types';
import { IconCheck } from '@/components/icons';
import { ErrorBox, Notice, PageHeader, Skeleton, useLoad, useToast } from '@/components/ui';

const BANNERS: Record<string, { kind: 'ok' | 'warn' | 'err'; text: string }> = {
  success: { kind: 'ok', text: 'پرداخت انجام شد و اشتراکت فعال است. ممنون! 🎉' },
  pending: { kind: 'warn', text: 'پرداخت در حال بررسی است. اگر پول از حسابت کم شده، تا چند دقیقهٔ دیگر خودکار فعال می‌شود.' },
  review: { kind: 'warn', text: 'مبلغ پرداخت‌شده با فاکتور هم‌خوانی نداشت و برای بررسی ثبت شد. با پشتیبانی تماس بگیر.' },
  failed: { kind: 'err', text: 'پرداخت انجام نشد. اگر پولی کم شده، تا ۷۲ ساعت به حسابت برمی‌گردد. می‌توانی دوباره امتحان کنی.' },
};
const STATUS_LABEL = { Pending: 'در انتظار', Paid: 'پرداخت‌شده', Failed: 'ناموفق' } as const;

export default function BillingPage() {
  const { isOwner } = useAuth();
  const { toast } = useToast();
  const q = useQuery();
  const [busy, setBusy] = useState<string | null>(null);
  const { data, loading, error, reload } = useLoad(async () => {
    const [plans, sub] = await Promise.all([api<Plan[]>('/api/billing/plans', { auth: false }), api<Subscription>('/api/billing/subscription')]);
    const invoices = isOwner ? await api<Invoice[]>('/api/billing/invoices').catch(() => []) : [];
    return { plans, sub, invoices };
  }, [isOwner]);

  async function buy(plan: Plan) {
    setBusy(plan.id);
    try {
      const r = await api<{ paymentUrl: string }>('/api/billing/checkout', { method: 'POST', body: { planId: plan.id } });
      window.location.href = r.paymentUrl;
    } catch (e) { toast(errorText(e), 'err'); setBusy(null); }
  }

  const banner = q.status ? BANNERS[q.status] : undefined;

  return (
    <>
      <PageHeader title="اشتراک و پرداخت" back="/dashboard/more" />
      {banner && <Notice kind={banner.kind}>{banner.text}</Notice>}

      {loading ? <><Skeleton h={90} /><Skeleton h={220} n={2} /></> : error || !data ? <ErrorBox text={error ?? 'خطا'} onRetry={reload} /> : (
        <>
          <section className="card bubble-card">
            <div className="muted small">وضعیت فعلی</div>
            <h3>{data.sub.status === 'Trial' ? 'دورهٔ آزمایشی' : data.sub.plan ?? 'بدون پلن'}</h3>
            <p className="muted" style={{ marginTop: 4 }}>
              {data.sub.expireDate
                ? data.sub.status === 'Expired' ? `در تاریخ ${faDate(data.sub.expireDate)} تمام شد.` : `تا ${faDate(data.sub.expireDate)} (${faNum(data.sub.daysLeft ?? 0)} روز دیگر)`
                : '—'}
            </p>
          </section>

          {!isOwner && <Notice kind="info">پرداخت فقط توسط مالک حساب انجام می‌شود.</Notice>}

          <h2>پلن‌ها</h2>
          {data.plans.length === 0 ? <Notice kind="warn">هنوز پلنی تعریف نشده است.</Notice> : (
            <ul className="list">
              {data.plans.map((p) => {
                const current = data.sub.plan === p.name && data.sub.status === 'Active';
                return (
                  <li key={p.id} className="card plan">
                    <div className="row" style={{ justifyContent: 'space-between' }}>
                      <h3>{p.name}</h3>
                      {current && <span className="badge badge-ok">پلن فعلی شما</span>}
                    </div>
                    <div className="plan-price">{toman(p.priceRial)} <span className="muted small">/ {faNum(p.durationDays)} روز</span></div>
                    <ul>
                      <li><IconCheck /> {faNum(p.maxInstagramAccounts)} پیج اینستاگرام</li>
                      <li><IconCheck /> {faNum(p.maxAutomations)} اتوماسیون</li>
                      <li><IconCheck /> پیام‌های نامحدود</li>
                    </ul>
                    {isOwner && (
                      <button className={`btn ${current ? 'btn-ghost' : 'btn-primary'} btn-block`} disabled={busy !== null} onClick={() => buy(p)}>
                        {busy === p.id ? 'در حال انتقال به درگاه…' : current ? 'تمدید' : 'خرید این پلن'}
                      </button>
                    )}
                  </li>
                );
              })}
            </ul>
          )}
          <p className="hint">پرداخت از درگاه امن زیبال انجام می‌شود. اطلاعات کارتت پیش ما نمی‌ماند.</p>

          {isOwner && data.invoices.length > 0 && (
            <section>
              <h2 style={{ marginBottom: 10 }}>فاکتورها</h2>
              <ul className="list">
                {data.invoices.map((i) => (
                  <li key={i.id} className="card row" style={{ justifyContent: 'space-between' }}>
                    <div><b>{toman(i.amountRial)}</b><div className="muted small">{faDate(i.createdAt)}</div></div>
                    <span className={`badge ${i.status === 'Paid' ? 'badge-ok' : i.status === 'Failed' ? 'badge-err' : 'badge-warn'}`}>{STATUS_LABEL[i.status]}</span>
                  </li>
                ))}
              </ul>
            </section>
          )}
        </>
      )}
    </>
  );
}
