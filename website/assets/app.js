/* BrAIn site — vanilla JS, no dependencies. Every demo is a simulation of the real app's behaviour. */
(() => {
'use strict';
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const sleep = ms => new Promise(r => setTimeout(r, ms));
const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
const fmt = n => n.toLocaleString('en-US');
const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

let toastT = 0;
function toast(msg) {
  const t = $('#toast'); t.textContent = msg; t.classList.add('on');
  clearTimeout(toastT); toastT = setTimeout(() => t.classList.remove('on'), 2600);
}
async function copyText(text, btn) {
  try { await navigator.clipboard.writeText(text); }
  catch { const a = document.createElement('textarea'); a.value = text; document.body.appendChild(a); a.select(); try { document.execCommand('copy'); } catch {} a.remove(); }
  if (btn) { const o = btn.textContent; btn.textContent = 'Copied'; btn.classList.add('ok'); setTimeout(() => { btn.textContent = o; btn.classList.remove('ok'); }, 1400); }
}

/* ------------------------------------------------------------------ loader */
addEventListener('load', () => setTimeout(() => document.body.classList.add('loaded'), 500));
setTimeout(() => document.body.classList.add('loaded'), 2200); // never trap the page behind the curtain

/* ------------------------------------------------------------------ page chrome */
(function chrome() {
  const nav = $('#nav'), prog = $('#progress'), links = $$('.links a[href^="#"]');
  const onScroll = () => {
    nav.classList.toggle('scrolled', scrollY > 20);
    document.body.classList.toggle('past-hero', scrollY > 80);
    const h = document.documentElement.scrollHeight - innerHeight;
    prog.style.width = (h > 0 ? scrollY / h * 100 : 0) + '%';
  };
  addEventListener('scroll', onScroll, { passive: true }); onScroll();
  $('#burger').addEventListener('click', e => {
    const open = $('#links').classList.toggle('open'); e.currentTarget.setAttribute('aria-expanded', open);
  });
  links.forEach(a => a.addEventListener('click', () => $('#links').classList.remove('open')));

  // active link
  const secs = links.map(a => $(a.getAttribute('href'))).filter(Boolean);
  const so = new IntersectionObserver(es => es.forEach(e => {
    if (e.isIntersecting) links.forEach(a => a.classList.toggle('on', a.getAttribute('href') === '#' + e.target.id));
  }), { rootMargin: '-45% 0px -50% 0px' });
  secs.forEach(s => so.observe(s));

  // reveal
  const ro = new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) { e.target.classList.add('in'); ro.unobserve(e.target); } }), { threshold: .12 });
  $$('.rv').forEach(el => ro.observe(el));

  // count-up
  const co = new IntersectionObserver(es => es.forEach(e => {
    if (!e.isIntersecting) return; co.unobserve(e.target);
    const el = e.target, to = +el.dataset.count, suf = el.dataset.suffix || '';
    if (reduce || to === 0) { el.textContent = to + suf; return; }
    const t0 = performance.now(), dur = 1400;
    (function step(now) {
      const p = clamp((now - t0) / dur, 0, 1), v = Math.round(to * (1 - Math.pow(1 - p, 4)));
      el.textContent = fmt(v) + suf; if (p < 1) requestAnimationFrame(step);
    })(t0);
  }), { threshold: .6 });
  $$('[data-count]').forEach(el => co.observe(el));

  // spotlight + tilt
  $$('.spot').forEach(el => el.addEventListener('pointermove', e => {
    const r = el.getBoundingClientRect(); el.style.setProperty('--mx', (e.clientX - r.left) + 'px'); el.style.setProperty('--my', (e.clientY - r.top) + 'px');
  }));
  const tilt = $('.tilt');
  if (tilt && !reduce && matchMedia('(hover:hover)').matches) {
    const host = tilt.parentElement;
    host.addEventListener('pointermove', e => {
      const r = host.getBoundingClientRect(), x = (e.clientX - r.left) / r.width - .5, y = (e.clientY - r.top) / r.height - .5;
      tilt.style.transform = `rotateY(${x * 7}deg) rotateX(${-y * 6}deg)`;
    });
    host.addEventListener('pointerleave', () => tilt.style.transform = '');
  }
  // magnetic primary buttons
  if (!reduce && matchMedia('(hover:hover)').matches) $$('.btn.primary').forEach(b => {
    b.addEventListener('pointermove', e => { const r = b.getBoundingClientRect(); b.style.transform = `translate(${(e.clientX - r.left - r.width / 2) * .08}px,${(e.clientY - r.top - r.height / 2) * .15}px)`; });
    b.addEventListener('pointerleave', () => b.style.transform = '');
  });
})();

/* ------------------------------------------------------------------ aurora shader */
(function aurora() {
  const cv = $('#aurora');
  const gl = cv.getContext('webgl', { antialias: false, alpha: false, powerPreference: 'low-power' });
  if (!gl) { cv.remove(); return; }
  const vs = 'attribute vec2 p;void main(){gl_Position=vec4(p,0.,1.);}';
  const fs = `precision mediump float;uniform vec2 r;uniform float t;uniform vec2 m;
float h(vec2 p){return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453);}
float n(vec2 p){vec2 i=floor(p),f=fract(p);f=f*f*(3.-2.*f);return mix(mix(h(i),h(i+vec2(1,0)),f.x),mix(h(i+vec2(0,1)),h(i+vec2(1,1)),f.x),f.y);}
float fbm(vec2 p){float v=0.,a=.5;for(int i=0;i<5;i++){v+=a*n(p);p=p*2.02+vec2(3.1,1.7);a*=.5;}return v;}
void main(){
  vec2 uv=gl_FragCoord.xy/r; vec2 p=(uv-.5)*vec2(r.x/r.y,1.);
  float a=fbm(p*1.5+vec2(t*.035,-t*.025)+m*.12);
  float b=fbm(p*2.1-vec2(t*.04,t*.02)+a*1.6);
  vec3 c1=vec3(.40,.32,1.),c2=vec3(.10,.82,.66),c3=vec3(.78,.25,.86);
  vec3 col=mix(c1,c2,smoothstep(.25,.85,b)); col=mix(col,c3,smoothstep(.45,.95,a)*.55);
  float glow=smoothstep(1.25,.0,length(p-vec2(-.55,.38)))*.9+smoothstep(1.1,.0,length(p-vec2(.62,-.34)))*.6;
  float k=(.05+.17*b)*glow+.025;
  vec3 bg=vec3(.027,.027,.051);
  gl_FragColor=vec4(bg+col*k,1.);
}`;
  const sh = (t, s) => { const o = gl.createShader(t); gl.shaderSource(o, s); gl.compileShader(o); return o; };
  const pr = gl.createProgram(); gl.attachShader(pr, sh(gl.VERTEX_SHADER, vs)); gl.attachShader(pr, sh(gl.FRAGMENT_SHADER, fs)); gl.linkProgram(pr);
  if (!gl.getProgramParameter(pr, gl.LINK_STATUS)) { cv.remove(); return; }
  gl.useProgram(pr);
  const buf = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buf);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
  const loc = gl.getAttribLocation(pr, 'p'); gl.enableVertexAttribArray(loc); gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
  const U = { r: gl.getUniformLocation(pr, 'r'), t: gl.getUniformLocation(pr, 't'), m: gl.getUniformLocation(pr, 'm') };
  let mx = 0, my = 0, tx = 0, ty = 0;
  addEventListener('pointermove', e => { tx = e.clientX / innerWidth - .5; ty = .5 - e.clientY / innerHeight; }, { passive: true });
  const size = () => { const s = .5; cv.width = Math.max(2, innerWidth * s | 0); cv.height = Math.max(2, innerHeight * s | 0); gl.viewport(0, 0, cv.width, cv.height); };
  size(); addEventListener('resize', size);
  const t0 = performance.now(); let raf = 0;
  const draw = now => {
    mx += (tx - mx) * .04; my += (ty - my) * .04;
    gl.uniform2f(U.r, cv.width, cv.height); gl.uniform1f(U.t, reduce ? 4 : (now - t0) / 1000); gl.uniform2f(U.m, mx, my);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    if (!reduce) raf = requestAnimationFrame(draw);
  };
  raf = requestAnimationFrame(draw);
  document.addEventListener('visibilitychange', () => { cancelAnimationFrame(raf); if (!document.hidden && !reduce) raf = requestAnimationFrame(draw); });
})();

