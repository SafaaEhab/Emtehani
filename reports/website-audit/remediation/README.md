# Remediation Package

**Status:** code complete, tested locally; **production deployment not performed**.

## Start here

1. `remediation-plan.md` — live checklist of every finding.
2. `fixed-issues.md` — root cause, files, fix, tests, deployment/production status.
3. `regression-test-results.md` — actual commands/results.
4. `deployment-guide.md` — Vercel + MonsterASP + SQL steps (requires approval).
5. `production-verification.md` — what was truly verified and what is still pending.
6. `remaining-issues.md` — blockers and accepted decisions.

## Code areas

- `frontend-changes.md`
- `backend-changes.md`
- `database-changes.md`
- `security-fixes.md`
- `performance-improvements.md`
- `infrastructure-discovery.md`
- `rollback-guide.md`

## Evidence

Everything under `evidence/`:

- Lighthouse before/after (landing/login)
- `live-sqlserver-verification.txt`
- `xff-rate-limit-after-fix.txt`
- `frontend-verification.txt`
- build/test/publish logs
- mobile menu fix screenshot
- production discovery read-only facts

## Key outcomes

- Backend tests: **79 passed, 5 skipped, 0 failed** (baseline 73/5/0).
- Frontend unit: **31 passed**.
- Frontend production build: **success** with Monster CSP guard.
- Local Lighthouse: **100 / 100 / 100** on landing and login.
- Live SQL Server full workflow: **pass**.
- No secrets, no production writes, no deployments, no production migrations.
