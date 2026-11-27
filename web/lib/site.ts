// Change the brand once here. The name is a placeholder: pick your own before launch.
export const SITE = {
  name: 'پاسخیار',
  tagline: 'پاسخ خودکار کامنت و دایرکت اینستاگرام',
  description:
    'با پاسخیار به کامنت‌ها و دایرکت‌های اینستاگرامت خودکار جواب بده: کلمه کلیدی تعریف کن، پیام متنی، عکس یا دکمه بفرست و فقط به فالوورها پاسخ بده. راه‌اندازی در چند دقیقه، مخصوص گوشی.',
  url: (process.env.NEXT_PUBLIC_SITE_URL ?? 'http://localhost:3000').replace(/\/$/, ''),
  locale: 'fa_IR',
};

export const API_URL = (process.env.NEXT_PUBLIC_API_URL ?? 'https://localhost:57363').replace(/\/$/, '');
