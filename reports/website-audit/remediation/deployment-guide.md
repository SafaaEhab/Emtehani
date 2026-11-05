# Deployment Guide

**Deployment completed on 2026-11-05. See `final-production-deployment.md` for execution evidence and verification results.** This guide is retained as the reproducible procedure and rollback reference.

---

## 0. Deployment topology (verified)

| Layer | Provider | Address |
|---|---|---|
| Frontend | Vercel project `emtehani` | `https://emtehani.vercel.app` |
| Authoritative API | MonsterASP.NET / IIS 10 / .NET 10 | `https://emtehani.runasp.net` |
| Database | MonsterASP SQL Server 2025 | `db73841` (private/local connection for the hosted site) |
| Legacy API | Vercel project `emtehani-api` | `https://emtehani-api.vercel.app` (shares the Monster DB; not authoritative) |

**Access blocker:** no MonsterASP FTP/WebDeploy/control-panel credentials are present in this environment (no deployment-related environment variables, no publish profile). The backend publish artifact and commands are ready, but the actual upload cannot be performed from here.

---

## 1. Pre-deployment gates

- [ ] Explicit approval to deploy frontend and backend.
- [ ] Verified Monster SQL backup and isolated restore (see `database-changes.md`).
- [ ] Run the read-only production schema verification (`docs/sql/2026-11-remediation-verify.sql`).
- [ ] Confirm production env values (names only below; never paste secrets into chat/repo).
- [ ] Confirm `VITE_API_URL=https://emtehani.runasp.net` in Vercel Production.
- [ ] Confirm Monster `FrontendOrigin=https://emtehani.vercel.app` and `PublicApiUrl=https://emtehani.runasp.net`.
- [ ] Confirm `Proxy__TrustForwardedFor` is absent or `false`; only enable with verified proxy IPs.
- [ ] Confirm SMTP settings and `Auth__AutoConfirmWithoutSmtp=false`.
- [ ] Announce a short maintenance window.

---

## 2. Backend — MonsterASP.NET

### Build and publish

```powershell
cd backend
dotnet restore Emtehani.slnx
dotnet test Emtehani.slnx
dotnet publish Emtehani.API\Emtehani.API.csproj -c Release -o .\publish\emtehani-api
```

Publish output includes `Emtehani.API.dll`, dependencies, and `appsettings.json` (no secrets). Target framework: `net10.0`.

### Upload

Use the MonsterASP control panel **File Manager**, **WebDeploy**, or **FTP** (credentials required; none available here). Deploy the contents of `publish\emtehani-api` to the site root, preserving the configured application pool.

- Application pool: .NET 10 / No Managed Code (if the panel offers it).
- Keep the existing site bindings/HTTPS certificate.
- If the panel supports WebDeploy, use the provided publish profile; do not invent endpoints.

### Environment variables (MonsterASP website panel)

