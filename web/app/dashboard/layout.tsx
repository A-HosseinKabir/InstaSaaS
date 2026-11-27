import type { Metadata } from 'next';
import DashboardShell from '@/components/DashboardShell';

export const metadata: Metadata = {
  title: { default: 'داشبورد', template: '%s | پاسخیار' },
  robots: { index: false, follow: false },
};

export default function Layout({ children }: { children: React.ReactNode }) {
  return <DashboardShell>{children}</DashboardShell>;
}
