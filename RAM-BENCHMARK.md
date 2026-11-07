# BrAIn vs Chrome: RAM with 12 real tabs

> Date: 2026-11-05 · MacBook, Apple M5, 16 GB, macOS 27.2 (on battery) · same machine, same network, one browser at a time
> Browsers: **Google Chrome 157.0.8103.38** (fresh profile) · **BrAIn 1.8.4** (the released app) · **BrAIn main** (`37459dd` = 1.8.4 + the background-tab memory fix, Release build; released as 1.8.6)
> Each browser was run twice with an identical, scripted session. Every number is the memory Activity Monitor shows, summed over all of the browser's processes.

## Short answer

- **Just launched, BrAIn is lighter**: 277 MB vs Chrome's 387 MB (28% less).
- **With 12 real sites open, they are level.** BrAIn main and Chrome both sit at about 3.7 GB, and the gap is within run-to-run noise.
- **BrAIn 1.8.4 (the current release) uses 7–17% more than Chrome** at every point after the sites are open: 4.1–4.7 GB with all 12 sites open. That fits the "5 GB on 12 tabs" seen in daily use.
- **The memory fix in `main` saves about 390–470 MB (8–12%)** over 1.8.4 in this test.
- **Almost all of the memory is the sites and WebKit, not BrAIn's code.** A plain WebKit view with no BrAIn code used about the same, 3.26 GB against BrAIn main's 3.33 GB. See [Is it BrAIn or WebKit?](#is-it-brain-or-webkit).
- The old "81× less RAM than Chrome" figure from [BENCHMARK.md](BENCHMARK.md) was not true. See [why the old numbers were wrong](#why-the-old-benchmark-said-81).

## Results

MB, mean of 2 runs (range in brackets). Smaller is better.

| Moment | Chrome | BrAIn 1.8.4 | BrAIn main | BrAIn main vs Chrome |
|---|---:|---:|---:|---:|
| Just launched (start page) | 387 (376–399) | 276 (276–276) | **277** (275–278) | **−28%** |
| Right after the 12th site loaded | **4,002** (3,696–4,308) | 4,687 (4,470–4,905) | 4,292 (3,998–4,586) | +7% |
| Left alone, 1–2.5 min later | **3,697** (3,673–3,721) | 4,134 (4,026–4,241) | 3,748 (3,603–3,893) | +1% |
| After clicking through every tab | 4,014 (3,976–4,051) | 4,325 (4,133–4,517) | **3,856** (3,681–4,031) | −4% |
| 1 minute after that | **3,836** (3,810–3,862) | 4,354 (4,147–4,561) | 3,881 (3,721–4,041) | +1% |
| 45 s after closing 6 of the tabs | 3,240 (3,224–3,256) | 3,470 (3,417–3,522) | **3,049** (2,831–3,266) | −6% |

Real sites change from load to load (ads, videos, A/B tests), so the two runs of one browser differ by up to 600 MB right after loading and by up to 450 MB later. Treat differences under about 5% as a tie.

Chrome got one free pass: Reddit gave it a "Prove your humanity" check page instead of the subreddit, in both runs. In BrAIn the real Reddit page added 307–337 MB. If Chrome's real Reddit page costs about the same, Chrome would land a few hundred MB above BrAIn main. That part was not measured. See [things that tilt the numbers](#things-that-tilt-the-numbers).

## RAM as the tabs were opened

Total after each site finished loading (MB, mean of 2 runs). Every site opens in a new tab that stays open.

| # | Site | Chrome | BrAIn 1.8.4 | BrAIn main |
|--:|---|---:|---:|---:|
| 1 | en.wikipedia.org | 469 | 450 | 404 |
| 2 | news.ycombinator.com | 427 | 468 | 394 |
| 3 | bbc.com | 772 | 750 | 665 |
| 4 | github.com | 1,277 | 991 | 911 |
| 5 | reddit.com (Chrome: check page only) | 1,273 | 1,327 | 1,220 |
| 6 | developer.mozilla.org | 1,357 | 1,416 | 1,254 |
| 7 | nytimes.com | 1,868 | 2,295 | 2,073 |
| 8 | stackoverflow.com | 2,129 | 2,716 | 2,422 |
| 9 | theverge.com | 2,807 | 3,151 | 2,890 |
| 10 | edition.cnn.com | 3,320 | 3,482 | 3,168 |
| 11 | youtube.com | 3,584 | 3,948 | 3,650 |
| 12 | amazon.com | 4,002 | 4,687 | 4,292 |

Both browsers grow by roughly 300–330 MB per site on this mix. Neither has a cheap per-tab cost.

## Where the memory goes

Measured after 2.5 minutes with the 12 sites open (MB, mean of 2 runs).

| Part | Chrome | BrAIn 1.8.4 | BrAIn main |
|---|---:|---:|---:|
| Web pages | 2,566 (60 renderer processes) | 3,567 (16 WebContent processes) | 3,106 (16 WebContent processes) |
| GPU / compositing | 609 | 285 | 195 |
| *Web pages + GPU together* | *3,175* | *3,852* | *3,301* |
| Main browser / app process | 273 | 226 | 226 |
| Network | 69 | 71 | 89 |
| Other (storage, utilities) | 104 | — | — |
| **Total** | **3,621** | **4,149** | **3,616** |

- **Compare the page and GPU processes together.** WebKit keeps most of its drawing memory in the page processes, and Chrome keeps it in its GPU process. So the two gaps (pages 3.1 vs 2.6 GB, GPU 195 vs 609 MB) mostly cancel. Together, BrAIn main is 4% above Chrome, and Chrome skipped the real Reddit page.
- **The 16 WebContent processes** are the 13 tabs, the toolbar, and one or two more that WebKit starts on its own.
- **Chrome uses many more processes:** 60 renderers for 13 tabs. Chrome gives every cross-site frame its own process (site isolation): ads, embedded players, sign-in widgets.
- **From 1.8.4 to main:** background tabs now leave the window, so WebKit frees their memory. That cut the page processes by about 460 MB and the GPU process by about 90 MB. It accounts for most of why 1.8.4 sits above Chrome.

## Is it BrAIn or WebKit?

Follow-up run on 2026-11-07, 2 runs each in alternating order. "Bare WebKit" is a small test app that opens plain `WKWebView`s with no BrAIn code. It uses the same sites, schedule, user agent and page size as BrAIn, and background views leave the window as in BrAIn main. Memory was read after 2.5 minutes with the 12 sites open (MB, mean of 2 runs).

| | Bare WebKit | BrAIn main | Chrome 157 |
|---|---:|---:|---:|
| **Total** | **3,256** | **3,326** | **3,313** |
| Web page + GPU processes | 3,096 | 3,047 | 2,916 |
| Main app / browser process | 39 | 202 | 269 |
| Network and other helpers | 121 | 76 | 128 |
| *Inside those totals:* | | | |
| Page heaps (JavaScript, page structure, layout) | 2,203 | 2,139 | 2,013 |
| Drawing memory | 379 | 534 | 405 |

- **BrAIn's code is not the main cost.** BrAIn main is 70 MB above bare WebKit at this reading, and 135 MB above by the median of the left-alone samples. The two bare WebKit runs differ by about 350 MB, so this overhead is only roughly known: about 100–300 MB.
- **Two BrAIn costs are clear:**
  - The app process is 163 MB larger than the test app's. That's the .NET runtime and BrAIn's own code and data.
  - Drawing memory is about 155 MB higher. The cause wasn't measured.
  - These add up to more than the total difference because the live sites vary by more than that between runs.
- **BrAIn's scripts in every page didn't show up.** An earlier experiment put them at about 10 MB per tab. At that size they are smaller than the noise here.
- **Page heaps are close.** Chrome's are 6–9% lower here, but it skipped the real Reddit and Amazon pages (see below), so this doesn't show that either engine is leaner. These figures are "WebKit Malloc" for BrAIn and bare WebKit, and the two app-specific memory tags Chrome's allocators use. WebKit also lists WebAssembly and JIT code separately (96–132 MB), which is left out above.
- **The page + GPU gap matches the first test.** BrAIn main is 131 MB above Chrome here and was 126 MB above on 2026-11-05.

Limits of this follow-up:

- **Chrome got two check pages.** Reddit and Amazon both showed it a check page instead of the real site, in both runs. In BrAIn the real pages added 320–390 MB (Reddit) and 720–830 MB (Amazon) when they loaded, though that falls later. Chrome's totals here are therefore low; it reached 3.7 GB on 2026-11-05 with the real Amazon page.
- **One Chrome run also had no Hacker News tab.** That's about 15 MB in WebKit.
- **Windows weren't equally visible.** BrAIn's window was not on the current screen when checked, while Chrome's was (33% and 0% covered). Bare WebKit's wasn't checked. Since the page + GPU gap matches the first test, where both were on screen, it doesn't appear to have mattered.

## How it was measured

- **Metric:** memory footprint (`phys_footprint`, the "Memory" column in Activity Monitor), added up over every process the browser runs.
  - **Chrome:** the browser process and all its child processes (GPU, network, storage, utilities, every renderer, and the updater it launched).
  - **BrAIn:** the app process and its WebKit helper processes (WebContent, GPU, Networking). macOS starts those helpers itself, so they are not BrAIn's children. They were matched to BrAIn through macOS's responsible-process link, and only helpers started after the run began were counted.
- **Fresh profiles every run:**
  - Chrome got a new `--user-data-dir`: no extensions, not signed in, default settings.
  - BrAIn ran in a new sandboxed home folder, with the welcome screen marked as already seen.
- **Sites, in this order:**
  1. Wikipedia "Web browser"
  2. Hacker News
  3. BBC News
  4. GitHub WebKit
  5. r/programming
  6. MDN JavaScript
  7. NYTimes
  8. Stack Overflow questions
  9. The Verge
  10. CNN
  11. YouTube
  12. Amazon
- **The same schedule for every run:**
  1. Launch, wait 15 s, then take 3 samples 5 s apart. "Just launched" is their median.
  2. Open each site in a new front tab. Wait until the page reports `document.readyState == "complete"` (at most 30 s), then wait 3 s more and take a sample.
  3. Leave the browser alone for 150 s, sampling every 15 s. The "left alone" row is the median of the samples 60–150 s in.
  4. Switch to each site tab in turn, 5 s each, then sample. Wait 60 s and sample again.
  5. Close the first 6 site tabs, 1 s apart. Wait 45 s and sample.
- **How each browser was driven:**
  - BrAIn through its own stdio MCP server: `browser_new_tab`, `browser_evaluate`, `browser_switch_tab`, `browser_close_tab`.
  - Chrome through its DevTools port: `/json/new`, `/json/activate` and `/json/close`, plus `Runtime.evaluate` to check page readiness.
- **Window:** both browsers maximized on the same 1470×923-point screen area, at 2× scale.
  - Chrome's page area was 1470×836 and BrAIn's 1470×877 (its toolbar is shorter), so BrAIn drew about 5% more pixels.
  - The window was checked at four points per run. It was on screen in every check. In Chrome run 1 it was covered by another window during launch and loading; its later numbers match run 2 within 75 MB.
- **Order:** Chrome, BrAIn main, BrAIn 1.8.4, then 1.8.4, main, Chrome, with a 20 s pause between runs. macOS reported 54–57% of memory available at the start of every run.

## Things that tilt the numbers

- **Reddit (favors Chrome).** Reddit showed a fresh Chrome profile a "Prove your humanity" check page in both runs. A separate check with no page scripting gave the same page, so the test driver didn't cause it. BrAIn got the real subreddit, which cost it about 310–335 MB.
- **Fresh profiles (favor Chrome).** Most people's Chrome has extensions, and each one adds its own process. Real Chrome sessions are heavier than this.
- **Chrome's Memory Saver did not act.** Its renderer count stayed flat at about 60 for the whole run, so no tab was put to sleep. Chrome can discard tabs that sit unused for a long time (Memory Saver) or when memory runs low. BrAIn on macOS has nothing like it, so in long sessions Chrome can pull ahead.
- **Some BrAIn tabs never answered the readiness check.**
  - `browser_evaluate` didn't answer within 30 s on bbc.com and cnn.com in every BrAIn run, on theverge.com in 3 of 4 runs, and on youtube.com in both 1.8.4 runs. BrAIn's tab list showed no title for some of them.
  - Their memory grew like everywhere else, so they did load. But the extra waiting made BrAIn runs take 600–660 s in total, against Chrome's 390 s.
  - This is a separate BrAIn issue, seen with both builds.
- **Compressed memory.** Activity Monitor's number counts memory that macOS has compressed at its full size, for both browsers. In the follow-up runs, macOS's `footprint` tool reported 55–84% of each browser's memory as compressed or swapped out. The physical RAM in use is well below the number shown, for both browsers.
- **Two runs per browser.** That's enough to separate 1.8.4 from main and Chrome, but not to call a winner between Chrome and BrAIn main after the sites are open.

## Why the old benchmark said "81×"

[BENCHMARK.md](BENCHMARK.md) (2026-10-11) reported 14,102 MB for Chrome against 174 MB for BrAIn after 25 tabs. Both numbers were wrong, and both errors favored BrAIn:

1. **BrAIn was counted without its page processes.** That benchmark added up the `ps` process tree. macOS starts WebKit's helpers itself, so they are not in BrAIn's tree. The 174 MB was the app process alone. In this test the app process is about 6% of BrAIn's total.
2. **Chrome was counted with resident memory (RSS) added across ~60 processes.** Every process maps the same system libraries and shared memory, so adding their RSS counts the shared parts dozens of times. In this test Chrome's summed RSS was 6.2–7.4 GB, while its real footprint was 3.6 GB.

The README, the welcome film and the Personality page repeated those numbers until they were corrected in 1.8.6.

## Raw numbers per run

MB. "Order" is the position in the test sequence.

| Run | Order | Just launched | 12 sites loaded | Left alone | Clicked through | +1 min | 6 closed |
|---|--:|--:|--:|--:|--:|--:|--:|
| Chrome r1 | 1 | 376 | 3,696 | 3,721 | 4,051 | 3,862 | 3,256 |
| Chrome r2 | 6 | 399 | 4,308 | 3,673 | 3,976 | 3,810 | 3,224 |
| BrAIn 1.8.4 r1 | 3 | 276 | 4,470 | 4,241 | 4,517 | 4,561 | 3,417 |
| BrAIn 1.8.4 r2 | 4 | 276 | 4,905 | 4,026 | 4,133 | 4,147 | 3,522 |
| BrAIn main r1 | 2 | 278 | 3,998 | 3,603 | 3,681 | 3,721 | 2,831 |
| BrAIn main r2 | 5 | 275 | 4,586 | 3,893 | 4,031 | 4,041 | 3,266 |

Process counts at the steady point: Chrome 66–68, BrAIn 19 (both builds).
