// Client-side copy of the server's KeywordMatcher (used by the live "try it" box in the wizard).
// Keep it in sync with Engine.cs -> KeywordMatcher.Normalize.
export function normalize(s: string | null | undefined): string {
  if (!s || !s.trim()) return '';
  let out = '';
  for (const c of s.trim()) {
    switch (c) {
      case 'ي': case 'ى': out += 'ی'; break;
      case 'ك': out += 'ک'; break;
      case '\u200c': case '\u200d': case '\u064b': case '\u064c': case '\u064d':
      case '\u064e': case '\u064f': case '\u0650': case '\u0651': case '\u0652': case '\u0640': break;
      default:
        if (c >= '۰' && c <= '۹') out += String(c.charCodeAt(0) - 0x06f0);
        else if (c >= '٠' && c <= '٩') out += String(c.charCodeAt(0) - 0x0660);
        else out += c.toLowerCase();
    }
  }
  return out.split(/\s+/).filter(Boolean).join(' ');
}

export function isMatch(text: string, keywords: string[], type: 'Exact' | 'Contains'): boolean {
  const t = normalize(text);
  if (!t) return false;
  return keywords.map(normalize).filter(Boolean).some((k) => (type === 'Exact' ? t === k : t.includes(k)));
}

export const MAX_KEYWORDS = 20;
export const MAX_KEYWORD_LEN = 100;
export const MAX_TEXT = 1000;
export const MAX_RESPONSES = 10;
export const MAX_BUTTONS = 3;
export const MAX_BUTTON_TITLE = 20;