/* ------------------------------------------------------------------ rotating headline word */
(async function rotator() {
  const el = $('#rot'); if (!el || reduce) return;
  const words = ['use', 'trust', 'audit', 'rewind', 'pause'];
  let i = 0;
  while (true) {
    await sleep(2600);
    for (let k = el.textContent.length; k >= 0; k--) { el.textContent = el.textContent.slice(0, k); await sleep(45); }
    i = (i + 1) % words.length;
    for (let k = 1; k <= words[i].length; k++) { el.textContent = words[i].slice(0, k); await sleep(70); }
  }
})();

/* ------------------------------------------------------------------ word-mask headline reveals */
(function masks() {
  if (reduce) return;
  const maskify = el => {
    const parts = [];
    el.childNodes.forEach(n => {
      if (n.nodeType === 3) n.textContent.split(/(\s+)/).forEach(w => { parts.push(/\S/.test(w) ? `<span class="ws"><span class="wi">${esc(w)}</span></span>` : w); });
      else if (n.nodeName === 'BR') parts.push('<br>');
      else parts.push(`<span class="ws"><span class="wi ${n.className || ''}"${n.id ? ` id="${n.id}"` : ''}>${n.innerHTML || esc(n.textContent)}</span></span>`);
    });
    el.innerHTML = parts.join('');
    $$('.wi', el).forEach((w, i) => w.style.transitionDelay = (i * .045) + 's');
  };
  const heads = $$('h1, .head h2, .txt h2'); heads.forEach(maskify);
  const io = new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); } }), { threshold: .3 });
  heads.forEach(h => io.observe(h));
})();

/* ------------------------------------------------------------------ side dot navigation */
(function dotnav() {
  const secs = [['top', 'Intro'], ['features', 'Features'], ['rewind', 'Rewind'], ['agent', 'Agent'], ['speed', 'Benchmarks'], ['download', 'Download']];
  const nav = document.createElement('nav'); nav.className = 'dotnav'; nav.setAttribute('aria-label', 'Sections');
  nav.innerHTML = secs.map(([id, l]) => `<a href="#${id}" data-l="${l}" data-s="${id}" aria-label="${l}"></a>`).join('');
  document.body.appendChild(nav);
  const dots = $$('a', nav);
  const io = new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) dots.forEach(d => d.classList.toggle('on', d.dataset.s === e.target.id)); }), { rootMargin: '-42% 0px -52% 0px' });
  secs.forEach(([id]) => { const el = document.getElementById(id); if (el) io.observe(el); });
})();

/* ------------------------------------------------------------------ manifesto: words light up on scroll */
(function manifesto() {
  const lines = $$('[data-words]'); if (!lines.length || reduce) return;
  lines.forEach(l => { l.innerHTML = l.textContent.split(/(\s+)/).map(w => /\S/.test(w) ? `<span class="w">${w}</span>` : w).join(''); });
  const all = lines.map(l => [...l.querySelectorAll('.w')]);
  const upd = () => all.forEach(ws => {
    const r = ws[0].parentElement.getBoundingClientRect();
    const n = Math.floor(clamp((innerHeight * .82 - r.top) / (innerHeight * .42), 0, 1) * ws.length);
    ws.forEach((w, i) => w.style.opacity = i < n ? 1 : '');
  });
  addEventListener('scroll', upd, { passive: true }); upd();
})();

/* ------------------------------------------------------------------ MCP tool marquee */
(function marquee() {
  const read = ['browser_snapshot', 'browser_text', 'browser_markdown', 'browser_html', 'browser_find', 'browser_describe', 'browser_listeners', 'browser_rewind', 'browser_state', 'browser_console_log', 'browser_network_requests', 'browser_cookies', 'browser_screenshot', 'browser_tabs', 'browser_wait_for', 'browser_evaluate'];
  const act = ['browser_navigate', 'browser_click', 'browser_click_at', 'browser_double_click', 'browser_hover', 'browser_type', 'browser_type_text', 'browser_press_key', 'browser_select_option', 'browser_set_checked', 'browser_scroll', 'browser_new_tab', 'browser_switch_tab', 'browser_close_tab', 'browser_upload', 'browser_ask_human', 'browser_reload', 'browser_back', 'browser_forward', 'browser_zoom', 'browser_pdf'];
  const row = (el, list, cls) => { const html = list.map(t => `<span class="tool ${cls}">${t}</span>`).join(''); el.innerHTML = html + html; };
  row($('#mq1'), act, 'act'); row($('#mq2'), read, '');
})();

