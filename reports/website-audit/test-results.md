# Test Results Register

**Totals:** 100 documented checks · **90 executed** · **79 passed** · **11 failed** · **2 blocked** · **8 not run**.
Counts are checks (a check may contain several assertions). Automated backend tests are reported separately at the end.

**Environment:** public pages/security/performance on production; full workflows on the local Development stack with disposable synthetic accounts. No production records were modified.

---

## A. Public & authentication (T01–T20) — 20 passed

| ID | Check | Status | Evidence / notes |
|---|---|---|---|
| T01 | Landing loads with Arabic RTL, H1, title | **PASS** | Production; `dir=rtl`, `lang=ar`, screenshot |
| T02 | Landing header/footer links and anchors present | **PASS** | hrefs inspected; login CTA clicked |
| T03 | Theme toggle dark ↔ light | **PASS** | Computed colors verified; transient flash noted |
| T04 | Mobile menu opens/closes | **PASS** | 390 px; screenshot; overlay issue noted |
| T05 | Developer portfolio loads | **PASS** | `?portfolio=1`; headings/links render |
| T06 | Login empty submission validation | **PASS** | “أدخل بريداً إلكترونياً صحيحاً.” |
| T07 | Login invalid e-mail validation | **PASS** | `not-an-email` blocked client-side |
| T08 | Login short password validation | **PASS** | “كلمة المرور يجب أن تكون بين 8 و128 حرفاً.” |
| T09 | Login wrong credentials | **PASS** | Generic 401 Arabic message; no enumeration |
| T10 | Login success — teacher | **PASS** | Dashboard + subscription state |
| T11 | Login success — student | **PASS** | Student dashboard |
| T12 | Login e-mail trim/lowercase | **PASS** | Uppercase + spaces accepted |
| T13 | Logout | **PASS** | Token/user/refresh cleared; landing |
| T14 | Session persists across reload | **PASS** | Dashboard restored from `/api/auth/me` |
| T15 | Session expiry handling | **PASS** | Corrupted token + no refresh → auth + notice |
| T16 | Refresh-token rotation | **PASS** | Old token 401; rotated token 200; logout 401 |
| T17 | Register empty submission validation | **PASS** | 4-part name error shown first |
| T18 | Student registration success | **PASS** | Auto-confirmed in Development; code generated |
| T19 | Teacher registration success | **PASS** | Auto-login → subscription page |
| T20 | Forgot password flow | **PASS** | Empty/invalid validation; unknown e-mail generic 200 |

## B. Teacher workflows (T21–T42) — 21 passed, 1 failed

| ID | Check | Status | Evidence / notes |
|---|---|---|---|
| T21 | Pending teacher blocked from workspace | **PASS** | 403 `subscription_pending` |
| T22 | Confirm Free plan | **PASS** | 200; “مؤكد”; audit event |
| T23 | Teacher dashboard statistics render | **PASS** | Cards + latest exams; one counter defect below |
| T24 | Exams list renders/filters controls | **PASS** | 1 exam, status Active then Finished |
| T25 | Create exam validation | **PASS** | 400 for duration 0/301, availability order, bad release mode |
| T26 | Create exam success | **PASS** | 201; 2 questions, 10 points, 45 min |
| T27 | Question type modal (6 types) | **PASS** | Opened; essay selected; focus trap defect below |
| T28 | Publish exam | **PASS** | PUT 200; status waiting |
| T29 | Open session | **PASS** | PUT 200; Active |
| T30 | Live roster updates | **PASS** | 0 → 1 participant, 10/10 after grading |
| T31 | End session confirmation | **PASS** | Dialog + confirm; status Finished |
| T32 | Results auto-publish (`AfterExamEnd`) | **PASS** | Published timestamp shown; student sees score |
| T33 | Results CSV export | **PASS** | Download 190 bytes; UTF-8 BOM; correct columns |
| T34 | Grant extra attempt | **PASS** | Confirm + success; allowed on finished exam (defect) |
| T35 | Essay review + grade + feedback | **PASS** | 5/5; final 10/10; note stored |
| T36 | Accept student link request | **PASS** | PUT 200; student immediately eligible |
| T37 | Students list + status/filters | **PASS** | 1 linked student, active |
| T38 | Student details / attempt summary | **FAIL** | Shows 0 used/0 completed after 10/10 (BUG-01) |
| T39 | Add student phone search validation | **PASS** | Empty → “أدخل رقم هاتف صحيحاً…” |
| T40 | Teacher invitation form validation | **PASS** | Empty name error; 6 fields present |
| T41 | Teacher profile save/publish/public page | **PASS** | API saved + published; `/t/teacher`; contacts hidden |
| T42 | Audit log renders events | **PASS** | 9 events; missing submission events (BUG-03) |

