'use client';

import Link from 'next/link';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import { api, ApiError, errorText } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { faNum } from '@/lib/format';
import { isMatch, MAX_KEYWORDS, MAX_KEYWORD_LEN, MAX_RESPONSES, normalize } from '@/lib/keyword';
import { triggerLabel } from '@/lib/labels';
import type { Account, Automation, AutomationDto, ButtonContent, MatchType, ResponseDto, TriggerType } from '@/lib/types';
import ChatPreview from './ChatPreview';
import { IconBack, IconBulb, IconCheck, IconClose, IconComment, IconDm, IconDown, IconEdit, IconImage, IconInsta, IconLink, IconLock, IconPlus, IconText, IconTrash, IconUp, Logo } from './icons';
import ReplyEditor from './ReplyEditor';
import { ConfirmSheet, Notice, Spinner, Switch } from './ui';

type StepId = 'account' | 'trigger' | 'keywords' | 'match' | 'replies' | 'followup' | 'review';

interface Draft {
  accountId: string;
  trigger: TriggerType | null;
  keywords: string[];
  matchType: MatchType;
  responses: ResponseDto[];
  followEnabled: boolean;
  followHours: number;
  followText: string;
  priority: number;
  isActive: boolean;
}

const DEFAULT_FOLLOW_TEXT = 'سلام! هنوز سؤالی داری؟ خوشحال می‌شویم کمکت کنیم 🌟';
const SUGGESTIONS = ['قیمت', 'خرید', 'سفارش', 'موجودی', 'لینک', 'آدرس', 'سایز', 'price'];
const HOURS = [1, 3, 6, 12, 23];

const INITIAL: Draft = {
  accountId: '', trigger: null, keywords: [], matchType: 'Contains', responses: [],
  followEnabled: false, followHours: 3, followText: DEFAULT_FOLLOW_TEXT, priority: 0, isActive: true,
};

function Ask({ title, sub }: { title: string; sub?: string }) {
  return (
    <div className="wz-ask">
      <Logo size={38} />
      <div className="wz-q"><h2>{title}</h2>{sub && <p className="muted">{sub}</p>}</div>
    </div>
  );
}

function Tip({ title, children }: { title: string; children: React.ReactNode }) {
  return <div className="tip"><IconBulb /><div><b>{title}</b>{children}</div></div>;
}

function responseSummary(r: ResponseDto): { icon: React.ReactNode; text: string } {
  if (r.messageType === 'Image') return { icon: <IconImage />, text: r.content };
  if (r.messageType === 'Button') {
    try { const b = JSON.parse(r.content) as ButtonContent; return { icon: <IconLink />, text: `${b.message} (${faNum(b.buttons.length)} دکمه)` }; }
    catch { return { icon: <IconLink />, text: 'پیام دکمه‌دار' }; }
  }
  return { icon: <IconText />, text: r.content };
}

