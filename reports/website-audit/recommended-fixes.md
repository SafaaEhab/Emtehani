# Recommended Fixes — Prioritised

**Principle:** fix security and data-correctness issues before cosmetic ones. No code was changed during the audit.

---

## P0 — Before scaling teacher onboarding

### 1. Fix the forwarded-header / rate-limit bypass (SEC-01, High)

**Files:** `backend/Emtehani.API/Program.cs` (ForwardedHeadersOptions, rate limiter `ClientKey`), deployment proxy config.

**Do**
- Replace `KnownProxies.Clear()` / `KnownIPNetworks.Clear()` with the actual edge proxy ranges (Vercel egress, MonsterASP/IIS front end, or your load balancer).
- Strip inbound `X-Forwarded-For` at the edge; only trust headers added by the platform.
- Add a per-account login failure counter (e.g. 5 failures → 5-minute backoff) independent of IP.
- Add a global unauthenticated budget so rotating identities cannot open unlimited partitions.

**Verify**
- 65 same-IP attempts → 429 by attempt 61.
- 65 attempts with rotating `X-Forwarded-For` → still 429.
- Add both to CI as an integration test.

### 2. Minimise and throttle the teacher phone lookup (SEC-02, Medium)

**File:** `backend/Emtehani.API/Program.cs` (`/api/teacher/students/lookup`).

**Do**
- Return `{ found: true, maskedName: "سالم أ…", requestStatus }` until an accepted relationship exists.
- Reveal stage/grade only after the student accepts the link.
- Add `RequireRateLimiting("lookup")` with a per-teacher budget (e.g. 10/minute) and audit each lookup.
- Consider requiring the full name + phone, or an invitation token, before returning any data.

**Verify**
- Unrelated teacher sees only a masked name.
- 11th lookup/minute → 429.
- Audit log records lookup events.

---

## P1 — Data correctness and user trust

### 3. Include finished exams in the student attempt summary (BUG-01, Medium)

**File:** `backend/Emtehani.API/Program.cs` student-detail endpoint.

**Do**
- Compute `attempts` from **all** teacher exams for the student, not only `Status != "Finished"`.
- Keep the ban-management list limited to non-finished exams if desired, but label it “الامتحانات القابلة للإدارة”.
- Ensure `attemptsCount`, `submittedAttempts`, `attempts.used/completed/remaining` all count finished exams.

**Verify**
- After a 10/10 graded attempt on a finished exam, the details modal shows Used 1 / Completed 1 / Remaining 0.

### 4. Fix dashboard “openable sessions” counter (BUG-02, Low)

**File:** `frontend/src/pages/TeacherDashboard.jsx` and/or `/api/teacher/overview`.

**Do** — count only exams that can actually be opened (exclude `Finished`). Prefer a server-computed `openableSessions` field so the UI cannot drift.

### 5. Show student submissions in the teacher audit log (BUG-03, Low)

**File:** `backend/Emtehani.API/Program.cs` (`GET /api/audit`).

**Do** — include events where `EntityType == "Submission"` and the submission's exam belongs to the requesting teacher, regardless of `ActorId`. Alternatively write a teacher-scoped event on submit. Update the page copy if the scope stays account-only.

### 6. Block extra attempts on finished exams (BUG-14, Low)

**File:** `POST /api/submissions/{id}/grant-extra-attempt`.

**Do** — return 409 unless the exam can be reopened, or provide an explicit “reopen + grant” flow.

---

## P1 — Accessibility

### 7. Trap focus in modals and restore it on close (BUG-05, Medium)

**File:** `frontend/src/components/ui.jsx` (`Modal`).

**Do**
- On open: save `document.activeElement`, focus the first meaningful element (or the dialog), set `inert` on the app root.
- On Tab/Shift+Tab: cycle within the dialog.
- On close: restore focus to the trigger.
- Link the description with `aria-describedby`.

**Verify** — Tab 20 times stays inside; Shift+Tab wraps; Escape/backdrop close restores focus.

### 8. Fix small-text contrast (BUG-08, Low)

**File:** `frontend/src/index.css`.

**Do** — darken light-theme `--app-subtle` from `#64748b` to about `#5b6b82`; keep 10.5 px text only for non-essential labels.

### 9. Fix heading order and logo label (BUG-09, Low)

