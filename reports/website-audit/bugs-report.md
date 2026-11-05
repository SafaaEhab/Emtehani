# Bugs Report

**Environment:** production for public pages/security/perf; local Development stack for disposable full workflows.
**Legend:** Critical · High · Medium · Low · Info.
**All bugs below were reproduced; none are speculative.**

---

## BUG-01 — Student attempt summary excludes finished exams and reports 0 attempts after a completed exam

- **Severity:** Medium
- **Affected URL:** Teacher → Students → student details (`/api/teacher/students/{studentId}`)
- **Component:** `TeacherStudentsPage.jsx` + `Program.cs` student detail endpoint
- **Preconditions:** Teacher has one linked student who has completed and been graded in a finished exam.
- **Steps:**
  1. Student submits an exam and teacher grades it (submission status `Reviewed`, score 10/10).
  2. Teacher opens **إدارة الطلاب** → **التفاصيل** for that student.
  3. Read the **المحاولات** card and exam list.
- **Expected:** Used = 1, Completed = 1, Remaining = 0 for a one-attempt exam; the exam appears in the student's history.
- **Actual:** **المسموح 1 / المستخدم 0 / المكتمل 0 / المتبقي 1**, “0 متاحة الآن · 0 ممنوع منها”, and “0 امتحان — لا توجد امتحانات غير منتهية لعرضها”.
- **API evidence:** `attemptsCount: 0`, `attempts: { used: 0, completed: 0, remaining: 1 }`, `submittedAttempts: 0`.
- **Evidence files:** `evidence/functional-evidence.json`; DOM text captured during the live run.
- **Root cause:** The endpoint first filters exams with `x.Status != "Finished"`, then loads submissions only for those exam ids and aggregates them. Attempts on finished exams are dropped from both the detail list and the summary.
- **Recommended fix:** Aggregate attempts across all teacher exams for the student, independent of the "manageable exams" list. If the exam list must remain limited to unfinished exams, compute `attempts` from all submissions and label the list accordingly.

---

## BUG-02 — Dashboard says a session can be opened even when the only exam is finished

- **Severity:** Low
- **Affected URL:** Teacher dashboard
- **Component:** `TeacherDashboard.jsx`
- **Preconditions:** Teacher has 1 published exam whose status is `Finished`; none are active.
- **Steps:** Open the teacher dashboard → read **ملخص سريع**.
- **Expected:** “جلسات يمكن فتحها 0”.
- **Actual:** “جلسات يمكن فتحها 1”, while the exam row says **منتهي** and cannot be opened.
- **Evidence:** `evidence/desktop-1920-teacher-dashboard.png` (stat card 1; latest exam منتهي).
- **Root cause:** `Math.max(0, publishedExams - activeExams)` counts finished published exams as openable.
- **Recommended fix:** Count only exams with status `Draft`/`Scheduled`/`Waiting` (or whatever statuses are legitimately openable), never `Finished`. Prefer computing this server-side in `/api/teacher/overview`.

---

## BUG-03 — Audit log says it includes submissions, but student submission events are missing

- **Severity:** Low
- **Affected URL:** Teacher → سجل النشاط
- **Component:** `AuditPage.jsx` + `GET /api/audit`
- **Preconditions:** A student has started and submitted an exam.
- **Steps:** Complete a student submission → open the teacher audit log.
- **Expected:** A `submission.submitted` event in the timeline and “تسليمات 1”.
- **Actual:** “تسليمات 0”; no submission event exists. Only teacher-originated events (created, published, opened, closed, essay graded, extra attempt) are shown.
- **Evidence:** Audit page DOM text and API audit response (9 events, no submission events); see `evidence/functional-evidence.json`.
- **Root cause:** Audit rows for `submission.started/submitted` are written with the **student** as `ActorId`, while the audit endpoint filters `ActorId == teacherId`.
- **Recommended fix:** For teacher audit, include events whose `EntityType == Submission` and whose submission/exam belongs to the teacher, or write a teacher-scoped audit event when a student submits. Clarify the page copy if the log is intentionally account-only.

---

## BUG-04 — Review pages show the generic header title “امتحاني”

