# Security Assessment Report

**Scope:** production frontend/API plus a local Development instance for safe, disposable workflow tests.
**Method:** authenticated and unauthenticated API probing, role/tenant isolation tests, header/CORS inspection, source review, and targeted rate-limit tests. No destructive payloads, no third-party systems, no real user data extracted, no production records modified.

**Risk rating: Moderate.** Authorization is strong; the two material issues are a **rate-limit bypass** and **PII exposure through the teacher phone lookup**.

---

## Findings

### SEC-01 — Rate limiting is bypassable via spoofed `X-Forwarded-For` (High)

**Evidence**

| Test | Result |
|---|---|
| 65 login requests, same connection, no forwarded header | 60 × 401, **5 × 429**; first 429 at attempt 61; `Retry-After: 60` |
| 65 login requests, a different `X-Forwarded-For` each time | **65 × 401, 0 × 429** |

The same limiter guards login, registration, forgot-password/reset and other IP-keyed policies. An attacker can rotate a spoofed header to avoid throttling and brute-force credentials or spam password-reset e-mails.

**Root cause:** `ForwardedHeadersOptions.KnownProxies.Clear()` and `KnownIPNetworks.Clear()` trust all proxies; `ClientKey` uses the rewritten `RemoteIpAddress`.

**Remediation**
1. Configure `KnownProxies`/`KnownNetworks` for the actual edge (Vercel egress, load balancer) and reject untrusted `X-Forwarded-For` values.
2. Strip inbound forwarded headers at the edge.
3. Add per-account throttling and exponential backoff/lockout after repeated failures, independent of IP.
4. Keep a conservative global limit for unauthenticated endpoints.

**Verification after fix:** repeat both tests; the rotating-header run must also return 429.

---

### SEC-02 — Teacher phone lookup exposes student PII without endpoint throttling (Medium)

**Evidence**

A newly created teacher account (Free plan, no relationship to the student) called:

`GET /api/teacher/students/lookup?phone=<exact student phone>`

and received:

- full name
- internal `studentId`
- stage and grade
- registration source
- prior request status for that teacher

The endpoint has no `RequireRateLimiting` policy, so it is not covered by the 120/min API limiter or the 10/min join limiter. Exact-phone matching limits blind guessing, but Egyptian mobile numbers are enumerable enough to make bulk probing practical, especially combined with SEC-01.

**Remediation**
- Return a masked name and a boolean existence flag, not academic data, until the student accepts a link.
- Apply a strict per-teacher limit (e.g. 10/min) and audit every lookup.
- Consider requiring the student’s full name plus phone, or a teacher-issued invitation token, for the lookup stage.

---

### SEC-03 — CSP drift between repository and production (Medium, deployment risk)

**Observation**

| Source | `connect-src` |
|---|---|
| Production response | `'self' https://emtehani-api.vercel.app` |
| `frontend/vercel.json` in the working tree | `'self' https://emtehani.runasp.net` |

Production works only because the deployed/dashboard configuration differs from the committed file. A redeploy from this tree could silently block all API traffic (login, exams, results).

**Remediation**

- Make the CSP derive from the same `VITE_API_URL` used by the build.
- Remove reliance on dashboard-only overrides; add a deploy smoke test that asserts the CSP contains the built API origin.
- Keep a single documented API hostname across README, `.env.example`, CSP, Link headers, and deployment docs.

---

### SEC-04 — Password policy is length-only (Low)

Registration and reset accept any 8–128 character password; there is no complexity, breach-list, or common-password check. Example: an 8-character all-lowercase password is accepted. Combined with SEC-01, credential guessing is easier.

**Remediation:** require 10–12+ characters, reject the top breached/common passwords, and add a strength meter. Do not impose arbitrary character-class rules that reduce entropy.

---

### SEC-05 — Tokens are stored in `localStorage` (Low / accepted risk)

Both access and refresh tokens are readable by JavaScript. The strict CSP (`script-src 'self'`) and the absence of `dangerouslySetInnerHTML`/HTML sinks materially reduce XSS risk, but any future XSS would allow full session theft.

**Remediation options:** move refresh tokens to `HttpOnly; Secure; SameSite` cookies with CSRF protection, keep short-lived access tokens in memory, or retain localStorage but enforce a strict CSP/Trusted Types and add token-binding/rotation alerts.

---

### SEC-06 — Explicit rate-limit coverage is low and inconsistent (Info → hardening)

