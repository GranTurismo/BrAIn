# BrAIn website

Static product page. **No build step and no dependencies** — plain HTML/CSS/JS.

```
index.html            the page
assets/style.css      styles
assets/app.js         aurora shader, demos (rewind / agent / tabs / shortcuts), chart, downloads logic
assets/og.png         social-card image (screenshot of the hero)
downloads/            the actual installers + SHA256SUMS.txt + downloads manifest
```

## Run locally

Open `index.html` directly, or serve it (needed for the in-browser checksum verify, which
reads the files over HTTP):

```
cd website && python3 -m http.server 8765
# → http://localhost:8765
```

## Ship a new release

```
bash scripts/make-dist.sh 1.0.2     # build installers into BrAIn/dist/
bash scripts/build-site.sh          # copies newest installers into downloads/, regenerates
                                    # SHA256SUMS.txt, downloads.json and downloads.js
```

`downloads.js` is what makes the Download section work when the file is opened from disk
(`fetch()` can't read `file://`). Keep it in sync by always using `build-site.sh`.

## Hosting

Any static host works. For GitHub Pages point it at this directory. The installers
(26–33 MB) are under the 100 MB Pages file limit — but ~60 MB in git is heavy for every
clone, so consider moving `downloads/` to a GitHub Release and pointing `downloads.json`
URLs at it if repo size becomes a problem.

## Deploy (Docker)

`Dockerfile` + `nginx.conf` build a self-contained nginx image (installers included).
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
- All demos are client-side simulations of real app behavior; claims match `README.md` /
  `BENCHMARK.md` (benchmark numbers are the macOS run, marked with caveats in the page).
- Test changes at ≥1440px and 390px widths; `prefers-reduced-motion` disables the shader
  and animations.
