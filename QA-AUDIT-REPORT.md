# QA Audit Report — Emtehani (Actual Results Only)

## Latest Verification Run
- Date: 2026-10-26 (latest)
- Backend (dev): http://127.0.0.1:5000 — `/api` OK, `/health` Healthy
- Frontend (dev): http://127.0.0.1:5173 — running
- E2E API: http://127.0.0.1:5099 (spawned by Playwright)
- E2E app: http://127.0.0.1:4173 (Vite dev, spawned by Playwright)
- E2E PostgreSQL: `127.0.0.1:55432`, database `emtehani_e2e`, UTF-8
- No deploy; no production data touched; running services kept intact.

## 1. PostgreSQL Connection & Isolation (VERIFIED)
Verified directly against the server:
- `inet_server_addr()` = `127.0.0.1`, `inet_server_port()` = `55432`, user `postgres`.
- Databases present: `emtehani_e2e`, `postgres` only (no dev/prod database).
- `server_encoding` = `UTF8`; Arabic round-trip `'اختبار'` succeeded.
- Schema tables present: `Users`, `Exams`, `Questions`, `Submissions`, `Answers`, `ExamParticipants`, `AuditEvents`.
- Data is E2E-only (unique emails like `student-8a46e4ab@example.com`, `teacher-8bbf6a23@example.com`).
- Isolation from development: dev backend `Program` uses SQL Server LocalDB `EmtehaniDB` from `appsettings.json`; the E2E API is passed `ConnectionStrings__DefaultConnection` = the 55432 test DB via `e2e/start-api.mjs`.
- Isolation from production: no production connection string or Vercel config referenced; `DATABASE_URL` not set in the test shell.
- Data directory is disposable: `%TEMP%\emtehani-pg\pgdata`.

## 2. Suites Executed (exact commands)
Environment used for all runs:
`$env:E2E_DATABASE_CONNECTION = "Host=127.0.0.1;Port=55432;Database=emtehani_e2e;Username=postgres;Password=postgres;SSL Mode=Disable"`

### `auth.spec.js`
`npx playwright test -c playwright.config.js e2e/auth.spec.js`
**Result: 2 passed, 0 failed, 0 skipped** (28.0s)
- desktop-chromium: teacher registration + RTL + student login — PASS
- mobile-chromium: same flow — PASS

### `exam-lifecycle.spec.js`
`npx playwright test -c playwright.config.js e2e/exam-lifecycle.spec.js`
**Result: 1 passed, 1 skipped, 0 failed** (37.9s)
- desktop-chromium lifecycle (autosave, reload/resume, idempotent submit, CSV permissions) — PASS
- mobile-chromium — **intentional `test.skip`** (`The full lifecycle is covered once on desktop.`)

### `qa-audit.spec.js`
`npx playwright test -c playwright.config.js e2e/qa-audit.spec.js`
**Result: 8 passed, 6 skipped, 0 failed** (2.7m)

Passed (8):
1. Public landing: anchors, FAQ, theme persistence, links, portfolio route
2. Authentication: validation, registration, duplicate email, login, password toggle
3. Teacher UI: create exam, validation, publish, open/pause/resume/close, results, essays, audit filters, CSV
4. Student lifecycle: available exams, lobby, all question types, autosave, copy detection, submit, review, essay grading, extra attempt
5. API authorization: unauthenticated/role/cross-teacher/cross-student/CORS
6. Responsive sweep at 1440/390/320 px
12. API authorization (mobile project)
14. Mobile exam room: question map, submit dialog, review

Skipped (6) — all intentional `test.skip` project guards, not test failures:
- `mobile exam room` skipped on desktop project (`Mobile-only interaction audit.`)
- On mobile project: `public landing`, `authentication`, `teacher UI`, `student lifecycle`, `responsive sweep` skipped (desktop-only guards).

## 3. Passed / Failed / Skipped / Blocked
| Category | Count |
|---|---|
| Passed | 2 + 1 + 8 = **11** |
| Failed | **0** |
| Skipped (intentional project guards) | 1 + 6 = **7** |
| Blocked | **0** |

## 4. Failures and Fixes
- No failures in this run. No changes were required.
- Earlier environment-only fix retained: E2E DB recreated as UTF-8 to allow Arabic (`22P05` → resolved).
- No application code changed after the last verified suite runs.

## 5. Backend Tests / Frontend Build
- Not rerun in this pass because no application code changed (per instruction).
- Last verified results (same codebase): `dotnet test` with isolated PostgreSQL → **36 passed, 0 failed, 0 skipped**; `dotnet build -c Release` → **0 warnings, 0 errors**; `npm run build` → **success**.

## 6. Readiness
- Application is ready for further staging verification.
- All documented Playwright suites pass against the isolated PostgreSQL E2E database.
- For CI/staging, use a managed PostgreSQL 17+ instance with UTF-8 encoding (the temp `embedded-postgres` harness is local-only).

## 7. Artifacts
- `QA-AUDIT-REPORT.md` (this file)
- `frontend/test-results/` (no failures in final runs)
- Temp PG harness: `%TEMP%\emtehani-pg\{start-pg.mjs,recreate-db.mjs,verify-isolation.mjs,pgdata}`
