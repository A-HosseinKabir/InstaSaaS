import Link from 'next/link';
import type { Metadata } from 'next';
import { Logo } from '@/components/icons';
import { API_URL, SITE } from '@/lib/site';
import type { Plan } from '@/lib/types';

export const metadata: Metadata = {
  title: { absolute: `${SITE.name} | پاسخ خودکار کامنت و دایرکت اینستاگرام` },
  description: SITE.description,
  alternates: { canonical: '/' },
};

export const revalidate = 3600;

const FAQ = [
  { q: 'برای وصل کردن پیج، رمز اینستاگرامم را به شما می‌دهم؟', a: 'نه. اتصال از طریق صفحهٔ رسمی ورود اینستاگرام انجام می‌شود و رمز عبور شما را نه دریافت می‌کنیم و نه ذخیره. هر وقت خواستی، پیج را از داخل برنامه جدا می‌کنی.' },
  { q: 'پیجم باید چه نوعی باشد؟', a: 'پیج باید حرفه‌ای (Business یا Creator) باشد. تبدیل پیج شخصی به حرفه‌ای در تنظیمات اینستاگرام رایگان است و چند ثانیه طول می‌کشد.' },
  { q: 'روی گوشی هم کار می‌کند؟', a: 'بله، برنامه اول برای گوشی طراحی شده. از مرورگر باز می‌شود و می‌توانی آن را مثل یک اپ روی صفحهٔ اصلی اندروید یا آیفون نصب کنی.' },
  { q: 'مشتری فالو نکرده باشد چه می‌شود؟', a: 'هر پیامی را می‌توانی «فقط برای فالوورها» علامت بزنی. اگر مشتری فالو نکرده باشد، به او می‌گوییم ابتدا پیج را فالو کند و دوباره پیام بدهد.' },
  { q: 'می‌توانم چند نفر با هم پیج را مدیریت کنیم؟', a: 'بله. با شمارهٔ موبایل دعوتشان می‌کنی و نقش «ادمین» یا «بیننده» می‌دهی. هر وقت خواستی دسترسی را قطع می‌کنی.' },
  { q: 'دورهٔ رایگان دارد؟', a: 'بله، با اولین ورود ۷ روز رایگان داری. نیازی به کارت بانکی نیست.' },
];

const FEATURES = [
  { t: 'جواب کامنت در دایرکت', d: 'کسی زیر پست نوشت «قیمت»؟ قیمت را همان لحظه در دایرکتش می‌فرستیم.' },
  { t: 'کلمه‌های فارسی را درست می‌فهمد', d: 'فرق «ی» و «ي»، «ک» و «ك» و اعداد فارسی و انگلیسی را خودش حساب می‌کند.' },
  { t: 'پیام متنی، عکس یا دکمه', d: 'هر چند پیام که خواستی پشت‌سرهم بفرست؛ حتی دکمه‌ای که مشتری را به سایت یا واتساپ می‌برد.' },
  { t: 'فقط برای فالوورها', d: 'قیمت یا کد تخفیف را فقط به کسانی نشان بده که پیجت را دنبال می‌کنند.' },
  { t: 'یادآوری خودکار', d: 'اگر مشتری جواب نداد، چند ساعت بعد یک پیام دوستانه می‌فرستیم.' },
  { t: 'کار تیمی', d: 'همکارانت را با شمارهٔ موبایل دعوت کن؛ ادمین‌ها می‌سازند، بیننده‌ها فقط می‌بینند.' },
];

async function getPlans(): Promise<Plan[]> {
  try {
    const r = await fetch(`${API_URL}/api/billing/plans`, { next: { revalidate: 3600 } });
    if (!r.ok) return [];
    return (await r.json()) as Plan[];
  } catch { return []; }
}

const fa = new Intl.NumberFormat('fa-IR');

