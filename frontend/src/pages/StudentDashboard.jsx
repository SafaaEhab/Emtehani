import React, { useCallback, useEffect, useState } from 'react';
import { motion } from 'motion/react';
import {
  ArrowLeft,
  Award,
  BookOpen,
  CheckCircle2,
  Clock3,
  FileText,
  KeyRound,
  Radio,
  Target,
  TrendingUp,
} from 'lucide-react';
import {
  Alert,
  Badge,
  Button,
  Card,
  CopyChip,
  EmptyState,
  PageHeader,
  ProgressBar,
  Skeleton,
  StatCard,
  StatusChip,
} from '../components/ui';
import { request } from '../lib/api';
import { formatDate, formatNumber, percent, scoreText } from '../lib/format';

export default function StudentDashboard({ token, user, onJoin, onViewResults, onOpenReview }) {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [refreshing, setRefreshing] = useState(false);

  const load = useCallback(async (background = false) => {
    if (background) setRefreshing(true); else setLoading(true);
    try {
      const result = await request('/api/results/mine', { token });
      setItems(Array.isArray(result) ? result : []);
      setError('');
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [token]);

  useEffect(() => { load(); }, [load]);

  const graded = items.filter((item) => item.score != null);
  const average = graded.length ? Math.round(graded.reduce((sum, item) => sum + percent(item.score, item.maxScore), 0) / graded.length) : 0;
  const best = graded.length ? Math.max(...graded.map((item) => percent(item.score, item.maxScore))) : 0;
  const recent = items.slice(0, 4);

  return (
    <div>
      <PageHeader
        eyebrow="مساحة الطالب"
        title={`أهلاً، ${user?.fullName?.split(' ')[0] || 'طالبنا'}.`}
        description="كودك الشخصي لا يتغير: اكتبه في صفحة دخول الامتحان وسنأخذك إلى غرفة الانتظار."
        action={<Button size="sm" icon={KeyRound} onClick={onJoin}>دخول امتحان</Button>}
      />

      {error ? <Alert tone="danger" className="mb-4" onClose={() => setError('')}>{error}</Alert> : null}

      <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} className="relative mb-4 overflow-hidden rounded-card border border-primary/15 bg-primary p-5 text-white shadow-[0_24px_60px_-28px_var(--app-primary)] sm:p-7">
        <div className="pointer-events-none absolute -left-12 -top-16 size-56 rounded-full bg-white/10 blur-2xl" />
        <div className="pointer-events-none absolute -bottom-20 right-1/3 size-56 rounded-full bg-emerald/20 blur-3xl" />
        <div className="relative grid items-center gap-5 sm:grid-cols-[1fr_auto]">
          <div>
            <Badge className="border-white/15 bg-white/10 text-white">كودك الشخصي</Badge>
            <h2 className="mt-3 font-display text-[clamp(1.5rem,3vw,2.1rem)] font-semibold leading-tight tracking-[-0.04em]">امتحانك يبدأ بكودك الخاص من 6 أرقام.</h2>
            <p className="mt-2 max-w-lg text-[13px] leading-7 text-white/80">تولّد لك تلقائياً عند إنشاء حسابك ولا تتغير، وترتبط ببياناتك فقط. اكتبه في صفحة دخول الامتحان لتسجيل حضورك وانتظر إشارة المدرس لبدء الجلسة. لا تشاركه مع أي شخص.</p>
            <div className="mt-4 flex flex-wrap items-center gap-3">
              <span className="rounded-[13px] border border-white/15 bg-white/10 px-4 py-2.5 font-mono text-2xl font-bold tracking-[0.35em] text-white" dir="ltr" style={{ maxWidth: '100%' }}>{user?.studentCode || '———'}</span>
              {user?.studentCode ? <span className="rounded-[13px] bg-white/95 px-1.5 text-primary shadow-sm"><CopyChip value={user.studentCode} label="نسخ الكود" /></span> : null}
            </div>
          </div>
          <div className="grid gap-2 sm:w-auto">
            <Button size="lg" onClick={onJoin} icon={ArrowLeft} className="w-full !bg-white !text-primary shadow-none hover:!bg-white/90 sm:w-auto">أدخل كودك الشخصي</Button>
            {!user?.studentCode ? <p className="max-w-[220px] text-[10.5px] leading-5 text-white/60">إن لم يظهر كودك هنا، سجّل الخروج ثم الدخول ليتم توليده تلقائياً.</p> : null}
          </div>
        </div>
      </motion.div>

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <StatCard icon={FileText} label="المحاولات" value={formatNumber(items.length)} note="محاولات مسلّمة" loading={loading} />
        <StatCard icon={Target} label="متوسط النتائج" value={`${formatNumber(average)}%`} note="من الامتحانات المصححة" tone="blue" loading={loading} />
        <StatCard icon={Award} label="أفضل نتيجة" value={`${formatNumber(best)}%`} note="أعلى نسبة حققتها" tone="emerald" loading={loading} />
        <StatCard icon={TrendingUp} label="قيد المراجعة" value={formatNumber(items.filter((item) => item.status === 'NeedsReview').length)} note="بانتظار المدرس" tone="warning" loading={loading} />
      </div>

      <div className="mt-4 grid gap-4 xl:grid-cols-[1.4fr_1fr]">
        <Card className="overflow-hidden">
          <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2 border-b border-line px-4 py-4 sm:px-5">
            <div><h2 className="font-display text-[15px] font-semibold text-ink">أحدث النتائج</h2><p className="mt-1 text-[11.5px] text-muted">آخر الامتحانات التي سلّمتها.</p></div>
            <Button variant="ghost" size="sm" iconEnd={ArrowLeft} onClick={onViewResults}>عرض الكل</Button>
          </div>
          {loading ? (
            <div className="grid gap-2 p-4">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-14 w-full" />)}</div>
          ) : recent.length === 0 ? (
            <EmptyState icon={BookOpen} title="لم تسلّم أي امتحان بعد" description="عندما تدخل امتحاناً وتسلّمه، ستظهر نتيجتك هنا مع إمكانية مراجعة الإجابات." action={<Button size="sm" icon={KeyRound} onClick={onJoin}>دخول أول امتحان</Button>} />
          ) : (
            <div className="grid gap-2 p-3 sm:p-4">
              {recent.map((item) => {
                const value = percent(item.score, item.maxScore);
                return (
                  <button key={item.id} type="button" onClick={() => onOpenReview(item.id)} className="group flex items-center gap-3 rounded-[13px] border border-line bg-surface-soft/50 p-3 text-right transition hover:border-primary/30 hover:bg-primary-soft/30">
                    <span className="grid size-9 shrink-0 place-items-center rounded-[10px] bg-primary-soft text-primary"><FileText size={16} /></span>
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2"><p className="truncate text-[13px] font-bold text-ink">{item.examTitle}</p><StatusChip status={item.status} className="px-2 py-1 text-[10px]" /></div>
                      <div className="mt-1.5 flex flex-wrap items-center gap-2">
                        <ProgressBar value={value} className="h-1.5 max-w-[110px]" tone={value >= 60 ? 'emerald' : value >= 50 ? 'warning' : 'danger'} />
                        <span className="text-[10.5px] text-muted">{formatDate(item.submittedAtUtc)}</span>
                      </div>
                    </div>
                    <div className="shrink-0 text-left"><strong className="ui-number block text-[13px] text-ink">{scoreText(item.score, item.maxScore)}</strong><span className="ui-number text-[10.5px] text-muted">{formatNumber(value)}%</span></div>
                  </button>
                );
              })}
            </div>
          )}
        </Card>

        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-1">
          <Card className="p-5">
            <span className="grid size-10 place-items-center rounded-[12px] bg-emerald-soft text-emerald"><Radio size={18} /></span>
            <h3 className="mt-3 font-display text-[14px] font-semibold text-ink">كيف تعمل غرفة الانتظار؟</h3>
            <div className="mt-3 grid gap-2.5">
              {[
                { icon: KeyRound, text: 'أدخل كودك الشخصي، وسيتحقق النظام من حسابك.' },
                { icon: Clock3, text: 'انتظر داخل الغرفة حتى يفتح المدرس الجلسة.' },
                { icon: CheckCircle2, text: 'سيبدأ الوقت تلقائياً. أجب ثم سلّم قبل انتهاء المدة.' },
              ].map((step, index) => (
                <div key={step.text} className="flex gap-2.5">
                  <span className="grid size-6 shrink-0 place-items-center rounded-full bg-primary-soft text-[10.5px] font-bold text-primary">{index + 1}</span>
                  <p className="text-[11.5px] leading-5 text-muted">{step.text}</p>
                </div>
              ))}
            </div>
          </Card>

          <Card className="p-5">
            <div className="flex items-center justify-between"><h3 className="font-display text-[14px] font-semibold text-ink">تنبيه مهم</h3><Badge tone="warning">للجميع</Badge></div>
            <p className="mt-2 text-[12px] leading-6 text-muted">كودك الشخصي مرتبط بحسابك وبياناتك ولا يتغير. لا تشاركه مع أي طالب آخر — الدخول به هو ما يسجّل حضورك.</p>
          </Card>
        </div>
      </div>
    </div>
  );
}
