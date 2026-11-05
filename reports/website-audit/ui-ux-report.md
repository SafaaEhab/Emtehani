# UI / UX and Responsive Report

**Design language:** Arabic-first RTL, Cairo/Readex Pro, restrained indigo palette, rounded cards, dark/light themes.
**Overall impression:** coherent and modern; empty states and onboarding copy are unusually clear. The main UX defects are information accuracy (attempt summary, dashboard counter), modal keyboard behaviour, and small-text contrast.

---

## Responsive matrix (measured, no horizontal overflow)

| Viewport | Page | Result | Evidence |
|---|---|---|---|
| 390×844 | Landing | Passed — no overflow; mobile menu opens | `mobile-390-landing.png`, `mobile-390-landing-menu.png` |
| 390×844 | Login | Passed — no overflow; form fits with 48 px submit | `mobile-390-login.png` |
| 390×844 | Student dashboard | Passed — cards stack; bottom nav fixed | `mobile-390-student-dashboard.png` |
| 768×1024 | Landing | Passed — no overflow | `tablet-768-landing.png` |
| 1366×768 | Landing | Passed — no overflow | `laptop-1366-landing.png` |
| 1920×1080 | Teacher dashboard | Passed — sidebar visible, content max-width, no overflow | `desktop-1920-teacher-dashboard.png` |

No horizontal scrollbar appeared at any tested width (`scrollWidth <= innerWidth` in all checks).

---

## Visual and interaction findings

### UI-01 — Mobile menu lacks a backdrop and full-height sheet (Low)

At 390 px the menu panel ends after the CTAs; the hero remains visible underneath and is cut mid-sentence. There is no dimming layer and no apparent background-scroll lock.

**Fix:** full-height overlay, translucent backdrop, `body { overflow:hidden }`, focus trap, and close on route selection.

### UI-02 — Key numbers contradict each other (Medium / High trust impact)

- Student details: **0 used / 0 completed** after a graded 10/10 attempt (BUG-01).
- Dashboard: **1 openable session** while the only exam is **منتهي** (BUG-02).
- Audit: **0 submissions** after a real submission (BUG-03).

Users will trust these numbers more than they should. Fix the data before polishing visuals.

### UI-03 — Nested pages lose their title (Low)

Student **مراجعة** and teacher **تصحيح الأسئلة المقالية** render the H1 “امتحاني” instead of a descriptive title (BUG-04).

### UI-04 — Modal keyboard experience is broken (Medium accessibility)

Tab escapes into the background navigation after the last modal button; focus is not restored on close. See BUG-05. This affects the question-type picker, confirmations, student details, and add-student dialogs.

### UI-05 — Small helper text is below AA contrast (Low)

Login helper/footer text: **4.44:1** at 10.5 px (`#64748b` on `#f6f7fb`). Landing also uses 9–10.5 px labels extensively; several are `text-subtle`. Lighthouse flagged landing contrast during the initial theme transition (transient) and login steady-state (persistent).

**Fix:** raise `--app-subtle` to ≈`#5b6b82`; avoid going below 11 px for meaningful copy.

### UI-06 — Heading hierarchy skips levels on landing (Low)

Heading sequence begins `H1 → H3 → H2 → H3…` inside the dashboard mock. Promotes the mock section heading to H2 or mark the mock `aria-hidden` if it is purely decorative.

### UI-07 — Logo accessible name does not match visible text (Low)

The logo button visibly reads “امتحاني EMTEHANI” but its accessible name is “العودة إلى الأعلى”. Screen-reader users hear an action, not the brand. Use a combined label.

### UI-08 — Theme switch flashes low contrast (Low)

On first paint, theme variables and element `transition duration-300` can briefly produce light ink on a white card or dark surface with light-theme muted text. Lighthouse captured 1.16:1 during this window. In steady state both themes are legible and attractive.

**Fix:** set `data-theme` before first paint (inline script in `index.html`) and exclude color/background from long transitions, or disable transitions on initial mount.

### UI-09 — Desktop sidebar appears only at ≥1024 px; bottom nav is used below that (Passed)

At 912 px the app correctly used the mobile layout; at 1920 px the right-side 280 px sidebar appeared. No overlap with the sticky header.

### UI-10 — Empty states are clear and useful (Passed)

Examples: “لا توجد امتحانات متاحة الآن”, “لا يوجد طلاب حاضرون بعد”, “لم تسلّم أي امتحان بعد”. They explain the next step rather than only saying “empty”.

### UI-11 — RTL, numerals, and bidirectionality (Passed)

`html dir="rtl" lang="ar"`; phone/e-mail/CSV fields use LTR helpers; Arabic text and Latin technology tags coexist without visible bidi breakage. Time/date strings use Arabic locale formatting.

### UI-12 — Touch targets (Passed)

Primary buttons are 44–48 px high; bottom-nav items are 48 px; icon buttons are 32–40 px with labels. The modal close button is 32 px, slightly below the 44 px recommendation.

---

## Visual evidence

| File | What it shows |
|---|---|
| `production-landing-desktop.png` | Full landing, dark theme |
| `production-login-desktop.png` | Login page, empty form |
| `production-register-desktop.png` | Student registration form |
| `production-developer-portfolio.png` | Developer portfolio |
| `public-teacher-portfolio.png` | Published teacher page, contacts hidden |
| `teacher-subscription-confirmed.png` | Free plan confirmed state |
| `desktop-1920-teacher-dashboard.png` | Desktop sidebar + dashboard stats |
| `mobile-390-student-dashboard.png` | Student dashboard stacking |
| `mobile-390-landing.png` / `mobile-390-login.png` | Mobile landing/login |
| `mobile-390-landing-menu.png` | Open mobile menu (UI-01) |
| `tablet-768-landing.png` / `laptop-1366-landing.png` | Tablet/laptop layouts |
| `lighthouse-*.html` | Accessibility/SEO evidence |

---

## UX recommendations (ordered)

1. Fix the three misleading counters (BUG-01/02/03) before visual polish.
2. Fix modal focus trapping and restoration (BUG-05) — keyboard users are currently blocked.
3. Add a proper mobile menu overlay with scroll lock and backdrop (UI-01).
4. Fix nested page titles and the logo accessible name.
5. Raise small-text contrast and the minimum helper text size.
6. Move theme initialization before first paint to remove the flash.
7. Consider a route-based URL model so refresh/share/back work (BUG-15).
