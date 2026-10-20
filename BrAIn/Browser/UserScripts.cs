using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrAIn.Browser;

/// <summary>
/// A user script: JS that BrAIn runs automatically on every page whose address
/// matches one of <see cref="Matches"/>. Written by the user, installed from a
/// template, or drafted by an AI agent (agent drafts wait for the user's OK).
/// </summary>
public sealed class UserScript
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>URL patterns: "*", "example.com" (and subdomains), "*.example.com/path/*", "https://x.com/*".</summary>
    public string[] Matches { get; set; } = ["*"];
    /// <summary>Hosts (and their subdomains) where the script is paused.</summary>
    public string[] Exclude { get; set; } = [];
    public string Js { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Drafted by an agent and not yet approved — never runs while pending.</summary>
    public bool Pending { get; set; }
    /// <summary>"user", "agent" or "template".</summary>
    public string Source { get; set; } = "user";
    public string? Template { get; set; }
    public long Created { get; set; }
    public long Updated { get; set; }
}

public sealed record ScriptTemplate(string Id, string Name, string Description, string Icon, string[] Matches, string Js);

public sealed class UserScriptStore
{
    public const int MaxJs = 200_000;
    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _file;
    private readonly object _lock = new();
    private List<UserScript> _items = [];
    private readonly Dictionary<string, Regex[]> _rx = new();

    /// <summary>Raised after any add, change or removal.</summary>
    public event Action? Changed;

    public UserScriptStore(string storeDir)
    {
        _file = Path.Combine(storeDir, "userscripts.json");
        try
        {
            if (File.Exists(_file))
                _items = JsonSerializer.Deserialize<List<UserScript>>(File.ReadAllText(_file), FileJson) ?? [];
        }
        catch { _items = []; }
    }

    public List<UserScript> All()
    {
        lock (_lock) return _items.Select(Clone).ToList();
    }

    public UserScript? Get(string id)
    {
        lock (_lock) return _items.Where(s => s.Id == id).Select(Clone).FirstOrDefault();
    }

    public bool AnyRunnable()
    {
        lock (_lock) return _items.Any(s => s.Enabled && !s.Pending);
    }

    /// <summary>Scripts that should run on this address, in creation order.</summary>
    public List<UserScript> ForUrl(string url)
        => Matching(url).Where(m => !m.Paused).Select(m => m.Script).ToList();

    /// <summary>Every enabled, approved script that matches the address, with whether the
    /// user paused it for this site.</summary>
    public List<(UserScript Script, bool Paused)> Matching(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return [];
        lock (_lock)
            return _items.Where(s => s.Enabled && !s.Pending && Matches(s, url, uri))
                .Select(s => (Clone(s), IsPaused(s, uri.Host))).ToList();
    }

    /// <summary>Does the address match the script's patterns (ignoring on/off and pausing)?</summary>
    public bool UrlMatches(UserScript s, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        lock (_lock) return Matches(s, url, uri);
    }

    private static string NormHost(string h)
    {
        h = (h ?? "").Trim().ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }

    private static bool IsPaused(UserScript s, string host)
    {
        var h = NormHost(host);
        return (s.Exclude ?? []).Any(x => h == x || h.EndsWith("." + x, StringComparison.Ordinal));
    }

