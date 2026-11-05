# Remediation Plan & Live Checklist

**Branch:** `remediation/website-audit-2026-11-05`
**Base commit:** `9c8bcdc`
**Date:** 2026-11-05
**Deployment status:** **DEPLOYED AND VERIFIED** on 2026-11-05. See `final-production-deployment.md`.

**Legend:** ✅ fixed & tested · 🟡 fixed, deployment/approval required · ⏸ accepted/documented · ⛔ blocked by external access.

---

## 0. Audit recovery

- [x] Read all nine audit reports in `reports/website-audit/`.
- [x] Inspected all evidence: Lighthouse, screenshots, API matrix, security/performance JSON.
- [x] Reconciled audit findings with the actual source and live schema.
- [x] Recorded the base commit and pre-existing working-tree changes.
- [x] Created a dedicated remediation branch (working-tree changes preserved).
- [x] Backed up key configuration/source files outside the repository.
- [x] Identified the authoritative API: **MonsterASP `https://emtehani.runasp.net`**.
- [x] Verified MonsterASP SQL Server connectivity and schema read-only.
- [x] Confirmed the Vercel API and Monster API share the same Monster SQL database.
- [x] Confirmed current production frontend/CSP still point at the legacy Vercel API (drift).

## 1. Security fixes

| ID | Finding | Status | Evidence / test |
|---|---|---|---|
| SEC-01 | High — `X-Forwarded-For` rate-limit bypass | ✅ | Trusted-proxy gate + per-account lockout; 65 spoofed requests now 54×401 + 11×429 after prior window use; integration test `SpoofedForwardedFor_DoesNotBypassAuthRateLimit` |
| SEC-02 | Medium — student PII exposed by phone lookup | ✅ | Masked name + no stage/grade before acceptance; per-teacher lookup limit; audit event; `PhoneLookup_MasksUnrelatedStudent_AndRevealsOnlyAfterAcceptance`, `PhoneLookup_IsRateLimitedPerTeacher` |
| SEC-03 | Medium — CSP/API origin drift | ✅ | Authoritative Monster origin; build-time `scripts/verify-csp.mjs` fails build on drift; unit test asserts CSP allows Monster only; production redeploy pending |
| SEC-04 | Low — length-only password policy | ✅ | 10–128 chars, common-password blocklist, whitespace guard; UI + server; `PasswordPolicy_RejectsShortAndCommonPasswords` |
| SEC-05 | Low — tokens in localStorage | ⏸ | Accepted risk documented; CSP strict, no HTML sinks; long-term cookie/BFF migration recommended |
| SEC-06 | Info — limited rate-limit coverage | ✅ | Global API limiter + named policies for lookup/import/export results |
| SEC-07 | Info — dev reset token disclosure | ⏸ | Already Development-only; documented production guard |
| SEC-08 | Info — public OpenAPI schema | ⏸ | Intentional discovery metadata; no secrets |

## 2. Functional/business fixes

| ID | Finding | Status | Evidence / test |
|---|---|---|---|
| BUG-01 | Finished-exam attempts vanish from student detail | ✅ | Aggregate across all teacher exams; `detail used=1 completed=1 remaining=0` live; `FinishedExam_AttemptsAreCounted_AuditShowsSubmission_AndExtraAttemptIsBlocked` |
| BUG-02 | Dashboard counts finished exam as "openable" | ✅ | Server `openableExams` excludes finished/active/paused; live `openable=0`; UI uses field |
| BUG-03 | Audit log omits student submissions | ✅ | Query includes submission-entity events for teacher's exams; live `hasSubmission=true`; test same |
| BUG-04 | Review pages show generic title "امتحاني" | ✅ | Added `review` / `essay-review` titles; document title per page; live title assertions |
| BUG-05 | Modal focus escapes to background | ✅ | Reusable `useFocusTrap`: Tab loop, Escape, restore focus, scroll lock; live 14-Tab test + `aria-describedby` |
| BUG-06 | High — same as SEC-01 | ✅ | See SEC-01 |
| BUG-07 | Medium — same as SEC-02 | ✅ | See SEC-02 |
| BUG-08 | Login helper contrast 4.44:1 | ✅ | Light `--app-subtle` changed to `#5b6b82` (5.07:1); Lighthouse login now 100 |
| BUG-09 | Heading order + logo accessible-name mismatch | ✅ | Live preview `aria-hidden`; logo label includes brand; Lighthouse landing 100 |
| BUG-10 | Invalid `Agentmap:` robots directive | ✅ | Removed; Lighthouse SEO 100; unit test asserts directive absent |
| BUG-11 | Same as SEC-03 | ✅ | See SEC-03 |
| BUG-12 | ~10 s production cold start | 🟡 | Authoritative API is Monster/IIS (139 ms health, 320 ms login); Vercel serverless cold-start path removed by pointing build to Monster; startup timer + optional bootstrap flag; no Monster idle tuning available without panel access |
| BUG-13 | Mobile menu lacks backdrop/full height | ✅ | Fixed full-height panel + backdrop + scroll lock + focus trap; live rect top=72/height=772/width=390 |
| BUG-14 | Extra attempt allowed on finished exam | ✅ | 409 guard; live `extraAttempt 409`; test |
| BUG-15 | Refresh/back loses workspace context | ✅ | Hash navigation for top-level pages + sessionStorage fallback; live back to `#/results`, deep link `#/students` |
| BUG-16 | README says password reset unavailable | ✅ | README updated with reset flow/SMTP requirement |
| BUG-17 | 780 KB entry bundle | ✅ | Page-level lazy routes + lazy developer page; entry now 476 KB (146 KB gzip) |

