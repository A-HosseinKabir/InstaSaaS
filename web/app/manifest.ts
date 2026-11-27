import type { MetadataRoute } from 'next';
import { SITE } from '@/lib/site';

export default function manifest(): MetadataRoute.Manifest {
  return {
    id: '/dashboard',
    name: `${SITE.name} | ${SITE.tagline}`,
    short_name: SITE.name,
    description: SITE.description,
    start_url: '/dashboard',
    scope: '/',
    display: 'standalone',
    orientation: 'portrait',
    dir: 'rtl',
    lang: 'fa',
    background_color: '#f3f4fb',
    theme_color: '#4b3fe0',
    categories: ['business', 'productivity'],
    icons: [
      { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png', purpose: 'any' },
      { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png', purpose: 'any' },
      { src: '/icons/maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
    ],
    shortcuts: [
      { name: 'اتوماسیون جدید', url: '/dashboard/automations/new' },
      { name: 'گزارش‌ها', url: '/dashboard/logs' },
    ],
  };
}
