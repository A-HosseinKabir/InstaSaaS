# وب‌اپ پاسخیار (Next.js، موبایل‌محور، قابل نصب)

## اجرا
```
cd web
copy .env.example .env.local      (در لینوکس/مک: cp)
npm install
npm run dev
```
بعد http://localhost:3000 را باز کن.

- `NEXT_PUBLIC_API_URL` باید آدرس همان API باشد که روی آن `/swagger` را باز می‌کنی (مثلاً `https://localhost:57363`).
- اگر API با HTTPS توسعه اجرا می‌شود، یک بار این را بزن تا مرورگر گواهی را قبول کند: `dotnet dev-certs https --trust`
- برای تست روی گوشی در همان وای‌فای: آدرس `http://IP-کامپیوتر:3000` را به `Cors:AllowedOrigins` در `appsettings.Development.json` اضافه کن و API را روی IP هم در دسترس بگذار.
- نصب به‌صورت اپ (PWA) فقط روی HTTPS واقعی (یا localhost) کار می‌کند؛ بعد از استقرار روی دامنه فعال می‌شود.

## قبل از انتشار
1. نام برند در `lib/site.ts` (فعلاً «پاسخیار» است؛ اسم خودت را بگذار) و آدرس‌ها در `.env.local`.
2. آیکون‌ها و تصویر شبکه‌های اجتماعی: `public/icons/*` و `public/og.png`.
3. متن‌های `/privacy` و `/terms` پیش‌نویس هستند و باید با مشاور حقوقی نهایی شوند.
4. `npm run build` و `npm start` را روی سرور اجرا کن.

## ساختار
- `app/page.tsx` صفحهٔ فرود (سئو: متادیتا، JSON-LD، سایت‌مپ، robots)
- `app/login` ورود با OTP
- `app/dashboard/*` خانه، پیج‌ها، اتوماسیون‌ها، گزارش‌ها، اشتراک، تیم
- `components/AutomationWizard.tsx` ویزارد تمام‌صفحه، یک سؤال در هر صفحه
