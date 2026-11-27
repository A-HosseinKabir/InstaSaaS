'use client';

import { useCallback, useEffect, useState } from 'react';
import { api } from '@/lib/api';
import { faDateTime } from '@/lib/format';
import { eventLabel, logStatus } from '@/lib/labels';
import type { LogItem } from '@/lib/types';
import { Empty, ErrorBox, PageHeader, Skeleton } from '@/components/ui';

const FILTERS = [
  { v: '', label: 'همه' },
  { v: 'ok', label: 'موفق' },
  { v: 'error', label: 'خطا' },
  { v: 'follow_required', label: 'فالو نکرده' },
];

export default function LogsPage() {
  const [filter, setFilter] = useState('');
  const [items, setItems] = useState<LogItem[]>([]);
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const load = useCallback(async (p: number, f: string, append: boolean) => {
    setLoading(true); setError('');
    try {
      const r = await api<{ total: number; items: LogItem[] }>(`/api/logs?page=${p}&pageSize=30${f ? `&status=${f}` : ''}`);
      setItems((old) => (append ? [...old, ...r.items] : r.items));
      setTotal(r.total); setPage(p);
    } catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { void load(1, filter, false); }, [filter, load]);

  return (
    <>
      <PageHeader title="گزارش‌ها" sub="ببین هر پیام چه شد." />
      <div className="chips" role="group" aria-label="فیلتر وضعیت">
        {FILTERS.map((f) => (
          <button key={f.v} className={`chip ${filter === f.v ? '' : 'chip-btn'}`} aria-pressed={filter === f.v} onClick={() => setFilter(f.v)}>{f.label}</button>
        ))}
      </div>

      {error ? <ErrorBox text={error} onRetry={() => load(1, filter, false)} /> : loading && items.length === 0 ? <Skeleton h={68} n={5} /> :
        items.length === 0 ? <Empty title="گزارشی نیست" text="وقتی اتوماسیون‌ها شروع به کار کنند، نتیجهٔ هر پاسخ اینجا ثبت می‌شود." /> : (
          <>
            <ul className="list">
              {items.map((l) => {
                const s = logStatus(l.status);
                return (
                  <li key={l.id} className="card">
                    <div className="row" style={{ justifyContent: 'space-between' }}>
                      <span className={`badge badge-${s.kind === 'info' ? 'brand' : s.kind}`}>{s.text}</span>
                      <span className="muted small">{faDateTime(l.createdAt)}</span>
                    </div>
                    <p className="muted small" style={{ marginTop: 6 }}>نوع رویداد: {eventLabel(l.eventType)}</p>
                    {l.errorMessage && <p className="small" style={{ marginTop: 4, overflowWrap: 'anywhere' }}>{l.errorMessage}</p>}
                  </li>
                );
              })}
            </ul>
            {items.length < total && (
              <button className="btn btn-ghost btn-block" disabled={loading} onClick={() => load(page + 1, filter, true)}>
                {loading ? 'در حال بارگذاری…' : 'موارد بیشتر'}
              </button>
            )}
          </>
        )}
    </>
  );
}
