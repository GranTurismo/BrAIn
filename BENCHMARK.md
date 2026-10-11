# BrAIn vs Chrome — 25-Tab Benchmark

> Date: 2026-10-11 · Machine: macOS (Apple Silicon), same machine, same network
> Builds: BrAIn `dist/BrAIn.app` (local release build) · Google Chrome (installed stable)
> Method scripts: `/tmp/bench25_brain.py`, `/tmp/bench25_chrome.mjs` (harness described below)

## What was tested

- **25 real websites**, opened **one after another as new tabs** in a single
  browser window. **No tab was ever closed** — the comparison models a real
  heavy browsing session, not a lab single-page load.
- Two metrics per step:
  - **Load speed** — the page's own `loadEventEnd` from the W3C Navigation
    Timing API (ms from navigation start to the `load` event). Read from the
    page, not from wall-clock guesses.
  - **RAM** — summed RSS of **every process in the browser's process tree**
    (main process + all renderer/GPU/network/utility helpers), sampled ~1.5 s
    after each tab finished loading. For Chrome this is honest — its real
    footprint includes those helper processes. For BrAIn same rule.

## Why the numbers look the way they do (important context)

- **Chrome**: site isolation gives most sites their own renderer process.
  Every tab stays resident so tab switching is instant — the cost is a
  per-tab memory tax (measured avg **~516 MB/tab** on this mix).
- **BrAIn**: every tab is a real `WKWebView`, but WebKit shares a process
  pool and suspends/caches background pages aggressively. RAM even *drops*
  between heavy tabs as WebKit reclaims memory (measured avg **~7 MB/tab**).
- Both measurements are "RAM while N tabs are open" — same semantics, same
  `ps`-tree accounting. Chrome genuinely needs the memory for its
  always-resident model; BrAIn trades a little re-activation latency for a
  dramatically smaller footprint.
- **Timeouts** (`>30s`): three sites never fired `load` inside the 30 s
  budget in one browser or the other — `cnn.com` stalled in **both**.
  These rows are excluded from win/loss counting.

## Results

`—` = load event did not fire within 30 s in that run.

| # | Site | Load — Chrome | Load — BrAIn | Faster | RAM after tab — Chrome | RAM after tab — BrAIn |
|--:|---|---:|---:|:-:|---:|---:|
| — | *(idle / new tab)* | — | — | — | 1,211 MB | **156 MB** |
| 1 | example.com | **369** | 399 | Chrome | 1,601 | 176 |
| 2 | news.ycombinator.com | 1,459 | **1,017** | BrAIn | 1,812 | 185 |
| 3 | en.wikipedia.org | **1,120** | 1,346 | Chrome | 2,026 | 191 |
| 4 | stackoverflow.com | 928 | **135** | BrAIn | 2,176 | 181 |
| 5 | developer.mozilla.org | 937 | **921** | BrAIn | 2,521 | 199 |
| 6 | github.com | 4,469 | **1,452** | BrAIn | 3,377 | 193 |
| 7 | bing.com | **1,572** | 1,951 | Chrome | 5,678 | 199 |
| 8 | arstechnica.com | **4,804** | — | Chrome | 6,977 | 186 |
| 9 | apple.com | **2,072** | 2,948 | Chrome | 7,700 | 198 |
| 10 | cloudflare.com | 3,857 | **2,206** | BrAIn | 8,114 | 182 |
| 11 | reddit.com | **3,000** | 3,382 | Chrome | 8,660 | 192 |
| 12 | amazon.com | **1,830** | 5,070 | Chrome | 8,817 | 194 |
| 13 | youtube.com | 1,902 | **1,726** | BrAIn | 9,583 | 197 |
| 14 | bbc.com | — | **5,146** | BrAIn | 11,873 | 195 |
| 15 | cnn.com | — | — | n/a | 11,712 | 170 |
| 16 | nytimes.com | 7,120 | **2,580** | BrAIn | 11,738 | 189 |
| 17 | twitch.tv | **2,721** | 2,925 | Chrome | 12,167 | 167 |
| 18 | medium.com | **1,836** | 2,969 | Chrome | 12,355 | 156 |
| 19 | forbes.com | 11,767 | **8,505** | BrAIn | 12,670 | 163 |
| 20 | weather.com | — | **6,941** | BrAIn | 13,075 | 163 |
| 21 | ebay.com | **2,751** | 3,430 | Chrome | 13,600 | 145 |
| 22 | imdb.com | **2,046** | 2,667 | Chrome | 14,078 | 145 |
| 23 | wordpress.com | 3,583 | **3,268** | BrAIn | 14,150 | 181 |
| 24 | office.com | **1,603** | 2,467 | Chrome | 14,079 | 181 |
| 25 | x.com | **2,236** | 2,614 | Chrome | 14,102 | 174 |