- **Severity:** Low
- **Affected URL:** Student review; teacher essay review
- **Component:** `App.jsx` title maps
- **Steps:** Open a student result → **مراجعة**; or teacher → **مراجعة المقالات** → **تصحيح**.
- **Expected:** “مراجعة المحاولة” / “تصحيح المحاولة”.
- **Actual:** The page H1 and document title fall back to “امتحاني” (the eyebrow still says النتائج / مراجعة يدوية).
- **Evidence:** DOM text captured in the live run; screenshot of the student review flow.
- **Root cause:** `studentTitles` has no `review` key and `teacherTitles` has no `essay-review` key, so `App.jsx` uses `'امتحاني'`.
- **Recommended fix:** Add `review: 'مراجعة المحاولة'` and `essay-review: 'تصحيح المحاولة'` to the title maps; update `document.title` per page, not only per role.

---

## BUG-05 — Modal focus escapes to the page behind it

- **Severity:** Medium (accessibility)
- **Affected URL:** Any modal, e.g. Create exam → **إضافة سؤال جديد**
- **Component:** `Modal` in `src/components/ui.jsx`
- **Steps:**
  1. Open the question-type modal.
  2. Press **Tab** repeatedly.
- **Expected:** Focus is trapped inside the dialog until it closes.
- **Actual:** After 6 Tab presses focus lands on background bottom navigation (“المركز”, “اختبارات”), which remains keyboard-operable while the modal is open.
- **Evidence:** Tab sequence captured live: 5 dialog buttons → `المركز` (inside=false) → `اختبارات` (inside=false).
- **Root cause:** The modal only focuses the close button and handles Escape; it does not trap Tab/Shift+Tab, does not set `inert`/`aria-hidden` on the background, and does not restore focus on close.
- **Recommended fix:** Implement a focus loop (first/last focusable), set `inert` on the app root while open, restore focus to the trigger on close, and add `aria-describedby` for the description.

---

## BUG-06 — Login rate limiting can be bypassed by rotating `X-Forwarded-For`

- **Severity:** High (security)
- **Affected URL:** `POST /api/auth/login` (and all IP-keyed limits)
- **Component:** `Program.cs` forwarded-header + rate-limiter configuration
- **Preconditions:** API reachable directly or through a proxy that preserves/appends attacker-controlled `X-Forwarded-For`.
- **Steps:**
  1. Send 61 login attempts from one connection without `X-Forwarded-For` → attempt 61 returns **429** (`Retry-After: 60`).
  2. Repeat 65 attempts, each with a different `X-Forwarded-For` value.
- **Expected:** 429 once the per-IP allowance is exhausted.
- **Actual:** **65 × 401, 0 × 429** — the limiter creates a fresh partition per spoofed IP.
- **Evidence:** `evidence/security-evidence.json` → `rate_limiting`.
- **Root cause:** `ForwardedHeadersOptions.KnownProxies.Clear()` and `KnownIPNetworks.Clear()` trust every proxy; the rate-limit key uses `context.Connection.RemoteIpAddress`, which is rewritten from the untrusted header.
- **Recommended fix:** Trust only known proxy ranges (Vercel egress / platform load balancer), strip inbound `X-Forwarded-For` at the edge, and key authentication limits by a value the client cannot choose (e.g. account e-mail + trusted edge IP, plus a global per-account limit). Add a lockout/backoff after repeated failures.

---

## BUG-07 — Any confirmed teacher can look up a student's full PII by phone, with no endpoint rate limit

- **Severity:** Medium (privacy / enumeration)
- **Affected URL:** `GET /api/teacher/students/lookup?phone=...`
- **Component:** `Program.cs` teacher lookup endpoint
- **Preconditions:** A confirmed teacher subscription (Free plan is enough).
- **Steps:** As an unrelated teacher, request a student's exact phone number.
- **Expected (privacy-minimal):** A yes/no result or masked name, with strict per-teacher throttling.
- **Actual:** Returns full name, `studentId`, stage, grade, registration source and request status. 65 rotating-IP requests showed the auth limiter does not protect this endpoint (no endpoint policy).
- **Evidence:** `evidence/security-evidence.json` → `pii_exposure`.
- **Root cause:** Lookup is intentionally available for onboarding but returns more data than needed and has no endpoint-specific rate limit.
- **Recommended fix:** Return only a masked name (e.g. “سالم أ…”) plus existence, require an exact full phone + one of parent/student phone match, add `RequireRateLimiting("lookup")` (e.g. 10/min per teacher), audit lookups, and notify/require student consent before revealing academic data.

---

## BUG-08 — Login helper text fails WCAG AA contrast (4.44:1)

