# Performance Report

**Environment:** production Vercel frontend/API and local Development API. Measurements taken 2026-11-05.
**Note:** network conditions were not throttled; numbers are single-run observations unless stated. They are directionally useful but not a substitute for a controlled lab benchmark.

---

## 1. Lighthouse (production)

| Page | Accessibility | Best Practices | SEO | Performance |
|---|---:|---:|---:|---:|
| Landing `/` | **93** | **100** | **80** | not scored by the available tool |
| Login | **96** | **100** | **80** | not scored by the available tool |

Reports:

- `evidence/lighthouse-production-landing.html` / `.json`
- `evidence/lighthouse-production-login.html` / `.json`

Failures:

- **Landing:** color contrast, heading order, logo label mismatch, invalid `robots.txt` directive.
- **Login:** color contrast (4.44:1 for 10.5 px helper text), invalid `robots.txt`.

Lighthouse did not provide a performance score; Core Web Vitals below are measured directly.

---

## 2. Page-load timing (production landing)

Measured in a fresh Playwright navigation at 1366×768:

| Metric | Value |
|---|---:|
| TTFB | **459 ms** |
| DOMContentLoaded | **1,860 ms** |
| Load | **1,860 ms** |
| Transferred JS | **206 KB** |
| Requests (document-level) | 7 |
| Images transferred | 0 KB |

The page is lightweight at the network level (no hero image), but the main JS chunk is large and parse/execute dominates the 1.86 s load.

---

## 3. API latency

| Endpoint | Environment | Measured |
|---|---|---:|
| `GET /api` | local warm | 78 ms / 4 ms / 2 ms (3 runs after warm-up) |
| `POST /api/auth/login` | local | 238 ms |
| `GET /api/exams?pageSize=10` | local | 332 ms |
| `GET /api/exams/{id}/live` | local | 5 ms |
| `POST /api/auth/login` | production, after idle | **9,900 ms** (first), 865 ms, 347 ms |
| `GET /api` | production | 227 ms |

**Finding PERF-01 (Medium):** the first production login after idle consistently pays a **~10-second** cold-start penalty (also observed earlier at 10.3 s). Users experience a spinner and may retry, creating duplicate login attempts. Warm requests are acceptable (347–865 ms).

**Likely contributors:** serverless scale-to-zero plus startup work in `Program.cs` (`EnsureCreatedAsync`, `ExamWorkflowSchema.EnsureAsync`, `PlatformDataUpgrade.EnsureAsync`) and EF model initialization. Verify with platform cold-start logs.

**Recommendations**
1. Keep one instance warm with a health-check ping every 2–4 minutes.
2. Move schema bootstrap/upgrade to a deployment job or a single startup migration, not the request path.
3. Enable provisioned concurrency if the hosting plan supports it; co-locate API and database regions.
4. Add server timing headers and alert on p95 cold-start latency.

---

## 4. Bundle and assets

| Asset | Size |
|---|---:|
| Main JS (`index-*.js`) | **780.1 KB** uncompressed / 206 KB transfer |
| CSS | 91.7 KB |
| Cairo font files | 33.0 + 30.2 + 16.3 KB |
| Readex Pro font files | 30.7 + 27.0 + 22.3 + 11.0 KB |
| Total font payload | ~170.5 KB (all subsets) |

**Finding PERF-02 (Info/Medium):** a single 780 KB entry chunk delays first interaction on mid-range mobile devices. The login page must download the teacher/student/admin code even though it only needs the auth form.

**Recommendations**
- Route-level `React.lazy` + Suspense for teacher, student, and admin workspaces.
- Load `motion` only on pages that animate; split icon imports.
- Preload only the Arabic font subset needed above the fold; `font-display: swap`.
- Set a CI bundle budget (e.g. entry ≤ 350 KB uncompressed) and track with a bundle analyzer.

---

## 5. Network behaviour during live workflows

- Landing: 7 requests, 0 failures, 0 console errors.
- Login: 0 console errors; expected 401 on bad credentials.
- Teacher dashboard: polling `/api/teacher/overview` once per page load.
- Exam detail: **`/api/exams/{id}/live` every 3 seconds** while open — ~20 requests/minute per teacher per exam tab.
- Session countdown re-renders every second (client-only; cheap).
- Student exam room: autosave requests on navigation/change; no runaway loop observed.
- No failed static assets, no CORS errors, no infinite loading states in the tested flows.

**Observation:** live polling is acceptable for small classes but scales linearly with open teacher tabs. Consider 5–10 s intervals, ETag/`If-None-Match`, or a single SSE/WebSocket channel per teacher. Measure before changing; the current endpoint is cheap (5 ms local).

---

## 6. Database and backend notes

- Backend unit/integration suite: **73 passed, 5 skipped, 0 failed** in ~20 s on SQLite; PostgreSQL schema tests were skipped (no isolated PostgreSQL instance).
- The student details endpoint loads all submissions for the selected exams without pagination; with large classes this can grow. Add a projection/aggregate query if classes exceed a few hundred attempts.
- CSV exports load all rows into memory; fine for current scale, but stream for large cohorts.
- `MapGet("/api/exams")` uses `pageSize` clamp (max 100); no unbounded list found except public teachers (max 200) and invitations.

---

## 7. What was not measured

- Lighthouse performance score under mobile throttling (tool unavailable).
- Memory-leak soak test (the app has countdowns/polling; a 1-hour soak is recommended).
- Real-device paint timings (only Chromium desktop emulation).
- Production database query plans and index usage under load.
- Concurrent-user load (no k6/artillery run was performed; doing so against production would be inappropriate without approval).

---

## 8. Priority actions

1. Remove the ~10 s cold-start penalty (PERF-01) — highest user-visible impact.
2. Code-split the 780 KB entry bundle (PERF-02).
3. Reduce live polling or add caching/SSE.
4. Add performance budgets and a synthetic login check to CI/uptime monitoring.
