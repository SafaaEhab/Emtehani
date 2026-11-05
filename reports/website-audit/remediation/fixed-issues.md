# Fixed Issues

Every item below was a confirmed audit finding. No additional bugs were invented.
**Deployment status:** code and tests complete; production deploy is **not executed** and awaits approval.

---

## BUG-01 / API-03 — Attempt summary excludes finished exams

- **Severity:** Medium
- **Root cause:** `GET /api/teacher/students/{id}` selected only exams with `Status != "Finished"`, then loaded submissions only for those exam ids and aggregated them. A completed attempt on a finished exam disappeared.
- **Files:** `backend/Emtehani.API/Program.cs` (student detail endpoint)
- **Fix:** Load all teacher exams for aggregation; keep the manageable-exam list filtered to non-finished; aggregate `attempts`, `attemptsCount`, `submittedAttempts` across all exams; return `finishedExamCount` for clarity.
- **Tests:** `RemediationTests.FinishedExam_AttemptsAreCounted_AuditShowsSubmission_AndExtraAttemptIsBlocked`
- **Result:** **PASS** — live SQL Server run: `used=1 completed=1 remaining=0 finishedExams=1`.
- **Database impact:** none.
- **Deployment:** backend only; compatible with old frontend.
- **Production verification:** read-only query confirmed the relevant statuses/rows; post-deploy UI check pending.

## BUG-02 — Dashboard counts a finished exam as “openable”

- **Severity:** Low
- **Root cause:** UI computed `publishedExams - activeExams`; finished published exams were included.
- **Files:** `backend/Emtehani.API/Program.cs` (`/api/teacher/overview`), `frontend/src/pages/TeacherDashboard.jsx`
- **Fix:** server returns `openableExams` = published and not Finished/Active/Paused; UI prefers it with a safe fallback.
- **Tests:** covered by the finished-exam remediation test (`overview openable=0`); live check.
- **Result:** **PASS**.
- **Database impact:** none.
- **Deployment:** backend + frontend.
- **Production verification:** pending deploy.

## BUG-03 / API-05 — Audit log omits student submissions

- **Severity:** Low
- **Root cause:** audit endpoint filtered `ActorId == teacherId`; `submission.started/submitted` events are written with the student as actor.
- **Files:** `backend/Emtehani.API/Program.cs` (`GET /api/audit`)
- **Fix:** include events where `EntityType == "Submission"` and `EntityId` belongs to one of the teacher's submissions (teacher-scoped; no cross-tenant leakage).
- **Tests:** same remediation test asserts `submission.submitted` is present; live run `hasSubmission=true`.
- **Result:** **PASS**.
- **Database impact:** none; supports the new bounded audit index.
- **Deployment:** backend.

## BUG-04 — Review pages show the generic title “امتحاني”

- **Severity:** Low
- **Root cause:** title maps lacked `review` and `essay-review`; document title was role-only.
- **Files:** `frontend/src/App.jsx`
- **Fix:** added `review: 'مراجعة المحاولة'` and `essay-review: 'تصحيح المحاولة'`; document title now derives from the active page.
- **Tests:** live Playwright checks: Results/Students/Dashboard titles correct; review title code path covered by map.
- **Result:** **PASS**.
- **Deployment:** frontend.

## BUG-05 / UI-04 — Modal focus escapes to background navigation

- **Severity:** Medium (accessibility)
- **Root cause:** `Modal` focused the close button and handled Escape only; no Tab trap, no background inert, no focus restoration.
- **Files:** `frontend/src/components/ui.jsx`, new `frontend/src/lib/useFocusTrap.js`, `frontend/src/components/AppShell.jsx`
- **Fix:** reusable focus trap: Tab/Shift+Tab cycle, Escape close, body scroll lock, focus restoration; applied to Modal and the AppShell mobile drawer; `aria-describedby` added.
- **Tests:** live Playwright 14-Tab test on question modal and 16-Tab test on mobile drawer: all focus stayed inside; Escape restored focus/closed.
- **Result:** **PASS**.
- **Deployment:** frontend.

## BUG-06 / SEC-01 — Rate limit bypass through `X-Forwarded-For`

- **Severity:** High (security)
- **Root cause:** all proxies trusted + limiter keyed on rewritten `RemoteIpAddress`.
- **Files:** `backend/Emtehani.API/Program.cs`, new `backend/Emtehani.API/LoginThrottle.cs`
- **Fix:**
  - `Proxy:TrustForwardedFor` defaults to **false**; `X-Forwarded-For` is ignored unless `Proxy:KnownProxies`/`KnownNetworks` are explicitly configured (startup fails if trust is requested without them).
  - `X-Forwarded-Proto` remains available for HTTPS detection.
  - Added per-account login lockout (default 5 failures / 15 minutes), independent of IP.
  - Added a global API limiter in addition to named policies.