- **Severity:** Low (accessibility)
- **Affected URL:** Production login page
- **Component:** Auth footer / helper text using `text-subtle` (`#64748b`) on `#f6f7fb`
- **Steps:** Run Lighthouse on the login page.
- **Expected:** ≥ 4.5:1 for 10.5 px normal text.
- **Actual:** **4.44:1** (Lighthouse color-contrast failure).
- **Evidence:** `evidence/lighthouse-production-login.html` / `.json`.
- **Root cause:** `--app-subtle` is slightly too light for the canvas at small sizes.
- **Recommended fix:** Darken light-theme `--app-subtle` to about `#5b6b82` (≈ 4.9:1) or raise the text to ≥ 12 px/medium weight.

---

## BUG-09 — Landing heading order and logo accessible name

- **Severity:** Low (accessibility)
- **Affected URL:** Production landing
- **Component:** Landing hero/dashboard mock + header logo button
- **Steps:** Run Lighthouse; inspect heading outline and the logo button.
- **Expected:** Sequential headings (H1 → H2 → H3) and an accessible name matching the visible logo text.
- **Actual:**
  - Heading order jumps **H1 → H3** in the dashboard mock.
  - Logo button has `aria-label="العودة إلى الأعلى"` while its visible text is “امتحاني EMTEHANI”; Lighthouse reports `label-content-name-mismatch`.
- **Evidence:** `evidence/lighthouse-production-landing.html` / `.json`.
- **Recommended fix:** Promote the mock’s section heading to H2 (or mark the mock `aria-hidden` if decorative); change the logo label to include the visible text, e.g. `aria-label="امتحاني — العودة إلى الأعلى"`.

---

## BUG-10 — robots.txt contains an unknown `Agentmap:` directive

- **Severity:** Low (SEO)
- **Affected URL:** `https://emtehani.vercel.app/robots.txt` line 76
- **Component:** `frontend/public/robots.txt`
- **Steps:** Run Lighthouse SEO audit.
- **Expected:** Valid robots.txt.
- **Actual:** Lighthouse reports **“Unknown directive”** for `Agentmap:`; SEO score 80.
- **Evidence:** `evidence/lighthouse-production-landing.json` → `robots-txt` details.
- **Recommended fix:** Remove `Agentmap:` or move the agent-map reference into a comment; keep `Sitemap:`. `Content-Signal` is also non-standard for Lighthouse but was not the flagged line.

---

## BUG-11 — CSP configuration drift between the repository and production

- **Severity:** Medium (deployment risk)
- **Affected URL:** `frontend/vercel.json` vs production headers
- **Component:** CSP `connect-src`
- **Steps:** Compare `frontend/vercel.json` with the headers served by production.
- **Expected:** The committed CSP allows the API actually used by the build (`https://emtehani-api.vercel.app`).
- **Actual:** Repository CSP allows only `https://emtehani.runasp.net`; production serves `connect-src 'self' https://emtehani-api.vercel.app`. A redeploy from this working tree would block all frontend API calls unless the dashboard override remains in place.
- **Evidence:** `curl` of production headers; `git diff frontend/vercel.json`; `evidence/security-evidence.json` → `configuration_drift`.
- **Root cause:** API hostname changed from `emtehani.runasp.net` to `emtehani-api.vercel.app` without synchronising the repo CSP.
- **Recommended fix:** Generate the CSP from the deployment environment (or commit the correct host), remove reliance on undocumented dashboard overrides, and add a smoke test that asserts `connect-src` contains the built API origin.

---

## BUG-12 — Production login pays a ~10-second serverless cold-start penalty

- **Severity:** Medium (performance)
- **Affected URL:** `POST https://emtehani-api.vercel.app/api/auth/login`
- **Component:** Vercel serverless API startup
- **Steps:** Wait for the API to go idle, then submit a login request.
- **Expected:** Sub-second response.
- **Actual:** **9.9–10.3 s** for the first request; 865 ms and 347 ms on immediate repeats. `/api` root 227 ms.
- **Evidence:** `evidence/performance-evidence.json`.
- **Root cause:** Serverless scale-to-zero plus startup DB bootstrap work (`EnsureCreated`, workflow/data upgrades) before accepting traffic.
- **Recommended fix:** Keep the function warm (scheduled ping every few minutes), provisioned concurrency if the platform supports it, move bootstrap/migrations out of the request path, and consider a persistent host/region closer to users.

