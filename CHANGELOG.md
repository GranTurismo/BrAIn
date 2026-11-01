# Changelog

All notable changes to BrAIn. Releases and installers: https://github.com/GranTurismo/BrAIn/releases

## 1.7.0 — 2026-11-01

### Added
- **Every website becomes an API** — BrAIn learns the requests a site makes behind the scenes and compiles them into tools your agent can call directly, as you, with no clicking. Open a site, **⋮ → AI agent → Learn this site's API** (or `browser_api_learn`), and use it: the page reloads and BrAIn watches the JSON `fetch`/XHR calls (GraphQL included) the site's own scripts make. They become **drafts** in the new **Site APIs** page (`about:apis`, also in the command palette): path ids turn into arguments, query strings / JSON bodies / GraphQL variables become typed arguments, the answer's shape is recorded, each endpoint gets a name (`list_issues`, `create_issue`…) and a read/write verdict. Review one, rename it, edit its path and arguments, then switch it on: it becomes a live MCP tool `api_<site>_<name>` (plus `browser_api_list`, `browser_api_learn` and `browser_api_run`). A call is replayed **inside a tab on the site**, so your session cookie, the site's own CSRF token and its bearer token all apply; with no tab open BrAIn loads the site in a helper tab and closes it again. **Safety:** nothing is callable until you approve it, and only the `about:apis` page can approve (a web page cannot, nor switch learning off); calls that change data, or touch something sensitive, ask you every time through the Seatbelt; sign-in/token endpoints, analytics, third-party hosts and URLs with secrets are never learned; credential-like headers are recorded by *name* only — their values stay in the page's memory, and the page refuses to send them to any other site; sample answers are trimmed and masked; nothing is learned in Ghost mode or private tabs. Stored as readable JSON in `apis/<site>.json`. The planned-features page no longer lists it.
- **Export a site's API as documentation (optional)** — each site on the Site APIs page has an **Export docs…** button with two checkboxes, both off until you tick them: an **OpenAPI 3 file** (`<site>-openapi.json`; a summary and a description on every operation, argument and response field, the servers, the session and token headers each call needs, GraphQL operations listed as `/graphql#OperationName`, and masked example answers; it validates with swagger-parser and Redocly) and a **full Markdown reference** (`<site>-api.md`: overview and authentication notes, a table of endpoints, and per endpoint its description, arguments table, an example agent tool call and `curl`, the GraphQL document, the response fields and a masked example, and a note when BrAIn asks first). Choose approved endpoints only, or drafts too (marked as drafts). Files are saved in Downloads and appear on the downloads shelf; only the Site APIs page can trigger an export. They hold no token, cookie or password value.

