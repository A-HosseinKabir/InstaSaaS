'use client';

import Link from 'next/link';
import { usePathname, useRouter } from 'next/navigation';
import { useEffect } from 'react';
import { useAuth } from '@/lib/auth';
import { IconBolt, IconHome, IconList, IconMore } from './icons';
import InstallPrompt from './InstallPrompt';
import { Spinner } from './ui';

const TABS = [
  { href: '/dashboard', label: 'خانه', Icon: IconHome, match: (p: string) => p === '/dashboard' },
  { href: '/dashboard/automations', label: 'اتوماسیون‌ها', Icon: IconBolt, match: (p: string) => p.startsWith('/dashboard/automations') },
  { href: '/dashboard/logs', label: 'گزارش‌ها', Icon: IconList, match: (p: string) => p.startsWith('/dashboard/logs') },
  { href: '/dashboard/more', label: 'بیشتر', Icon: IconMore,
    match: (p: string) => ['more', 'accounts', 'billing', 'team'].some((x) => p.startsWith('/dashboard/' + x)) },
];

export default function DashboardShell({ children }: { children: React.ReactNode }) {
  const { ready, token } = useAuth();
  const router = useRouter();
  const path = usePathname() ?? '';

  useEffect(() => { if (ready && !token) router.replace('/login'); }, [ready, token, router]);

  if (!ready || !token) {
    return <div style={{ minHeight: '100dvh', display: 'grid', placeItems: 'center' }}><Spinner /></div>;
  }

  // The automation wizard takes the whole screen: no tab bar, nothing to distract from the current question.
  const immersive = path === '/dashboard/automations/new' || /^\/dashboard\/automations\/[^/]+$/.test(path);
  if (immersive) return <>{children}</>;

  return (
    <div className="app">
      <main className="app-main">{children}</main>
      <nav className="tabbar" aria-label="منوی اصلی">
        {TABS.map(({ href, label, Icon, match }) => (
          <Link key={href} href={href} className="tab" aria-current={match(path) ? 'page' : undefined}>
            <Icon /> <span>{label}</span>
          </Link>
        ))}
      </nav>
      <InstallPrompt />
    </div>
  );
}
