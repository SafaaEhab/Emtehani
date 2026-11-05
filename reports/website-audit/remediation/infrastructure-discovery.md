# Infrastructure Discovery

**Date:** 2026-11-05
**Authority hierarchy:** MonsterASP API + Monster SQL Server are the production source of truth.

> **Post-deployment note:** this is the pre-deployment discovery snapshot. Backup, index application, and deployment were completed later on 2026-11-05; see `final-production-deployment.md`.

---

## 1. Frontend

| Item | Finding |
|---|---|
| Provider | Vercel, project `emtehani` (`prj_4rN9XCkAxub4WWdhktMUfS7pSr8r`) |
| Production URL | `https://emtehani.vercel.app` |
| Framework | React 19.3, Vite 8.3, Tailwind 4 |
| Build | `npm run build` (`verify-csp.mjs` → `vite build`) |
| Production env | `VITE_API_URL=https://emtehani.runasp.net` (current Vercel env) |
| Current deployed build | Still calls `emtehani-api.vercel.app`; CSP matches that old origin |
| CSP source | `frontend/vercel.json` (repo) now matches Monster |
| Deployment | Vercel CLI authenticated as `safaaehab`; no deploy executed |

## 2. Backend

| Item | Finding |
|---|---|
| Authoritative provider | MonsterASP.NET |
| URL | `https://emtehani.runasp.net` |
| Server | Microsoft-IIS/10.0, ASP.NET, .NET 10 |
| Health | 200 Healthy (139 ms) |
| Login timing | 320 ms (unknown account, expected 401) |
| CORS | Allows `https://emtehani.vercel.app`; preflight 204 |
| Legacy sibling | `https://emtehani-api.vercel.app` (Vercel, same Monster DB, not authoritative) |
| Deployment artifact | `dotnet publish -c Release` succeeds |
| Deployment access | **Missing** FTP/WebDeploy/control-panel credentials |
| Runtime config | Env vars in Monster panel; no secrets in repo |
| Forwarded headers | Now untrusted by default; proxy IPs unknown |
| Logging | Structured ASP.NET logs; startup bootstrap duration logged |

## 3. Database

| Item | Finding |
|---|---|
| Provider | MonsterASP SQL Server 2025 Express |
| Database | `db73841` |
| Access | Read-only queries from workstation using the existing user connection; no writes |
| Schema | 15 tables; no stored procedures/views/triggers |
| Migration history | 3 EF migrations present |
| Key types | `Status`, `EntityType`, `EntityId` are `nvarchar(max)` |
| Row counts | Users 29, Exams 4, Questions 31, Submissions 12, Answers 255, AuditEvents 118, TeacherStudentRequests 16, TeacherSubscriptions 6 |
| Repair indexes | Prepared, validated on LocalDB probe, **not applied** |
| Backup | Not executed; requires approval |

## 4. Evidence / configuration drift

- Vercel env says Monster; deployed frontend calls Vercel API; CSP says Vercel API.
- Repository CSP already says Monster.
- Next approved frontend build with the current env will make JS/CSP consistent with Monster.
- The Vercel API is a separate deployment with its own cold-start behavior; it must be decommissioned or kept on the same source/contract, not forked.

## 5. Access matrix

| Capability | Status |
|---|---|
| Read repository/build/test locally | Yes |
| Read production API health/CORS | Yes |
| Read production SQL schema/counts | Yes (read-only) |
| Write production SQL | **No — not authorized** |
| Deploy Vercel | Credentials present; **not executed without approval** |
| Deploy MonsterASP | **No credentials** |
| Admin UI credentials | **No** |
| Isolated PostgreSQL 17 E2E DB | **No** |

## 6. Security note

No production connection string, JWT, SMTP password, or API token is included in this document. Configuration values above are names/origins or measured timings only.
