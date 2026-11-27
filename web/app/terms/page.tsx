import type { Metadata } from 'next';
import Link from 'next/link';
import { SITE } from '@/lib/site';

export const metadata: Metadata = { title: 'قوانین استفاده', description: `شرایط استفاده از ${SITE.name}`, alternates: { canonical: '/terms' } };

export default function Terms() {
  return (
    <main className="wrap prose">
      <Link href="/">← {SITE.name}</Link>
      <h1>قوانین استفاده</h1>
      <p className="muted">پیش‌نویس: قبل از انتشار، این متن را با مشاوره حقوقی نهایی کنید.</p>
      <h2>استفاده درست</h2>
      <p>از سرویس برای ارسال اسپم، محتوای فریبنده یا نقض قوانین اینستاگرام استفاده نکنید. مسئولیت محتوای پیام‌ها با صاحب پیج است.</p>
      <h2>محدودیت‌های اینستاگرام</h2>
      <p>ارسال پیام تابع قوانین و محدودیت‌های اینستاگرام است (مثلاً پنجرهٔ ۲۴ ساعته). ما نمی‌توانیم ارسال هر پیام را تضمین کنیم.</p>
      <h2>اشتراک</h2>
      <p>اشتراک به‌صورت دوره‌ای پرداخت می‌شود و با پایان دوره، پاسخ‌های خودکار متوقف می‌شوند.</p>
    </main>
  );
}
