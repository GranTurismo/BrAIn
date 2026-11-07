// BrAIn welcome film — shown once on first launch (and from ⌘K → "Welcome tour").
// Evaluated by the host into the toolbar view (macOS split) or the current document.
// Self-contained: one Shadow-DOM overlay, a pausable timeline engine, a cursor-reactive
// neural-field canvas and six scenes. Nothing here runs until the host evaluates it.
(() => {
    'use strict';
    const prev = window.__brainWelcome;
    if (prev && prev.isOpen && prev.isOpen()) return;

    const CFG = Object.assign({ exe: '', mac: /Mac/i.test(navigator.platform), test: false, lite: false }, window.__brainWelcomeCfg || {});
    const REDUCE = (() => { try { return matchMedia('(prefers-reduced-motion: reduce)').matches; } catch (e) { return false; } })();
    // LITE: the host paints this view on the CPU (Linux: the toolbar view is software-painted, see LinuxEngine.NewSettings), so
    // every frame re-rasters whatever changed. Anything that animates across the whole window (grain, the drifting glow, the
    // neural-field canvas) repaints ALL of it, and blurred shadows / filters are paid for per pixel: the full film ran at
    // ~10 fps with the web process pinned at 100% CPU on a 1080p screen. Lite keeps the story and the look (flat shadows, the
    // hairline rings and glows stay) and drops what only decorates; a governor (see `govern`) sheds the canvas too if even
    // that cannot hold ~24 fps (4K / HiDPI screens).
    const LITE = !!CFG.lite;
    const MOD = CFG.mac ? '\u2318' : 'Ctrl+';
    const send = cmd => {
        if (CFG.test) { (window.__welcomeLog = window.__welcomeLog || []).push(cmd); return; }
        try { window.external && window.external.sendMessage('brain:' + cmd); } catch (e) { }
    };

    // ---------------------------------------------------------------- easing
    const E_OUT = 'cubic-bezier(.16,1,.3,1)', E_IO = 'cubic-bezier(.65,0,.35,1)', E_BACK = 'cubic-bezier(.34,1.56,.64,1)', E_IN = 'cubic-bezier(.5,0,.75,0)';
    const easeIO = t => t < .5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
    const easeOut = t => 1 - Math.pow(1 - t, 3);
    const easeOutExpo = t => t >= 1 ? 1 : 1 - Math.pow(2, -10 * t);
    const lerp = (a, b, t) => a + (b - a) * t;
    const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
    const rnd = (a, b) => a + Math.random() * (b - a);

    // ---------------------------------------------------------------- icons
    const I = {
        arrow: '<svg viewBox="0 0 24 24"><path d="M5 2.6v16.8l4.3-4 2.9 6.4 2.8-1.2-2.9-6.3h5.9z" stroke-linejoin="round"/></svg>',
        ghost: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"><path d="M3.2 14V7.2a4.8 4.8 0 0 1 9.6 0V14l-1.6-1.3-1.6 1.3-1.6-1.3-1.6 1.3-1.6-1.3z"/><circle cx="6.1" cy="7.4" r=".9" fill="currentColor" stroke="none"/><circle cx="9.9" cy="7.4" r=".9" fill="currentColor" stroke="none"/></svg>',
        rw: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3.1 6.8A5.3 5.3 0 1 1 2.8 9"/><path d="M2.8 4.5v4h4"/><path d="M8.1 5.3V8l1.8 1.4"/></svg>',
        dl: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M8 2.5v8M4.6 7.4 8 10.8l3.4-3.4M3 13.5h10"/></svg>',
        dots: '<svg viewBox="0 0 16 16" fill="currentColor"><circle cx="8" cy="3.5" r="1.3"/><circle cx="8" cy="8" r="1.3"/><circle cx="8" cy="12.5" r="1.3"/></svg>',
        lock: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5"><rect x="3.5" y="7" width="9" height="6.5" rx="1.6"/><path d="M5.5 7V5.3a2.5 2.5 0 0 1 5 0V7"/></svg>',
        shield: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"><path d="M8 1.8 13 3.6v4c0 3.1-2.1 5.4-5 6.6-2.9-1.2-5-3.5-5-6.6v-4z"/><path d="m5.7 8 1.6 1.6 3-3.1" stroke-linecap="round"/></svg>',
        spark: '<svg viewBox="0 0 16 16" fill="currentColor"><path d="M8 .8c.5 3.6 1.6 5.4 7.2 7.2-5.6 1.8-6.7 3.6-7.2 7.2C7.5 11.6 6.4 9.8.8 8 6.4 6.2 7.5 4.4 8 .8z"/></svg>',
        check: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m3.4 8.4 3 3 6.2-6.6"/></svg>',
        x: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="m4 4 8 8M12 4l-8 8"/></svg>',
        play: '<svg viewBox="0 0 16 16" fill="currentColor"><path d="M4.5 2.8v10.4L13 8z"/></svg>',
        pause: '<svg viewBox="0 0 16 16" fill="currentColor"><rect x="3.8" y="2.8" width="3" height="10.4" rx=".8"/><rect x="9.2" y="2.8" width="3" height="10.4" rx=".8"/></svg>',
        next: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="m6 3.5 4.5 4.5L6 12.5"/></svg>',
        right: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 8h10M9 4l4 4-4 4"/></svg>',
        key: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><circle cx="5.4" cy="10.2" r="3"/><path d="M7.7 7.9 13.2 2.4M10.4 5.2l2.2 2.2M12.2 3.4l2.2 2.2" stroke-linecap="round"/></svg>',
        search: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><circle cx="7" cy="7" r="4.4"/><path d="m10.4 10.4 3.2 3.2"/></svg>',
        code: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5.5 4.5 2 8l3.5 3.5M10.5 4.5 14 8l-3.5 3.5"/></svg>',
        plane: '<svg viewBox="0 0 16 16" fill="currentColor"><path d="M14.6 7.1c.6 0 .9.4.9.9s-.3.9-.9.9H10l-3.6 5.3H4.8L6.6 8.9H3.5l-1.2 1.6H1L1.9 8 1 5.5h1.3l1.2 1.6h3.1L4.8 1.8h1.6L10 7.1z"/></svg>',
        mask: '<svg viewBox="0 0 16 16" fill="currentColor"><path d="M1.5 6.4c0-1 .8-1.6 1.7-1.6h9.6c.9 0 1.7.6 1.7 1.6 0 2.7-1.5 4.6-3.4 4.6-1.4 0-2.1-1.3-3.1-1.3S6.5 11 5 11c-1.9 0-3.5-1.9-3.5-4.6zm3 1.1c.6.6 1.6.6 2.2 0M9.3 7.5c.6.6 1.6.6 2.2 0" fill-rule="evenodd"/></svg>',
    };

    // The BrAIn mark (same geometry as the app icon: a lobed brain contour around a neural pentagon).
    function markSvg(uid) {
        const R = 36, pts = [];
        for (let i = 0; i <= 360; i += 3) {
            const ph = i * Math.PI / 180, r = R * (1 + .115 * Math.sin(9 * ph + 1.1));
            pts.push((r * Math.cos(ph)).toFixed(2) + ' ' + (r * Math.sin(ph)).toFixed(2));
        }
        const ring = [], rr = 16.9;
        for (let i = 0; i < 5; i++) {
            const a = (-90 + i * 72) * Math.PI / 180;
            ring.push([+(rr * Math.cos(a)).toFixed(2), +(rr * Math.sin(a)).toFixed(2)]);
        }
        const edges = ring.map((p, i) => { const q = ring[(i + 1) % 5]; return `<line pathLength="1" x1="${p[0]}" y1="${p[1]}" x2="${q[0]}" y2="${q[1]}"/>`; }).join('');
        const spokes = ring.map(p => `<line pathLength="1" x1="0" y1="0" x2="${p[0]}" y2="${p[1]}"/>`).join('');
        const nodes = ring.map(p => `<circle class="nd" cx="${p[0]}" cy="${p[1]}" r="4.6"/>`).join('') + '<circle class="nd c" cx="0" cy="0" r="5.4"/>';
        return `<svg class="mark" viewBox="-50 -50 100 100" data-ring='${JSON.stringify(ring)}'>
            <defs>
              <linearGradient id="mg${uid}" x1="-40" y1="-40" x2="40" y2="40" gradientUnits="userSpaceOnUse">
                <stop offset="0" stop-color="#ffd7a3"/><stop offset=".5" stop-color="#ff8a4c"/><stop offset="1" stop-color="#ff5f9e"/></linearGradient>
              <radialGradient id="mh${uid}"><stop offset="0" stop-color="#ff8a4c" stop-opacity=".42"/><stop offset=".55" stop-color="#ff5f9e" stop-opacity=".1"/><stop offset="1" stop-color="#ff5f9e" stop-opacity="0"/></radialGradient>
            </defs>
            <circle class="halo" r="49" fill="url(#mh${uid})"/>
            <path class="contour" pathLength="1" d="M${pts.join(' L')} Z" stroke="url(#mg${uid})"/>
            <g class="edges" stroke="url(#mg${uid})">${edges}</g>
            <g class="spokes" stroke="url(#mg${uid})">${spokes}</g>
            <g class="nodes">${nodes}</g>
            <g class="pulses"></g>
          </svg>`;
    }

    // ---------------------------------------------------------------- styles
    const CSS = `
:host{all:initial}
*{box-sizing:border-box;margin:0;padding:0}
#wl{position:fixed;inset:0;overflow:hidden;background:#0b0908;color:var(--ink);
  font:15px/1.5 var(--sans);-webkit-font-smoothing:antialiased;user-select:none;-webkit-user-select:none;cursor:default;
  --sans:-apple-system,BlinkMacSystemFont,"SF Pro Display","Segoe UI Variable Display","Segoe UI",system-ui,sans-serif;
  --mono:ui-monospace,"SF Mono",Menlo,"Cascadia Code",Consolas,monospace;
  --ink:#f8f1eb;--mut:#b7a99f;--dim:#7d7069;--ac:#ff8a4c;--achi:#ffb588;--ac2:#ff5f9e;--gold:#ffc978;
  --glass:rgba(255,244,236,.045);--glass2:rgba(255,244,236,.085);--line:rgba(255,236,222,.1);
  --vio:#a78bfa;--viohi:#d4c6ff;--padL:24px}
#wl.mac{--padL:94px}
#wl button{font-family:var(--sans)}
.bg{position:absolute;inset:-25%;pointer-events:none;will-change:transform}
.bgA{background:radial-gradient(38% 34% at 22% 18%,rgba(255,118,56,.28),transparent 70%),radial-gradient(42% 40% at 84% 88%,rgba(255,72,140,.2),transparent 70%),radial-gradient(28% 26% at 66% 30%,rgba(255,196,120,.08),transparent 70%);
  animation:drift 26s ease-in-out infinite alternate;transition:opacity 1.2s}
.bgV{opacity:0;background:radial-gradient(40% 36% at 30% 22%,rgba(124,58,237,.34),transparent 70%),radial-gradient(40% 40% at 80% 80%,rgba(167,139,250,.2),transparent 70%);
  animation:drift 30s ease-in-out infinite alternate-reverse;transition:opacity 1.2s}
#wl.ghosty .bgV{opacity:1}#wl.ghosty .bgA{opacity:.2}
@keyframes drift{from{transform:translate3d(-3%,-2%,0) rotate(-2deg)}to{transform:translate3d(3%,2.5%,0) rotate(2deg)}}
canvas.field{position:absolute;inset:0;width:100%;height:100%;pointer-events:none}
.vign{position:absolute;inset:0;pointer-events:none;background:radial-gradient(130% 95% at 50% 45%,transparent 52%,rgba(0,0,0,.6))}
.grain{position:absolute;inset:-60px;pointer-events:none;opacity:.075;mix-blend-mode:overlay;
  background-image:url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='180' height='180'><filter id='n'><feTurbulence type='fractalNoise' baseFrequency='.85' numOctaves='2' stitchTiles='stitch'/></filter><rect width='100%' height='100%' filter='url(%23n)'/></svg>");
  animation:grain 1s steps(6) infinite}
@keyframes grain{0%{transform:translate(0,0)}20%{transform:translate(-22px,14px)}40%{transform:translate(18px,-26px)}60%{transform:translate(-10px,30px)}80%{transform:translate(28px,8px)}100%{transform:translate(0,0)}}
#wl.paused *,#wl.paused *::before,#wl.paused *::after{animation-play-state:paused!important}

/* header */
.top{position:absolute;top:0;left:0;right:0;height:64px;display:flex;align-items:center;justify-content:space-between;padding:0 20px 0 var(--padL);z-index:6}
.brand{display:flex;align-items:center;gap:9px;font:700 13px var(--sans);letter-spacing:.02em;color:var(--ink);opacity:0;transition:opacity .6s}
.brand.on{opacity:1}
.brand .mark{width:24px;height:24px}
.brand span{color:var(--dim);font-weight:600}
.skip{appearance:none;border:0;display:flex;align-items:center;gap:10px;height:34px;padding:0 7px 0 16px;border-radius:999px;cursor:pointer;
  background:var(--glass);box-shadow:inset 0 0 0 1px var(--line);color:var(--ink);font:600 13px var(--sans);transition:background .2s,transform .15s,box-shadow .2s}
.skip:hover{background:var(--glass2);box-shadow:inset 0 0 0 1px rgba(255,236,222,.18)}
.skip:active{transform:scale(.96)}
.skip kbd{font:600 10.5px var(--mono);padding:3px 8px;border-radius:999px;background:rgba(255,255,255,.08);color:var(--mut)}
.skip:focus-visible,.chap button:focus-visible,.cta:focus-visible,.cta2:focus-visible{outline:2px solid var(--achi);outline-offset:3px}

/* chapters */
.chap{position:absolute;left:50%;bottom:26px;transform:translateX(-50%);display:flex;align-items:center;gap:14px;z-index:6;transition:opacity .5s}
.chap.dim{opacity:.4}.chap.dim:hover{opacity:1}
.pp,.nx{appearance:none;border:0;width:34px;height:34px;border-radius:50%;display:grid;place-items:center;cursor:pointer;color:var(--ink);
  background:var(--glass);box-shadow:inset 0 0 0 1px var(--line);transition:background .2s,transform .15s,box-shadow .3s}
.pp:hover,.nx:hover{background:var(--glass2)}.pp:active,.nx:active{transform:scale(.92)}
.pp svg,.nx svg{width:13px;height:13px}
.nx.hot{box-shadow:inset 0 0 0 1px rgba(255,138,76,.6),0 0 22px rgba(255,138,76,.45);animation:hot 1.6s ease-in-out infinite}
@keyframes hot{50%{box-shadow:inset 0 0 0 1px rgba(255,138,76,.9),0 0 34px rgba(255,138,76,.7)}}
.segs{display:flex;gap:8px}
.seg{appearance:none;border:0;background:none;cursor:pointer;display:flex;flex-direction:column;gap:9px;width:clamp(64px,8.5vw,118px);padding:6px 0;color:var(--dim);font:600 11px var(--sans);letter-spacing:.02em;text-align:left;transition:color .3s}
.seg:hover{color:var(--mut)}
.seg.on{color:var(--ink)}
.seg i{display:block;height:3px;border-radius:3px;background:rgba(255,240,230,.12);overflow:hidden}
.seg b{display:block;height:100%;width:100%;transform-origin:0 50%;transform:scaleX(0);background:linear-gradient(90deg,var(--gold),var(--ac),var(--ac2));border-radius:3px}
.seg span{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}

/* scenes */
.scenes{position:absolute;top:64px;left:0;right:0;bottom:92px}
.scene{position:absolute;inset:0;display:grid;grid-template-columns:minmax(300px,.92fr) 1.08fr;align-items:center;gap:clamp(24px,4vw,72px);padding:0 clamp(28px,5.5vw,96px)}
.scene.center{grid-template-columns:1fr;justify-items:center;text-align:center;padding:0 24px}
@media (max-width:900px){.scene{grid-template-columns:1fr;grid-template-rows:auto 1fr;align-content:center;gap:12px}.copy{max-width:640px}}
.copy{position:relative;display:grid;max-width:540px;z-index:2}
.cp{grid-area:1/1;align-self:center}
.cp.later{visibility:hidden}
.eyebrow{display:inline-flex;align-items:center;gap:10px;font:700 11px/1 var(--sans);letter-spacing:.26em;text-transform:uppercase;color:var(--achi);margin-bottom:20px}
.eyebrow::before{content:"";width:7px;height:7px;border-radius:50%;background:var(--ac);box-shadow:0 0 14px var(--ac)}
.cp.v .eyebrow{color:var(--viohi)}.cp.v .eyebrow::before{background:var(--vio);box-shadow:0 0 14px var(--vio)}
.hl{font:780 clamp(32px,3.5vw,62px)/1.03 var(--sans);letter-spacing:-.04em;color:#fff;margin-bottom:20px}
.hl em{font-style:normal}
.w{display:inline-block;will-change:transform,opacity}
.w.g{background:linear-gradient(100deg,var(--gold),var(--ac) 50%,var(--ac2));-webkit-background-clip:text;background-clip:text;color:transparent;padding-bottom:.06em}
.cp.v .w.g{background:linear-gradient(100deg,#e9e1ff,var(--vio) 60%,#7c5cf0);-webkit-background-clip:text;background-clip:text}
.sub{font-size:clamp(14.5px,1.12vw,17px);line-height:1.62;color:var(--mut);max-width:31em}
.sub b{color:var(--ink);font-weight:600}
.chips{display:flex;flex-wrap:wrap;gap:8px;margin-top:22px}
.chip{display:inline-flex;align-items:center;gap:7px;font:600 12px var(--sans);color:var(--ink);padding:7px 12px 7px 10px;border-radius:999px;background:var(--glass);box-shadow:inset 0 0 0 1px var(--line)}
.chip svg{width:12px;height:12px;color:var(--achi)}
.try{margin-top:24px;display:flex;width:max-content;align-items:center;gap:10px;font:600 12.5px var(--sans);color:var(--achi);opacity:0}
.try i{width:8px;height:8px;border-radius:50%;background:var(--ac);animation:ping 1.6s ease-out infinite}
.cp.v .try{color:var(--viohi)}.cp.v .try i{background:var(--vio);animation-name:pingv}
@keyframes ping{0%{box-shadow:0 0 0 0 rgba(255,138,76,.65)}100%{box-shadow:0 0 0 12px rgba(255,138,76,0)}}
@keyframes pingv{0%{box-shadow:0 0 0 0 rgba(167,139,250,.65)}100%{box-shadow:0 0 0 12px rgba(167,139,250,0)}}
.foot{margin-top:18px;font-size:11.5px;color:var(--dim);line-height:1.5}
kbd.k{font:600 .86em var(--mono);padding:2px 7px;border-radius:6px;background:rgba(255,255,255,.08);box-shadow:inset 0 0 0 1px var(--line);color:var(--ink)}

/* stage + design canvas (760 x 500, scaled to fit) */
.stage{position:relative;height:100%;min-height:0;display:grid;place-items:center;perspective:1700px}
.tilt{position:relative;transform-style:preserve-3d;will-change:transform}
.cv{position:absolute;left:0;top:0;width:760px;height:500px;transform-origin:0 0}

/* demo window: a real-sized BrAIn window. The toolbar inside .wch is the real one (chrome.js kit). */
.win{position:absolute;left:20px;top:24px;width:900px;height:564px;border-radius:12px;background:#110f0d;overflow:hidden;
  box-shadow:0 60px 120px -34px rgba(0,0,0,.9),0 34px 100px -46px rgba(255,110,50,.42),inset 0 0 0 1px rgba(255,240,230,.1)}
.wpage{position:absolute;top:46px;left:0;right:0;bottom:0;overflow:hidden;background:#f6f1eb;color:#2b211c;font-family:var(--sans)}
.wpage.off{display:none}
.wpage iframe{position:absolute;inset:0;width:100%;height:100%;border:0;background:#0d0b0a}
.wch{position:absolute;inset:0;z-index:3;pointer-events:none}
.gv2{position:absolute;inset:0;z-index:2;pointer-events:none}
.press{animation:press .32s ease}
@keyframes press{40%{transform:scale(.9)}}

/* cursors */
.cur{position:absolute;left:0;top:0;z-index:30;pointer-events:none;opacity:0;will-change:transform}
.cur .ar{width:22px;height:22px;display:block}
.cur .ar svg{width:22px;height:22px;display:block;overflow:visible}
.cur.agent .ar svg path{fill:#ff8a4c;stroke:#fff;stroke-width:1.4}
.cur.agent .ar{filter:drop-shadow(0 0 7px rgba(255,138,76,.9)) drop-shadow(0 3px 5px rgba(0,0,0,.4))}
.cur.you .ar svg path{fill:#fff;stroke:#1b1512;stroke-width:1.4}
.cur.you .ar{filter:drop-shadow(0 3px 6px rgba(0,0,0,.45))}
.cur .lbl{position:absolute;left:18px;top:19px;padding:3px 8px;border-radius:7px;font:700 10px var(--sans);white-space:nowrap;letter-spacing:.02em}
.cur.agent .lbl{background:linear-gradient(120deg,#ff9a5c,#ff5f9e);color:#fff;box-shadow:0 4px 14px rgba(255,95,80,.45)}
.cur.you .lbl{background:#fff;color:#1b1512;box-shadow:0 4px 14px rgba(0,0,0,.35)}
.trail{position:absolute;left:0;top:0;width:6px;height:6px;margin:-1px 0 0 -1px;border-radius:50%;background:#ff8a4c;pointer-events:none;z-index:29;opacity:0}
.ripple{position:absolute;width:36px;height:36px;margin:-18px 0 0 -18px;border-radius:50%;pointer-events:none;z-index:28;border:2px solid var(--ac);opacity:0}
.ripple.y{border-color:#fff}

/* ---------- intro / finale ---------- */
.intro,.fin{display:flex;flex-direction:column;align-items:center;text-align:center}
.markwrap{width:clamp(150px,17vw,210px);height:clamp(150px,17vw,210px);position:relative;cursor:pointer}
.markwrap .mark{width:100%;height:100%;overflow:visible}
.mark .contour,.mark .edges line,.mark .spokes line{fill:none;stroke-linecap:round;stroke-linejoin:round}
.mark .contour{stroke-width:4.3}
.mark .edges line,.mark .spokes line{stroke-width:2.9}
.mark .nd{fill:#fff;filter:drop-shadow(0 0 3px rgba(255,160,110,.95))}
.mark .nd.c{fill:#fff6ef}
.mark .pulses circle{fill:#fff;filter:drop-shadow(0 0 2.5px #ffb588)}
.brand .mark .contour{stroke-width:6}.brand .mark .edges line,.brand .mark .spokes line{stroke-width:4.2}.brand .mark .halo{display:none}.brand .mark .nd{filter:none}
.word{display:flex;margin-top:22px;font:800 clamp(46px,6.6vw,104px)/1 var(--sans);letter-spacing:-.045em}
.word span{display:inline-block;background:linear-gradient(170deg,#fff 20%,#ffd2b3 60%,#ff9b62);-webkit-background-clip:text;background-clip:text;color:transparent;padding:0 .01em .08em}
.word span.ai{background:linear-gradient(140deg,var(--gold),var(--ac) 50%,var(--ac2));-webkit-background-clip:text;background-clip:text}
.tag{margin-top:14px;font:650 clamp(19px,2vw,28px)/1.25 var(--sans);letter-spacing:-.02em;color:var(--ink)}
.tag2{margin-top:12px;font-size:clamp(13.5px,1.05vw,15.5px);color:var(--mut)}
.hint{position:absolute;left:0;right:0;width:max-content;margin:0 auto;bottom:4px;font:600 12px var(--sans);color:var(--dim);display:flex;align-items:center;gap:8px;opacity:0;white-space:nowrap}
.hint i{width:7px;height:7px;border-radius:50%;background:var(--ac);animation:ping 1.6s ease-out infinite}

/* ---------- agent scene: flights site (a website, so it is ours to draw) ---------- */
.bubble{position:absolute;z-index:8;max-width:260px;padding:12px 15px 13px;border-radius:16px 16px 16px 5px;font:600 14px/1.38 var(--sans);color:#fff;
  background:linear-gradient(140deg,rgba(60,48,42,.92),rgba(32,26,23,.92));box-shadow:0 18px 40px -12px rgba(0,0,0,.7),inset 0 0 0 1px rgba(255,236,222,.14);backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px);opacity:0}
.bubble .who{display:flex;align-items:center;gap:6px;font:700 10px var(--sans);letter-spacing:.14em;text-transform:uppercase;color:var(--mut);margin-bottom:5px}
.bubble.ai{border-radius:16px 16px 5px 16px;background:linear-gradient(140deg,rgba(255,138,76,.95),rgba(255,95,158,.92));font-size:13px}
.bubble.ai .who{color:rgba(255,255,255,.8)}
.fl{position:absolute;inset:0;padding:22px 34px;background:linear-gradient(180deg,#fbf6f1,#f3ece5)}
.fl-top{display:flex;align-items:center;justify-content:space-between;margin-bottom:18px}
.fl-logo{display:flex;align-items:center;gap:8px;font:800 18px var(--sans);letter-spacing:-.02em;color:#1e2a5a}
.fl-logo i{width:28px;height:28px;border-radius:9px;display:grid;place-items:center;background:linear-gradient(135deg,#3557e8,#2bb3e8);color:#fff}.fl-logo i svg{width:15px;height:15px}
.fl-navs{font:600 12.5px var(--sans);color:#6d625b;display:flex;gap:20px}
.fl-search{display:flex;gap:10px;padding:12px;border-radius:16px;background:#fff;box-shadow:0 10px 26px -14px rgba(60,40,20,.35),0 0 0 1px rgba(40,25,10,.06)}
.fld{position:relative;flex:1;padding:8px 13px;border-radius:10px;background:#f6f2ee;transition:box-shadow .25s,background .25s}
.fld.focus{background:#fff;box-shadow:0 0 0 2px #3557e8}
.fld label{display:block;font:700 10px var(--sans);letter-spacing:.1em;text-transform:uppercase;color:#958980}
.fld .v{font:650 14px var(--sans);color:#231a15;height:20px;display:flex;align-items:center;white-space:nowrap}
.fld .ph{color:#b1a69e;font-weight:500}
.fld .caret{display:none;width:1.5px;height:15px;background:#3557e8;margin-left:1px;animation:blink .9s steps(1) infinite}
.fld.focus .caret{display:block}
@keyframes blink{50%{opacity:.35}}
.sugg{position:absolute;left:0;top:100%;margin-top:6px;width:240px;border-radius:12px;background:#fff;box-shadow:0 18px 36px -12px rgba(40,25,10,.4),0 0 0 1px rgba(40,25,10,.07);padding:5px;z-index:3;opacity:0;transform:translateY(-4px);pointer-events:none}
.sg{padding:8px 10px;border-radius:8px;font:500 13px var(--sans);color:#3d322b}
.sg b{color:#120c08}.sg.hot{background:#eef1fe}
.go{appearance:none;border:0;padding:0 22px;border-radius:10px;font:700 13.5px var(--sans);color:#fff;background:linear-gradient(135deg,#3557e8,#2b8fe8);box-shadow:0 6px 14px -6px rgba(53,87,232,.7)}
.fl-res{margin-top:18px;display:flex;flex-direction:column;gap:10px;position:relative}
.res{display:flex;align-items:center;gap:18px;padding:13px 18px;border-radius:14px;background:#fff;box-shadow:0 1px 0 rgba(40,25,10,.05),0 0 0 1px rgba(40,25,10,.06);opacity:0;position:relative}
.res .air{display:flex;align-items:center;gap:10px;width:150px;font:700 13.5px var(--sans);color:#231a15}
.res .air i{width:28px;height:28px;border-radius:8px;display:grid;place-items:center;font:800 10px var(--sans);color:#fff;background:var(--c)}
.res .leg{flex:1;font:500 13px var(--sans);color:#6d625b}
.res .pr{font:800 19px var(--sans);letter-spacing:-.02em;color:#231a15}
.res .sel{appearance:none;border:0;padding:8px 15px;border-radius:9px;font:700 12.5px var(--sans);color:#3557e8;background:#eef1fe}
.res .best{position:absolute;top:-9px;left:18px;padding:2px 9px;border-radius:999px;font:800 10px var(--sans);letter-spacing:.06em;text-transform:uppercase;color:#fff;background:linear-gradient(120deg,#ff8a4c,#ff5f9e)}
.res.pick{box-shadow:0 0 0 2px #3557e8,0 12px 26px -12px rgba(53,87,232,.5)}
.skel{height:54px;border-radius:14px;background:linear-gradient(90deg,#efe8e1 25%,#f8f3ee 50%,#efe8e1 75%) 0 0/200% 100%;animation:shim 1s linear infinite}
@keyframes shim{to{background-position:-200% 0}}
.paybar{position:absolute;left:34px;right:34px;bottom:18px;display:flex;align-items:center;justify-content:space-between;gap:12px;padding:12px 14px 12px 20px;border-radius:16px;background:#1e1a33;color:#fff;font:600 13px var(--sans);box-shadow:0 18px 30px -12px rgba(20,10,40,.6);transform:translateY(160%);z-index:3}
.paybar span{color:#c9c3e6}
.pay{appearance:none;border:0;padding:11px 20px;border-radius:11px;font:800 13.5px var(--sans);color:#14121f;background:linear-gradient(135deg,#ffd27a,#ff9a5c)}
.done{position:absolute;left:50%;top:46%;z-index:7;display:flex;align-items:center;gap:12px;padding:14px 22px 14px 14px;border-radius:18px;
  background:#fff;color:#1d1611;font:750 15px var(--sans);box-shadow:0 24px 50px -14px rgba(40,20,10,.5);opacity:0;transform:translate(-50%,-50%) scale(.9);white-space:nowrap}
.done i{width:34px;height:34px;border-radius:50%;display:grid;place-items:center;color:#fff;background:linear-gradient(135deg,#43d58e,#1fae7a)}
.done i svg{width:18px;height:18px}
.done small{display:block;font:600 11.5px var(--sans);color:#7c7069}

/* ---------- rewind + ghost: shop page ---------- */
.shop{position:absolute;inset:0;padding:22px 40px;background:linear-gradient(180deg,#fbf7f3,#f2ebe4)}
.sh-nav{display:flex;align-items:center;gap:22px;font:600 12.5px var(--sans);color:#7a6e66;margin-bottom:24px}
.sh-nav b{font:800 18px var(--sans);letter-spacing:-.02em;color:#1b1411;margin-right:auto}
.sh-main{display:flex;gap:40px;align-items:center}
.sh-img{--hue:330;position:relative;width:330px;height:330px;border-radius:26px;flex:none;overflow:hidden;
  background:radial-gradient(70% 70% at 30% 25%,hsl(calc(var(--hue) + 30) 90% 80%),hsl(var(--hue) 70% 58%) 60%,hsl(calc(var(--hue) - 30) 60% 38%))}
.hp{position:absolute;left:50%;top:52%;width:180px;height:180px;transform:translate(-50%,-50%)}
.hp .band{position:absolute;left:16px;right:16px;top:0;height:146px;border:17px solid rgba(20,12,16,.85);border-bottom:0;border-radius:84px 84px 0 0}
.hp .cup{position:absolute;bottom:4px;width:56px;height:84px;border-radius:24px;background:linear-gradient(160deg,#2a1e26,#120b10);box-shadow:inset 0 2px 0 rgba(255,255,255,.12),0 14px 24px -8px rgba(0,0,0,.5)}
.hp .cup.l{left:2px}.hp .cup.r{right:2px}
.hp .cup::after{content:"";position:absolute;inset:16px 14px;border-radius:14px;background:radial-gradient(circle at 40% 35%,hsl(var(--hue) 90% 70%),transparent 70%);opacity:.6}
.sh-info{flex:1;min-width:0}
.sh-kick{font:700 11px var(--sans);letter-spacing:.14em;text-transform:uppercase;color:#a0938a}
.sh-title{margin-top:8px;font:800 30px/1.1 var(--sans);letter-spacing:-.03em;color:#1b1411}
.sh-stars{margin-top:10px;font:700 13.5px var(--sans);color:#f2a63a}.sh-stars span{color:#8a7d75;font-weight:600;margin-left:6px}
.sh-price{position:relative;margin-top:20px;display:flex;align-items:baseline;gap:12px}
.sh-price .now{font:850 42px/1 var(--sans);letter-spacing:-.04em;color:#1b1411}
.sh-price .was{font:600 16px var(--sans);color:#a0938a;text-decoration:line-through}
.sh-badge{display:inline-block;margin-top:14px;padding:5px 12px;border-radius:999px;font:750 12.5px var(--sans)}
.b-red{background:#ffe1df;color:#c2352b}.b-amber{background:#fff0d4;color:#a5650c}.b-green{background:#ddf6e7;color:#1a8a50}.b-blue{background:#e2ebff;color:#2f5bd2}
.sh-buy{appearance:none;border:0;display:block;margin-top:20px;width:250px;padding:14px;border-radius:13px;font:750 14.5px var(--sans);color:#fff;background:#1b1411}
.sh-buy.dis{opacity:.35}
.sh-ship{margin-top:12px;font:500 12.5px var(--sans);color:#8a7d75}
.callout{position:absolute;z-index:9;padding:9px 13px;border-radius:12px;font:700 13px/1.35 var(--sans);color:#fff;background:linear-gradient(130deg,#ff8a4c,#ff5f9e);box-shadow:0 14px 30px -10px rgba(255,90,80,.6);opacity:0;white-space:nowrap}
.callout small{display:block;font:600 11px var(--sans);color:rgba(255,255,255,.82)}
.callout::before{content:"";position:absolute;left:-5px;top:50%;width:10px;height:10px;margin-top:-5px;background:#ff8a4c;transform:rotate(45deg);border-radius:2px}
.bit{position:absolute;width:4px;height:4px;border-radius:1px;pointer-events:none;z-index:8}
.gsw{display:inline-flex;align-items:center;gap:12px;margin-top:24px;padding:8px 14px 8px 8px;border-radius:999px;cursor:pointer;background:var(--glass);box-shadow:inset 0 0 0 1px var(--line);font:650 13px var(--sans);color:var(--ink);transition:background .3s,box-shadow .3s;appearance:none;border:0}
.gsw:hover{background:var(--glass2)}
.gsw .knob{position:relative;width:46px;height:28px;border-radius:999px;background:rgba(255,255,255,.12);transition:background .35s}
.gsw .knob::after{content:"";position:absolute;top:3px;left:3px;width:22px;height:22px;border-radius:50%;background:#fff;box-shadow:0 2px 6px rgba(0,0,0,.4);transition:transform .35s cubic-bezier(.34,1.56,.64,1)}
.gsw.on{box-shadow:inset 0 0 0 1px rgba(167,139,250,.5),0 0 28px rgba(124,58,237,.35)}
.gsw.on .knob{background:linear-gradient(135deg,#a78bfa,#7c3aed)}
.gsw.on .knob::after{transform:translateX(18px)}
.gsw svg{width:16px;height:16px;color:var(--viohi)}

/* ---------- flows: the real about:flows page runs in the window; this card is the film's narration ---------- */
.call{position:absolute;right:-26px;bottom:-14px;width:360px;z-index:9;padding:14px 16px;border-radius:16px;font:500 12.5px/1.5 var(--mono);color:#e5d9d0;
  background:rgba(14,11,10,.94);box-shadow:0 30px 60px -20px rgba(0,0,0,.85),inset 0 0 0 1px rgba(255,236,222,.12);opacity:0}
.call .who{font:700 10px var(--sans);letter-spacing:.14em;text-transform:uppercase;color:var(--ac2);margin-bottom:6px}
.call .fn{color:#ffd2b5}.call .s{color:#ffcf8a}
.call .res2{margin-top:9px;font:650 12px var(--sans);color:#8ff0bb;opacity:0;display:flex;align-items:center;gap:6px}
.call .res2 svg{width:14px;height:14px}

/* ---------- site scripts: news page (a website) ---------- */
.news{position:absolute;inset:0;padding:18px 34px;background:#fbf8f4;color:#1d1714;font-family:Georgia,"Times New Roman",serif}
.nw-top{display:flex;align-items:center;justify-content:space-between;padding-bottom:12px;border-bottom:2px solid #1d1714;font:900 24px Georgia,serif;letter-spacing:-.02em}
.nw-top span{font:600 11px var(--sans);letter-spacing:.12em;text-transform:uppercase;color:#7a6e66}
.nw-body{display:flex;gap:30px;margin-top:20px}
.nw-art{flex:1}
.nw-kick{font:800 11px var(--sans);letter-spacing:.14em;text-transform:uppercase;color:#c2412b}
.nw-h{margin-top:8px;font:700 31px/1.12 Georgia,serif;letter-spacing:-.02em}
.nw-l{height:9px;border-radius:5px;background:#e6ddd4;margin-top:12px}
.nw-side{width:240px;flex:none}
.nw-card{height:118px;border-radius:12px;background:linear-gradient(135deg,#e9dfd5,#d9ccbf);margin-bottom:12px}
.clut{position:absolute;z-index:4;cursor:pointer;font-family:var(--sans);transition:filter .2s}
.clut:hover{filter:brightness(1.06)}
.c-ad{left:0;right:0;top:0;height:46px;display:flex;align-items:center;justify-content:center;gap:12px;background:repeating-linear-gradient(135deg,#ffe35a 0 14px,#ffd42a 14px 28px);font:900 15px var(--sans);color:#3b2a00;letter-spacing:.02em}
.c-ad small{padding:2px 7px;border-radius:4px;background:#3b2a00;color:#ffe35a;font-size:10px}
.c-veil{position:absolute;inset:0;z-index:4;background:rgba(20,14,10,.42);pointer-events:none;opacity:0}
.c-modal{left:50%;top:50%;width:340px;margin:-110px 0 0 -170px;padding:24px;border-radius:18px;background:#fff;box-shadow:0 30px 60px -20px rgba(0,0,0,.5);text-align:center;z-index:5}
.c-modal h6{font:800 21px var(--sans);letter-spacing:-.02em;color:#1d1714}
.c-modal p{margin:7px 0 14px;font:500 13px var(--sans);color:#7a6e66}
.c-modal .in{height:36px;border-radius:9px;background:#f2ede8;margin-bottom:9px}
.c-modal .b{height:38px;border-radius:10px;background:#c2412b;color:#fff;font:800 13.5px/38px var(--sans)}
.c-modal .x{position:absolute;right:12px;top:9px;font:600 16px var(--sans);color:#b7aaa0}
.c-ck{left:16px;width:500px;bottom:16px;display:flex;align-items:center;gap:16px;padding:15px 18px;border-radius:16px;background:#1d1714;color:#f2ebe5;font:500 12.5px/1.4 var(--sans);box-shadow:0 20px 40px -12px rgba(0,0,0,.6);z-index:5}
.c-ck b{display:block;font:800 13.5px var(--sans);color:#fff}
.c-ck .bs{display:flex;gap:7px;margin-left:auto;flex:none}
.c-ck .bs i{padding:9px 13px;border-radius:9px;font:700 12px var(--sans);font-style:normal;background:#3a302b}.c-ck .bs i:last-child{background:#ffb02e;color:#1d1714}
.c-vid{right:34px;top:142px;width:240px;height:146px;border-radius:12px;background:linear-gradient(135deg,#2d2a4a,#141225);display:grid;place-items:center;color:#fff;font:800 11px var(--sans);letter-spacing:.1em;box-shadow:0 10px 24px -10px rgba(0,0,0,.6)}
.c-vid i{position:absolute;left:9px;top:9px;padding:2px 7px;border-radius:4px;background:#ff3b3b;font-style:normal}
.c-vid svg{width:36px;height:36px;opacity:.9}

/* ---------- performance ---------- */
.bench{position:absolute;inset:0}
.big{position:absolute;right:-4px;top:-40px;font:900 150px/1 var(--sans);letter-spacing:-.07em;color:transparent;-webkit-text-stroke:1.5px rgba(255,138,76,.32);opacity:0;pointer-events:none}
.modes{position:absolute;left:0;top:10px;display:flex;gap:4px;padding:4px;border-radius:12px;background:var(--glass);box-shadow:inset 0 0 0 1px var(--line);z-index:2}
.modes button{appearance:none;border:0;padding:7px 14px;border-radius:9px;font:700 11.5px var(--sans);color:var(--mut);background:none;cursor:pointer;transition:background .25s,color .25s}
.modes button.on{background:rgba(255,138,76,.2);color:#fff;box-shadow:inset 0 0 0 1px rgba(255,138,76,.4)}
.brow{position:absolute;left:0;right:0}
.brow.chrome{top:126px}.brow.brain{top:202px}
.blab{display:flex;align-items:baseline;justify-content:space-between;margin-bottom:10px}
.blab b{font:750 15px var(--sans);color:var(--ink);display:flex;align-items:center;gap:9px}
.blab b i{width:10px;height:10px;border-radius:50%;background:#8b8f98}
.brow.brain .blab b i{background:var(--ac);box-shadow:0 0 12px var(--ac)}
.blab .val{font:800 30px/1 var(--sans);letter-spacing:-.04em;color:#fff;font-variant-numeric:tabular-nums}
.blab .val small{font:650 13px var(--sans);color:var(--mut);letter-spacing:0;margin-left:4px}
.bar{position:relative;height:34px;border-radius:10px;background:rgba(255,240,230,.05);box-shadow:inset 0 0 0 1px rgba(255,240,230,.06);overflow:hidden}
.bar i{position:absolute;left:0;top:0;bottom:0;width:100%;transform-origin:0 50%;transform:scaleX(0);border-radius:10px}
.brow.chrome .bar i{background:repeating-linear-gradient(135deg,rgba(255,255,255,.06) 0 10px,transparent 10px 20px),linear-gradient(90deg,#5c5f68,#8b8f98 60%,#c45a4a);animation:stripes 1.2s linear infinite}
.brow.brain .bar i{background:linear-gradient(90deg,var(--gold),var(--ac));box-shadow:0 0 24px rgba(255,138,76,.9)}
@keyframes stripes{to{background-position:28px 0,0 0}}
.pertab{position:absolute;top:284px;left:0;right:0;display:flex;gap:26px;font:600 12px var(--sans);color:var(--mut);opacity:0}
.pertab b{color:var(--ink);font-weight:750}
.minis{position:absolute;left:0;right:0;top:330px;display:grid;grid-template-columns:repeat(2,1fr);gap:14px}
.mini{position:relative;height:186px;border-radius:16px;padding:14px 16px;background:linear-gradient(180deg,rgba(40,33,30,.9),rgba(24,20,18,.92));box-shadow:inset 0 0 0 1px rgba(255,236,222,.09);overflow:hidden;opacity:0}
.mini h6{font:700 12.5px var(--sans);color:var(--ink)}
.mini p{font:500 11.5px var(--sans);color:var(--dim);margin-top:2px}
/* the real palette / password card (chrome.js kit), at a reduced scale */
.mstage{position:absolute;left:14px;right:14px;top:58px;bottom:14px;border-radius:12px;overflow:hidden}
.mk{position:absolute;left:0;top:0;transform-origin:0 0;pointer-events:none}

/* ---------- finale ---------- */
.fin .markwrap{width:clamp(110px,11vw,150px);height:clamp(110px,11vw,150px);margin-bottom:26px}
.fin .hl{margin-bottom:14px}
.fin .sub{margin:0 auto}
.ctas{display:flex;gap:12px;justify-content:center;margin-top:30px}
.cta{appearance:none;border:0;display:inline-flex;align-items:center;gap:10px;padding:15px 24px 15px 28px;border-radius:999px;cursor:pointer;font:750 15px var(--sans);color:#1c120c;
  background:linear-gradient(120deg,var(--gold),var(--ac) 55%,var(--ac2));box-shadow:0 18px 50px -12px rgba(255,110,70,.75),inset 0 1px 0 rgba(255,255,255,.5);transition:transform .18s cubic-bezier(.34,1.56,.64,1),filter .2s}
.cta:hover{transform:translateY(-2px) scale(1.03);filter:brightness(1.06)}.cta:active{transform:scale(.97)}
.cta svg{width:16px;height:16px;transition:transform .25s}.cta:hover svg{transform:translateX(3px)}
.cta2{appearance:none;border:0;padding:15px 22px;border-radius:999px;cursor:pointer;font:700 14px var(--sans);color:var(--ink);background:var(--glass);box-shadow:inset 0 0 0 1px var(--line);transition:background .2s}
.cta2:hover{background:var(--glass2)}
.mcp{width:min(560px,90vw);margin:22px auto 0;padding:16px;border-radius:16px;text-align:left;background:rgba(20,16,14,.85);box-shadow:inset 0 0 0 1px var(--line);display:none}
.mcp.on{display:block}
.mcp p{font:500 12.5px var(--sans);color:var(--mut);margin-bottom:10px}
.mcp pre{position:relative;padding:12px 14px;border-radius:10px;background:rgba(0,0,0,.45);font:500 11.5px/1.6 var(--mono);color:#ffd2b5;white-space:pre;overflow:auto;user-select:text;-webkit-user-select:text}
.mcp .cp2{position:absolute;right:8px;top:8px;appearance:none;border:0;padding:5px 10px;border-radius:7px;font:700 10.5px var(--sans);color:var(--ink);background:rgba(255,255,255,.1);cursor:pointer}
@media (prefers-reduced-motion:reduce){#wl *,#wl *::before,#wl *::after{animation-duration:.01ms!important;animation-iteration-count:1!important}}
`;

    // Lite styling: drop the big soft box-shadow layers (blur > 16px; hairline rings and small glows stay) and every
    // backdrop blur / glow filter, and let no CSS animation loop. Applied to the film's own stylesheet and to the real
    // toolbar's (the demo windows).
    function liteCss(css) {
        const blurred = layer => {
            // lengths only: take the colour functions (rgba(...), var(...)) and `inset` out, hex colours start with '#'
            const nums = layer.replace(/\([^)]*\)/g, '').replace(/\binset\b/, '').trim().split(/\s+/).filter(x => /^-?[\d.]/.test(x));
            return nums.length >= 3 && parseFloat(nums[2]) > 16; // small glows on small dots are cheap; the big soft shadows are not
        };
        const shadows = css.replace(/box-shadow:([^;}]+)/g, (m, v) => {
            const parts = []; let depth = 0, cur = '';
            for (const ch of v) {
                if (ch === '(') depth++; else if (ch === ')') depth--;
                if (ch === ',' && !depth) { parts.push(cur); cur = ''; } else cur += ch;
            }
            parts.push(cur);
            const keep = parts.filter(p => !blurred(p));
            return 'box-shadow:' + (keep.length ? keep.join(',') : 'none');
        });
        // backdrop blur and glow / blur filters (colour filters such as invert() or brightness() are left alone)
        return shadows.replace(/(?:-webkit-)?backdrop-filter:[^;}]+/g, 'backdrop-filter:none')
            .replace(/filter:[^;}]*(?:drop-shadow|blur)\([^;}]*/g, 'filter:none');
    }
    // No CSS animation loops forever. On a CPU-painted view every running animation of a property that cannot be composited
    // (box-shadow, background-position, ...) makes WebKit run a whole rendering update each frame, whatever its size: one 8px
    // pulsing dot took the agent scene from 55 to 12 fps on a HiDPI screen. (Scripted animations are not affected.)
    const NO_LOOPS = '*,*::before,*::after{animation-iteration-count:1!important}';
    // the film's own extras, on top of liteCss
    const LITE_RULES = `
#wl.lite *,#wl.lite *::before,#wl.lite *::after{animation-iteration-count:1!important}
#wl.lite,#wl.lite *{filter:none!important;backdrop-filter:none!important;-webkit-backdrop-filter:none!important}
#wl.lite .grain{display:none}
#wl.lite .bg{animation:none}
#wl.lite .stage{perspective:none}
#wl.lite canvas.field,#wl.lite .vign{transition:opacity .9s}
#wl.flat canvas.field,#wl.flat .vign{opacity:0}`;

    // ---------------------------------------------------------------- DOM shell
    const host = document.createElement('div');
    host.id = '__brain-welcome';
    host.style.cssText = 'all:initial;position:fixed;inset:0;z-index:2147483647;display:block;';
    host.setAttribute('role', 'dialog');
    host.setAttribute('aria-label', 'Welcome to BrAIn');
    const shadow = host.attachShadow({ mode: 'open' });
    shadow.innerHTML = `<style>${LITE ? liteCss(CSS) + LITE_RULES : CSS}</style>
      <div id="wl" class="${CFG.mac ? 'mac' : ''}${LITE ? ' lite' : ''}" tabindex="-1">
        <div class="bg bgA"></div><div class="bg bgV"></div>
        <canvas class="field"></canvas>
        <div class="vign"></div>
        <div class="scenes"></div>
        <div class="grain"></div>
        <div class="top">
          <div class="brand">${markSvg('b')}BrAIn <span>Welcome</span></div>
          <button class="skip" type="button">Skip <kbd>esc</kbd></button>
        </div>
        <nav class="chap" aria-label="Chapters">
          <button class="pp" type="button" aria-label="Pause">${I.pause}</button>
          <div class="segs"></div>
          <button class="nx" type="button" aria-label="Next">${I.next}</button>
        </nav>
      </div>`;
    const $ = sel => shadow.querySelector(sel);
    const wl = $('#wl'), scenesEl = $('.scenes'), segsEl = $('.segs'), ppBtn = $('.pp'), nxBtn = $('.nx'), brandEl = $('.brand');

    // ---------------------------------------------------------------- clock, tweens, waits
    let clock = 0, playing = true, rafId = 0, lastNow = 0, isOpen = false, closing = false;
    const waits = new Set(), tweens = new Set(), frameFns = new Set();
    function loop(now) {
        rafId = requestAnimationFrame(loop);
        const dt = lastNow ? Math.min(50, now - lastNow) : 16;
        lastNow = now;
        if (LITE) govern(now);
        tick(dt, now);
    }
    // Frame governor (lite only). Measures the real frame rate over 2 s windows; two slow windows in a row while the film
    // is playing (a paused film or a hidden window gets no frames, that is not slowness) and the canvas and the vignette are
    // shed (they fade out) — the last things that repaint the whole window.
    let govT0 = 0, govN = 0, govSlow = 0, shed = false;
    function govern(now) {
        if (shed) return;
        if (!govT0) { govT0 = now; govN = 0; return; }
        govN++;
        if (now - govT0 < 2000) return;
        const fps = govN * 1000 / (now - govT0);
        govT0 = now; govN = 0;
        if (!playing || document.hidden) { govSlow = 0; return; }
        govSlow = fps < 30 ? govSlow + 1 : 0;
        if (govSlow >= 2) { shed = true; wl.classList.add('flat'); field.off(); }
    }
    function tick(dt, now) {
        if (playing) clock += dt;
        for (const w of waits) if (clock >= w.at) { waits.delete(w); w.res(); }
        if (playing) for (const t of tweens) {
            const p = Math.min(1, (clock - t.t0) / t.dur);
            try { t.fn(t.ease(p), p); } catch (e) { }
            if (p >= 1) { tweens.delete(t); t.res(); }
        }
        for (const f of frameFns) { try { f(dt, now); } catch (e) { } }
    }

    class Abort extends Error { }
    function makeCtx() {
        const c = { alive: true, anims: new Set(), waits: new Set(), tweens: new Set(), offs: [], touched: -1e9, engaged: false };
        c.wait = ms => new Promise((res, rej) => {
            if (!c.alive) return rej(new Abort());
            const w = { at: clock + ms, res, rej }; waits.add(w); c.waits.add(w);
        });
        c.tween = (dur, fn, ease = easeIO) => new Promise((res, rej) => {
            if (!c.alive) return rej(new Abort());
            const t = { t0: clock, dur: Math.max(1, dur), fn, ease, res, rej };
            tweens.add(t); c.tweens.add(t); fn(ease(0), 0);
        });
        c.anim = (el, kf, o) => {
            const a = el.animate(kf, Object.assign({ fill: 'both', easing: E_OUT }, o));
            c.anims.add(a); if (!playing) a.pause();
            return a;
        };
        c.fork = p => { p.catch(() => { }); return p; };
        c.on = (el, ev, fn, opt) => { el.addEventListener(ev, fn, opt); c.offs.push(() => el.removeEventListener(ev, fn, opt)); };
        c.touch = () => { c.touched = clock; c.engaged = true; };
        c.idle = () => clock - c.touched;
        c.kill = freeze => {
            c.alive = false;
            c.waits.forEach(w => { waits.delete(w); w.rej(new Abort()); });
            c.tweens.forEach(t => { tweens.delete(t); t.rej(new Abort()); });
            c.anims.forEach(a => { try { freeze ? a.pause() : a.cancel(); } catch (e) { } });
            c.offs.forEach(f => f());
        };
        return c;
    }

    // ---------------------------------------------------------------- helpers
    function splitWords(el) {
        const walk = (n, inEm) => {
            for (const ch of Array.from(n.childNodes)) {
                if (ch.nodeType === 3) {
                    const frag = document.createDocumentFragment();
                    for (const p of ch.textContent.split(/(\s+)/)) {
                        if (!p) continue;
                        if (/^\s+$/.test(p)) { frag.appendChild(document.createTextNode(p)); continue; }
                        const s = document.createElement('span');
                        s.className = 'w' + (inEm ? ' g' : '');
                        s.textContent = p;
                        frag.appendChild(s);
                    }
                    ch.replaceWith(frag);
                } else if (ch.nodeType === 1 && ch.tagName !== 'BR') walk(ch, inEm || ch.tagName === 'EM');
            }
        };
        walk(el, false);
        return Array.from(el.querySelectorAll('.w'));
    }
    function reveal(c, el, delay = 0, step = 55) {
        if (!el) return;
        const words = el._words || (el._words = splitWords(el));
        words.forEach((w, i) => c.anim(w, REDUCE
            ? [{ opacity: 0 }, { opacity: 1 }]
            : [{ opacity: 0, transform: 'translateY(.5em) rotate(1.5deg)', filter: 'blur(10px)' }, { opacity: 1, transform: 'none', filter: 'blur(0)' }],
            { duration: 950, delay: delay + i * step }));
    }
    function fadeUp(c, el, delay = 0, dist = 14, dur = 800) {
        if (!el) return null;
        return c.anim(el, [{ opacity: 0, transform: `translateY(${dist}px)` }, { opacity: 1, transform: 'none' }], { duration: dur, delay });
    }
    function popIn(c, el, delay = 0, dur = 600) {
        if (!el) return null;
        return c.anim(el, [{ opacity: 0, transform: 'scale(.85)' }, { opacity: 1, transform: 'none' }], { duration: dur, delay, easing: E_BACK });
    }
    function showCopy(c, cp, delay = 0) {
        cp.classList.remove('later');
        fadeUp(c, cp.querySelector('.eyebrow'), delay, 10, 700);
        reveal(c, cp.querySelector('.hl'), delay + 80);
        fadeUp(c, cp.querySelector('.sub'), delay + 420, 12, 900);
        cp.querySelectorAll('.chip,.gsw,.foot').forEach((ch, i) => fadeUp(c, ch, delay + 620 + i * 90, 10, 700));
    }
    async function swapCopy(c, from, to) {
        const a = c.anim(from, [{ opacity: 1, transform: 'none', filter: 'blur(0)' }, { opacity: 0, transform: 'translateY(-16px)', filter: 'blur(8px)' }], { duration: 420, easing: E_IN });
        await c.wait(380);
        from.style.visibility = 'hidden';
        showCopy(c, to, 0);
        return a;
    }
    function showTry(c, cp, delay = 0) { const t = cp && cp.querySelector('.try'); if (t) fadeUp(c, t, delay, 8, 600); }
    // Offset of el inside ancestor in unscaled design pixels (layout offsets ignore transforms).
    function posIn(el, anc) {
        // inside a demo window's page iframe: the iframe's offset plus the element's place in that page
        const fr = el.ownerDocument !== document && el.ownerDocument.defaultView && el.ownerDocument.defaultView.frameElement;
        if (fr) { const p = posIn(fr, anc), r = el.getBoundingClientRect(); return { x: p.x + r.left, y: p.y + r.top, w: r.width, h: r.height }; }
        let x = 0, y = 0, n = el;
        while (n && n !== anc) { x += n.offsetLeft; y += n.offsetTop; n = n.offsetParent; }
        return { x, y, w: el.offsetWidth, h: el.offsetHeight };
    }
    function center(el, anc, dx = .5, dy = .5) { const p = posIn(el, anc); return { x: p.x + p.w * dx, y: p.y + p.h * dy }; }

    // Cursors: an AI agent (ember, with a light trail) and "you".
    function makeCursor(cv, kind, label) {
        const cur = document.createElement('div');
        cur.className = 'cur ' + kind;
        cur.innerHTML = `<i class="ar">${I.arrow}</i><span class="lbl">${label}</span>`;
        cv.appendChild(cur);
        cur._x = 0; cur._y = 0; cur._trail = [];
        if (kind === 'agent' && !REDUCE) for (let i = 0; i < 5; i++) {
            const t = document.createElement('i'); t.className = 'trail'; cv.appendChild(t); cur._trail.push({ el: t, x: 0, y: 0 });
        }
        return cur;
    }
    function placeCursor(cur, x, y) {
        cur._x = x; cur._y = y;
        cur.style.transform = `translate(${x.toFixed(1)}px,${y.toFixed(1)}px)`;
    }
    async function moveTo(c, cur, x, y, dur) {
        const x0 = cur._x, y0 = cur._y, dx = x - x0, dy = y - y0, d = Math.hypot(dx, dy) || 1;
        dur = dur || clamp(380 + d * 1.1, 420, 1150);
        const bend = (cur._bend = -(cur._bend || 1)) * Math.min(60, d * .18);
        const cx = x0 + dx / 2 - dy / d * bend, cy = y0 + dy / 2 + dx / d * bend;
        const hist = [];
        await c.tween(dur, p => {
            const u = 1 - p, px = u * u * x0 + 2 * u * p * cx + p * p * x, py = u * u * y0 + 2 * u * p * cy + p * p * y;
            placeCursor(cur, px, py);
            if (cur._trail.length) {
                hist.unshift([px, py]); if (hist.length > 26) hist.pop();
                cur._trail.forEach((t, i) => {
                    const h = hist[Math.min(hist.length - 1, (i + 1) * 4)];
                    t.el.style.transform = `translate(${h[0].toFixed(1)}px,${h[1].toFixed(1)}px) scale(${1 - i * .15})`;
                    t.el.style.opacity = p < 1 ? (.5 - i * .09).toFixed(2) : '0';
                });
            }
        }, easeIO);
        cur._trail.forEach(t => { t.el.style.opacity = '0'; });
    }
    async function moveToEl(c, cur, el, cv, dx = .5, dy = .55, dur) {
        const p = center(el, cv, dx, dy);
        await moveTo(c, cur, p.x - 3, p.y - 2, dur);
    }
    async function click(c, cur, cv, target) {
        const ar = cur.querySelector('.ar');
        c.anim(ar, [{ transform: 'scale(1)' }, { transform: 'scale(.78)' }, { transform: 'scale(1)' }], { duration: 260, easing: E_IO, fill: 'none' });
        const r = document.createElement('i');
        r.className = 'ripple' + (cur.classList.contains('you') ? ' y' : '');
        r.style.left = (cur._x + 3) + 'px'; r.style.top = (cur._y + 2) + 'px';
        cv.appendChild(r);
        c.anim(r, [{ opacity: .9, transform: 'scale(.3)' }, { opacity: 0, transform: 'scale(1.6)' }], { duration: 560, easing: 'ease-out' });
        if (target) { target.classList.remove('press'); void target.offsetWidth; target.classList.add('press'); }
        await c.wait(140);
    }
    async function typeInto(c, el, text, cps = 15) {
        for (const ch of text) { el.textContent += ch; await c.wait(1000 / cps * rnd(.65, 1.35)); }
    }
    // A burst of small squares where an element was ("dissolve" / "blast").
    function shatter(c, el, cont, colors, opts = {}) {
        const p = posIn(el, cont), n = opts.n || 18;
        if (opts.centered) { p.x -= p.w / 2; p.y -= p.h / 2; } // element positioned with translate(-50%,-50%)
        for (let i = 0; i < n; i++) {
            const b = document.createElement('i');
            b.className = 'bit';
            const x = p.x + rnd(0, p.w), y = p.y + rnd(0, p.h), s = rnd(3, 7);
            b.style.cssText = `left:${x}px;top:${y}px;width:${s}px;height:${s}px;background:${colors[i % colors.length]}`;
            cont.appendChild(b);
            const ang = opts.up ? rnd(-2.4, -.7) : rnd(0, Math.PI * 2), dist = rnd(opts.min || 30, opts.max || 120);
            c.anim(b, [{ opacity: 1, transform: 'translate(0,0) rotate(0)' },
            { opacity: 0, transform: `translate(${Math.cos(ang) * dist}px,${Math.sin(ang) * dist}px) rotate(${rnd(-200, 200)}deg) scale(.4)` }],
                { duration: rnd(600, 1000), easing: 'cubic-bezier(.2,.7,.3,1)', delay: rnd(0, 120) });
            setTimeout(() => b.remove(), 1400);
        }
    }

    // ---------------------------------------------------------------- neural field
    const field = (() => {
        const cvs = $('canvas.field'), g = cvs.getContext('2d');
        let W = 0, H = 0, dpr = 1, nodes = [], sparks = [], waves = [];
        let tint = [255, 138, 76], tintFrom = tint, tintTo = tint, tintT = 1, tintDur = 1;
        let level = 1, levelFrom = 1, levelTo = 1, levelT = 1, levelDur = 1;
        let px = -1e4, py = -1e4, spawn = 0;
        const LINK = 150;
        function resize() {
            dpr = LITE ? 1 : Math.min(1.5, window.devicePixelRatio || 1);
            W = cvs.clientWidth || innerWidth; H = cvs.clientHeight || innerHeight;
            cvs.width = Math.round(W * dpr); cvs.height = Math.round(H * dpr);
            const n = Math.round(clamp(W * H / (LITE ? 24000 : 14500), 46, LITE ? 64 : 118));
            nodes = Array.from({ length: n }, () => ({ x: rnd(0, W), y: rnd(0, H), vx: rnd(-9, 9), vy: rnd(-9, 9), r: rnd(.8, 2.1), f: 0 }));
            sparks = []; waves = [];
        }
        function step(dt) {
            const s = dt / 1000;
            if (tintT < 1) { tintT = Math.min(1, tintT + dt / tintDur); const e = easeIO(tintT); tint = tintFrom.map((v, i) => lerp(v, tintTo[i], e)); }
            if (levelT < 1) { levelT = Math.min(1, levelT + dt / levelDur); level = lerp(levelFrom, levelTo, easeIO(levelT)); }
            for (const n of nodes) {
                const dx = px - n.x, dy = py - n.y, d = Math.hypot(dx, dy);
                if (d < 210 && d > 1) { const f = (1 - d / 210) * 26 * s; n.vx += dx / d * f; n.vy += dy / d * f; }
                n.vx *= .992; n.vy *= .992;
                const sp = Math.hypot(n.vx, n.vy); if (sp > 34) { n.vx *= 34 / sp; n.vy *= 34 / sp; }
                if (sp < 4) { n.vx += rnd(-1, 1); n.vy += rnd(-1, 1); }
                n.x += n.vx * s; n.y += n.vy * s;
                if (n.x < -20) n.x = W + 20; else if (n.x > W + 20) n.x = -20;
                if (n.y < -20) n.y = H + 20; else if (n.y > H + 20) n.y = -20;
                n.f = Math.max(0, n.f - s * 1.6);
            }
            for (const w of waves) {
                w.r += 820 * s;
                for (let i = 0; i < nodes.length; i++) {
                    const n = nodes[i], d = Math.hypot(n.x - w.x, n.y - w.y);
                    if (Math.abs(d - w.r) < 22 && n.f < .5) { n.f = 1; if (Math.random() < .35) addSpark(i); }
                }
            }
            waves = waves.filter(w => w.r < w.max);
            spawn += s * 2.4 * level;
            while (spawn > 1) { spawn -= 1; addSpark(Math.floor(Math.random() * nodes.length)); }
            sparks = sparks.filter(k => (k.t += dt / k.dur) < 1);
        }
        function addSpark(i) {
            const a = nodes[i]; if (!a) return;
            let best = -1, bd = 1e9;
            for (let j = 0; j < nodes.length; j++) {
                if (j === i) continue;
                const d = Math.hypot(nodes[j].x - a.x, nodes[j].y - a.y);
                if (d < LINK && d < bd && Math.random() < .7) { bd = d; best = j; }
            }
            if (best >= 0 && sparks.length < 60) sparks.push({ a: i, b: best, t: 0, dur: rnd(550, 1000) });
        }
        function draw() {
            g.setTransform(dpr, 0, 0, dpr, 0, 0);
            g.clearRect(0, 0, W, H);
            if (level < .01) return;
            const [r, gg, b] = tint.map(v => Math.round(v));
            g.globalCompositeOperation = 'lighter';
            g.lineWidth = 1;
            for (let i = 0; i < nodes.length; i++) {
                const a = nodes[i];
                for (let j = i + 1; j < nodes.length; j++) {
                    const c = nodes[j], dx = a.x - c.x, dy = a.y - c.y, d2 = dx * dx + dy * dy;
                    if (d2 > LINK * LINK) continue;
                    const d = Math.sqrt(d2);
                    let al = Math.pow(1 - d / LINK, 2) * .3 * level;
                    const mx = (a.x + c.x) / 2 - px, my = (a.y + c.y) / 2 - py, dm = Math.hypot(mx, my);
                    if (dm < 240) al *= 1 + 2.2 * (1 - dm / 240);
                    al += (a.f + c.f) * .25 * level;
                    g.strokeStyle = `rgba(${r},${gg},${b},${Math.min(.9, al).toFixed(3)})`;
                    g.beginPath(); g.moveTo(a.x, a.y); g.lineTo(c.x, c.y); g.stroke();
                }
            }
            for (const n of nodes) {
                const near = Math.max(0, 1 - Math.hypot(n.x - px, n.y - py) / 220);
                const al = (.42 + near * .5 + n.f * .6) * level;
                g.fillStyle = `rgba(${r},${gg},${b},${(al * .16).toFixed(3)})`;
                g.beginPath(); g.arc(n.x, n.y, n.r * 4.5, 0, 6.283); g.fill();
                g.fillStyle = `rgba(255,${Math.round(220 + 35 * n.f)},${Math.round(200 + 55 * n.f)},${Math.min(1, al).toFixed(3)})`;
                g.beginPath(); g.arc(n.x, n.y, n.r, 0, 6.283); g.fill();
            }
            for (const k of sparks) {
                const a = nodes[k.a], c = nodes[k.b]; if (!a || !c) continue;
                const t = easeIO(k.t), x = lerp(a.x, c.x, t), y = lerp(a.y, c.y, t);
                const tx = lerp(a.x, c.x, Math.max(0, t - .18)), ty = lerp(a.y, c.y, Math.max(0, t - .18));
                const al = Math.sin(k.t * Math.PI) * level;
                const grad = g.createLinearGradient(tx, ty, x, y);
                grad.addColorStop(0, `rgba(${r},${gg},${b},0)`); grad.addColorStop(1, `rgba(255,236,220,${(al * .9).toFixed(3)})`);
                g.strokeStyle = grad; g.lineWidth = 1.6;
                g.beginPath(); g.moveTo(tx, ty); g.lineTo(x, y); g.stroke();
                g.fillStyle = `rgba(255,245,235,${al.toFixed(3)})`;
                g.beginPath(); g.arc(x, y, 1.8, 0, 6.283); g.fill();
            }
            for (const w of waves) {
                const al = (1 - w.r / w.max) * .32 * Math.max(.6, level);
                g.strokeStyle = `rgba(${r},${gg},${b},${al.toFixed(3)})`; g.lineWidth = 1.5;
                g.beginPath(); g.arc(w.x, w.y, w.r, 0, 6.283); g.stroke();
            }
            if (px > -1e3) {
                const rg = g.createRadialGradient(px, py, 0, px, py, 180);
                rg.addColorStop(0, `rgba(${r},${gg},${b},${(.09 * level).toFixed(3)})`); rg.addColorStop(1, `rgba(${r},${gg},${b},0)`);
                g.fillStyle = rg; g.beginPath(); g.arc(px, py, 180, 0, 6.283); g.fill();
            }
            g.globalCompositeOperation = 'source-over';
        }
        return {
            resize,
            frame(dt) {
                if (REDUCE) { if (!this._drawn) { this._drawn = 1; draw(); } return; }
                if (this._off) return;
                // lite: every canvas change repaints the whole window on the CPU, so redraw ~15x a second (the dots drift
                // at a few px/s: nobody can tell) and let the other frames stay cheap
                if (LITE) { this._acc = (this._acc || 0) + dt; if (this._acc < 62) return; dt = this._acc; this._acc = 0; }
                step(dt); draw();
            },
            /** The governor gave up on the canvas: stop drawing (the last frame fades out, see `.flat`), then clear it for good. */
            off() { this._off = true; setTimeout(() => g.clearRect(0, 0, cvs.width, cvs.height), 1000); },
            pointer(x, y) { px = x; py = y; },
            wave(x, y) { waves.push({ x, y, r: 0, max: Math.hypot(W, H) * .75 }); },
            tint(rgb, ms = 1200) { tintFrom = tint.slice(); tintTo = rgb; tintT = 0; tintDur = ms; },
            level(v, ms = 1200) { levelFrom = level; levelTo = v; levelT = 0; levelDur = ms; this._drawn = 0; },
        };
    })();
    const EMBER = [255, 138, 76], VIOLET = [167, 139, 250], GOLD = [255, 186, 96];

    // ---------------------------------------------------------------- stage scaling + tilt
    // A scene draws on a fixed design canvas (S.cv, default 760 x 500) that is scaled to fit the stage.
    function fitStage(run) {
        const st = run.el.querySelector('.stage'), tl = run.el.querySelector('.tilt'), cv = run.el.querySelector('.cv');
        if (!st || !tl || !cv) return;
        const [DW, DH] = run.S.cv || [760, 500];
        cv.style.width = DW + 'px'; cv.style.height = DH + 'px';
        const k = Math.max(.3, Math.min(st.clientWidth / (DW + 40), st.clientHeight / (DH + 30), 1.3));
        tl.style.width = DW * k + 'px'; tl.style.height = DH * k + 'px';
        cv.style.transform = `scale(${k})`;
        run.k = k;
    }
    let ptx = 0, pty = 0, tiltX = 0, tiltY = 0;

    // ---------------------------------------------------------------- demo windows
    // Every browser window in the film is the real thing: chrome.js hands over the toolbar's own
    // stylesheet, markup, icons and tab pills (window.__brainChrome.kit()), and BrAIn's own pages run
    // in an iframe on demo data. Only the websites inside the windows are drawn by the film.
    let KIT;
    function kit() {
        if (KIT === undefined) {
            KIT = null;
            try { const k = window.__brainChrome && window.__brainChrome.kit && window.__brainChrome.kit(); if (k && k.css) KIT = LITE ? Object.assign({}, k, { css: liteCss(k.css) + NO_LOOPS }) : k; } catch (e) { }
        }
        return KIT;
    }
    const esc = s => String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    const SPLIT = window.__BRAIN_ROLE === 'bar' || window.__BRAIN_SPLIT === true;
    const WW = 900, WH = 564, WIN_CV = [940, 612];
    // The toolbar normally fills the screen; here it sits inside a small window.
    const WIN_CSS = '.bar{position:absolute;animation:none;pointer-events:auto;cursor:default}' +
        '#askcard,#rwbar,#ttip,#usmenu,#ntp{position:absolute}#askcard,#rwbar,#usmenu,#ntp{pointer-events:auto}' +
        '#ntp{bottom:0;height:auto}' +
        // macOS draws these over the toolbar natively
        '.tls{position:absolute;left:13px;top:17px;display:flex;gap:8px;z-index:11;pointer-events:none}' +
        '.tls i{width:12px;height:12px;border-radius:50%;background:#ff5f57;box-shadow:inset 0 0 0 .5px rgba(0,0,0,.25)}' +
        '.tls i:nth-child(2){background:#febc2e}.tls i:nth-child(3){background:#28c840}';
    const winOpts = new Map();
    let winSeq = 0;
    // o: { tabs: [{ id, title, url, icon, priv, ntp }], active, pages: '<div class="wpage" data-tab="…">…</div>…' }
    const winHtml = o => {
        const id = ++winSeq;
        winOpts.set(id, o);
        return `<div class="win" data-w="${id}"><div class="gv2"></div>${o.pages}<div class="wch"></div></div>`;
    };
    function mountWins(el) { el.querySelectorAll('.win[data-w]').forEach(w => { const o = winOpts.get(+w.dataset.w); winOpts.delete(+w.dataset.w); if (o) w._w = buildWin(w, o); }); }
    function agLabel(t) { return String(t || '').replace(/^browser_/, '').replace(/^flow_/, 'flow: ').replace(/_/g, ' '); }

    function buildWin(win, o) {
        const K = kit(), wch = win.querySelector('.wch'), pages = Array.from(win.querySelectorAll('.wpage'));
        const W = { win, tabs: [], hue: null, gv: 0, ghostOn: false, rwOn: false };
        // A tab switch crossfades like the browser's (WkTabs.Show): the new page fades in over
        // the old one, which is hidden once it is covered.
        const showPage = (id, fade) => {
            const inc = pages.find(p => p.dataset.tab === id), out = pages.find(p => p !== inc && !p.classList.contains('off'));
            pages.forEach(p => { if (p !== out) p.classList.toggle('off', p !== inc); });
            if (!out) return;
            if (!fade || REDUCE) { out.classList.add('off'); return; }
            let done = false;
            const fin = () => { if (done) return; done = true; out.classList.add('off'); if (inc) inc.style.zIndex = ''; };
            if (inc) {
                inc.style.zIndex = '1';
                inc.animate([{ opacity: 0, translate: '0 8px' }, { opacity: 1, translate: '0 0' }], { duration: 220, easing: 'cubic-bezier(0,0,.58,1)' }).onfinish = fin;
            }
            setTimeout(fin, inc ? 400 : 220);
        };
        // The way chrome.js lets cards and bars go (wireExits): a short fade, then hidden.
        const exitFade = (el, kind, then) => {
            if (REDUCE || !el.animate || getComputedStyle(el).display === 'none') { then(); return; }
            const a = el.animate(kind === 'up' ? [{ opacity: 1, translate: '0 0' }, { opacity: 0, translate: '0 -6px' }]
                : kind === 'fade' ? [{ opacity: 1 }, { opacity: 0 }] : [{ opacity: 1, scale: '1' }, { opacity: 0, scale: '.97' }],
                { duration: kind === 'fade' ? 160 : 130, easing: 'cubic-bezier(.4,0,1,1)', fill: 'forwards' });
            let done = false;
            const fin = () => { if (done) return; done = true; then(); a.cancel(); };
            a.onfinish = fin; setTimeout(fin, 400);
        };
        W.cur = () => W.tabs.find(x => x.t.active);
        if (!K) { // no toolbar to borrow (never expected): show the pages bare
            W.$ = () => null; W.activate = showPage;
            for (const k of ['agent', 'tip', 'untip', 'hover', 'busy', 'belt', 'rec', 'rwOpen', 'us', 'usMenu', 'addTab']) W[k] = () => null;
            W.ghost = () => Promise.resolve(); W.ntp = () => { };
            showPage(o.active);
            return W;
        }
        const root = wch.attachShadow({ mode: 'open' });
        root.innerHTML = `<style>${K.css}${WIN_CSS}</style>${K.bar()}${K.ntp()}${K.rwbar()}<div id="askcard"></div><div id="usmenu"></div><div id="ttip"></div>` +
            (CFG.mac ? '<div class="tls"><i></i><i></i><i></i></div>' : '');
        const $ = id => root.getElementById(id), bar = root.querySelector('.bar');
        Object.assign(W, { root, $, bar, K });
        if (CFG.mac) bar.classList.add('mtl'); else $('wctrl').style.display = 'flex';
        root.querySelector('[data-cmd="forward"]').classList.add('dis');

        W.addTab = (t, fresh) => {
            t = Object.assign({ active: false }, t);
            const el = K.tab(t);
            if (!fresh) el.classList.add('noanim');
            el.dataset.id = t.id;
            $('tabs').appendChild(el);
            W.tabs.push({ t, el });
            return el;
        };
        W.tabEl = id => (W.tabs.find(x => x.t.id === id) || {}).el;
        const sync = () => W.tabs.forEach(x => K.tab(x.t, x.el));
        // address bar, same rules as chrome.js updateAddr / updateStar
        W.addr = url => {
            const addr = $('addr'), lock = $('lock'), star = $('star');
            if (!url) { addr.innerHTML = '<span class="dim">Search or type a URL</span>'; lock.style.opacity = '0'; star.style.display = 'none'; return; }
            addr.innerHTML = K.url(url);
            const https = url.startsWith('https:');
            lock.style.opacity = url.startsWith('http') ? '1' : '.35';
            lock.innerHTML = https ? K.svg.lock : K.svg.warn;
            lock.classList.toggle('bad', url.startsWith('http') && !https);
            // the split toolbar view reads its own (non-http) address, so the star never shows there
            star.style.display = !SPLIT && /^https?:/.test(url) && !(W.cur() && W.cur().t.priv) ? 'grid' : 'none';
        };
        W.setHue = h => {
            if (W.hue === h) return;
            const first = W.hue === null;
            W.hue = h;
            wch.style.setProperty('--h', h);
            if (!first) { const on1 = bar.classList.contains('sw1'); bar.classList.remove('sw1', 'sw2'); bar.classList.add(on1 ? 'sw2' : 'sw1'); }
        };
        W.accent = () => { const a = W.cur(); W.setHue(W.ghostOn ? 278 : a && !a.t.ntp ? K.hue(a.t.url) : 24); };
        W.ntp = (on, priv) => {
            const n = $('ntp');
            if (!on) { if (n.classList.contains('on')) exitFade(n, 'fade', () => n.classList.remove('on')); return; }
            n.classList.add('on'); n.classList.toggle('priv', !!priv); n.classList.toggle('ghost', W.ghostOn);
            const d = new Date(), hh = d.getHours(), mm = d.getMinutes(); // as chrome.js ntpTick
            $('ntpck').textContent = (hh < 10 ? '0' : '') + hh + ':' + (mm < 10 ? '0' : '') + mm;
            $('ntpdt').textContent = d.toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric' });
            $('ntpgr').textContent = hh < 5 ? 'Still up' : hh < 12 ? 'Good morning' : hh < 18 ? 'Good afternoon' : 'Good evening';
        };
        W.activate = id => {
            const was = W.cur();
            W.tabs.forEach(x => { x.t.active = x.t.id === id; });
            sync();
            const a = W.cur();
            showPage(a.t.ntp ? '' : id, !!was && was !== a);
            W.ntp(!!a.t.ntp, a.t.priv);
            W.addr(a.t.ntp ? '' : a.t.url);
            $('omni').classList.toggle('priv', !!a.t.priv || W.ghostOn);
            $('rwb').classList.toggle('rec', W.rwOn && !W.ghostOn && !a.t.ntp && !a.t.priv);
            W.accent();
        };
        // tab loading: the comet ring, the orb spin, reload turns into stop (chrome.js syncTabEl / updateNav)
        W.busy = (id, on, rx) => {
            const x = W.tabs.find(x => x.t.id === id); if (!x) return;
            x.t.busy = !!on; x.t.rx = !!rx;
            K.tab(x.t, x.el);
            bar.classList.toggle('ld', !!on);
            const rel = $('rel');
            rel.innerHTML = on ? K.svg.stop : K.svg.reload;
            rel.classList.toggle('stopping', !!on);
        };
        // tooltips + the sliding hover pill (chrome.js wireTools)
        let pinned = null, tipT = 0;
        W.tip = (b, text, sc, pin) => {
            if (typeof b === 'string') b = $(b);
            if (pin) pinned = { b, text, sc };
            const tip = $('ttip'), k = K.sc(sc || '');
            tip.innerHTML = `<span>${esc(text)}</span>` + (k ? `<kbd>${esc(k)}</kbd>` : '');
            const x = posIn(b, bar).x + b.offsetWidth / 2, w = tip.offsetWidth;
            tip.style.left = clamp(x - w / 2, 8, WW - w - 8) + 'px';
            tip.classList.add('on');
        };
        W.untip = () => { pinned = null; $('ttip').classList.remove('on'); };
        const hov = (box, hl, b) => {
            if (!b) { box.classList.remove('hov'); return; }
            if (!box.classList.contains('hov')) {
                hl.style.transition = 'opacity .16s';
                hl.style.width = b.offsetWidth + 'px'; hl.style.transform = `translateX(${b.offsetLeft}px)`;
                void hl.offsetWidth; hl.style.transition = '';
                box.classList.add('hov');
            } else { hl.style.width = b.offsetWidth + 'px'; hl.style.transform = `translateX(${b.offsetLeft}px)`; }
        };
        W.hover = b => {
            if (typeof b === 'string') b = $(b);
            hov($('tools'), $('hl'), b && b.closest('.tools') ? b : null);
            hov($('nav'), $('nhl'), b && b.closest('.nav') ? b : null);
        };
        for (const [box, sel] of [[$('tools'), '.tb'], [$('nav'), '.nav-b']]) {
            box.addEventListener('mouseover', e => {
                const b = e.target.closest && e.target.closest(sel); if (!b) return;
                W.hover(b); clearTimeout(tipT);
                tipT = setTimeout(() => { if (b.getAttribute('data-tip')) W.tip(b, b.getAttribute('data-tip'), b.getAttribute('data-sc')); }, 380);
            });
            box.addEventListener('mouseleave', () => {
                box.classList.remove('hov'); clearTimeout(tipT);
                if (pinned) W.tip(pinned.b, pinned.text, pinned.sc); else $('ttip').classList.remove('on');
            });
        }
        // agent button + the sweep under the toolbar (chrome.js paintAgent)
        W.agent = (mode, k, tool, showTip) => {
            const b = $('agbtn');
            b.classList.toggle('ag-on', mode !== 'off');
            b.classList.toggle('ag-run', mode === 'run');
            b.classList.toggle('ag-pause', mode === 'pause');
            bar.classList.toggle('agrun', mode === 'run'); bar.classList.toggle('agpause', mode === 'pause');
            const key = mode === 'pause' ? 'p' + (k || 0) : 'a';
            if (b._k !== key) {
                b._k = key;
                b.innerHTML = mode === 'pause' ? K.svg.pause + (k ? `<i class="agk">${Math.min(k, 99)}</i>` : '') : K.svg.bot + '<i class="agd"></i>';
            }
            const tip = mode === 'pause' ? 'Agent paused — click to resume' + (k ? ' (' + k + ' blocked)' : '')
                : mode === 'run' ? 'Agent working' + (tool ? ': ' + agLabel(tool) : '') + ' — click to pause'
                    : 'Agent connected — click to pause';
            b.setAttribute('data-tip', tip);
            if (showTip) W.tip(b, tip, 'Ctrl+Shift+.', true);
            else if (pinned && pinned.b === b) W.untip();
        };
        // Ghost mode: one value 0..1 drives the violet layer, the pill and the page veil (chrome.js ghostApply)
        let pw = 0;
        const veil = win.querySelector('.gv2');
        W.gApply = v => {
            W.gv = v;
            const gb = $('ghb');
            if (!gb || !$('gmist') || !$('gpill')) return; // a toolbar without Ghost mode
            const gx = posIn(gb, bar).x + gb.offsetWidth / 2;
            $('gmist').style.clipPath = `circle(${(v * Math.ceil(Math.hypot(Math.max(gx, WW - gx), 46))).toFixed(1)}px at ${gx}px 50%)`;
            const pill = $('gpill');
            if (!pw) { const keep = pill.style.cssText; pill.style.maxWidth = 'none'; pill.style.padding = '0 11px 0 8px'; pw = pill.offsetWidth || 80; pill.style.cssText = keep; }
            const q = clamp((v - .35) / .65, 0, 1);
            pill.style.maxWidth = (q * pw).toFixed(1) + 'px';
            pill.style.padding = q ? `0 ${(11 * q).toFixed(2)}px 0 ${(8 * q).toFixed(2)}px` : '0';
            pill.style.opacity = q.toFixed(3);
            pill.style.transform = `translateX(${((1 - q) * 8).toFixed(1)}px)`;
            pill.style.pointerEvents = q > .5 ? 'auto' : 'none';
            if (v <= 0 || v >= 1) { veil.style.opacity = '0'; return; }
            const rp = Math.ceil(Math.hypot(Math.max(gx, WW - gx), WH + 46)), grow = Math.min(1, v / .6);
            veil.style.background = `radial-gradient(circle at ${gx}px 0,rgba(88,48,170,.5),rgba(24,12,48,.42) 75%)`;
            veil.style.clipPath = `circle(${(grow * rp).toFixed(1)}px at ${gx}px 23px)`;
            veil.style.opacity = (v < .6 ? 1 : 1 - (v - .6) / .4).toFixed(3);
        };
        W.ghost = (c, on) => {
            if (W.ghostOn === on) return Promise.resolve();
            W.ghostOn = on;
            const gb = $('ghb');
            if (!gb) return Promise.resolve();
            gb.classList.toggle('on', on);
            gb.setAttribute('aria-pressed', on ? 'true' : 'false');
            gb.setAttribute('data-tip', on ? 'Ghost mode is ON — nothing is saved. Click to turn off' : 'Ghost mode — browse without leaving a trace');
            bar.classList.toggle('ghost', on);
            const a = W.cur();
            $('omni').classList.toggle('priv', on || !!(a && a.t.priv));
            $('ntp').classList.toggle('ghost', on);
            $('rwb').classList.toggle('rec', W.rwOn && !on && !(a && (a.t.ntp || a.t.priv)));
            W.accent();
            const from = W.gv, to = on ? 1 : 0;
            if (REDUCE) { W.gApply(to); return Promise.resolve(); }
            return c.tween(Math.max(140, 460 * Math.abs(to - from)), p => W.gApply(from + (to - from) * p), easeOut);
        };
        W.gApply(0);
        // Rewind recording dot, and the scrubber bar under the toolbar
        W.rec = on => { W.rwOn = on; $('rwb').classList.toggle('on', on); $('rwb').classList.toggle('rec', on && !W.ghostOn); };
        W.rwOpen = on => {
            const rb = $('rwbar');
            if (on) rb.classList.add('on'); else if (rb.classList.contains('on')) exitFade(rb, 'up', () => rb.classList.remove('on'));
            pages.forEach(p => { p.style.top = on ? '90px' : ''; });
        };
        // Seatbelt card: the real markup (chrome.js beltHtml)
        W.belt = a => {
            const b = $('askcard');
            if (!a) { exitFade(b, 'pop', () => { b.style.display = 'none'; b.innerHTML = ''; }); return null; }
            b.innerHTML = K.belt(a, 0); b.style.display = 'flex';
            return b;
        };
        // Site scripts badge + its menu (chrome.js paintUs / fillUs)
        W.us = list => {
            const b = $('usbtn'), n = list.length;
            b.classList.toggle('us-on', n > 0);
            b.innerHTML = K.svg.code + `<i class="usk">${n}</i>`;
            b.setAttribute('data-tip', n + ' site script' + (n === 1 ? '' : 's') + ' running here');
            W.usList = list;
        };
        W.usMenu = on => {
            const m = $('usmenu'), b = $('usbtn');
            if (!on) { m.classList.remove('on'); return; }
            m.innerHTML = '<div class="uh">Site scripts on this page</div>' +
                (W.usList || []).map(x => `<div class="ur"><b>${esc(x)}</b><i class="usw"></i></div>`).join('') +
                '<div class="ufoot"><span>Manage scripts</span><span>Pausing applies on next load</span></div>';
            m.style.right = Math.max(8, WW - (posIn(b, bar).x + b.offsetWidth) - 6) + 'px';
            m.classList.add('on');
        };

        for (const t of o.tabs) W.addTab(t, false);
        W.activate(o.active);
        return W;
    }

    // BrAIn's own pages (about:flows, about:personality) in a demo window: the real page and its code,
    // with a stand-in host that answers its commands with demo data instead of touching the browser.
    function demoPage(c, ifr, html, onCmd) {
        const stub = '<script>(function(){var q=[];window.__demoQ=q;' +
            'window.external={sendMessage:function(m){m=String(m);if(window.__demo)window.__demo(m);else q.push(m)},receiveMessage:function(){}};' +
            'window.confirm=function(){return false};window.alert=function(){};window.prompt=function(){return null};})();<\/script>';
        return new Promise(res => {
            ifr.addEventListener('load', () => {
                const w = ifr.contentWindow;
                const handle = m => { if (c.alive) { try { onCmd(m.replace(/^brain:/, ''), w); } catch (e) { } } };
                w.__demo = handle;
                (w.__demoQ || []).splice(0).forEach(handle);
                w.addEventListener('keydown', e => { if (e.key === 'Escape' && !w.document.querySelector('.modal.on')) close('skip'); }, true);
                w.addEventListener('pointerdown', () => { if (c.alive) c.touch(); }, true);
                res(w);
            }, { once: true });
            if (LITE) html = liteCss(html).replace(/<head>/i, '<head><style>' + NO_LOOPS + '</style>'); // the real page, minus its big shadows and loops
            ifr.srcdoc = html.replace(/<head>/i, '<head>' + stub);
        });
    }
    // Scrolls only the page inside the iframe (scrollIntoView would also scroll the film around it).
    function frameScroll(w, el, pad) { try { if (w && el) w.scrollTo(0, el.getBoundingClientRect().top + w.scrollY - pad); } catch (e) { } }
    function pushTo(w, j) { try { w.document.dispatchEvent(new w.CustomEvent('brain-msg', { detail: w.JSON.parse(JSON.stringify(j)) })); } catch (e) { } }
    // Typing into a real page's field, the way a person would (value + input events).
    async function typeField(c, el, text, cps = 16) {
        el.focus({ preventScroll: true });
        for (const ch of text) {
            el.value += ch;
            el.dispatchEvent(new el.ownerDocument.defaultView.Event('input', { bubbles: true }));
            await c.wait(1000 / cps * rnd(.65, 1.35));
        }
    }
    // Site icons for the demo tabs (a tab shows the site's own favicon)
    const fav = (bg, inner) => 'data:image/svg+xml,' + encodeURIComponent(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><rect width="16" height="16" rx="4" fill="${bg}"/>${inner}</svg>`);
    const FAV = {
        sky: fav('#3557e8', '<path fill="#fff" transform="translate(2.2 2.2) scale(.72)" d="M14.6 7.1c.6 0 .9.4.9.9s-.3.9-.9.9H10l-3.6 5.3H4.8L6.6 8.9H3.5l-1.2 1.6H1L1.9 8 1 5.5h1.3l1.2 1.6h3.1L4.8 1.8h1.6L10 7.1z"/>'),
        lumen: fav('#1b1411', '<text x="8" y="11.6" text-anchor="middle" font-family="Helvetica,Arial" font-weight="800" font-size="10" fill="#ffb27a">L</text>'),
        news: fav('#1d1714', '<text x="8" y="12" text-anchor="middle" font-family="Georgia,serif" font-weight="900" font-size="11" fill="#fbf8f4">B</text>'),
    };

    // ---- 0. Intro
    const S_INTRO = {
        label: 'Intro', dur: 6800, tint: EMBER, level: 1, center: true,
        html: () => `
          <div class="intro">
            <div class="markwrap">${markSvg('i')}</div>
            <div class="word">${'BrAIn'.split('').map((ch, i) => `<span class="${i === 2 || i === 3 ? 'ai' : ''}">${ch}</span>`).join('')}</div>
            <p class="tag">The browser your <em>AI</em> can drive.</p>
            <p class="tag2">A real browser. A built-in MCP server. And you, always in control.</p>
          </div>
          <div class="hint"><i></i>It\u2019s alive \u2014 move your mouse, click anywhere</div>`,
        async play(c, el) {
            const mw = el.querySelector('.markwrap');
            await drawMark(c, mw.querySelector('.mark'), 0, true);
            el.querySelectorAll('.word span').forEach((s, i) => c.anim(s, REDUCE ? [{ opacity: 0 }, { opacity: 1 }] :
                [{ opacity: 0, transform: 'translateY(.35em) scale(.92)', filter: 'blur(14px)' }, { opacity: 1, transform: 'none', filter: 'blur(0)' }],
                { duration: 1100, delay: 120 + i * 70 }));
            const tag = el.querySelector('.tag'); reveal(c, tag, 700, 70);
            fadeUp(c, el.querySelector('.tag2'), 1400, 10, 900);
            fadeUp(c, el.querySelector('.hint'), 2600, 8, 800);
            c.on(mw, 'click', () => { c.touch(); pulseMark(c, mw.querySelector('.mark'), 6); });
            await c.wait(1600);
            pulseMark(c, mw.querySelector('.mark'), 0);
        },
    };
    // Mark choreography: contour draws itself, the mesh wires up, nodes pop, then pulses run outward forever.
    async function drawMark(c, svg, delay = 0, big = false) {
        const contour = svg.querySelector('.contour');
        c.anim(svg.querySelector('.halo'), [{ opacity: 0, transform: 'scale(.4)' }, { opacity: 1, transform: 'scale(1)' }], { duration: 1600, delay: delay + 300 });
        c.anim(contour, [{ strokeDasharray: '1 1', strokeDashoffset: '1' }, { strokeDasharray: '1 1', strokeDashoffset: '0' }], { duration: big ? 1500 : 1100, delay, easing: 'cubic-bezier(.6,0,.2,1)' });
        svg.querySelectorAll('.edges line,.spokes line').forEach((ln, i) =>
            c.anim(ln, [{ strokeDasharray: '1 1', strokeDashoffset: '1' }, { strokeDasharray: '1 1', strokeDashoffset: '0' }], { duration: 520, delay: delay + 420 + i * 70, easing: E_IO }));
        svg.querySelectorAll('.nd').forEach((nd, i) =>
            c.anim(nd, [{ opacity: 0, transform: 'scale(0)' }, { opacity: 1, transform: 'scale(1)' }], { duration: 640, delay: delay + 820 + i * 70, easing: E_BACK }));
        svg.querySelectorAll('.nd').forEach(nd => { nd.style.transformBox = 'fill-box'; nd.style.transformOrigin = 'center'; });
        const halo = svg.querySelector('.halo'); halo.style.transformBox = 'fill-box'; halo.style.transformOrigin = 'center';
        if (big) c.anim(svg, [{ transform: 'scale(.82) rotate(-8deg)' }, { transform: 'none' }], { duration: 2200, delay, easing: E_OUT });
    }
    function pulseMark(c, svg, burst) {
        const ring = JSON.parse(svg.getAttribute('data-ring')), g = svg.querySelector('.pulses');
        const fire = (i, d) => {
            const p = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
            p.setAttribute('r', '1.9'); g.appendChild(p);
            const [x, y] = ring[i % 5];
            c.anim(p, [{ transform: 'translate(0px,0px)', opacity: 0 }, { opacity: 1, offset: .15 }, { transform: `translate(${x}px,${y}px)`, opacity: 0 }],
                { duration: 900, delay: d, iterations: burst ? 1 : Infinity, easing: 'cubic-bezier(.4,0,.6,1)', fill: 'none' });
        };
        if (burst) for (let i = 0; i < burst * 2; i++) fire(i, i * 40);
        else for (let i = 0; i < 5; i++) fire(i, i * 360 + 200);
    }

    // ---- 1. AI agent
    const flightsPage = `
      <div class="fl">
        <div class="fl-top"><span class="fl-logo"><i>${I.plane}</i>Skyline</span><span class="fl-navs"><span>Flights</span><span>Hotels</span><span>My trips</span></span></div>
        <div class="fl-search">
          <div class="fld"><label>From</label><div class="v">Berlin (BER)</div></div>
          <div class="fld to"><label>To</label><div class="v"><span class="ph">Where to?</span><span class="tx"></span><i class="caret"></i></div>
            <div class="sugg"><div class="sg hot"><b>Tbilisi</b> (TBS) · Georgia</div><div class="sg">Tbilisi Natakhtari (TBN)</div></div></div>
          <div class="fld"><label>Depart</label><div class="v">Fri, 14 Nov</div></div>
          <button class="go" type="button" tabindex="-1">Search</button>
        </div>
        <div class="fl-res">
          <div class="res" style="--c:#2f4fd6"><div class="air"><i>NJ</i>NorthJet</div><div class="leg">BER 06:10 → TBS 14:55 · 1 stop</div><div class="pr">€214</div><button class="sel" tabindex="-1">Select</button></div>
          <div class="res cheap" style="--c:#e0532f"><span class="best">Cheapest</span><div class="air"><i>CA</i>Caspia Air</div><div class="leg">BER 09:40 → TBS 17:20 · 1 stop</div><div class="pr">€129</div><button class="sel" tabindex="-1">Select</button></div>
          <div class="res" style="--c:#1b8f6a"><div class="air"><i>SW</i>Silkway</div><div class="leg">BER 13:05 → TBS 21:30 · 1 stop</div><div class="pr">€176</div><button class="sel" tabindex="-1">Select</button></div>
        </div>
        <div class="paybar"><div>Caspia Air · BER → TBS<br><span>Fri, 14 Nov · 1 adult</span></div><button class="pay" type="button" tabindex="-1">Pay €129</button></div>
        <div class="done"><i>${I.check}</i><div>You’re booked · BER → TBS<small>Caspia Air · Fri, 14 Nov · €129</small></div></div>
      </div>`;
    const S_AGENT = {
        label: 'AI agent', dur: 15500, tint: EMBER, level: .5, cv: WIN_CV,
        html: () => `
          <div class="copy"><div class="cp">
            <div class="eyebrow">AI agent</div>
            <h1 class="hl">Your AI drives.<br><em>You stay in control.</em></h1>
            <p class="sub">BrAIn has an <b>MCP server built in</b>. Claude — or any MCP agent — uses the real browser with your logins, right next to you. <b>Seatbelt</b> stops it before anything risky.</p>
            <div class="chips"><span class="chip">${I.spark}~35 browser tools</span><span class="chip">${I.pause}Pause anytime</span><span class="chip">${I.shield}Ask before paying, deleting, sending</span></div>
            <div class="try"><i></i><span class="tryt">Click the robot in the toolbar to pause it</span></div>
          </div></div>
          <div class="stage"><div class="tilt"><div class="cv">
            ${winHtml({ tabs: [{ id: 'sky', title: 'Skyline — Cheap flights', url: 'https://skyline.example/flights', icon: FAV.sky }], active: 'sky', pages: `<div class="wpage" data-tab="sky">${flightsPage}</div>` })}
            <div class="bubble you" style="left:-40px;top:150px"><div class="who">You → Claude</div>Find the cheapest flight to Tbilisi next Friday and book it.</div>
            <div class="bubble ai a1" style="right:-26px;top:118px;left:auto"><div class="who">Claude</div>On it — opening Skyline.</div>
            <div class="bubble ai a2" style="right:-26px;top:170px;left:auto"><div class="who">Claude</div>Understood — I didn’t pay. Nothing was booked.</div>
          </div></div></div>`,
        async play(c, el, run) {
            const cv = el.querySelector('.cv'), win = cv.querySelector('.win'), W = win._w, page = win.querySelector('.wpage');
            const cp = el.querySelector('.cp'), agb = W.$('agbtn');
            const S = { paused: false, tool: '' };
            const gate = async () => {
                while (S.paused) { await c.wait(120); if (c.idle() > 7000) setPaused(false); }
                if (!c.alive) throw new Abort();
            };
            const work = tool => { S.tool = tool; if (!S.paused) W.agent('run', 0, tool, true); };
            const agent = makeCursor(cv, 'agent', 'Claude');
            function setPaused(v) {
                S.paused = v;
                W.agent(v ? 'pause' : 'run', 0, S.tool, true);
            }
            if (agb) c.on(agb, 'click', e => { e.stopPropagation(); if (S.locked) return; c.touch(); setPaused(!S.paused); });
            W.agent('on');

            showCopy(c, cp, 0);
            c.anim(win, [{ opacity: 0, transform: 'translateY(40px) scale(.96)' }, { opacity: 1, transform: 'none' }], { duration: 1100, delay: 100 });
            await c.wait(700);
            const you = cv.querySelector('.bubble.you'), ai = cv.querySelector('.bubble.a1');
            c.anim(you, [{ opacity: 0, transform: 'translateY(12px) scale(.9)' }, { opacity: 1, transform: 'none' }], { duration: 650, easing: E_BACK });
            await c.wait(900);
            c.anim(ai, [{ opacity: 0, transform: 'translateY(12px) scale(.9)' }, { opacity: 1, transform: 'none' }], { duration: 650, easing: E_BACK });
            await c.wait(500);
            work('browser_navigate'); W.busy('sky', true, true);
            await c.wait(450);
            W.busy('sky', false);
            work('browser_snapshot');
            showTry(c, cp, 600);
            placeCursor(agent, 760, 520);
            c.anim(agent, [{ opacity: 0 }, { opacity: 1 }], { duration: 400 });
            c.anim(you, [{ opacity: 1 }, { opacity: 0, transform: 'translateY(-8px)' }], { duration: 500, delay: 1400 });
            c.anim(ai, [{ opacity: 1 }, { opacity: 0, transform: 'translateY(-8px)' }], { duration: 500, delay: 1600 });

            const to = page.querySelector('.fld.to'), tx = to.querySelector('.tx'), ph = to.querySelector('.ph'), sugg = to.querySelector('.sugg');
            await gate(); await moveToEl(c, agent, to, cv, .4, .6);
            work('browser_click');
            await click(c, agent, cv);
            to.classList.add('focus'); ph.style.display = 'none';
            work('browser_type');
            await gate(); await typeInto(c, tx, 'Tbili', 13);
            c.anim(sugg, [{ opacity: 0, transform: 'translateY(-4px)' }, { opacity: 1, transform: 'none' }], { duration: 300 });
            await c.wait(250);
            await gate(); await moveToEl(c, agent, sugg.querySelector('.sg'), cv, .35, .6, 520);
            work('browser_click');
            await click(c, agent, cv);
            tx.textContent = 'Tbilisi (TBS)';
            c.anim(sugg, [{ opacity: 1 }, { opacity: 0 }], { duration: 200 });
            to.classList.remove('focus');
            const go = page.querySelector('.go');
            await gate(); await moveToEl(c, agent, go, cv);
            await click(c, agent, cv, go);

            // the results load: the tab's ring spins while the page fetches
            const resWrap = page.querySelector('.fl-res'), cards = Array.from(resWrap.querySelectorAll('.res'));
            const sk = cards.map(() => { const s = document.createElement('div'); s.className = 'skel'; return s; });
            cards.forEach(cd => { cd.style.display = 'none'; }); sk.forEach(s => resWrap.appendChild(s));
            W.busy('sky', true, false);
            await c.wait(380);
            W.busy('sky', true, true);
            await c.wait(420);
            W.busy('sky', false);
            sk.forEach(s => s.remove()); cards.forEach(cd => { cd.style.display = ''; });
            cards.forEach((cd, i) => c.anim(cd, [{ opacity: 0, transform: 'translateY(10px)' }, { opacity: 1, transform: 'none' }], { duration: 520, delay: i * 90 }));
            work('browser_snapshot');
            await c.wait(1300);
            const cheap = page.querySelector('.res.cheap'), sel = cheap.querySelector('.sel');
            await gate(); await moveToEl(c, agent, sel, cv);
            work('browser_click');
            await click(c, agent, cv, sel);
            cheap.classList.add('pick');
            const paybar = page.querySelector('.paybar'), pay = paybar.querySelector('.pay');
            c.anim(paybar, [{ transform: 'translateY(160%)' }, { transform: 'none' }], { duration: 700 });
            await c.wait(650);
            await gate(); await moveToEl(c, agent, pay, cv, .3, .5);
            work('browser_click');
            await click(c, agent, cv, pay);

            // Seatbelt: the click looks like a payment, so BrAIn holds it and asks you
            S.locked = true;
            W.untip();
            const belt = W.belt({ tool: 'browser_click', label: 'Pay €129', host: 'skyline.example', why: 'looks like a payment or purchase' });
            cp.querySelector('.tryt').textContent = 'Your call — Allow or Deny';
            c.anim(cp.querySelector('.try'), [{ opacity: .3 }, { opacity: 1 }], { duration: 400 });
            let choice = null;
            const decided = belt ? new Promise(res => {
                c.on(belt.querySelector('.ok'), 'click', e => { e.stopPropagation(); c.touch(); choice = choice || 'allow'; res(); });
                c.on(belt.querySelector('.st'), 'click', e => { e.stopPropagation(); c.touch(); choice = choice || 'allow'; res(); });
                c.on(belt.querySelector('.dn'), 'click', e => { e.stopPropagation(); c.touch(); choice = choice || 'deny'; res(); });
            }) : Promise.resolve();
            await Promise.race([decided, c.wait(4600)]);
            if (!c.alive) throw new Abort();
            if (!choice) {
                const me = makeCursor(cv, 'you', 'You');
                placeCursor(me, 880, 590);
                c.anim(me, [{ opacity: 0 }, { opacity: 1 }], { duration: 300 });
                const ok = belt && belt.querySelector('.ok');
                if (ok) await moveToEl(c, me, ok, cv, .5, .6, 900);
                if (!choice) { await click(c, me, cv); choice = 'allow'; }
                c.anim(me, [{ opacity: 1 }, { opacity: 0 }], { duration: 500, delay: 300 });
            }
            W.belt(null);
            await c.wait(250);
            const done = page.querySelector('.done');
            if (choice === 'allow') {
                work('browser_click');
                await moveToEl(c, agent, pay, cv, .5, .55, 380);
                await click(c, agent, cv, pay);
                W.busy('sky', true, true);
                await c.wait(500);
                W.busy('sky', false);
                c.anim(done, [{ opacity: 0, transform: 'translate(-50%,-50%) scale(.85)' }, { opacity: 1, transform: 'translate(-50%,-50%) scale(1)' }], { duration: 650, easing: E_BACK });
                shatter(c, done, page, ['#ff8a4c', '#ffc978', '#ff5f9e', '#43d58e'], { n: 26, min: 60, max: 170, centered: true });
                field.wave(innerWidth * .62, innerHeight * .5);
            } else {
                c.anim(agent, [{ opacity: 1 }, { opacity: 0 }], { duration: 500 });
                c.anim(cv.querySelector('.bubble.a2'), [{ opacity: 0, transform: 'translateY(12px) scale(.9)' }, { opacity: 1, transform: 'none' }], { duration: 650, easing: E_BACK });
            }
            await c.wait(1200);
            W.agent('on');
            await c.wait(600);
        },
    };

    // ---- 2. Rewind + Ghost
    const FR = [ // four snapshots, then the live page
        { ago: 2292000, price: '€139', was: '', badge: 'Pre-order', bc: 'b-blue', buy: 'Pre-order', hue: 205 },
        { ago: 1385000, price: '€129', was: '€149', badge: 'In stock · free shipping', bc: 'b-green', buy: 'Add to cart', hue: 24 },
        { ago: 707000, price: '€129', was: '€149', badge: 'In stock', bc: 'b-green', buy: 'Add to cart', hue: 24 },
        { ago: 249000, price: '€149', was: '', badge: 'Only 2 left', bc: 'b-amber', buy: 'Add to cart', hue: 330 },
        { ago: 0, price: '€149', was: '', badge: 'Sold out', bc: 'b-red', buy: 'Notify me', hue: 330, dis: true },
    ];
    const shopPage = `
      <div class="shop">
        <div class="sh-nav"><b>Lumen</b><span>Audio</span><span>Sale</span><span>Support</span></div>
        <div class="sh-main">
          <div class="sh-img"><div class="hp"><i class="band"></i><i class="cup l"></i><i class="cup r"></i></div></div>
          <div class="sh-info">
            <div class="sh-kick">Lumen Audio</div>
            <div class="sh-title">Aurora ANC<br>Headphones</div>
            <div class="sh-stars">★★★★☆<span>2,148 reviews</span></div>
            <div class="sh-price"><span class="now">€149</span><span class="was"></span></div>
            <div class="sh-badge b-red">Sold out</div>
            <button class="sh-buy dis" type="button" tabindex="-1">Notify me</button>
            <div class="sh-ship">Free returns · 2-year warranty</div>
          </div>
        </div>
      </div>`;
    const S_REWIND = {
        label: 'Rewind & Ghost', dur: 21000, tint: EMBER, level: .5, cv: WIN_CV,
        html: () => `
          <div class="copy">
            <div class="cp a">
              <div class="eyebrow">Rewind</div>
              <h1 class="hl">See any page<br><em>as it was.</em></h1>
              <p class="sub">Price went up? Post deleted? Scrub back through what the page looked like minutes ago. Snapshots stay <b>in memory only</b> — never private tabs or login pages.</p>
              <div class="try"><i></i>Drag the Rewind slider</div>
            </div>
            <div class="cp v b later">
              <div class="eyebrow">Ghost mode</div>
              <h1 class="hl">One switch.<br><em>No trace.</em></h1>
              <p class="sub">No history. No saved passwords. No Rewind snapshots. No session on disk. Tabs you open are private — and vanish when you close them.</p>
              <button class="gsw" type="button" role="switch" aria-checked="false"><span class="knob"></span>${I.ghost}Ghost mode <kbd class="k">${CFG.mac ? '⇧⌘X' : 'Ctrl+Shift+X'}</kbd></button>
              <div class="try"><i></i>Flip the switch — or the ghost in the toolbar</div>
            </div>
          </div>
          <div class="stage"><div class="tilt"><div class="cv">
            ${winHtml({ tabs: [{ id: 'lumen', title: 'Aurora ANC Headphones — Lumen', url: 'https://lumen.example/aurora-anc', icon: FAV.lumen }], active: 'lumen', pages: `<div class="wpage" data-tab="lumen">${shopPage}</div>` })}
          </div></div></div>`,
        async play(c, el, run) {
            const cv = el.querySelector('.cv'), win = cv.querySelector('.win'), W = win._w, page = win.querySelector('.wpage');
            const cpA = el.querySelector('.cp.a'), cpB = el.querySelector('.cp.b'), K = kit();
            const rwBtn = W.$('rwb'), ghBtn = W.$('ghb');
            const S = { frame: FR.length - 1, user: false, ghost: false, busy: false };
            const N = FR.length - 1, NOW = Date.now();
            W.rec(true);
            showCopy(c, cpA, 0);
            c.anim(win, [{ opacity: 0, transform: 'translateY(40px) scale(.96)' }, { opacity: 1, transform: 'none' }], { duration: 1100, delay: 100 });

            // the real Rewind bar: range slider, label, Live (chrome.js rwRender / rwLabel)
            const rwbar = W.$('rwbar'), sl = W.$('rwsl'), lbl = W.$('rwlbl');
            const shop = page.querySelector('.shop'), img = shop.querySelector('.sh-img'), now = shop.querySelector('.now'), was = shop.querySelector('.was');
            const badge = shop.querySelector('.sh-badge'), buy = shop.querySelector('.sh-buy');
            function label(i) {
                if (!K) return;
                if (i >= N) lbl.innerHTML = `<b>Live</b> · ${N} snapshots · last ${K.ago(FR[N - 1].ago)} ago`;
                else lbl.innerHTML = `<b>${K.ago(FR[i].ago)} ago</b> · ${K.clock(NOW - FR[i].ago)} · Aurora ANC Headphones`;
                rwbar.classList.toggle('past', i < N);
            }
            function setFrame(i) {
                i = clamp(i, 0, N);
                const f = FR[i];
                S.frame = i;
                if (sl) sl.value = String(i);
                label(i);
                now.textContent = f.price; was.textContent = f.was;
                badge.className = 'sh-badge ' + f.bc; badge.textContent = f.badge;
                buy.textContent = f.buy; buy.classList.toggle('dis', !!f.dis);
                img.style.setProperty('--hue', f.hue);
            }
            if (sl) {
                sl.max = String(N);
                const user = () => { S.user = true; c.touch(); hideCallout(); };
                c.on(sl, 'input', () => { user(); setFrame(+sl.value); });
                c.on(sl, 'pointerdown', () => { run.drag = true; user(); });
                c.on(sl, 'pointerup', () => { run.drag = false; });
                c.on(W.$('rwprev'), 'click', () => { user(); setFrame(S.frame - 1); });
                c.on(W.$('rwnext'), 'click', () => { user(); setFrame(S.frame + 1); });
                c.on(W.$('rwlive'), 'click', () => { user(); setFrame(N); });
                c.on(W.$('rwx'), 'click', () => { user(); setFrame(N); W.rwOpen(false); });
                c.on(rwBtn, 'click', () => { user(); const on = !rwbar.classList.contains('on'); if (!on) setFrame(N); W.rwOpen(on); });
            }
            setFrame(N);

            const call = document.createElement('div');
            call.className = 'callout'; call.innerHTML = '€129 — 11 minutes ago<small>It went up while you browsed</small>';
            cv.appendChild(call);
            function hideCallout() { if (call._on) { call._on = false; c.anim(call, [{ opacity: 1 }, { opacity: 0 }], { duration: 250 }); } }

            await c.wait(1100);
            const me = makeCursor(cv, 'you', 'You');
            placeCursor(me, 640, 420);
            c.anim(me, [{ opacity: 0 }, { opacity: 1 }], { duration: 350 });
            await moveToEl(c, me, rwBtn, cv);
            W.hover(rwBtn); W.tip(rwBtn, rwBtn.getAttribute('data-tip') || 'Rewind', 'Ctrl+Shift+Y');
            await c.wait(500);
            await click(c, me, cv);
            W.hover(null); W.untip();
            if (!rwbar.classList.contains('on')) W.rwOpen(true);
            await c.wait(450);
            if (sl && !S.user) await moveToEl(c, me, sl, cv, .96, .5, 700);
            for (const i of [3, 2]) {
                if (S.user) break;
                if (sl) await moveToEl(c, me, sl, cv, i / N, .5, 600);
                if (S.user) break;
                setFrame(i);
                await c.wait(500);
            }
            c.anim(me, [{ opacity: 1 }, { opacity: 0 }], { duration: 400, delay: 300 });
            if (!S.user) {
                await c.wait(300);
                const p = posIn(now, cv), q = was.textContent ? posIn(was, cv) : p;
                call.style.left = (q.x + q.w + 18) + 'px'; call.style.top = (p.y + p.h / 2 - 22) + 'px';
                call._on = true;
                c.anim(call, [{ opacity: 0, transform: 'translateX(-8px)' }, { opacity: 1, transform: 'none' }], { duration: 500, easing: E_BACK });
            }
            showTry(c, cpA, 200);
            await c.wait(2800);
            while (run.drag || (c.engaged && c.idle() < 2600 && S.user)) await c.wait(200);

            // ---- beat 2: Ghost mode
            hideCallout();
            c.fork(swapCopy(c, cpA, cpB));
            setFrame(N); W.rwOpen(false);
            field.tint(VIOLET, 1400);
            const sw = cpB.querySelector('.gsw');
            async function setGhost(on) {
                if (S.busy || on === S.ghost) return;
                S.busy = true; S.ghost = on;
                sw.classList.toggle('on', on); sw.setAttribute('aria-checked', on ? 'true' : 'false');
                wl.classList.toggle('ghosty', on);
                field.tint(on ? VIOLET : EMBER, 900);
                await W.ghost(c, on);
                S.busy = false;
            }
            c.on(sw, 'click', e => { e.stopPropagation(); c.touch(); c.fork(setGhost(!S.ghost)); });
            if (ghBtn) c.on(ghBtn, 'click', e => { e.stopPropagation(); c.touch(); c.fork(setGhost(!S.ghost)); });
            await c.wait(900);
            const me2 = makeCursor(cv, 'you', 'You');
            placeCursor(me2, 520, 380);
            c.anim(me2, [{ opacity: 0 }, { opacity: 1 }], { duration: 350 });
            if (ghBtn) {
                await moveToEl(c, me2, ghBtn, cv);
                W.hover(ghBtn); W.tip(ghBtn, ghBtn.getAttribute('data-tip'), 'Ctrl+Shift+X');
                await c.wait(450);
                W.hover(null); W.untip();
                if (!S.ghost && !c.engaged) { await click(c, me2, cv); await setGhost(true); }
            }
            // in Ghost mode a new tab opens private
            const plus = W.$('newtab');
            if (plus && !c.engaged) {
                await c.wait(500);
                await moveToEl(c, me2, plus, cv);
                await click(c, me2, cv);
                W.addTab({ id: 'pv', title: 'New Tab', url: '', priv: true, ntp: true }, true);
                W.activate('pv');
            }
            c.anim(me2, [{ opacity: 1 }, { opacity: 0 }], { duration: 400, delay: 200 });
            showTry(c, cpB, 300);
            await c.wait(2800);
        },
        leave() { wl.classList.remove('ghosty'); },
    };

    // ---- 3. Flows + Site scripts
    const FLOW = {
        id: 'p-clear-inbox', name: 'clear_inbox', source: 'agent',
        description: 'Mark every GitHub notification as read, then export the {{label}} report as CSV.',
        parameters: [{ name: 'label', type: 'string', required: true, description: 'Which report to export' }],
        steps: [
            { tool: 'browser_navigate', args: { url: 'https://github.com/notifications' } },
            { tool: 'browser_click', args: { element: 'Mark all as read' } },
            { tool: 'browser_type', args: { element: '#q', text: '{{label}}' } },
            { tool: 'browser_press_key', args: { key: 'Enter' } },
            { tool: 'browser_click', args: { element: 'Export CSV' } },
        ],
    };
    const ACT = [
        ['browser_navigate', 'url=https://github.com/notifications', 412],
        ['browser_snapshot', '', 96],
        ['browser_click', 'element=button "Mark all as read"', 188],
        ['browser_type', 'element=#q  text=weekly report', 241],
        ['browser_press_key', 'key=Enter', 77],
        ['browser_click', 'element=text="Export CSV"', 205],
    ];
    const newsPage = `
      <div class="news">
        <div class="nw-top">The Daily Byte<span>Tech · Science · Culture</span></div>
        <div class="nw-body">
          <div class="nw-art"><div class="nw-kick">Analysis</div><div class="nw-h">The quiet web is back — and you can have it today</div>
            <div class="nw-l" style="width:96%"></div><div class="nw-l" style="width:88%"></div><div class="nw-l" style="width:93%"></div><div class="nw-l" style="width:72%"></div><div class="nw-l" style="width:90%"></div><div class="nw-l" style="width:64%"></div><div class="nw-l" style="width:84%"></div></div>
          <div class="nw-side"><div class="nw-card"></div><div class="nw-card"></div></div>
        </div>
        <div class="clut c-ad">MEGA SALE — 80% OFF EVERYTHING <small>AD</small></div>
        <div class="clut c-vid"><i>LIVE</i>${I.play}</div>
        <div class="c-veil"></div>
        <div class="clut c-modal"><span class="x">×</span><h6>Don’t miss a thing!</h6><p>Join 2M readers. Get our newsletter every morning.</p><div class="in"></div><div class="b">Subscribe</div></div>
        <div class="clut c-ck"><div><b>We value your privacy</b>We and our 847 partners use cookies to personalise ads.</div><div class="bs"><i>Manage</i><i>Accept all</i></div></div>
      </div>`;
    const SCRIPT_JS = "// Quiet news — runs on news.example\ndocument.querySelectorAll('.cookie, .popup, .ad, video')\n  .forEach(el => el.remove());";
    const S_FLOWS = {
        label: 'Flows & scripts', dur: 24000, tint: GOLD, level: .5, cv: WIN_CV,
        html: () => `
          <div class="copy">
            <div class="cp a">
              <div class="eyebrow">Flows</div>
              <h1 class="hl">Teach it once.<br><em>Replay in one call.</em></h1>
              <p class="sub">When an agent finishes a task, it can save the steps as a <b>flow</b>. You review and approve it — then any agent runs the whole thing as a single tool, with stable locators that survive page changes.</p>
              <div class="try"><i></i>Press Run on the flow</div>
            </div>
            <div class="cp b later">
              <div class="eyebrow">Site scripts</div>
              <h1 class="hl">Rewrite<br><em>any site.</em></h1>
              <p class="sub">Your JavaScript runs on every matching page. One-click tweaks kill cookie banners, pop-ups and autoplay — for good. Agents can draft scripts; <b>nothing runs until you approve it</b>.</p>
              <div class="try"><i></i>Pop the clutter yourself</div>
            </div>
          </div>
          <div class="stage"><div class="tilt"><div class="cv">
            <div class="beatA">
              ${winHtml({ tabs: [{ id: 'fl', title: 'Flows & activity', url: 'about:flows' }], active: 'fl', pages: '<div class="wpage" data-tab="fl"><iframe title="Flows & activity" tabindex="-1"></iframe></div>' })}
              <div class="bubble ai fb" style="right:-26px;top:118px;left:auto"><div class="who">Claude</div>Done. I saved those steps as a flow — approve it?</div>
              <div class="call"><div class="who">Any agent, later</div><span class="fn">flow_clear_inbox</span>(<span class="s">label: "weekly report"</span>)
                <div class="res2">${I.check}<span>5 steps · 1 tool call · done</span></div></div>
            </div>
            <div class="beatB" style="display:none">
              ${winHtml({
                tabs: [{ id: 'news', title: 'The Daily Byte — The quiet web is back', url: 'https://news.example/the-quiet-web', icon: FAV.news }, { id: 'pers', title: 'Personality', url: 'about:personality' }],
                active: 'news', pages: `<div class="wpage" data-tab="news">${newsPage}</div><div class="wpage" data-tab="pers"><iframe title="Personality" tabindex="-1"></iframe></div>` })}
            </div>
          </div></div></div>`,
        async play(c, el, run) {
            const cv = el.querySelector('.cv'), cpA = el.querySelector('.cp.a'), cpB = el.querySelector('.cp.b');
            const A = cv.querySelector('.beatA'), B = cv.querySelector('.beatB');
            const winA = A.querySelector('.win'), WA = winA._w, ifrA = winA.querySelector('iframe');
            const call = A.querySelector('.call'), res2 = call.querySelector('.res2');
            const pages = CFG.pages || {};

            // ---- beat 1: the real about:flows page
            const t0 = Date.now() - 5 * 60000;
            const st = {
                b: 30, flows: [], pending: [], tools: [],
                activity: ACT.map((a, i) => ({ id: 'a' + (i + 1), t: t0 + i * 2300, tool: a[0], ok: true, ms: a[2], url: 'https://github.com/notifications', a: a[1], r: 'ok' })),
            };
            let fw = null;
            const runFlow = async () => {
                res2.style.opacity = '0';
                c.anim(call, [{ opacity: 0, transform: 'translateY(10px)' }, { opacity: 1, transform: 'none' }], { duration: 500 });
                await c.wait(1300);
                c.anim(res2, [{ opacity: 0, transform: 'translateY(4px)' }, { opacity: 1, transform: 'none' }], { duration: 400 });
            };
            const onFlows = (cmd, w) => {
                const i = cmd.indexOf(':'), verb = i < 0 ? cmd : cmd.slice(0, i), arg = i < 0 ? '' : cmd.slice(i + 1);
                if (verb === 'flowapprove') {
                    const f = Object.assign({}, FLOW, { created: Math.floor(Date.now() / 1000) }); delete f.id;
                    st.pending = []; st.flows = [f];
                    pushTo(w, st);
                    pushTo(w, { b: 31, ok: true, text: 'Approved. The flow is now available to agents.', t: Date.now() });
                } else if (verb === 'flowrun') {
                    pushTo(w, { b: 31, ok: true, text: 'Running flow_clear_inbox…', t: Date.now() });
                    c.fork(runFlow().then(() => pushTo(w, { b: 31, ok: true, text: "Flow 'clear_inbox' completed (5 steps).", t: Date.now() })));
                } else if (verb === 'flowreject') { st.pending = []; pushTo(w, st); }
            };
            c.anim(winA, [{ opacity: 0, transform: 'translateY(40px) scale(.96)' }, { opacity: 1, transform: 'none' }], { duration: 1000, delay: 100 });
            showCopy(c, cpA, 0);
            if (pages.flows) fw = await demoPage(c, ifrA, pages.flows.replace('__BRAIN_FLOWS_JSON__', () => JSON.stringify(st).replace(/</g, '\\u003c')), onFlows);
            if (!c.alive) throw new Abort();
            const fd = fw && fw.document, q = s => fd && fd.querySelector(s);
            const me = makeCursor(cv, 'you', 'You');
            placeCursor(me, 560, 560);
            c.anim(me, [{ opacity: 0 }, { opacity: 1 }], { duration: 300 });
            await c.wait(700);
            // Activity: every call the agent made
            if (q('.tab[data-t="activity"]')) {
                await moveToEl(c, me, q('.tab[data-t="activity"]'), cv, .5, .6, 650);
                await click(c, me, cv); q('.tab[data-t="activity"]').click();
            }
            await c.wait(1100);
            // the agent proposes a flow from those calls
            st.pending = [Object.assign({}, FLOW, { created: Math.floor(Date.now() / 1000) - 4 })];
            if (fw) pushTo(fw, st);
            const fb = A.querySelector('.bubble.fb');
            c.anim(fb, [{ opacity: 0, transform: 'translateY(12px) scale(.9)' }, { opacity: 1, transform: 'none' }], { duration: 650, easing: E_BACK });
            c.anim(fb, [{ opacity: 1 }, { opacity: 0, transform: 'translateY(-8px)' }], { duration: 500, delay: 2600 });
            await c.wait(900);
            if (q('.tab[data-t="pending"]')) {
                await moveToEl(c, me, q('.tab[data-t="pending"]'), cv, .5, .6, 650);
                await click(c, me, cv); q('.tab[data-t="pending"]').click();
            }
            await c.wait(1200);
            const appr = q('[data-a="approve"]');
            if (appr) { await moveToEl(c, me, appr, cv, .5, .6, 700); await click(c, me, cv); if (q('[data-a="approve"]')) q('[data-a="approve"]').click(); }
            await c.wait(900);
            if (q('.tab[data-t="flows"]')) {
                await moveToEl(c, me, q('.tab[data-t="flows"]'), cv, .5, .6, 600);
                await click(c, me, cv); q('.tab[data-t="flows"]').click();
            }
            c.anim(me, [{ opacity: 1 }, { opacity: 0 }], { duration: 400, delay: 300 });
            await c.wait(600);
            await runFlow();
            showTry(c, cpA, 100);
            await c.wait(2600);
            while (c.engaged && c.idle() < 2400) await c.wait(200);

            // ---- beat 2: site scripts, written on the real Personality page
            c.fork(swapCopy(c, cpA, cpB));
            c.anim(A, [{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'scale(.96)', filter: 'blur(6px)' }], { duration: 450, easing: E_IN });
            await c.wait(420);
            A.style.display = 'none'; B.style.display = '';
            field.tint(EMBER, 1000);
            const win = B.querySelector('.win'), W = win._w, newsWrap = win.querySelector('.wpage[data-tab="news"]'), news = newsWrap.querySelector('.news');
            const ifrB = win.querySelector('.wpage[data-tab="pers"] iframe');
            c.anim(win, [{ opacity: 0, transform: 'translateY(30px) scale(.96)' }, { opacity: 1, transform: 'none' }], { duration: 900 });
            const clutter = Array.from(news.querySelectorAll('.clut')), veil = news.querySelector('.c-veil');
            const popIn2 = () => {
                clutter.forEach((k, i) => { k._gone = false; k.style.pointerEvents = ''; c.anim(k, [{ opacity: 0, transform: 'scale(.9)' }, { opacity: 1, transform: 'none' }], { duration: 500, delay: 300 + i * 160, easing: E_BACK }); });
                c.anim(veil, [{ opacity: 0 }, { opacity: 1 }], { duration: 500, delay: 600 });
            };
            const blast = k => {
                if (k._gone) return; k._gone = true;
                const cs = getComputedStyle(k);
                shatter(c, k, news, [cs.backgroundColor && cs.backgroundColor !== 'rgba(0, 0, 0, 0)' ? cs.backgroundColor : '#ffd42a', '#ff8a4c', '#ffc978', '#1d1714'], { n: 26, min: 40, max: 160 });
                c.anim(k, [{ opacity: 1, transform: 'none', filter: 'blur(0)' }, { opacity: 0, transform: 'scale(.82) rotate(-3deg)', filter: 'blur(8px)' }], { duration: 420, easing: E_IN });
                k.style.pointerEvents = 'none';
                if (k.classList.contains('c-modal')) c.anim(veil, [{ opacity: 1 }, { opacity: 0 }], { duration: 400 });
            };
            clutter.forEach(k => c.on(k, 'click', e => { e.stopPropagation(); c.touch(); blast(k); }));
            popIn2();
            showTry(c, cpB, 1400);
            // the Personality page, with the real script templates
            const items = [];
            const onPers = (cmd, w) => {
                const i = cmd.indexOf(':'), verb = i < 0 ? cmd : cmd.slice(0, i), arg = i < 0 ? '' : cmd.slice(i + 1);
                if (verb === 'usget') pushTo(w, { b: 90, items, templates: CFG.tpls || [] });
                else if (verb === 'ussave') {
                    let s = {}; try { s = JSON.parse(arg); } catch (e) { }
                    items.push({ id: 'demo' + (items.length + 1), name: s.name || 'Script', description: s.description || '', matches: s.matches || [], exclude: [], js: s.js || '', enabled: true, pending: false,
                        source: s.source === 'template' ? 'template' : 'user', template: s.template || null, runs: 0, lastRun: 0, errors: [] });
                    pushTo(w, { b: 91, ok: true });
                    pushTo(w, { b: 90, items });
                }
            };
            let pw = null;
            if (pages.personality) pw = await demoPage(c, ifrB, pages.personality.replace('__BRAIN_PERSONALITY_JSON__', () => JSON.stringify({ b: 90, items: [], templates: CFG.tpls || [] }).replace(/</g, '\\u003c')), onPers);
            if (!c.alive) throw new Abort();
            const pd = pw && pw.document, pq = s => pd && pd.querySelector(s);
            await c.wait(1500);
            const me2 = makeCursor(cv, 'you', 'You');
            placeCursor(me2, 520, 400);
            c.anim(me2, [{ opacity: 0 }, { opacity: 1 }], { duration: 300 });
            const persTab = W.tabEl('pers');
            if (persTab) { await moveToEl(c, me2, persTab, cv, .5, .55, 700); await click(c, me2, cv); }
            W.activate('pers');
            frameScroll(pw, pq('#scripts'), 12);
            await c.wait(700);
            if (pq('#new')) {
                await moveToEl(c, me2, pq('#new'), cv, .5, .6, 700); await click(c, me2, cv); pq('#new').click();
                await c.wait(500);
                await moveToEl(c, me2, pq('#f-name'), cv, .3, .5, 500);
                await typeField(c, pq('#f-name'), 'Quiet news', 18);
                await moveToEl(c, me2, pq('#f-match'), cv, .3, .4, 400);
                await typeField(c, pq('#f-match'), 'news.example', 20);
                await moveToEl(c, me2, pq('#f-js'), cv, .3, .3, 400);
                await typeField(c, pq('#f-js'), SCRIPT_JS, 55);
                await c.wait(300);
                // the editor is taller than this small window: scroll it to its buttons, as you would
                const mbox = pq('.mbox');
                if (mbox) { const top0 = mbox.scrollTop, max = mbox.scrollHeight - mbox.clientHeight; await c.tween(550, p => { mbox.scrollTop = lerp(top0, max, p); }); }
                await moveToEl(c, me2, pq('#msave'), cv, .5, .6, 600); await click(c, me2, cv); pq('#msave').click();
                await c.wait(500);
                frameScroll(pw, pq('#list'), 120);
                await c.wait(1200);
            }
            // back to the news tab: it reloads, and the script runs
            const newsTab = W.tabEl('news');
            if (newsTab) { await moveToEl(c, me2, newsTab, cv, .5, .55, 700); await click(c, me2, cv); }
            W.activate('news');
            W.busy('news', true, true);
            clutter.forEach(k => { if (k._gone) { k._gone = false; k.style.pointerEvents = ''; c.anim(k, [{ opacity: 0 }, { opacity: 1 }], { duration: 200 }); } });
            await c.wait(500);
            W.busy('news', false);
            for (const k of clutter) { blast(k); await c.wait(140); }
            W.us(['Quiet news']);
            field.wave(innerWidth * .65, innerHeight * .5);
            await c.wait(700);
            const usb = W.$('usbtn');
            if (usb) {
                await moveToEl(c, me2, usb, cv, .5, .55, 700); await click(c, me2, cv);
                W.usMenu(true);
                await c.wait(2200);
                W.usMenu(false);
            }
            c.anim(me2, [{ opacity: 1 }, { opacity: 0 }], { duration: 400, delay: 200 });
            await c.wait(1200);
        },
    };

    // ---- 4. Performance (numbers from RAM-BENCHMARK.md: 12 real sites on macOS, Chrome 157 vs BrAIn, every process counted)
    const BENCH = { launch: { chrome: 387, brain: 277, big: '−28%' }, sites: { chrome: 3697, brain: 3748, big: '≈' } };
    // A real piece of BrAIn's UI (chrome.js kit) drawn at a reduced scale inside a tile.
    function kitMini(stage, html, natW) {
        const K = kit(); if (!K) return null;
        const h = document.createElement('div');
        h.className = 'mk';
        h.style.width = natW + 'px';
        h.style.setProperty('--h', 24);
        h.style.transform = `scale(${(stage.clientWidth / natW).toFixed(4)})`;
        stage.appendChild(h);
        const r = h.attachShadow({ mode: 'open' });
        r.innerHTML = `<style>${K.css}.pbox{position:relative;top:0;left:0;margin:0;width:100%;animation:none}#pres{max-height:none}` +
            `#pwsave{position:relative;top:0;right:0;width:100%;max-width:none}</style>${html}`;
        return r;
    }
    const S_PERF = {
        label: 'Performance', dur: 13500, tint: EMBER, level: .5, cv: [760, 530],
        html: () => `
          <div class="copy"><div class="cp">
            <div class="eyebrow">Performance</div>
            <h1 class="hl"><em>Lighter at launch.</em><br>Level with Chrome under load.</h1>
            <p class="sub">Just launched, BrAIn uses <b>277 MB</b> of RAM to Chrome's <b>387 MB</b>. With 12 real sites open, both sit at about <b>3.7 GB</b>.</p>
            <p class="foot">Chrome 157 vs BrAIn on macOS, same Apple Silicon Mac and network, every process counted. Methodology in RAM-BENCHMARK.md.</p>
            <div class="try"><i></i>Switch between launch and 12 sites</div>
          </div></div>
          <div class="stage"><div class="tilt"><div class="cv">
            <div class="bench">
              <div class="big"></div>
              <div class="modes"><button class="on" data-m="launch" type="button">Launch</button><button data-m="sites" type="button">12 sites</button></div>
              <div class="brow chrome"><div class="blab"><b><i></i>Chrome 157</b><span class="val">0<small>MB</small></span></div><div class="bar"><i></i></div></div>
              <div class="brow brain"><div class="blab"><b><i></i>BrAIn</b><span class="val">0<small>MB</small></span></div><div class="bar"><i></i></div></div>
              <div class="pertab"><span>After closing 6 of the tabs: Chrome <b>3,240 MB</b></span><span>BrAIn <b>3,049 MB</b></span></div>
              <div class="minis">
                <div class="mini"><h6>Command palette</h6><p>Tabs, history, every action — ${MOD}K</p><div class="mstage mpal"></div></div>
                <div class="mini"><h6>Passwords, built in</h6><p>Saved and filled for you — never in Ghost mode</p><div class="mstage mpw"></div></div>
              </div>
            </div>
          </div></div></div>`,
        async play(c, el) {
            const cv = el.querySelector('.cv'), cp = el.querySelector('.cp'), K = kit();
            const rc = cv.querySelector('.brow.chrome'), rb = cv.querySelector('.brow.brain'), big = cv.querySelector('.big');
            const vc = rc.querySelector('.val'), vb = rb.querySelector('.val'), bc = rc.querySelector('.bar i'), bb = rb.querySelector('.bar i');
            const modes = Array.from(cv.querySelectorAll('.modes button'));
            showCopy(c, cp, 0);
            [cv.querySelector('.modes'), rc, rb].forEach((e, i) => fadeUp(c, e, 200 + i * 120, 16, 800));
            const MAX = BENCH.sites.brain;
            let cur = { chrome: 0, brain: 0 }, shown = 0, picked = false;
            const fmt = v => Math.round(v).toLocaleString('en-US') + '<small>MB</small>';
            async function show(m, dur = 1500) {
                const d = BENCH[m], from = Object.assign({}, cur), me = ++shown;
                modes.forEach(b => b.classList.toggle('on', b.getAttribute('data-m') === m));
                big.textContent = d.big;
                c.anim(big, [{ opacity: 0, transform: 'scale(.9)' }, { opacity: 1, transform: 'none' }], { duration: 900, delay: dur * .55 });
                await c.tween(dur, p => {
                    if (me !== shown) return; // a newer choice took over
                    cur.chrome = lerp(from.chrome, d.chrome, p); cur.brain = lerp(from.brain, d.brain, p);
                    vc.innerHTML = fmt(cur.chrome); vb.innerHTML = fmt(cur.brain);
                    bc.style.transform = `scaleX(${(cur.chrome / MAX).toFixed(4)})`;
                    bb.style.transform = `scaleX(${Math.max(.012, cur.brain / MAX).toFixed(4)})`;
                }, easeOutExpo);
            }
            await c.wait(600);
            await show('launch', 1500);
            // goes on to the 12 sites by itself, so nobody leaves this scene having seen only the launch numbers
            c.fork((async () => { await c.wait(1400); if (!picked) await show('sites', 1500); })());
            fadeUp(c, cv.querySelector('.pertab'), 0, 8, 600);
            c.anim(rb.querySelector('.bar'), [{ boxShadow: '0 0 0 0 rgba(255,138,76,0)' }, { boxShadow: '0 0 0 6px rgba(255,138,76,.25)' }, { boxShadow: '0 0 0 0 rgba(255,138,76,0)' }], { duration: 1200, fill: 'none' });
            modes.forEach(b => c.on(b, 'click', e => { e.stopPropagation(); c.touch(); picked = true; c.fork(show(b.getAttribute('data-m'), 900)); }));
            const minis = Array.from(cv.querySelectorAll('.mini'));
            minis.forEach((m, i) => c.anim(m, [{ opacity: 0, transform: 'translateY(18px)' }, { opacity: 1, transform: 'none' }], { duration: 700, delay: 200 + i * 130 }));
            showTry(c, cp, 900);
            // the real command palette, typing "rewind"; the real save-password card
            const pal = K && kitMini(cv.querySelector('.mpal'), `<div class="pbox"><div class="pin">${K.svg.mag}<input id="pq" readonly tabindex="-1" placeholder="Jump to a tab, search history, run an action, or type a URL"><span class="pesc">esc</span></div><div id="pres"></div></div>`, 470);
            const pws = K && kitMini(cv.querySelector('.mpw'), `<div id="pwsave">${K.pwsave('github.com', 'you@example.com')}</div>`, 330);
            const loopPal = async () => {
                const pq = pal.getElementById('pq'), pres = pal.getElementById('pres');
                for (; ;) {
                    pq.value = ''; pres.innerHTML = '';
                    await c.wait(700);
                    for (const ch of 'rewind') {
                        pq.value += ch;
                        pres.innerHTML = K.pal(pq.value);
                        await c.wait(rnd(90, 150));
                    }
                    await c.wait(2400);
                }
            };
            const loopPw = async () => {
                const card = pws.getElementById('pwsave'), sv = card.querySelector('.sv');
                for (; ;) { // the card pops in (its own animation), Save is pressed, it goes
                    card.style.display = 'flex';
                    await c.wait(2600);
                    sv.style.background = 'hsl(24 100% 78%)';
                    await c.wait(220);
                    sv.style.background = '';
                    card.style.display = 'none';
                    await c.wait(380);
                }
            };
            if (pal) c.fork(loopPal());
            if (pws) c.fork(loopPw());
            await c.wait(3200);
        },
    };

    // ---- 5. Finale
    const S_FINAL = {
        label: '', dur: 1, tint: EMBER, level: 1, center: true, hold: true,
        html: () => `
          <div class="fin">
            <div class="markwrap">${markSvg('f')}</div>
            <h1 class="hl">Ready when <em>you</em> are.</h1>
            <p class="sub">The browser your AI can drive. Press <kbd class="k">${MOD}K</kbd> anytime to find anything \u2014 or to replay this tour.</p>
            <div class="ctas"><button class="cta" type="button">Start browsing ${I.right}</button><button class="cta2" type="button">Connect your AI</button></div>
            <div class="mcp"><p>Add BrAIn to any MCP client \u2014 for example Claude Desktop\u2019s <b>claude_desktop_config.json</b>:</p>
              <pre><button class="cp2" type="button">Copy</button><code></code></pre></div>
          </div>`,
        async play(c, el) {
            const fin = el.querySelector('.fin'), svg = fin.querySelector('.mark');
            drawMark(c, svg, 0, true);
            reveal(c, fin.querySelector('.hl'), 300, 70);
            fadeUp(c, fin.querySelector('.sub'), 700, 10, 900);
            const ctas = fin.querySelector('.ctas');
            fadeUp(c, ctas, 1000, 14, 900);
            const cta = fin.querySelector('.cta'), cta2 = fin.querySelector('.cta2'), mcp = fin.querySelector('.mcp');
            const exe = CFG.exe || (CFG.mac ? '/Applications/BrAIn.app/Contents/MacOS/BrAIn' : 'C:\\path\\to\\BrAIn.exe');
            const json = `{\n  "mcpServers": {\n    "brain": {\n      "command": ${JSON.stringify(exe)}\n    }\n  }\n}`;
            mcp.querySelector('code').textContent = json;
            c.on(cta, 'click', e => { e.stopPropagation(); close('start'); });
            c.on(cta2, 'click', e => {
                e.stopPropagation(); c.touch();
                const on = !mcp.classList.contains('on');
                mcp.classList.toggle('on', on);
                if (on) c.anim(mcp, [{ opacity: 0, transform: 'translateY(-8px)' }, { opacity: 1, transform: 'none' }], { duration: 450 });
            });
            c.on(mcp.querySelector('.cp2'), 'click', e => {
                e.stopPropagation();
                const btn = e.currentTarget; let ok = false;
                try {
                    const ta = document.createElement('textarea'); ta.value = json; ta.style.cssText = 'position:fixed;opacity:0;left:-9999px';
                    shadow.appendChild(ta); ta.select(); ok = document.execCommand('copy'); ta.remove();
                } catch (er) { }
                if (!ok && navigator.clipboard) navigator.clipboard.writeText(json).then(() => { btn.textContent = 'Copied'; }, () => { });
                btn.textContent = ok ? 'Copied' : 'Select & copy';
            });
            setTimeout(() => { try { cta.focus({ preventScroll: true }); } catch (er) { } }, 1300);
            await c.wait(1600);
            pulseMark(c, svg, 0);
        },
    };

    const SCENES = [S_INTRO, S_AGENT, S_REWIND, S_FLOWS, S_PERF, S_FINAL];
    const CHAPTERS = SCENES.filter(s => s.label);
    segsEl.innerHTML = CHAPTERS.map((s, i) => `<button class="seg" type="button" data-i="${i}"><i><b></b></i><span>${s.label}</span></button>`).join('');
    const segs = Array.from(segsEl.querySelectorAll('.seg'));

    // ---------------------------------------------------------------- runner
    let cur = -1, run = null, sceneStart = 0;
    function go(i, dir = 1) {
        i = clamp(i, 0, SCENES.length - 1);
        const old = run;
        if (old && i === cur) return;
        if (old) {
            old.ctx.kill(true);
            if (old.S.leave) old.S.leave();
            const oe = old.el;
            oe.style.pointerEvents = 'none';
            const a = oe.animate([{ opacity: 1, transform: 'none', filter: 'blur(0)' },
            { opacity: 0, transform: `translateX(${dir < 0 ? 50 : -50}px) scale(.985)`, filter: 'blur(12px)' }], { duration: 520, easing: E_IO, fill: 'forwards' });
            a.onfinish = () => oe.remove();
            setTimeout(() => oe.remove(), 900);
        }
        const S = SCENES[i];
        cur = i;
        const ctx = makeCtx();
        const el = document.createElement('section');
        el.className = 'scene' + (S.center ? ' center' : '');
        el.innerHTML = S.html();
        scenesEl.appendChild(el);
        mountWins(el);
        const r = { S, ctx, el, k: 1, drag: false };
        run = r; sceneStart = clock;
        fitStage(r);
        field.tint(S.tint, 1200); field.level(REDUCE ? S.level * .6 : S.level, 1400);
        brandEl.classList.toggle('on', !S.center);
        $('.chap').classList.toggle('dim', !!S.hold);
        nxBtn.classList.remove('hot');
        if (!old) wl.focus({ preventScroll: true });
        (async () => {
            try { await S.play(ctx, el, r); }
            catch (e) { if (!(e instanceof Abort)) console.error('[welcome]', e); if (!ctx.alive) return; }
            try {
                for (; ;) {
                    const need = S.dur - (clock - sceneStart);
                    if (need > 0) { await ctx.wait(need); continue; }
                    if (S.hold) { await ctx.wait(1e9); continue; }
                    if (ctx.engaged && ctx.idle() < 3800) { nxBtn.classList.add('hot'); await ctx.wait(3800 - ctx.idle()); continue; }
                    break;
                }
            } catch (e) { return; }
            if (run === r) go(cur + 1, 1);
        })();
    }

    // per-frame: field, chapter progress, stage tilt
    frameFns.add(dt => field.frame(dt));
    frameFns.add(() => {
        const ch = run && run.S.label ? CHAPTERS.indexOf(run.S) : CHAPTERS.length;
        segs.forEach((s, i) => {
            const b = s.firstChild.firstChild;
            let v = i < ch ? 1 : i > ch ? 0 : clamp((clock - sceneStart) / run.S.dur, 0, 1);
            b.style.transform = `scaleX(${v.toFixed(4)})`;
            s.classList.toggle('on', i === ch);
        });
    });
    frameFns.add(dt => {
        if (!run || REDUCE || LITE) return; // lite: no 3D tilt — it re-renders the whole demo window offscreen on every mouse move
        const tl = run.tl || (run.tl = run.el.querySelector('.tilt'));
        if (!tl) return;
        const tx = run.drag ? 0 : ptx, ty = run.drag ? 0 : pty, k = 1 - Math.pow(.0025, dt / 1000);
        tiltX = lerp(tiltX, tx, k); tiltY = lerp(tiltY, ty, k);
        tl.style.transform = `rotateY(${(tiltX * 6).toFixed(2)}deg) rotateX(${(-tiltY * 4.5).toFixed(2)}deg)`;
    });

    function setPlaying(v) {
        playing = v;
        wl.classList.toggle('paused', !v);
        ppBtn.innerHTML = v ? I.pause : I.play;
        ppBtn.setAttribute('aria-label', v ? 'Pause' : 'Play');
        if (run) run.ctx.anims.forEach(a => { try { if (!v && a.playState === 'running') a.pause(); else if (v && a.playState === 'paused') a.play(); } catch (e) { } });
    }

    // ---------------------------------------------------------------- input
    const onKey = e => {
        if (!isOpen) return;
        if (e.metaKey && !['ArrowLeft', 'ArrowRight'].includes(e.key)) return; // let ⌘Q / ⌘W etc. through
        const k = e.key;
        let used = true;
        if (k === 'Escape') close('skip');
        else if (k === 'ArrowRight') go(cur + 1, 1);
        else if (k === 'ArrowLeft') go(cur - 1, -1);
        else if (k === ' ' || k === 'Spacebar') { const t0 = e.composedPath ? e.composedPath()[0] : e.target; if (!(t0 && t0.closest && t0.closest('button'))) setPlaying(!playing); else used = false; }
        else if (k === 'Enter' && SCENES[cur] === S_FINAL) close('start');
        else used = false;
        if (used) e.preventDefault();
        e.stopImmediatePropagation();
    };
    const onResize = () => { field.resize(); if (run) { fitStage(run); run.tl = null; } };
    wl.addEventListener('pointermove', e => {
        ptx = clamp(e.clientX / innerWidth * 2 - 1, -1, 1); pty = clamp(e.clientY / innerHeight * 2 - 1, -1, 1);
        field.pointer(e.clientX, e.clientY);
    });
    wl.addEventListener('pointerleave', () => { ptx = 0; pty = 0; field.pointer(-1e4, -1e4); });
    wl.addEventListener('pointerdown', e => {
        const t = e.composedPath()[0];
        if (t && t.closest && t.closest('button,.win,.call,.mini,.mcp,.gsw,.chap,.top')) return;
        field.wave(e.clientX, e.clientY);
        if (run) run.ctx.touch();
    });
    $('.skip').addEventListener('click', () => close('skip'));
    ppBtn.addEventListener('click', () => setPlaying(!playing));
    nxBtn.addEventListener('click', () => go(cur + 1, 1));
    segs.forEach((s, i) => s.addEventListener('click', () => { const j = SCENES.indexOf(CHAPTERS[i]); go(j, j < cur ? -1 : 1); }));

    // ---------------------------------------------------------------- open / close
    function open() {
        isOpen = true;
        window.__brainWelcomeOpen = true;
        document.documentElement.appendChild(host);
        try { window.__brainBarRecheck && window.__brainBarRecheck(); } catch (e) { }
        window.addEventListener('keydown', onKey, true);
        window.addEventListener('resize', onResize);
        field.resize();
        rafId = requestAnimationFrame(loop);
        wl.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 700, easing: 'ease-out', fill: 'both' });
        go(0);
    }
    function close(reason) {
        if (closing || !isOpen) return;
        closing = true;
        send('welcomed:' + (reason || 'skip'));
        if (run) { run.ctx.kill(true); if (run.S.leave) run.S.leave(); }
        const a = wl.animate([{ opacity: 1, transform: 'none', filter: 'blur(0)' }, { opacity: 0, transform: 'scale(1.06)', filter: 'blur(14px)' }],
            { duration: REDUCE ? 200 : 750, easing: 'cubic-bezier(.4,0,.2,1)', fill: 'forwards' });
        const finish = () => {
            if (!isOpen) return;
            isOpen = false;
            cancelAnimationFrame(rafId);
            window.removeEventListener('keydown', onKey, true);
            window.removeEventListener('resize', onResize);
            host.remove();
            window.__brainWelcomeOpen = false;
            try { window.__brainBarRecheck && window.__brainBarRecheck(); } catch (e) { }
        };
        a.onfinish = finish;
        setTimeout(finish, 1200);
    }

    window.__brainWelcome = {
        isOpen: () => isOpen,
        close: r => { if (!(CFG.test && r === 'agent')) close(r); }, // the test rig drives it over MCP: don't yield to itself
        jump: i => go(i, i < cur ? -1 : 1),
        pause: () => setPlaying(false),
        play: () => setPlaying(true),
        scene: () => cur,
    };
    // Test rig only: step the film deterministically (a hidden page gets no frames or animation time).
    if (CFG.test) window.__brainWelcome.step = async ms => {
        cancelAnimationFrame(rafId);
        for (let t = 0; t < ms; t += 16) {
            tick(16, performance.now());
            // the film, the demo toolbars / tiles inside it, and the pages running in its iframes
            const roots = [shadow, ...Array.from(shadow.querySelectorAll('.wch,.mk'), h => h.shadowRoot).filter(Boolean)];
            shadow.querySelectorAll('iframe').forEach(f => { try { if (f.contentDocument) roots.push(f.contentDocument); } catch (e) { } });
            const list = new Set();
            for (const r of roots) { try { r.getAnimations().forEach(a => list.add(a)); } catch (e) { } }
            for (const a of list) if (a.playState !== 'paused' || playing) { try { a.currentTime = (a.currentTime || 0) + 16; } catch (e) { } }
            for (let i = 0; i < 12; i++) await Promise.resolve();
        }
        return cur;
    };
    open();
})();