---

## BUG-13 — Mobile menu does not dim the page and does not cover the viewport

- **Severity:** Low (visual/UX)
- **Affected URL:** Production landing, 390 px
- **Component:** Landing mobile navigation
- **Steps:** At 390×844, tap the hamburger.
- **Expected:** A full-height sheet with a dimmed backdrop and locked background scroll.
- **Actual:** The panel ends after the links/CTAs; the hero remains visible and appears cut mid-sentence underneath, with no backdrop.
- **Evidence:** `evidence/mobile-390-landing-menu.png`.
- **Recommended fix:** Use a full-height overlay with backdrop and `overflow:hidden` on `body` while open; animate the sheet, not the page beneath.

---

## BUG-14 — “Grant extra attempt” is allowed on a finished exam

- **Severity:** Low (business logic)
- **Affected URL:** Teacher → Results → **محاولة إضافية**
- **Component:** Results page + `POST /api/submissions/{id}/grant-extra-attempt`
- **Steps:** Finish an exam, then grant an extra attempt from the results page.
- **Expected:** Blocked, or clearly explained that the exam must be reopened first.
- **Actual:** Confirmation succeeds (“تم منح الطالب محاولة إضافية…”) even though the exam is finished and the student cannot use it.
- **Evidence:** Live confirmation + success message; API returned 200.
- **Recommended fix:** Reject the action when the exam is `Finished`, or offer “reopen session and grant” as one explicit workflow.

---

## BUG-15 — Workspace navigation is not reflected in the URL; refresh/back loses context

- **Severity:** Low (usability)
- **Affected URL:** All dashboard pages
- **Component:** `App.jsx` state navigation
- **Steps:** Navigate to Results or Exam detail, then refresh or press browser Back.
- **Expected:** The same view is restored (or a meaningful URL).
- **Actual:** `/` is the only URL; refresh always reopens the dashboard, and Back cannot walk through app sections. Deep links such as `/dashboard` return 404.
- **Evidence:** `App.jsx` (`page` state + `setPage`), 404 checks for `/dashboard` and `/admin`.
- **Recommended fix:** Adopt a router (React Router) with role-guarded routes and URL state (`/teacher/exams/:id`, `/student/results/:id`), preserving the current design.

---

## BUG-16 — README claims password reset is unavailable, but it is implemented

- **Severity:** Info (documentation)
- **Affected URL:** `README.md` → “ملاحظات معروفة”
- **Actual:** README says “لا توجد استعادة كلمة مرور تلقائية؛ تُدار الحسابات من الإدارة حالياً”, while the login page, forgot-password endpoint, reset-password endpoint, e-mail template, and token invalidation are all implemented and were verified.
- **Recommended fix:** Update the README and operator runbook; document SMTP requirements for reset e-mails.

---

## BUG-17 — Large single JS bundle

- **Severity:** Info (performance)
- **Measured:** 780.1 KB uncompressed JS, 206 KB transferred; one entry chunk.
- **Impact:** Slower first load on mobile networks; most of the bundle is needed on every page.
- **Recommended fix:** Route-level `React.lazy`/dynamic imports for teacher/student/admin areas, split `motion` and icon usage, and set a bundle budget in CI.

---

## Summary

| ID | Severity | Area | Status |
|---|---|---|---|
| BUG-01 | Medium | Teacher student details | Confirmed |
| BUG-02 | Low | Teacher dashboard | Confirmed |
| BUG-03 | Low | Audit log | Confirmed |
| BUG-04 | Low | Page titles | Confirmed |
| BUG-05 | Medium | Accessibility / modals | Confirmed |
| BUG-06 | High | Security / rate limiting | Confirmed |
| BUG-07 | Medium | Security / privacy | Confirmed |
| BUG-08 | Low | Accessibility / contrast | Confirmed |
| BUG-09 | Low | Accessibility / semantics | Confirmed |
| BUG-10 | Low | SEO | Confirmed |
| BUG-11 | Medium | Deployment configuration | Confirmed |
| BUG-12 | Medium | Performance | Measured |
| BUG-13 | Low | Mobile UI | Confirmed |
| BUG-14 | Low | Business logic | Confirmed |
| BUG-15 | Low | Navigation UX | Confirmed |
| BUG-16 | Info | Documentation | Confirmed |
| BUG-17 | Info | Performance | Measured |