Times in ms, RAM in MB (process-tree RSS).

## Headline numbers

| Metric | Chrome | BrAIn | BrAIn advantage |
|---|---:|---:|---:|
| Idle RAM | 1,211 MB | 156 MB | **7.8×** |
| RAM after 25 tabs | 14,102 MB | 174 MB | **81×** |
| Avg RAM per added tab | ~516 MB | ~7 MB | ~74× |
| Load-speed wins (of 24 comparable) | 13 | 11 | — |
| Median load delta where both loaded | Chrome −13% faster overall | — | — |

### Speed detail

- **BrAIn wins (11)**: HN, StackOverflow, MDN, GitHub, Cloudflare, YouTube,
  BBC, NYTimes, Forbes, Weather, WordPress.
- **Chrome wins (13)**: example.com, Wikipedia, Bing, ArsTechnica, Apple,
  Reddit, Amazon, Twitch, Medium, eBay, IMDB, Office, X.
- **Unresolved (1)**: CNN timed out in both.
- Largest BrAIn margins: GitHub −67%, NYT −64%, StackOverflow −85%,
  Cloudflare −43%, Forbes −28%.
- Largest Chrome margins: Amazon −64%, Apple −30%, ArsTechnica (BrAIn
  timed out).
- Single samples per site — real-world TTFB variance (±100–500 ms) flips
  close races; treat sub-~200 ms deltas as ties.

### RAM detail

- Chrome grows roughly linearly: +100–800 MB per tab, never releases; at
  14 GB for 25 sites it's using ~4% of a 32 GB machine *for one browser
  window*.
- BrAIn peaked at 199 MB (site 7) and *settled back* to 174 MB at the end —
  WebKit's shared process pool + page suspension means later tabs reuse
  earlier allocations.
- BrAIn's whole 25-tab session fits inside **14% of Chrome's idle
  footprint**.

## Methodology (reproducible)

1. **BrAIn**: spawned `dist/BrAIn.app` bound to a JSON-RPC stdio pipe
   (FIFO pair), drove it with its own MCP tools: `browser_new_tab` per
   site → polled `performance.getEntriesByType('navigation')[0].loadEventEnd`
   via `browser_evaluate` (≤30 s) → slept 1.5 s → summed `ps` RSS of the
   BrAIn process tree.
2. **Chrome**: spawned a dedicated instance
   (`--user-data-dir=/tmp/chrome-bench-profile --remote-debugging-port=9223`,
   fresh profile, no extensions) → per site `Target.createTarget` +
   `Page.navigate` over CDP → waited `Page.loadEventFired` (≤30 s) → read
   the same Navigation Timing value → same `ps`-tree RSS walk.
3. Same site list, same order, sequential runs (never concurrent, so no
   CPU/network contention). Order: BrAIn first, then Chrome.
4. Both browsers start from an essentially cold page-cache for these
   sites (fresh Chrome profile; BrAIn bench instance).

### Caveats (read before quoting)

- **Single sample** per site per browser — noisy sites (ads, A/B tests,
  video autoplay) vary run to run.
- Chrome's number includes extension/utility processes *of a fresh
  profile* — i.e. Chrome at its leanest, no user extensions installed.
- BrAIn's low per-tab cost partly reflects WebKit page suspension —
  switching back to a suspended tab may cost a quick re-render that
  Chrome never pays. RAM-vs-latency is a real trade-off; this benchmark
  measures the RAM side.
- `loadEventEnd` ≠ "usable". Some SPA-heavy sites keep loading resources
  after `load`; the metric is identical for both browsers, which is what
  makes it fair.
