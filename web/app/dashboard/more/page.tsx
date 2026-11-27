'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useState } from 'react';
import { api, errorText } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { roleLabel } from '@/lib/labels';
import type { TenantRow } from '@/lib/types';
import { IconBack, IconCard, IconInsta, IconLogout, IconUsers } from '@/components/icons';
import { ConfirmSheet, PageHeader, useLoad, useToast } from '@/components/ui';

export default function MorePage() {
  const { claims, logout, login } = useAuth();
  const router = useRouter();
  const { toast } = useToast();
  const [out, setOut] = useState(false);
  const { data: tenants } = useLoad(() => api<TenantRow[]>('/api/team/tenants'));

  async function switchTo(id: string) {
    try {
      const r = await api<{ token: string }>('/api/team/switch', { method: 'POST', body: { tenantId: id } });
      login(r.token);
      toast('حساب عوض شد');
      router.replace('/dashboard');
    } catch (e) { toast(errorText(e), 'err'); }
  }

  const items = [
    { href: '/dashboard/accounts', label: 'پیج‌های اینستاگرام', Icon: IconInsta },
    { href: '/dashboard/billing', label: 'اشتراک و پرداخت', Icon: IconCard },
    { href: '/dashboard/team', label: 'تیم و دسترسی‌ها', Icon: IconUsers },
  ];

  return (
    <>
      <PageHeader title="بیشتر" sub={claims ? `نقش شما در این حساب: ${roleLabel(claims.role)}` : undefined} />
      <nav className="card menu-list" aria-label="بخش‌های بیشتر">
        {items.map(({ href, label, Icon }) => (
          <Link key={href} href={href} className="menu-item"><Icon /> <span className="grow">{label}</span> <IconBack style={{ width: 18, height: 18, opacity: .4 }} /></Link>
        ))}
      </nav>

      {tenants && tenants.length > 1 && (
        <section className="card">
          <h3 style={{ marginBottom: 10 }}>حساب‌های من</h3>
          <p className="muted small" style={{ marginBottom: 10 }}>تو عضو چند حساب هستی. برای رفتن به هر کدام روی آن بزن.</p>
          <ul className="list">
            {tenants.map((t) => (
              <li key={t.tenantId}>
                <button className="menu-item" onClick={() => !t.current && switchTo(t.tenantId)} aria-current={t.current}>
                  <span className="grow"><b>{t.name}</b><br /><span className="muted small">{roleLabel(t.role)}</span></span>
                  {t.current && <span className="badge badge-ok">فعلی</span>}
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}

      <button className="btn btn-ghost btn-block" onClick={() => setOut(true)}><IconLogout style={{ width: 20, height: 20 }} /> خروج از حساب</button>
      <ConfirmSheet open={out} onClose={() => setOut(false)} title="از حساب خارج می‌شوی؟" text="اتوماسیون‌ها همچنان روی سرور کار می‌کنند. هر وقت خواستی با شمارهٔ موبایلت دوباره وارد شو."
        confirmLabel="خروج" onConfirm={() => { logout(); router.replace('/login'); }} />
    </>
  );
}
