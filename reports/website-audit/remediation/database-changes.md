# Database Changes

**Production:** MonsterASP SQL Server 2025 Express, database `db73841`.
**Access used:** read-only queries during discovery/verification. No production write was executed.
**Schema mechanism:** existing idempotent startup upgrade (`EnsureCreated` → `ExamWorkflowSchema.EnsureAsync` → `PlatformDataUpgrade.EnsureAsync`), **not** a new EF migration, because the baseline already had pending model changes and the project explicitly uses the startup schema for production.

---

## 1. Production schema verified read-only

- SQL Server 2025 (17.0.5015.4), compatibility level 170, collation `SQL_Latin1_General_CP1_CI_AS`, SIMPLE recovery.
- 15 tables: `Users`, `Exams`, `Questions`, `Submissions`, `Answers`, `AuditEvents`, `ExamParticipants`, `TeacherStudentRequests`, `TeacherStudentInvitations`, `TeacherStudentExamBans`, `StudentAcademicChanges`, `SubscriptionPlans`, `TeacherSubscriptions`, `TeacherProfiles`, `SystemSettings`.
- No stored procedures, views, or triggers.
- Foreign keys present for questions/exams, submissions/exams/users, answers/questions/submissions, participants, invitations, bans, academic changes, subscriptions, teacher profiles.
- Unique constraints/indexes present for e-mail, student code, student phone, normalized student name, join code, participant code, answer per submission/question, in-progress attempt per exam/student, pending/accepted relationship per pair, teacher profile per teacher, public slug.
- Row counts (snapshot, read-only): Users 29, Exams 4, Questions 31, Submissions 12, Answers 255, AuditEvents 118, TeacherStudentRequests 16, TeacherSubscriptions 6.
- EF migration history: `20261019130152_InitialCreate`, `20261020213639_HardenSubmissionConcurrency`, `20261029071926_20261029150000_FirstMigration`.
- Production `Status`, `EntityType`, and `EntityId` columns are **nvarchar(max)**. This constrained the index design.

## 2. Additive indexes (not applied to production)

| Index | Table | Columns | Purpose |
|---|---|---|---|
| `IX_AuditEvents_ActorId_AtUtc` | `AuditEvents` | `ActorId`, `AtUtc DESC` | teacher audit page |
| `IX_Submissions_StudentId_ExamId` | `Submissions` | `StudentId`, `ExamId` | student attempt aggregation |
| `IX_Submissions_ExamId` | `Submissions` | `ExamId` | exam results/roster/export |
| `IX_TeacherStudentRequests_TeacherId_Unlinked` | `TeacherStudentRequests` | `TeacherId`, `UnlinkedAtUtc` | relationship/pending checks |
| `IX_Exams_TeacherId_Published` | `Exams` | `TeacherId`, `IsPublished` | dashboard/exam lists |

**Important:** no index uses `Status`, `EntityType`, or `EntityId` as a key column because those are `nvarchar(max)` in production. An early draft that indexed them was rejected and corrected. The probe below validated the final SQL against production-like max-length columns.

**Scripts:**
- `docs/sql/2026-11-remediation-indexes.sql` (idempotent create + commented rollback)
- `docs/sql/2026-11-remediation-verify.sql` (read-only row counts, index presence, integrity checks)

## 3. Verification performed

- Created a disposable LocalDB database from scratch with the API startup path.
- Confirmed the five indexes exist with the intended names.
- Created a separate LocalDB probe with `nvarchar(max)` Status/EntityType/EntityId columns; the final index batch created successfully and all five indexes were present. Probe dropped.
- Live full workflow on a fresh LocalDB database succeeded (registration → accept → exam → submit → close → attempt summary → audit → lookup). See `evidence/live-sqlserver-verification.txt`.
- Read-only production queries validated the audit-submission join and attempt aggregation shape against the real schema.

## 4. Data changes

- **None.** No production row, table, column, or index was modified.
- No database reset, truncate, delete, or restore was executed.
- All disposable LocalDB databases used for testing were dropped.

## 5. EF migration decision

`dotnet ef migrations has-pending-model-changes` reported pending changes before this remediation (pre-existing user work). Adding a migration would have mixed unrelated model changes with the five indexes and created a migration-history mismatch on a production database managed by `EnsureCreated` + idempotent schema upgrades.

**Decision:** follow the project’s existing production mechanism and add the indexes idempotently to `ExamWorkflowSchema` and the EF model. This is additive and backward-compatible with the previous API build. Do not introduce a second schema mechanism.

If the team later standardises on EF migrations, create a dedicated baseline migration task separately; do not apply it blindly to `db73841`.

## 6. Deployment and rollback

**Pre-deploy (requires approval):**
1. Take a verified MonsterASP backup (control-panel backup or `sqlpackage /Action:Export` with verified extraction).
2. Restore to an isolated database; compare row counts with the read-only snapshot above.
3. Run the index script on the isolated restore first.
4. Run the read-only verification script.

**Deploy:** the new API startup applies the five indexes idempotently. No data is rewritten.

**Rollback:**
- Indexes are additive and can be dropped using the commented statements in `docs/sql/2026-11-remediation-indexes.sql`.
- The previous API build remains compatible with the upgraded schema (extra indexes are ignored).
- Restore the backup only for actual data corruption, with explicit approval. Do not use a production restore as a routine rollback.

## 7. Capacity/query notes

- Current production scale is small (118 audit events, 12 submissions); the audit OR-query is cheap. If audit volume grows, consider a bounded `EntityType` length and a composite index in a separate approved migration.
- The audit endpoint currently materialises the teacher's submission id list to compare against `AuditEvents.EntityId`. This is acceptable at current scale but should be revisited for very large cohorts.
- Exports currently load all rows into memory; stream when a single exam exceeds a practical memory bound.
