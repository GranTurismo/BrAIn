# Linux test rig

BrAIn is only ever run on Linux inside a container: a headless desktop (Xvfb + openbox) with the libraries a real
user installs (GTK 3, WebKitGTK 4.1, libnotify, libsecret). Needs Docker (on a Mac: OrbStack or Docker Desktop)
and the .NET SDK on the host; nothing else.

```bash
scripts/linux/test.sh              # the tab self-test (default)
scripts/linux/test.sh package      # the release archive: install, check, uninstall
scripts/linux/test.sh update       # the self-updater
scripts/linux/test.sh all
```

The first run builds the image (`brain-linux-test`, a few minutes; again only when `LABEL brain.rig` in the
Dockerfile changes). Each run publishes the app for the container's CPU (arm64 on Apple Silicon, x64 elsewhere),
mounts it, and ends with `ALL PASSED` or `SOMETHING FAILED` and a non-zero exit. `KEEP=1` keeps the work folder
(published app, archives, screenshots, logs); a failed run keeps it anyway and prints where it is.

| Mode | What it checks | Script (runs in the container) |
|---|---|---|
| `selftest` | `BrAIn --selftest-tabs`: state survives a tab switch, views are evicted and restored, private tabs | `inside-selftest.sh` |
| `package` | the archive from `scripts/make-linux-dist.sh`: layout, ownership, `release.txt`; `install.sh` for a user and `--system` (files, menu entry validated with `desktop-file-validate`, icons, the `brain` link, an odd path with quotes and spaces, refusals), upgrade in place, `uninstall.sh` and `--purge` | `inside-package.sh` |
| `update` | the update helper script on its own (swap, rollback when a rename fails, stale `.old`, waits for the app, a zombie is not "running"); then the real app, installed from the archive, updating itself from a fake GitHub release (`fake-github.py`: HTTPS on the real `api.github.com` name via `--add-host` and a certificate the container trusts) started with Ctrl+K, *update*: swap, relaunch, clean-up; a damaged archive and a build that does not start leave the install untouched; releases that cannot be proven genuine are refused and nothing is installed (no `.sig`, signed by a key the app does not trust, a genuine signature of another version, no GitHub digest) — the fake releases are signed with a throw-away key that the app is told to trust through `BRAIN_UPDATE_TEST_KEY`, never the real one; a folder the user cannot rename is kept and restarted; a `/opt` install, a folder with a foreign file and a bare publish folder say *Updates unavailable* | `inside-helper.sh`, `inside-update.sh` + `update-user.sh` |

**The app's behaviour** (windows, input, dialogs, site data, notifications, HiDPI, ...) is covered by the end-to-end suite in
[`e2e/`](e2e/README.md): `scripts/linux/e2e/run.sh`. It drives the real app over MCP and real X input in the same kind of container
and asserts on what is actually on screen.

Reports and screenshots land in `<work folder>/out` (`selftest-tabs.txt`, `update-*.png`, ...). The same
`inside-package.sh` runs on the CI runner (`.github/workflows/linux-selftest.yml`), where `RIG` and `PKG` point at
these scripts and the archives.

## The image

Ubuntu 24.04, software GL (`LIBGL_ALWAYS_SOFTWARE=1`, a container has no GPU), `GDK_BACKEND=x11`, WebKit's sandbox
off (`WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1`: an unprivileged container has no user namespaces). Nothing sets
`WEBKIT_DISABLE_DMABUF_RENDERER`, so the rig runs the renderer a user gets by default. Look at a screen with
`import -window root /out/x.png` inside it and open the PNG on the host.

To test the other architecture, build an image for it and point the script at it (it runs under emulation, so it is
slow): `docker build --platform linux/amd64 -t brain-linux-x64 -f scripts/linux/Dockerfile scripts/linux` and
`BRAIN_TEST_IMAGE=brain-linux-x64 BRAIN_TEST_ARCH=x64 scripts/linux/test.sh package`.

`lib.sh` holds the small helpers the `inside-*.sh` scripts share (`check`, `expect_eq`, `start_desktop`, ...).