/* ------------------------------------------------------------------ shared scrubber */
function Scrubber(track, fill, thumb, ticksEl, onChange) {
  const s = { n: 0, idx: -1, items: [], drag: false };
  const posOf = i => s.n === 0 ? 1 : (i < 0 ? 1 : i / s.n);
  function layout() { const p = posOf(s.idx) * 100; fill.style.width = p + '%'; thumb.style.left = p + '%'; track.setAttribute('aria-valuenow', Math.round(p)); }
  s.render = () => {
    ticksEl.innerHTML = s.items.map((it, i) => `<i class="tk${it.agent ? ' agent' : ''}${it.hit ? ' hit' : ''}" style="left:${posOf(i) * 100}%"></i>`).join('');
    layout();
  };
  s.set = (i, silent) => { s.idx = i >= s.n ? -1 : i; layout(); if (!silent) onChange(s.idx); };
  s.setItems = items => { s.items = items; s.n = items.length; s.render(); };
  const fromEvent = e => { const r = track.getBoundingClientRect(); return clamp((e.clientX - r.left) / r.width, 0, 1); };
  const pick = p => { const i = Math.round(p * s.n); s.set(i >= s.n ? -1 : i); };
  track.addEventListener('pointerdown', e => { s.drag = true; track.setPointerCapture(e.pointerId); pick(fromEvent(e)); s.onUser && s.onUser(); });
  track.addEventListener('pointermove', e => { if (s.drag) pick(fromEvent(e)); });
  const up = () => s.drag = false; track.addEventListener('pointerup', up); track.addEventListener('pointercancel', up);
  track.addEventListener('keydown', e => {
    if (!s.n) return;
    const cur = s.idx < 0 ? s.n : s.idx;
    if (e.key === 'ArrowLeft') { s.set(Math.max(0, cur - 1)); e.preventDefault(); s.onUser && s.onUser(); }
    else if (e.key === 'ArrowRight') { s.set(cur + 1 >= s.n ? -1 : cur + 1); e.preventDefault(); s.onUser && s.onUser(); }
    else if (e.key === 'End') { s.set(-1); e.preventDefault(); } else if (e.key === 'Home') { s.set(0); e.preventDefault(); }
  });
  return s;
}

/* ------------------------------------------------------------------ hero mock: agent acts, then Rewind undoes it */
(function heroMock() {
  const page = $('#heroPage'); if (!page) return;
  const price = $('#hPrice'), stock = $('#hStock'), buy = $('#hBuy'), cur = $('#hCursor'), tag = $('#hTag'), lbl = $('#hLbl'), live = $('#hLive'), agentDot = $('#hAgent'), ago = $('#hAgo');
  const states = [
    { p: '$19.00', s: '● In stock — 14 left', a: '4 min ago' },
    { p: '$24.00', s: '● Only 3 left', a: '2 min ago' },
    { p: '$31.00', s: '● Only 1 left — price rose', a: '20 s ago' },
  ];
  const map = i => i < 4 ? 0 : i < 8 ? 1 : 2;
  const N = 12; let count = 3; // snapshots recorded so far
  let userUntil = 0;
  const sc = Scrubber($('#hTrack'), $('#hFill'), $('#hThumb'), $('#hTicks'), idx => {
    const frozen = idx >= 0, st = states[frozen ? map(idx) : cur_state];
    show(st); page.classList.toggle('frozen-on', frozen);
    live.classList.toggle('off', frozen); lbl.textContent = frozen ? 'Frozen' : 'Live'; ago.textContent = st.a;
  });
  sc.onUser = () => userUntil = Date.now() + 9000;
  let cur_state = 0;
  const show = st => { price.firstChild.nodeValue = st.p; stock.textContent = st.s; };
  const items = n => Array.from({ length: n }, (_, i) => ({ agent: i === 0 || i === 4 }));
  sc.setItems(items(count));
  live.addEventListener('click', () => { sc.set(-1); sc.onUser(); });
  const moveTo = (el, dx = 0.5, dy = .5) => {
    const pr = page.getBoundingClientRect(), r = el.getBoundingClientRect();
    cur.style.transform = `translate(${r.left - pr.left + r.width * dx}px,${r.top - pr.top + r.height * dy}px)`;
  };
  const ripple = () => {
    const pr = page.getBoundingClientRect(), r = buy.getBoundingClientRect();
    const d = document.createElement('i'); d.className = 'ripple';
    d.style.left = (r.left - pr.left + r.width / 2) + 'px'; d.style.top = (r.top - pr.top + r.height / 2) + 'px';
    page.appendChild(d); setTimeout(() => d.remove(), 800);
  };
  const wait = async ms => { await sleep(ms); while (Date.now() < userUntil) await sleep(300); };
  async function loop() {
    cur.style.transform = 'translate(300px,60px)'; await sleep(400);
    while (true) {
      cur_state = 0; count = 3; sc.setItems(items(count)); sc.set(-1, true); show(states[0]); page.classList.remove('frozen-on'); lbl.textContent = 'Live'; live.classList.remove('off');
      agentDot.classList.add('pulse'); tag.textContent = 'agent · snapshot';
      await wait(1100); moveTo(buy); tag.textContent = 'agent · click e5'; await wait(1200);
      buy.classList.add('press'); ripple(); await sleep(160); buy.classList.remove('press');
      cur_state = 1; show(states[1]); price.classList.add('bump'); count = 7; sc.setItems(items(count)); sc.set(-1, true);
      await sleep(500); price.classList.remove('bump'); tag.textContent = 'agent · state';
      await wait(1500); cur_state = 2; show(states[2]); price.classList.add('bump'); count = 11; sc.setItems(items(count)); sc.set(-1, true);
      await sleep(500); price.classList.remove('bump'); agentDot.classList.remove('pulse'); tag.textContent = 'agent · done';
      await wait(1500);
      // the user scrubs back to before the price changed
      for (let i = count - 1; i >= 0; i--) { if (Date.now() < userUntil) break; sc.set(i); await sleep(95); }
      await wait(2600);
      sc.set(-1); await wait(2200);
    }
  }
  if (reduce) { sc.setItems(items(count)); show(states[2]); return; }
  const io = new IntersectionObserver(es => { if (es[0].isIntersecting) { io.disconnect(); loop(); } }); io.observe(page);
})();

