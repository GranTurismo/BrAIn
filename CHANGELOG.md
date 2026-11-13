# Changelog

All notable changes to BrAIn. Releases and installers: https://github.com/GranTurismo/BrAIn/releases

## 1.10.0 — 2026-11-13

**Scope:** new on **macOS** (`brain view` and the automatic `brain` command). **Linux and Windows:** nothing new, only the version number.

### Added
- **`brain view` on a Mac** — BrAIn runs without a window (nothing on your screen, no Dock icon) and the terminal
  shows it: pictures in iTerm2, Ghostty, kitty and WezTerm, real-text mode in the macOS Terminal. Mouse, keyboard and
  wheel go into the browser; the terminal's `Ctrl`+letter is `⌘`+letter (`Ctrl+L` address bar, `Ctrl+T` new tab,
  `Ctrl+C` copy). A drop-down list opens as a list in the terminal instead of a menu on your screen, and sign-in
  popups appear over the page with a title bar and a close box. `brain view` starts that BrAIn when none is running;
  `brain start --headless` starts it without showing it.
- **The `brain` command sets itself up on macOS** — an app that is dragged into Applications or updated in place never
  runs an installer, so BrAIn now links `brain` itself, a few seconds after it starts, in a folder on your PATH
  (`~/.local/bin`, `~/bin`, `/usr/local/bin` or `/opt/homebrew/bin`, whichever your shell uses and you can write to).
  It never replaces a different program called `brain`, keeps its link pointing at BrAIn when the app is moved, and
  does not put the link back if you delete it.

### Fixed
- `brain shot` without a file name could crash on Apple Silicon Macs: the terminal's size was read with the wrong
  calling convention.

## 1.9.0 — 2026-11-13

**Scope:** the `brain` command and the headless mode are new on **Linux**; the `brain` command (without `view`) also
works on **macOS**. **Windows** is unchanged (it has no `brain` command yet).

### Added
- **The `brain` command: BrAIn from a terminal** — `brain open`, `read`, `snap`, `click`, `type`, `shot`, `tabs`,
  `wait`, `eval`, `call` (any tool) and more drive the running browser, and start one in the background when none
  is running. Each command is one MCP tool call over a private Unix socket, with the same safety checks and activity
  log as an AI agent. `brain help` lists them.
- **Linux servers without a display** — BrAIn starts on an invisible screen of its own (Xvfb, locked with a cookie
  only your account can read, plus a private session bus) and keeps running after you log out, until `brain stop`.
  Needs `xvfb` (`install.sh` says so when it is missing).
- **`brain view`: the browser live in a terminal** — the whole window mirrored as pictures (kitty, Ghostty, WezTerm,
  iTerm2, sixel terminals), with your mouse and keyboard played into it as real input: tabs, address bar, menus,
  dialogs and BrAIn's shortcuts work. Only what changed is sent again. Copying in the browser lands on your own
  clipboard; `Ctrl+Q` leaves.
- **Text mode for terminals without pictures** (macOS Terminal, GNOME Terminal, tmux, any SSH app) — the page's and
  the toolbar's text drawn as real, sharp characters with their colours, bold and links, on the colour really behind
  them, and pictures as coloured blocks. While you watch, the page is laid out on a character grid (a monospace font
  sized to the cells); it gets its own fonts back when you leave. Text that is clipped, covered or only for screen
  readers is left out; the focused field's caret is the terminal's cursor. `Ctrl+O` opens the reader: the page as
  Markdown with numbered links.
- **Sign-in popups on a server** — a sized popup ("Continue with Google", payment pages) opens over the browser window
  where `brain view` shows it, with a title bar and close button of its own, takes the keyboard, and gives it back when it
  closes; text mode draws its text on top. Clicking a window in the view gives it the keyboard.
- **`brain mcp`** — an MCP server on stdio that is the running browser, so an AI agent and you (in `brain view`)
  share one browser on a server. An agent on another machine can use it through SSH (`ssh server brain mcp`).

### Changed
- A BrAIn started without an MCP client (from the desktop, Finder or a terminal) no longer runs an MCP session on
  stdin that ends at once; SIGTERM and Ctrl+C now close the browser properly instead of leaving its window behind.

## 1.8.8 — 2026-11-10

