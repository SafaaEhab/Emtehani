# API & Backend Report

**Stack:** ASP.NET Core 10 minimal APIs, EF Core, JWT bearer auth, rate limiting, SQL Server/PostgreSQL.
**Base URL:** production `https://emtehani-api.vercel.app`; local test `http://127.0.0.1:5000`.
**Route declarations found:** 78 `Map*` calls (76 parsed into the grouped inventory below).

Full live matrix: `reports/website-audit/evidence/api-test-results.txt`.

---

## 1. Authentication and session model

- JWT HMAC-SHA256, issuer/audience validated, 24-hour fixed session, 30 s clock skew.
- PBKDF2-SHA256 (120,000 iterations, 16-byte salt, 32-byte output), fixed-time comparison.
- Refresh token: 32 random bytes, stored as SHA-256 hash, rotated on every refresh, revoked on logout and password reset.
- Login response includes access token, refresh token, role, subscription status, student code, stage/grade, session expiry and server UTC.
- Login normalizes e-mail (trim + lowercase) and returns generic 401 for unknown e-mail or wrong password (no account enumeration).
- E-mail verification: 32-byte random token, 24 h; Development auto-confirms only when SMTP is absent.
- Password reset: 32-byte token, 1 h, single use, clears refresh tokens.
- Production startup fails fast if `FrontendOrigin` or `PublicApiUrl` are invalid, or if `Jwt:Key` is shorter than 32 bytes.

**Verified:** valid login, wrong password, case/space normalization, refresh rotation, old-refresh rejection, logout revocation, tampered-JWT rejection.

---

## 2. Endpoint groups and results

| Group | Representative endpoints | Live result |
|---|---|---|
| Health/root | `GET /health`, `GET /api` | **Passed** 200 |
| Public discovery | `/api/public/teachers`, `/api/public/teachers/{slug}`, `/api/public/invitations/{token}`, robots/sitemap/agent docs | **Passed** 200; invalid invitation not exercised |
| Auth | register, login, refresh, logout, me, forgot/reset, resend, verify | **Passed** except `verify-email` (needs a real token) |
| Student profile | `GET/PUT /api/student/profile`, link requests, education profile | **Passed** / **Partial** |
| Exam creation | `POST /api/exams` | **Passed**; student → 403; invalid payloads → 400 |
| Exam management | list, get, publish, availability, state, results, export, participants | **Passed** except availability/regenerate/exports not exercised |
| Teacher student management | list, detail, lookup, invite, requests, bans, suspend, reactivate, unlink | **Passed** for list/detail/lookup/requests; destructive/bans not run |
| Teacher profile | get, update, publish, public profile | **Passed**; unpublish not exercised live |
| Subscriptions | plans, current, select | **Passed** |
| Admin | overview, settings, reset | **403** for teacher/student; **Blocked** for admin (no account) |
| Audit | `GET /api/audit` | **Passed**; teacher 200, student 403 |
| Submission | answers, submit, review, grade, extra attempt, events | **Passed** except focus-loss events |
| Results | mine, exam results, CSV | **Passed** |

---

## 3. Status codes observed

| Code | Context |
|---|---|
| **200** | login, refresh, logout, profile, list/detail, publish, state, results, export, grade, subscription |
| **201** | exam creation |
| **204** | CORS preflight |
| **400** | missing/invalid body, duration 0/301, availability end before start, invalid release mode, invalid phone, weak password, 3-part student name |
| **401** | missing/tampered/expired token, wrong credentials, reused/revoked refresh token |
| **403** | wrong role, pending teacher subscription, cross-tenant reads, student reading teacher results/audit/participants |
| **404** | unknown IDs, malformed GUID, other teacher's mutable resources, invalid public slug |
| **405** | `GET /api/admin/reset` (POST only in non-production) |
| **409** | duplicate e-mail, duplicate relationship request, teacher limit reached, finished exam entry, duplicate grant |
| **429** | 61st login attempt from one IP; `Retry-After: 60` |
| **500** | **none observed**; logs contained no 500 responses |

