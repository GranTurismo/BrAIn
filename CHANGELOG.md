# Changelog

All notable changes to BrAIn. Releases and installers: https://github.com/GranTurismo/BrAIn/releases

## 1.2.1 — 2026-10-20

### Fixed
- **YouTube showed "Sign in" after typing `youtube.com`** — the redirected first load could come back signed out (a refresh fixed it). A typed bare domain now goes straight to `www.` when your history shows the site lives there.
- Automated clicks, typing and scrolling could leave pages such as Google Play Console shifted sideways with no way to scroll back.
- Clicking **Skip** on YouTube ads uses a real input click, as YouTube ignores script-made ones.
- The address bar opens empty and focused on a new tab; the tab-audio indicator and the popover position in split mode are fixed.

### Added
- Two-finger trackpad swipe left/right goes back/forward (macOS).
- Passkey entitlements for Developer-ID builds (`BRAIN_SIGN_IDENTITY`, see `scripts/mac-permissions.sh`); the default ad-hoc build is unchanged.
- **Linux (experimental)** — a `linux-x64` archive that opens pages and navigates; in-page automation is not available there yet.

## 1.2.0 — 2026-10-18

### Added
- **Ollama agent** (⋮ → Ollama agent, or `ollama` in the address bar) — chat with a local Ollama model that drives the browser, no external MCP client needed.
  - Auto-detects the server (`localhost:11434`, `$OLLAMA_HOST`) and lists installed models; context size is configurable.
  - **Streams** the answer and, for thinking models (qwen3, gemma4, deepseek-r1…), the model's thinking.
  - **Side panel (macOS)** — docks to the right of the page so you can watch the page while it works; elsewhere it opens as a tab.
  - Runs through the same guards as any MCP client: Agent pause, "your turn" handoffs, seatbelt approvals and the activity log.
  - Enter or ⌘/Ctrl+Enter sends; Shift+Enter adds a new line.
- **Cancel AI agent** — a hard stop (Cancel button in the Ollama panel, or the command palette): aborts the current action, discards the task, declines open approvals, and refuses every further tool call from any agent until you resume.

### Fixed
- `browser_click`: reports whether the click reached the page and whether the page changed; falls back to a scripted click when the OS click is dropped; no longer clicks twice when a click navigates.
- `browser_type`: finds the real field inside wrappers, works with rich editors (Facebook, Lexical, Draft.js…), and reports when the text did not appear.
- `browser_snapshot`: honours `maxNodes`, lists open dialogs first, and includes clickable elements that have no role.
- `browser_press_key`: Enter in a text area adds a line instead of submitting; Backspace/Delete work; F1–F12; pages that handle Enter themselves are no longer double-triggered.
- `browser_set_checked`, `browser_select_option`, `browser_scroll` (inner scroll panels), `browser_find` (`text:` / `role:` selectors), `browser_wait_for` timing, and `browser_type_text` failing silently with nothing focused.
- Element refs could repeat after the page script was re-injected, sending clicks to the wrong element.

### Notes
- macOS build is ad-hoc signed (Apple Silicon only); right-click → Open on first launch.

## 1.1.0 — 2026-10-17

### Added
- **Personality page** (⋮ → Personality, `about:personality`) — a new home for BrAIn's headline features, with its own look.
- **Site scripts** — JavaScript that runs automatically whenever a matching site loads.
  - Write scripts with URL patterns (`example.com`, `*.site.com/blog/*`, `*`), turn them on/off, edit, delete.
  - **Quick tweaks** templates: auto-reject cookie banners, remove newsletter pop-ups, un-stick fixed bars, stop autoplaying video, unlock copy & right-click, dark mode, YouTube cleanup.
  - **Run now / Test on open tab** — try a script without reloading the page.
  - **`</>` toolbar badge** — shows which scripts match the current page, with a per-site pause switch (hideable via Customize toolbar).
  - **Error reporting** — errors, including ones thrown later from timers, animation frames and MutationObservers, show on the script with a "Copy fix prompt" for your agent.
- **MCP tools for agents** — `userscript_create`, `userscript_install_template`, `userscript_run`, `userscript_errors`, `userscript_update`, `userscript_get`, `userscript_list`, `userscript_templates`, `userscript_delete`.
  - Agent-written scripts are saved as **drafts and never run until you approve them**; agents cannot edit or delete scripts you wrote.

### Changed
- **Shorter ⋮ menu** — Library entries stay on top level; Tab actions, This page, AI agent, Settings and More tools now open as side panels. Personality is highlighted at the top.

### Fixed
- ⋮ menu flickered or "reloaded" when opening a submenu and for the first few seconds after launch (clicks inside the toolbar's shadow DOM were misread as outside clicks, and update-status pushes rebuilt the open menu).

### Notes
- macOS build is ad-hoc signed (Apple Silicon only); right-click → Open on first launch.

## 1.0.4 — 2026-10-15
- **Toolbar customization** — hide built-in buttons and add shortcuts (History, Passwords, Flows, Screenshot, Developer tools, Print).
- **History suggestions** — the address bar searches your whole history, including imported history.
- **Two-stage tab loading** — calm ring while connecting, glowing ring once content arrives.
- **Smoother tabs (macOS)** — new tabs wait until the page is ready; pages fade in on switch/close.

## 1.0.3 — 2026-10-12
- **Page theme** — sites render with their own background and theme instead of the app's dark window bleeding through.

## 1.0.2 — 2026-10-12
- **Popup windows (macOS)** — `window.open` / `target=_blank` work; sized popups (OAuth, payments) get a real window so `window.opener` survives.
- **Real input (macOS)** — agent clicks use OS-level events so gesture-gated flows (e.g. WebAuthn prompts) can be triggered.
- **Agent handoff** — `browser_ask_human` card shows on every tab; saved flows no longer race the user's manual step.

## 1.0.1 / 1.0.0 — 2026-10-11
- Initial releases: native browser (WebView2 / WKWebView) with an embedded MCP server, tab strip and omnibox, accessibility-tree snapshots, screenshots, console/network capture, password manager, history, downloads, Rewind, flows & activity log, self-updater.
