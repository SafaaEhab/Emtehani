# Regression Test Results

**Date:** 2026-11-05
**Branch:** `remediation/website-audit-2026-11-05`
**Baseline:** 73 passed / 5 skipped / 0 failed (backend).
**Final backend:** **79 passed / 5 skipped / 0 failed** (84 total; +6 regression tests).
**Production regression (2026-11-05):** **43 / 43 checks passed**; see `final-production-deployment.md` and `evidence/production-regression-2026-11-05.json`.

---

## 1. Backend

Command:

```powershell
cd backend
dotnet test Emtehani.slnx --no-restore
```

Result:

```
Passed! - Failed: 0, Passed: 79, Skipped: 5, Total: 84, Duration: 15–28 s
```

New tests (all passing):

1. `FinishedExam_AttemptsAreCounted_AuditShowsSubmission_AndExtraAttemptIsBlocked`
2. `PhoneLookup_MasksUnrelatedStudent_AndRevealsOnlyAfterAcceptance`
3. `PhoneLookup_IsRateLimitedPerTeacher`
4. `PasswordPolicy_RejectsShortAndCommonPasswords`
5. `Login_IsLockedPerAccount_AfterRepeatedFailures`
6. `SpoofedForwardedFor_DoesNotBypassAuthRateLimit`

The five skipped tests are the existing PostgreSQL schema tests; no PostgreSQL instance was available. They are **not** counted as passed.

The test fixture passwords were updated from 9 to 13 characters to match the new 10-character policy. Existing assertions were preserved.

Build:

```
dotnet build Emtehani.slnx              -> 0 errors (27 nullable warnings, pre-existing)
dotnet build Emtehani.slnx -c Release   -> 0 errors
dotnet publish Emtehani.API -c Release  -> success
```

Logs: `evidence/dotnet-test-final.txt`, `evidence/dotnet-release-build.txt`, `evidence/dotnet-publish-final.txt`.

---

## 2. Frontend

```powershell
cd frontend
$env:VITE_API_URL="https://emtehani.runasp.net"
$env:VERCEL_ENV="production"
npm run build
```

Result: **success**, CSP guard pass, 22 page chunks, entry 476.82 KB / 146.21 KB gzip.

```powershell
npm run test:unit
```

Result: **31 passed, 0 failed**.

```powershell
node scripts/verify-csp.mjs   # with wrong.example -> exit 1
node scripts/verify-csp.mjs   # with Monster -> exit 0
```

Logs: `evidence/frontend-build-final.txt`, `evidence/frontend-unit-final.txt`.

---

## 3. Live SQL Server regression (disposable LocalDB)

Fresh database; full flow executed through real HTTP:

- register teacher → confirm Free → register student → accept request
- create exam → publish → open → enter → start
- save/submit MCQ + true/false
- close exam
- teacher student detail: `used=1 completed=1 remaining=0 finishedExams=1 exams=0`
- overview: `openable=0`
- audit: `hasSubmission=true`
- extra attempt: `409`
- unrelated teacher lookup: `masked=true`, stage/grade null
- linked teacher lookup: `masked=false`, stage=ثانوي
- XFF spoof after fix: 429 observed
- fresh startup: 2.15 s (DB creation included); second start ~1.6 s
- five indexes present after startup

Log: `evidence/live-sqlserver-verification.txt`.

A separate LocalDB probe recreated production-like `nvarchar(max)` Status/EntityType/EntityId columns and confirmed the final index SQL creates successfully; the probe database was dropped.

---

## 4. Browser / accessibility

- Lighthouse landing: **100 accessibility / 100 best practices / 100 SEO**, 0 failures.
- Lighthouse login: **100 / 100 / 100**, 0 failures.
- Modal focus: 14 Tab presses stayed inside; Escape restored focus.
- Mobile drawer: 16 Tab presses stayed inside; Escape closed; scroll lock released.
- Mobile menu: full-height 772 px at 390×844, backdrop present, body locked.
- Hash navigation: Back from `#/students` returned to `#/results`; deep link `#/students` restored.
- Password UI: 8-character password rejected with 10–128 message.
- No new console errors observed during these flows.

Evidence: `evidence/lighthouse-local-landing-final.*`, `evidence/lighthouse-local-login-final.*`, `evidence/mobile-menu-fixed.png`, `evidence/frontend-verification.txt`.

---

## 5. What was not run / blocked

| Suite | Reason | Status |
|---|---|---|
| Existing Playwright E2E (`npm run test:e2e`) | Requires isolated PostgreSQL 17 (`E2E_DATABASE_CONNECTION`) and starts API/frontend on 5099/4173; no PostgreSQL instance available | **BLOCKED** |
| PostgreSQL schema tests | No isolated PostgreSQL | **SKIPPED (5)** |
| Production deployment smoke test | Deployed and verified 2026-11-05; 43/43 checks passed | **PASS** (see `final-production-deployment.md`) |
| Admin UI regression | Verified read-only with a temporary disposable Admin account (deleted after) | **PASS (read-only)** |
| Live timer expiry / concurrent autosave-submit / ban-suspend | Existing backend integration tests cover these; not rerun as live timed scenarios | **NOT RUN LIVE** |
| Full performance benchmark | No controlled throttled Lighthouse performance score; no load test | **NOT RUN** |

---

## 6. Comparison to baseline

| Metric | Baseline | Final |
|---|---:|---:|
| Backend passed | 73 | **79** |
| Backend failed | 0 | **0** |
| Backend skipped | 5 | **5** |
| Backend total | 78 | **84** |
| Frontend unit | not part of baseline | **31 passed** |
| Frontend build | passing | **passing + CSP guard** |
| Lighthouse landing A11y | 93 | **100** |
| Lighthouse login A11y | 96 | **100** |
| Lighthouse SEO | 80 | **100** |
| Entry JS | 780.1 KB | **476.8 KB** |

No existing failure was hidden or converted to a skip. The only new warnings are nullable-reference warnings already present in the codebase; no errors.
