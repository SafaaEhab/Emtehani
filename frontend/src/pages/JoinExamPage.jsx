import React, { useState } from 'react';
import { motion } from 'motion/react';
import {
  ArrowLeft,
  ArrowRight,
  CheckCircle2,
  Clock3,
  Hash,
  KeyRound,
  LockKeyhole,
  Radio,
  ShieldCheck,
  Sparkles,
  Users,
} from 'lucide-react';
import { Alert, Badge, Button, Card, PageHeader } from '../components/ui';
import { request } from '../lib/api';

export default function JoinExamPage({ token, onJoined, onBack }) {
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  function change(event) {
    const digits = event.target.value.replace(/\D/g, '').slice(0, 6);
    setCode(digits);
    setError('');
  }

  async function submit(event) {
    event.preventDefault();
    if (code.length !== 6) {
      setError('أدخل كوداً مكوناً من 6 أرقام.');
      return;
    }
    setBusy(true);
    setError('');
    try {
      const result = await request('/api/exams/enter-code', { token, method: 'POST', body: JSON.stringify({ code }) });
      onJoined(result);
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <PageHeader
        eyebrow="بوابة الطالب"
        title="دخول امتحان"
        description="اكتب كودك الشخصي المرتبط بحسابك لتسجيل حضورك في الجلسة الحالية."
        action={<Button variant="secondary" size="sm" icon={ArrowRight} onClick={onBack}>العودة للرئيسية</Button>}
      />

      {error ? <Alert tone="danger" className="mb-4" onClose={() => setError('')}>{error}</Alert> : null}

      <div className="grid items-start gap-4 lg:grid-cols-[1fr_360px]">
        <Card className="relative overflow-hidden p-5 sm:p-8">
          <div className="pointer-events-none absolute -left-16 -top-16 size-56 rounded-full bg-primary-soft blur-3xl" />
          <div className="relative mx-auto max-w-lg text-center">
            <div className="mx-auto grid size-14 place-items-center rounded-[18px] bg-primary-soft text-primary"><KeyRound size={25} /></div>
            <h2 className="mt-4 font-display text-[clamp(1.5rem,3vw,2rem)] font-semibold tracking-[-0.04em] text-ink">أدخل كودك الشخصي</h2>
            <p className="mx-auto mt-1.5 max-w-sm text-[13px] leading-6 text-muted">الكود من 6 أرقام، تولّد تلقائياً عند إنشاء حسابك ولا يتغير. تجده دائماً في مساحتك الرئيسية.</p>

            <form onSubmit={submit} className="mt-6">
              <label htmlFor="exam-code" className="sr-only">كودك الشخصي</label>
              <div className="relative mx-auto max-w-[330px]">
                <input
                  id="exam-code"
                  value={code}
                  onChange={change}
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  placeholder="000000"
                  aria-describedby="code-hint"
                  className="ui-ltr h-20 w-full rounded-[18px] border-2 border-line bg-surface-soft px-4 text-center font-mono text-[clamp(1.8rem,6vw,2.6rem)] font-bold tracking-[0.35em] text-ink outline-none transition placeholder:text-line-strong focus:border-primary focus:bg-surface focus:ring-4 focus:ring-primary-soft"
                />
                <span className="pointer-events-none absolute inset-y-0 right-4 flex items-center text-subtle"><Hash size={17} /></span>
              </div>
              <p id="code-hint" className="mt-2 text-[11.5px] text-subtle">يمكنك لصق كودك مباشرة، وسنحتفظ بالأرقام فقط.</p>

              <div className="mx-auto mt-5 max-w-[330px]">
                <Button type="submit" size="lg" loading={busy} disabled={code.length !== 6} iconEnd={ArrowLeft} className="w-full">دخول غرفة الانتظار</Button>
              </div>
            </form>

            <div className="mx-auto mt-7 flex max-w-md flex-wrap items-center justify-center gap-x-5 gap-y-2 border-t border-line pt-4 text-[11px] font-semibold text-subtle">
              <span className="inline-flex items-center gap-1.5"><ShieldCheck size={12} className="text-emerald" />مرتبط بحسابك</span>
              <span className="inline-flex items-center gap-1.5"><LockKeyhole size={12} className="text-primary" />دخول آمن</span>
              <span className="inline-flex items-center gap-1.5"><Clock3 size={12} className="text-warning" />انتظار منظم</span>
            </div>
          </div>
        </Card>

        <div className="grid gap-3">
          <Card className="relative overflow-hidden bg-[#10172e] p-5 text-white dark:bg-[#0a1120] dark:ring-1 dark:ring-line">
            <div className="pointer-events-none absolute -left-8 -top-10 size-36 rounded-full bg-primary/30 blur-2xl" />
            <div className="relative">
              <Badge className="border-white/15 bg-white/10 text-white"><Sparkles size={11} /> ماذا بعد؟</Badge>
              <h3 className="mt-3 font-display text-[16px] font-semibold">لن يبدأ الوقت قبل إشارة المدرس.</h3>
              <div className="mt-4 grid gap-3">
                {[
                  { icon: Users, title: 'دخول الغرفة', text: 'سيظهر اسمك للمدرس كطالب حاضر.' },
                  { icon: Clock3, title: 'انتظار هادئ', text: 'ابقَ في الصفحة حتى تُفتح الجلسة.' },
                  { icon: Radio, title: 'بدء تلقائي', text: 'ستنتقل للأسئلة ويبدأ العد التنازلي.' },
                ].map((step, index) => (
                  <div key={step.title} className="flex gap-2.5">
                    <span className="grid size-7 shrink-0 place-items-center rounded-[8px] bg-white/10 text-white/80"><step.icon size={13} /></span>
                    <div><p className="text-[12px] font-bold">{step.title}</p><p className="mt-0.5 text-[10.5px] leading-5 text-white/65">{step.text}</p></div>
                  </div>
                ))}
              </div>
            </div>
          </Card>

          <Card className="p-4">
            <div className="flex items-start gap-2">
              <CheckCircle2 size={15} className="mt-0.5 shrink-0 text-emerald" />
              <p className="text-[11.5px] leading-5 text-muted">إذا ظهرت رسالة أن الكود غير صحيح، تأكد من كتابته بدقة أو راجع ملف حسابك. لا تحاول استخدام كود طالب آخر.</p>
            </div>
          </Card>
        </div>
      </div>
    </div>
  );
}
