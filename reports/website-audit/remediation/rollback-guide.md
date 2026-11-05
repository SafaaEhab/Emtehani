# Rollback Guide

**No production change has been executed.** This guide applies only after an approved deployment.

---

## 1. Fast rollback matrix

| Symptom | Action |
|---|---|
| Frontend JS/CSP error, API unreachable | Revert Vercel to the previous production deployment; do not change Monster |
| Backend 5xx or startup failure | Redeploy the previous Monster publish artifact; keep the database (indexes are additive) |
| New index suspected in a query regression | Drop the five new indexes using the rollback block in `docs/sql/2026-11-remediation-indexes.sql` |
| Schema/bootstrap failure | Restore the pre-deploy backup **only with explicit approval**; do not reset the database |
| Security regression | Revert to previous API build; rotate JWT/SMTP secrets if exposure is suspected; review logs |

---

## 2. Frontend rollback (Vercel)

1. Open Vercel → project `emtehani` → Deployments.
2. Promote the last known-good production deployment.
3. Confirm the served page and CSP match that build (`connect-src` may point to the old Vercel API if that is the rollback target).
4. Do not leave `VITE_API_URL` pointing to an API that the rolled-back build/CSP cannot reach.

The repository rollback is a normal revert of the remediation commit(s); the audit branch contains all changes. No database dependency exists for the frontend.

---

## 3. Backend rollback (MonsterASP)

1. Keep the previous `publish` directory/zip before uploading the new one.
2. If the new API fails, restore the previous publish output through the MonsterASP control panel/WebDeploy/FTP.
3. Restart the app pool/site.
4. Verify `GET /health` and a read-only login probe.
5. The previous API build is compatible with the added nullable columns/tables/indexes; it ignores the new indexes.

**Do not** run `EnsureCreated` against a different database or reset the schema as a rollback.

---

## 4. Database rollback

The remediation adds **indexes only** — no tables, columns, rows, or constraints are removed/changed.

Rollback indexes:

```sql
DROP INDEX IF EXISTS [IX_AuditEvents_ActorId_AtUtc] ON [AuditEvents];
DROP INDEX IF EXISTS [IX_Submissions_StudentId_ExamId] ON [Submissions];
DROP INDEX IF EXISTS [IX_Submissions_ExamId] ON [Submissions];
DROP INDEX IF EXISTS [IX_TeacherStudentRequests_TeacherId_Unlinked] ON [TeacherStudentRequests];
DROP INDEX IF EXISTS [IX_Exams_TeacherId_Published] ON [Exams];
```

Only do this if a measured regression is attributable to the indexes; otherwise keep them.

If a future schema change is applied, the backup/restore plan is:

1. Stop writes / put the site in maintenance mode.
2. Confirm the verified pre-change `.bacpac`.
3. Restore to an **isolated** database first; verify row counts and the smoke checklist.
4. Production restore only with explicit approval and after preserving the failed database copy.
5. Never use a production restore as a routine rollback.

---

## 5. Data safety rules

- No production row, table, or column was modified by the remediation code.
- Do not truncate/delete real users, exams, submissions, answers, or audit rows.
- Do not reuse test accounts/data in production verification.
- Do not expose connection strings, JWTs, or SMTP credentials in logs/reports.
- If a security incident is suspected, rotate `Jwt__Key`, SMTP credentials, and the database password through the provider panel, then invalidate sessions by clearing refresh-token hashes in a controlled, approved operation.

---

## 6. Rollback verification

- [ ] `/health` returns 200 Healthy.
- [ ] Frontend loads with the expected API origin/CSP.
- [ ] Login works with a disposable account.
- [ ] Teacher sees exams; student sees assigned exam.
- [ ] Audit/attempt counters behave as expected (old build may lose the fixed counters; document if rollback is temporary).
- [ ] No 5xx in Monster logs.
- [ ] Row counts unchanged.