**Scope:** **all platforms** (the MCP tool is shared code). Nothing else changes.

### Changed
- **`browser_upload` takes files up to 5 GB (was 64 MB)** — the file used to be read whole and sent as one text string, which cannot hold more than about 1–2 GB. It is now read and sent to the page in pieces of about 450 KB, and the page assembles them into the file.

### Known
- The page keeps the whole file in memory while it is attached, so a multi-GB file needs about that much free RAM, and attaching one takes a while: every byte goes through the page's JavaScript.

## 1.8.7 — 2026-11-07

**Scope:** a text-only release for **all platforms**: nothing changes in how BrAIn behaves.

### Changed
- **BrAIn no longer compares its RAM use with Chrome** — the comparison is gone from the welcome film, the Personality page, the README and the website. The film's slide now shows only the command palette and passwords.

## 1.8.6 — 2026-11-07

**Scope:** **macOS** gets the memory fix and the two smaller fixes below. **All platforms** get the corrected RAM comparison. 1.8.5 shipped only a Windows installer and was never offered as an update, so its changes also first reach people through the updater here: **Windows** gets its startup fixes, and **macOS and Linux** get the app logo on BrAIn's own tabs (details under 1.8.5).

### Fixed
- **Background tabs use less memory (macOS)** — background tabs stayed inside the window, only hidden, and WebKit frees a page's memory only once it has left the window. They now leave it, the way Safari's and Chrome's do. Pages keep running: audio, timers, downloads, agents and screenshots all still work. On 12 real sites this saves about 390–470 MB (8–12%). A tab that has been in the background for more than about 10 s needs 30–200 ms to repaint when you return; the old page stays on screen until the new one is ready, so there is no white flash.
- **A screenshot of a background tab can no longer hang (macOS)** — it could wait forever for a screen update that an out-of-window tab never produces.
- **The fullscreen memory readout counts only BrAIn (macOS)** — started from a terminal or by an MCP client, it also counted other apps' WebKit processes started from that terminal.

### Changed
- **The 81× RAM claim is withdrawn (all platforms)** — the welcome film, the Personality page and the README said BrAIn uses 81× less RAM than Chrome. That was wrong: it counted only BrAIn's app process, not the WebKit processes that hold the pages.

### Known
- What 1.8.5 lists as known is still open: WebView2 helper processes can outlive BrAIn on Windows, and the two Windows security gaps from 1.8.4 remain.

## 1.8.5 — 2026-11-05

**Scope:** a Windows fix release. **Windows** gets both fixes below. **macOS and Linux** change only in the tab logo (shared toolbar script, not run on either). The version number and the update feed are shared by every platform, which is why they also get a new installer.

### Fixed
- **Windows: the first tab opens on the start page again, not a black screen** — a new tab's view starts on a blank placeholder page titled `braintab-<id>`, and BrAIn took that page's address and title as the tab's own. The first tab then showed a black window with a `data:text/html…` address, a `braintab-…` title and a letter tile instead of the New Tab page, and startup "navigated" it back to the placeholder. BrAIn now ignores the placeholder.
- **Windows: BrAIn no longer loses about 8 s at startup on roughly half of launches** — BrAIn connects to WebView2's debugging endpoint by polling it from the first fraction of a second with one shared HTTP client and a 3 s timeout. A request that lands between the port opening and the server answering can hang for its whole timeout (reproduced with a minimal WebView2 host and a plain .NET client, so it is not BrAIn's own code), and the shared client kept hanging after it. Each attempt now gets its own connection, skips the system-proxy lookup and starts with a short timeout (300 ms, growing to 3 s). Measured on one machine: the New Tab page appeared in 9–14 s on about half of launches before, and in 1.8–2.6 s on 6 of 6 launches after.

### Changed
- **BrAIn's own pages show the app logo on their tab** (New Tab, Help, History, Passwords, Flows, Site APIs, Personality, Planned features, Ollama and the rest) instead of a coloured letter tile. The logo is the one in the app icon; it also shows in the command palette's tab list.

### Known
- **Windows: WebView2 helper processes can outlive BrAIn** — after closing BrAIn normally, its two WebView2 helper processes per launch were still running on the test machine (about 30 after a day of test launches, a couple of GB). Not investigated yet, so not fixed.
- The two Windows security gaps listed under 1.8.4 are still open.
- Verified on Windows 11 only (release build, six launches). macOS and Linux were not run for this release.

