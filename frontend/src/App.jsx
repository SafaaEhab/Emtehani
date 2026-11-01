import React, { useEffect, useState } from 'react';
import { AnimatePresence, motion } from 'motion/react';
import { ArrowRight, LoaderCircle, LogOut, Sparkles } from 'lucide-react';
import DeveloperPage from './DeveloperPage.jsx';
import AppShell from './components/AppShell';
import SessionCountdown from './components/SessionCountdown';
import { Button, Logo } from './components/ui';
import LandingPage from './pages/LandingPage';
import AuthPage from './pages/AuthPage';
import TeacherDashboard from './pages/TeacherDashboard';
import StudentDashboard from './pages/StudentDashboard';
import ExamsPage from './pages/ExamsPage';
import ExamForm from './pages/ExamForm';
import TeacherExamDetail from './pages/TeacherExamDetail';
import ResultsPage from './pages/ResultsPage';
import EssaysPage from './pages/EssaysPage';
import TeacherRequestsPage from './pages/TeacherRequestsPage';
import TeacherStudentsPage from './pages/TeacherStudentsPage';
import TeachersPage from './pages/TeachersPage';
import AuditPage from './pages/AuditPage';
import AvailableExamsPage from './pages/AvailableExamsPage';
import LobbyPage from './pages/LobbyPage';
import ExamRoomPage from './pages/ExamRoomPage';
import ReviewDetailPage from './pages/ReviewDetailPage';
import EssayReviewPage from './pages/EssayReviewPage';
import StudentProfilePage from './pages/StudentProfilePage';
import SubscriptionPage from './pages/SubscriptionPage';
import AdminDashboard from './pages/AdminDashboard';
import { authUserFromResponse, clearAuth, getStoredAuth, request, storeAuth, storeSession } from './lib/api';
import { installWebMcpTools } from './lib/webmcp';

const teacherTitles = {
  dashboard: 'مركز التحكم',
  exams: 'الامتحانات',
  create: 'إنشاء امتحان',
  'exam-detail': 'إدارة الجلسة',
  results: 'النتائج والتحليلات',
  essays: 'مراجعة المقالات',
  requests: 'طلبات الطلاب',
  students: 'إدارة الطلاب',
  subscription: 'الاشتراك',
  audit: 'سجل النشاط',
};

const studentTitles = {
  dashboard: 'نظرة عامة',
  available: 'الامتحانات المتاحة',
  results: 'نتائجي',
  teachers: 'المدرسون',
  profile: 'ملفي الأكاديمي',
};

const adminTitles = {
  dashboard: 'لوحة الإدارة',
  admin: 'لوحة الإدارة',
  'admin-teachers': 'المدرسون والاشتراكات',
};

const activePages = {
  'exam-detail': 'exams',
  review: 'results',
  'essay-review': 'essays',
};

