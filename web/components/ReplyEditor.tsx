'use client';

import { useEffect, useState } from 'react';
import { MAX_BUTTONS, MAX_BUTTON_TITLE, MAX_TEXT } from '@/lib/keyword';
import type { ButtonContent, MessageType, ResponseDto, TriggerType } from '@/lib/types';
import { IconImage, IconLink, IconPlus, IconText, IconTrash } from './icons';
import { Sheet, Switch } from './ui';

interface Draft {
  type: MessageType; text: string; imageUrl: string;
  buttonMsg: string; buttons: { title: string; url: string }[]; requireFollow: boolean;
}
const EMPTY: Draft = { type: 'Text', text: '', imageUrl: '', buttonMsg: '', buttons: [{ title: '', url: '' }], requireFollow: false };

const isHttps = (u: string) => { try { return new URL(u.trim()).protocol === 'https:'; } catch { return false; } };

export function toDraft(r: ResponseDto | null): Draft {
  if (!r) return { ...EMPTY, buttons: [{ title: '', url: '' }] };
  const d: Draft = { ...EMPTY, type: r.messageType, requireFollow: r.requireFollow, buttons: [{ title: '', url: '' }] };
  if (r.messageType === 'Text') d.text = r.content;
  else if (r.messageType === 'Image') d.imageUrl = r.content;
  else {
    try {
      const bc = JSON.parse(r.content) as ButtonContent;
      d.buttonMsg = bc.message ?? '';
      const web = (bc.buttons ?? []).filter((b) => b.type === 'web_url');
      if (web.length) d.buttons = web.map((b) => ({ title: b.title, url: b.url }));
    } catch { /* keep empty */ }
  }
  return d;
}

function validate(d: Draft): Record<string, string> {
  const e: Record<string, string> = {};
  if (d.type === 'Text') {
    if (!d.text.trim()) e.text = 'متن پیام را بنویس.';
    else if (d.text.length > MAX_TEXT) e.text = `متن حداکثر ${MAX_TEXT} کاراکتر باشد.`;
  } else if (d.type === 'Image') {
    if (!isHttps(d.imageUrl)) e.imageUrl = 'آدرس تصویر باید با https شروع شود.';
  } else {
    if (!d.buttonMsg.trim()) e.buttonMsg = 'متن بالای دکمه‌ها را بنویس.';
    else if (d.buttonMsg.length > MAX_TEXT) e.buttonMsg = `متن حداکثر ${MAX_TEXT} کاراکتر باشد.`;
    d.buttons.forEach((b, i) => {
      if (!b.title.trim() || b.title.length > MAX_BUTTON_TITLE) e[`bt${i}`] = `عنوان دکمه ۱ تا ${MAX_BUTTON_TITLE} حرف باشد.`;
      if (!isHttps(b.url)) e[`bu${i}`] = 'لینک باید با https شروع شود.';
    });
  }
  return e;
}

export function toResponse(d: Draft): ResponseDto {
  if (d.type === 'Text') return { messageType: 'Text', content: d.text.trim(), requireFollow: d.requireFollow };
  if (d.type === 'Image') return { messageType: 'Image', content: d.imageUrl.trim(), requireFollow: d.requireFollow };
  const bc: ButtonContent = {
    message: d.buttonMsg.trim(),
    buttons: d.buttons.map((b) => ({ type: 'web_url' as const, title: b.title.trim(), url: b.url.trim() })),
  };
  return { messageType: 'Button', content: JSON.stringify(bc), requireFollow: d.requireFollow };
}

