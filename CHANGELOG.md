# Changelog

All notable changes to BrAIn. Releases and installers: https://github.com/GranTurismo/BrAIn/releases

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
