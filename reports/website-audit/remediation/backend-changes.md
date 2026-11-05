# Backend Changes

**Stack:** ASP.NET Core 10 minimal APIs, EF Core 10, SQL Server 2025 / PostgreSQL.
**Authoritative production host:** MonsterASP.NET (`https://emtehani.runasp.net`, IIS 10).
**Database:** MonsterASP SQL Server `db73841` (shared with the legacy Vercel API).

---

## Files changed

| File | Change |
|---|---|
| `Emtehani.API/Program.cs` | Trusted-proxy gate; global/per-account rate limits; per-account login throttle; lookup masking/rate limit/audit; finished-exam attempt aggregation; audit submission visibility; extra-attempt guard; `openableExams`; password policy; startup timing/bootstrap switch; rate limits on results/export |
| `Emtehani.API/LoginThrottle.cs` | **New** process-local account lockout (5 failures/15 min defaults) |
| `Emtehani.Infrastructure/DependencyInjection.cs` | Comment documenting why EF retrying execution strategy was rejected (explicit transactions); no functional retry change |
| `Emtehani.Infrastructure/Persistence/ExamWorkflowSchema.cs` | Idempotent SQL Server/PostgreSQL indexes for audit, submissions, relationships, exams |
| `Emtehani.Infrastructure/Persistence/AppDbContext.cs` | Matching model indexes (bounded columns only) |
| `Emtehani.Tests/RemediationTests.cs` | **New** six regression tests |
| `Emtehani.Tests/ApiIntegrationTests.cs`, `PlatformUpgradeTests.cs`, `StudentManagementTests.cs` | Test fixture passwords updated to satisfy 10-character policy; no assertions weakened |
| `Emtehani.API/.env.example` | Documented proxy, lockout, lookup limit, bootstrap switch |
| `docs/monsterasp-deployment.md` | New production variables and authoritative API notes |
| `docs/sql/*.sql` | **New** idempotent index deployment and read-only verification scripts |

---

## Security changes

### Forwarded headers / rate limits
- `Proxy:TrustForwardedFor` defaults to **false**. `X-Forwarded-For` is ignored unless the operator lists exact `Proxy:KnownProxies` or `Proxy:KnownNetworks`; startup throws if trust is requested without a list.
- `X-Forwarded-Proto` remains accepted so HTTPS detection works behind Monster/IIS.
- Added a **global API limiter** (120/min default) plus named policies:
  - `auth`: 60/min per IP
  - `join`: 10/min per authenticated user
  - `lookup`: 12/min per authenticated teacher
  - `api`: 120/min per IP
- Added `LoginThrottle`: 5 failed passwords per normalized e-mail → 15-minute 429 lockout, independent of IP. Successful login clears the counter.
- Added rate limits to results, results export, participant export, and audit endpoints.

### Lookup privacy
- Unrelated teacher: masked name (`first + initial + …`), `masked=true`, `stage=null`, `grade=null`, `requestStatus` only.
- Accepted teacher: full name/stage/grade.
- Response no longer includes `registrationSource`.
- Each lookup writes `teacher.student.lookup` with match/masked counts (no phone/name in details).

### Password policy
- 10–128 characters.
- Rejects common passwords (`password`, `1234567890`, `qwertyuiop`, etc.) and leading/trailing whitespace.
- Applies to registration and password reset; frontend mirrors the length rule.
- Existing accounts with shorter legacy passwords can still log in; the policy governs new credentials.

---

## Business-logic changes

### Finished-exam attempt aggregation
- `GET /api/teacher/students/{id}` now aggregates submissions across **all** teacher exams, including `Finished`.
- `attempts.used/completed/remaining`, `attemptsCount`, and `submittedAttempts` are consistent after grading.
- The manageable-exam list remains limited to unfinished exams; `finishedExamCount` is returned for context.
- `AttemptSummaryFor` business rules are unchanged: abandoned and exception attempts do not consume attempts.

### Audit visibility
- `GET /api/audit` includes teacher-owned events **and** `Submission` events whose IDs belong to the teacher's exams.
- Result: student `submission.started/submitted` events become visible without exposing another teacher's records.

### Extra attempt
- `POST /api/submissions/{id}/grant-extra-attempt` returns **409** when the exam is `Finished` with a clear message.

### Dashboard
- `/api/teacher/overview` returns `openableExams` (published, not Finished/Active/Paused).

### Startup
- Logs `Database bootstrap completed in {ms}`.
- `Database:BootstrapOnStartup` (default `true`) allows a deployment pipeline to manage schema and skip startup bootstrap after verification.
- EF `EnableRetryOnFailure` was tested and **rejected**: existing explicit transactions are not wrapped in `CreateExecutionStrategy()`, causing 500s on relationship accept. This was caught in the live SQL Server run and reverted.

---

## Tests added

`Emtehani.Tests/RemediationTests.cs`:

1. `FinishedExam_AttemptsAreCounted_AuditShowsSubmission_AndExtraAttemptIsBlocked`
2. `PhoneLookup_MasksUnrelatedStudent_AndRevealsOnlyAfterAcceptance`
3. `PhoneLookup_IsRateLimitedPerTeacher`
4. `PasswordPolicy_RejectsShortAndCommonPasswords`
5. `Login_IsLockedPerAccount_AfterRepeatedFailures`
6. `SpoofedForwardedFor_DoesNotBypassAuthRateLimit` (separate low-limit factory)

**Result:** 79 passed, 5 skipped, 0 failed (84 total) — baseline 73 passed, 5 skipped, 0 failed, plus 6 new tests.

---

## Live SQL Server verification

Disposable LocalDB, fresh database, full flow:

- register teacher → confirm Free → register student → accept request → create/publish/open exam → enter/start/submit → close.
- `detail used=1 completed=1 remaining=0 finishedExams=1 exams=0`
- `overview openable=0`
- `audit hasSubmission=true`
- `extraAttempt 409`
- masked lookup: `masked=true name=طالب ت… stage=null grade=null`
- linked lookup: `masked=false stage=ثانوي`
- XFF spoof: 54×401 + 11×429 (auth window partly consumed by prior calls).
- Fresh startup: 2.15 s (includes DB creation); second startup ~1.6 s.

Evidence: `evidence/live-sqlserver-verification.txt`, `evidence/xff-rate-limit-after-fix.txt`, `evidence/dotnet-test-final.txt`.

---

## Deployment requirements

- Deploy the Release publish output to MonsterASP.
- Set/confirm:
  - `ASPNETCORE_ENVIRONMENT=Production`
  - `ConnectionStrings__DefaultConnection` (Monster Local string)
  - `Jwt__Key` (32+ random bytes)
  - `FrontendOrigin=https://emtehani.vercel.app`
  - `PublicApiUrl=https://emtehani.runasp.net`
  - `Proxy__TrustForwardedFor=false` (default). Set `Proxy__KnownProxies`/`KnownNetworks` only with verified proxy IPs.
  - `Auth__MaxFailedLogins=5`, `Auth__LoginLockoutMinutes=15`
  - `RateLimiting__LookupPermitLimit=12`
  - `Database__BootstrapOnStartup=true` for the index rollout; set false only after verification.
- Startup will create five additive indexes. No table/row is modified.
- **Backend deployed to MonsterASP on 2026-11-05.** See `final-production-deployment.md` for verification.
