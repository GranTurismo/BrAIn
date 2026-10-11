# BrAIn

A real desktop browser that any MCP-capable AI agent can drive — built on
[Photino.NET](https://github.com/tryphotino/photino.NET) (native WebView2 on
Windows, WKWebView on macOS) with a first-class [Model Context
Protocol](https://modelcontextprotocol.io) server embedded in the same process.

One binary = a windowed browser + a stdio MCP server exposing ~35 tools for
navigation, DOM inspection (accessibility-tree snapshots with element refs),
input (click/type/keys/scroll), screenshots, tabs/windows, console & network
capture, cookies, PDF export and more.

Every window shows a persistent browser chrome bar at the top — tab strip,
back/forward/reload, home, and an omnibox — so it works as a normal browser
for a human while remaining fully agent-drivable.

## Requirements

| Platform | Runtime |
|----------|---------|
| Windows  | .NET 8+ SDK, WebView2 Evergreen runtime (preinstalled on Win10/11) |
| macOS    | .NET 8+ SDK, no extra dependencies (WKWebView is in the OS) |

## Build & run

```bash
dotnet build
dotnet run --project BrAIn          # opens the browser window
dotnet run --project BrAIn -- --help
```

CLI options:

```
BrAIn [options] [url]
  url                 Initial URL (default: built-in new-tab page)
  --cdp-port <port>   Pin the DevTools port (Windows only; random otherwise)
  --no-toolbar        Don't inject the BrAIn toolbar into pages
```

Publish for distribution:

```bash
dotnet publish -c Release -r win-x64    # Windows
dotnet publish -c Release -r osx-arm64  # Apple Silicon
dotnet publish -c Release -r osx-x64    # Intel Mac
```

## Using it as an MCP server

The process speaks MCP over **stdio**. Point your client at the executable —
it opens a browser window and serves tools until the client disconnects
(closing stdin closes the browser and exits the process).

### Claude Desktop (`claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "brain": {
      "command": "C:\\path\\to\\BrAIn.exe"
    }
  }
}
```

macOS:

```json
{
  "mcpServers": {
    "brain": {
      "command": "/path/to/BrAIn"
    }
  }
}
```

To open on a specific page at startup, add `"args": ["https://example.com"]`.

### Typical agent loop

1. `browser_navigate` — go to a URL
2. `browser_snapshot` — get the accessibility tree; interactive elements get
   refs like `e1`, `e2`, …
3. `browser_click` / `browser_type` / `browser_press_key` — act on refs
4. `browser_state` / `browser_screenshot` — verify the result

## Tool surface

| Area | Tools |
|------|-------|
| Navigation | `browser_navigate`, `browser_back`, `browser_forward`, `browser_reload` |
| Page read | `browser_snapshot`, `browser_find`, `browser_describe`, `browser_listeners`, `browser_text`, `browser_html`, `browser_state` |
| Interaction | `browser_click`, `browser_double_click`, `browser_click_at`, `browser_type`, `browser_type_text`, `browser_press_key`, `browser_hover`, `browser_scroll`, `browser_scroll_to`, `browser_select_option`, `browser_set_checked` |
| Waits | `browser_wait_for` (element / text / URL / readyState) |
| Capture | `browser_screenshot` (PNG, optional element clip / full page), `browser_pdf` (Windows) |
| Windows/tabs | `browser_tabs`, `browser_new_tab`, `browser_switch_tab`, `browser_close_tab`, `browser_window_resize`, `browser_window_state`, `browser_set_viewport`, `browser_zoom` |
| Diagnostics | `browser_evaluate`, `browser_console_log`, `browser_network_requests`, `browser_cookies` |
| Page history | `browser_rewind` (list / read / search the tab's past states — opt-in, see Rewind below) |

Element selectors accept: snapshot refs (`e5`), CSS (`css:#id` or bare
`#id`), text (`text:Sign in`), role+name (`role:button:Submit`), or XPath
(`xpath://button`).

## How it works

```
BrAIn.exe
├── main thread (STA) ── Photino window + OS message loop
└── background task ─── MCP host (stdio transport, logs → stderr)

IBrowserDriver
├── Windows: CdpDriver ─── Chrome DevTools Protocol over
│                          WebView2's --remote-debugging-port
└── macOS:   WebKitDriver ─ objc_msgSend → WKWebView evaluateJavaScript,
                            results via window.external.sendMessage,
                            screenshots via CGWindowListCreateImage
```

Notes on the design:

