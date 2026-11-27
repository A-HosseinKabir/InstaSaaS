import { API_URL } from './site';

const TOKEN_KEY = 'ps_token';

export const tokenStore = {
  get(): string | null { try { return localStorage.getItem(TOKEN_KEY); } catch { return null; } },
  set(t: string) { try { localStorage.setItem(TOKEN_KEY, t); } catch { /* private mode */ } },
  clear() { try { localStorage.removeItem(TOKEN_KEY); } catch { /* ignore */ } },
};

export class ApiError extends Error {
  status: number;
  errors: string[];
  constructor(status: number, message: string, errors: string[] = []) {
    super(message);
    this.status = status;
    this.errors = errors;
  }
}

function extractMessages(body: unknown): { message: string; errors: string[] } {
  const b = body as Record<string, unknown> | null;
  if (!b || typeof b !== 'object') return { message: '', errors: [] };
  if (typeof b.error === 'string') return { message: b.error, errors: [] };
  const errs = b.errors;
  if (Array.isArray(errs)) return { message: String(errs[0] ?? ''), errors: errs.map(String) };
  if (errs && typeof errs === 'object') {          // ASP.NET model validation: { field: [messages] }
    const flat = Object.values(errs as Record<string, unknown>).flat().map(String);
    return { message: flat[0] ?? '', errors: flat };
  }
  if (typeof b.title === 'string') return { message: b.title, errors: [] };
  return { message: '', errors: [] };
}

const DEFAULT_MESSAGES: Record<number, string> = {
  400: 'اطلاعات واردشده درست نیست.',
  401: 'نشست شما تمام شده است. دوباره وارد شوید.',
  402: 'به سقف پلن رسیده‌اید. برای ادامه پلن را ارتقا دهید.',
  403: 'شما اجازهٔ انجام این کار را ندارید.',
  404: 'پیدا نشد.',
  429: 'تعداد درخواست‌ها زیاد است. کمی صبر کنید و دوباره امتحان کنید.',
  502: 'ارتباط با سرویس بیرونی برقرار نشد. کمی بعد دوباره امتحان کنید.',
  503: 'این بخش هنوز راه‌اندازی نشده است.',
};

export async function api<T = unknown>(
  path: string,
  opts: { method?: string; body?: unknown; auth?: boolean } = {},
): Promise<T> {
  const { method = 'GET', body, auth = true } = opts;
  const headers: Record<string, string> = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (auth) {
    const t = tokenStore.get();
    if (t) headers.Authorization = `Bearer ${t}`;
  }

  let res: Response;
  try {
    res = await fetch(`${API_URL}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  } catch {
    throw new ApiError(0, 'اتصال به سرور برقرار نشد. اینترنت خود را بررسی کنید و دوباره امتحان کنید.');
  }

  if (res.status === 401 && auth) {
    tokenStore.clear();
    if (typeof window !== 'undefined') window.dispatchEvent(new Event('ps-unauthorized'));
  }

  const text = await res.text();
  let data: unknown = null;
  if (text) { try { data = JSON.parse(text); } catch { data = null; } }

  if (!res.ok) {
    const { message, errors } = extractMessages(data);
    throw new ApiError(res.status, message || DEFAULT_MESSAGES[res.status] || 'مشکلی پیش آمد. دوباره امتحان کنید.', errors);
  }
  return data as T;
}

export function errorText(e: unknown): string {
  return e instanceof ApiError ? e.message : 'مشکلی پیش آمد. دوباره امتحان کنید.';
}
