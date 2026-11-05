# Final Production Deployment

**Status: FULLY DEPLOYED AND VERIFIED**

**Date:** 2026-11-05  
**Branch:** `remediation/website-audit-2026-11-05`  
**Deployment commit:** `0786c06` — “Remediate production audit findings and prepare deployment”  
**Authoritative backend:** `https://emtehani.runasp.net`  
**Production frontend:** `https://emtehani.vercel.app`

---

## 1. Executive summary

The remediated Emtehani platform is live in production and verified end to end:

- New ASP.NET Core Release backend is running on the verified MonsterASP site.
- The five reviewed additive indexes exist in the production SQL Server database.
- The updated React frontend is served by Vercel with the correct Monster API target and CSP.
- Production regression passed **43 / 43 checks**, including a complete teacher/student exam workflow, essay grading, results publication, CSV export, audit visibility, PII masking, authorization boundaries, session rotation, registration validation, and rate limiting.
- Browser verification found **0 console errors/warnings** at desktop and mobile widths; all API traffic went to `https://emtehani.runasp.net`.
- All disposable accounts and their records were deleted. Production row counts and audit-event count are exactly at their pre-test baseline.
- A verified database backup and a full site archive exist as rollback points.

No production data was lost. One deployment-window incident (publish replaced `appsettings.json`, causing a temporary 500) was recovered from the verified site backup; see §7.

---

## 2. Verified targets and ownership

| Component | Verified target |
|---|---|
| MonsterASP account | Ahmed Gbreel / `ahmedgbreel47@gmail.com`; Customer ID 19681; **Verified** |
| Website | `emtehani.runasp.net`; `site102923`; GUID `c44aa1b7-d11c-494d-92fb-6fa73c1167b9`; .NET 10 / IIS |
| Database | `db73841` / `db73841.databaseasp.netEmtehani`; GUID `1ad21590-fa56-4767-89fa-9e7f0f4b4097`; SQL Server 2025 |
| Vercel project | `emtehani`; `prj_4rN9XCkAxub4WWdhktMUfS7pSr8r`; team `safaas-projects-216f1299` |
| Vercel account | `safaaehab012345@gmail.com` — Verified primary email |

The site and database were confirmed as the correct resources before any change. The legacy Vercel API `emtehani-api.vercel.app` is **not** authoritative and is no longer referenced by the production frontend.

---

## 3. Backup and recovery readiness

| Control | Result |
|---|---|
| Database backup before changes | `db73841_custom_05.11.2026_639294985474398020.bak` |
| Backup size | 9 MB |
| Backup status | Done / Completed; restore action available |
| Site rollback archive | `wwwroot.zip`, 35.7 MB, 74 files |
| Original production settings | Preserved and restored; secrets never disclosed |

The database backup was created and verified before the backend deployment and index changes.

---

## 4. Deployment result

### 4.1 Backend — MonsterASP

| Check | Result |
|---|---|
| Release publish deployed | Yes |
| `Emtehani.API.dll` SHA-256 | `6352240599200EE2EAFBADA54564B16904E5C269A0825DD1FDC9169A632C5BA6` |
| `Emtehani.Infrastructure.dll` SHA-256 | `3825529B48844C21ACE8BD2656C1136610E5FF6EA12B2617A874FE0C5E5F043D` |
| `Emtehani.Domain.dll` SHA-256 | `992C541915D52CDA0E288BC95E0B3B8ECFD95586552546BEBAD95D065921EABB` |
| `Emtehani.Application.dll` SHA-256 | `E4D3EA67B21A171785F22CECD60507FB86BA81793C2163A8B437DBDDBDF9D5A5` |
| Deployed vs. local Release hashes | **All four match** |
| `GET /health` | **200 Healthy** |
| `GET /api` | **200** `{"service":"Emtehani API","version":"1.0"}` |
| Swagger in production | Disabled (`404`) |
| Unauthenticated protected endpoints | `401` |
| CORS from `https://emtehani.vercel.app` | Allowed |
| CORS from unknown origin | Rejected |

### 4.2 Database indexes — applied and verified

All five reviewed additive indexes exist in production:

- `IX_AuditEvents_ActorId_AtUtc`
- `IX_Exams_TeacherId_Published`
- `IX_Submissions_ExamId`
- `IX_Submissions_StudentId_ExamId`
- `IX_TeacherStudentRequests_TeacherId_Unlinked`

No schema-destructive operation or EF migration was applied.

### 4.3 Frontend — Vercel

| Check | Result |
|---|---|
| Deployment ID | `dpl_FLpEdBAw5DqDWqiuiDU5maWBTCdR` |
| Deployment URL | `https://emtehani-gh585vuq1-safaas-projects-216f1299.vercel.app` |
| Production URL | `https://emtehani.vercel.app` |
| Status | **READY**, production |
| Entry bundle | `/assets/index-CHDVhvvL.js` (468,367 bytes) |
| Bundle contains Monster API | Yes — `emtehani.runasp.net` |
| Bundle contains legacy API | **No** — `emtehani-api.vercel.app` absent |
| CSP `connect-src` | `'self' https://emtehani.runasp.net` only |
| HSTS / frame / MIME / referrer / permissions headers | Present |
| Vercel settings | Root directory restored to `frontend`; `requireVerifiedCommits` restored to `true`; accidental `vercel-prebuilt2` project deleted |

---

## 5. Production regression — 43 / 43 passed

Full machine-readable results: `evidence/production-regression-2026-11-05.json`.