- **Copy image and Save image** — right-click a picture: *Open image in new tab*, *Copy image*, *Save image to Downloads* and *Copy image address*. It works on `<img>` pictures (also ones with a transparent layer on top, as galleries do), `<canvas>`, CSS background images and `data:` pictures. *Copy image* puts the picture on the clipboard (the original PNG / JPEG / GIF bytes plus a version every app can paste on macOS; PNG on Windows); *Save* keeps the original file byte for byte, uses the picture's own name, never overwrites (`name (1).png`) and lists it on the downloads shelf; a small message on the page says what happened. SVG pictures are saved and copied as PNG. When a site doesn't let the page read its own picture (another site's CDN), BrAIn downloads it itself with the page as referrer, so hotlink-protected pictures work too. **Safety:** a web page can send these requests too, so BrAIn only accepts them from the tab you are looking at, at most ten in 30 seconds, identifies the picture from its bytes (PNG, JPEG, GIF, WebP, BMP or AVIF — not SVG, HTML or programs, whatever the page claims), gives the file the extension of what it really is (no `photo.exe`), caps size (40 MB) and pixels, and when it has to download a picture it refuses addresses on your own network and `file:` links unless the page is itself local. Windows: compile-checked only.

### Changed
- **Auto-reject cookie banners was rebuilt and now reaches the copy you installed** — the old script only pressed a button whose text was *exactly* "Reject all" (and a short list of known IDs), so most banners were missed. The new one: finds the consent pop-up (it has to talk about cookies / consent / tracking, so newsletter and "Decline" dialogs are left alone), presses *Reject all*, *Decline*, *Necessary only*, *Continue without accepting* and their many variants in English, German, French, Spanish, Italian, Portuguese, Dutch, Polish, Czech, Russian, Ukrainian, Georgian, Turkish, Greek, Nordic languages and more; reads the button's `aria-label` and works on links and `div` buttons; sees through shadow DOM (Usercentrics) and same-site iframes; and when a pop-up only offers *Settings* / *Manage preferences* it opens them, **switches every optional category off** (necessary ones stay), and presses *Save* — and presses a confirmation if one follows. It never presses an *Accept* button, never touches a "Cookie settings" link that has no consent prompt next to it, never presses a button that mixes "necessary *and* analytics", waits for pop-ups that appear late (up to 30 seconds), and **leaves alone a panel you opened yourself** (a pop-up that appears within 2.5 seconds of your own click or key press is yours, and it only flips switches during the first 8 seconds of a page or in a panel it opened itself). It logs what it did in `window.__brainCookies`. Copies of the old template that you never edited are upgraded automatically the next time BrAIn starts (name, sites and on/off stay as they were); a copy you changed is not touched — turn the template on again to get the new one. Limit: a consent pop-up that lives in another site's iframe (some Sourcepoint / consent-manager set-ups) can't be reached by a site script.
- **BrAIn now opens straight into fullscreen (macOS)** — it used to appear as a 1440×900 window, load the start page, and only then slide into fullscreen. Now the window stays invisible from the moment it is created until macOS reports that fullscreen is reached, and then fades in already full screen. It also asks for fullscreen as early as the window allows (it silently ignores the request for the first half second, so it asks again until it is accepted) and listens for macOS's own "entered fullscreen" notification instead of waiting a fixed time. If fullscreen is not accepted, or anything goes wrong, the window is shown anyway (a watchdog shows it after six seconds at the latest), and `--no-fullscreen` is unchanged. The first-run welcome film starts once the window is visible.
- **Ghost mode now logs you out of every account** — until you turn it off. Before, tabs that were already open kept their signed-in sessions, so only new tabs were clean. Now the tab you are looking at reloads logged out the moment Ghost mode goes on, and each other open tab does the same when you switch to it (a background tab with something half-typed is not touched until you look at it). Turning Ghost mode off puts every one of those tabs back on the page it was on before — never on what it showed in Ghost, so nothing browsed there reaches history or the session file — and your accounts are back. Per-tab views only (macOS, and Windows with its normal per-tab views).
- **“Stay logged in” has a proper place and name** — the switch used to be *Ghost: cookies & logins*, buried in ⋮ → Tabs. Now the Ghost pill beside the address bar says **Logged out** or **Logged in** and opens a small panel with the **Stay logged in** switch (and *Turn off Ghost mode*). It is still in the ⋮ menu and the command palette. With it on, ghost tabs start with a copy of your cookies and can autofill saved passwords, as before; flipping it while Ghost mode is on reloads your ghost tabs to match. Only the toolbar can change it — a web page can't.

### Fixed
- **The picture-in-picture button no longer looks disabled when it isn't (macOS)** — the toolbar lives in its own view, which has no videos of its own, so it always dimmed the button and said "No video on this page", while a click still worked because it is passed on to the page. The page now tells the toolbar whether it has a video (or one is already floating), so the button is lit on pages with a video, dimmed only on pages without one, and shows the "exit" state while floating. The ⋮ menu entry and `Ctrl/Cmd+Shift+P` follow the same state.
- **Updating now shows what it is doing** — *Check for updates* and *Update now* used to change one menu row and nothing else, so after you clicked there was no sign that anything was happening. An update card now drops down from the toolbar: a spinner while it checks, then *BrAIn is up to date* (it clears itself after a few seconds) or *BrAIn X is available* with *Later* / *Update now*. Downloading has a real progress bar with megabytes and percent and a *Cancel* button; after it come *Verifying the download* and *Installing* (BrAIn restarts by itself). A failure says why and has *Try again*, which carries on from wherever it stopped instead of making you check again first. While it checks, the card is alive: a morphing aurora orb with sonar rings and orbiting sparks, a light running round the card's border, a shimmering title, and a status line that decodes out of noise into a new line every moment (*Pinging GitHub's satellites…*); when you are up to date the check mark pops in with a burst of sparks, an available update gets a pinging ring and the download bar has a light sweeping along it. A check you start by hand keeps the *checking* card up for about two seconds so you can actually see it (the real request takes a fraction of that); *Reduce motion* turns all of it off. The quiet check at launch still only lights the *NEW* badge. Closing the card does not stop a download, and the menu row follows along (*Verifying update…*, *Installing update…*); clicking it while something is running brings the card back.

