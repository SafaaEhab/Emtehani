# Production Verification

**Status: NOT DEPLOYED. Post-deployment verification has not been performed.**

This document distinguishes verified read-only production facts from pending post-deploy checks. No claim is made that the remediation is live.

---

## 1. Production facts verified read-only

| Check | Result |
|---|---|
| Monster API `GET /health` | **200 Healthy** (139 ms measured) |
| Monster API `GET /api` | **200** `{"service":"Emtehani API","version":"1.0"}` |
| Monster API login timing (unknown account, expected 401) | **320 ms** |
| Monster CORS preflight from `https://emtehani.vercel.app` | **204**, `Access-Control-Allow-Origin` present |
| Monster CORS simple GET | **200**, allowed origin present |
| Monster API server | Microsoft-IIS/10.0, ASP.NET |
| SQL Server connectivity | **PASS** (SQL Server 2025 Express, db73841) |
| SQL Server schema/row counts | Read-only; see `database-changes.md` |
| Shared database | Monster API and legacy Vercel API returned identical 6 public teachers and matched DB query |
| EF migration history | Present (3 migrations) |
| Production `Status`/`EntityType`/`EntityId` types | `nvarchar(max)`; index design adjusted |
| Read-only audit submission join | **PASS** (returns submission events) |
| Read-only attempt aggregation shape | **PASS** |
| Production frontend current JS | Calls `emtehani-api.vercel.app` |
| Production frontend current CSP | `connect-src https://emtehani-api.vercel.app` |
| Vercel production env `VITE_API_URL` | `https://emtehani.runasp.net` |
| Production frontend/Monster mismatch | **Confirmed**; fixed in the next approved build/deploy |

Evidence: `evidence/production-discovery.txt`.

---

## 2. Post-deployment verification (pending approval)

### Availability
- [ ] Vercel production returns 200; no deployment error.
- [ ] Monster `/health` returns 200 Healthy after app-pool recycle.
- [ ] `/api` returns 200.
- [ ] Monster SQL connection works from the hosted site (not only from the workstation).

### Integration
- [ ] Landing and login JS call **only** `https://emtehani.runasp.net`.
- [ ] Served CSP `connect-src` includes Monster and not the legacy Vercel API.
- [ ] No CORS errors in browser console.
- [ ] No blocked requests in DevTools Network.

### Functional (disposable accounts)
- [ ] Teacher registration/login.
- [ ] Free plan confirmation.
- [ ] Exam create/publish/open.
- [ ] Student registration/link acceptance.
- [ ] Student exam start/autosave/submit.
- [ ] Teacher student detail: Used 1 / Completed 1 / Remaining correct.
- [ ] Audit log shows student submission.
- [ ] Essay grading + result publication.
- [ ] CSV export.
- [ ] Phone lookup masked for unrelated teacher; full for linked.
- [ ] Extra attempt on finished exam → 409.
- [ ] Rate limit: 61 same-client login attempts → 429; spoofed XFF also 429.
- [ ] Session refresh/expiry behaves.

### Roles
- [ ] Student cannot open teacher/admin endpoints.
- [ ] Teacher cannot open admin endpoints.
- [ ] Cross-teacher exam/submission access blocked.
- [ ] Admin login/dashboard (requires an approved admin bootstrap/account).

### Browser quality
- [ ] No console errors.
- [ ] No failed network requests except expected auth/conflict responses.
- [ ] Mobile 390 / tablet 768 / laptop 1366 / desktop 1920 layouts.
- [ ] Lighthouse accessibility ≥ 95 on landing/login.
- [ ] Theme persists without flash.

### Logs / data
- [ ] Monster error log: no unhandled 5xx from the smoke flow.
- [ ] Startup log: database bootstrap completed; five indexes present.
- [ ] Row counts: only disposable test rows added; no real data altered.
- [ ] Backups retained.

---

## 3. Exact blockers preventing production verification now

1. **No deployment approval** — required by the mission and change-control rules.
2. **No MonsterASP deployment credentials** — no FTP/WebDeploy/control-panel secret is present in the workspace or environment. Publish output exists, but the upload cannot be executed here.
3. **No admin credentials** — admin UI/role production verification cannot be performed.
4. **No PostgreSQL 17 E2E environment** — full existing Playwright suite cannot run.
5. **No production write authorization** — only read-only DB/API probes were performed.

Do not mark any post-deploy item as passed until the corresponding tool output exists.