- **Tests:** `ForwardedHeaderRateLimitTests.SpoofedForwardedFor_DoesNotBypassAuthRateLimit`; live 65-request spoof test returned 429 (54×401 + 11×429 after a prior auth window).
- **Result:** **PASS**.
- **Database impact:** none.
- **Deployment:** backend + environment variables (`Proxy__TrustForwardedFor=false`). Configure real proxy IPs only after obtaining them from MonsterASP/IIS logs.
- **Production verification:** Monster CORS/health verified read-only; spoof test cannot be repeated on production without authorization. Local regression is the evidence.

## BUG-07 / SEC-02 — Student PII exposure through phone lookup

- **Severity:** Medium (privacy)
- **Root cause:** lookup returned full name, stage, grade, registration source and id to any confirmed teacher; no endpoint-specific rate limit.
- **Files:** `backend/Emtehani.API/Program.cs`, `frontend/src/pages/TeacherStudentsPage.jsx`
- **Fix:** masked name (`طالب ت…`) and null stage/grade until an accepted relationship exists; full data only for an accepted link; per-teacher lookup limit (12/min); audit event per lookup; UI shows “بيانات مخفية” and explains consent.
- **Tests:** `PhoneLookup_MasksUnrelatedStudent_AndRevealsOnlyAfterAcceptance`, `PhoneLookup_IsRateLimitedPerTeacher`; live SQL Server: masked `true`, stage/grade `null` for unrelated teacher, full for linked teacher.
- **Result:** **PASS**.
- **Deployment:** backend + frontend; no schema change.

## BUG-08 / UI-05 — Login helper text contrast 4.44:1

- **Severity:** Low
- **Root cause:** light-theme `--app-subtle` was `#64748b` on `#f6f7fb`.
- **Files:** `frontend/src/index.css`
- **Fix:** `--app-subtle: #5b6b82` (5.07:1).
- **Tests:** Lighthouse login and landing now **100 accessibility, 0 failures**.
- **Result:** **PASS**.

## BUG-09 / UI-06 / UI-07 — Heading order and logo accessible name

- **Severity:** Low
- **Root cause:** decorative dashboard mock used H3 before the next H2; logo aria-label omitted visible brand text.
- **Files:** `frontend/src/pages/LandingPage.jsx`
- **Fix:** mock marked `aria-hidden="true"`; logo label `"امتحاني EMTEHANI — العودة إلى الأعلى"`.
- **Tests:** Lighthouse landing **100 accessibility, 0 failures**.
- **Result:** **PASS**.

## BUG-10 — Invalid `Agentmap:` robots directive

- **Severity:** Low (SEO)
- **Root cause:** non-standard directive.
- **Files:** `frontend/public/robots.txt`, `frontend/test/agent-readiness.test.js`
- **Fix:** removed `Agentmap:`; unit test asserts it is absent; Sitemap/Content-Signal retained.
- **Tests:** `npm run test:unit` 31 pass; Lighthouse SEO 100.
- **Result:** **PASS**.

## BUG-11 / SEC-03 — CSP / API origin drift

- **Severity:** Medium (deployment risk)
- **Root cause:** production build called `emtehani-api.vercel.app` and served a CSP for that host, while the current Vercel env points to `emtehani.runasp.net`; repo CSP listed Monster.
- **Files:** `frontend/vercel.json` (already Monster), new `frontend/scripts/verify-csp.mjs`, `frontend/package.json`, `frontend/test/agent-readiness.test.js`, `.env.example`, docs.
- **Fix:** authoritative origin is Monster; build now runs a CSP guard that fails if `VITE_API_URL` is not in `connect-src` or if script-src is unsafe/wildcarded.
- **Tests:** guard rejects `https://wrong.example` (exit 1) and accepts Monster (exit 0); production build succeeds with Monster; unit test asserts CSP contents.
- **Result:** **PASS (code)**; production redeploy pending.
- **Deployment:** deploy frontend and backend together. Current production still serves the old Vercel-origin CSP until redeploy.

## BUG-12 / PERF-01 — Production cold start ~10 s

- **Severity:** Medium (performance)
- **Root cause:** the slow path was the Vercel serverless API (`emtehani-api.vercel.app`), not the authoritative Monster/IIS API. Measured Monster: `/health` 139 ms, login 320 ms; Vercel cold login 9.9–10.3 s.
- **Files:** `backend/Emtehani.API/Program.cs` (startup timing + `Database:BootstrapOnStartup` flag), `docs/monsterasp-deployment.md`, `backend/.env.example`.
- **Fix:** point the frontend build at Monster (authoritative), document Monster idle/app-pool settings, log bootstrap duration, allow operators to skip startup bootstrap only after schema is managed. No artificial traffic was added. EF retry strategy was **not** enabled because it breaks existing explicit transactions (caught in live SQL Server testing and removed).
- **Tests:** local fresh DB bootstrap 2.1 s, second start ~1.6 s; live SQL Server full flow; Monster API health/login measured.
- **Result:** **PASS for the authoritative path**; Monster hosting idle settings cannot be changed without control-panel access.
- **Production verification:** Monster health/CORS/DB read-only verified; post-deploy login timing pending.

