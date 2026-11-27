import type { ButtonContent, ResponseDto, TriggerType } from '@/lib/types';
import { IconLock } from './icons';

function isHttps(u: string) { try { return new URL(u).protocol === 'https:'; } catch { return false; } }

export default function ChatPreview({ trigger, sample, responses }: { trigger: TriggerType; sample: string; responses: ResponseDto[] }) {
  let gatedShown = false;
  return (
    <div className="chat" role="img" aria-label="پیش‌نمایش گفتگو با مشتری">
      <div className="msg msg-meta">{trigger === 'Comment' ? 'مشتری زیر پست کامنت می‌گذارد' : 'مشتری در دایرکت پیام می‌دهد'}</div>
      <div className="msg msg-in">{sample || '…'}</div>
      {trigger === 'Comment' && <div className="msg msg-meta">پاسخ در دایرکت ارسال می‌شود</div>}
      {responses.map((r, i) => {
        const showLock = r.requireFollow && !gatedShown;
        if (r.requireFollow) gatedShown = true;
        let body: React.ReactNode;
        if (r.messageType === 'Image') {
          body = isHttps(r.content)
            // eslint-disable-next-line @next/next/no-img-element
            ? <img className="msg-img" src={r.content} alt="تصویر پیام" loading="lazy" referrerPolicy="no-referrer" />
            : <div className="msg-img" />;
        } else if (r.messageType === 'Button') {
          let bc: ButtonContent | null = null;
          try { bc = JSON.parse(r.content) as ButtonContent; } catch { bc = null; }
          body = (<>{bc?.message}{bc?.buttons?.map((b, k) => <span key={k} className="msg-btn">{b.title}</span>)}</>);
        } else body = r.content;
        return (
          <div key={i} className="msg msg-out">
            {showLock && <div className="msg-lock"><IconLock /> از اینجا به بعد فقط برای کسانی که پیج را فالو کرده‌اند</div>}
            {body}
          </div>
        );
      })}
    </div>
  );
}