Error format is consistent (`message`/`title`/`status`/`traceId` in problem responses). Arabic messages are used throughout.

---

## 4. Validation quality

Strong server-side validation was observed for:

- exam title, duration (1–300), attempts (1–20), question types, points, MCQ options/correct answer, true/false values, matching pairs;
- availability ordering and results-release mode;
- student 4-part name, stage/grade pairing, unique phone, parent phone differs;
- teacher count 1–20 and selected teacher ids exist/confirmed;
- e-mail format/length, password 8–128;
- teacher profile HTTPS-only URLs, slug regex, social links;
- pagination clamps and page ≥ 1.

**Gaps**

- Password is length-only (no strength/breach check).
- A few destructive/high-value endpoints have no endpoint-specific rate limit (see Security report).
- `GET /api/teacher/students/lookup` returns more PII than the task requires.
- UI relies on JavaScript validation; inputs do not have the `required` attribute (accessibility, not security).

---

## 5. Pagination, filtering, sorting

- `/api/exams`: `page`, `pageSize` (1–100), `search`, `status`; invalid page values are clamped.
- `/api/exams/{id}/results`: `page`, `pageSize` (1–100).
- `/api/exams/{id}/participants`: `page`, `pageSize` (1–100).
- `/api/audit`: `page`, `pageSize` (1–100).
- `/api/admin/overview`: `search`, `planStatus`, `sort`, pagination.
- Student/teacher lists are unpaginated but bounded by relationship counts.
- Search with SQL metacharacters returned a safe empty result; EF parameterization is in use.

Only the exams list pagination was exercised live; other pagination paths were verified by source and are marked partial.

---

## 6. Notable API findings

| ID | Severity | Finding |
|---|---|---|
| API-01 | High | Login/auth rate limit bypass via spoofed `X-Forwarded-For` (SEC-01) |
| API-02 | Medium | Student PII lookup by phone has no endpoint rate limit (SEC-02) |
| API-03 | Medium | Teacher student detail returns 0 attempts for finished exams (BUG-01) |
| API-04 | Medium | Production cold start ~10 s (PERF-01) |
| API-05 | Low | Audit endpoint hides student-originated submission events from teachers (BUG-03) |
| API-06 | Low | Only 18 of 76 parsed routes declare an explicit rate-limit policy |
| API-07 | Low | Extra attempt can be granted for a `Finished` exam (BUG-14) |
| API-08 | Info | Public OpenAPI JSON; intended discovery metadata, no secrets |
| API-09 | Info | Two live API hosts (`emtehani-api.vercel.app`, `emtehani.runasp.net`) and CSP/docs disagree (SEC-03) |
| API-10 | Info | Export endpoints load all rows into memory; stream for large cohorts |

---

## 7. Data integrity observations

- Exam, question, participant, submission, answer and result records persisted correctly through the full lifecycle.
- Attempt/score values matched: 5 objective + 5 essay = 10/10; correct-answer review showed 1 correct, 0 wrong, 1 essay, 0 pending.
- CSV file was valid UTF-8 with BOM; Arabic name bytes were correct (`D8 B3…`), and formula-injection prefixes are implemented.
- The only data consistency defect confirmed is the teacher-side attempt summary (BUG-01).
- No orphaned or duplicate submissions were observed during the single-user flow; concurrency paths are covered by backend tests but not re-run live.

---

## 8. Recommendations

1. Fix the trusted-proxy/rate-limit configuration (SEC-01) and add per-account failure backoff.
2. Return masked/minimal PII from the phone lookup and rate-limit it.
3. Aggregate attempt history across all exams, including finished ones.
4. Move startup schema work out of the request path to reduce cold starts.
5. Add an endpoint policy registry so every route has an explicit rate-limit class.
6. Add contract tests for status codes and error shapes; include the authorization matrix in CI.