| Area | Result |
|---|---|
| Availability, health, API metadata, disabled Swagger | Pass |
| Unauthenticated and invalid-token rejection | Pass |
| CORS allow-list | Pass |
| Teacher free-plan confirmation | Pass |
| Exam create → publish → open | Pass |
| Student discovery → enter → start → paper | Pass |
| Answer-key absence in student paper | Pass |
| Autosave and final submit | Pass |
| Extra attempt on exhausted exam → `409` | Pass |
| Teacher results visibility | Pass |
| Essay review and grading | Pass |
| Exam close and results publication | Pass |
| Student result visibility (3/3) | Pass |
| CSV export | Pass |
| Student-detail attempt aggregate (`used=1`, `completed=1`, `remaining=0`) | Pass |
| Audit log records submission | Pass |
| Linked teacher sees unmasked student | Pass |
| Unrelated teacher sees masked student | Pass |
| Student blocked from teacher/admin endpoints | Pass |
| Teacher blocked from admin endpoints | Pass |
| Cross-teacher exam/results/grading blocked | Pass |
| Refresh-token rotation and old-token rejection | Pass |
| Weak-password, duplicate-email, and three-part-name rejection | Pass |

### 5.1 Rate limit and forwarded-header handling

- 61 login requests were sent, each with a different spoofed `X-Forwarded-For` value.
- Result: **60 × HTTP 401**, then **HTTP 429 on request 61**.
- Conclusion: forwarded headers are not trusted for rate-limit partitioning; the per-IP auth limit works.

### 5.2 Browser verification

- Landing, teacher login/dashboard, student login/results, registration validation, and Admin dashboard rendered correctly.
- Console: **0 errors, 0 warnings**.
- API traffic: only `https://emtehani.runasp.net`; no legacy API requests.
- Responsive widths 390, 768, 1366, 1920: no horizontal overflow; main heading visible.
- Registration UI exposed `البكالوريا` with `أولى بكالوريا` and `تانية بكالوريا`.
- A three-part student name produced the expected validation alert; no account was created.
- Temporary disposable Admin verification: `GET /api/admin/overview` → 200, `GET /api/admin/subscription-settings` → 200, Admin UI rendered. The account was deleted immediately.

---

## 6. Data preservation and cleanup

Baseline and final production counts are identical:

| Table | Baseline | Final |
|---|---:|---:|
| Users | 29 | 29 |
| Exams | 4 | 4 |
| Submissions | 12 | 12 |
| Answers | 255 | 255 |
| Questions | 31 | 31 |
| ExamParticipants | 11 | 11 |
| TeacherStudentRequests | 16 | 16 |
| TeacherSubscriptions | 6 | 6 |
| AuditEvents | 118 | 118 |

Only rows created by disposable test accounts were removed. No real user, exam, submission, answer, link request, subscription, or audit event was modified or deleted. `SELECT COUNT(*) FROM Users WHERE Email LIKE 'prodtest-%'` returned **0** after cleanup.

---

## 7. Deployment-window incident and recovery

During backend extraction, the publish ZIP replaced the live `appsettings.json` with the repository template, causing HTTP 500. The original production settings file was restored from the verified `wwwroot.zip` site backup, and the application pool was restarted.

Result after recovery:

- `GET /health` → **200 Healthy**
- `/api` → **200**
- Deployed DLL hashes match the Release artifact
- Production connection string, JWT key, and SMTP configuration presence re-verified (values not disclosed)
- Monster event log: the last application errors are from the initial 19:44 attempt; no new application errors after recovery (last application start 21:15)

The previous working site files remain available in `wwwroot.zip` for rollback.

---

## 8. Known limitations

These are documented limitations, not deployment failures:

1. The existing Playwright E2E suite requires an isolated PostgreSQL 17 environment and was not run in that configuration. Production API/browser regression was used instead.
2. Admin **write** actions (for example, changing free-plan features) were intentionally not performed, to avoid altering production configuration. Admin reads and UI were verified.
3. `X-Forwarded-For` remains intentionally untrusted until verified Monster proxy IPs/CIDRs are supplied. Per-IP auth limiting works and was verified.
4. `LoginThrottle` is process-local; a future scale-out deployment would need a distributed store.
5. The deployment commit `0786c06` is on the local branch; the Vercel project is not Git-connected, so the production release was deployed from the verified local build rather than a Git push.

---

## 9. Rollback

- Database: restore `db73841_custom_05.11.2026_639294985474398020.bak` from the MonsterASP backup panel.
- Site files: extract/restore `wwwroot.zip` at the site root and restart the application pool.
- Frontend: promote the previous Ready Vercel deployment from the project’s deployment history.
- Indexes: rollback SQL is in `docs/sql/2026-11-remediation-indexes.sql`.

---

## 10. Evidence index

| Evidence | Path |
|---|---|
| This report | `reports/website-audit/remediation/final-production-deployment.md` |
| Deployment and infrastructure facts | `reports/website-audit/remediation/evidence/production-deployment-2026-11-05.txt` |
| 43-check production regression | `reports/website-audit/remediation/evidence/production-regression-2026-11-05.json` |
| Backup and target verification | `reports/website-audit/remediation/evidence/production-backup-created.txt` |
| Backend test log | `reports/website-audit/remediation/evidence/dotnet-test-final.txt` |
| Frontend unit test log | `reports/website-audit/remediation/evidence/frontend-unit-final.txt` |
| Frontend build log | `reports/website-audit/remediation/evidence/frontend-build-final.txt` |
| Local Lighthouse reports | `reports/website-audit/remediation/evidence/lighthouse-local-*.json` |

---

**Final statement:** The remediated backend, database indexes, and frontend are deployed to the verified production targets. All executed production regression checks passed, production data was preserved, and rollback artifacts are retained.