/* ------------------------------------------------------------------ Rewind demo */
(function rewind() {
  const rows = $('#rwRows'); if (!rows) return;
  const feed = $('#rwFeed'), lbl = $('#rwLbl'), ago = $('#rwAgo'), count = $('#rwCount'), search = $('#rwSearch'), pause = $('#rwPause'), liveBtn = $('#rwLive');
  const syms = { 'BTC/USD': 64210, 'ETH/USD': 3420, 'SOL/USD': 148, 'NVDA': 912, 'TSLA': 187 };
  const news = ['NASDAQ: trading halt on ZZXY pending news', 'ETH upgrade scheduled for next epoch', 'Fed minutes: rates unchanged', 'Exchange outage resolved — withdrawals reopened', 'USB-C cable restock: sold out in 14 min', 'Analyst raises SOL target to $210'];
  let snaps = [], view = [], paused = false, tickN = 0, sel = -1, query = '';
  const clock = () => { const d = new Date(); return d.toTimeString().slice(0, 8); };
  const pushRow = () => {
    tickN++;
    let r;
    if (tickN % 6 === 0) r = { t: clock(), txt: news[(tickN / 6 | 0) % news.length], v: 'NEWS', k: 'n' };
    else {
      const keys = Object.keys(syms), k = keys[Math.floor(Math.random() * keys.length)], d = (Math.random() - .48) * .006;
      syms[k] *= 1 + d; r = { t: clock(), txt: k, v: syms[k].toLocaleString('en-US', { maximumFractionDigits: 2, minimumFractionDigits: 2 }), k: d >= 0 ? 'up' : 'dn' };
    }
    view = [r, ...view].slice(0, 8);
    snaps.push({ rows: view.slice(), at: Date.now(), agent: tickN % 9 === 0 });
    let shifted = false;
    if (snaps.length > 48) { snaps.shift(); shifted = true; }
    if (shifted && sel > 0) sel--;                            // keep the same snapshot selected as old ones fall off
    refreshTicks();
    if (sel < 0) render(view);
  };
  const rowHtml = (r, q) => {
    let txt = esc(r.txt); if (q) txt = txt.replace(new RegExp('(' + q.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ')', 'ig'), '<mark>$1</mark>');
    return `<div class="tick-row"><span><span class="t">${r.t}</span>${txt}</span><span class="v ${r.k}">${esc(r.v)}</span></div>`;
  };
  const render = list => { rows.innerHTML = list.map(r => rowHtml(r, query)).join(''); };
  const matches = s => query && s.rows.some(r => (r.txt + ' ' + r.v).toLowerCase().includes(query));
  const sc = Scrubber($('#rwTrack'), $('#rwFill'), $('#rwThumb'), $('#rwTicks'), idx => {
    sel = idx;
    feed.classList.toggle('frozen-on', idx >= 0);
    if (idx < 0) { render(view); lbl.textContent = 'Live'; ago.textContent = 'now'; }
    else { const s = snaps[idx], secs = Math.max(1, Math.round((Date.now() - s.at) / 1000)); render(s.rows); lbl.textContent = 'Frozen'; ago.textContent = secs + ' s ago'; }
  });
  function refreshTicks() {
    sc.setItems(snaps.map(s => ({ agent: s.agent, hit: matches(s) })));
    if (sel >= 0) sc.set(sel, true);
    count.textContent = snaps.length + ' snapshot' + (snaps.length === 1 ? '' : 's');
  }
  search.addEventListener('input', () => {
    query = search.value.trim().toLowerCase(); refreshTicks();
    if (!query) { sc.set(-1); return; }
    let hit = -1; for (let i = snaps.length - 1; i >= 0; i--) if (matches(snaps[i])) { hit = i; break; }
    if (hit >= 0) sc.set(hit); else { render([{ t: '—', txt: 'No snapshot contains “' + search.value.trim() + '”', v: '', k: '' }]); lbl.textContent = 'No match'; }
  });
  liveBtn.addEventListener('click', () => sc.set(-1));
  pause.addEventListener('click', () => { paused = !paused; pause.textContent = paused ? 'Resume feed' : 'Pause feed'; });
  // seed history so the scrubber has something to scrub straight away
  for (let i = 0; i < 14; i++) { pushRow(); snaps[snaps.length - 1].at -= (14 - i) * 1400; }
  render(view);
  setInterval(() => { if (!paused && !document.hidden) pushRow(); }, 1400);
})();

/* ------------------------------------------------------------------ agent terminal demo */
(function agent() {
  const term = $('#term'); if (!term) return;
  const bar = $('#agentbar'), st = $('#agentSt'), pauseBtn = $('#pauseBtn'), replay = $('#replayBtn');
  const script = [
    { u: 'Add the cheapest USB-C cable to my cart and tell me the total. Don’t check out.' },
    { tool: 'browser_navigate', args: '{"url":"https://shop.example/cables"}', out: 'Navigated to https://shop.example/cables\nTitle: USB-C cables — Shop', act: true, ms: 412 },
    { tool: 'browser_snapshot', args: '{}', out: '[e3] searchbox "Search"\n[e7] link "USB-C Cable 2 m — $19.00"\n[e9] link "USB-C Cable 1 m — $24.00"\n[e12] button "Add to cart" (for e7)', act: false, ms: 96 },
    { tool: 'browser_click', args: '{"element":"e12"}', out: 'Clicked e12 → role:button:Add to cart\nlocator saved: role:button:Add to cart', act: true, ms: 188 },
    { tool: 'browser_wait_for', args: '{"text":"Added to cart"}', out: 'Matched text “Added to cart” after 240 ms', act: false, ms: 251 },
    { tool: 'browser_text', args: '{"element":"css:#cart"}', out: 'Cart (1) — USB-C Cable 2 m — $19.00 — Total $19.00', act: false, ms: 71 },
    { a: 'Added the USB-C Cable 2 m ($19.00) to your cart. Cart total: $19.00. I did not check out.' },
  ];
  let paused = false, run = 0, blockedShown = false;
  const line = (cls, html) => { const d = document.createElement('div'); d.className = 'ln ' + cls; d.innerHTML = html; term.appendChild(d); term.scrollTop = term.scrollHeight; return d; };
  async function type(el, text, speed = 14) {
    for (let i = 1; i <= text.length; i++) { el.firstChild.nodeValue = text.slice(0, i); if (i % 3 === 0) term.scrollTop = term.scrollHeight; await sleep(reduce ? 0 : speed); }
  }
  function setPaused(p) {
    paused = p; bar.classList.toggle('paused', p); pauseBtn.firstChild.nodeValue = p ? 'Resume agent ' : 'Pause agent ';
    st.textContent = p ? 'Paused — action tools are blocked' : 'Agent running';
  }
  pauseBtn.addEventListener('click', () => setPaused(!paused));
  addEventListener('keydown', e => { if (e.ctrlKey && e.shiftKey && e.key === '>' || e.ctrlKey && e.shiftKey && e.code === 'Period') { if ($('#agent').getBoundingClientRect().top < innerHeight) { setPaused(!paused); e.preventDefault(); } } });
  async function play() {
    const my = ++run; term.innerHTML = ''; setPaused(false); blockedShown = false;
    await sleep(500);
    for (const s of script) {
      if (my !== run) return;
      if (s.u) { const l = line('', '<span class="p">you ›</span> <span class="u"> </span>'); await type(l.querySelector('.u'), s.u, 16); await sleep(500); continue; }
      if (s.a) { await sleep(300); const l = line('', '<span class="p">assistant ›</span> <span class="u"> </span>'); await type(l.querySelector('.u'), s.a, 12); st.textContent = 'Done'; bar.querySelector('.led').style.animation = 'none'; return; }
      line('', `<span class="tl">tool</span> <span class="ar">→</span> <b>${s.tool}</b> <span class="dim">${esc(s.args)}</span>`);
      await sleep(350);
      // action tools are refused while paused; read-only tools keep working
      while (paused && s.act) {
        if (my !== run) return;
        if (!blockedShown) { line('bl', '   BLOCKED — the user paused the agent. Wait, then retry.'); blockedShown = true; }
        await sleep(250);
      }
      if (blockedShown && !paused) { line('dim', '   resumed — retrying'); blockedShown = false; }
      await sleep(s.ms * (reduce ? 0 : 1.2));
      line('ok', '   ← ' + esc(s.out).replace(/\n/g, '\n     ') + `  <span class="dim">· ${s.ms} ms</span>`);
      await sleep(450);
    }
  }
  replay.addEventListener('click', () => { bar.querySelector('.led').style.animation = ''; play(); });
  const io = new IntersectionObserver(es => { if (es[0].isIntersecting) { io.disconnect(); play(); } }, { threshold: .35 }); io.observe(term);
})();