- **Script execution** — released Photino has no JS-eval API (PR
  tryphotino/photino.NET#276 is still open), so Windows uses CDP and macOS
  talks to the WKWebView through the Objective-C runtime directly.
- **`brain.js`** is injected into every document: it builds the
  accessibility-tree snapshot, keeps stable element refs between calls, and
  dispatches realistic input events.
- **`chrome.js`** is injected alongside it: a Shadow-DOM-isolated top bar with
  a tab strip (favicons + loading spinner), back/forward/reload/stop/home
  buttons, an editable omnibox with a history dropdown, a loading progress
  bar, a link-hover status bubble and a live security indicator. BrAIn
  tabs are *logical* — Photino embeds exactly one webview per window, so all
  tabs share the single window and switching tabs navigates it to that tab's
  URL (mobile-browser style). It talks to the host over Photino's
  `window.external` bridge (`sendMessage` commands out, `SendWebMessage` state
  pushes in) and auto-hides while screenshots are captured.

  Shortcuts: `Ctrl/Cmd+T` new tab, `Ctrl/Cmd+W` close tab, `Ctrl/Cmd+Shift+T`
  reopen closed tab, `Ctrl/Cmd+L` focus omnibox, `Ctrl+R`/`F5` reload,
  `Esc` stop loading, `Alt+←/→` back/forward, `Ctrl+Tab` cycle tabs,
  `Ctrl+Shift+Y` Rewind.
  Middle-click a tab or link closes it / opens it in a new tab; right-click a
  tab for the tab menu (reload, duplicate, close others/right); drag tabs to
  reorder them; scroll the strip with the wheel.
- **`[STAThread]` matters** — `Main` must be a synchronous STA entry point;
  WebView2 environment creation fails with `RPC_E_CHANGED_MODE` on an MTA
  thread, and `[STAThread]` on `async Main` is silently ignored.
- **User-data folders are per-process** (`%LOCALAPPDATA%\BrAIn\profile-<pid>`)
  because WebView2 environments sharing a folder share a browser process,
  which would break the per-instance debug port.

## Rewind — time travel for pages

**Off by default** — recording pages costs extra CPU and memory, so nothing
runs until the user turns it on (⋮ → *Rewind — page time travel*, the toolbar
rewind button, or `Ctrl+Shift+Y`; the first open explains the cost and asks
to confirm).

Once on, each page view keeps a compressed timeline of what the tab's page
looked like: a MutationObserver marks the document dirty, and a throttled
capture clones the DOM, strips scripts/iframes/media sources and any
secret-looking form values, bakes in live input state and CSSOM-only styles,
then gzips it (in-page `CompressionStream`, falling back to host-side gzip).
The throttle adapts to how expensive a capture is (~2.5% of a core worst
case), identical snapshots are deduplicated by hash, and the store evicts by
age, total bytes and per-tab count — all configurable from the ⚙ menu.

The Rewind bar has a scrubber (right end = Live), prev/next steppers, a text
search across snapshots, and a frozen `srcdoc` viewer (sandboxed,
script-less, links inert). Snapshots live **in memory only** — private tabs,
login pages and excluded sites are never recorded, and closing a tab drops
its history.

The agent can read the timeline through `browser_rewind` — but only if the
user separately allows it (⚙ → *Let the AI agent read this history*). When
allowed, action tool calls also leave `agent · <tool>`-tagged snapshots so
the timeline shows what the agent did.

## Activity log & flows

**Activity log.** Every MCP tool call is recorded (time, tool, arguments, result,
duration, page URL) in `calls.jsonl` in the app data folder, newest 1,000 kept.
Clicks and typing also store *stable locators* (`css:#id`, `role:button:Save`,
`text:Next`, …) next to the ref the agent used, because refs like `e7` change on
every page load. Password fields, `autocomplete="cc-*"` inputs and cookie values
are masked (`••••••`) and never written to disk.

**Flows** are saved, parameterised replays of a task. They appear to agents as
real tools named `flow_<name>`.

1. An agent finishes a task and calls `browser_activity` to see its call ids,
   then `browser_save_flow(name, description, callIds, parameters)` where each
   parameter gives the literal `example` value to replace with `{{name}}`.
   Read-only and failed calls are dropped; masked secrets become required
   arguments (`secret_1`, …) and are never stored.
2. This only creates a **proposal**. Nothing is callable until you open
   **⋮ → Flows & activity → Pending approval**, review/edit the steps, and approve.
3. Approved flows live in `flows/<name>.json` and are registered with the MCP
   server at runtime (clients are notified via `tools/list_changed`; if a client
   does not refresh its tool list, agents can use `browser_run_flow`).

You can also build one yourself: **Activity** tab → tick calls → *Make flow from
selected*, or *+ New flow*. The editor shows every step; use *Edit as JSON* for
full control. A step is `{ "tool": "browser_click", "args": {"element": "role:button:Save"},
"alts": ["css:#save"], "optional": false }`; strings may contain `{{param}}`.
When a step runs, `args.element` and then each `alts` locator are tried until one
resolves on the page. If a step fails, the flow stops and tells the agent which
step and what the page shows, so it can finish manually.

Allowed step tools: navigate/back/forward/reload, click, click_at, double_click,
hover, type, type_text, press_key, select_option, set_checked, scroll,
scroll_to, wait_for, text, evaluate, new_tab, switch_tab, upload. Cookies and
screenshots are deliberately excluded. Because an approved flow can run
`browser_evaluate`, only approve flows you have read.

### Agent indicator & pause

Once an agent calls a tool, an **Agent** button appears in the toolbar. It pulses
(with a sweep along the toolbar's bottom edge) while a tool call runs. Click it,
use ⋮ → *Pause agent*, or press `Ctrl+Shift+.` to pause: action tools
(click, type, navigate, flows…) then return a `BLOCKED` error telling the agent to
wait, running flows stop before their next step, and read-only tools (state,
snapshot, text, screenshot…) keep working. The button turns amber and counts
blocked attempts. Click again to resume. Pause is per app run (not persisted).

## Known limitations

- macOS build is compile-verified only; the WKWebView bridge needs on-device
  testing.
- `browser_pdf` is Windows-only (CDP `Page.printToPDF`).
- `browser_click_at` uses real OS-level input on Windows; the JS fallback on
  macOS dispatches synthetic events.
