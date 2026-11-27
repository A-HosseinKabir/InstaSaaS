'use client';

import { useEffect, useState } from 'react';
import { IconShare } from './icons';
import { Sheet } from './ui';

interface BIPEvent extends Event { prompt: () => Promise<void>; userChoice: Promise<{ outcome: string }> }
const KEY = 'ps_install_dismissed';

export default function InstallPrompt() {
  const [evt, setEvt] = useState<BIPEvent | null>(null);
  const [ios, setIos] = useState(false);
  const [show, setShow] = useState(false);
  const [help, setHelp] = useState(false);

  useEffect(() => {
    try { if (localStorage.getItem(KEY)) return; } catch { /* ignore */ }
    const standalone = window.matchMedia('(display-mode: standalone)').matches || (navigator as unknown as { standalone?: boolean }).standalone;
    if (standalone) return;

    const ua = navigator.userAgent;
    const isIos = /iphone|ipad|ipod/i.test(ua) && !/crios|fxios/i.test(ua);      // only Safari can install on iOS
    if (isIos) { setIos(true); const t = setTimeout(() => setShow(true), 6000); return () => clearTimeout(t); }

    const onBip = (e: Event) => { e.preventDefault(); setEvt(e as BIPEvent); setTimeout(() => setShow(true), 4000); };
    window.addEventListener('beforeinstallprompt', onBip);
    return () => window.removeEventListener('beforeinstallprompt', onBip);
  }, []);

  const dismiss = () => { setShow(false); try { localStorage.setItem(KEY, '1'); } catch { /* ignore */ } };
  const install = async () => {
    if (ios) { setHelp(true); return; }
    if (!evt) return;
    await evt.prompt();
    await evt.userChoice;
    dismiss();
  };

  return (
    <>
      {show && (
        <div className="install" role="region" aria-label="نصب برنامه">
          <div className="grow">
            <b>برنامه را روی گوشی نصب کن</b>
            <div className="small" style={{ opacity: .8 }}>بدون دردسر، مثل یک اپ معمولی باز می‌شود.</div>
          </div>
          <button className="btn btn-sm" onClick={install}>نصب</button>
          <button className="icon-btn" onClick={dismiss} aria-label="بعداً">✕</button>
        </div>
      )}
      <Sheet open={help} onClose={() => { setHelp(false); dismiss(); }} title="نصب روی آیفون">
        <ol className="stack" style={{ paddingInlineStart: 20 }}>
          <li>دکمهٔ «اشتراک‌گذاری» <IconShare style={{ width: 18, height: 18, verticalAlign: 'middle' }} /> را در پایین Safari بزن.</li>
          <li>گزینهٔ «Add to Home Screen» (افزودن به صفحهٔ اصلی) را انتخاب کن.</li>
          <li>روی «Add» بزن. حالا آیکون برنامه روی صفحهٔ اصلی گوشی است.</li>
        </ol>
        <p className="muted small">اگر دکمهٔ اشتراک‌گذاری را نمی‌بینی، لینک را در Safari باز کن؛ مرورگرهای دیگر روی آیفون نصب را پشتیبانی نمی‌کنند.</p>
      </Sheet>
    </>
  );
}