function initialTheme() {
  try {
    const query = new URLSearchParams(window.location.search).get('theme');
    if (query === 'dark' || query === 'light') return query;
  } catch { /* ignore */ }
  const saved = localStorage.getItem('emtehani-theme');
  if (saved === 'dark' || saved === 'light') return saved;
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

function isPortfolioUrl() {
  try { return new URLSearchParams(window.location.search).has('portfolio'); } catch { return false; }
}

function isInviteUrl() {
  try { return new URLSearchParams(window.location.search).has('invite'); } catch { return false; }
}

export default function App() {
  const stored = getStoredAuth();
  const [token, setToken] = useState(stored.token);
  const [user, setUser] = useState(stored.user);
  const [booting, setBooting] = useState(Boolean(stored.token));
  const [theme, setTheme] = useState(initialTheme);
  const [page, setPage] = useState(() => isPortfolioUrl() ? 'portfolio' : isInviteUrl() ? 'auth' : 'landing');
  const [authMode, setAuthMode] = useState(() => isInviteUrl() ? 'register' : 'login');
  const [selectedExam, setSelectedExam] = useState(null);
  const [joined, setJoined] = useState(null);
  const [paper, setPaper] = useState(null);
  const [reviewId, setReviewId] = useState('');
  const [reviewBack, setReviewBack] = useState('results');
  const [authNotice, setAuthNotice] = useState('');

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem('emtehani-theme', theme);
  }, [theme]);

  // Progressive enhancement: register public, read-only site tools only when
  // the browser exposes WebMCP. Unsupported browsers and private data are
  // unaffected; backend authorization remains authoritative.
  useEffect(() => installWebMcpTools(), []);

  useEffect(() => {
    function onSessionExpired() {
      clearAuth();
      setToken('');
      setUser(null);
      setSelectedExam(null);
      setJoined(null);
      setPaper(null);
      setReviewId('');
      setAuthMode('login');
      setAuthNotice('انتهت صلاحية الجلسة. سجّل الدخول مرة أخرى للمتابعة؛ إجاباتك المحفوظة لن تُفقد وسيُستأنف الامتحان تلقائياً.');
      setPage((current) => current === 'portfolio' ? current : 'auth');
    }
    window.addEventListener('emtehani:session-expired', onSessionExpired);
    return () => window.removeEventListener('emtehani:session-expired', onSessionExpired);
  }, []);

  useEffect(() => {
    if (!stored.token) return;
    let active = true;
    request('/api/auth/me', { token: stored.token })
      .then((me) => {
        if (!active) return;
        const nextUser = { ...stored.user, ...authUserFromResponse(me), ...me };
        const latest = getStoredAuth();
        setUser(nextUser);
        setToken(latest.token || stored.token);
        storeAuth(latest.token || stored.token, nextUser);
        storeSession(me.sessionExpiresAtUtc, me.serverUtc);
        setPage((current) => {
          if (current === 'portfolio') return current;
          return nextUser.role === 'Teacher' && nextUser.subscriptionStatus !== 'Confirmed'
            ? 'subscription'
            : 'dashboard';
        });
      })
      .catch((err) => {
        if (!active) return;
        clearAuth();
        setToken('');
        setUser(null);
        if (err?.status === 401) {
          setAuthMode('login');
          setAuthNotice('انتهت صلاحية الجلسة. سجّل الدخول مرة أخرى للمتابعة.');
          setPage((current) => current === 'portfolio' ? current : 'auth');
        } else {
          setPage((current) => current === 'portfolio' ? current : 'landing');
        }
      })
      .finally(() => { if (active) setBooting(false); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    function onPopState() {
      if (isPortfolioUrl()) setPage('portfolio');
      else setPage((current) => current === 'portfolio' ? (user ? 'dashboard' : 'landing') : current);
    }
    window.addEventListener('popstate', onPopState);
    return () => window.removeEventListener('popstate', onPopState);
  }, [user]);

  useEffect(() => {
    window.scrollTo({ top: 0, behavior: 'auto' });
  }, [page]);

  useEffect(() => {
    document.title = page === 'portfolio'
      ? 'Ahmed Gbreel · Full Stack Developer'
      : user
        ? `${user.role === 'Admin' ? 'لوحة الإدارة' : user.role === 'Teacher' ? 'مركز التحكم' : 'مساحة الطالب'} · امتحاني`
        : 'امتحاني · منصة امتحانات عربية';
  }, [page, user]);

  async function authenticate(mode, payload) {
    const data = await request(`/api/auth/${mode === 'login' ? 'login' : 'register'}`, {
      method: 'POST',
      body: JSON.stringify(mode === 'login' ? { email: payload.email, password: payload.password } : payload),
    });
    if (mode === 'register') {
      // The account is created by the register call (which deliberately does not return a
      // session). When the account is already active, sign in immediately so a teacher can
      // choose the Free package right away. If e-mail verification is required, fall back
      // to the normal "check your inbox" flow.
      if (payload.role === 'Teacher') {
        try {
          const login = await request('/api/auth/login', {
            method: 'POST',
            body: JSON.stringify({ email: payload.email.trim(), password: payload.password }),
          });
          const nextUser = authUserFromResponse(login);
          storeAuth(login.token, nextUser, login.refreshToken);
          storeSession(login.sessionExpiresAtUtc, login.serverUtc);
          setToken(login.token);
          setUser(nextUser);
          setAuthNotice('');
          setAuthMode('login');
          setPage('subscription');
          return { registered: true, autoLoggedIn: true, message: data.message, studentCode: data.studentCode };
        } catch {
          // Verification is required or auto-login is unavailable; keep the notice path.
        }
      }
      return { registered: true, message: data.message, studentCode: data.studentCode };
    }
    const nextUser = authUserFromResponse(data);
    storeAuth(data.token, nextUser, data.refreshToken);
    storeSession(data.sessionExpiresAtUtc, data.serverUtc);
    setToken(data.token);
    setUser(nextUser);
    setAuthNotice('');
    setPage(nextUser.role === 'Teacher' && nextUser.subscriptionStatus !== 'Confirmed' ? 'subscription' : 'dashboard');
    setAuthMode('login');
    return { registered: false };
  }

  function logout() {
    try {
      const refreshToken = localStorage.getItem('emtehani-refresh-token');
      if (refreshToken) request('/api/auth/logout', { method: 'POST', body: JSON.stringify({ refreshToken }) }).catch(() => {});
    } catch { /* best-effort revocation */ }
    clearAuth();
    setToken('');
    setUser(null);
    setSelectedExam(null);
    setJoined(null);
    setPaper(null);
    setReviewId('');
    setAuthNotice('');
    setPage('landing');
    setAuthMode('login');
  }

  function openPortfolio() {
    setPage('portfolio');
    try {
      const url = new URL(window.location.href);
      url.searchParams.set('portfolio', '1');
      window.history.pushState(null, '', url);
    } catch { /* ignore */ }
  }

  function closePortfolio() {
    try {
      const url = new URL(window.location.href);
      url.searchParams.delete('portfolio');
      window.history.pushState(null, '', url);
    } catch { /* ignore */ }
    setPage(page === 'portfolio' ? (user ? 'dashboard' : 'landing') : page);
  }

  function navigate(next) {
    if (next === 'portfolio') { openPortfolio(); return; }
    if (user?.role === 'Teacher' && user?.subscriptionStatus !== 'Confirmed' && next !== 'subscription') {
      setPage('subscription');
      return;
    }
    if ((next === 'admin' || next === 'admin-teachers') && user?.role !== 'Admin') { setPage('dashboard'); return; }
    if (next === 'exam-detail' && !selectedExam) { setPage('exams'); return; }
    if (next === 'lobby' && !joined) { setPage('dashboard'); return; }
    if (next === 'take' && !paper) { setPage('dashboard'); return; }
    setPage(next);
  }

  function updateEducation(profile) {
    setUser((current) => {
      const next = { ...current, ...profile };
      storeAuth(token, next);
      return next;
    });
  }

  function updateSubscription(result) {
    setUser((current) => {
      const next = {
        ...current,
        subscriptionStatus: result?.status || 'Confirmed',
        subscriptionPlan: result?.planCode || current?.subscriptionPlan || 'free',
      };
      storeAuth(token, next);
      return next;
    });
  }

  function openExam(exam) {
    setSelectedExam(exam);
    setPage('exam-detail');
  }

  function openResults(exam) {
    if (exam) setSelectedExam(exam);
    setPage('results');
  }

  function openReview(submissionId, back = 'results') {
    setReviewId(submissionId);
    setReviewBack(back);
    setPage('review');
  }

  function openEssayReview(submissionId) {
    setReviewId(submissionId);
    setPage('essay-review');
  }

  function joinedExam(result) {
    if (result.examStatus === 'Finished' || result.participantStatus === 'Submitted') {
      if (result.submissionId) {
        openReview(result.submissionId, 'results');
        return;
      }
    }
    setJoined(result);
    setPage('lobby');
  }

  function startedExam(nextPaper) {
    setPaper(nextPaper);
    setPage('take');
  }

  function submittedExam(result) {
    setPaper(null);
    setJoined(null);
    openReview(result.id, 'results');
  }

  function exitLobby() {
    setJoined(null);
    setPage('dashboard');
  }

  if (booting) {
    return (
      <div className="grid min-h-dvh place-items-center bg-canvas px-4">
        <div className="flex flex-col items-center text-center">
          <Logo />
          <LoaderCircle size={20} className="mt-6 animate-spin text-primary" />
          <p className="mt-2 text-[11px] text-muted">جارٍ تجهيز مساحتك...</p>
        </div>
      </div>
    );
  }

  if (page === 'portfolio') {
    return <DeveloperPage theme={theme} onThemeChange={setTheme} onBack={closePortfolio} />;
  }

  if (!user || !token) {
    if (page === 'auth') {
      return (
        <AuthPage
          initialMode={authMode}
          onAuth={authenticate}
          onBack={() => { setAuthNotice(''); setPage('landing'); }}
          onOpenPortfolio={openPortfolio}
          theme={theme}
          onToggleTheme={() => setTheme((value) => value === 'dark' ? 'light' : 'dark')}
          notice={authNotice}
          noticeTone="warning"
          clearNotice={() => setAuthNotice('')}
        />
      );
    }
    return (
      <LandingPage
        onLogin={() => { setAuthMode('login'); setPage('auth'); }}
        onRegister={() => { setAuthMode('register'); setPage('auth'); }}
        theme={theme}
        onToggleTheme={() => setTheme((value) => value === 'dark' ? 'light' : 'dark')}
        onOpenPortfolio={openPortfolio}
      />
    );
  }

  if (page === 'take' && paper) {
    return <ExamRoomPage token={token} paper={paper} onSubmitted={submittedExam} />;
  }

  if (page === 'lobby' && joined) {
    return (
      <div className="min-h-dvh bg-canvas">
        <header className="mx-auto flex max-w-3xl items-center justify-between gap-3 px-4 py-4 sm:px-6">
          <Logo />
          <div className="flex items-center gap-2">
            <div className="hidden sm:block"><SessionCountdown compact /></div>
            <span className="hidden text-[11px] text-muted sm:inline">{user.fullName}</span>
            <Button variant="ghost" size="xs" icon={LogOut} onClick={logout}>خروج</Button>
          </div>
        </header>
        <div className="px-4 pb-12 sm:px-6">
          <LobbyPage token={token} joined={joined} onStarted={startedExam} onResults={(id) => openReview(id, 'results')} onExit={exitLobby} />
        </div>
      </div>
    );
  }

  const isTeacher = user.role === 'Teacher';
  const isAdmin = user.role === 'Admin';
  const teacherPending = isTeacher && user.subscriptionStatus !== 'Confirmed';
  const effectivePage = teacherPending ? 'subscription' : page;
  const title = (isAdmin ? adminTitles : isTeacher ? teacherTitles : studentTitles)[effectivePage] || 'امتحاني';
  const activePage = activePages[effectivePage] || effectivePage;

  let content;
  if (teacherPending) {
    content = <SubscriptionPage token={token} onConfirmed={updateSubscription} />;
  } else if (isAdmin) {
    content = page === 'admin' || page === 'admin-teachers' || page === 'dashboard'
      ? <AdminDashboard token={token} />
      : <AdminDashboard token={token} />;
  } else if (page === 'dashboard') {
    content = isTeacher
      ? <TeacherDashboard token={token} user={user} onNavigate={navigate} onCreate={() => setPage('create')} onOpenExam={openExam} />
      : <StudentDashboard token={token} user={user} onBrowse={() => setPage('available')} onEnter={joinedExam} onViewResults={() => setPage('results')} onOpenReview={(id) => openReview(id, 'dashboard')} onProfileUpdated={updateEducation} />;
  } else if (page === 'available' && !isTeacher) {
    content = <AvailableExamsPage token={token} onEnter={joinedExam} />;
  } else if (page === 'exams') {
    content = <ExamsPage token={token} onOpenExam={openExam} onCreate={() => setPage('create')} />;
  } else if (page === 'create') {
    content = <ExamForm token={token} onBack={() => setPage('exams')} onCreated={(exam) => { openExam(exam); }} />;
  } else if (page === 'exam-detail' && selectedExam) {
    content = <TeacherExamDetail token={token} exam={selectedExam} onBack={() => setPage('exams')} onOpenResults={openResults} />;
  } else if (page === 'results') {
    content = <ResultsPage token={token} user={user} selectedExam={selectedExam} onOpenReview={(id) => openReview(id, 'results')} onOpenExam={openExam} />;
  } else if (page === 'essays') {
    content = <EssaysPage token={token} onOpenEssayReview={openEssayReview} onOpenResults={openResults} />;
  } else if (page === 'requests' && isTeacher) {
    content = <TeacherRequestsPage token={token} />;
  } else if (page === 'students' && isTeacher) {
    content = <TeacherStudentsPage token={token} />;
  } else if (page === 'teachers' && !isTeacher) {
    content = <TeachersPage token={token} />;
  } else if (page === 'profile' && !isTeacher) {
    content = <StudentProfilePage token={token} onProfileUpdated={updateEducation} />;
  } else if (page === 'subscription' && isTeacher) {
    content = <SubscriptionPage token={token} onConfirmed={updateSubscription} />;
  } else if (page === 'audit') {
    content = <AuditPage token={token} />;
  } else if (page === 'review' && reviewId) {
    content = <ReviewDetailPage token={token} user={user} submissionId={reviewId} onBack={() => setPage(reviewBack)} onOpenEssayReview={openEssayReview} />;
  } else if (page === 'essay-review' && reviewId) {
    content = <EssayReviewPage token={token} submissionId={reviewId} onBack={() => setPage('essays')} />;
  } else {
    content = isTeacher
      ? <TeacherDashboard token={token} user={user} onNavigate={navigate} onCreate={() => setPage('create')} onOpenExam={openExam} />
      : <StudentDashboard token={token} user={user} onBrowse={() => setPage('available')} onEnter={joinedExam} onViewResults={() => setPage('results')} onOpenReview={(id) => openReview(id, 'dashboard')} onProfileUpdated={updateEducation} />;
  }

  return (
    <AppShell user={user} page={effectivePage} activePage={activePage} title={title} onNavigate={navigate} onLogout={logout} theme={theme} onToggleTheme={() => setTheme((value) => value === 'dark' ? 'light' : 'dark')}>
      <AnimatePresence mode="wait">
        <motion.div key={effectivePage} initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0 }} transition={{ duration: 0.2 }}>
          {content}
        </motion.div>
      </AnimatePresence>
    </AppShell>
  );
}
