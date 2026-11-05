# Security Fixes

**Scope:** all confirmed security findings from the audit. No destructive exploitation was performed.

---

## SEC-01 (High) — Forwarded-header rate-limit bypass

**Root cause:** `ForwardedHeadersOptions.KnownProxies/KnownIPNetworks` were cleared, so every request was treated as coming from a trusted proxy. The limiter keyed partitions on the rewritten `RemoteIpAddress`; a rotating `X-Forwarded-For` created a new partition per request.

**Fix:**
- `Proxy:TrustForwardedFor` defaults to **false**; `X-Forwarded-For` is ignored.
- If trust is explicitly enabled, `Proxy:KnownProxies` or `Proxy:KnownNetworks` must be configured; startup fails otherwise.
- `X-Forwarded-Proto` remains accepted for HTTPS detection.
- Added a global API limiter (120/min) plus named auth/join/lookup policies.
- Added per-account login lockout: 5 failures / 15 minutes by default, independent of IP.

**Verification:**
- Live pre-fix: 65 rotating-XFF requests → 65×401, 0×429.
- Live post-fix: 65 rotating-XFF requests → **54×401 + 11×429** (the auth window was partly consumed by prior calls in the same minute).
- Integration test: `SpoofedForwardedFor_DoesNotBypassAuthRateLimit`.

**Deployment requirement:** keep `Proxy__TrustForwardedFor=false` unless MonsterASP provides verified proxy addresses. If ARR/IIS fronts the app, obtain the proxy IPs/CIDRs and set `Proxy__KnownProxies`/`Proxy__KnownNetworks`. In-memory lockout is per process; use a distributed store if the API scales to multiple instances.

**Production verification:** Monster API health/CORS verified read-only; the spoof test is not run against production without authorization. Post-deploy verification should repeat the 65-request test from an approved test client.

---

## SEC-02 (Medium) — Student PII exposure through phone lookup

**Root cause:** exact-phone lookup returned full name, ID, stage, grade and registration source to any confirmed teacher; no endpoint-specific rate limit.

**Fix:**
- Unrelated teacher receives a masked name (`first + initial + …`), `masked=true`, `stage=null`, `grade=null`; no registration source.
- Full data only when an accepted, active relationship exists.
- `lookup` policy: 12/minute per authenticated teacher.
- Audit event `teacher.student.lookup` records match/masked counts (no phone/name).
- UI labels masked results and explains consent.

**Verification:** integration tests mask/reveal and rate-limit; live SQL Server run returned `masked=true`, `stage=null` for an unrelated teacher and full data for the linked teacher.

---

## SEC-03 (Medium) — CSP / API origin drift

**Root cause:** the deployed frontend build and CSP referenced `emtehani-api.vercel.app`; the current Vercel env and authoritative API are `emtehani.runasp.net`.

**Fix:**
- Authoritative origin is Monster.
- `scripts/verify-csp.mjs` is part of `npm run build`; it fails when `VITE_API_URL` is absent from `connect-src`, when `connect-src` contains `*`, when `script-src` lacks `'self'`, contains unsafe inline/eval, or when object/frame protections are missing.
- Unit test asserts the CSP includes Monster and no wildcard.
- Current repository CSP already allows Monster; the next Vercel build with the current env will produce a consistent JS/CSP pair.

**Verification:** guard rejects `https://wrong.example` (exit 1), accepts Monster (exit 0); production build succeeds; unit tests pass.

**Deployment requirement:** deploy frontend + `vercel.json` together. Until then, production continues to serve the old Vercel-origin build/CSP.

---

## SEC-04 (Low) — Weak password policy

**Fix:** 10–128 characters, common-password blocklist, whitespace guard; applied to registration and reset. UI mirrors the length rule.

**Verification:** `PasswordPolicy_RejectsShortAndCommonPasswords` (short/common rejected, strong accepted); frontend shows 10-character message.

**Residual:** existing legacy accounts with shorter passwords can still log in; consider a forced reset campaign if policy requires.

---

## SEC-05 (Low) — Tokens in localStorage

**Decision:** accepted risk, documented. Mitigations already present: strict CSP (`script-src 'self'`, no unsafe-inline/eval), no `dangerouslySetInnerHTML`/HTML sinks, short-lived access token (24 h session), refresh rotation/revocation.

**Recommended future work:** move refresh tokens to `HttpOnly; Secure; SameSite` cookies with CSRF protection, or use a BFF; keep access tokens in memory. This is a cross-cutting architecture change, not a small patch; it is not required to close the current findings.

---

## SEC-06 (Info) — Rate-limit coverage

**Fix:** global API limiter now covers all `/api` routes; lookup/result/export/audit policies added. Query/business endpoints can still be tuned.

**Residual:** limits are per process. Multi-instance deployments need a distributed cache/limiter.

---

## SEC-07 (Info) — Development reset-token disclosure

`forgot-password` returns `resetToken` only when SMTP is absent **and** `IsDevelopment()`. Production must run with `ASPNETCORE_ENVIRONMENT=Production` and SMTP configured. No change needed; documented as a production guard.

---

## SEC-08 (Info) — Public OpenAPI description

`/swagger/v1/swagger.json` is public by design (discovery metadata, Link header advertises it; UI disabled). No secrets are present. Keep as intentional; gate it behind `Swagger:Enabled` if policy changes.

---

## Controls re-verified after changes

| Control | Result |
|---|---|
| Unauthenticated `/api/auth/me`, `/api/exams`, `/api/teacher/overview` | 401 |
| Student → teacher endpoints | 403 |
| Student → admin endpoints | 403 |
| Cross-teacher exam access | 403/404 |
| Cross-student submission access | 403/404 |
| Student exam creation | 403 |
| Tampered JWT | 401 |
| Refresh rotation / logout revocation | pass |
| Password reset invalid token | 400 |
| CORS evil origin | no `Access-Control-Allow-Origin` |
| Security headers | CSP/HSTS/nosniff/DENY/no-referrer/no-store |
| CSV formula injection | `'` prefix for `= + - @` |
| SQL metacharacter search | safe (EF parameterization) |
| XSS sinks | none found |
| CSRF | not applicable (bearer tokens, no cookie auth) |