export default function AutomationWizard({ id }: { id?: string }) {
  const router = useRouter();
  const { canEdit, ready } = useAuth();
  const editing = !!id;

  const [accounts, setAccounts] = useState<Account[] | null>(null);
  const [loadErr, setLoadErr] = useState('');
  const [d, setD] = useState<Draft>(INITIAL);
  const [touched, setTouched] = useState(false);
  const [idx, setIdx] = useState(0);
  const [kwInput, setKwInput] = useState('');
  const [kwErr, setKwErr] = useState('');
  const [sample, setSample] = useState('');
  const [editor, setEditor] = useState<{ index: number | null; initial: ResponseDto | null } | null>(null);
  const [leaveAsk, setLeaveAsk] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveErrs, setSaveErrs] = useState<string[]>([]);
  const [limitHit, setLimitHit] = useState(false);
  const [done, setDone] = useState<string[] | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);

  const upd = (p: Partial<Draft>) => { setD((x) => ({ ...x, ...p })); setTouched(true); };

  /* ---- load accounts (+ the automation when editing) ---- */
  useEffect(() => {
    if (!ready) return;
    let alive = true;
    (async () => {
      try {
        const accs = await api<Account[]>('/api/accounts');
        let a: Automation | null = null;
        if (id) a = await api<Automation>(`/api/automations/${id}`);
        if (!alive) return;
        setAccounts(accs);
        if (a) {
          setD({
            accountId: a.accountId, trigger: a.triggerType, keywords: a.keywords.split(',').filter(Boolean), matchType: a.matchType,
            responses: a.responses, followEnabled: a.followUpAfterHours != null, followHours: a.followUpAfterHours ?? 3,
            followText: a.followUpText ?? DEFAULT_FOLLOW_TEXT, priority: a.priority, isActive: a.isActive,
          });
        } else if (accs.length === 1) {
          setD((x) => ({ ...x, accountId: accs[0].id }));
        }
      } catch (e) { if (alive) setLoadErr(errorText(e)); }
    })();
    return () => { alive = false; };
  }, [id, ready]);

  /* ---- steps ---- */
  const steps: StepId[] = useMemo(() => {
    const s: StepId[] = [];
    if (!editing && (accounts?.length ?? 0) > 1) s.push('account');
    s.push('trigger', 'keywords', 'match', 'replies', 'followup', 'review');
    return s;
  }, [editing, accounts]);
  const step = steps[Math.min(idx, steps.length - 1)];

  useEffect(() => { scrollRef.current?.scrollTo({ top: 0 }); }, [step]);
  useEffect(() => {
    if (!touched || done) return;
    const h = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    window.addEventListener('beforeunload', h);
    return () => window.removeEventListener('beforeunload', h);
  }, [touched, done]);

  /* ---- keywords ---- */
  function commitKeywords(raw: string): string[] {
    const parts = raw.split(/[,،\n]/).map((x) => x.trim()).filter(Boolean);
    let list = [...d.keywords];
    let err = '';
    for (const p of parts) {
      if (p.length > MAX_KEYWORD_LEN) { err = `هر کلمه حداکثر ${faNum(MAX_KEYWORD_LEN)} حرف باشد.`; continue; }
      if (list.some((k) => normalize(k) === normalize(p))) continue;
      if (list.length >= MAX_KEYWORDS) { err = `حداکثر ${faNum(MAX_KEYWORDS)} کلمه می‌توانی اضافه کنی.`; break; }
      list.push(p);
    }
    setKwErr(err);
    if (list.length !== d.keywords.length) upd({ keywords: list });
    setKwInput('');
    return list;
  }

  /* ---- navigation ---- */
  const canNext = (() => {
    switch (step) {
      case 'account': return !!d.accountId;
      case 'trigger': return d.trigger !== null;
      case 'keywords': return d.keywords.length > 0 || kwInput.trim().length > 0;
      case 'replies': return d.responses.length > 0;
      case 'followup': return !d.followEnabled || (d.followHours >= 1 && d.followHours <= 23);
      default: return true;
    }
  })();

  function next() {
    if (step === 'keywords' && kwInput.trim()) commitKeywords(kwInput);
    if (step === 'keywords' && d.keywords.length === 0 && !kwInput.trim()) return;
    setIdx((i) => Math.min(i + 1, steps.length - 1));
  }
  function back() {
    if (idx > 0) { setIdx(idx - 1); return; }
    if (touched) setLeaveAsk(true); else router.push('/dashboard/automations');
  }

  /* ---- replies ---- */
  function saveReply(r: ResponseDto) {
    if (!editor) return;
    const list = [...d.responses];
    if (editor.index === null) list.push(r); else list[editor.index] = r;
    upd({ responses: list });
    setEditor(null);
  }
  function move(i: number, dir: -1 | 1) {
    const j = i + dir;
    if (j < 0 || j >= d.responses.length) return;
    const list = [...d.responses];
    [list[i], list[j]] = [list[j], list[i]];
    upd({ responses: list });
  }

  /* ---- save ---- */
  async function save() {
    if (!d.trigger) return;
    setSaving(true); setSaveErrs([]); setLimitHit(false);
    const body: AutomationDto = {
      accountId: d.accountId, triggerType: d.trigger, keywords: d.keywords.join(','), matchType: d.matchType,
      priority: d.priority, followUpAfterHours: d.followEnabled ? d.followHours : null,
      followUpText: d.followEnabled && d.followText.trim() ? d.followText.trim() : null,
      isActive: d.isActive, responses: d.responses,
    };
    try {
      const r = await api<{ warnings?: string[] }>(editing ? `/api/automations/${id}` : '/api/automations', { method: editing ? 'PUT' : 'POST', body });
      setTouched(false);
      setDone(r.warnings ?? []);
    } catch (e) {
      if (e instanceof ApiError) {
        setSaveErrs(e.errors.length ? e.errors : [e.message]);
        if (e.status === 402) setLimitHit(true);
      } else setSaveErrs([errorText(e)]);
    } finally { setSaving(false); }
  }

  /* ---- guards ---- */
  if (ready && !canEdit) {
    return <div className="auth"><Notice kind="warn">نقش «بیننده» اجازهٔ ساخت یا ویرایش اتوماسیون را ندارد.</Notice><Link href="/dashboard/automations" className="btn btn-primary">بازگشت</Link></div>;
  }
  if (loadErr) return <div className="auth"><Notice kind="err">{loadErr}</Notice><Link href="/dashboard/automations" className="btn btn-primary">بازگشت</Link></div>;
  if (!accounts || (editing && d.trigger === null)) return <div style={{ minHeight: '100dvh', display: 'grid', placeItems: 'center' }}><Spinner /></div>;
  if (accounts.length === 0) {
    return (
      <div className="auth">
        <h1>اول پیج را وصل کن</h1>
        <p className="muted">اتوماسیون روی یک پیج اینستاگرام کار می‌کند. وصل کردنش یک دقیقه طول می‌کشد.</p>
        <Link href="/dashboard/accounts" className="btn btn-primary">وصل کردن پیج</Link>
        <Link href="/dashboard/automations" className="btn btn-ghost">بعداً</Link>
      </div>
    );
  }

  /* ---- finished ---- */
  if (done) {
    return (
      <div className="wz"><div className="wz-scroll"><div className="wz-inner done-screen">
        <div className="done-check"><IconCheck /></div>
        <h1>{editing ? 'تغییرات ذخیره شد' : 'اتوماسیون ساخته شد 🎉'}</h1>
        <p className="muted">{d.isActive ? 'از همین الان به پیام‌های مشتری‌ها جواب می‌دهد.' : 'فعلاً خاموش است. از لیست اتوماسیون‌ها روشنش کن.'}</p>
        {done.map((w, i) => <Notice key={i} kind="warn">{w}</Notice>)}
        <div className="stack" style={{ width: '100%' }}>
          <Link href="/dashboard/automations" className="btn btn-primary btn-block">دیدن اتوماسیون‌ها</Link>
          {!editing && <button className="btn btn-ghost btn-block" onClick={() => { setD({ ...INITIAL, accountId: d.accountId }); setIdx(0); setDone(null); setTouched(false); setSample(''); }}>ساخت یکی دیگر</button>}
        </div>
      </div></div></div>
    );
  }

  const trigger = d.trigger ?? 'Dm';
  const isLast = step === 'review';
  const accName = accounts.find((a) => a.id === d.accountId)?.username;
  const sampleText = sample || d.keywords[0] || '';
  const tryResult = sample.trim() ? isMatch(sample, d.keywords.length ? d.keywords : kwInput ? [kwInput] : [], d.matchType) : null;

  return (
    <div className="wz">
      <div className="wz-top">
        <div className="wz-bar">
          <button className="icon-btn" onClick={back} aria-label={idx === 0 ? 'خروج' : 'مرحلهٔ قبل'}>{idx === 0 ? <IconClose /> : <IconBack />}</button>
          <div className="wz-steps" role="progressbar" aria-valuemin={1} aria-valuemax={steps.length} aria-valuenow={idx + 1} aria-label="پیشرفت">
            {steps.map((s, i) => <i key={s} className={i <= idx ? 'done' : ''} />)}
          </div>
          <span className="wz-count num">{faNum(idx + 1)} از {faNum(steps.length)}</span>
        </div>
      </div>

      <div className="wz-scroll" ref={scrollRef}>
        <div className="wz-inner" key={step}>
          {step === 'account' && (
            <>
              <Ask title="این اتوماسیون برای کدام پیج است؟" sub="هر اتوماسیون فقط روی یک پیج کار می‌کند." />
              <div role="radiogroup" aria-label="پیج" className="stack">
                {accounts.map((a) => (
                  <button key={a.id} role="radio" aria-checked={d.accountId === a.id} className="choice" onClick={() => upd({ accountId: a.id })}>
                    <span className="item-ico"><IconInsta /></span>
                    <span><b className="ltr">@{a.username}</b></span>
                  </button>
                ))}
              </div>
            </>
          )}

          {step === 'trigger' && (
            <>
              <Ask title="چه اتفاقی اتوماسیون را شروع کند؟" sub="یکی را انتخاب کن. برای حالت دیگر بعداً می‌توانی اتوماسیون جدا بسازی." />
              <div role="radiogroup" aria-label="نوع شروع" className="stack">
                <button role="radio" aria-checked={d.trigger === 'Comment'} className="choice" onClick={() => upd({ trigger: 'Comment' })}>
                  <span className="item-ico"><IconComment /></span>
                  <span><b>کامنت زیر پست</b><span className="muted">وقتی کسی زیر پست یا ریلت کامنت می‌گذارد، جواب را در دایرکت برایش می‌فرستیم.</span></span>
                </button>
                <button role="radio" aria-checked={d.trigger === 'Dm'} className="choice" onClick={() => upd({ trigger: 'Dm' })}>
                  <span className="item-ico"><IconDm /></span>
                  <span><b>پیام در دایرکت</b><span className="muted">وقتی کسی در دایرکت پیجت پیام می‌دهد، همان‌جا جواب می‌دهیم.</span></span>
                </button>
              </div>
              {d.trigger === 'Comment' && (
                <Tip title="خوب است بدانی">
                  این اتوماسیون روی همهٔ پست‌ها و ریل‌های پیج کار می‌کند. اولین پیام به‌صورت «پاسخ خصوصی به کامنت» می‌رود؛ اینستاگرام ممکن است ارسال چند پیام پشت‌سرهم را قبل از جواب کاربر محدود کند، پس مهم‌ترین پیام را اول بگذار.
                </Tip>
              )}
            </>
          )}

          {step === 'keywords' && (
            <>
              <Ask title={d.trigger === 'Comment' ? 'مشتری چه کلمه‌ای کامنت کند؟' : 'مشتری چه کلمه‌ای بنویسد؟'} sub="هر کلمه‌ای که بنویسی، اگر مشتری بگوید جواب می‌دهیم." />
              <div className="field">
                <label htmlFor="kw">کلمهٔ کلیدی</label>
                <div className="row">
                  <input id="kw" className="input grow" value={kwInput} placeholder="مثلاً: قیمت" enterKeyHint="done"
                    onChange={(e) => { setKwInput(e.target.value); setKwErr(''); }}
                    onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); commitKeywords(kwInput); } }}
                    onBlur={() => { if (kwInput.trim()) commitKeywords(kwInput); }} />
                  <button className="btn btn-primary" style={{ minWidth: 64, padding: 0 }} onClick={() => commitKeywords(kwInput)} disabled={!kwInput.trim()} aria-label="افزودن کلمه"><IconPlus /></button>
                </div>
                {kwErr && <p className="field-err" role="alert">{kwErr}</p>}
                <div className="counter">{faNum(d.keywords.length)} از {faNum(MAX_KEYWORDS)}</div>
              </div>
              {d.keywords.length > 0 && (
                <div className="chips" aria-label="کلمات اضافه‌شده">
                  {d.keywords.map((k) => (
                    <span key={k} className="chip">{k}
                      <button className="chip-x" aria-label={`حذف ${k}`} onClick={() => upd({ keywords: d.keywords.filter((x) => x !== k) })}><IconClose /></button>
                    </span>
                  ))}
                </div>
              )}
              <div>
                <p className="muted small" style={{ marginBottom: 8 }}>پیشنهاد (با یک ضربه اضافه می‌شود):</p>
                <div className="chips">
                  {SUGGESTIONS.filter((s) => !d.keywords.some((k) => normalize(k) === normalize(s))).map((s) => (
                    <button key={s} className="chip chip-btn" onClick={() => commitKeywords(s)}>+ {s}</button>
                  ))}
                </div>
              </div>
              <Tip title="چند کلمه بگذار">
                مشتری‌ها یک چیز را به چند شکل می‌گویند: «قیمت»، «چنده»، «price». هر کدام را جدا اضافه کن. فرق «ی/ي» و «ک/ك» و اعداد فارسی و انگلیسی را خودمان حساب می‌کنیم.
              </Tip>
            </>
          )}

          {step === 'match' && (
            <>
              <Ask title="کلمه چطور تشخیص داده شود؟" sub="این تنظیم مشخص می‌کند پیام مشتری چقدر باید شبیه کلمه‌ات باشد." />
              <div role="radiogroup" aria-label="نوع تطبیق" className="stack">
                <button role="radio" aria-checked={d.matchType === 'Contains'} className="choice" onClick={() => upd({ matchType: 'Contains' })}>
                  <span className="item-ico"><IconCheck /></span>
                  <span><b>اگر کلمه توی پیام بود <span className="badge badge-brand">پیشنهادی</span></b><span className="muted">«قیمت این چنده؟» هم جواب می‌گیرد، چون «قیمت» در آن هست.</span></span>
                </button>
                <button role="radio" aria-checked={d.matchType === 'Exact'} className="choice" onClick={() => upd({ matchType: 'Exact' })}>
                  <span className="item-ico"><IconCheck /></span>
                  <span><b>فقط اگر دقیقاً همین بود</b><span className="muted">فقط وقتی کل پیام همان کلمه باشد جواب می‌دهیم، مثل «قیمت». «قیمت چنده؟» جواب نمی‌گیرد.</span></span>
                </button>
              </div>
              <div className="card try">
                <label htmlFor="try" className="label">امتحان کن</label>
                <input id="try" className="input" placeholder="یک پیام نمونه بنویس…" value={sample} onChange={(e) => setSample(e.target.value)} />
                {tryResult !== null && (
                  <div className={`try-res ${tryResult ? 'yes' : 'no'}`} role="status">
                    {tryResult ? '✓ این پیام جواب خودکار می‌گیرد' : '✕ این پیام جواب خودکار نمی‌گیرد'}
                  </div>
                )}
              </div>
            </>
          )}

          {step === 'replies' && (
            <>
              <Ask title="چه جوابی بفرستیم؟" sub="می‌توانی چند پیام پشت‌سرهم بسازی؛ به همین ترتیب فرستاده می‌شوند." />
              {d.responses.length === 0 ? (
                <div className="card" style={{ textAlign: 'center' }}>
                  <p className="muted" style={{ marginBottom: 12 }}>هنوز پیامی نساخته‌ای. با یک متن ساده شروع کن.</p>
                  <button className="btn btn-primary" onClick={() => setEditor({ index: null, initial: null })}><IconPlus style={{ width: 20, height: 20 }} /> ساخت اولین پیام</button>
                </div>
              ) : (
                <>
                  <ul className="list">
                    {d.responses.map((r, i) => {
                      const s = responseSummary(r);
                      return (
                        <li key={i} className="card reply-card">
                          <span className="item-ico">{s.icon}</span>
                          <div className="grow">
                            <div className="row" style={{ gap: 6, marginBottom: 2 }}>
                              <span className="badge">پیام {faNum(i + 1)}</span>
                              {r.requireFollow && <span className="badge badge-warn"><IconLock style={{ width: 12, height: 12 }} /> فقط فالوورها</span>}
                            </div>
                            <p className="txt">{s.text}</p>
                          </div>
                          <div className="reply-tools">
                            <button className="icon-btn" aria-label="ویرایش" onClick={() => setEditor({ index: i, initial: r })}><IconEdit /></button>
                            <button className="icon-btn" aria-label="حذف" onClick={() => upd({ responses: d.responses.filter((_, k) => k !== i) })}><IconTrash /></button>
                          </div>
                          {d.responses.length > 1 && (
                            <div className="reply-tools">
                              <button className="icon-btn" aria-label="بالا" disabled={i === 0} onClick={() => move(i, -1)}><IconUp /></button>
                              <button className="icon-btn" aria-label="پایین" disabled={i === d.responses.length - 1} onClick={() => move(i, 1)}><IconDown /></button>
                            </div>
                          )}
                        </li>
                      );
                    })}
                  </ul>
                  {d.responses.length < MAX_RESPONSES && (
                    <button className="btn btn-ghost" onClick={() => setEditor({ index: null, initial: null })}><IconPlus style={{ width: 20, height: 20 }} /> پیام دیگر</button>
                  )}
                </>
              )}
              <Tip title="ایده">
                مثلاً پیام اول: یک سلام و قیمت. پیام دوم: عکس محصول. پیام سوم: دکمهٔ «ثبت سفارش» با لینک. اگر می‌خواهی فقط فالوورها قیمت را ببینند، همان پیام را روی «فقط فالوورها» بگذار.
              </Tip>
            </>
          )}

          {step === 'followup' && (
            <>
              <Ask title="اگر جواب نداد، یادآوری بفرستیم؟" sub="گاهی مشتری پیام را می‌بیند و یادش می‌رود. یک یادآوری ملایم کمک می‌کند." />
              <div className="card row" style={{ justifyContent: 'space-between' }}>
                <div className="grow"><b>یادآوری خودکار</b><p className="muted small">اگر مشتری بعد از جواب ما پیامی نداد.</p></div>
                <Switch checked={d.followEnabled} onChange={(v) => upd({ followEnabled: v })} label="یادآوری خودکار" />
              </div>
              {d.followEnabled && (
                <>
                  <div className="field">
                    <span className="label">بعد از چند ساعت؟</span>
                    <div className="chips" role="radiogroup" aria-label="ساعت">
                      {HOURS.map((h) => (
                        <button key={h} role="radio" aria-checked={d.followHours === h} className={`chip ${d.followHours === h ? '' : 'chip-btn'}`} onClick={() => upd({ followHours: h })}>{faNum(h)} ساعت</button>
                      ))}
                    </div>
                  </div>
                  <div className="field">
                    <label htmlFor="ft">متن یادآوری</label>
                    <textarea id="ft" className="textarea" style={{ minHeight: 96 }} value={d.followText} onChange={(e) => upd({ followText: e.target.value })} />
                  </div>
                  <Tip title="چرا حداکثر ۲۳ ساعت؟">
                    اینستاگرام فقط تا ۲۴ ساعت بعد از آخرین پیام مشتری اجازهٔ ارسال می‌دهد. اگر مشتری در این مدت خودش پیام بدهد، یادآوری ارسال نمی‌شود.
                    {d.trigger === 'Comment' && <> برای اتوماسیون‌های کامنتی ممکن است اینستاگرام یادآوری را رد کند.</>}
                  </Tip>
                </>
              )}
            </>
          )}

          {step === 'review' && (
            <>
              <Ask title="همه‌چیز آماده است" sub="مشتری گفتگو را این‌طور می‌بیند. اگر چیزی را می‌خواهی عوض کنی، روی «ویرایش» بزن." />
              <ChatPreview trigger={trigger} sample={sampleText} responses={d.responses} />
              <div className="card summary">
                {accName && accounts.length > 1 && <Row k="پیج" v={`@${accName}`} />}
                <Row k="شروع با" v={triggerLabel(trigger)} onEdit={() => setIdx(steps.indexOf('trigger'))} />
                <Row k="کلمه‌ها" v={d.keywords.join('، ')} onEdit={() => setIdx(steps.indexOf('keywords'))} />
                <Row k="تشخیص" v={d.matchType === 'Contains' ? 'اگر در پیام بود' : 'دقیقاً همین'} onEdit={() => setIdx(steps.indexOf('match'))} />
                <Row k="پیام‌ها" v={`${faNum(d.responses.length)} پیام`} onEdit={() => setIdx(steps.indexOf('replies'))} />
                <Row k="یادآوری" v={d.followEnabled ? `بعد از ${faNum(d.followHours)} ساعت` : 'ندارد'} onEdit={() => setIdx(steps.indexOf('followup'))} />
              </div>
              <details className="adv">
                <summary>تنظیمات پیشرفته</summary>
                <div className="stack">
                  <div className="field">
                    <label htmlFor="pr">اولویت</label>
                    <input id="pr" className="input" type="number" min={0} max={99} inputMode="numeric" value={d.priority}
                      onChange={(e) => upd({ priority: Math.max(0, Math.min(99, Number(e.target.value) || 0)) })} />
                    <p className="hint">اگر یک پیام با چند اتوماسیون جور شد، عدد کمتر برنده است. معمولاً همان ۰ را بگذار.</p>
                  </div>
                  <div className="row" style={{ justifyContent: 'space-between' }}>
                    <div><b>روشن باشد</b><p className="muted small">می‌توانی بعداً از لیست خاموشش کنی.</p></div>
                    <Switch checked={d.isActive} onChange={(v) => upd({ isActive: v })} label="فعال بودن" />
                  </div>
                </div>
              </details>
              {saveErrs.length > 0 && (
                <div className="notice notice-err" role="alert" style={{ display: 'block' }}>
                  <ul>{saveErrs.map((m, i) => <li key={i}>{m}</li>)}</ul>
                  {limitHit && <p style={{ marginTop: 8 }}><Link href="/dashboard/billing" className="btn btn-sm btn-primary">ارتقای پلن</Link></p>}
                </div>
              )}
            </>
          )}
        </div>
      </div>

      <div className="wz-foot">
        <div className="wz-foot-in">
          <button className="btn btn-primary btn-block" disabled={!canNext || saving} onClick={isLast ? save : next}>
            {isLast ? (saving ? 'در حال ذخیره…' : editing ? 'ذخیرهٔ تغییرات' : 'ذخیره و فعال‌سازی') : 'ادامه'}
          </button>
          {step === 'keywords' && !canNext && <p className="hint" style={{ textAlign: 'center' }}>حداقل یک کلمه لازم است.</p>}
          {step === 'replies' && !canNext && <p className="hint" style={{ textAlign: 'center' }}>حداقل یک پیام بساز تا ادامه بدهی.</p>}
        </div>
      </div>

      <ReplyEditor open={!!editor} initial={editor?.initial ?? null} trigger={trigger}
        isFirst={editor ? (editor.index ?? d.responses.length) === 0 : false} onSave={saveReply} onClose={() => setEditor(null)} />
      <ConfirmSheet open={leaveAsk} onClose={() => setLeaveAsk(false)} title="از ساخت اتوماسیون خارج می‌شوی؟"
        text="تغییراتی که داده‌ای ذخیره نشده‌اند و از بین می‌روند." confirmLabel="خروج" danger
        onConfirm={() => { setTouched(false); router.push('/dashboard/automations'); }} />
    </div>
  );
}

function Row({ k, v, onEdit }: { k: string; v: string; onEdit?: () => void }) {
  return (
    <div className="summary-row">
      <span className="muted">{k}</span>
      <span className="v">{v}</span>
      {onEdit && <button className="btn btn-ghost btn-sm" onClick={onEdit}>ویرایش</button>}
    </div>
  );
}