## BUG-13 / UI-01 — Mobile menu lacks backdrop/full height

- **Severity:** Low
- **Root cause:** panel was a dropdown inside a `backdrop-filter` header; no backdrop; fixed descendants used the header as containing block.
- **Files:** `frontend/src/pages/LandingPage.jsx`
- **Fix:** full-height fixed panel + backdrop, body scroll lock, focus trap, remove header backdrop-filter while open so fixed children use the viewport.
- **Tests:** live 390×844: panel top 72/height 772/width 390; backdrop matches; body overflow hidden; focus stays inside; Escape closes.
- **Result:** **PASS**; screenshot `evidence/mobile-menu-fixed.png`.

## BUG-14 / API-07 — Extra attempt allowed on a finished exam

- **Severity:** Low (business logic)
- **Root cause:** grant endpoint did not check exam status.
- **Files:** `backend/Emtehani.API/Program.cs`
- **Fix:** 409 when `Exam.Status == "Finished"` with a clear message to reopen the session.
- **Tests:** live SQL Server `extraAttempt 409`; remediation test asserts 409.
- **Result:** **PASS**.

## BUG-15 — Refresh/back loses workspace context

- **Severity:** Low (usability)
- **Root cause:** state-only navigation, no URL/state persistence.
- **Files:** `frontend/src/App.jsx`
- **Fix:** hash navigation for top-level pages (`#/results`, `#/students`, …), popstate handling, sessionStorage fallback, role-guarded deep links, logout resets the hash. Non-restorable detail pages keep the previous top-level hash.
- **Tests:** live Playwright: `#/dashboard → #/results → #/students`; Back returns to `#/results`; direct `/#/students` restores page; reload restores results.
- **Result:** **PASS** for top-level pages; detail-page deep links remain a future router improvement (documented in remaining issues).

## BUG-16 — README says password reset is unavailable

- **Severity:** Info (documentation)
- **Files:** `README.md`
- **Fix:** documented forgot/reset flow, one-hour token, invalidation, SMTP requirement.
- **Result:** **PASS (docs)**.

## BUG-17 / PERF-02 — Large single JS bundle

- **Severity:** Info/Medium
- **Root cause:** all pages imported eagerly.
- **Files:** `frontend/src/App.jsx`
- **Fix:** `React.lazy` + Suspense for teacher/student/admin pages, exam room, developer page; static landing/auth remain for first paint.
- **Tests:** build succeeds; entry chunk **476 KB** (146 KB gzip) vs 780 KB before; 22 page chunks emitted.
- **Result:** **PASS**; further vendor splitting is a remaining optimization.

---

## Production verification status per issue

All items are verified locally/in a disposable SQL Server; **no post-deploy production verification has been performed**.

| ID | Production verification |
|---|---|
| BUG-01 | Pre-deploy read-only schema/query validated; post-deploy pending |
| BUG-02 | Post-deploy pending |
| BUG-03 | Pre-deploy read-only audit join validated; post-deploy pending |
| BUG-04 | Post-deploy pending |
| BUG-05 | Local browser verified; post-deploy pending |
| BUG-06 | Local spoof test verified; production spoof test pending authorization |
| BUG-07 | Live disposable SQL Server verified; post-deploy pending |
| BUG-08 | Local Lighthouse 100; post-deploy pending |
| BUG-09 | Local Lighthouse 100; post-deploy pending |
| BUG-10 | Local Lighthouse SEO 100; post-deploy pending |
| BUG-11 | Build guard verified; production deploy pending |
| BUG-12 | Monster API read-only timing verified; post-deploy login timing pending |
| BUG-13 | Local Playwright verified; post-deploy pending |
| BUG-14 | Live disposable SQL Server verified; post-deploy pending |
| BUG-15 | Local Playwright verified; post-deploy pending |
| BUG-16 | Documentation only; no runtime verification needed |
| BUG-17 | Build measured locally; production build/deploy pending |

## Additional audit items

| Item | Status | Notes |
|---|---|---|
| SEC-04 password policy | ✅ | 10–128 + common blocklist; test added |
| SEC-05 localStorage tokens | ⏸ accepted | strict CSP/no HTML sinks; cookie/BFF migration remains a recommendation |
| SEC-06 rate-limit coverage | ✅ | global API limiter + lookup/export/results policies |
| SEC-07 dev reset token | ⏸ | Development-only; document production guard |
| SEC-08 public OpenAPI | ⏸ | intentional discovery metadata |
| API-10 export memory | ⏸ | stream for large cohorts |
| DB indexes | 🟡 | code + SQL prepared; not applied to production |
