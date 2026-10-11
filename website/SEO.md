# SEO report — brain.gtgroup.dev

Source: Seobility SEO Checker (`seobility.net/en/seocheck/`), run 2026-10-11
Result URL analyzed: `https://brain.gtgroup.dev/`

## Score

**On-page score: 85%**

| Category | Score | Notes |
|---|---|---|
| Meta data | 98% | title + description lengths perfect, canonical valid, UTF-8, HTML5 doctype, favicon ok |
| Page structure | 100% | single perfect H1, clean heading hierarchy |
| Page quality | 77% | see warnings below |
| Link structure | 96% | ok except duplicate anchor texts |
| Server config | 100% | no redirect chain, compression on, 0.03 s TTFB, 37 kB HTML |
| External factors | 3% | expected — new domain, no backlinks |

HTTP facts: `200`, index/follow, `en`, 37.40 kB, 1,720 words, 34 text blocks.

## Issues found by Seobility (1 warning + minor flags)

1. **Image alt attributes — 3 images have no alt.** *(the listed "To-Do" item)*
   Our `<img>` tags use `alt=""` (decorative loader/nav/footer icons). Seobility still
   counts them. Fix: give the logo images a real alt (`alt="BrAIn logo"`) — it's a logo,
   not decoration, so this is both an SEO and a11y win.

2. **External factors 3% — only 1 referring domain / 1 backlink.** *Very important, not fixable in code.*
   The domain is brand new. This score only improves by earning links: post to
   Hacker News / Product Hunt / Reddit, add the site to the GitHub repo's website
   field, link it from `README.md` and release notes, submit to browser/agentic-tool
   directories and newsletters.

3. **Hosted on a subdomain** (`brain.gtgroup.dev`). *Low importance.*
   Seobility recommends a top-level domain. Cheap win if BrAIn gets its own domain
   (`brainbrowser.dev` etc.) — then 301 the subdomain.

4. **Duplicate anchor texts** — some internal links reuse the same link text.
   Descriptive anchors ("try the Rewind demo" vs generic "Learn more"/"Try it →")
   would fix it. Low priority, mostly cosmetic for a single-page site.

5. **40 strong/bold tags — recommends ≤34.** *Low importance.*
   We use `<b>`/`<strong>` liberally in feature copy; not worth restructuring.

6. **No social sharing buttons.** *Nice-to-have.*
   Deliberate — no third-party trackers on the page. OG/Twitter cards already
   handle sharing properly; skip.

7. No `hreflang` — single-language page, correct to omit.

## Already covered (Seobility green)

- Title 405/580 px, meta description 911/1000 px — optimal lengths
- Canonical URL, HTTPS everywhere, compression
- H1 + heading structure, no frames
- Viewport + apple-touch-icon (mobile)
- 1,720 words, no thin/placeholder/duplicate content, lists + 34 paragraphs
- Fast TTFB (0.03 s), small HTML (37 kB)
- Structured data: `SoftwareApplication` + `FAQPage` JSON-LD (eligible for rich results)
- `robots.txt` + `sitemap.xml` live at the canonical origin
- OG/Twitter cards with absolute image URLs (2400×1260)

## Next actions (priority order)

- [ ] Change the 3 logo `<img>` from `alt=""` to `alt="BrAIn logo"` — fixes the only real warning
- [ ] Point `brain.gtgroup.dev` from the GitHub repo website field, README, and PROMO.md links
- [ ] Launch posts (HN/Product Hunt/Reddit r/selfhosted + agent communities) for backlinks
- [ ] Optional: dedicated top-level domain later
- [ ] Optional: make a couple of repeated anchor texts descriptive
