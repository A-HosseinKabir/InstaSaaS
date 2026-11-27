'use client';
import { useEffect, useState } from 'react';

// Reads ?a=b&c=d after mount. (useSearchParams would force a Suspense boundary on every page.)
export function useQuery(): Record<string, string> {
  const [q, setQ] = useState<Record<string, string>>({});
  useEffect(() => {
    const out: Record<string, string> = {};
    new URLSearchParams(window.location.search).forEach((v, k) => { out[k] = v; });
    setQ(out);
  }, []);
  return q;
}
