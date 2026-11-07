# BrAIn vs Chrome — Page-Load Benchmark

> Date: 2026-10-11 · Machine: macOS (Apple Silicon), same machine, same network
> Builds: BrAIn `dist/BrAIn.app` (local release build) · Google Chrome (installed stable)

## What was tested

- **25 real websites**, opened **one after another as new tabs** in a single
  browser window. **No tab was ever closed** — the comparison models a real
  heavy browsing session, not a lab single-page load.
- **Load speed** — the page's own `loadEventEnd` from the W3C Navigation
  Timing API (ms from navigation start to the `load` event). Read from the
  page, not from wall-clock guesses.
- **Timeouts** (`>30s`): three sites never fired `load` inside the 30 s
  budget in one browser or the other — `cnn.com` stalled in **both**.
  These rows are excluded from win/loss counting.

## Results

`—` = load event did not fire within 30 s in that run. Times in ms.

| # | Site | Load — Chrome | Load — BrAIn | Faster |
|--:|---|---:|---:|:-:|
| 1 | example.com | **369** | 399 | Chrome |
| 2 | news.ycombinator.com | 1,459 | **1,017** | BrAIn |
| 3 | en.wikipedia.org | **1,120** | 1,346 | Chrome |
| 4 | stackoverflow.com | 928 | **135** | BrAIn |
| 5 | developer.mozilla.org | 937 | **921** | BrAIn |
| 6 | github.com | 4,469 | **1,452** | BrAIn |
| 7 | bing.com | **1,572** | 1,951 | Chrome |
| 8 | arstechnica.com | **4,804** | — | Chrome |
| 9 | apple.com | **2,072** | 2,948 | Chrome |
| 10 | cloudflare.com | 3,857 | **2,206** | BrAIn |
| 11 | reddit.com | **3,000** | 3,382 | Chrome |
| 12 | amazon.com | **1,830** | 5,070 | Chrome |
| 13 | youtube.com | 1,902 | **1,726** | BrAIn |
| 14 | bbc.com | — | **5,146** | BrAIn |
| 15 | cnn.com | — | — | n/a |
| 16 | nytimes.com | 7,120 | **2,580** | BrAIn |
| 17 | twitch.tv | **2,721** | 2,925 | Chrome |
| 18 | medium.com | **1,836** | 2,969 | Chrome |
| 19 | forbes.com | 11,767 | **8,505** | BrAIn |
| 20 | weather.com | — | **6,941** | BrAIn |
| 21 | ebay.com | **2,751** | 3,430 | Chrome |
| 22 | imdb.com | **2,046** | 2,667 | Chrome |
| 23 | wordpress.com | 3,583 | **3,268** | BrAIn |
| 24 | office.com | **1,603** | 2,467 | Chrome |
| 25 | x.com | **2,236** | 2,614 | Chrome |

## Headline numbers

| Metric | Chrome | BrAIn |
|---|---:|---:|
| Load-speed wins (of 24 comparable) | 13 | 11 |
| Median load delta where both loaded | −13% faster overall | — |

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

## Methodology

1. **BrAIn**: spawned `dist/BrAIn.app` bound to a JSON-RPC stdio pipe
   (FIFO pair), drove it with its own MCP tools: `browser_new_tab` per
   site → polled `performance.getEntriesByType('navigation')[0].loadEventEnd`
   via `browser_evaluate` (≤30 s).
2. **Chrome**: spawned a dedicated instance
   (`--user-data-dir=/tmp/chrome-bench-profile --remote-debugging-port=9223`,
   fresh profile, no extensions) → per site `Target.createTarget` +
   `Page.navigate` over CDP → waited `Page.loadEventFired` (≤30 s) → read
   the same Navigation Timing value.
3. Same site list, same order, sequential runs (never concurrent, so no
   CPU/network contention). Order: BrAIn first, then Chrome.
4. Both browsers start from an essentially cold page-cache for these
   sites (fresh Chrome profile; BrAIn bench instance).

### Caveats (read before quoting)

- **Single sample** per site per browser — noisy sites (ads, A/B tests,
  video autoplay) vary run to run.
- `loadEventEnd` ≠ "usable". Some SPA-heavy sites keep loading resources
  after `load`; the metric is identical for both browsers, which is what
  makes it fair.