## 1.6.0 — 2026-10-31

### Added
- **Ghost mode: cookies & logins** — a new switch (⋮ → Tabs → *Ghost: cookies & logins*, or the command palette) chooses what a ghost tab starts with. Off (the default, as before): empty, so every site sees a visitor who isn't signed in. On: new ghost tabs start with a *copy* of your cookies, so signed-in sites open signed in, and saved passwords can be autofilled. The copy only goes one way — nothing a ghost tab sets or changes reaches your real profile, no new passwords are offered for saving, and everything is dropped when the last ghost tab closes. Only cookies are copied (sites that keep their sign-in in local storage still ask you to sign in), and tabs you open with *Private* during Ghost mode always start clean. The choice is remembered across launches (Ghost mode itself still isn't) and applies to tabs opened afterwards; the ghost button's tooltip says which kind new tabs will be. `browser_ghost` tells an agent which kind of ghost tabs it will get, but only you can change the setting.

## 1.5.0 — 2026-10-31

### Added
- **Grab video captions** site-script template — spots the subtitle files a video player loads (WebVTT, SRT, TTML, YouTube `timedtext`, HLS subtitle playlists, plus `<track>` elements and cues a player builds itself), reads them from the network and keeps the text. It watches `fetch`/XHR for new requests and the resource timeline for ones made before it ran. A small **CC** chip shows up on the page to copy the text or an `.srt`; an agent can read `window.__brainCaptions` (`list()`, `text(id)`, `srt(id)`, `vtt(id)`) with `browser_evaluate`. On sites that switch video without reloading (YouTube), only the current video's captions are listed, and the language comes from the caption file, not the page. Main frame only — a player inside an iframe is not covered.

## 1.4.0 — 2026-10-30