## 3. UI/UX & accessibility register

| ID | Item | Status |
|---|---|---|
| UI-01 | Mobile menu backdrop/height/background interaction | ✅ |
| UI-02 | Misleading counters (attempts/dashboard/audit) | ✅ via BUG-01/02/03 |
| UI-03 | Nested review titles | ✅ via BUG-04 |
| UI-04 | Modal keyboard trap | ✅ via BUG-05 |
| UI-05 | Small-text contrast | ✅ via BUG-08 |
| UI-06 | Heading order | ✅ via BUG-09 |
| UI-07 | Logo accessible name | ✅ via BUG-09 |
| UI-08 | Theme flash/transient contrast | ✅ | external pre-paint `theme-init.js`; no inline script (CSP safe) |
| UI-09 | Desktop sidebar ≥1024 px | ✅ verified |
| UI-10 | Empty states | ✅ verified |
| UI-11 | RTL/bidi | ✅ verified |
| UI-12 | Touch targets | ✅ verified; modal close 32 px noted as minor |

## 4. API / database / performance

| ID | Item | Status |
|---|---|---|
| API-01 | XFF rate-limit bypass | ✅ SEC-01 |
| API-02 | PII lookup | ✅ SEC-02 |
| API-03 | Attempt summary | ✅ BUG-01 |
| API-04 | Cold start | 🟡 BUG-12 |
| API-05 | Audit visibility | ✅ BUG-03 |
| API-06 | Rate-limit coverage | ✅ global + named policies |
| API-07 | Extra attempt on finished exam | ✅ BUG-14 |
| API-08 | Public OpenAPI | ⏸ intentional |
| API-09 | Two API hosts / CSP drift | ✅ SEC-03 + deployment guide |
| API-10 | Export loads all rows | ⏸ documented; stream for large cohorts |
| PERF-01 | Cold start | 🟡 BUG-12 |
| PERF-02 | Bundle size | ✅ BUG-17 (remaining improvement documented) |
| DB-01 | Add missing indexes | 🟡 code + SQL prepared; not applied |
| DB-02 | Production schema/type verification | ✅ read-only |
| DB-03 | Backup/rollback | ✅ documented; not executed |

## 5. Regression gates

- [x] `dotnet build` Debug: 0 errors.
- [x] `dotnet test`: **79 passed, 5 skipped, 0 failed** (baseline 73/5; +6 new tests).
- [x] `dotnet build -c Release`: 0 errors.
- [x] `dotnet publish -c Release`: success.
- [x] `npm run build` with `VITE_API_URL=https://emtehani.runasp.net`: success; CSP guard pass.
- [x] `npm run test:unit`: 31 passed, 0 failed.
- [x] Frontend Lighthouse local: **100 accessibility / 100 best practices / 100 SEO** on landing and login.
- [x] Live SQL Server disposable flow: registration → accept → exam → submit → close → attempts/audit/lookup.
- [x] XFF spoof regression: 429 observed.
- [ ] Existing Playwright E2E suite: blocked (no isolated PostgreSQL 17; documented).
- [x] Production deployment and post-deploy verification: **43 / 43 checks passed** on 2026-11-05; see `final-production-deployment.md`.

## 6. Remaining external limitations after deployment

1. **Full Playwright E2E** requires an isolated PostgreSQL 17 database; it was not run in that configuration. Production API/browser regression was used instead.
2. **Admin write actions** were intentionally not exercised. Admin read APIs and the Admin UI were verified with a temporary disposable account that was deleted.
3. **Monster proxy IPs** are unknown; `X-Forwarded-For` remains untrusted by default. Per-IP auth limits were verified (429 on request 61).
4. **Login lockout is process-local**; scale-out would require a distributed store.
5. The deployment commit `0786c06` is local; the Vercel project is not Git-connected, so the release was deployed from the verified local build.
