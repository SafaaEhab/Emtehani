# Routes & Pages Inventory

**Discovered:** 29 pages/views + 10 modal/dialog surfaces.
**Status legend:** Passed · Failed · Blocked · Partially tested · Not tested.

## Routing model

The application is a **state-driven SPA** (React state, no client router). Only the following real URLs exist:

| URL pattern | Behaviour |
|---|---|
| `/` | Landing page (or dashboard when a valid session exists) |
| `/t/:slug` | Public teacher portfolio (Vercel rewrite to `index.html`) |
| `/?portfolio=1` | Developer portfolio |
| `/?invite=...` | Opens registration with a teacher invitation |
| `/?resetToken=...` | Opens password reset |
| Any other path | Vercel returns **404** (`text/plain`) — verified for `/dashboard`, `/admin`, `/does-not-exist-xyz`, `/t/invalid-slug-xyz` |

Because workspace pages do not change the URL, browser Back/Forward and refresh do not restore the current workspace view. This is reported as a Low UX finding (BUG-15).

---

## Public pages

| # | Page / view | URL | Test status | Notes |
|---:|---|---|---|---|
| 1 | Landing | `/` | **Passed** | H1, RTL, CTAs, anchors, theme toggle, desktop/mobile/tablet/laptop |
| 2 | Login | state (`auth`, login mode) | **Passed** | Empty, invalid e-mail, short password, wrong credentials, success |
| 3 | Register — student | state (`auth`, register mode) | **Passed** | 4-part name, stage/grade, phones, teacher selection, consent |
| 4 | Register — teacher | state | **Passed** | Registration, auto-login, Free plan flow |
| 5 | Forgot password | state | **Passed** | Empty/invalid validation, generic unknown-email response |
| 6 | Developer portfolio | `/?portfolio=1` | **Passed** | Sections, links, scroll reveal, mobile |
| 7 | Public teacher portfolio | `/t/teacher` (test slug) | **Passed** | Published profile renders; e-mail/phone hidden by default |
| 8 | 404 | any invalid path | **Passed** | Vercel 404; plain text, not an app shell |

## Student pages

| # | Page / view | Test status | Notes |
|---:|---|---|---|
| 9 | Dashboard | **Passed** | 0 exams before link, 1 after acceptance, stats, latest results |
| 10 | Available exams | **Passed** | Published + matching stage/grade + accepted link required |
| 11 | Results | **Passed** | Hidden before publication; 10/10 after publication |
| 12 | Review detail | **Passed** | Score, correct answers, teacher note; header title defect (BUG-04) |
| 13 | Teachers | **Passed** | Accepted/pending states; teacher-limit error at 1/1 |
| 14 | Academic profile | **Passed** | Read, empty-name validation, student code read-only, academic history |
| 15 | Lobby | **Partially tested** | Entered an already-open session. Waiting-before-open state not exercised live. |
| 16 | Exam room | **Passed** | Timer, question navigation, MCQ, essay, autosave, review modal, submit |

## Teacher pages

| # | Page / view | Test status | Notes |
|---:|---|---|---|
| 17 | Dashboard | **Passed** | Stats, live sessions empty state; "openable sessions" counter defect (BUG-02) |
| 18 | Exams list | **Passed** | Published/active/finished rows, actions |
| 19 | Create exam | **Passed** | Validation, stage/grade, 2 question types, modal, save success |
| 20 | Exam detail / session management | **Passed** | Publish, open, live roster, end session, results, export |
| 21 | Results & analytics | **Passed** | 10/10 row, CSV export, extra-attempt action |
| 22 | Essays list | **Passed** | Pending review list, open grading |
| 23 | Essay review | **Passed** | 5/5 grade + feedback; header title defect (BUG-04) |
| 24 | Student requests | **Passed** | Pending → accepted; counters |
| 25 | Students management | **Passed** | List, filters, details, add/search/invite validation; attempt summary defect (BUG-01) |
| 26 | Teacher profile | **Partially tested** | UI fields inspected; save/publish verified through the API; UI save not completed |
| 27 | Subscription | **Passed** | Free plan confirm, status cards, success message |
| 28 | Audit log | **Passed** | 9 events, pagination UI; missing submission events (BUG-03) |

## Admin pages

| # | Page / view | Test status | Notes |
|---:|---|---|---|
| 29 | Admin dashboard | **Blocked** | No admin account/bootstrap configured; `/api/admin/*` returns 403 for teacher/student |
| 30 | Admin teachers & subscriptions | **Blocked** | Same reason |

> The admin component exists but both admin navigation entries render the same `AdminDashboard` component; it was not reachable in this environment.

---

## Modals and dialogs

| # | Dialog | Status | Notes |
|---:|---|---|---|
| M1 | Question type picker | **Passed** | 6 types; focus escapes dialog (BUG-05) |
| M2 | Exam save success | **Passed** | "إدارة الامتحان" opens exam detail |
| M3 | Submit review / final submit | **Passed** | 2 answered, 0 unanswered, final submit |
| M4 | End session confirmation | **Passed** | Warning + confirm; session finished |
| M5 | Grant extra attempt | **Passed** | Confirm + success; allowed on a finished exam (BUG-14) |
| M6 | Student details | **Passed** | Guardian details, attempts, linked exams |
| M7 | Add student — phone search | **Passed** | Empty/invalid phone validation |
| M8 | Add student — invitation | **Passed** | 6 fields, empty validation |
| M9 | Ban from exam | **Not tested** | No non-finished exam available at test time |
| M10 | Suspend / unlink confirmation | **Not tested** | Destructive actions intentionally not confirmed |

---

## Totals

- **Pages/views discovered:** 29
- **Reached and exercised:** 27 (including 1 partially tested teacher profile)
- **Fully passed:** 26
- **Failed (defects found on the page):** 4 (students details, dashboard, audit, nested review headers)
- **Blocked:** 2 admin views
- **Modal surfaces:** 10 discovered; 8 tested, 2 not tested
