import type { Metadata, Viewport } from 'next';
import { Vazirmatn } from 'next/font/google';
import './globals.css';
import Providers from '@/components/Providers';
import { SITE } from '@/lib/site';

const vazir = Vazirmatn({ subsets: ['arabic', 'latin'], display: 'swap', variable: '--font-vazir' });

export const metadata: Metadata = {
  metadataBase: new URL(SITE.url),
  title: { default: `${SITE.name} | ${SITE.tagline}`, template: `%s | ${SITE.name}` },
  description: SITE.description,
  applicationName: SITE.name,
  keywords: [
    'پاسخ خودکار اینستاگرام', 'ربات اینستاگرام', 'اتوماسیون اینستاگرام', 'پاسخ خودکار دایرکت',
    'پاسخ خودکار کامنت', 'ربات دایرکت اینستاگرام', 'مدیریت پیج اینستاگرام', 'فروش در اینستاگرام',
  ],
  alternates: { canonical: '/' },
  openGraph: {
    type: 'website', locale: SITE.locale, siteName: SITE.name, url: '/',
    title: `${SITE.name} | ${SITE.tagline}`, description: SITE.description,
    images: [{ url: '/og.png', width: 1200, height: 630, alt: SITE.name }],
  },
  twitter: { card: 'summary_large_image', title: `${SITE.name} | ${SITE.tagline}`, description: SITE.description, images: ['/og.png'] },
  robots: { index: true, follow: true },
  manifest: '/manifest.webmanifest',
  icons: {
    icon: [{ url: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' }, { url: '/icons/icon.svg', type: 'image/svg+xml' }],
    apple: [{ url: '/icons/apple-touch-icon.png', sizes: '180x180' }],
  },
  appleWebApp: { capable: true, title: SITE.name, statusBarStyle: 'default' },
  formatDetection: { telephone: false },
};

export const viewport: Viewport = {
  width: 'device-width',
  initialScale: 1,
  viewportFit: 'cover',
  themeColor: [
    { media: '(prefers-color-scheme: light)', color: '#f3f4fb' },
    { media: '(prefers-color-scheme: dark)', color: '#0f1124' },
  ],
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="fa" dir="rtl" className={vazir.variable} suppressHydrationWarning>
      <body>
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}