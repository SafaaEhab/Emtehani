# Remaining Issues & Accepted Decisions

These are the items that remain open, blocked, or deliberately accepted after the remediation. No confirmed remediation item is silently omitted.

---

## Blocked by external access/approval

### 1. Production deployment — **BLOCKED**
- No explicit deployment approval.
- No MonsterASP FTP/WebDeploy/control-panel credentials in this environment.
- The backend publish artifact and deployment steps are ready; the actual upload is not.
- Frontend Vercel CLI is authenticated but **no deploy command was executed**.
- **Action required:** provide Monster deployment access and approve the change window.

### 2. Production post-deploy verification — **PENDING**
- All production checks in `production-verification.md` after the deploy are pending.
- Only read-only pre-deploy checks were performed.
- Do not treat the local/LocalDB results as production verification.

### 3. Admin UI/roles — **BLOCKED**
- No admin account or bootstrap credentials.
- Admin APIs correctly returned 403 for teacher/student in the audit.
- **Action required:** create an admin via the documented `Admin__BootstrapEmail`/`Admin__BootstrapPassword` flow in a controlled environment, then run role/settings tests.

### 4. Full Playwright E2E suite — **BLOCKED**
- Requires isolated PostgreSQL 17 (`E2E_DATABASE_CONNECTION`) and ports 5099/4173.
- No PostgreSQL instance was available.
- Backend integration tests and manual Playwright browser checks were used instead.
- **Action required:** provide an isolated PostgreSQL 17 database; run `npm run test:e2e`.

---

## Deployment / operations follow-ups

### 5. Production database indexes not applied — **PENDING APPROVAL**
- The five indexes are in `ExamWorkflowSchema` and in `docs/sql/2026-11-remediation-indexes.sql`.
- They are validated on disposable SQL Server databases but not applied to `db73841`.
- Startup will apply them on the next approved backend deploy.
- Pre-deploy backup/restore verification is still required.

### 6. Real Monster proxy IPs unknown
- `X-Forwarded-For` is now ignored by default.
- Per-account login lockout protects credentials.
- If Monster uses ARR/IIS as a reverse proxy, all requests may share a proxy `RemoteIpAddress`, making IP limits broader than intended.
- **Action required:** obtain verified proxy IPs/CIDRs from MonsterASP/IIS logs and set `Proxy__KnownProxies`/`Proxy__KnownNetworks` with `Proxy__TrustForwardedFor=true`.
- Do not guess these ranges.

### 7. Per-account lockout is process-local
- `LoginThrottle` uses in-memory state. Monster currently appears to be a single IIS deployment; if scaled out, lockout must move to a distributed cache (Redis/SQL) or sticky sessions.
- Rate limits are also per process.

### 8. Legacy Vercel API still live
- `emtehani-api.vercel.app` remains deployed and shares the Monster database.
- It is not authoritative and has a different cold-start profile.
- **Action required:** decommission it, or deploy the same commit and environment contract; never create a second set of business logic there.

---

## Accepted technical debt / recommendations

### 9. Tokens in localStorage (SEC-05)
- Access and refresh tokens are in `localStorage`.
- Mitigated by strict CSP, no unsafe inline/eval, no HTML injection sinks, and refresh rotation.
- **Recommended:** HttpOnly refresh cookie/BFF and in-memory access token. This is a cross-cutting architecture change, deliberately not bundled into this remediation.

### 10. Public OpenAPI description (SEC-08)
- `/swagger/v1/swagger.json` is public and advertised in the Link header.
- It is intentional discovery metadata and contains no secrets.
- Change only if the product policy requires private API docs.

### 11. Audit query scalability
- `GET /api/audit` currently materialises the teacher's submission IDs to match `AuditEvents.EntityId` (nvarchar(max)).
- Fine at 118 events/12 submissions; could be expensive for very large cohorts.
- A future approved schema migration could introduce a bounded `SubmissionId` column or a teacher-scoped audit column/index.

### 12. Export memory usage
- CSV exports load all matching rows into memory.
- Fine at current scale; stream for large cohorts.

### 13. Bundle still 476.8 KB uncompressed / 146.2 KB gzip
- Reduced from 780.1 KB / 206 KB, but the entry still contains React/Motion/icons/shared UI.
- Further vendor chunking/tree-shaking is optional; measure first.

### 14. Detail-page deep links
- Top-level pages now have hash URLs and Back/Forward.
- Detail pages (`exam-detail`, `review`, `essay-review`, `create`, `take`, `lobby`) require live state and are not deep-linkable.
- A full React Router migration remains a future enhancement.

### 15. Legacy short passwords
- New and reset passwords require 10–128 chars + common-password rejection.
- Existing accounts with 8–9 character passwords remain able to log in.
- If policy requires, run an approved forced-reset campaign; do not lock real users without notice.

### 16. Minor UI polish
- Modal close control is 32 px, below the 44 px touch-target recommendation.
- Some landing mock labels are 8–10.5 px; decorative/low-priority.
- Polling remains 3 s on teacher live view; consider 5–10 s/ETag/SSE for large classes.

### 17. No load/soak testing
- No k6/artillery/concurrent-user run; not appropriate against production without approval.
- No memory-leak soak for timers/polling.
- **Recommended:** staging load test with disposable data before scaling.

### 18. Hosting cold-start settings
- Monster app-pool idle/always-running settings are outside the repo.
- If the site unloads after idle, configure the app pool in the MonsterASP panel.
- Frontend now uses Monster, so the legacy Vercel 10-second cold-start path is no longer in the critical path after deployment.

---

## Verification ledger

| Item | State |
|---|---|
| Code fixes | Complete |
| Backend regression | 79 pass / 5 skip / 0 fail |
| Frontend unit | 31 pass / 0 fail |
| Frontend build + CSP guard | Pass |
| SQL Server fresh-DB flow | Pass (disposable) |
| Accessibility/SEO Lighthouse | 100 / 100 / 100 local |
| Production deployment | **Not performed** |
| Production post-deploy checks | **Pending** |
| Production DB indexes | **Not applied** |
| Admin UI | **Blocked** |
| Full Playwright E2E | **Blocked** |
