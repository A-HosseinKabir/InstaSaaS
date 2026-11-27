'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useEffect, useRef, useState } from 'react';
import { api, errorText } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { faNum, normalizePhone, toEnglishDigits } from '@/lib/format';
import { Logo } from '@/components/icons';
import { SITE } from '@/lib/site';

export default function LoginPage() {
  const { ready, token, login } = useAuth();
  const router = useRouter();
  const [step, setStep] = useState<'phone' | 'code'>('phone');
  const [phoneRaw, setPhoneRaw] = useState('');
  const [phone, setPhone] = useState('');
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [wait, setWait] = useState(0);
  const codeRef = useRef<HTMLInputElement>(null);

  useEffect(() => { if (ready && token) router.replace('/dashboard'); }, [ready, token, router]);
  useEffect(() => {
    if (wait <= 0) return;
    const t = setTimeout(() => setWait((w) => w - 1), 1000);
    return () => clearTimeout(t);
  }, [wait]);
  useEffect(() => { if (step === 'code') codeRef.current?.focus(); }, [step]);

  async function sendCode(e?: React.FormEvent) {
    e?.preventDefault();
    const p = normalizePhone(phoneRaw);
    if (!p) { setError('شمارهٔ موبایل را درست وارد کن. مثال: ۰۹۱۲۱۲۳۴۵۶۷'); return; }
    setBusy(true); setError('');
    try {
      await api('/api/auth/request-otp', { method: 'POST', body: { phone: p }, auth: false });
      setPhone(p); setStep('code'); setCode(''); setWait(60);
    } catch (err) { setError(errorText(err)); }
    finally { setBusy(false); }
  }

  async function verify(c: string) {
    setBusy(true); setError('');
    try {
      const r = await api<{ token: string }>('/api/auth/verify-otp', { method: 'POST', body: { phone, code: c }, auth: false });
      login(r.token);
      router.replace('/dashboard');
    } catch (err) { setError(errorText(err)); setCode(''); codeRef.current?.focus(); }
    finally { setBusy(false); }
  }

  function onCode(v: string) {
    const d = toEnglishDigits(v).replace(/\D/g, '').slice(0, 5);
    setCode(d);
    if (d.length === 5 && !busy) void verify(d);      // submit by itself: one less tap
  }

  return (
    <main className="auth">
      <Link href="/" className="brand" aria-label={`صفحهٔ اصلی ${SITE.name}`}><Logo /> {SITE.name}</Link>

      {step === 'phone' ? (
        <form onSubmit={sendCode} className="stack" style={{ marginTop: '6vh' }} noValidate>
          <div>
            <h1>خوش آمدی 👋</h1>
            <p className="muted" style={{ marginTop: 6 }}>شمارهٔ موبایلت را بنویس تا یک کد تأیید برایت بفرستیم. رمز عبور لازم نیست.</p>
          </div>
          <div className="field">
            <label htmlFor="phone">شمارهٔ موبایل</label>
            <input id="phone" className="input ltr" inputMode="tel" autoComplete="tel" placeholder="09123456789"
              value={phoneRaw} onChange={(e) => setPhoneRaw(e.target.value)} aria-invalid={!!error} autoFocus />
            {error && <p className="field-err" role="alert">{error}</p>}
          </div>
          <button className="btn btn-primary btn-block" disabled={busy || phoneRaw.trim().length < 10}>
            {busy ? 'در حال ارسال…' : 'دریافت کد تأیید'}
          </button>
          <p className="hint">اگر حساب نداشته باشی، همین الان ساخته می‌شود و ۷ روز رایگان امتحانش می‌کنی.</p>
        </form>
      ) : (
        <div className="stack" style={{ marginTop: '6vh' }}>
          <div>
            <h1>کد را وارد کن</h1>
            <p className="muted" style={{ marginTop: 6 }}>
              کد ۵ رقمی به <b className="num">{faNum(phone)}</b> پیامک شد.
            </p>
          </div>
          <div className="field">
            <label htmlFor="code" className="sr-only">کد تأیید</label>
            <input id="code" ref={codeRef} className="input otp-input" inputMode="numeric" autoComplete="one-time-code"
              maxLength={5} placeholder="•••••" value={code} onChange={(e) => onCode(e.target.value)} disabled={busy} aria-invalid={!!error} />
            {error && <p className="field-err" role="alert">{error}</p>}
          </div>
          {busy && <p className="muted" style={{ textAlign: 'center' }}>در حال بررسی…</p>}
          <button className="btn btn-ghost btn-block" onClick={() => void sendCode()} disabled={wait > 0 || busy}>
            {wait > 0 ? `ارسال دوباره تا ${faNum(wait)} ثانیه دیگر` : 'کد نرسید؟ دوباره بفرست'}
          </button>
          <button className="btn btn-block" style={{ background: 'none' }} onClick={() => { setStep('phone'); setError(''); }}>
            شماره را اشتباه زدم
          </button>
        </div>
      )}
    </main>
  );
}