/* ------------------------------------------------------------------ shortcut editor demo */
(function shortcuts() {
  const list = $('#keyList'); if (!list) return;
  const warn = $('#keyWarn'), hint = $('#keyHint'), seg = $('#platSeg');
  const defs = [
    { id: 'newtab', n: 'New tab', g: 'mod+t' }, { id: 'closetab', n: 'Close tab', g: 'mod+w' },
    { id: 'reopen', n: 'Reopen closed tab', g: 'mod+shift+t' }, { id: 'pal', n: 'Command palette', g: 'mod+k' },
    { id: 'rewind', n: 'Rewind', g: 'mod+shift+y' }, { id: 'pause', n: 'Pause agent', g: 'mod+shift+.' },
  ];
  let plat = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent) ? 'mac' : 'win';
  const over = {}; let cap = null, pending = null;
  const eff = id => id in over ? over[id] : defs.find(d => d.id === id).g;
  const show = g => {
    if (!g) return ['—'];
    const p = g.split('+'), k = p.pop(), mods = new Set(p);
    const kk = k.length === 1 ? k.toUpperCase() : ({ arrowleft: '←', arrowright: '→', arrowup: '↑', arrowdown: '↓', enter: '↩', escape: 'Esc', ' ': 'Space' }[k] || k[0].toUpperCase() + k.slice(1));
    if (plat === 'mac') return [...(mods.has('ctrl') ? ['⌃'] : []), ...(mods.has('alt') ? ['⌥'] : []), ...(mods.has('shift') ? ['⇧'] : []), ...(mods.has('mod') ? ['⌘'] : []), kk];
    return [...(mods.has('mod') ? ['Ctrl'] : []), ...(mods.has('alt') ? ['Alt'] : []), ...(mods.has('shift') ? ['Shift'] : []), kk];
  };
  const label = g => show(g).join(plat === 'mac' ? '' : '+');
  function render() {
    $$('button', seg).forEach(b => b.classList.toggle('on', b.dataset.p === plat));
    list.innerHTML = defs.map(d => {
      const edited = d.id in over, g = eff(d.id);
      return `<div class="krow${edited ? ' edited' : ''}${cap === d.id ? ' cap' : ''}" data-id="${d.id}" tabindex="0" role="button" aria-label="Rebind ${d.n}">
        <span class="nm">${d.n}</span><span class="chips">${cap === d.id ? 'Press keys… (Esc cancels · ⌫ unbinds)' : show(g).map(k => `<kbd>${esc(k)}</kbd>`).join('')}</span>
        ${edited && cap !== d.id ? '<button class="rs" title="Reset" data-reset="' + d.id + '">⟲</button>' : ''}</div>`;
    }).join('');
  }
  const gestureOf = e => {
    const k = e.key; if (['Control', 'Shift', 'Alt', 'Meta'].includes(k)) return null;
    const mod = plat === 'mac' ? e.metaKey : e.ctrlKey;
    const parts = []; if (mod) parts.push('mod'); if (plat === 'mac' && e.ctrlKey) parts.push('ctrl'); if (e.altKey) parts.push('alt'); if (e.shiftKey) parts.push('shift');
    let key = k === ' ' ? 'space' : k.toLowerCase();
    if (e.code && /^Key[A-Z]$/.test(e.code)) key = e.code.slice(3).toLowerCase(); else if (e.code && /^Digit\d$/.test(e.code)) key = e.code.slice(5);
    return { g: [...parts, key].join('+'), bare: !mod && !e.altKey && !/^f\d+$/i.test(k) };
  };
  list.addEventListener('click', e => {
    const rs = e.target.closest('[data-reset]'); if (rs) { delete over[rs.dataset.reset]; cap = null; warn.textContent = ''; render(); return; }
    const row = e.target.closest('.krow'); if (!row) return; cap = row.dataset.id; pending = null; warn.textContent = ''; render(); row.focus();
  });
  list.addEventListener('keydown', e => {
    if (!cap) { if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('krow')) { cap = e.target.dataset.id; render(); $(`.krow[data-id="${cap}"]`).focus(); e.preventDefault(); } return; }
    e.preventDefault(); e.stopPropagation();
    if (e.key === 'Escape') { cap = null; pending = null; warn.textContent = ''; render(); return; }
    if (e.key === 'Backspace' || e.key === 'Delete') { over[cap] = ''; cap = null; warn.textContent = ''; render(); return; }
    const r = gestureOf(e); if (!r) return;
    if (r.bare) { warn.textContent = 'Add ' + (plat === 'mac' ? '⌘' : 'Ctrl') + ' or Alt so it doesn’t clash with typing.'; return; }
    const other = defs.find(d => d.id !== cap && eff(d.id) === r.g);
    if (other && pending !== r.g) { pending = r.g; warn.textContent = `${label(r.g)} is already “${other.n}”. Press it again to take it.`; return; }
    if (other) over[other.id] = '';
    over[cap] = r.g; cap = null; pending = null; warn.textContent = ''; render();
  });
  list.addEventListener('focusout', e => { if (cap && !list.contains(e.relatedTarget)) { cap = null; warn.textContent = ''; render(); } });
  $('#keyResetAll').addEventListener('click', () => { Object.keys(over).forEach(k => delete over[k]); cap = null; warn.textContent = ''; render(); toast('All shortcuts reset'); });
  seg.addEventListener('click', e => { const b = e.target.closest('button'); if (b) { plat = b.dataset.p; render(); } });
  render();
})();