## 1.8.4 — 2026-11-04

**Scope:** mainly a security release. **macOS and Linux** get everything below. **Windows** gets the shared parts (the update signature check, the agent protections, cookie scoping) — built, but not run on Windows — and **two Windows security problems are not fixed** (see Known). The version number and the update feed are shared by every platform, which is why Windows also gets a new installer.

### Security
- **A web page can no longer read BrAIn's own data or use its browser commands (macOS, Linux)** — BrAIn's toolbar scripts run inside every web page, and a hostile page could listen to everything BrAIn told them (your history, bookmarks, every open tab including private ones, downloads) and send commands the toolbar uses (for example "replay this request with my cookies", which also reached addresses on your local network). Four layers now stand between a page and BrAIn: every message from a page's view carries a secret token that only BrAIn's own scripts hold; a web page's view is only sent what it needs (its own tab, no history, bookmarks or other tabs); toolbar-only commands are refused from page views; and request replays only go to the same site, never to local-network addresses (also against DNS rebinding). DevTools cookies are limited to the inspected site with HttpOnly values hidden. Found by testing the released 1.8.3; a proof-of-concept page that read 68 of BrAIn's messages in 1.8.3 now sees only its own tab.
- **Updates must now be signed (all platforms)** — before, BrAIn installed whatever the update feed offered if GitHub's checksum matched (and skipped even that when the checksum was missing), so whoever took over the GitHub account or a release could have shipped code to every user. An update is now installed only if it comes from this repository's release folder, carries GitHub's SHA-256, **and** a signature (made with a private key that is not on GitHub) over the version, the file name and the file's hash, which the program checks against a public key built into it. A release without a valid signature is refused; nothing is installed. The macOS update script no longer builds shell commands from file names and checks that the new app is intact before replacing the old one.
- **An AI agent that a web page has talked into misbehaving can no longer leak your secrets or write where it likes** — credential and key files (`~/.ssh`, `.aws`, keychains, other browsers' profiles, `.env`, `*.pem`, BrAIn's own saved passwords and cookies, also behind links and `../`) can never be uploaded or opened as a `file:` address by an agent, in any mode, with no question asked; `save_page` and PDF export only write inside your Downloads folder; network traffic shown to an agent has tokens, cookies and passwords replaced by `[redacted]` (asking for the real values needs your OK; the HAR file you save keeps everything); `browser_cookies` lists only the current site's cookies and never HttpOnly values; a script that clicks, submits or sends *and* names a Buy / Delete / Send / Confirm word asks like the click would; Space on a button is checked like Enter; the file-upload helper variable is random and deleted at once.
- **Agent uploads ask once where from** — the first time an agent uploads a file, a card asks where agents may upload from: only from one folder (you pick how far up from the file, e.g. `~/Documents` or `~/Documents/taxes`), or anywhere (with a warning), or just this once. The choice is remembered; a file outside the chosen folder asks again; **Agent file uploads…** in the menu / command palette forgets it. Everyday agent work — clipboard, local pages, ordinary scripts — is never interrupted. For people who want every upload, clipboard read, cookie listing and script to ask, put `"strict": true` in `belt.json` in BrAIn's data folder.

### Fixed
- **Closing a tab while a video is fullscreen no longer leaves a black screen (macOS)** — with the Fullscreen API WebKit moves the video into its own window and takes BrAIn's off screen; closing the tab then left BrAIn invisible while the video kept playing, and closing it before the enter animation finished could crash BrAIn. BrAIn now leaves fullscreen first and defers tearing the view down. Linux no longer stays stuck in window fullscreen after closing or switching away from a fullscreen tab. (Windows has the same code path; not verified.)

### Added
- **Memory use in macOS fullscreen** — the empty space at the top left of a fullscreen window now shows BrAIn's live memory, updated each second, counting the app and its WebKit helper processes the way Activity Monitor's *Memory* column does.
- **DevTools Network recorder (macOS, Linux)** — Time and Waterfall columns, per-request tabs (Headers, Payload, Preview, Response, Timing, Messages for WebSockets), Copy as cURL / fetch, HAR export, and "Preserve log". It records only while DevTools is open or an agent asked. Agents get `browser_network_requests`, `browser_network_request`, `browser_network_har` and `browser_network_record`. Throttling is not offered (WebKit cannot really do it). Windows keeps its existing Network panel.