Required/authoritative:

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__DefaultConnection=<Monster LOCAL connection string>
Jwt__Key=<random 32+ byte secret>
Jwt__Issuer=Emtehani
Jwt__Audience=Emtehani.Client
FrontendOrigin=https://emtehani.vercel.app
PublicApiUrl=https://emtehani.runasp.net
Auth__AutoConfirmWithoutSmtp=false
Email__From=<trusted sender>
Email__SmtpHost=<smtp host>
Email__SmtpPort=587
Email__Username=<smtp user>
Email__Password=<smtp app password>
Email__EnableSsl=true
Swagger__Enabled=false
```

Remediation additions:

```text
Proxy__TrustForwardedFor=false
# Proxy__KnownProxies=<verified proxy IPs, only if trust is enabled>
# Proxy__KnownNetworks=<verified CIDRs, only if trust is enabled>
Auth__MaxFailedLogins=5
Auth__LoginLockoutMinutes=15
RateLimiting__LookupPermitLimit=12
Database__BootstrapOnStartup=true
```

Do not put the connection string or SMTP/JWT secrets in source, screenshots, or reports.

### Schema

- Startup runs `EnsureCreated` → `ExamWorkflowSchema.EnsureAsync` → `PlatformDataUpgrade.EnsureAsync`.
- It creates five **additive** indexes; no rows/tables are modified.
- If the DBA prefers pre-creating them, run `docs/sql/2026-11-remediation-indexes.sql` first.
- After schema verification, `Database__BootstrapOnStartup` may be set to `false` if the pipeline owns schema changes.
- Do not apply a production migration/restore without approval.

### Health

```powershell
Invoke-WebRequest https://emtehani.runasp.net/health -UseBasicParsing
# Expect 200 "Healthy"
```

---

## 3. Frontend — Vercel

### Build

```powershell
cd frontend
npm ci
$env:VITE_API_URL="https://emtehani.runasp.net"
$env:VERCEL_ENV="production"
npm run build
```

The build fails if the CSP does not allow the build API origin. `VITE_API_URL` must be HTTPS in production.

### Deploy

Use the Vercel dashboard (project `emtehani`) or:

```powershell
vercel --prod --cwd frontend
```

The CLI was authenticated as `safaaehab` for the completed 2026-11-05 production deployment. The release was built and verified locally before upload; see `final-production-deployment.md`.

- Confirm `frontend/vercel.json` is part of the deployment (CSP + rewrites + Link headers).
- Verify the generated JS contains `emtehani.runasp.net` and not `emtehani-api.vercel.app`.
- Verify the served CSP `connect-src` includes Monster.
- Keep the Vercel project env `VITE_API_URL` set to Monster.

### Legacy Vercel API

`emtehani-api.vercel.app` is not authoritative. It shares the Monster DB but uses a separate deployment and cold-start path.

- Do **not** add/duplicate business logic there.
- Recommended: after the Monster API is verified, decommission it or redirect traffic; if it must remain, deploy the same commit and environment contract, but treat Monster as the source of truth.
- Do not leave two production APIs with different code/config long-term.

---

## 4. Recommended deployment order

1. Take/verify backup.
2. Deploy backend to MonsterASP (startup creates/adds indexes).
3. Verify `/health`, `/api`, CORS preflight, and a read-only login probe.
4. Deploy frontend to Vercel with Monster env.
5. Verify the served JS/CSP uses Monster.
6. Run the smoke checklist below.
7. Keep the previous artifacts for rollback.

---

## 5. Production smoke checklist (after approval)

Use disposable accounts only; do not modify real classes/exams.

- [ ] `GET /health` = Healthy.
- [ ] Landing loads; no console errors; CSP has Monster.
- [ ] Teacher registration/login (disposable).
- [ ] Confirm Free plan.
- [ ] Create/publish/open a disposable exam.
- [ ] Student registration + link acceptance.
- [ ] Student sees the exam; start/submit.
- [ ] Teacher student detail shows Used/Completed 1.
- [ ] Audit log shows `submission.submitted`.
- [ ] Essay grading + result publication.
- [ ] Phone lookup masks an unrelated student; full for linked.
- [ ] Extra attempt on finished exam returns 409.
- [ ] Rate limiting: 61 login attempts from one client → 429; spoofed XFF also 429.
- [ ] Browser Network: all requests go to `emtehani.runasp.net`.
- [ ] Browser Console: no errors.
- [ ] Monster error log: no unhandled 5xx.
- [ ] Check production row counts are unchanged aside from disposable test rows.

**Verification status:** the pre-deploy read-only checks are complete; the post-deploy checklist is **pending**.

---

## 6. Configuration consistency checklist

| Item | Value |
|---|---|
| Vercel `VITE_API_URL` | `https://emtehani.runasp.net` |
| Vercel CSP `connect-src` | `'self' https://emtehani.runasp.net` |
| Monster `FrontendOrigin` | `https://emtehani.vercel.app` |
| Monster `PublicApiUrl` | `https://emtehani.runasp.net` |
| README / docs / Link headers | Monster URL |
| Legacy Vercel API | not referenced by the frontend; decommission or keep on the same commit |
