# BrAIn website

Static product page. **No build step and no dependencies** — plain HTML/CSS/JS.

```
index.html            the page
assets/style.css      styles — the app's palette (theme.css): warm near-black, glass panels, ember accent
assets/app.js         aurora shader, demos (rewind / agent / tabs / shortcuts), chart, live download cards
assets/og.png         social-card image (2400×1260 screenshot of the hero)
assets/logo.png       128 px app icon for the nav, loader and footer
assets/favicon.png    64 px app icon · apple-touch-icon.png: 180 px, full-bleed (iOS rounds it)
assets/brain-512.png  512 px app icon
```

The icons are resized from `BrAIn/Assets/icon-src.png` (made by `BrAIn/Assets/gen-icon.py`); the
touch icon is that renderer's tile without the rounded-corner mask.

## Run locally

Open `index.html` directly, or serve it:

```
cd website && python3 -m http.server 8765
# → http://localhost:8765
```

The Download section reads the **latest GitHub release** in the browser
(`api.github.com/repos/GranTurismo/BrAIn/releases/latest`), so it needs network access.

## Ship a new release

Nothing to do on the website. Publish a GitHub release with the installers attached and the
Download section picks it up on the next page load:

```
bash scripts/make-dist.sh 1.3.2     # build installers into BrAIn/dist/ (Windows, macOS and both Linux archives)
gh release create v1.3.2 BrAIn/dist/BrAIn-1.3.2-* BrAIn/dist/BrAIn-Setup-1.3.2-x64.exe
```

(`BrAIn-1.3.2-*` already matches the two Linux archives, `BrAIn-1.3.2-linux-x64.tar.gz` and
`BrAIn-1.3.2-linux-arm64.tar.gz`; `scripts/make-linux-dist.sh` builds just those.)

How the page maps assets to cards (by file name, see `downloads()` in `assets/app.js`):

| File name | Card |
|---|---|
| `*.dmg` | macOS (`x64`/`intel` in the name → Intel, otherwise Apple Silicon) |
| `*.exe` | Windows x64 |
| `*-linux-x64.tar.gz` | Linux x64 |
| `*-linux-arm64.tar.gz` | Linux arm64 (the card marked "your PC" follows the visitor's user agent: `aarch64` → arm64, else x64) |

Version, size, download URL and SHA-256 come from the release itself (GitHub computes the
`digest` for every uploaded asset). Keep those file-name patterns when adding platforms (the app's own updater picks
`BrAIn-<ver>-linux-<x64|arm64>.tar.gz`, `*macOS*arm64*.dmg` and `BrAIn-Setup*.exe` the same way); a new
kind of file needs one line in `KINDS` plus its install steps.

The API is anonymous (60 requests/hour per visitor IP), so the page caches the answer for
10 minutes in `sessionStorage` and, if GitHub is unreachable or rate-limited, shows a link to
the releases page instead.

## Hosting

Any static host works — the site is plain files and carries no installers (they live in GitHub Releases).

## Deploy (Docker)

`Dockerfile` + `nginx.conf` build a small nginx image of the static site.
`.github/workflows/website-deploy.yml` runs on every push touching `website/**`:
build → push to `ghcr.io/granturismo/brain-web` (`latest` + `sha-<commit>`) → SSH to the
box → pull → restart the `brainweb` container on the `caddynet` docker network.
The `caddy` container then reverse-proxies **brain.gtgroup.dev → brainweb:80**
(no host port is published).

Repo secrets needed (already configured on this repo):

| Secret | Value |
|---|---|
| `SERVER_HOST` | `51.77.65.222` |
| `SERVER_USER` | `ubuntu` |
| `SERVER_PASSWORD` | SSH password |

The server pulls from GHCR using the workflow's own `GITHUB_TOKEN` while the job runs —
no PAT needed.

The Caddyfile block on the server is:

```
brain.gtgroup.dev {
    encode zstd gzip
    reverse_proxy brainweb:80
}
```

Manual run without CI:

```
docker build -t brainweb website/
docker run -d --name brainweb --restart unless-stopped --network caddynet brainweb
```

## Conventions

- No CDNs or external requests — the page must work offline.
- All demos are client-side simulations of real app behavior; claims match `README.md`,
  `RAM-BENCHMARK.md` (memory) and `BENCHMARK.md` (load times only). Benchmark numbers are the macOS runs, marked with caveats in the page.
- Test changes at ≥1440px and 390px widths; `prefers-reduced-motion` disables the shader
  and animations.
