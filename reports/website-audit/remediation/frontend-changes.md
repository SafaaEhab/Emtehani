# Frontend Changes

**Stack:** React 19.3 + Vite 8.3 + Tailwind CSS 4 + Motion.
**Authoritative API:** `https://emtehani.runasp.net` (MonsterASP.NET).
**Production host:** Vercel project `emtehani`.

---

## Files changed

| File | Change |
|---|---|
| `src/App.jsx` | Lazy page imports/Suspense; page-specific document titles; hash navigation + sessionStorage restore; review/essay-review titles |
| `src/components/ui.jsx` | Modal uses `useFocusTrap`; `aria-describedby`; dialog ref/tabIndex |
| `src/components/AppShell.jsx` | Mobile drawer uses focus trap + tabIndex; removed duplicate Escape/body-scroll effect |
| `src/lib/useFocusTrap.js` | **New** reusable keyboard trap/scroll lock/focus restore |
| `src/pages/LandingPage.jsx` | Accessible logo label; decorative mock `aria-hidden`; full-height mobile menu + backdrop + focus trap; remove header backdrop-filter while open |
| `src/pages/AuthPage.jsx` | Password policy UI 10–128; hints/messages updated |
| `src/pages/TeacherDashboard.jsx` | Uses server `openableExams` |
| `src/pages/TeacherStudentsPage.jsx` | Masked lookup badge/text; does not show stage/grade until accepted |
| `src/index.css` | Light `--app-subtle` to `#5b6b82`; dark filled-primary contrast rule |
| `public/theme-init.js` | **New** CSP-safe pre-paint theme bootstrap; removes theme flash |
| `public/robots.txt` | Removed invalid `Agentmap:`; Sitemap retained |
| `index.html` | Loads `/theme-init.js` before the app |
| `scripts/verify-csp.mjs` | **New** build-time CSP/API-origin guard |
| `package.json` | `build` runs CSP guard; added `verify:csp` |
| `test/agent-readiness.test.js` | Rejects `Agentmap:`; asserts CSP allows Monster only |
| `e2e/qa-audit.spec.js` | Updated password-length assertion to 10 |
| `.env.example` | Documents Monster production API URL |
| `vercel.json` | Authoritative CSP already uses Monster; guard prevents future drift |

---

## Behavioural changes

### Navigation and refresh
- Top-level pages now have hash URLs: `#/dashboard`, `#/exams`, `#/results`, `#/students`, `#/available`, `#/teachers`, etc.
- Browser Back/Forward moves between top-level pages.
- Deep links such as `/#/students` restore the page after authentication.
- `sessionStorage` restores the last top-level page if no hash is present.
- Detail pages (`create`, `exam-detail`, `review`, `essay-review`, `take`, `lobby`) require live state and intentionally keep the last top-level hash; a full router remains a future enhancement.

### Accessibility
- Dialogs and the app mobile drawer trap Tab/Shift+Tab, close on Escape, lock background scroll, and restore focus to the trigger.
- Decorative landing dashboard is hidden from the accessibility tree.
- Logo name includes visible brand text.
- Light helper text contrast is 5.07:1; dark filled primary controls use dark text (WCAG AA).
- Theme is applied before first paint via an external script allowed by `script-src 'self'`.

### Security / config
- Build fails if `VITE_API_URL` is not present in `connect-src`.
- Build fails if `connect-src` has `*`, `script-src` lacks `'self'`, contains `'unsafe-inline'`/`'unsafe-eval'`, or `object-src`/`frame-ancestors` protections are missing.
- Authoritative production origin is Monster; the old Vercel API URL is not present in the new build.

### Performance
- Teacher/student/admin pages, exam room, lobby, portfolio, and developer page are lazily loaded.
- Entry bundle: **476.82 KB uncompressed / 146.21 KB gzip** (was 780.1 KB / 206 KB).
- 22 page chunks emitted. Further vendor splitting remains optional.

### UX/data
- Dashboard “openable sessions” now uses `openableExams` from the server.
- Phone lookup shows masked data until an accepted relationship.
- Review pages show descriptive titles instead of “امتحاني”.

---

## Verification

| Check | Result |
|---|---|
| `npm run build` with Monster env | **PASS**; CSP guard pass, 22 chunks |
| `npm run test:unit` | **31 passed, 0 failed** |
| Lighthouse local landing | **100 / 100 / 100** (A11y/Best Practices/SEO), 0 failures |
| Lighthouse local login | **100 / 100 / 100**, 0 failures |
| Modal Tab trap | 14 Tab presses stayed inside; Escape restored focus |
| AppShell drawer | 16 Tab presses stayed inside; Escape closed |
| Mobile menu | Full-height 772 px at 390×844; backdrop; scroll lock |
| Hash navigation | Back returned to `#/results`; deep link `#/students` restored |
| No horizontal overflow | 390 / 768 / 1366 / 1920 px |

Evidence: `evidence/lighthouse-local-*-final.*`, `evidence/mobile-menu-fixed.png`, `evidence/frontend-verification.txt`, `evidence/frontend-build-final.txt`, `evidence/frontend-unit-final.txt`.

---

## Environment / deployment requirements

- `VITE_API_URL=https://emtehani.runasp.net` in Vercel Production (already set; current deployed build predates this remediation).
- Deploy `frontend/vercel.json` with the build so the CSP and Link headers are updated atomically.
- Do not deploy with a localhost or legacy Vercel API URL; the build guard will fail for CSP mismatch.
- No secrets were added to the frontend.
- **No Vercel deployment was performed.** Awaiting approval.