/* ------------------------------------------------------------------ benchmarks */
(function bench() {
  const sites = ['example.com', 'news.ycombinator.com', 'en.wikipedia.org', 'stackoverflow.com', 'developer.mozilla.org', 'github.com', 'bing.com', 'arstechnica.com', 'apple.com', 'cloudflare.com', 'reddit.com', 'amazon.com', 'youtube.com', 'bbc.com', 'cnn.com', 'nytimes.com', 'twitch.tv', 'medium.com', 'forbes.com', 'weather.com', 'ebay.com', 'imdb.com', 'wordpress.com', 'office.com', 'x.com'];
  const chromeRam = [1601, 1812, 2026, 2176, 2521, 3377, 5678, 6977, 7700, 8114, 8660, 8817, 9583, 11873, 11712, 11738, 12167, 12355, 12670, 13075, 13600, 14078, 14150, 14079, 14102];
  const brainRam = [176, 185, 191, 181, 199, 193, 199, 186, 198, 182, 192, 194, 197, 195, 170, 189, 167, 156, 163, 163, 145, 145, 181, 181, 174];
  const load = [[369, 399], [1459, 1017], [1120, 1346], [928, 135], [937, 921], [4469, 1452], [1572, 1951], [4804, null], [2072, 2948], [3857, 2206], [3000, 3382], [1830, 5070], [1902, 1726], [null, 5146], [null, null], [7120, 2580], [2721, 2925], [1836, 2969], [11767, 8505], [null, 6941], [2751, 3430], [2046, 2667], [3583, 3268], [1603, 2467], [2236, 2614]];

  // bars
  const bars = $('#bars');
  if (bars) new IntersectionObserver((es, o) => { if (!es[0].isIntersecting) return; o.disconnect();
    $$('.bar-fill', bars).forEach(b => b.style.width = b.dataset.w + '%');
    $$('[data-to]', bars).forEach(el => { const to = +el.dataset.to, t0 = performance.now(); (function s(now) { const p = clamp((now - t0) / 1600, 0, 1); el.textContent = fmt(Math.round(to * (1 - Math.pow(1 - p, 3)))) + ' MB'; if (p < 1) requestAnimationFrame(s); })(t0); });
    const r = $('[data-ratio]', bars), to = +r.dataset.ratio, t0 = performance.now(); (function s(now) { const p = clamp((now - t0) / 1600, 0, 1); r.textContent = Math.round(to * (1 - Math.pow(1 - p, 3))) + '×'; if (p < 1) requestAnimationFrame(s); })(t0);
  }, { threshold: .4 }).observe(bars);

  // line chart
  const svg = $('#chart'); if (!svg) return;
  const W = 700, H = 300, L = 48, R = 12, T = 12, B = 30, max = 15000;
  const x = i => L + (W - L - R) * i / 24, y = v => T + (H - T - B) * (1 - v / max);
  const path = a => a.map((v, i) => (i ? 'L' : 'M') + x(i).toFixed(1) + ' ' + y(v).toFixed(1)).join('');
  let g = '';
  [0, 5000, 10000, 15000].forEach(v => { g += `<line x1="${L}" x2="${W - R}" y1="${y(v)}" y2="${y(v)}" stroke="rgba(255,255,255,.08)"/><text x="${L - 8}" y="${y(v) + 4}" text-anchor="end" fill="#6f6f8c" font-size="11">${v ? v / 1000 + ' GB' : '0'}</text>`; });
  [1, 5, 10, 15, 20, 25].forEach(n => g += `<text x="${x(n - 1)}" y="${H - 8}" text-anchor="middle" fill="#6f6f8c" font-size="11">${n}</text>`);
  g += `<text x="${(L + W) / 2}" y="${H + 8}" text-anchor="middle" fill="#6f6f8c" font-size="11">tabs open</text>`;
  g += `<defs><linearGradient id="lg" x1="0" x2="1"><stop offset="0" stop-color="#8f7cff"/><stop offset="1" stop-color="#6fe0c6"/></linearGradient><linearGradient id="ar" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#a5a5bd" stop-opacity=".25"/><stop offset="1" stop-color="#a5a5bd" stop-opacity="0"/></linearGradient></defs>`;
  g += `<path d="${path(chromeRam)}L${x(24)} ${y(0)}L${x(0)} ${y(0)}Z" fill="url(#ar)" id="cArea" opacity="0"/>`;
  g += `<path id="cLine" d="${path(chromeRam)}" fill="none" stroke="#a5a5bd" stroke-width="2.5" stroke-linejoin="round" stroke-linecap="round"/>`;
  g += `<path id="bLine" d="${path(brainRam)}" fill="none" stroke="url(#lg)" stroke-width="3" stroke-linejoin="round" stroke-linecap="round"/>`;
  g += `<g id="hov" opacity="0"><line id="hl" y1="${T}" y2="${H - B}" stroke="rgba(255,255,255,.25)"/><circle id="hc" r="5" fill="#a5a5bd"/><circle id="hb" r="5" fill="#6fe0c6"/></g><rect id="hit" x="${L}" y="${T}" width="${W - L - R}" height="${H - T - B}" fill="transparent"/>`;
  svg.innerHTML = g;
  const draw = id => { const p = $(id, svg), len = p.getTotalLength(); p.style.strokeDasharray = len; p.style.strokeDashoffset = len; p.getBoundingClientRect(); p.style.transition = 'stroke-dashoffset 2.2s cubic-bezier(.16,1,.3,1)'; requestAnimationFrame(() => p.style.strokeDashoffset = 0); };
  new IntersectionObserver((es, o) => { if (!es[0].isIntersecting) return; o.disconnect(); draw('#cLine'); draw('#bLine'); setTimeout(() => { $('#cArea').style.transition = 'opacity 1s'; $('#cArea').setAttribute('opacity', 1); }, 900); }, { threshold: .35 }).observe(svg);
  const tip = $('#tip'), hov = $('#hov', svg), card = $('#chartCard');
  $('#hit', svg).addEventListener('pointermove', e => {
    const r = svg.getBoundingClientRect(), px = (e.clientX - r.left) / r.width * W, i = clamp(Math.round((px - L) / (W - L - R) * 24), 0, 24);
    hov.setAttribute('opacity', 1); $('#hl', svg).setAttribute('x1', x(i)); $('#hl', svg).setAttribute('x2', x(i));
    $('#hc', svg).setAttribute('cx', x(i)); $('#hc', svg).setAttribute('cy', y(chromeRam[i])); $('#hb', svg).setAttribute('cx', x(i)); $('#hb', svg).setAttribute('cy', y(brainRam[i]));
    tip.innerHTML = `<b>${i + 1} tabs</b> · ${sites[i]}<br><span style="color:#c9c9de">Chrome</span> <b>${fmt(chromeRam[i])} MB</b><br><span style="color:#8fe6d2">BrAIn</span> <b>${fmt(brainRam[i])} MB</b>`;
    const cr = card.getBoundingClientRect(); let tx = e.clientX - cr.left + 16; if (tx > cr.width - 190) tx = e.clientX - cr.left - 190;
    tip.style.left = tx + 'px'; tip.style.top = (e.clientY - cr.top - 20) + 'px'; tip.style.opacity = 1;
  });
  $('#hit', svg).addEventListener('pointerleave', () => { hov.setAttribute('opacity', 0); tip.style.opacity = 0; });

  // table
  const tb = $('#loadTable tbody'); let sortK = 'site', dir = 1;
  const rowsData = sites.map((s, i) => ({ site: s, c: load[i][0], b: load[i][1], d: load[i][0] && load[i][1] ? (load[i][1] - load[i][0]) / load[i][0] : null }));
  const renderT = () => {
    const rs = [...rowsData].sort((a, b) => { const va = a[sortK], vb = b[sortK]; if (va == null) return 1; if (vb == null) return -1; return (va > vb ? 1 : va < vb ? -1 : 0) * dir; });
    tb.innerHTML = rs.map(r => {
      const cw = r.c != null && (r.b == null || r.c < r.b), bw = r.b != null && (r.c == null || r.b < r.c);
      const cell = (v, w) => v == null ? '<td class="na">timed out</td>' : `<td class="${w ? 'w' : ''}">${fmt(v)}</td>`;
      const d = r.d == null ? '<td class="na">—</td>' : `<td class="${r.d < 0 ? 'w' : ''}">${r.d > 0 ? '+' : ''}${Math.round(r.d * 100)}%</td>`;
      return `<tr><td>${r.site}</td>${cell(r.c, cw)}${cell(r.b, bw)}${d}</tr>`;
    }).join('');
  };
  $$('#loadTable th').forEach(th => th.addEventListener('click', () => { const k = th.dataset.k; dir = sortK === k ? -dir : 1; sortK = k; renderT(); }));
  renderT();
})();

