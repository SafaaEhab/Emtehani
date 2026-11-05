# Executive Summary — Emtehani Website Audit

**Audit date:** 2026-11-05
**Auditor role:** Senior QA / Security / Web Application Audit
**Application:** امتحاني (Emtehani) — ASP.NET Core 10 API + React 19 / Vite frontend
**Production URLs tested:** `https://emtehani.vercel.app/` and `https://emtehani-api.vercel.app`
**Local test environment:** frontend `http://localhost:5173`, API `http://127.0.0.1:5000` (Development, disposable data)

---

## Overall assessment

**Health score: 78 / 100 — Good, with several material defects.**

The core product works. A complete real-world lifecycle was executed successfully in a live browser and against the live API:

1. Teacher registration → e-mail auto-confirmed in Development → Free plan confirmed.
2. Exam created (2 questions, 10 points, 45 minutes), published, and session opened.
3. Student registered, teacher accepted the link request, student saw the exam, entered the lobby, started the exam.
4. MCQ and essay answers saved automatically; final submission completed through the review modal.
5. Teacher graded the essay (5/5 + feedback), closed the session, results published automatically.
6. Student saw **10/10**, correct answers, and the teacher's note.

Authentication, authorization, tenant isolation, IDOR protection, JWT validation, refresh-token rotation, CORS, and security headers are all in good shape. The main risks are a **rate-limit bypass through spoofed `X-Forwarded-For`**, **teacher-side student PII lookup with no endpoint rate limit**, a **misleading attempt summary** that excludes finished exams, and a **~10-second production API cold start**.

---

## Key findings

| Severity | Count | Notable items |
|---|---:|---|
| Critical | 0 | — |
| High | 1 | Login/register/forgot-password rate limit bypassed by rotating `X-Forwarded-For` (65 requests, no 429) |
| Medium | 5 | PII lookup by phone exposed to any confirmed teacher; attempt summary shows 0 attempts after a completed exam; CSP configuration drift between repo and production; ~10 s production login cold start |
| Low | 11 | Dashboard "openable sessions" counter; audit log omits student submissions; generic page titles on review pages; modal focus escapes; contrast 4.44:1; heading order; logo accessible-name mismatch; invalid `Agentmap` robots directive |
| Info | 5 | Weak password policy (length only), tokens in localStorage, 18/76 endpoints explicitly rate-limited, 780 KB JS bundle, README/feature drift |

### What is working well

- **Exam workflow:** create → publish → open → join → autosave → submit → grade → publish is coherent and survived live use.
- **Authorization:** students cannot create exams or read teacher results; other teachers receive 403/404; cross-student submission access is blocked.
- **Session security:** access tokens are validated correctly, tampered JWTs return 401, refresh tokens rotate and are revoked on logout, password reset invalidates the old password and refresh tokens.
- **Headers and CORS:** strong CSP (production), HSTS, `nosniff`, `DENY`, `no-referrer`, `Permissions-Policy`, and explicit origin allow-list.
- **CSV export:** UTF-8 BOM for Excel and formula-injection escaping (`= + - @`) are both implemented.
- **Responsive layout:** no horizontal overflow at 390, 768, 1366, or 1920 px.

---

## Metrics

| Metric | Result |
|---|---:|
| Pages/views discovered | 29 |
| Pages reached and exercised | 27 (2 admin views blocked — no admin account) |
| Documented test checks | 100 |
| Checks executed | 90 |
| Passed | 79 |
| Failed | 11 |
| Blocked | 2 |
| Not run (out of safe/live scope) | 8 |
| Critical / High / Medium / Low findings | 0 / 1 / 5 / 11 |
| Backend unit + integration tests | **73 passed, 5 skipped, 0 failed** (`dotnet test`, 20 s) |
| Production Lighthouse accessibility | 93 (landing) / 96 (login) |
| Production Lighthouse best practices | 100 / 100 |
| Production Lighthouse SEO | 80 / 80 |
| Production landing TTFB / load | 459 ms / 1,860 ms |
| Production login API cold / warm | **9,900 ms** / 865 ms / 347 ms |
| JS bundle (uncompressed / transfer) | 780 KB / 206 KB |

> Counts above refer to the documented checks in `test-results.md`. A single check may include several assertions (for example both success and rejection of a form).

---

## Evidence

All evidence is inside `reports/website-audit/evidence/`:

- `lighthouse-production-landing.html` / `.json`
- `lighthouse-production-login.html` / `.json`
- 13 screenshots covering production landing, login, registration, subscription, teacher dashboard, public portfolio, student dashboard, and 390/768/1366/1920 px layouts
- `functional-evidence.json`, `security-evidence.json`, `performance-evidence.json`
- `api-test-results.txt`

No source code was modified during this audit. Two temporary token files created during API testing were deleted.

---

## Report index

| Report | Purpose |
|---|---|
| `executive-summary.md` | This document |
| `routes-inventory.md` | Every page/view, role, URL and test status |
| `bugs-report.md` | Confirmed defects with reproduction and root cause |
| `security-report.md` | Security findings and controls that passed |
| `performance-report.md` | Measured performance results |
| `ui-ux-report.md` | Visual, responsive and accessibility findings |
| `api-report.md` | API behaviour and status-code matrix |
| `test-results.md` | Passed / failed / blocked / not-run register |
| `recommended-fixes.md` | Prioritised remediation plan |

---

## Bottom line

Ship-blocking issues: **none observed in the core exam flow**. Fix the **rate-limit bypass** and the **PII lookup exposure** before scaling teacher onboarding; fix the **attempt summary** because it can cause a teacher to make wrong decisions about a student's remaining attempts; address the **cold-start latency** because it directly delays every first login after idle.

