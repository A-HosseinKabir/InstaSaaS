export function logStatus(status: string): { text: string; kind: 'ok' | 'warn' | 'err' | 'info' } {
  switch (status) {
    case 'ok': return { text: 'پاسخ ارسال شد', kind: 'ok' };
    case 'followup_ok': return { text: 'یادآوری ارسال شد', kind: 'ok' };
    case 'follow_required': return { text: 'کاربر فالو نکرده بود', kind: 'warn' };
    case 'skipped': return { text: 'ارسال نشد: اشتراک تمام شده', kind: 'warn' };
    case 'followup_skipped': return { text: 'یادآوری ارسال نشد', kind: 'warn' };
    case 'error': return { text: 'خطا در ارسال', kind: 'err' };
    case 'followup_error': return { text: 'خطا در ارسال یادآوری', kind: 'err' };
    default: return { text: status, kind: 'info' };
  }
}

export function eventLabel(t: string): string {
  return t === 'Comment' ? 'کامنت' : t === 'Dm' ? 'دایرکت' : t === 'FollowUp' ? 'یادآوری' : t;
}

export const triggerLabel = (t: 'Comment' | 'Dm') => (t === 'Comment' ? 'کامنت زیر پست' : 'دایرکت');
export const roleLabel = (r: string) => (r === 'Owner' ? 'مالک' : r === 'Admin' ? 'ادمین' : 'بیننده');
export const roleHelp = (r: string) =>
  r === 'Admin' ? 'می‌تواند اتوماسیون بسازد و ویرایش کند' : r === 'Viewer' ? 'فقط می‌تواند ببیند' : 'دسترسی کامل، شامل پرداخت و تیم';
