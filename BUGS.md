# Fixed

* ~~Support of Passkeys (it doesn't show passkey verification dialog)~~
  - macOS now sends real OS-level input (CGEvent) for `browser_click` /
    `browser_click_at`, so clicks carry user activation like a human click.
  - `BrAIn.entitlements` adds `com.apple.web-browser.public-key-credential`;
    `mac-permissions.sh` signs with it. Rebuild via `rebuild-mac.command`.
  - Note: the passkey sheet itself (Touch ID) always needs the human — the
    agent must hand off with `browser_ask_human` at that point.
* ~~Support of popup windows~~
  - macOS: `WkPopup` wraps the WKWebView UIDelegate. Plain `window.open` /
    `target=_blank` open as BrAIn tabs; sized popups (OAuth/payment) get a real
    NSWindow so `window.opener` survives. `webViewDidClose:` cleans up.
  - Windows already routed `NewWindowRequested` the same way.
* ~~Agent doesn't pause when the user must do a step manually~~
  - `browser_ask_human` now reaches every tab (card was only pushed to the
    main tab, so it was invisible when the agent worked elsewhere).
  - The MCP gate now also blocks `browser_run_flow` while a handoff is
    pending — flows replay actions and could race the user's input.

# Still open / verify

* Passkey E2E on macOS: the entitlement is embedded by ad-hoc signing, but
  Apple's docs tie full WebAuthn to a provisioning profile. If the sheet
  still doesn't appear after rebuild, the app needs a real Developer ID
  signature. Until then `browser_ask_human` + "Try another way" is the
  fallback path.