## C. Student workflows (T43–T55) — 13 passed

| ID | Check | Status | Evidence / notes |
|---|---|---|---|
| T43 | Available exams empty before link acceptance | **PASS** | 0 exams + clear empty state |
| T44 | Available exam appears after acceptance | **PASS** | 1 exam, correct teacher/stage/grade |
| T45 | Enter exam by account | **PASS** | POST enter 200 |
| T46 | Lobby / start transition | **PASS** | “تم فتح الجلسة” → exam room |
| T47 | MCQ answer + autosave | **PASS** | Selected “4”; “تم الحفظ” |
| T48 | Essay answer + autosave | **PASS** | Textarea saved; status updated |
| T49 | Submit review modal | **PASS** | 2 answered, 0 unanswered, answers listed |
| T50 | Final submit | **PASS** | Submission accepted; results pending state |
| T51 | Results hidden before publication | **PASS** | “بانتظار موعد إظهار النتائج” |
| T52 | Results visible after publication | **PASS** | 10/10, 100%, Reviewed |
| T53 | Review detail with correct answers/note | **PASS** | 1 correct, 0 wrong, 1 essay, teacher note |
| T54 | Teachers page / request limit | **PASS** | Accepted teacher shown; 1/1 limit enforced |
| T55 | Academic profile validation | **PASS** | Empty name rejected; code read-only; history shown |

## D. API & security (T56–T75) — 16 passed, 4 failed

| ID | Check | Status | Evidence / notes |
|---|---|---|---|
| T56 | Unauthenticated protected endpoints | **PASS** | 401 for me/exams/overview |
| T57 | Role enforcement | **PASS** | student→teacher 403; teacher→admin 403 |
| T58 | Cross-teacher tenant isolation | **PASS** | 403/404 on get/publish/export/state |
| T59 | Student IDOR on submission | **PASS** | 403 review; 404 autosave/submit |
| T60 | Student cannot create exam | **PASS** | 403 with valid payload |
| T61 | Tampered JWT rejected | **PASS** | 401 |
| T62 | SQL-metacharacter search | **PASS** | Safe empty result; no 500 |
| T63 | CORS with evil origin | **PASS** | No ACAO on simple/preflight |
| T64 | Security headers | **PASS** | CSP/HSTS/nosniff/DENY/no-referrer/no-store |
| T65 | Rate limit on normal IP | **PASS** | 60×401 → 429 at 61; Retry-After 60 |
| T66 | Rate limit with rotating X-Forwarded-For | **FAIL** | 65×401, 0×429 (SEC-01) |
| T67 | Teacher PII lookup by phone | **FAIL** | Unrelated teacher got full name/stage/grade/id (SEC-02) |
| T68 | Password strength policy | **FAIL** | Length-only; 8-char weak accepted (SEC-04) |
| T69 | CSV formula-injection escaping | **PASS** | `'` prefix for `= + - @`; BOM present |
| T70 | Admin endpoints with non-admin tokens | **PASS** | 403 |
| T71 | Input validation status codes | **PASS** | 400s for duration/order/release/name/phone/password |
| T72 | No unexpected 5xx during valid flows | **PASS** | No 500 responses in logs |
| T73 | CSP/API origin consistency | **FAIL** | Repo CSP ≠ production API host (SEC-03) |
| T74 | Public discovery metadata | **PASS** | api-docs, catalogs, agent.md, llms.txt 200 |
| T75 | Consistent error envelope | **PASS** | problem+json/message + status/traceId |