/* ------------------------------------------------------------------ connect snippets */
(function connect() {
  const code = $('#cfgCode'); if (!code) return;
  const tabs = $('#cfgTabs');
  const K = s => `<span class="n">"${s}"</span>`, S = s => `<span class="s">"${esc(s)}"</span>`;
  const cfg = {
    mac: { raw: '{\n  "mcpServers": {\n    "brain": {\n      "command": "/Applications/BrAIn.app/Contents/MacOS/BrAIn"\n    }\n  }\n}', },
    win: { raw: '{\n  "mcpServers": {\n    "brain": {\n      "command": "C:\\\\Program Files\\\\BrAIn\\\\BrAIn.exe"\n    }\n  }\n}' },
    args: { raw: '{\n  "mcpServers": {\n    "brain": {\n      "command": "/Applications/BrAIn.app/Contents/MacOS/BrAIn",\n      "args": ["https://example.com"]\n    }\n  }\n}' },
  };
  const colour = raw => esc(raw).replace(/(&quot;)([^&]*?)(&quot;)(\s*:)/g, '<span class="n">"$2"</span>$4').replace(/:\s*(&quot;)([^&]*?)(&quot;)/g, ': <span class="s">"$2"</span>').replace(/\[(&quot;)([^&]*?)(&quot;)\]/g, '[<span class="s">"$2"</span>]');
  let cur = /Windows/.test(navigator.userAgent) ? 'win' : 'mac';
  const set = c => { cur = c; $$('button', tabs).forEach(b => b.classList.toggle('on', b.dataset.c === c)); code.innerHTML = colour(cfg[c].raw); };
  tabs.addEventListener('click', e => { const b = e.target.closest('button'); if (b) set(b.dataset.c); });
  $('#cfgCopy').addEventListener('click', e => copyText(cfg[cur].raw, e.currentTarget));
  set(cur);
})();

