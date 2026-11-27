const nf = new Intl.NumberFormat('fa-IR');
export const faNum = (n: number | string) => nf.format(Number(n));

export function faDate(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso.endsWith('Z') || /[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z');
  if (isNaN(d.getTime())) return '—';
  return new Intl.DateTimeFormat('fa-IR-u-ca-persian', { year: 'numeric', month: 'long', day: 'numeric' }).format(d);
}

export function faDateTime(iso: string): string {
  const d = new Date(iso.endsWith('Z') || /[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z');
  if (isNaN(d.getTime())) return '—';
  const date = new Intl.DateTimeFormat('fa-IR-u-ca-persian', { month: 'short', day: 'numeric' }).format(d);
  const time = new Intl.DateTimeFormat('fa-IR', { hour: '2-digit', minute: '2-digit' }).format(d);
  return `${date}، ${time}`;
}

export function relativeTime(iso: string): string {
  const d = new Date(iso.endsWith('Z') || /[+-]\d\d:\d\d$/.test(iso) ? iso : iso + 'Z').getTime();
  const s = Math.max(0, Math.round((Date.now() - d) / 1000));
  if (s < 60) return 'همین الان';
  if (s < 3600) return `${faNum(Math.floor(s / 60))} دقیقه پیش`;
  if (s < 86400) return `${faNum(Math.floor(s / 3600))} ساعت پیش`;
  return `${faNum(Math.floor(s / 86400))} روز پیش`;
}

export const toman = (rial: number) => `${faNum(Math.round(rial / 10))} تومان`;

// Accepts Persian/Arabic digits, spaces, +98 etc. Mirrors the server's PhoneNormalizer.
export function normalizePhone(raw: string): string | null {
  let s = raw
    .replace(/[۰-۹]/g, (c) => String(c.charCodeAt(0) - 0x06f0))
    .replace(/[٠-٩]/g, (c) => String(c.charCodeAt(0) - 0x0660))
    .replace(/\D/g, '');
  if (s.startsWith('98') && s.length === 12) s = '0' + s.slice(2);
  else if (s.startsWith('9') && s.length === 10) s = '0' + s;
  return s.length === 11 && s.startsWith('09') ? s : null;
}

export const toEnglishDigits = (s: string) =>
  s.replace(/[۰-۹]/g, (c) => String(c.charCodeAt(0) - 0x06f0)).replace(/[٠-٩]/g, (c) => String(c.charCodeAt(0) - 0x0660));
