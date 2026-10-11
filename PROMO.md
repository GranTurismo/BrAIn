# BrAIn — 60-Second Promo

## One-liner

**BrAIn is a real desktop browser with an AI built into its bones — every page is inspectable, every action is verifiable, and every moment is rewindable.**

## The pitch (voiceover script, ~60s)

> **[0:00–0:08 — cold open]**
> Browsers haven't changed in fifteen years. Tabs, an address bar, and a prayer that your AI agent in another app isn't hallucinating what it clicked.
>
> **[0:08–0:20 — what it is]**
> BrAIn is a full desktop browser — native WebKit on macOS, Chromium on Windows — that *is* the AI's hands and eyes. An embedded MCP server lives inside the same process: no plugins, no relay servers, no screenshots-and-guesses. The agent sees the real DOM and acts on the real page, in the same window you're looking at.
>
> **[0:20–0:35 — the banger: Rewind]**
> And then it does something no browser has ever done. Rewind. A scrubber under the toolbar that takes the page *back in time*. The tweet that refreshed away. The price that changed mid-checkout. The form that ate itself. Drag left — the page reverts to exactly what it showed minutes ago, frozen, searchable, byte-for-byte. Your browsing finally has an undo.
>
> **[0:35–0:50 — paradise for devs]**
> For developers it's loaded: an in-window DevTools with a complete event-listener registry — every listener on every element, including delegated, anonymous, and already-removed ones, each with the exact line that registered it. Console, network feed, element inspect — plus the agent can answer "why doesn't my click work" because it sees everything the page did from the first byte.
>
> **[0:50–1:00 — trust + close]**
> And when the AI drives, you hold the kill switch — a live agent indicator, one keystroke pauses every action tool, and irreversible moments stay behind your fingerprint. BrAIn. The browser that remembers — and the first one your AI can actually use.

## On-screen captions / beat list

| Time | Visual | Caption |
|------|--------|---------|
| 0:00 | Plain browser window, quick tab/search actions | *"A real browser."* |
| 0:08 | MCP config → agent navigates, clicks, fills | *"An AI that doesn't guess."* |
| 0:20 | Drag the Rewind scrubber, page morphs backwards | *"Rewind. Time travel for the web."* |
| 0:28 | Search across snapshots finds gone text | *"Search what the page used to say."* |
| 0:35 | DevTools → listener registry on a live site | *"Every listener. Every call site."* |
| 0:44 | Agent pause, BLOCKED, resume | *"The AI works. You veto."* |
| 0:52 | Rewind timeline with `agent · click` tags | *"See exactly what it did."* |
| 0:56 | Logo | **BrAIn** |

## Feature sheet (what's actually in the product)

### Everyday browsing
- Tabs — drag to reorder, middle-click close, right-click menu, reopen closed (`Ctrl+Shift+T`), per-tab mute
- Omnibox — URLs, search, history autocomplete dropdown
- Bookmarks, history page, session restore, per-site feel
- Find-in-page, zoom, print/PDF, downloads with decisions log
- Private tabs — excluded from history, snapshots, everything
- Import wizard — pulls bookmarks/history/passwords from Chrome & co.
- Password vault — OS-auth gated (Touch ID / Windows Hello), generator, autofill with an in-field eye menu
- Command palette (`Ctrl+K`) — tabs, history, bookmarks, every action
- Picture-in-picture, link-hover status, live security indicator
- Aurora-inspired chrome — translucent, minimal, keyboard-first

### Rewind — the signature feature
- Scrubber under the toolbar: drag left, the page reverts to a frozen past state
- Compressed in-memory DOM snapshots — nothing touches disk, nothing leaves the machine
- Text search across past snapshots — "find when the page said this"
- Agent-tagged entries — the timeline shows exactly what the AI did, step by step
- Off by default; first open asks. Private tabs, login pages, and excluded sites are never recorded
- Adaptive capture ≈ ≤2.5% of a core; age/size/count budgets in the ⚙ menu

### Developer tools
- Custom in-window DevTools — elements tree, styles, console, network feed
- **Complete listener registry** — injected before page JS; sees delegated, `once`, anonymous, and removed listeners, each with registration call site; removable from the UI
- `browser_evaluate`, console capture, cookie inspection — all exposed to scripts too

### The AI layer (what makes it BrAIn)
- Embedded MCP server over stdio — ~35 tools: navigate, snapshot (accessibility tree with stable element refs), click/type/scroll, screenshots, tabs, waits, evaluate, cookies, network, listeners, rewind
- Point any MCP client at the binary — Claude, Devin, your own agent
- **Flows** — the agent proposes a saved, parameterized replay of what it did; nothing runs until you review and approve each step; approved flows become real MCP tools (`flow_*`)
- **Agent pause** — `Ctrl+Shift+.` blocks every action tool with a visible indicator; read-only tools keep working
- **Activity log** — every agent call recorded with arguments, results, and stable locators; secrets masked
- Action guardrails — sensitive moments (passwords, vault access) require OS-level auth

## Short description (store / social, ~280 chars)

> BrAIn is a desktop browser with a built-in AI agent (MCP server inside the process), Rewind — time travel that reverts any page to what it showed minutes ago — dev-grade tooling, and a human kill switch on every agent action. macOS & Windows.

## Why it's different (for the description footer)

- **The AI isn't a plugin** — it shares the process and the DOM. No extensions, no screen scraping, no CDP bridge process on macOS.
- **Rewind doesn't exist anywhere else** — other browsers can go Back. Only BrAIn can rewind the page itself.
- **Verifiable agents** — actions are logged, snapshot-tagged in Rewind, pausable mid-flight, and replayable as approved flows. Agentic browsing you can watch and audit, not trust blindly.
- **Dev-first instrumentation** — the browser sees what the page does before the page does anything.
