import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Eye, Globe2, Link2, Plus, RefreshCw, Save, Send, Trash2, Undo2, UserRound } from 'lucide-react';
import { Alert, Badge, Button, Card, Field, Input, PageHeader, Select, Skeleton, cn } from '../components/ui';
import TeacherPortfolioView from '../components/TeacherPortfolioView';
import { request } from '../lib/api';
import { EDUCATION_STAGES, gradesForStage } from '../lib/education';
import { normalizeDisplayName } from '../lib/validation';

const emptyForm = {
  professionalName: '',
  specialty: '',
  bio: '',
  qualifications: '',
  experience: '',
  yearsOfExperience: '',
  teachingStyle: '',
  extraInfo: '',
  avatarUrl: '',
  coverUrl: '',
  contactPhone: '',
  stages: [],
  grades: [],
  socialLinks: [],
  showEmail: false,
  showPhone: false,
  publicSlug: '',
};

function mapProfile(profile) {
  if (!profile) return { ...emptyForm };
  return {
    professionalName: profile.professionalName || '',
    specialty: profile.specialty || '',
    bio: profile.bio || '',
    qualifications: profile.qualifications || '',
    experience: profile.experience || '',
    yearsOfExperience: profile.yearsOfExperience ?? '',
    teachingStyle: profile.teachingStyle || '',
    extraInfo: profile.extraInfo || '',
    avatarUrl: profile.avatarUrl || '',
    coverUrl: profile.coverUrl || '',
    contactPhone: profile.contactPhone || '',
    stages: Array.isArray(profile.stages) ? profile.stages : [],
    grades: Array.isArray(profile.grades) ? profile.grades : [],
    socialLinks: Array.isArray(profile.socialLinks) ? profile.socialLinks : [],
    showEmail: Boolean(profile.showEmail),
    showPhone: Boolean(profile.showPhone),
    publicSlug: profile.publicSlug || '',
  };
}

function isHttpUrl(value) {
  if (!value) return true;
  try {
    const url = new URL(value);
    return url.protocol === 'https:';
  } catch {
    return false;
  }
}