**Files:** `LandingPage.jsx` header/mock.

**Do** — use `h2` for the mock's section heading (or `aria-hidden` if decorative); change the logo button label to include “امتحاني EMTEHANI”.

### 10. Fix the mobile menu overlay (BUG-13, Low)

**Do** — full-height sheet, dim backdrop, `overflow:hidden` on `body`, focus trap, and close on navigation.

### 11. Set the theme before first paint (UI-08, Low)

**Do** — add a tiny inline script in `index.html` that reads `emtehani-theme`/`prefers-color-scheme` and sets `data-theme` before CSS paints; exclude color transitions on initial mount. This removes the transient 1.16:1 contrast Lighthouse captured.

---

## P1 — Performance

### 12. Remove the ~10 s production cold start (BUG-12, Medium)

**Do**
- Keep the API warm with a scheduled ping (2–4 min) or provisioned concurrency.
- Move `EnsureCreatedAsync` + `ExamWorkflowSchema.EnsureAsync` + `PlatformDataUpgrade.EnsureAsync` to a deploy-time job (or run once with a distributed lock and cache the result).
- Co-locate API and database regions; enable server timing headers.
- Alert when first-request latency exceeds 2 s.

### 13. Code-split the 780 KB entry bundle (BUG-17, Info/Medium)

**Do**
- `React.lazy` the teacher, student, and admin workspaces.
- Lazy-load `motion`/icon-heavy pages.
- Preload only the required Arabic font subset.
- Add a CI bundle budget (e.g. entry ≤ 350 KB uncompressed).

### 14. Tune live polling (Info)

**Do** — move `/live` from 3 s to 5–10 s, add ETag/`If-None-Match` or SSE for larger classes. Measure before/after.

---

## P2 — Configuration, docs, architecture

### 15. Synchronise CSP with the deployed API origin (SEC-03, Medium)

**Files:** `frontend/vercel.json`, deployment env.

**Do** — make the CSP connect-src derive from `VITE_API_URL`; commit the correct host; add a deploy smoke test that loads the app and asserts a successful `/api` call under the CSP.

### 16. Update README for password reset (BUG-16, Info)

Document the implemented forgot/reset flow and SMTP requirement; remove “no automatic password reset”.

### 17. Remove the invalid `Agentmap:` robots directive (BUG-10, Low)

Keep `Sitemap:`; move agent-discovery references to a comment or a standard mechanism.

### 18. Adopt URL-based workspace routing (BUG-15, Low)

Use React Router with role guards and paths such as `/teacher/exams/:id`, `/student/results/:id`. This fixes refresh/back/deep links and enables sharing.

### 19. Add focused regression tests

- **Security:** rotating `X-Forwarded-For` still 429; PII masking; CSP/API origin assertion.
- **Data:** attempt summary after a finished exam; dashboard `openableSessions`; audit event visibility.
- **UI:** modal focus trap; nested page titles; mobile menu scroll lock.
- **Performance:** synthetic login p95 and bundle budget.

---

## Suggested release plan

| Release | Contents | Exit criteria |
|---|---|---|
| **Hotfix** | SEC-01, SEC-02, SEC-03 | Rate-limit bypass closed; lookup masked/throttled; CSP deploy test green |
| **1.1** | BUG-01, BUG-02, BUG-03, BUG-14, BUG-05 | Attempt numbers correct; audit complete; keyboard modal test green |
| **1.2** | BUG-08/09/10/13, UI-08, BUG-04 | Lighthouse accessibility ≥ 95 on landing/login; no focus escapes |
| **1.3** | BUG-12, BUG-17, polling | Login p95 < 2 s including cold start; entry bundle ≤ 350 KB |
| **Backlog** | BUG-15 router, docs, admin test coverage | Deep links work; README accurate; admin E2E in CI |

---

## Effort estimate (rough)

| Item | Effort |
|---|---|
| Trusted proxies + per-account lockout | 0.5–1 day |
| Lookup minimisation + rate limit | 0.5 day |
| Attempt summary + dashboard/audit fixes | 1–2 days |
| Modal focus trap + a11y fixes | 1 day |
| Cold-start mitigation | 0.5–2 days (platform dependent) |
| Code splitting | 1–2 days |
| Router migration | 3–5 days |
| Regression tests | 1–2 days |
