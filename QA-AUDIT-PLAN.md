# QA Audit Plan — Emtehani Full-Stack

## 1. Project Inspected
- Backend: ASP.NET Core 10 (Emtehani.API / Domain / App / Infra / Tests)
- Frontend: React 19 + Vite + Tailwind CSS (RTL Arabic)
- Tests: Playwright 1.64.1 (e2e/auth.spec.js, exam-lifecycle.spec.js, qa-audit.spec.js 864 lines)
- DB: SQL Server (LocalDB) default; PostgreSQL via DATABASE_URL

## 2. Pages / Workflows Identified
Public: Landing
Auth: Login, Register (teacher/student roles), email verification, password toggle
Teacher: Dashboard, Exams (list/search/filter), ExamForm (create), TeacherExamDetail (publish/open/pause/resume/close), Results (CSV export), Essays (grading), Audit (search/filter), Participant codes
Student: Dashboard, AvailableExams (search/filter), JoinExam (code entry), Lobby (wait for open), ExamRoom (autosave, copy detection, submit, resume), Results (review submissions), EssayReview

## 3. Startup Diagnosis
- .NET 10 SDK present (10.0.402)
- Playwright installed (1.64.1)
- No running backend or DB process found initially
- Backend appsettings uses LocalDB; must set DATABASE_URL or start with env vars
- Frontend Vite dev server configured for port 5173, proxy to backend 5000

## 4. Testing Plan (stages)
Stage A: Start backend safely with env, verify /health
Stage B: Start frontend dev server on actual port
Stage C: Playwright open and basic navigation (landing, auth, pages)
Stage D: Full workflows (teacher create/open/close, student join/take/submit/review)
Stage E: API authorization, responsive sweep, console/network errors
Stage F: Run automated qa-audit.spec.js, save artifacts
Stage G: Fix verified bugs only after code inspection, rerun

## 5. Actual results will be reported after each stage.
No claims made until executed.
