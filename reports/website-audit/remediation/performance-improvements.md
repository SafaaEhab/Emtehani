# Performance Improvements

**Principle:** measured changes only; hosting limitations separated from application changes.

---

## 1. Production cold start — resolved by using the authoritative path

**Audit:** Vercel serverless API login cold start 9.9–10.3 s, warm 347–865 ms.
**Investigation:** the slow host was `emtehani-api.vercel.app`, not MonsterASP. MonsterASP/IIS measured:
- `/health`: **139 ms**
- `POST /api/auth/login` (unknown account, expected 401): **320 ms**
- Both APIs share the same Monster SQL database.

**Fix:** the frontend build now targets the authoritative Monster API `https://emtehani.runasp.net` (current Vercel env already points there; the deployed build and CSP were stale). A build-time CSP guard prevents a future mismatch.

**Application-level changes:**
- Startup logs `Database bootstrap completed in {ms}`.
- `Database:BootstrapOnStartup` allows a deployment pipeline to skip runtime bootstrap after schema verification (default remains true).
- No artificial traffic/keep-alive was added.
- EF `EnableRetryOnFailure` was rejected after live SQL Server testing showed it breaks existing explicit transactions; it was reverted. No unverified retry behavior remains.

**Measured local startup:** fresh LocalDB DB + schema + indexes **2.15 s**; second start **~1.6 s**. This is a dev-machine figure, not a Monster benchmark.

**Residual:** Monster IIS app-pool idle/sleep settings are controlled by the hosting panel. If the app unloads after idle, configure app-pool idle timeout/always-running according to the MonsterASP plan. This could not be changed without panel access.

---

## 2. Bundle size / code splitting

| Metric | Before | After |
|---|---:|---:|
| Entry JS (uncompressed) | 780.1 KB | **476.8 KB** |
| Entry JS (gzip) | 206 KB | **146.2 KB** |
| Page chunks | 1 | **22** |

**Changes:** `React.lazy` + Suspense for teacher/student/admin pages, exam room, lobby, teacher portfolio, and developer page; static landing/auth remain for first paint.

**Residual:** the entry still contains React, Motion, icons and shared UI. Vendor chunking (manualChunks) and icon tree-shaking can reduce it further; the current build warning threshold is no longer exceeded.

---

## 3. Database indexes

Five additive indexes are prepared for the queries used by the fixed features:

- audit by actor/time,
- student attempts by student/exam,
- exam submissions by exam,
- teacher relationship checks by teacher/unlinked,
- teacher exams by teacher/published.

They avoid `nvarchar(max)` columns (production constraint) and were validated on a production-like LocalDB probe. They are not applied to production yet.

**Expected impact:** lower scan cost for teacher student details, audit, results/rosters as data grows. Current production data is small, so gains are not yet material and no claim of measured production speedup is made.

---

## 4. Polling and live session

The teacher live roster still polls `/api/exams/{id}/live` every 3 seconds (~20 req/min per open exam tab). This was not changed because the audit did not find a measured failure and the endpoint is cheap (5 ms local). Recommended future work: 5–10 s interval, ETag/`If-None-Match`, or SSE/WebSocket for larger cohorts.

---

## 5. Lighthouse / accessibility performance

Local fixed build:
- Landing: Accessibility **100**, Best Practices **100**, SEO **100**, 0 failures.
- Login: Accessibility **100**, Best Practices **100**, SEO **100**, 0 failures.

These are correctness/accessibility scores, not a full performance benchmark. The available Lighthouse tool did not produce a performance score.

---

## 6. Remaining measurement work

- Post-deploy Monster login p95 and app-pool cold start.
- Bundle vendor split and long-term caching headers.
- Live polling load with many concurrent teacher tabs.
- Query plans for audit/result exports at scale.
- Memory soak test (countdowns/polling).