The parser found **76 route declarations and only 18 with an explicit `RequireRateLimiting` policy**. Sensitive examples without endpoint-specific limits include:

- `GET /api/teacher/students/lookup`
- `POST /api/teacher/students/invite`
- `GET /api/exams/{id}/results`
- `GET /api/exams/{id}/export`
- `GET /api/audit`

Add a named policy per risk class (auth, lookup, export, write) rather than relying on per-endpoint copy-paste.

---

### SEC-07 — Development-only password-reset token disclosure (Info)

When SMTP is not configured **and** the environment is Development, `POST /api/auth/forgot-password` returns the reset token in the JSON response for testing. This is gated by `IsDevelopment()` and was not observed in production, but it must remain impossible to enable accidentally in production.

**Recommendation:** add a startup assertion in Production that `ASPNETCORE_ENVIRONMENT=Production` and SMTP is configured, and a test that the response never contains `resetToken` outside Development.

---

### SEC-08 — Public OpenAPI description (Info, by design)

`/swagger/v1/swagger.json` is public on both API hosts; the interactive UI is disabled (`/swagger` → 404). The endpoint list and schemas are discoverable. This appears intentional (the Link header advertises the OpenAPI document), and no secrets are present. Treat it as intentional discovery metadata; if not, gate it with the same `Swagger:Enabled` flag.

---

## Controls that passed

| Control | Result | Evidence |
|---|---|---|
| Unauthenticated access to protected APIs | **401** | `/api/auth/me`, `/api/exams`, `/api/teacher/overview` |
| Role enforcement | **403** | student → teacher/admin APIs; teacher → admin APIs |
| Teacher tenant isolation | **403/404** | teacher1 cannot read/publish/export teacher2's exam, cannot read teacher2's student |
| Student IDOR on submissions | **403/404** | student2 cannot read, autosave, or submit student1's attempt |
| Student cannot create exams | **403** | valid exam payload with student token |
| Admin endpoints | **403** | teacher and student tokens |
| Tampered JWT signature | **401** | modified access token |
| Refresh-token rotation | **pass** | old token rejected after first refresh; logout invalidates |
| Password reset token | **pass** | invalid token → 400; reset clears refresh tokens |
| SQL injection signal | **pass** | search with `' OR 1=1--` returned a safe empty result; EF parameterization used |
| XSS sinks | **pass by inspection** | no `dangerouslySetInnerHTML`/`innerHTML`; React escaping; strict CSP |
| CSRF | **not applicable** | bearer tokens in Authorization headers, no cookie auth |
| CSV formula injection | **pass** | `= + - @` cells prefixed with `'` in both exports |
| CORS | **pass** | evil origin receives no `Access-Control-Allow-Origin` on simple request or preflight |
| Security headers | **pass** | CSP, HSTS, nosniff, DENY, no-referrer, Permissions-Policy, `no-store` on API |
| Account enumeration on login | **pass** | wrong e-mail and wrong password both generic 401 |
| Forgot-password enumeration | **pass** | unknown e-mail returns the same generic message |
| Registration duplicate e-mail | **pass** | 409, generic enough for this context |
| File upload safety | **not applicable** | no binary uploads; avatar/cover are HTTPS URL fields validated server-side |
| Query pagination bounds | **pass by source** | page ≥ 1, pageSize clamped (1–100) |

---

## Residual risk

| Risk | Likelihood | Impact | Rating |
|---|---|---|---|
| Credential stuffing / password spraying | Medium | High | **High** while SEC-01 is open |
| Student PII harvesting by a teacher | Medium | Medium | **Medium** |
| CSP break on redeploy | Low | High | **Medium** |
| Token theft via future XSS | Low | High | **Low–Medium** (strict CSP) |
| Session replay after device theft | Low | Medium | **Low** (24 h fixed session) |

---

## Recommendations (priority order)

1. **Fix SEC-01 before scaling** — trusted proxy configuration + per-account lockout/backoff.
2. **Fix SEC-02** — mask PII in lookup, add per-teacher rate limit and audit.
3. **Fix SEC-03** — synchronise CSP/API origin and add a deploy assertion.
4. Harden passwords (SEC-04), add endpoint policies (SEC-06), and keep development-only reset tokens behind a production guard (SEC-07).
5. Add automated security regression tests for the authorization matrix, CORS, and rate limits (including spoofed headers) to CI.