/* ------------------------------------------------------------------ download (live from the latest GitHub release) */
(async function downloads() {
  const grid = $('#dlGrid'); if (!grid) return;
  const REPO = 'GranTurismo/BrAIn', LATEST = `https://github.com/${REPO}/releases/latest`, CACHE_KEY = 'brain.release', CACHE_MS = 10 * 60 * 1000;

  // GitHub's unauthenticated API allows 60 requests/hour/IP, so cache for a few minutes and fall back to a stale copy.
  const readCache = () => { try { return JSON.parse(sessionStorage.getItem(CACHE_KEY)); } catch { return null; } };
  async function latestRelease() {
    const cached = readCache();
    if (cached && Date.now() - cached.t < CACHE_MS) return cached.data;
    try {
      const res = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { headers: { Accept: 'application/vnd.github+json' } });
      if (!res.ok) throw new Error('HTTP ' + res.status);
      const data = await res.json();
      try { sessionStorage.setItem(CACHE_KEY, JSON.stringify({ t: Date.now(), data })); } catch {}
      return data;
    } catch { return cached ? cached.data : null; }
  }

  // Map release assets to platform cards by file name, so a new release needs no site change.
  const KINDS = [
    { re: /\.dmg$/i, os: 'mac', name: 'macOS', label: a => /x64|intel/i.test(a) ? 'macOS · Intel' : 'macOS · Apple Silicon' },
    { re: /\.exe$/i, os: 'win', name: 'Windows', label: () => 'Windows 10/11 · x64' },
    { re: /\.tar\.gz$/i, os: 'linux', name: 'Linux', label: () => 'Linux · x64 (experimental)' },
  ];
  const parse = rel => (rel.assets || []).map(a => {
    const k = KINDS.find(k => k.re.test(a.name)); if (!k) return null;
    return { os: k.os, name: k.name, label: k.label(a.name), file: a.name, bytes: a.size, url: a.browser_download_url, sha256: (a.digest || '').replace(/^sha256:/, '') };
  }).filter(Boolean);

  const rel = await latestRelease(), items = rel ? parse(rel) : [];
  if (!items.length) {
    grid.innerHTML = `<div class="card dl" style="grid-column:1/-1">Couldn’t load the latest release right now. <a href="${LATEST}">Get BrAIn from GitHub Releases →</a></div>`;
    $$('[data-dl-cta], #heroDl').forEach(a => a.setAttribute('href', LATEST));
    return;
  }
  const version = String(rel.tag_name || '').replace(/^v/, '');
  $$('[data-version]').forEach(el => el.textContent = 'v' + version);

  const ua = navigator.userAgent;
  const plat = /Windows/.test(ua) ? 'win' : /Android/.test(ua) ? '' : /Linux/.test(ua) ? 'linux' : /Mac/.test(ua) && !/iPhone|iPad/.test(ua) ? 'mac' : '';
  const mb = b => (b / 1048576).toFixed(1) + ' MB';
  const ICON = {
    mac: '<svg viewBox="0 0 24 24"><path d="M16.4 1.4c0 1.1-.5 2.2-1.2 3-.8.9-2.1 1.5-3.2 1.4-.1-1.1.4-2.3 1.1-3 .8-.9 2.2-1.5 3.3-1.4zM20.5 17.1c-.6 1.3-.8 1.8-1.5 2.9-1 1.5-2.4 3.5-4.2 3.5-1.5 0-2-1-4.1-1s-2.5 1-4.1 1c-1.7 0-3.1-1.7-4.1-3.3C-.2 15.9-.5 10.7 1.2 8.1 2.4 6.2 4.3 5.1 6.1 5.1c1.8 0 2.9 1 4.4 1 1.5 0 2.3-1 4.4-1 1.6 0 3.3.9 4.5 2.4-4 2.2-3.3 7.8.1 9.6z"/></svg>',
    win: '<svg viewBox="0 0 24 24"><path d="M3 5.5l7.4-1v7H3zM11.6 4.35L21 3v8.5h-9.4zM3 12.5h7.4v7L3 18.5zM11.6 12.5H21V21l-9.4-1.3z"/></svg>',
    linux: '<svg viewBox="0 0 24 24"><path d="M12 2C9.2 2 8 4.4 8 7.2c0 1.5-.4 2.4-1.2 3.6C5.6 12.600 5 14.300 5 16c0 1.100.3 1.900.9 2.500-.4.700-1.200 1.100-1.900 1.400-.5.200-.4.800.1.900 1.300.300 2.800.100 3.900-.6.900.4 2 .6 4 .6s3.100-.2 4-.6c1.100.7 2.600.9 3.900.6.500-.1.600-.7.100-.9-.7-.3-1.500-.7-1.900-1.400.6-.6.900-1.400.9-2.500 0-1.700-.6-3.400-1.800-5.200C16.400 9.600 16 8.700 16 7.200 16 4.400 14.800 2 12 2zm-1.500 5.300c.5 0 .9.500.9 1.100s-.4 1.100-.9 1.100-.9-.5-.9-1.100.4-1.100.9-1.100zm3 0c.5 0 .9.500.9 1.100s-.4 1.100-.9 1.100-.9-.5-.9-1.100.4-1.100.9-1.100z"/></svg>',
  };
  const STEPS = {
    mac: ['Open the DMG and drag <b>BrAIn</b> to Applications.', 'First launch: right-click the app → <b>Open</b> (it isn’t notarized yet) — or run <code>xattr -dr com.apple.quarantine /Applications/BrAIn.app</code>.', 'Press <kbd>⌘</kbd> <kbd>/</kbd> for every shortcut; point your MCP client at the binary.'],
    win: ['Run the installer. If SmartScreen appears: <b>More info → Run anyway</b> (not code-signed yet).', 'It installs the WebView2 runtime if your PC lacks it, adds Start Menu entries, and includes a <b>tab self-test</b> shortcut.', 'Point your MCP client at <code>C:\\Program Files\\BrAIn\\BrAIn.exe</code>.'],
    linux: ['Extract the archive: <code>tar xzf BrAIn-*-linux-x64.tar.gz</code> (needs GTK3 and WebKitGTK).', 'Experimental: it opens pages and navigates; in-page automation (MCP tools) isn’t available on Linux yet.'],
  };
  const VERIFY = { mac: 'shasum -a 256 ~/Downloads/', win: 'Get-FileHash -Algorithm SHA256 $HOME\\Downloads\\', linux: 'sha256sum ~/Downloads/' };
  const rank = d => (d.os === plat ? -10 : 0) + ['mac', 'win', 'linux'].indexOf(d.os);
  const order = items.sort((a, b) => rank(a) - rank(b));
  grid.innerHTML = order.map(d => {
    const ext = (d.file.match(/\.(tar\.gz|[^.]+)$/) || [, ''])[1], rec = d.os === plat;
    return `<article class="card dl rv spot${rec ? ' rec' : ''}" data-os="${d.os}">
      <div class="os"><div class="ico">${ICON[d.os]}</div><div><h3>${d.name}</h3><div class="sub2">${esc(d.label)}</div></div><span class="badge">YOUR PC</span></div>
      <div class="facts"><div><b>${esc(version)}</b><span>version</span></div><div><b>${mb(d.bytes)}</b><span>download</span></div><div><b>.${esc(ext)}</b><span>format</span></div></div>
      <a class="btn primary" href="${esc(d.url)}" data-dl>
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v11m0 0l-4.5-4.5M12 15l4.5-4.5M5 20h14"/></svg>Download for ${d.name}</a>
      ${d.sha256 ? `<div class="hash"><span>SHA-256</span><code title="${d.sha256}">${d.sha256}</code><button data-copy="${d.sha256}">Copy</button></div>` : ''}
      <ol class="steps">${STEPS[d.os].map(s => `<li><span>${s}</span></li>`).join('')}</ol>
      ${d.sha256 ? `<details><summary style="cursor:pointer;color:var(--mut);font-size:13.5px">Verify from the command line</summary><div class="code" style="margin-top:10px;font-size:12px">${VERIFY[d.os]}${esc(d.file)}\n<span class="c"># expected</span>\n${d.sha256}</div></details>` : ''}
    </article>`;
  }).join('');
  $$('.rv', grid).forEach(el => setTimeout(() => el.classList.add('in'), 50));
  $$('.spot', grid).forEach(el => el.addEventListener('pointermove', e => { const r = el.getBoundingClientRect(); el.style.setProperty('--mx', (e.clientX - r.left) + 'px'); el.style.setProperty('--my', (e.clientY - r.top) + 'px'); }));

  // hero button reflects the visitor's OS
  const pick = items.find(d => d.os === plat);
  if (pick) $('#heroDlLabel').textContent = 'Download for ' + pick.name;

  grid.addEventListener('click', e => {
    const c = e.target.closest('[data-copy]'); if (c) { copyText(c.dataset.copy, c); return; }
    if (e.target.closest('[data-dl]')) toast('Download started — verify the checksum when it finishes');
  });
})();
})();
