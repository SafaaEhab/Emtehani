import React from 'react';
import { Award, BookOpenCheck, BriefcaseBusiness, ExternalLink, GraduationCap, Mail, Phone, Sparkles, UserRound } from 'lucide-react';
import { Badge, Card } from './ui';

function Section({ icon: Icon, title, children }) {
  if (!children) return null;
  return (
    <Card className="p-5">
      <div className="mb-2 flex items-center gap-2 text-[13px] font-bold text-ink">
        <span className="grid size-8 place-items-center rounded-[9px] bg-primary-soft text-primary"><Icon size={15} /></span>
        {title}
      </div>
      <div className="whitespace-pre-line text-[12.5px] leading-7 text-muted">{children}</div>
    </Card>
  );
}

export default function TeacherPortfolioView({ profile, preview = false }) {
  if (!profile) return null;
  const stages = Array.isArray(profile.stages) ? profile.stages : [];
  const grades = Array.isArray(profile.grades) ? profile.grades : [];
  const links = Array.isArray(profile.socialLinks) ? profile.socialLinks : [];
  const cover = profile.coverUrl || '';
  const avatar = profile.avatarUrl || '';

  return (
    <div className="overflow-hidden rounded-[20px] border border-line bg-surface">
      <div
        className="relative h-36 bg-gradient-to-l from-primary/25 via-primary-soft to-emerald-soft sm:h-44"
        style={cover ? { backgroundImage: `url(${cover})`, backgroundSize: 'cover', backgroundPosition: 'center' } : undefined}
      >
        {preview ? <Badge tone="warning" className="absolute left-3 top-3">معاينة — غير منشورة</Badge> : null}
      </div>
      <div className="px-4 pb-5 sm:px-6">
        <div className="-mt-10 flex flex-wrap items-end gap-4 sm:-mt-12">
          <div className="grid size-20 shrink-0 place-items-center overflow-hidden rounded-[18px] border-4 border-surface bg-primary-soft text-primary sm:size-24">
            {avatar ? <img src={avatar} alt="" className="size-full object-cover" /> : <UserRound size={34} />}
          </div>
          <div className="min-w-0 flex-1 pb-1">
            <h1 className="truncate font-display text-[1.25rem] font-bold text-ink sm:text-[1.5rem]">{profile.professionalName || profile.fullName}</h1>
            <p className="mt-0.5 text-[12px] text-muted">{profile.specialty || 'مدرس'} · {profile.fullName}</p>
            <div className="mt-2 flex flex-wrap items-center gap-1.5">
              {profile.yearsOfExperience ? <Badge tone="primary">{profile.yearsOfExperience} سنة خبرة</Badge> : null}
              {stages.map((stage) => <Badge key={stage} tone="neutral">{stage}</Badge>)}
            </div>
          </div>
        </div>

        <div className="mt-4 grid gap-3">
          <Section icon={Sparkles} title="نبذة تعريفية">{profile.bio}</Section>
          <Section icon={GraduationCap} title="المؤهلات العلمية">{profile.qualifications}</Section>
          <Section icon={BriefcaseBusiness} title="الخبرات التعليمية">{profile.experience}</Section>
          <Section icon={BookOpenCheck} title="أسلوب التدريس">{profile.teachingStyle}</Section>
          <Section icon={Award} title="معلومات إضافية">{profile.extraInfo}</Section>

          {grades.length ? (
            <Card className="p-5">
              <div className="mb-2 flex items-center gap-2 text-[13px] font-bold text-ink">
                <span className="grid size-8 place-items-center rounded-[9px] bg-primary-soft text-primary"><GraduationCap size={15} /></span>
                المراحل والصفوف
              </div>
              <div className="flex flex-wrap gap-1.5">
                {grades.map((grade) => <Badge key={grade} tone="neutral">{grade}</Badge>)}
              </div>
            </Card>
          ) : null}

          {(links.length > 0 || profile.email || profile.phone) ? (
            <Card className="p-5">
              <p className="mb-2 text-[13px] font-bold text-ink">التواصل</p>
              <div className="flex flex-wrap gap-2">
                {profile.email ? (
                  <a href={`mailto:${profile.email}`} className="inline-flex min-h-9 items-center gap-1.5 rounded-lg border border-line bg-surface px-3 text-[11.5px] font-bold text-muted transition hover:text-primary"><Mail size={13} />{profile.email}</a>
                ) : null}
                {profile.phone ? (
                  <a href={`tel:${profile.phone}`} className="inline-flex min-h-9 items-center gap-1.5 rounded-lg border border-line bg-surface px-3 text-[11.5px] font-bold text-muted transition hover:text-primary" dir="ltr"><Phone size={13} />{profile.phone}</a>
                ) : null}
                {links.map((link, index) => (
                  <a key={`${link.url}-${index}`} href={link.url} target="_blank" rel="noreferrer" className="inline-flex min-h-9 items-center gap-1.5 rounded-lg border border-line bg-surface px-3 text-[11.5px] font-bold text-muted transition hover:text-primary">
                    <ExternalLink size={13} />{link.label || 'رابط'}
                  </a>
                ))}
              </div>
            </Card>
          ) : null}
        </div>
      </div>
    </div>
  );
}