export default async function Landing() {
  const plans = await getPlans();

  const jsonLd = {
    '@context': 'https://schema.org',
    '@graph': [
      { '@type': 'Organization', '@id': `${SITE.url}/#org`, name: SITE.name, url: SITE.url, logo: `${SITE.url}/icons/icon-512.png` },
      { '@type': 'WebSite', '@id': `${SITE.url}/#site`, url: SITE.url, name: SITE.name, inLanguage: 'fa-IR', publisher: { '@id': `${SITE.url}/#org` } },
      {
        '@type': 'SoftwareApplication', name: SITE.name, applicationCategory: 'BusinessApplication', operatingSystem: 'Android, iOS, Web',
        description: SITE.description, inLanguage: 'fa-IR', url: SITE.url,
        ...(plans.length ? { offers: plans.map((p) => ({ '@type': 'Offer', name: p.name, price: String(p.priceRial), priceCurrency: 'IRR' })) } : {}),
      },
      { '@type': 'FAQPage', mainEntity: FAQ.map((f) => ({ '@type': 'Question', name: f.q, acceptedAnswer: { '@type': 'Answer', text: f.a } })) },
    ],
  };

  return (
    <>
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd).replace(/</g, '\\u003c') }} />

      <header className="site-head">
        <div className="wrap">
          <Link href="/" className="brand"><Logo /> {SITE.name}</Link>
          <nav className="site-nav" aria-label="منوی سایت">
            <a href="#how">چطور کار می‌کند</a><a href="#features">امکانات</a><a href="#pricing">قیمت</a><a href="#faq">سؤالات</a>
          </nav>
          <Link href="/login" className="btn btn-primary btn-sm">ورود</Link>
        </div>
      </header>

      <main>
        <section className="wrap hero" aria-labelledby="h1">
          <div>
            <h1 id="h1">به کامنت و دایرکت اینستاگرامت، خودکار جواب بده</h1>
            <p className="lead">
              مشتری زیر پستت نوشت «قیمت»؟ قبل از اینکه گوشی را برداری، قیمت در دایرکتش است. تنظیمش از روی گوشی و با چند سؤال ساده انجام می‌شود.
            </p>
            <div className="hero-cta">
              <Link href="/login" className="btn btn-primary">شروع رایگان ۷ روزه</Link>
              <a href="#how" className="btn btn-ghost">ببین چطور کار می‌کند</a>
            </div>
            <p className="hero-note">بدون کارت بانکی · رمز اینستاگرامت را نمی‌گیریم · قابل نصب روی اندروید و آیفون</p>
          </div>

          <div className="phone" aria-label="نمونهٔ گفتگوی خودکار">
            <div className="phone-bar"><span className="phone-dot" /> فروشگاه نمونه</div>
            <div className="phone-chat" aria-hidden="true">
              <div className="msg msg-meta demo">زیر پست جدید کامنت گذاشت</div>
              <div className="msg msg-in demo">قیمت؟</div>
              <div className="msg msg-meta demo">چند ثانیه بعد، در دایرکت</div>
              <div className="msg msg-out demo">سلام! 👋 قیمت‌ها از ۲۰۰ هزار تومان شروع می‌شود.</div>
              <div className="msg msg-out demo">کاتالوگ کامل و ثبت سفارش 👇<span className="msg-btn">دیدن کاتالوگ</span></div>
              <div className="msg msg-in demo">عالی، سفارش می‌دم 🙏</div>
            </div>
          </div>
        </section>

        <section id="how" className="wrap sec" aria-labelledby="how-h">
          <h2 id="how-h" style={{ marginBottom: 28 }}>سه قدم تا اولین پاسخ خودکار</h2>
          <ol className="steps-flow">
            <li className="step-row"><span className="step-n">۱</span><div><h3>پیجت را وصل کن</h3><p className="muted">با ورود رسمی اینستاگرام، در چند ثانیه. رمزت پیش ما نمی‌ماند.</p></div></li>
            <li className="step-row"><span className="step-n">۲</span><div><h3>به سؤال‌ها جواب بده</h3><p className="muted">هر صفحه یک سؤال دارد و راهنما کنارش است. نیازی به اسکرول و فرم طولانی نیست.</p></div></li>
            <li className="step-row"><span className="step-n">۳</span><div><h3>روشنش کن</h3><p className="muted">از همان لحظه به مشتری‌ها جواب می‌دهد؛ حتی نصفه‌شب.</p></div></li>
          </ol>
        </section>

        <section id="features" className="wrap sec" aria-labelledby="f-h">
          <h2 id="f-h" style={{ marginBottom: 28 }}>هر چیزی که برای جواب دادن لازم داری</h2>
          <div className="feat">
            {FEATURES.map((f) => <div key={f.t} className="feat-row"><h3>{f.t}</h3><p className="muted">{f.d}</p></div>)}
          </div>
        </section>

        <section id="pricing" className="wrap sec" aria-labelledby="p-h">
          <h2 id="p-h" style={{ marginBottom: 8 }}>قیمت ساده، بدون شگفتی</h2>
          <p className="muted" style={{ marginBottom: 24 }}>همه‌چیز را ۷ روز رایگان امتحان کن. هر زمان خواستی پلنت را عوض کن.</p>
          {plans.length === 0 ? (
            <p className="muted">پلن‌ها بعد از ورود در بخش «اشتراک» نمایش داده می‌شوند.</p>
          ) : (
            <div className="price-grid">
              {plans.map((p) => (
                <div key={p.id} className="card plan bubble-card">
                  <h3>{p.name}</h3>
                  <div className="plan-price">{fa.format(Math.round(p.priceRial / 10))} <span className="muted small">تومان / {fa.format(p.durationDays)} روز</span></div>
                  <ul>
                    <li>✓ {fa.format(p.maxInstagramAccounts)} پیج اینستاگرام</li>
                    <li>✓ {fa.format(p.maxAutomations)} اتوماسیون</li>
                    <li>✓ پیام‌های نامحدود</li>
                  </ul>
                  <Link href="/login" className="btn btn-primary btn-block">شروع رایگان</Link>
                </div>
              ))}
            </div>
          )}
        </section>

        <section id="faq" className="wrap sec faq" aria-labelledby="q-h">
          <h2 id="q-h" style={{ marginBottom: 20 }}>سؤال‌هایی که زیاد می‌پرسند</h2>
          {FAQ.map((f) => <details key={f.q}><summary>{f.q}</summary><p>{f.a}</p></details>)}
        </section>

        <section className="wrap sec-tight">
          <div className="cta-band">
            <h2>امشب مشتری‌ات بی‌جواب نماند</h2>
            <p>ثبت‌نام با شمارهٔ موبایل است و کمتر از یک دقیقه طول می‌کشد.</p>
            <Link href="/login" className="btn">شروع رایگان</Link>
          </div>
        </section>
      </main>

      <footer className="wrap site-foot">
        <div className="brand"><Logo size={26} /> {SITE.name}</div>
        <p>{SITE.tagline}</p>
        <nav aria-label="پیوندهای پایین صفحه"><Link href="/privacy">حریم خصوصی</Link><Link href="/terms">قوانین استفاده</Link><Link href="/login">ورود</Link></nav>
        <p className="small">این سرویس وابسته به اینستاگرام یا Meta نیست.</p>
      </footer>
    </>
  );
}