export default function TeacherProfilePage({ token }) {
  const [form, setForm] = useState({ ...emptyForm });
  const [teacher, setTeacher] = useState(null);
  const [status, setStatus] = useState('Draft');
  const [publicSlug, setPublicSlug] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [publishing, setPublishing] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [preview, setPreview] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const data = await request('/api/teacher/profile', { token });
      setTeacher(data?.teacher || null);
      setForm(mapProfile(data?.profile));
      setStatus(data?.profile?.status || 'Draft');
      setPublicSlug(data?.profile?.publicSlug || data?.suggestedSlug || '');
      setError('');
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }, [token]);

  useEffect(() => { load(); }, [load]);

  function update(field) {
    return (event) => setForm((current) => ({ ...current, [field]: event.target.value }));
  }

  function toggleStage(stage) {
    setForm((current) => {
      const selected = current.stages.includes(stage);
      const stages = selected ? current.stages.filter((item) => item !== stage) : [...current.stages, stage];
      const allowed = new Set(stages.flatMap((item) => gradesForStage(item)));
      return { ...current, stages, grades: current.grades.filter((grade) => allowed.has(grade)) };
    });
  }

  function toggleGrade(grade) {
    setForm((current) => ({
      ...current,
      grades: current.grades.includes(grade)
        ? current.grades.filter((item) => item !== grade)
        : [...current.grades, grade],
    }));
  }

  function updateLink(index, field, value) {
    setForm((current) => ({
      ...current,
      socialLinks: current.socialLinks.map((link, i) => (i === index ? { ...link, [field]: value } : link)),
    }));
  }

  function addLink() {
    setForm((current) => current.socialLinks.length >= 8 ? current : ({
      ...current,
      socialLinks: [...current.socialLinks, { label: '', url: '' }],
    }));
  }

  function removeLink(index) {
    setForm((current) => ({ ...current, socialLinks: current.socialLinks.filter((_, i) => i !== index) }));
  }

  function validate() {
    if (normalizeDisplayName(form.professionalName).length < 2) return 'أدخل الاسم المهني (حرفان على الأقل).';
    if (form.yearsOfExperience !== '' && (Number(form.yearsOfExperience) < 0 || Number(form.yearsOfExperience) > 70)) return 'سنوات الخبرة يجب أن تكون بين 0 و70.';
    if (!isHttpUrl(form.avatarUrl) || !isHttpUrl(form.coverUrl)) return 'روابط الصور يجب أن تكون روابط HTTPS آمنة.';
    if (form.publicSlug && !/^[a-z0-9](?:[a-z0-9-]{1,62})[a-z0-9]$/.test(form.publicSlug)) return 'الرابط العام يقبل الحروف الإنجليزية الصغيرة والأرقام والشرطة فقط (3-64 حرفاً).';
    for (const link of form.socialLinks) {
      if (!link.label?.trim() || !isHttpUrl(link.url)) return 'راجع اسم ورابط كل وسيلة تواصل.';
    }
    return '';
  }

  function payload() {
    return {
      professionalName: normalizeDisplayName(form.professionalName),
      specialty: normalizeDisplayName(form.specialty),
      bio: form.bio.trim(),
      qualifications: form.qualifications.trim(),
      experience: form.experience.trim(),
      yearsOfExperience: form.yearsOfExperience === '' ? null : Number(form.yearsOfExperience),
      teachingStyle: form.teachingStyle.trim(),
      extraInfo: form.extraInfo.trim(),
      avatarUrl: form.avatarUrl.trim(),
      coverUrl: form.coverUrl.trim(),
      contactPhone: form.contactPhone.trim(),
      stages: form.stages,
      grades: form.grades,
      socialLinks: form.socialLinks.map((link) => ({ label: link.label.trim(), url: link.url.trim() })),
      showEmail: form.showEmail,
      showPhone: form.showPhone,
      publicSlug: form.publicSlug.trim().toLowerCase(),
    };
  }

  async function save(silent = false) {
    const validation = validate();
    if (validation) {
      if (!silent) setError(validation);
      return false;
    }
    setSaving(true);
    setError('');
    if (!silent) setNotice('');
    try {
      const result = await request('/api/teacher/profile', { token, method: 'PUT', body: JSON.stringify(payload()) });
      setPublicSlug(result?.publicSlug || form.publicSlug);
      setForm((current) => ({ ...current, publicSlug: result?.publicSlug || current.publicSlug }));
      if (!silent) setNotice(result?.message || 'تم حفظ الصفحة التعريفية.');
      return true;
    } catch (err) {
      setError(err.message);
      return false;
    } finally {
      setSaving(false);
    }
  }

  async function publish() {
    const saved = await save(true);
    if (!saved) return;
    setPublishing(true);
    setError('');
    setNotice('');
    try {
      const result = await request('/api/teacher/profile/publish', { token, method: 'POST' });
      setStatus(result?.status || 'Published');
      setNotice(result?.message || 'تم نشر الصفحة التعريفية.');
    } catch (err) {
      setError(err.message);
    } finally {
      setPublishing(false);
    }
  }

  async function unpublish() {
    setPublishing(true);
    setError('');
    setNotice('');
    try {
      const result = await request('/api/teacher/profile/unpublish', { token, method: 'POST' });
      setStatus(result?.status || 'Draft');
      setNotice(result?.message || 'تم إخفاء الصفحة التعريفية.');
    } catch (err) {
      setError(err.message);
    } finally {
      setPublishing(false);
    }
  }

  async function copyLink() {
    const path = publicSlug ? `/t/${publicSlug}` : '';
    if (!path) return;
    try {
      await navigator.clipboard.writeText(`${window.location.origin}${path}`);
      setNotice('تم نسخ رابط الصفحة التعريفية.');
    } catch {
      setError('تعذر نسخ الرابط. يمكنك نسخه يدوياً من الحقل.');
    }
  }

  const availableGrades = useMemo(
    () => form.stages.flatMap((stage) => gradesForStage(stage)),
    [form.stages],
  );

  const previewProfile = useMemo(() => ({
    ...form,
    fullName: teacher?.fullName || '',
    email: form.showEmail ? teacher?.email : null,
    phone: form.showPhone ? (form.contactPhone || teacher?.studentPhone) : null,
    yearsOfExperience: form.yearsOfExperience === '' ? null : Number(form.yearsOfExperience),
  }), [form, teacher]);

  return (
    <div>
      <PageHeader
        eyebrow="صفحتي التعريفية"
        title="صفحة المدرس العامة"
        description="عرّف الطلاب بنفسك: التخصص، الخبرات، أسلوب التدريس، ووسائل التواصل التي تختار إظهارها. الصفحة تبقى مسودة حتى تضغط نشر."
        action={(
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="secondary" size="sm" icon={RefreshCw} loading={loading} onClick={load}>تحديث</Button>
            <Button variant="secondary" size="sm" icon={Eye} onClick={() => setPreview((value) => !value)}>{preview ? 'إخفاء المعاينة' : 'معاينة الصفحة'}</Button>
            {status === 'Published' ? (
              <Button variant="ghost" size="sm" icon={Undo2} loading={publishing} onClick={unpublish}>إلغاء النشر</Button>
            ) : (
              <Button size="sm" icon={Send} loading={publishing} onClick={publish}>نشر الصفحة</Button>
            )}
            <Button size="sm" icon={Save} loading={saving} onClick={() => save(false)}>حفظ</Button>
          </div>
        )}
      />

      {error ? <Alert tone="danger" className="mb-4" onClose={() => setError('')}>{error}</Alert> : null}
      {notice ? <Alert tone="success" className="mb-4" onClose={() => setNotice('')}>{notice}</Alert> : null}

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <Badge tone={status === 'Published' ? 'emerald' : 'warning'}>{status === 'Published' ? 'منشورة للطلاب' : 'مسودة — تظهر لك فقط'}</Badge>
        {teacher ? <span className="text-[11px] text-muted">{teacher.fullName}</span> : null}
        {publicSlug ? (
          <button type="button" onClick={copyLink} className="inline-flex min-h-8 items-center gap-1.5 rounded-lg border border-line bg-surface px-2.5 text-[10.5px] font-bold text-muted transition hover:text-primary">
            <Link2 size={12} />/t/{publicSlug}
          </button>
        ) : null}
      </div>

      {loading ? (
        <div className="grid gap-3"><Skeleton className="h-40 w-full" /><Skeleton className="h-64 w-full" /></div>
      ) : preview ? (
        <TeacherPortfolioView profile={previewProfile} preview={status !== 'Published'} />
      ) : (
        <div className="grid gap-4">
          <Card className="p-4 sm:p-5">
            <h2 className="mb-3 flex items-center gap-2 text-[13.5px] font-bold text-ink"><UserRound size={15} className="text-primary" />الهوية المهنية</h2>
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="الاسم المهني" id="tp-professional" hint="الاسم الذي يظهر في صفحتك العامة.">
                <Input id="tp-professional" maxLength={160} value={form.professionalName} onChange={update('professionalName')} placeholder="مثال: أ. أحمد جبريل" />
              </Field>
              <Field label="التخصص / المادة" id="tp-specialty">
                <Input id="tp-specialty" maxLength={160} value={form.specialty} onChange={update('specialty')} placeholder="مثال: الرياضيات — الثانوية العامة" />
              </Field>
              <Field label="الصورة الشخصية (رابط)" id="tp-avatar" hint="رابط HTTPS لصورة مربعة مناسبة.">
                <Input id="tp-avatar" dir="ltr" maxLength={500} value={form.avatarUrl} onChange={update('avatarUrl')} placeholder="https://..." className="text-left" />
              </Field>
              <Field label="صورة الغلاف (رابط)" id="tp-cover" hint="رابط HTTPS لصورة عريضة.">
                <Input id="tp-cover" dir="ltr" maxLength={500} value={form.coverUrl} onChange={update('coverUrl')} placeholder="https://..." className="text-left" />
              </Field>
              <Field label="سنوات الخبرة" id="tp-years">
                <Input id="tp-years" type="number" min={0} max={70} value={form.yearsOfExperience} onChange={update('yearsOfExperience')} placeholder="مثال: 8" />
              </Field>
              <Field label="رقم التواصل (اختياري)" id="tp-contact-phone" hint="يظهر للطلاب فقط عند تفعيل إظهار الهاتف.">
                <Input id="tp-contact-phone" dir="ltr" inputMode="tel" maxLength={20} value={form.contactPhone} onChange={update('contactPhone')} placeholder="01xxxxxxxxx" className="text-left" />
              </Field>
              <Field label="الرابط العام للصفحة" id="tp-slug" hint={`يظهر كـ ${window.location.origin}/t/${form.publicSlug || '...'}`}>
                <Input id="tp-slug" dir="ltr" maxLength={64} value={form.publicSlug} onChange={(event) => setForm((current) => ({ ...current, publicSlug: event.target.value.toLowerCase() }))} placeholder="ahmed-math" className="text-left" />
              </Field>
            </div>
          </Card>

          <Card className="p-4 sm:p-5">
            <h2 className="mb-3 flex items-center gap-2 text-[13.5px] font-bold text-ink"><Globe2 size={15} className="text-primary" />نبذتك وخبراتك</h2>
            <div className="grid gap-3">
              <Field label="نبذة تعريفية" id="tp-bio">
                <textarea id="tp-bio" value={form.bio} onChange={update('bio')} maxLength={2000} rows={4} className="w-full rounded-[11px] border border-line bg-surface px-3 py-2.5 text-[12.5px] leading-6 text-ink outline-none transition focus:border-primary focus:ring-4 focus:ring-primary-soft" placeholder="من أنت؟ وما الذي يميز تجربتك التعليمية؟" />
              </Field>
              <Field label="المؤهلات العلمية" id="tp-qualifications">
                <textarea id="tp-qualifications" value={form.qualifications} onChange={update('qualifications')} maxLength={2000} rows={3} className="w-full rounded-[11px] border border-line bg-surface px-3 py-2.5 text-[12.5px] leading-6 text-ink outline-none transition focus:border-primary focus:ring-4 focus:ring-primary-soft" placeholder="مثال: بكالوريوس علوم — جامعة القاهرة، دبلوم تربوي..." />
              </Field>
              <Field label="الخبرات التعليمية" id="tp-experience">
                <textarea id="tp-experience" value={form.experience} onChange={update('experience')} maxLength={2000} rows={3} className="w-full rounded-[11px] border border-line bg-surface px-3 py-2.5 text-[12.5px] leading-6 text-ink outline-none transition focus:border-primary focus:ring-4 focus:ring-primary-soft" placeholder="أماكن العمل السابقة، عدد السنوات، أو إنجازات طلابك." />
              </Field>
              <Field label="أسلوبك في التدريس" id="tp-style">
                <textarea id="tp-style" value={form.teachingStyle} onChange={update('teachingStyle')} maxLength={2000} rows={3} className="w-full rounded-[11px] border border-line bg-surface px-3 py-2.5 text-[12.5px] leading-6 text-ink outline-none transition focus:border-primary focus:ring-4 focus:ring-primary-soft" placeholder="كيف تشرح؟ وكيف تتابع تقدم الطالب؟" />
              </Field>
              <Field label="معلومات إضافية" id="tp-extra">
                <textarea id="tp-extra" value={form.extraInfo} onChange={update('extraInfo')} maxLength={2000} rows={2} className="w-full rounded-[11px] border border-line bg-surface px-3 py-2.5 text-[12.5px] leading-6 text-ink outline-none transition focus:border-primary focus:ring-4 focus:ring-primary-soft" placeholder="أي شيء آخر تريد أن يعرفه الطالب." />
              </Field>
            </div>
          </Card>

          <Card className="p-4 sm:p-5">
            <h2 className="mb-3 text-[13.5px] font-bold text-ink">المراحل والصفوف التي تدرسها</h2>
            <div className="flex flex-wrap gap-1.5">
              {EDUCATION_STAGES.map((stage) => (
                <button key={stage} type="button" onClick={() => toggleStage(stage)} aria-pressed={form.stages.includes(stage)} className={cn('min-h-9 rounded-lg border px-3 text-[11.5px] font-bold transition', form.stages.includes(stage) ? 'border-primary bg-primary-soft text-primary' : 'border-line bg-surface text-muted hover:border-line-strong')}>{stage}</button>
              ))}
            </div>
            {availableGrades.length ? (
              <div className="mt-3 flex flex-wrap gap-1.5 border-t border-line pt-3">
                {availableGrades.map((grade) => (
                  <button key={grade} type="button" onClick={() => toggleGrade(grade)} aria-pressed={form.grades.includes(grade)} className={cn('min-h-8 rounded-lg border px-2.5 text-[10.5px] font-bold transition', form.grades.includes(grade) ? 'border-emerald bg-emerald-soft text-emerald' : 'border-line bg-surface text-muted hover:border-line-strong')}>{grade}</button>
                ))}
              </div>
            ) : null}
          </Card>

          <Card className="p-4 sm:p-5">
            <div className="mb-3 flex items-center justify-between gap-2">
              <h2 className="text-[13.5px] font-bold text-ink">وسائل التواصل</h2>
              <Button size="xs" variant="secondary" icon={Plus} onClick={addLink} disabled={form.socialLinks.length >= 8}>إضافة رابط</Button>
            </div>
            {form.socialLinks.length === 0 ? <p className="text-[11.5px] text-muted">لم تضف أي وسيلة تواصل بعد. أضف ما تريد إظهاره فقط.</p> : (
              <div className="grid gap-2">
                {form.socialLinks.map((link, index) => (
                  <div key={index} className="grid gap-2 sm:grid-cols-[1fr_2fr_auto]">
                    <Input aria-label={`اسم الرابط ${index + 1}`} maxLength={40} value={link.label} onChange={(event) => updateLink(index, 'label', event.target.value)} placeholder="مثال: يوتيوب" />
                    <Input aria-label={`رابط التواصل ${index + 1}`} dir="ltr" maxLength={500} value={link.url} onChange={(event) => updateLink(index, 'url', event.target.value)} placeholder="https://..." className="text-left" />
                    <Button size="sm" variant="ghost" icon={Trash2} onClick={() => removeLink(index)} aria-label="حذف الرابط" />
                  </div>
                ))}
              </div>
            )}
            <div className="mt-4 grid gap-2 border-t border-line pt-3 sm:grid-cols-2">
              <label className="flex min-h-10 items-center gap-2 rounded-lg border border-line bg-surface-soft px-3 text-[11.5px] font-bold text-muted">
                <input type="checkbox" checked={form.showEmail} onChange={(event) => setForm((current) => ({ ...current, showEmail: event.target.checked }))} />
                إظهار البريد الإلكتروني
              </label>
              <label className="flex min-h-10 items-center gap-2 rounded-lg border border-line bg-surface-soft px-3 text-[11.5px] font-bold text-muted">
                <input type="checkbox" checked={form.showPhone} onChange={(event) => setForm((current) => ({ ...current, showPhone: event.target.checked }))} />
                إظهار رقم الهاتف
              </label>
            </div>
            <p className="mt-2 text-[10.5px] leading-5 text-subtle">البريد والهاتف مخفيان افتراضياً. لا يظهران للطلاب إلا إذا فعّلت الخيار صراحةً.</p>
          </Card>

          <div className="flex flex-wrap items-center justify-between gap-2 rounded-[14px] border border-line bg-surface-soft p-3">
            <p className="text-[10.5px] leading-5 text-subtle">الحفظ يحفظ المسودة فقط. النشر يظهر الصفحة للطلاب. يمكنك المعاينة قبل النشر.</p>
            <div className="flex gap-2">
              <Button variant="secondary" size="sm" icon={Eye} onClick={() => setPreview(true)}>معاينة</Button>
              <Button size="sm" icon={Save} loading={saving} onClick={() => save(false)}>حفظ</Button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
