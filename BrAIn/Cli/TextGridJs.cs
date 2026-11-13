namespace BrAIn.Cli;

/// <summary>
/// The terminal viewer's text layer (<c>brain view</c> in a terminal without pictures): where every visible character of the
/// page — and of BrAIn's toolbar — sits, so the viewer can draw it as a real, sharp terminal character instead of a blur of
/// coloured blocks. Called as <c>Source + "(K,M,OX,OY,CSS)"</c>:
///
///   K, M    the size of one terminal cell in page pixels
///   OX, OY  where the view sits in the window (the page is under the toolbar)
///   CSS     1 = lay the page out on a character grid: every font monospace with one character exactly K px wide and every
///           line exactly M px tall (what the browsh project does) — then text lands on whole cells and nothing collides;
///           0 = take that layout away again; -1 = leave the page alone (BrAIn's own toolbar)
///
/// Returns JSON { runs: [[row, col, text, style, maxCells, isField, group, groupEndCol]…], styles: [[r,g,b,flags]…], caret: [row,col]|null,
/// occ: [[col0,row0,col1,row1]…] (toolbar only: its opaque popups) }
/// (flags: 1 bold, 2 italic, 4 underline/link, 8 dim/placeholder). Text covered by something else (a banner, a menu) is
/// left out; password fields show bullets. It only reads, apart from the grid stylesheet.
/// </summary>
internal static class TextGridJs
{
    internal const string Source = """
(function (K, M, OX, OY, CSS) {
  'use strict';
  var d = document, W = innerWidth, H = innerHeight;
  var out = { runs: [], styles: [], caret: null, grid: false, focus: false };
  try { out.focus = d.hasFocus(); } catch (e) { }
  var MONO = '"DejaVu Sans Mono","Liberation Mono","Noto Sans Mono","Ubuntu Mono",monospace';
  try {
    var st = d.getElementById('__brain_grid');
    if (CSS === 1) {
      // the monospace font's advance at 100px, measured once per document (an inline !important beats our own rule)
      if (!window.__brainGridR) {
        var sp = d.createElement('span');
        sp.textContent = 'MMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMM';
        sp.style.setProperty('font-family', MONO, 'important');
        sp.style.setProperty('font-size', '100px', 'important');
        sp.style.setProperty('letter-spacing', '0', 'important');
        sp.style.setProperty('position', 'absolute', 'important');
        sp.style.setProperty('visibility', 'hidden', 'important');
        sp.style.setProperty('white-space', 'pre', 'important');
        (d.body || d.documentElement).appendChild(sp);
        window.__brainGridR = sp.getBoundingClientRect().width / 50 / 100 || 0.6;
        sp.remove();
      }
      var F = (K / window.__brainGridR).toFixed(3);
      var sel = '*:not(#__brain_g1):not(#__brain_g2)';
      var css = sel + ',' + sel + '::before,' + sel + '::after{font-family:' + MONO + ' !important;font-size:' + F +
        'px !important;line-height:' + M + 'px !important;letter-spacing:0 !important;word-spacing:0 !important}';
      if (!st) { st = d.createElement('style'); st.id = '__brain_grid'; (d.head || d.documentElement).appendChild(st); }
      if (st.textContent !== css) st.textContent = css;
      out.grid = true;
    } else if (CSS === 0 && st) { st.remove(); }
  } catch (e) { }

  var styles = {};
  function styleOf(el, extra) {
    var cs = getComputedStyle(el);
    if (cs.visibility === 'hidden' || cs.visibility === 'collapse' || +cs.opacity < 0.1) return -1;
    var m = /rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?/.exec(cs.color);
    if (!m || (m[4] !== undefined && +m[4] < 0.15)) return -1;
    var fl = extra || 0;
    if ((parseInt(cs.fontWeight, 10) || 400) >= 600) fl |= 1;
    if (cs.fontStyle === 'italic') fl |= 2;
    if ((el.closest && el.closest('a[href]')) || /underline/.test(cs.textDecorationLine || '')) fl |= 4;
    if (el.closest && el.closest('h1,h2,h3')) fl |= 1;
    var key = m[1] + ',' + m[2] + ',' + m[3] + ',' + fl;
    if (!(key in styles)) { styles[key] = out.styles.length; out.styles.push([m[1] | 0, m[2] | 0, m[3] | 0, fl]); }
    return styles[key];
  }

  // Text is shown at a point when the topmost element there is its own element or one inside it. When the topmost one is
  // an ANCESTOR, the text is clipped away there (collapsed menus, "visually hidden" screen-reader text, skip links); when it
  // is something unrelated, something covers it (a banner, a popup). Elements that ignore the mouse never come back from
  // elementFromPoint, so for those an ancestor is fine.
  function covered(root, el, x, y) {
    if (x < 0 || y < 0 || x >= W || y >= H) return true;
    var hit = root.elementFromPoint ? root.elementFromPoint(x, y) : null;
    if (!hit || hit === el || el.contains(hit)) return false;
    if (hit.contains(el)) return getComputedStyle(el).pointerEvents !== 'none';
    return true;
  }

  // The box an element's content can be seen in: the viewport, cut down by every ancestor that clips (overflow other than
  // visible, `clip`, `clip-path`). A "visually hidden" element (screen-reader text, skip links: a 1px clipped box) shows
  // nothing at all. A fixed element escapes its ancestors' clipping.
  var VIEW = { l: 0, t: 0, r: W, b: H }, NONE = { l: 0, t: 0, r: -1, b: -1 }, clips = new Map();
  function clipOf(el) {
    if (!el || el === d.documentElement) return VIEW;
    var c = clips.get(el);
    if (c) return c;
    var cs = getComputedStyle(el);
    var parent = el.parentElement || (el.parentNode && el.parentNode.host) || null;
    c = cs.position === 'fixed' ? VIEW : clipOf(parent);
    var clipProp = cs.clip && cs.clip !== 'auto', clipPath = cs.clipPath && cs.clipPath !== 'none';
    if (c !== NONE && (cs.overflowX !== 'visible' || cs.overflowY !== 'visible' || clipProp || clipPath)) {
      var r = el.getBoundingClientRect();
      if (r.width <= 2 || r.height <= 2 || (clipPath && /inset\(\s*50%/.test(cs.clipPath))) c = NONE;
      else c = { l: Math.max(c.l, r.left), t: Math.max(c.t, r.top), r: Math.min(c.r, r.right), b: Math.min(c.b, r.bottom) };
    }
    clips.set(el, c);
    return c;
  }

  // runs inside the same clipping box form a group: the viewer lays a group's runs out one after another up to the box's
  // right edge (proportional text — the toolbar — has more letters than its pixel width has cells)
  var groups = new Map();
  function groupOf(c) { var g = groups.get(c); if (g === undefined) { g = groups.size; groups.set(c, g); } return g; }

  var rng = d.createRange(), budget = 40000;

  function addText(node, root) {
    var data = node.data, el = node.parentElement;
    if (!el || !/\S/.test(data)) return;
    rng.selectNodeContents(node);
    var rs = rng.getClientRects(), seen = false;
    for (var q = 0; q < rs.length; q++) { var rr = rs[q]; if (rr.bottom > 0 && rr.top < H && rr.right > 0 && rr.left < W) { seen = true; break; } }
    if (!seen) return;
    var s = styleOf(el); if (s < 0) return;
    var line = null, lines = [];
    for (var i = 0; i < data.length && budget > 0; i++) {
      var c = data.charCodeAt(i), n = (c >= 0xD800 && c <= 0xDBFF && i + 1 < data.length) ? 2 : 1;
      rng.setStart(node, i); rng.setEnd(node, i + n);
      var b = rng.getBoundingClientRect(), str = data.substr(i, n);
      i += n - 1; budget--;
      if (b.width < 0.5 || b.height < 0.5 || b.bottom < 0 || b.top > H) continue;   // collapsed space, off screen
      var cp = str.codePointAt(0);
      if (cp < 32 || (cp >= 0xE000 && cp <= 0xF8FF) || cp >= 0xF0000) str = ' ';     // icon-font glyphs have no text
      var cy = b.top + b.height / 2;
      if (!line || Math.abs(cy - line.cy) > M * 0.5 || b.left < line.r - 1) { line = { cy: cy, l: b.left, r: b.right, t: '' }; lines.push(line); }
      line.t += str; line.r = b.right;
    }
    var clip = clipOf(el);
    for (var k = 0; k < lines.length; k++) {
      var L = lines[k], t = L.t.replace(/\s+$/, '');
      if (!t || L.cy < 0 || L.cy >= H || L.r < 0 || L.l >= W) continue;
      // its first letter must be inside the visible box; letters past its right edge are cut off
      var fx = Math.min(L.r, L.l + K / 2);
      if (fx < clip.l || fx > clip.r || L.cy < clip.t || L.cy > clip.b) continue;
      if (covered(root, el, Math.min(W - 1, Math.max(0, L.l + 2)), L.cy)) continue;
      var right = Math.min(L.r, clip.r);
      out.runs.push([Math.floor((L.cy + OY) / M), Math.round((L.l + OX) / K), t, s, Math.max(1, Math.round((right - L.l) / K)), 0,
        groupOf(clip), Math.floor((Math.min(clip.r, W) + OX) / K)]);
    }
  }

  var active = d.activeElement;
  while (active && active.shadowRoot && active.shadowRoot.activeElement) active = active.shadowRoot.activeElement;

  function addField(el, root) {
    var tag = el.tagName, type = (el.type || '').toLowerCase();
    if (tag === 'INPUT' && /^(checkbox|radio|range|color|file|hidden|image)$/.test(type)) return;
    var r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2 || r.bottom < 0 || r.top > H || r.right < 0 || r.left > W) return;
    var cs = getComputedStyle(el);
    var val = tag === 'SELECT' ? (el.selectedIndex >= 0 && el.options[el.selectedIndex] ? el.options[el.selectedIndex].text : '') : String(el.value || '');
    var ph = false;
    if (!val && el.placeholder) { val = el.placeholder; ph = true; }
    if (type === 'password' && !ph) val = val.replace(/./g, '•');
    var s = styleOf(el, ph ? 8 : 0); if (s < 0) return;
    var pl = (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.borderLeftWidth) || 0);
    var pr = (parseFloat(cs.paddingRight) || 0) + (parseFloat(cs.borderRightWidth) || 0);
    var fc = clipOf(el), fy = r.top + r.height / 2;
    if (r.left + pl + 1 < fc.l || r.left + pl + 1 > fc.r || fy < fc.t || fy > fc.b) return;
    var max = Math.max(1, Math.floor((r.width - pl - pr) / K)), col = Math.round((r.left + pl + OX) / K);
    if (covered(root, el, Math.min(W - 1, Math.max(0, r.left + pl + 1)), Math.min(H - 1, Math.max(0, r.top + r.height / 2)))) return;
    if (tag === 'TEXTAREA') {
      var top = r.top + (parseFloat(cs.paddingTop) || 0) + (parseFloat(cs.borderTopWidth) || 0) - el.scrollTop;
      var lh = parseFloat(cs.lineHeight) || M, y = top, pos = 0, caretDone = false;
      var parts = val.split('\n');
      for (var p = 0; p < parts.length; p++) {
        var rest = parts[p];
        do {
          var piece = rest.substr(0, max);
          if (y + lh / 2 > r.top && y + lh / 2 < r.bottom && y + lh / 2 < H) {
            var row = Math.floor((y + lh / 2 + OY) / M);
            if (piece) out.runs.push([row, col, piece, s, max, 1]);
            if (el === active && !ph && !caretDone && el.selectionStart >= pos && el.selectionStart <= pos + piece.length) {
              out.caret = [row, col + el.selectionStart - pos]; caretDone = true;
            }
          }
          pos += piece.length; rest = rest.substr(piece.length); y += lh;
        } while (rest.length);
        pos += 1; // the newline
      }
    } else {
      var start = 0;
      if (val.length > max && el === active && typeof el.selectionEnd === 'number' && el.selectionEnd >= max) start = el.selectionEnd - max + 1;
      var row1 = Math.floor((r.top + r.height / 2 + OY) / M);
      out.runs.push([row1, col, val.substr(start, max), s, max, 1]);
      if (el === active && !ph && typeof el.selectionStart === 'number') out.caret = [row1, col + Math.max(0, Math.min(max, el.selectionStart - start))];
      else if (el === active && ph) out.caret = [row1, col];
    }
  }

  function walk(root) {
    var tw = d.createTreeWalker(root, 5, { acceptNode: function (n) {
      if (n.nodeType === 1 && /^(SCRIPT|STYLE|NOSCRIPT|TEMPLATE|HEAD|IFRAME|OBJECT|TITLE)$/.test(String(n.tagName).toUpperCase())) return 2;
      return 1;
    } });
    var n;
    while ((n = tw.nextNode()) && budget > 0) {
      if (n.nodeType === 3) { addText(n, root); continue; }
      var t = n.tagName;
      if (t === 'INPUT' || t === 'TEXTAREA' || t === 'SELECT') addField(n, root);
      if (n.shadowRoot) walk(n.shadowRoot);
    }
  }
  try { walk(d.documentElement || d); } catch (e) { out.error = String(e); }

  // BrAIn's toolbar view (CSS -1): the boxes of its opaque popups (menus, suggestions, the palette), in cells. The page's
  // text is hidden only under those; the rest of a grown toolbar view shows the page through it.
  if (CSS === -1) {
    out.occ = [];
    var occ = function (root) {
      var all = root.querySelectorAll('*');
      for (var i = 0; i < all.length && out.occ.length < 40; i++) {
        var e = all[i];
        if (e.shadowRoot) occ(e.shadowRoot);
        if (e.id === '__brain_bd') continue;
        var cs = getComputedStyle(e);
        if ((cs.position !== 'fixed' && cs.position !== 'absolute') || cs.display === 'none' || cs.visibility !== 'visible' || +cs.opacity < 0.5) continue;
        var bg = /rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?/.exec(cs.backgroundColor);
        if (!bg || (bg[4] !== undefined && +bg[4] < 0.6)) continue;
        var r = e.getBoundingClientRect();
        if (r.width * r.height < 3000 || r.bottom <= 0 || r.top >= H) continue;
        out.occ.push([Math.floor((r.left + OX) / K), Math.floor((r.top + OY) / M), Math.ceil((r.right + OX) / K), Math.ceil((r.bottom + OY) / M)]);
      }
    };
    try { occ(d); } catch (e) { }
  }

  // a caret in an editable element that is not a form field (rich editors)
  try {
    if (!out.caret && active && active.isContentEditable) {
      var sel = getSelection();
      if (sel && sel.rangeCount) {
        var cr = sel.getRangeAt(0).getClientRects()[0] || sel.getRangeAt(0).getBoundingClientRect();
        if (cr && (cr.height > 0)) out.caret = [Math.floor((cr.top + cr.height / 2 + OY) / M), Math.round((cr.left + OX) / K)];
      }
    }
  } catch (e) { }
  return JSON.stringify(out);
})
""";
}