    /// <summary>Pause or resume one script on one site.</summary>
    public bool SetHostPaused(string id, string host, bool paused)
    {
        host = NormHost(host);
        if (host.Length is 0 or > 100) return false;
        lock (_lock)
        {
            var s = _items.FirstOrDefault(x => x.Id == id);
            if (s is null) return false;
            var list = (s.Exclude ?? []).ToList();
            if (paused) { if (!list.Contains(host)) list.Add(host); }
            else list.RemoveAll(x => x == host || host.EndsWith("." + x, StringComparison.Ordinal));
            if (list.Count > 100) return false;
            s.Exclude = list.ToArray();
            Persist();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Creates or replaces a script. Returns an error message, or null on success.</summary>
    public string? Save(UserScript s, out UserScript saved)
    {
        saved = s;
        var err = Validate(s);
        if (err is not null) return err;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var cur = string.IsNullOrEmpty(s.Id) ? null : _items.FirstOrDefault(x => x.Id == s.Id);
            if (cur is null)
            {
                s.Id = Guid.NewGuid().ToString("N")[..8];
                s.Created = now;
                s.Updated = now;
                _items.Add(s);
            }
            else
            {
                s.Created = cur.Created;
                s.Updated = now;
                _items[_items.IndexOf(cur)] = s;
            }
            _rx.Remove(s.Id);
            saved = Clone(s);
            Persist();
        }
        Changed?.Invoke();
        return null;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (_items.RemoveAll(s => s.Id == id) == 0) return false;
            _rx.Remove(id);
            Persist();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Turning a script on is the user's approval: it clears Pending.</summary>
    public bool SetEnabled(string id, bool on)
    {
        lock (_lock)
        {
            var s = _items.FirstOrDefault(x => x.Id == id);
            if (s is null) return false;
            s.Enabled = on;
            if (on) s.Pending = false;
            s.Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Persist();
        }
        Changed?.Invoke();
        return true;
    }

    private static string? Validate(UserScript s)
    {
        s.Name = (s.Name ?? "").Trim();
        if (s.Name.Length is < 1 or > 80) return "name must be 1-80 characters";
        s.Description = (s.Description ?? "").Trim();
        if (s.Description.Length > 300) s.Description = s.Description[..300];
        if (string.IsNullOrWhiteSpace(s.Js)) return "script is empty";
        if (s.Js.Length > MaxJs) return $"script is too large (max {MaxJs} characters)";
        s.Matches = (s.Matches ?? []).Select(m => (m ?? "").Trim()).Where(m => m.Length > 0).Distinct().ToArray();
        if (s.Matches.Length == 0) return "needs at least one URL pattern (use * for every site)";
        if (s.Matches.Length > 30) return "too many URL patterns (max 30)";
        if (s.Matches.Any(m => m.Length > 200)) return "a URL pattern is too long";
        s.Exclude = (s.Exclude ?? []).Select(NormHost).Where(h => h.Length is > 0 and <= 100).Distinct().Take(100).ToArray();
        if (s.Source is not ("user" or "agent" or "template")) s.Source = "user";
        return null;
    }

    private static UserScript Clone(UserScript s) => new()
    {
        Id = s.Id, Name = s.Name, Description = s.Description, Matches = (string[])s.Matches.Clone(),
        Exclude = (string[])(s.Exclude ?? []).Clone(), Js = s.Js,
        Enabled = s.Enabled, Pending = s.Pending, Source = s.Source, Template = s.Template,
        Created = s.Created, Updated = s.Updated,
    };

    private void Persist()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, FileJson));
            File.Move(tmp, _file, true);
        }
        catch { /* read-only profile: keep working in memory */ }
    }

    // ---------------------------------------------------------------- matching

    private bool Matches(UserScript s, string url, Uri uri)
    {
        Regex[] rxs;
        if (!_rx.TryGetValue(s.Id, out rxs!))
            _rx[s.Id] = rxs = s.Matches.Select(Compile).ToArray();
        return rxs.Any(r => r.IsMatch(url));
    }

    /// <summary>Pattern → regex over the full URL. A bare host also covers its subdomains.</summary>
    internal static Regex Compile(string pattern)
    {
        var p = pattern.Trim();
        const RegexOptions o = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (p == "*" || p.Equals("<all_urls>", StringComparison.OrdinalIgnoreCase))
            return new Regex(@"^https?://", o);
        string host = p, path = "";
        var scheme = @"https?";
        var si = p.IndexOf("://", StringComparison.Ordinal);
        if (si >= 0)
        {
            var sch = p[..si];
            scheme = sch == "*" ? "https?" : Regex.Escape(sch);
            host = p[(si + 3)..];
        }
        var slash = host.IndexOf('/');
        if (slash >= 0) { path = host[slash..]; host = host[..slash]; }
        if (host.StartsWith("*.")) host = host[2..];
        var hostRx = Glob(host, "[^/]*");
        var pathRx = path.Length == 0 ? "(?:[/?#].*)?" : Glob(path, ".*");
        // port is optional, subdomains are always allowed
        return new Regex($"^{scheme}://(?:[^/@]*\\.)?{hostRx}(?::\\d+)?{pathRx}$", o);
    }

    private static string Glob(string g, string any)
    {
        var sb = new StringBuilder();
        foreach (var ch in g)
            sb.Append(ch == '*' ? any : Regex.Escape(ch.ToString()));
        return sb.ToString();
    }

    // ---------------------------------------------------------------- injection

    /// <summary>
    /// The program evaluated in the page. Runs once per document (guarded by id; <paramref name="force"/>
    /// re-runs it), waits for the DOM if the page is still parsing, and isolates errors so a broken
    /// script can't take the page's own code down. Errors — including ones thrown later from
    /// timers, animation frames and MutationObserver callbacks the script registers — are
    /// reported to the host and kept in <c>window.__brainUsLog</c>.
    /// </summary>
    public static string Wrap(UserScript s, bool force = false)
    {
        var id = JsonSerializer.Serialize(s.Id);
        var label = JsonSerializer.Serialize(s.Name);
        return
            "(function(){var ID=" + id + ",NAME=" + label + ",K=window.__brainUs||(window.__brainUs={});" +
            (force ? "" : "if(K[ID])return;") + "K[ID]=1;" +
            "var L=window.__brainUsLog||(window.__brainUsLog=[]);" +
            "function say(v){try{window.external&&window.external.sendMessage('brain:'+v)}catch(_){}}" +
            "function fail(e){var m=String(e&&e.stack||e).slice(0,400);L.push({i:ID,m:m,t:Date.now()});if(L.length>50)L.shift();" +
            "say('usrerr:'+ID+'|'+m);try{console.error('[BrAIn script '+NAME+']',e)}catch(_){}}" +
            "function guard(f){return typeof f==='function'?function(){try{return f.apply(this,arguments)}catch(e){fail(e)}}:f}" +
            "function run(){say('usran:'+ID);" +
            "var setTimeout=function(f,t){return window.setTimeout.apply(window,[guard(f)].concat([].slice.call(arguments,1)))};" +
            "var setInterval=function(f,t){return window.setInterval.apply(window,[guard(f)].concat([].slice.call(arguments,1)))};" +
            "var requestAnimationFrame=function(f){return window.requestAnimationFrame(guard(f))};" +
            "var MutationObserver=function(f){return new window.MutationObserver(guard(f))};" +
            "try{\n" + s.Js + "\n}catch(e){fail(e)}}" +
            "if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',run,{once:true});else run();})();";
    }

    // ---------------------------------------------------------------- templates

    public static readonly ScriptTemplate[] Templates =
    [
        new("cookies", "Auto-reject cookie banners",
            "Clicks \"Reject all\" / \"Necessary only\" on cookie and consent pop-ups. Knows OneTrust, Cookiebot, Didomi, Usercentrics and more, plus a text-based fallback in many languages.",
            "shield", ["*"], TplCookies),
        new("popups", "Remove newsletter pop-ups",
            "Hides \"subscribe to our newsletter\" overlays and gives you back page scrolling.",
            "mail", ["*"], TplPopups),
        new("unstick", "Un-stick fixed bars",
            "Turns floating headers, footers and chat bars into normal page content so they stop covering the text.",
            "pin", ["*"], TplUnstick),
        new("noautoplay", "Stop autoplaying video",
            "Pauses videos and audio that start playing on their own. Click play when you want them.",
            "pause", ["*"], TplAutoplay),
        new("unlock", "Unlock copy & right-click",
            "Re-enables text selection, copy and the context menu on pages that block them.",
            "unlock", ["*"], TplUnlock),
        new("darkmode", "Dark mode for any site",
            "Inverts the page colors while keeping images and video natural.",
            "moon", ["*"], TplDark),
        new("youtube", "YouTube cleanup",
            "Hides Shorts shelves and menu entries, and clicks \"Skip\" (with a real click) the moment a skippable ad offers it.",
            "play", ["youtube.com"], TplYoutube),
    ];

    private const string TplCookies = """
// Auto-reject cookie banners
var REJECT = /^(reject all|reject|reject non-essential|decline all|decline|deny all|deny|refuse all|refuse|only necessary|necessary only|only essential|essential only|use necessary cookies only|continue without accepting|disagree|no, thanks|no thanks|ablehnen|alle ablehnen|nur notwendige|refuser|tout refuser|continuer sans accepter|rechazar|rechazar todo|rifiuta|rifiuta tutto|rejeitar|recusar|weigeren|alles weigeren|odmítnout|odrzuć wszystko|отклонить|отклонить все|უარყოფა)$/i;
var SELECTORS = [
  '#onetrust-reject-all-handler', '#CybotCookiebotDialogBodyButtonDecline',
  '#CybotCookiebotDialogBodyLevelButtonLevelOptinDeclineAll', '#didomi-notice-disagree-button',
  '[data-testid="uc-deny-all-button"]', '.fc-cta-do-not-consent', '#sp-cc-rejectall-link',
  '#truste-consent-required', '.osano-cm-denyAll', '.cn-decline', '.cky-btn-reject', '.cmplz-deny',
  '._brlbs-refuse-btn', 'button[data-cookiefirst-action="reject"]', '.cc-deny', '#cookie-consent-decline'
];
var done = false;
function visible(el) {
  var r = el.getBoundingClientRect();
  return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden';
}
function press(el) { try { el.click(); done = true; } catch (e) { } return done; }
function scan() {
  if (done) return;
  for (var i = 0; i < SELECTORS.length; i++) {
    var el = document.querySelector(SELECTORS[i]);
    if (el && visible(el) && press(el)) return;
  }
  var roots = document.querySelectorAll('[id*="cookie" i],[class*="cookie" i],[id*="consent" i],[class*="consent" i],[id*="gdpr" i],[class*="gdpr" i],[id*="privacy" i],[role="dialog"],[aria-modal="true"]');
  for (var r = 0; r < roots.length; r++) {
    var btns = roots[r].querySelectorAll('button,a,[role="button"],input[type="button"],input[type="submit"]');
    for (var b = 0; b < btns.length; b++) {
      var t = (btns[b].innerText || btns[b].value || btns[b].getAttribute('aria-label') || '').trim();
      if (t.length && t.length < 40 && REJECT.test(t) && visible(btns[b]) && press(btns[b])) return;
    }
  }
}
scan();
var timer, mo = new MutationObserver(function () { clearTimeout(timer); timer = setTimeout(scan, 150); });
mo.observe(document.documentElement, { childList: true, subtree: true });
setTimeout(function () { mo.disconnect(); }, 20000);
""";

    private const string TplPopups = """
// Remove newsletter / subscribe pop-ups
var WORDS = /subscribe|newsletter|sign up|signup|join (our|the)|get (our|the) (latest|updates)|your email/i;
function scan() {
  var cands = document.querySelectorAll('[class*="modal" i],[class*="popup" i],[class*="overlay" i],[class*="newsletter" i],[class*="subscribe" i],[id*="modal" i],[id*="popup" i],[id*="newsletter" i],[role="dialog"],[aria-modal="true"]');
  var hid = false;
  for (var i = 0; i < cands.length; i++) {
    var el = cands[i];
    if (el.dataset.brainHidden) continue;
    var pos = getComputedStyle(el).position;
    if (pos !== 'fixed' && pos !== 'absolute' && pos !== 'sticky') continue;
    if (el.querySelector('input[type="password"]')) continue;       // login dialogs stay
    if (!el.querySelector('input[type="email"],input[name*="email" i]')) continue;
    if (!WORDS.test((el.innerText || '').slice(0, 600))) continue;
    el.dataset.brainHidden = '1';
    el.style.setProperty('display', 'none', 'important');
    hid = true;
  }
  if (hid) {
    [document.documentElement, document.body].forEach(function (n) {
      if (!n) return;
      n.style.setProperty('overflow', 'auto', 'important');
      n.style.setProperty('position', 'static', 'important');
    });
    document.querySelectorAll('[class*="backdrop" i],[class*="overlay" i]').forEach(function (b) {
      if (getComputedStyle(b).position === 'fixed' && !b.innerText.trim()) b.style.setProperty('display', 'none', 'important');
    });
  }
}
scan();
var timer, mo = new MutationObserver(function () { clearTimeout(timer); timer = setTimeout(scan, 300); });
mo.observe(document.documentElement, { childList: true, subtree: true });
setTimeout(function () { mo.disconnect(); }, 30000);
""";

    private const string TplUnstick = """
// Un-stick fixed bars (headers, footers, floating widgets)
function unstick() {
  var all = document.body ? document.body.querySelectorAll('*') : [];
  for (var i = 0; i < all.length; i++) {
    var el = all[i];
    if (el.dataset.brainUnstuck) continue;
    var cs = getComputedStyle(el);
    if (cs.position !== 'fixed' && cs.position !== 'sticky') continue;
    var r = el.getBoundingClientRect();
    if (r.height > innerHeight * 0.5 || r.width < 80) continue;      // full-screen layers are not "bars"
    el.dataset.brainUnstuck = '1';
    el.style.setProperty('position', 'static', 'important');
  }
}
unstick();
var timer, mo = new MutationObserver(function () { clearTimeout(timer); timer = setTimeout(unstick, 400); });
if (document.body) mo.observe(document.body, { childList: true, subtree: true });
setTimeout(function () { mo.disconnect(); }, 20000);
""";

    private const string TplAutoplay = """
// Stop autoplaying video and audio
function quiet(m) {
  if (m.dataset.brainQuiet) return;
  m.dataset.brainQuiet = '1';
  m.autoplay = false; m.removeAttribute('autoplay');
  if (!m.paused) m.pause();
  // let the user's own click start it
  m.addEventListener('play', function (e) { if (!navigator.userActivation || !navigator.userActivation.hasBeenActive) m.pause(); }, true);
}
function sweep() { document.querySelectorAll('video,audio').forEach(quiet); }
sweep();
var mo = new MutationObserver(sweep);
mo.observe(document.documentElement, { childList: true, subtree: true });
setTimeout(function () { mo.disconnect(); }, 15000);
""";

    private const string TplUnlock = """
// Unlock copy, selection and right-click
var st = document.createElement('style');
st.textContent = '*{-webkit-user-select:text!important;user-select:text!important;-webkit-touch-callout:default!important}';
(document.head || document.documentElement).appendChild(st);
['contextmenu', 'copy', 'cut', 'paste', 'selectstart', 'dragstart'].forEach(function (ev) {
  window.addEventListener(ev, function (e) { e.stopImmediatePropagation(); }, true);
  document.addEventListener(ev, function (e) { e.stopImmediatePropagation(); }, true);
});
[document, document.documentElement, document.body].forEach(function (n) {
  if (!n) return;
  n.oncontextmenu = null; n.onselectstart = null; n.oncopy = null; n.oncut = null; n.ondragstart = null;
});
""";

    private const string TplDark = """
// Dark mode for any site (invert, then re-invert media)
if (!document.getElementById('brain-dark')) {
  var st = document.createElement('style');
  st.id = 'brain-dark';
  st.textContent =
    'html{filter:invert(1) hue-rotate(180deg)!important;background:#fff!important}' +
    'img,picture,video,canvas,iframe,svg image,[style*="background-image"]{filter:invert(1) hue-rotate(180deg)!important}';
  (document.head || document.documentElement).appendChild(st);
}
""";

    private const string TplYoutube = """
// YouTube cleanup: hide Shorts, click "Skip" on skippable ads
var st = document.createElement('style');
st.textContent =
  'ytd-rich-shelf-renderer[is-shorts],ytd-reel-shelf-renderer,ytd-rich-section-renderer:has(ytd-rich-shelf-renderer[is-shorts]),' +
  'ytd-guide-entry-renderer:has(a[title="Shorts"]),ytd-mini-guide-entry-renderer[aria-label="Shorts"],' +
  'ytd-video-renderer:has(a[href^="/shorts/"]),ytd-grid-video-renderer:has(a[href^="/shorts/"]){display:none!important}';
(document.head || document.documentElement).appendChild(st);
function skipAd() {
  var b = document.querySelector('.ytp-skip-ad-button,.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-ad-skip-button-container button');
  // YouTube ignores script-made clicks on Skip, so ask BrAIn for a real input click
  if (b && b.getClientRects().length) say('ytskip');
}
// React the moment the button is added/shown (throttled to one check per frame),
// with a fast poll as the fallback for attribute-only changes and background tabs.
var queued = false;
new MutationObserver(function () {
  if (queued) return;
  queued = true;
  requestAnimationFrame(function () { queued = false; skipAd(); });
}).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'hidden'] });
setInterval(skipAd, 250);
""";
}