export default function ReplyEditor({
  open, initial, trigger, isFirst, onSave, onClose,
}: { open: boolean; initial: ResponseDto | null; trigger: TriggerType; isFirst: boolean; onSave: (r: ResponseDto) => void; onClose: () => void }) {
  const [d, setD] = useState<Draft>(EMPTY);
  const [tried, setTried] = useState(false);

  useEffect(() => { if (open) { setD(toDraft(initial)); setTried(false); } }, [open, initial]);

  const errs = tried ? validate(d) : {};
  const set = (p: Partial<Draft>) => setD((x) => ({ ...x, ...p }));
  const setBtn = (i: number, p: Partial<{ title: string; url: string }>) =>
    setD((x) => ({ ...x, buttons: x.buttons.map((b, k) => (k === i ? { ...b, ...p } : b)) }));

  function save() {
    setTried(true);
    if (Object.keys(validate(d)).length) return;
    onSave(toResponse(d));
  }

  return (
    <Sheet open={open} onClose={onClose} title={initial ? 'ویرایش پیام' : 'پیام جدید'}
      footer={<button className="btn btn-primary btn-block" onClick={save}>ذخیرهٔ پیام</button>}>
      <div className="seg" role="group" aria-label="نوع پیام">
        <button aria-pressed={d.type === 'Text'} onClick={() => set({ type: 'Text' })}><IconText /> متن</button>
        <button aria-pressed={d.type === 'Image'} onClick={() => set({ type: 'Image' })}><IconImage /> تصویر</button>
        <button aria-pressed={d.type === 'Button'} onClick={() => set({ type: 'Button' })}><IconLink /> دکمه‌دار</button>
      </div>

      {d.type === 'Text' && (
        <div className="field">
          <label htmlFor="rt">متن پیام</label>
          <textarea id="rt" className="textarea" value={d.text} onChange={(e) => set({ text: e.target.value })} aria-invalid={!!errs.text}
            placeholder={'مثلاً:\nسلام! قیمت‌ها از ۲۰۰ هزار تومان شروع می‌شود. کدام مدل را می‌خواهی؟'} />
          <div className="counter">{d.text.length} / {MAX_TEXT}</div>
          {errs.text && <p className="field-err" role="alert">{errs.text}</p>}
          <p className="hint">کوتاه و دوستانه بنویس؛ شبیه چیزی که خودت در دایرکت می‌نویسی. می‌توانی از ایموجی هم استفاده کنی.</p>
        </div>
      )}

      {d.type === 'Image' && (
        <div className="field">
          <label htmlFor="ri">آدرس تصویر</label>
          <input id="ri" className="input ltr" inputMode="url" placeholder="https://example.com/photo.jpg" value={d.imageUrl}
            onChange={(e) => set({ imageUrl: e.target.value })} aria-invalid={!!errs.imageUrl} />
          {errs.imageUrl && <p className="field-err" role="alert">{errs.imageUrl}</p>}
          {isHttps(d.imageUrl) && (
            // eslint-disable-next-line @next/next/no-img-element
            <img className="msg-img" style={{ maxWidth: 260 }} src={d.imageUrl.trim()} alt="پیش‌نمایش تصویر" referrerPolicy="no-referrer" />
          )}
          <p className="hint">تصویر را جایی آپلود کن (سایت خودت یا سرویس عکس) و لینک مستقیمش را اینجا بگذار. لینک باید به خود عکس برسد، نه یک صفحهٔ وب.</p>
        </div>
      )}

      {d.type === 'Button' && (
        <>
          <div className="field">
            <label htmlFor="rb">متن پیام</label>
            <textarea id="rb" className="textarea" style={{ minHeight: 96 }} value={d.buttonMsg} onChange={(e) => set({ buttonMsg: e.target.value })}
              aria-invalid={!!errs.buttonMsg} placeholder="مثلاً: برای دیدن کاتالوگ و سفارش روی دکمه بزن 👇" />
            {errs.buttonMsg && <p className="field-err" role="alert">{errs.buttonMsg}</p>}
          </div>
          {d.buttons.map((b, i) => (
            <div key={i} className="card" style={{ background: 'var(--surface-2)', boxShadow: 'none' }}>
              <div className="row" style={{ justifyContent: 'space-between', marginBottom: 8 }}>
                <b>دکمهٔ {i + 1}</b>
                {d.buttons.length > 1 && <button className="icon-btn" aria-label={`حذف دکمهٔ ${i + 1}`} onClick={() => set({ buttons: d.buttons.filter((_, k) => k !== i) })}><IconTrash /></button>}
              </div>
              <div className="stack">
                <div className="field">
                  <input className="input" placeholder="نوشتهٔ روی دکمه (مثلاً: دیدن کاتالوگ)" maxLength={MAX_BUTTON_TITLE + 10}
                    value={b.title} onChange={(e) => setBtn(i, { title: e.target.value })} aria-label={`عنوان دکمهٔ ${i + 1}`} aria-invalid={!!errs[`bt${i}`]} />
                  {errs[`bt${i}`] && <p className="field-err" role="alert">{errs[`bt${i}`]}</p>}
                </div>
                <div className="field">
                  <input className="input ltr" inputMode="url" placeholder="https://..." value={b.url}
                    onChange={(e) => setBtn(i, { url: e.target.value })} aria-label={`لینک دکمهٔ ${i + 1}`} aria-invalid={!!errs[`bu${i}`]} />
                  {errs[`bu${i}`] && <p className="field-err" role="alert">{errs[`bu${i}`]}</p>}
                </div>
              </div>
            </div>
          ))}
          {d.buttons.length < MAX_BUTTONS && (
            <button className="btn btn-ghost" onClick={() => set({ buttons: [...d.buttons, { title: '', url: '' }] })}><IconPlus style={{ width: 18, height: 18 }} /> دکمهٔ دیگر</button>
          )}
          <p className="hint">حداکثر {MAX_BUTTONS} دکمه؛ هر کدام کاربر را به یک لینک می‌برد (سایت، واتساپ، فروشگاه…).</p>
        </>
      )}

      <div className="card" style={{ background: 'var(--surface-2)', boxShadow: 'none' }}>
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <div className="grow"><b>فقط برای کسانی که فالو کرده‌اند</b>
            <p className="muted small">اول چک می‌کنیم کاربر پیج را فالو دارد. اگر نداشت، از او می‌خواهیم فالو کند.</p></div>
          <Switch checked={d.requireFollow} onChange={(v) => set({ requireFollow: v })} label="فقط برای فالوورها" />
        </div>
        {d.requireFollow && <p className="small" style={{ marginTop: 8 }}>این پیام و همهٔ پیام‌های بعد از آن بعد از بررسی فالو ارسال می‌شوند.</p>}
      </div>

      {trigger === 'Comment' && isFirst && (
        <p className="muted small">این اولین پیام است و به‌صورت «پاسخ خصوصی به کامنت» فرستاده می‌شود. اینستاگرام اجازه می‌دهد برای هر کامنت فقط یک بار این کار انجام شود.</p>
      )}
    </Sheet>
  );
}