### Added
- **Welcome tour** — a short interactive film plays on first launch: an AI agent booking a flight while Seatbelt asks before it pays, Rewind scrubbing a page back in time, Ghost mode opening a private tab, a flow being approved, a site script written and run, and the memory benchmark against Chrome. The browser windows in it are BrAIn's own: the real toolbar, Seatbelt card, Rewind bar and new-tab page, and the real Flows and Personality pages running on demo data, so the tour always looks like the app. Every scene can be poked (pause the agent, answer Seatbelt, drag the Rewind slider, flip Ghost mode, press Run, pop the clutter). `Esc` skips; replay it any time from `Ctrl/Cmd+K` → *Welcome tour*, ⋮ → *Welcome tour* or the Help page. It steps aside the moment an agent starts working, and is only loaded when shown.
- **Ghost mode** — one switch for “leave no trace”: the ghost button in the toolbar, `Ctrl/Cmd+Shift+X`, ⋮ → Ghost mode, or the command palette. While it is on there is no history, no password saving or autofill, no Rewind snapshots, no session-restore state and no MCP call log on disk, and new tabs open as private tabs. It is never remembered across launches, and tabs that were already open keep their normal site data.
- **Quick controls** — a sliders button in the toolbar with five simple switches: *Block pop-ups*, *Block autoplay*, *Mute sound*, *Dark pages* and *Allow copy & right-click*. Each one can be set for **this tab**, **this site** or **everywhere**; the narrowest one that is set wins, so a site or tab can opt out of an everywhere setting. Site and everywhere settings are saved in `prefs.json`; tab settings last until the tab closes (the tab pill's speaker is the same mute, at tab scope). Private tabs and Ghost mode can't save a site. While pop-ups are blocked, every window a page tries to open is dropped (your own clicks on links that open a new tab still work), the button shows how many were blocked, and *Open last* opens the most recent one as a tab. *Dark pages* switches sites that have a dark theme to it and inverts the rest, keeping photos and video in their real colours; pages that are already dark are left alone. Autoplay waits until you click or type on the page.
- **Full control mode** — ⋮ → AI agent → *Full control mode…* (or the command palette) lets an AI agent connected over MCP act as you without asking first: *Ask before risky actions* stops asking, site scripts it writes run right away and it may change or delete yours, flows it saves are active without review, and it can turn Ghost mode off. Turning it on goes through a warning that spells out what the agent can do and why that is risky (your signed-in accounts, prompt injection from websites, actions that can't be undone), needs an "I understand" tick, and makes you choose when it ends: after 15 minutes, after an hour, or when BrAIn closes. It is never remembered across launches and no MCP tool can switch it on — nor can an agent click its way in through BrAIn's own toolbar: it won't turn on while an agent is using the mouse or keyboard, or for a few seconds after. Where the toolbar runs inside web pages (Windows), a native confirmation that page scripts can't click is required as well. While it is on, a red *Full control* pill in the toolbar counts down and turns it off with one click. Saved passwords stay out of reach, and Pause / Cancel agent keep working.
- **Planned features page** (`about:roadmap`, or type `planned`) — what's in development or planned now has its own page: *Every website becomes an API*, *Session hub*, *Swarm mode*, *Dark-pattern bodyguard* and *Goals, not tabs*. The Personality page keeps only what you can use today, with a card that links there. Also in the command palette as *Planned features*.
- **Suggestions in the start page's search box** — typing in the big "Search Google or type a URL" box now suggests like the address bar: a *Search …* / *Go to …* row, then matching bookmarks and history (older history too), with inline completion, ↑/↓, Enter, `Alt+Enter` for a new tab and `Esc`. The placeholder also names your chosen search engine.
- **`browser_ghost` MCP tool** — an agent can check Ghost mode and turn it on for a sensitive task. It can turn it off again only if it turned it on itself (or while you have full control mode on); if you switched it on, it stays on and the agent is told to ask you.

### Changed
- **Smoother transitions (macOS)** — switching tabs now crossfades from one page to the other instead of fading in over the dark window (no flash between two light sites), and closing a tab fades into its neighbour. The address bar's text settles in when it changes, the Rewind bar slides in, and menus, cards, the command palette, the find bar and the new-tab page fade out instead of vanishing (they already animated in). Everything is a short Core Animation or CSS animation that only runs during the change, and it is all skipped when *Reduce motion* is on.
- **Faster start page** — the start page now finishes loading in about 0.5 s instead of about 1.2 s, with no loading bar lingering after launch. The "Recent" tiles used to fetch each site's icon from the internet on every launch, and WebKit counts those fetches as the page still loading. Icons are now downloaded once, kept in `favicons.json` and shown from disk (a tile shows its letter until its icon has been downloaded). Built-in pages such as History and Help also stop waiting an extra 0.15 s after they load.
- Startup milestones are logged to stderr as `startup +NNNms …`, for measuring launch time.

### Fixed
- **Video players' fullscreen button works on every site (macOS)** — tabs had the web's Fullscreen API switched off (WebKit ships it off), so only a plain `<video>` could go fullscreen and players that make their own container fullscreen (most custom players) did nothing when the button was pressed. It is now on for every tab, and while a page is fullscreen BrAIn's layout leaves it alone. On Windows a video going fullscreen now takes the whole screen even when the window isn't fullscreen (it only filled the window before), and the window goes back to how it was when the video exits.
- **Option/Alt shortcuts no longer fire while typing in a field** — Option+← in a text box or the address bar moves the cursor by a word instead of going Back (same for Option+→, Option+Home and the rest). Cmd/Ctrl shortcuts are unchanged.

## 1.3.1 — 2026-10-21

### Fixed
- **macOS: crash after a page's popup window closed** — closing a popup (for example a sign-in window) released its web view twice, so the next run-loop turn touched freed memory and BrAIn quit with a segmentation fault, usually seconds after loading a page such as LinkedIn's sign-in. The popup window now owns its web view alone and is closed safely.

## 1.3.0 — 2026-10-20

### Added
- **MCP tools for your browser data and chrome** — `browser_bookmark` (add / remove / toggle / check / list, by URL or for the open page), `browser_history` (list / search / delete / clear), `browser_downloads` (list / wait / cancel / reveal / clear), `browser_tab` (pin, mute, duplicate, move, reopen closed tab), `browser_clipboard`, `browser_find_in_page`, `browser_save_page`, `browser_emulate` and `browser_import`.
  - Clipboard contents are never written to the activity log.
  - `browser_import` brings in bookmarks and history only; cookies and saved passwords stay a manual step on `about:import`, and saved passwords are not reachable through MCP at all.
  - Downloaded files can be revealed but never opened by an agent.

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
