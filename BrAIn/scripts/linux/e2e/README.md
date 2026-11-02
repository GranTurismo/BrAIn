# Linux end-to-end tests

BrAIn runs in a container with a headless X session (Xvfb + openbox). The tests drive it the way a user and an MCP client
would — the MCP protocol over stdio, and **real** X key and mouse events — and assert on what the app does and on what is
really on screen (pixels, window states, the clipboard, files on disk). Nothing touches your machine: no display, no home
folder, no clipboard, no keyring.

```sh
scripts/linux/e2e/run.sh                 # everything (builds the image and publishes the app first)
scripts/linux/e2e/run.sh 02 hidpi        # by number or part of the name
scripts/linux/e2e/run.sh --list
scripts/linux/e2e/run.sh --arch x64      # the x64 build under emulation (slow but works with Docker Desktop/OrbStack)
scripts/linux/e2e/run.sh --shots /tmp/s  # keep the screenshots the tests take
```

Needs Docker and the .NET SDK. Exit status = number of failed tests.

## What is covered

| test | what it proves |
| --- | --- |
| `01_startup_toolbar` | starts, toolbar really painted, MCP answers, page loads |
| `02_palette_dimming` | the palette dims the whole window however the toolbar was used before |
| `03_keys_after_focus_moves` | no key is lost or leaked when focus moves between toolbar and page |
| `04_popups` | links/`window.open` → tabs; sized popups → real windows with a working `window.opener` |
| `05_dialogs` | permission prompt, HTTP basic auth |
| `06_fullscreen` | element fullscreen enters and leaves, toolbar comes back |
| `07_site_data` | private/ghost isolation, cookies + web storage survive a restart, data folder is 0700 |
| `08_image_menu_clipboard` | right-click Copy/Save image on the real clipboard; clipboard tool |
| `09_notifications` | web notifications through a mock `org.freedesktop.Notifications` service |
| `10_window_management` | window state/size tools; the toolbar's own drag/resize/min/max/close |
| `11_hidpi` | a scaled display (`GDK_SCALE=2`): window size, input mapping, screenshots |
| `12_exit_behaviour` | the process ends promptly when the window closes / the client leaves |
| `13_color_scheme` | light by default, per-tab dark emulation survives tab switches |
| `14_downloads` | download lands in `~/Downloads`, panel + tool |
| `15_tools_smoke` | ~40 MCP tools answer sensibly and have real effects |
| `16_keyboard_input_print` | shortcuts, Georgian/accented text, print dialog |
| `17_selftest_tabs` | the app's own `--selftest-tabs` |
| `18_import_vault_keyring_pam` | a real account: importing fake Chrome/Chromium/Opera/Snap/Flatpak/Firefox profiles (v10 + v11 cookies and passwords), the vault key in the keyring, a password reveal gated by the real PAM dialog |
| `19_import_vault_no_keyring` | a minimal session: key file (0600) fallback, Allow/Cancel fallback, importing through the MCP tool |

## Writing a test

A test is a Python file in `tests/` that uses `lib/mcp.py` (`MCP`, `check`, `finish`, `px`, `sh`, …) and `lib/fixtures.py`
(a tiny website on `127.0.0.1:8765`). Header comments ask for extras:

```python
# docker: -e GDK_SCALE=2 -e RES=3200x2000x24     extra docker flags (here: a scaled 3200x2000 screen)
# needs: notifd                                   a session bus with a mock notification service
# as: keyring-user                                (with `# docker: --user root`) run as an account with a known password and an
#                                                  unlocked keyring on a session bus, like a real desktop session
```

Lessons that cost real time, built into the helpers:

* **Never judge "is it painted" by brightness.** The GTK theme grey is 246/255 and looks bright; a flat band of any colour
  has a standard deviation near 0, a real toolbar ~22 (`strip_stddev()`).
* Look at screenshots (`desk(name)`, kept with `--shots`) when a pixel check surprises you.
* Real input needs human-ish pacing: a window-manager drag needs the button really held down for a moment.

## Not covered here

* **Wayland.** Keyboard and rendering were smoke-tested on headless sway (`sway` + `grim` + `wtype`) — the app starts, paints
  and passes `--selftest-tabs` — but a headless compositor has no pointer, so mouse behaviour is untested.
* Real GPUs, NVIDIA, IME frameworks (ibus/fcitx), HDR, multi-monitor.