### Known
- **Windows is not fixed for two things:** the toolbar there lives inside the web page, so the page-vs-BrAIn protection above (macOS, Linux) does not apply to it; and WebView2's debugging port is open on `127.0.0.1` without a password, so any program on the same computer can control the browser. Both need the toolbar moved out of the page, which needs a Windows machine to build and test.
- **Updating from 1.8.3 or older installs this release the old way** (without the signature check, which only exists from this version on); every update after this one is signature-checked, and an update without a valid signature is refused.
- Not fixed yet: the downloads panel's "Open" has no warning for dangerous file types, and favicon fetches have no local-address guard.
- Verified on macOS (a sandboxed build, real WebKit) and in the Linux test container (full end-to-end suite, including real clicks on the approval cards). The upload-folder card was clicked through on Linux only, not on macOS; Windows builds were not run.

## 1.8.3 — 2026-11-04

**macOS and Windows:** nothing changes for you in this release — it is a Linux change. It still ships as a new version with rebuilt installers, because the version number and the update feed are shared by every platform.

### Changed
- **Linux: BrAIn works out of the box on Fedora 45 and newer — no extra package** — 1.8.2 told you what was missing when a system had no OpenSSL 3 (Fedora 45 ships OpenSSL 4, which BrAIn's .NET runtime cannot use). The Linux archives now carry OpenSSL 3.5 themselves, in an `openssl/` folder next to the program, and BrAIn loads it at start **only when the system has no OpenSSL it can use** — a system copy always wins, so the distribution's security updates still apply. The same dialog / stderr message as in 1.8.2 remains for the case where even the bundled copy cannot load (a glibc older than 2.38). `install.sh` no longer asks for `openssl3-libs`; it prints a note when it sees the system has none. The archives are about 2.8 MB larger. The libraries are Fedora's own `openssl3-libs-3.5.9` build, Apache-2.0 licensed, with the licence in `openssl/LICENSE.txt` and the provenance and checksums in `third_party/openssl/README.md`.

### Known
- GTK 3, WebKitGTK 4.1, libnotify and libsecret are still system packages (`sudo dnf install webkit2gtk4.1 gtk3 libnotify libsecret` on Fedora); they are too large and too tied to the graphics stack to ship inside the archive.
- Verified on Fedora 45 (WebKitGTK 2.54) in an arm64 container, not on a real desktop; the x64 library is the same package for x86-64 and was checked for architecture only.

## 1.8.2 — 2026-11-04

**macOS and Windows:** nothing changes for you in this release — it is a Linux fix. It still ships as a new version with rebuilt installers, because the version number and the update feed are shared by every platform.

### Fixed
- **Linux: BrAIn no longer dies a few seconds after start on systems without OpenSSL 3 (Fedora 45 and newer)** — BrAIn's .NET runtime needs OpenSSL 3 (`libssl.so.3`; 1.1 also works) for TLS and crypto: the update check, hashing, the password vault. Fedora 45 ships OpenSSL 4 and keeps OpenSSL 3 in a separate package, and when the runtime finds no version it knows it does not throw — it aborts the whole process ("No usable version of libssl was found"), about a second after the home screen appears. BrAIn now checks for the library first. If it is missing, a desktop launch shows a dialog that names the package for your distribution (Fedora: `sudo dnf install openssl3-libs`) and BrAIn exits cleanly; started by an MCP client over pipes it prints the same message to stderr and exits with status 1, without a dialog. Nothing changes when OpenSSL 3 is present.
- **Linux: `install.sh` warns about a missing OpenSSL 3** along with the other required libraries, with the right package name for Debian/Ubuntu, Fedora/RHEL, openSUSE and Arch; the README and the archive's README list it.

### Known
- On Fedora 45 and newer you still have to install OpenSSL 3 yourself once: `sudo dnf install openssl3-libs`. Setting `CLR_OPENSSL_VERSION_OVERRIDE=4` does not help. Bundling OpenSSL 3 so no extra step is needed is possible but not done.
- The self-updater needs TLS too, so a Linux system without OpenSSL 3 cannot update itself until the package is installed.
- Verified on Fedora 45 (WebKitGTK 2.54) in a container, not on a real desktop.

## 1.8.1 — 2026-11-04

**macOS and Windows:** nothing changes for you in this release — it is a Linux fix. It still ships as a new version with rebuilt installers, because the version number and the update feed are shared by every platform.

### Fixed
- **Linux: the welcome film no longer lags** — the first report from a real desktop said BrAIn "crashes and lags on the welcome screen". On Linux the toolbar (where the film plays) is painted on the CPU, and the full film — film grain, a drifting glow, a full-window neural-field canvas, large soft shadows and a dozen looping animations — ran at about 11 frames per second on a 1080p screen and under 4 on a HiDPI one, with the toolbar's web process at 100% CPU. On Linux the film now drops what only decorates (large soft shadows, blur filters, endlessly looping CSS animations, grain, the 3D tilt), draws its canvas at 1× and about 15 times a second, and sheds the canvas altogether when it still cannot hold 30 fps. Measured in the test container over the whole film: 11 → 47 fps at 1080p, 4 → 38 fps on a 3200×2000 HiDPI screen. The film looks the same otherwise.
- **Linux: a toolbar whose web process dies now comes back** — before, the window became a blank white rectangle (a frozen strip of the film, no toolbar, nothing to click) until you restarted BrAIn. The toolbar and the side panel now reload (up to three times in 30 seconds), and a welcome film that was playing is counted as seen so it cannot crash BrAIn again on every launch; **Welcome tour** in the command palette plays it on demand.

### Added
- `BRAIN_DEBUG_BAR=1` also reports the toolbar's frame rate (`FPS n worst-frame=…`) every two seconds, and a new Linux end-to-end test (`20_welcome_film`) checks the film's frame rate and the toolbar's recovery.

### Known
- We could not reproduce what kills the toolbar's web process on the reporting machine; if BrAIn still crashes, please send the output of running `brain` from a terminal, your distro, GPU, X11 or Wayland, and display scale.
- The "Flows & scripts" scene is the heaviest part of the film (about 15 fps on a HiDPI screen in the test container). Everything was measured in containers, not on real desktops.

## 1.8.0 — 2026-11-02

### Added
- **Linux is a full platform now (x64 and arm64)** — the experimental archive that only opened pages is replaced by the real browser: the same toolbar, tab strip, command palette and menus as on macOS, one WebKitGTK view per tab (switching tabs keeps scroll, form input and JS state), and the embedded MCP server with the same tools — navigation, snapshots, real mouse input (genuine GDK events, so clicks are trusted), typing, full-page screenshots, tabs and windows, cookies, clipboard, downloads, zoom, user-agent and light/dark emulation. Around it: private tabs and Ghost mode, the downloads shelf, pop-up windows, permission and HTTP-auth prompts, element fullscreen for video players, the picture menu (copy / save image), printing through the GTK print dialog, web notifications through libnotify, and the password vault — encrypted at rest, with its key in the desktop keyring (libsecret) when one is running and in a `0600` file otherwise, and your account password (checked through PAM) before a password is revealed or filled — a plain Allow / Cancel confirmation where PAM cannot be used (there is no Touch ID on Linux). The window is chromeless like the Windows one (minimise / maximise / close and drag / resize are drawn by the toolbar), opens straight into fullscreen like macOS (`--no-fullscreen` turns that off), and runs on X11 and, natively, on Wayland (`GDK_BACKEND=x11` forces X11). Needs GTK 3 and WebKitGTK 4.1: `libwebkit2gtk-4.1-0 libgtk-3-0 libnotify4 libsecret-1-0` on Debian / Ubuntu. Still Windows-only, as on macOS: `browser_pdf`; offline and geolocation emulation are not available on any WebKit build. Tested so far in containers only (X11 with software rendering on Ubuntu 24.04, x64 and arm64; Wayland on a headless compositor, keyboard and rendering but no mouse) — not yet across real desktops, GPUs and input-method frameworks, so reports are welcome.
- **Import from other browsers on Linux** — `about:import` and `browser_import` now read Chrome, Chromium, Brave, Edge, Vivaldi and Opera (also their Snap and Flatpak installs) and Firefox profiles: bookmarks, history, cookies and, for the Chromium family, saved passwords. Chromium's encrypted values (`v10`, and `v11` with the key from the desktop keyring) are decrypted; a value that cannot be read — a locked keyring, a Flatpak key held by the portal — is skipped and the page says why instead of importing garbage.
- **The browser-engine data is kept private and persists** — cookies, logins and web storage live in `~/.local/share/BrAIn/webkit` and survive a restart (session cookies still do not), and the app's data and cache folders are readable only by you.
- **Linux end-to-end tests** — `scripts/linux/e2e/run.sh` starts the real app in a container and drives it the way a user and an MCP client do, with real X input: window controls, popups, dialogs, fullscreen, private and Ghost tabs, the image menu and clipboard, notifications, a HiDPI display, shutdown behaviour, the importer and the PAM-gated vault. See `scripts/linux/e2e/README.md`.
- **Install on Linux** — the release now ships `BrAIn-<version>-linux-x64.tar.gz` and `BrAIn-<version>-linux-arm64.tar.gz`. Each unpacks to one folder, `BrAIn-<version>-linux-<arch>/`, holding the self-contained program, its native window library, `install.sh`, `uninstall.sh`, a menu entry and icons from 16 to 512 px. `./install.sh` installs for the current user without root (`~/.local/share/brain-browser`, the `brain` command in `~/.local/bin`, an application-menu entry; it also warns about missing system libraries and names the packages for apt, dnf, pacman and zypper); `sudo ./install.sh --system` installs to `/opt/brain-browser` for everybody; `uninstall.sh` removes it again and keeps your profile in `~/.local/share/BrAIn` unless you pass `--purge`. The menu entry does not register BrAIn as a handler for web links or files. `scripts/make-linux-dist.sh` builds both archives (it cross-publishes, so it runs on a Mac too) and `scripts/make-dist.sh` now calls it.
- **Self-update on Linux** — a per-user install updates itself like the macOS and Windows builds: **Check for updates** (toolbar menu or command palette) finds `BrAIn-<version>-linux-<x64|arm64>.tar.gz` in the latest GitHub release, downloads it into a private folder of yours (`~/.cache/BrAIn/update`, not a shared `/tmp`), verifies its SHA-256 and unpacks it next to the install folder. Before anything is replaced, the new program is run once (`--version`), so a damaged archive or a build for the wrong CPU is reported on the update card and the installed version is left exactly as it was. Then a detached helper waits for BrAIn to close, swaps the two folders (the old one stays as `.old` until the new one is in place, and is put back if the swap fails), starts the new version and cleans up; what it did is in `~/.local/share/BrAIn/update.log`. It only replaces a folder that holds nothing but what a release put there (the archive's `release.txt` lists it), so it can never delete files of yours that sit next to the program. A `--system` install in `/opt`, a distro package, a read-only folder, a folder with other files in it and a plain `dotnet publish` say *Updates unavailable* and why instead; update those by running `install.sh` from the new archive.
- **Linux tests: a Docker rig and CI** — `scripts/linux/test.sh` builds the rig image (Ubuntu 24.04: WebKitGTK 4.1, Xvfb, openbox) when it is missing and runs, inside it, the tab self-test (`--selftest-tabs`), the install / uninstall checks for both archives (per user and `--system`, including a path full of spaces and quotes) and the update tests: the helper script on its own (including a failed rename that must roll back) and the real app updating itself from a fake GitHub release served over TLS, plus the cases it must refuse. The new `linux-selftest.yml` workflow runs the self-test on an Ubuntu 24.04 runner, and builds both archives and install-checks the x64 one.

### Changed
- **Quitting is quicker (all platforms)** — BrAIn now ends about a second after its window closes, even while an AI agent still holds the MCP connection open. Before, it waited up to six seconds for that connection to wind down, and on Linux a windowless process could linger for good.
- **Update card wording** — the *Installing* text no longer talks about Windows permission prompts on Linux ("BrAIn is restarting and will reopen by itself"), and the menu row says plain *Updates unavailable* when an install cannot update itself; *(dev build)* is kept for dev builds.
- Docs: the README's requirements say .NET 10 (as the project has always required) and gain a Linux section; the website offers one download card per Linux CPU with install steps, a Linux MCP snippet and a corrected platform list.

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