## E. UI, responsive, accessibility, performance (T76–T90) — 9 passed, 6 failed

| ID | Check | Status | Evidence / notes |
|---|---|---|---|
| T76 | 390 px landing | **PASS** | No overflow; screenshot |
| T77 | 390 px login | **PASS** | No overflow; form centered |
| T78 | 390 px student dashboard | **PASS** | Cards stack; bottom nav |
| T79 | 768 px landing | **PASS** | No overflow |
| T80 | 1366 px landing | **PASS** | No overflow |
| T81 | 1920 px dashboard | **PASS** | Sidebar + content; no overflow |
| T82 | Lighthouse landing | **FAIL** | A11y 93; contrast/heading/logo/robots failures |
| T83 | Lighthouse login | **PASS** | A11y 96; best practices 100 |
| T84 | Modal focus containment | **FAIL** | Focus escapes to background (BUG-05) |
| T85 | Heading order + logo accessible name | **FAIL** | H1→H3; label mismatch (BUG-09) |
| T86 | robots.txt validity | **FAIL** | Unknown `Agentmap:` directive (BUG-10) |
| T87 | Production login cold start | **FAIL** | 9.9–10.3 s first request (BUG-12) |
| T88 | Bundle size measurement | **PASS** | 780 KB JS / 206 KB transfer (INFO) |
| T89 | Console errors on landing/login | **PASS** | None; React DevTools info only |
| T90 | Failed network requests | **PASS** | Only expected 401/409; no asset failures |

## F. Blocked and not run (T91–T100)

| ID | Check | Status | Reason |
|---|---|---|---|
| T91 | Admin dashboard UI | **BLOCKED** | No admin account or bootstrap credentials; API 403 |
| T92 | Admin subscription settings | **BLOCKED** | Same |
| T93 | Live timer expiry + auto-submit | **NOT RUN** | Would require waiting out a 45-min exam; backend test covers finalization |
| T94 | Concurrent autosave vs submit | **NOT RUN** | Covered by backend integration test; not reproduced in browser |
| T95 | Availability window enforcement | **NOT RUN** | Backend test exists; live window not configured |
| T96 | Exam ban / suspend / reactivate | **NOT RUN** | Destructive relationship actions intentionally not confirmed |
| T97 | Invitation-token registration E2E | **NOT RUN** | Form validation tested; full invite consumption not executed |
| T98 | Legacy join-by-code endpoint | **NOT RUN** | Backend tests cover student-code join; not exercised UI |
| T99 | Manual results publication (legacy) | **NOT RUN** | Auto-publish mode was used; manual publish path not exercised |
| T100 | Teacher profile UI save | **NOT RUN** | API save/publish verified; UI save not completed due to automation misclick |

---

## Automated backend tests

`dotnet test backend/Emtehani.slnx`:

- **73 passed**
- **5 skipped** (PostgreSQL-only schema tests; no isolated PostgreSQL instance configured)
- **0 failed**
- Duration: ~20 seconds

Coverage highlights: registration (student/teacher, production mode), exam lifecycle, idempotent submission, autosave-after-submit rejection, essay review IDs, results publication, teacher ownership, student enrollment, availability windows, suspension/bans, invitation flow, admin reset protections, rate limiting per account, concurrency, expired attempts, refresh/session fixed duration, academic history.

## Existing frontend E2E suite

`npm run test:e2e` was **not run**: it requires an isolated PostgreSQL 17 database (`E2E_DATABASE_CONNECTION`) and starts API/frontend on ports 5099/4173. No PostgreSQL instance was available. The basic `qa-running-audit.spec.js` targets the running services but only checks landing/health/root; those were exercised manually here.

## Pass/fail summary

| Status | Count |
|---|---:|
| PASS | 79 |
| FAIL | 11 |
| BLOCKED | 2 |
| NOT RUN | 8 |
| **Total** | **100** |
