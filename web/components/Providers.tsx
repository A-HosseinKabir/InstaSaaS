'use client';

import { useEffect } from 'react';
import { AuthProvider } from '@/lib/auth';
import { ToastProvider } from './ui';

export default function Providers({ children }: { children: React.ReactNode }) {
  useEffect(() => {
    // Service worker = installable app + offline fallback. Production only, so dev reloads stay predictable.
    if (process.env.NODE_ENV === 'production' && 'serviceWorker' in navigator) {
      navigator.serviceWorker.register('/sw.js').catch(() => { /* not critical */ });
    }
  }, []);
  return (
    <AuthProvider>
      <ToastProvider>{children}</ToastProvider>
    </AuthProvider>
  );
}
