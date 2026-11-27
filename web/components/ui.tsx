'use client';

import Link from 'next/link';
import { createContext, useCallback, useContext, useEffect, useId, useRef, useState } from 'react';
import { IconBack, IconClose } from './icons';

/* ---------- toast ---------- */
type ToastKind = 'ok' | 'err';
interface ToastCtx { toast: (msg: string, kind?: ToastKind) => void }
const ToastContext = createContext<ToastCtx>({ toast: () => {} });
export const useToast = () => useContext(ToastContext);

export function ToastProvider({ children }: { children: React.ReactNode }) {
  const [items, setItems] = useState<{ id: number; msg: string; kind: ToastKind }[]>([]);
  const seq = useRef(0);
  const toast = useCallback((msg: string, kind: ToastKind = 'ok') => {
    const id = ++seq.current;
    setItems((x) => [...x.slice(-2), { id, msg, kind }]);
    setTimeout(() => setItems((x) => x.filter((i) => i.id !== id)), 3800);
  }, []);
  return (
    <ToastContext.Provider value={{ toast }}>
      {children}
      <div className="toasts" role="status" aria-live="polite">
        {items.map((t) => <div key={t.id} className={`toast toast-${t.kind}`}>{t.msg}</div>)}
      </div>
    </ToastContext.Provider>
  );
}

/* ---------- bottom sheet ---------- */
export function Sheet({
  open, onClose, title, children, footer,
}: { open: boolean; onClose: () => void; title: string; children: React.ReactNode; footer?: React.ReactNode }) {
  const titleId = useId();
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    ref.current?.focus();
    return () => { document.removeEventListener('keydown', onKey); document.body.style.overflow = prev; };
  }, [open, onClose]);

  if (!open) return null;
  return (
    <div className="sheet-root">
      <div className="sheet-backdrop" onClick={onClose} />
      <div className="sheet" role="dialog" aria-modal="true" aria-labelledby={titleId} ref={ref} tabIndex={-1}>
        <div className="sheet-grab" aria-hidden="true" />
        <div className="sheet-head">
          <h2 id={titleId}>{title}</h2>
          <button className="icon-btn" onClick={onClose} aria-label="بستن"><IconClose /></button>
        </div>
        <div className="sheet-body">{children}</div>
        {footer && <div className="sheet-foot">{footer}</div>}
      </div>
    </div>
  );
}

export function ConfirmSheet({
  open, onClose, title, text, confirmLabel, danger, busy, onConfirm,
}: {
  open: boolean; onClose: () => void; title: string; text: string; confirmLabel: string;
  danger?: boolean; busy?: boolean; onConfirm: () => void;
}) {
  return (
    <Sheet open={open} onClose={onClose} title={title}
      footer={
        <div className="row-2">
          <button className="btn btn-ghost" onClick={onClose} disabled={busy}>انصراف</button>
          <button className={`btn ${danger ? 'btn-danger' : 'btn-primary'}`} onClick={onConfirm} disabled={busy}>
            {busy ? 'کمی صبر کنید…' : confirmLabel}
          </button>
        </div>
      }>
      <p className="muted">{text}</p>
    </Sheet>
  );
}

/* ---------- small pieces ---------- */
export function Switch({ checked, onChange, label, disabled }: { checked: boolean; onChange: (v: boolean) => void; label: string; disabled?: boolean }) {
  return (
    <button type="button" role="switch" aria-checked={checked} aria-label={label} disabled={disabled}
      className={`switch ${checked ? 'on' : ''}`} onClick={() => onChange(!checked)}>
      <span className="switch-knob" />
    </button>
  );
}

export function Spinner() { return <span className="spinner" role="status" aria-label="در حال بارگذاری" />; }

export function Skeleton({ h = 72, n = 1 }: { h?: number; n?: number }) {
  return <>{Array.from({ length: n }).map((_, i) => <div key={i} className="skeleton" style={{ height: h }} />)}</>;
}

export function Empty({ title, text, action }: { title: string; text: string; action?: React.ReactNode }) {
  return (
    <div className="empty">
      <div className="empty-bubble" aria-hidden="true"><i /><i /><i /></div>
      <h3>{title}</h3>
      <p className="muted">{text}</p>
      {action}
    </div>
  );
}

export function ErrorBox({ text, onRetry }: { text: string; onRetry?: () => void }) {
  return (
    <div className="notice notice-err" role="alert">
      <span>{text}</span>
      {onRetry && <button className="btn btn-ghost btn-sm" onClick={onRetry}>تلاش دوباره</button>}
    </div>
  );
}

export function Notice({ kind = 'info', children }: { kind?: 'info' | 'ok' | 'warn' | 'err'; children: React.ReactNode }) {
  return <div className={`notice notice-${kind}`} role={kind === 'err' ? 'alert' : undefined}>{children}</div>;
}

export function PageHeader({ title, sub, back, action }: { title: string; sub?: string; back?: string; action?: React.ReactNode }) {
  return (
    <header className="page-head">
      {back && <Link href={back} className="icon-btn" aria-label="بازگشت"><IconBack /></Link>}
      <div className="page-head-text">
        <h1>{title}</h1>
        {sub && <p className="muted">{sub}</p>}
      </div>
      {action}
    </header>
  );
}

/* ---------- tiny data hook ---------- */
export function useLoad<T>(fn: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  const fnRef = useRef(fn);
  fnRef.current = fn;

  useEffect(() => {
    let alive = true;
    setLoading(true);
    setError(null);
    fnRef.current()
      .then((d) => { if (alive) setData(d); })
      .catch((e) => { if (alive) setError(e?.message ?? 'خطا'); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tick, ...deps]);

  return { data, setData, error, loading, reload: () => setTick((t) => t + 1) };
}
