import type { Metadata } from 'next';
import Link from 'next/link';
import { SITE } from '@/lib/site';

export const metadata: Metadata = { title: 'حریم خصوصی', description: `سیاست حفظ حریم خصوصی ${SITE.name}`, alternates: { canonical: '/privacy' } };

export default function Privacy() {
  return (
    <main className="wrap prose">
      <Link href="/">← {SITE.name}</Link>
      <h1>حریم خصوصی</h1>
      <p className="muted">پیش‌نویس: قبل از انتشار، این متن را با مشاوره حقوقی نهایی کنید.</p>
      <h2>چه اطلاعاتی نگه می‌داریم؟</h2>
      <p>شمارهٔ موبایل برای ورود، نام کاربری پیج‌های وصل‌شده، تنظیمات اتوماسیون‌ها و گزارش ارسال پیام‌ها. رمز عبور اینستاگرام شما را دریافت یا ذخیره نمی‌کنیم؛ اتصال از طریق صفحهٔ رسمی ورود اینستاگرام انجام می‌شود.</p>
      <h2>اطلاعات پرداخت</h2>
      <p>پرداخت در درگاه بانکی انجام می‌شود و اطلاعات کارت شما نزد ما ذخیره نمی‌شود.</p>
      <h2>حذف اطلاعات</h2>
      <p>می‌توانید هر زمان پیج را از حساب جدا کنید. برای حذف کامل حساب با پشتیبانی تماس بگیرید.</p>
    </main>
  );
}
