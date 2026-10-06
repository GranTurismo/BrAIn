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
| Page read | `browser_snapshot`, `browser_find`, `browser_describe`, `browser_text`, `browser_html`, `browser_state` |
| Interaction | `browser_click`, `browser_double_click`, `browser_click_at`, `browser_type`, `browser_type_text`, `browser_press_key`, `browser_hover`, `browser_scroll`, `browser_scroll_to`, `browser_select_option`, `browser_set_checked` |
| Waits | `browser_wait_for` (element / text / URL / readyState) |
| Capture | `browser_screenshot` (PNG, optional element clip / full page), `browser_pdf` (Windows) |
| Windows/tabs | `browser_tabs`, `browser_new_tab`, `browser_switch_tab`, `browser_close_tab`, `browser_window_resize`, `browser_window_state`, `browser_set_viewport`, `browser_zoom` |
| Diagnostics | `browser_evaluate`, `browser_console_log`, `browser_network_requests`, `browser_cookies` |

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
  `Esc` stop loading, `Alt+←/→` back/forward, `Ctrl+Tab` cycle tabs.
  Middle-click a tab or link closes it / opens it in a new tab; right-click a
  tab for the tab menu (reload, duplicate, close others/right); drag tabs to
  reorder them; scroll the strip with the wheel.
- **`[STAThread]` matters** — `Main` must be a synchronous STA entry point;
  WebView2 environment creation fails with `RPC_E_CHANGED_MODE` on an MTA
  thread, and `[STAThread]` on `async Main` is silently ignored.
- **User-data folders are per-process** (`%LOCALAPPDATA%\BrAIn\profile-<pid>`)
  because WebView2 environments sharing a folder share a browser process,
  which would break the per-instance debug port.

## Known limitations

- macOS build is compile-verified only; the WKWebView bridge needs on-device
  testing.
- `browser_pdf` is Windows-only (CDP `Page.printToPDF`).
- `browser_click_at` uses real OS-level input on Windows; the JS fallback on
  macOS dispatches synthetic events.
