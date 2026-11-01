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
        UpgradeTemplateCopies();
    }

    /// <summary>Earlier versions of a template that are safe to replace, by the id of the template: the
    /// fingerprint of their source (see <see cref="Fingerprint"/>) and the descriptions they shipped with.
    /// A copy the user edited no longer matches, so it is never touched.</summary>
    private static readonly Dictionary<string, (string[] Sources, string[] Descriptions)> Superseded = new()
    {
        ["cookies"] = (["b01d6794aa534b2c"],
            ["Clicks \"Reject all\" / \"Necessary only\" on cookie and consent pop-ups. Knows OneTrust, Cookiebot, Didomi, Usercentrics and more, plus a text-based fallback in many languages."]),
    };

    /// <summary>First 16 hex characters of the SHA-256 of the source with line endings and trailing space ignored.</summary>
    internal static string Fingerprint(string js)
    {
        var norm = (js ?? "").Replace("\r\n", "\n").TrimEnd();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(norm)))[..16].ToLowerInvariant();
    }

    /// <summary>A template that got rebuilt reaches the copies people installed earlier — but only the copies
    /// that are still exactly an old version of it. Name, sites, on/off and approval stay as they were.</summary>
    private void UpgradeTemplateCopies()
    {
        var changed = false;
        foreach (var s in _items)
        {
            if (s.Source != "template" || s.Template is null || !Superseded.TryGetValue(s.Template, out var old)) continue;
            var t = Templates.FirstOrDefault(x => x.Id == s.Template);
            if (t is null) continue;
            var fp = Fingerprint(s.Js);
            if (fp == Fingerprint(t.Js) || !old.Sources.Contains(fp)) continue;
            s.Js = t.Js;
            if (old.Descriptions.Contains(s.Description)) s.Description = t.Description;
            s.Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            changed = true;
        }
        if (changed) Persist();
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
            "Says no to cookie and consent pop-ups for you: presses \"Reject all\" / \"Necessary only\", or opens \"Settings\", switches every optional category off and saves. Knows OneTrust, Cookiebot, Didomi, Usercentrics, Quantcast and more, reads many languages, and never presses \"Accept\".",
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
        new("captions", "Grab video captions",
            "Spots the subtitle files a video player loads (WebVTT, SRT, TTML, YouTube, HLS) and keeps the text. A small CC chip copies it; an agent reads window.__brainCaptions.",
            "captions", ["*"], TplCaptions),
    ];

    private const string TplCookies = """
// Auto-reject cookie banners
// Finds a cookie / consent pop-up and presses its "reject" button. A pop-up that only offers "settings" is
// opened, every optional category is switched off, and the choice is saved. An "accept" button is never
// pressed, and nothing outside something that looks like a consent pop-up (it must talk about cookies) is
// touched. Sees through shadow DOM and same-site iframes; knows the big consent tools plus the wording used
// in many languages. A pop-up the visitor opened themselves (it appeared right after a real click or key press)
// is left alone. What it did is listed in window.__brainCookies.
var LOG = window.__brainCookies = { clicks: [], state: 'looking' };
var DEADLINE = Date.now() + 30000, MAX_CLICKS = 8, lastClick = 0, settingsOpened = 0, rejectedAt = 0;
var timer = 0, tick = 0, mo = null, seen = new WeakMap(), born = new WeakMap(), lastInput = 0;

// ---------------------------------------------------------------- words (lowercase)
function lc(t) { return String(t == null ? '' : t).replace(/[←-⇿»›✕×▶]+/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase(); }
function has(t, list) { for (var i = 0; i < list.length; i++) if (t.indexOf(list[i]) >= 0) return true; return false; }

var REJ = ['reject', 'decline', 'deny', 'refuse', 'disagree', 'do not accept', "don't accept", 'don’t accept', 'do not agree', "don't agree",
  'no thanks', 'no, thanks', 'opt out', 'opt-out', 'without accepting', 'without agreeing', 'without consent', 'i do not consent',
  'ablehnen', 'nicht zustimmen', 'widerspreche', 'verweigern', 'refuser', 'je refuse', 'continuer sans', 'rechazar', 'denegar', 'no acepto',
  'continuar sin aceptar', 'rifiuta', 'rifiuto', 'non accetto', 'continua senza', 'rejeitar', 'recusar', 'não aceito', 'continuar sem aceitar',
  'weigeren', 'afwijzen', 'niet akkoord', 'doorgaan zonder', 'odrzuć', 'nie zgadzam', 'bez akceptacji', 'odmítnout', 'nesouhlasím', 'odmietnuť',
  'elutasít', 'respinge', 'refuză', 'avvisa', 'avvis', 'afvis', 'hylkää', 'reddet', 'отклонить', 'отказаться',
  'не принимаю', 'не согласен', 'без согласия', 'відхилити', 'відмовитися',
  'უარყოფ', 'უარი', 'არ ვეთანხმები', 'არ ვეთანხმებით',
  'απόρριψη', 'απορρίπτω', '拒否', '拒绝', '拒絕', '거부', 'رفض'];
var ONLY_RX = [/only\s+(the\s+)?(strictly\s+|absolutely\s+)?(necessary|essential|required|mandatory)/, /(necessary|essential|required|mandatory)\s+(cookies\s+)?only/,
  /(use|allow|accept|continue with|keep|save|confirm)\s+(only\s+)?(the\s+)?(strictly\s+)?(necessary|essential|required|mandatory)\s*(cookies)?(\s|$)/];
var ONLY_PH = ['nur notwendige', 'nur erforderliche', 'nur essenzielle', 'nur technisch', 'ausschließlich notwendige', 'uniquement nécessaires', 'cookies nécessaires uniquement',
  'seulement les cookies nécessaires', 'solo necesarias', 'solo las necesarias', 'solo esenciales', 'solo necessari', 'solo i necessari', 'soltanto necessari',
  'apenas necessários', 'somente necessários', 'só necessários', 'alleen noodzakelijke', 'alleen essentiële', 'tylko niezbędne', 'tylko wymagane',
  'pouze nezbytné', 'pouze nutné', 'только необходимые', 'только обязательные',
  'лише необхідні', 'тільки необхідні',
  'მხოლოდ აუცილებელ', 'მხოლოდ საჭირო', 'მხოლოდ აუცილ',
  'endast nödvändiga', 'kun nødvendige', 'vain välttämättömät', 'sadece gerekli', 'yalnızca gerekli', 'μόνο τα απαραίτητα',
  '必要なもののみ', '仅必要', '仅限必要', '필수만'];
var SET = ['manage', 'customize', 'customise', 'settings', 'preferences', 'more options', 'options', 'choices', 'show purposes', 'details', 'configure', 'personalize', 'personalise',
  'einstellungen', 'anpassen', 'verwalten', 'paramètres', 'paramétrer', 'personnaliser', 'gérer', 'configurar', 'personalizar', 'gestionar', 'más opciones',
  'impostazioni', 'personalizza', 'gestisci', 'configurações', 'gerir', 'instellingen', 'beheren', 'aanpassen', 'ustawienia', 'dostosuj', 'zarządzaj', 'nastavení',
  'přizpůsobit', 'настройки', 'настроить', 'управление', 'параметры',
  'პარამეტრ', 'მართვა', 'ayarlar', 'inställningar', 'innstillinger', 'indstillinger', 'asetukset'];
var SAVE = ['save', 'confirm', 'apply', 'submit', 'done', 'allow selection', 'allow selected', 'accept selected', 'accept selection', 'accept current',
  'speichern', 'bestätigen', 'auswahl', 'übernehmen', 'enregistrer', 'confirmer', 'valider', 'guardar', 'confirmar', 'aplicar', 'salva', 'conferma', 'salvar',
  'opslaan', 'bevestigen', 'zapisz', 'potwierdź', 'uložit', 'potvrdit', 'сохранить', 'подтвердить', 'применить',
  'შენახვა', 'დადასტურება', 'kaydet', 'onayla', 'spara', 'bekräfta', 'gem', 'tallenna'];
// pressing any of these accepts everything: they are never pressed, and they veto a settings / save match
var ALL = ['accept all', 'allow all', 'agree to all', 'accept and', 'accept cookies', 'accept everything', 'i accept', 'i agree', 'alle akzeptieren', 'alles akzeptieren',
  'alle zulassen', 'tout accepter', 'accepter et', 'aceptar todo', 'aceptar todas', 'accetta tutto', 'accetta tutti', 'aceitar tudo', 'aceitar todos', 'alles accepteren',
  'akceptuję wszystko', 'zaakceptuj wszystkie', 'přijmout vše', 'принять все', 'принять и', 'прийняти всі',
  'ყველას მიღება', 'ვეთანხმები'];
// an accept button is what makes a pop-up a consent prompt (a footer "Cookie settings" link has none)
var ACCEPTISH = ['accept', 'allow', 'agree', 'got it', 'understood', 'i understand', 'okay', 'akzept', 'zulassen', 'zustimm', 'einverstanden', 'accepter', 'autoriser', 'aceptar',
  'permitir', 'accetta', 'consenti', 'aceitar', 'accepteren', 'toestaan', 'akceptuj', 'zgadzam', 'přijmout', 'принять', 'согласен', 'разрешить', 'прийняти', 'მიღება', 'ვეთანხმ',
  'kabul', 'godkänn', 'tillad', 'hyväksy', '同意', '許可', '허용', 'موافق'];
var MIXED = /(necessar|essential|required|notwendig|nécessaire)[a-z]*\s*(,|and|&|\+|und|et|y|e)\s*(analytic|statistic|marketing|advertis|performance|functional|preference|personali|targeting|social|werbung|analyse)|(analytic|statistic|marketing|advertis|performance|functional|preference|personali|targeting|social)[a-z]*\s*(,|and|&|\+)\s*(necessar|essential|required)/;
var NECESSARY = /necessar|essential|required|mandatory|strictly|always|notwendig|erforderlich|nécessaire|necesari|necessari|noodzakelijk|niezbędn|nezbytn|необходим|обязательн|აუცილებელ|საჭირო|zorunlu|nödvändig|nødvendig/i;

function isReject(t) { return !!t && t.length <= 80 && !MIXED.test(t) && (has(t, REJ) || has(t, ONLY_PH) || ONLY_RX.some(function (r) { return r.test(t); })); }
function isSettings(t) { return !!t && t.length <= 60 && has(t, SET) && !isReject(t) && !has(t, ALL) && !has(t, SAVE); }
function isAcceptish(t) { return !!t && t.length <= 60 && (t === 'ok' || has(t, ACCEPTISH)); }
function isSave(t) { return !!t && t.length <= 60 && has(t, SAVE) && !has(t, ALL); }

// ---------------------------------------------------------------- finding things
// every element, through open shadow roots and same-site iframes, in one pass
function flat() {
  var out = [];
  (function walk(root, depth) {
    if (depth > 6) return;
    var all; try { all = root.querySelectorAll('*'); } catch (e) { return; }
    for (var i = 0; i < all.length; i++) {
      var n = all[i];
      out.push(n);
      if (n.shadowRoot) walk(n.shadowRoot, depth + 1);
      else if (n.tagName === 'IFRAME') { try { var d = n.contentDocument; if (d && d.documentElement) walk(d, depth + 1); } catch (e) { } }
    }
  })(document, 0);
  return out;
}
function visible(el) {
  if (!el || !el.isConnected) return false;
  var r = el.getBoundingClientRect();
  if (r.width < 2 || r.height < 2) return false;
  var cs = (el.ownerDocument.defaultView || window).getComputedStyle(el);
  return cs.visibility !== 'hidden' && cs.display !== 'none' && parseFloat(cs.opacity || '1') > 0.05;
}
function label(el) {
  var t = lc((el.tagName === 'INPUT' && /^(button|submit)$/i.test(el.type) && el.value) ? el.value : (el.innerText || el.textContent || ''));
  if (!t) t = lc(el.getAttribute('aria-label') || el.getAttribute('title') || '');   // icon-only buttons (a bare cross or tick)
  return t;
}
var FAST = ['#onetrust-reject-all-handler', '.ot-pc-refuse-all-handler', '#CybotCookiebotDialogBodyButtonDecline', '#CybotCookiebotDialogBodyLevelButtonLevelOptinDeclineAll',
  '#didomi-notice-disagree-button', '.didomi-continue-without-agreeing', '[data-testid="uc-deny-all-button"]', '#uc-btn-deny-banner', '.fc-cta-do-not-consent', '#sp-cc-rejectall-link',
  '#truste-consent-required', '.osano-cm-denyAll', '.cn-decline', '.cm-btn-decline', '.cky-btn-reject', '.cmplz-deny', '._brlbs-refuse-btn', 'button[data-cookiefirst-action="reject"]',
  '.cc-deny', '#cookie-consent-decline', '#cn-refuse-cookie', '.iubenda-cs-reject-btn', '[data-tid="banner-decline"]', '#cookiescript_reject', '#axeptio_btn_dismiss',
  '[data-cookiebanner="accept_only_essential_button"]', '#ccc-reject-settings', '.ccc-reject-button'];
var FAST_SEL = FAST.join(',');
var ROOTS = '#onetrust-consent-sdk,#onetrust-banner-sdk,#onetrust-pc-sdk,#CybotCookiebotDialog,#didomi-host,#usercentrics-root,.qc-cmp2-container,#qc-cmp2-ui,.fc-consent-root,' +
  '#cookie-law-info-bar,#cmplz-cookiebanner-container,.cky-consent-container,._brlbs-box-wrap,.cc-window,.osano-cm-window,.iubenda-cs-container,#iubenda-cs-banner,' +
  '.cookiefirst-root,#truste-consent-track,.klaro,#cmpbox,#axeptio_overlay,#cookiescript_injected,[role="dialog"],[role="alertdialog"],[aria-modal="true"],dialog';
var NAMED = /cookie|consent|gdpr|ccpa|cmp|privacy|banner|notice|overlay|modal|popup/i;
var NAMED_CC = /cookie|consent|gdpr|ccpa|cmp|privacy/i;
var STRONG = /cookie|consent|gdpr|ccpa|tracking|datenschutz|einwillig|zustimm|куки|ქუქი|çerez|cookies/i;
var WEAK = /privacy|personali[sz]|advertis|partners|vendors|confidential|privacidad|vie privée|конфиденц|კონფიდენც/i;

// the consent pop-up this element belongs to, or null
function popup(el) {
  var n = el, hops = 0;
  while (n && hops < 18) {
    if (n.nodeType === 1) {
      var known = false;
      try { known = n.matches(ROOTS); } catch (e) { }
      var idc = (n.id || '') + ' ' + (typeof n.className === 'string' ? n.className : '');
      var named = !known && NAMED.test(idc), ccNamed = known || NAMED_CC.test(idc);
      var fixed = false;
      if (!known && !named) { try { var p = (n.ownerDocument.defaultView || window).getComputedStyle(n).position; fixed = p === 'fixed' || p === 'sticky'; } catch (e) { } }
      if (known || named || fixed) {
        var txt = n.textContent || '';
        if (!txt && n.tagName === 'IFRAME') { try { txt = n.contentDocument.documentElement.textContent || ''; } catch (e) { } }
        if (txt.length < 9000) {
          var head = txt.slice(0, 2500);
          if (STRONG.test(head) || (ccNamed && WEAK.test(head))) return n;
        }
      }
    }
    n = up(n);
    hops++;
  }
  return null;
}

function up(n) { return n.parentElement || (n.getRootNode && n.getRootNode().host) || (n.ownerDocument && n.ownerDocument.defaultView && n.ownerDocument.defaultView.frameElement) || null; }
function inside(host, el) { for (var n = el, i = 0; n && i < 40; n = up(n), i++) if (n === host) return true; return false; }
// the consent pop-up this visible element belongs to, unless the visitor opened it themselves. The moment it is
// first seen on screen decides: a pop-up that shows up right after a real click / key press was asked for.
function owner(el) {
  var h = popup(el);
  if (!h) return null;
  var mine = born.get(h);
  if (mine === undefined) { mine = Date.now() - lastInput > 2500; born.set(h, mine); }
  return mine ? h : null;
}
function shown(sw) {                                             // custom switches hide the real checkbox: look at what draws it
  for (var n = sw, i = 0; n && i < 3; n = n.parentElement, i++) if (visible(n)) return true;
  return false;
}

function pressable(el, kind) {
  if (!visible(el) || el.disabled || el.getAttribute('aria-disabled') === 'true') return false;
  if (el.tagName === 'A') {
    var h = el.getAttribute('href');
    var real = !!h && !/^(#|javascript:)/i.test(h.trim());
    if (real) {
      if (kind !== 'reject') return false;                // a link that goes somewhere is never "settings" or "save"
      try { if (new URL(h, location.href).origin !== location.origin) return false; } catch (e) { return false; }
    }
  }
  return (seen.get(el) || 0) < 2;
}
function press(el, what) {
  try {
    var o = { bubbles: true, cancelable: true, composed: true, view: el.ownerDocument.defaultView || window, button: 0 };
    ['pointerdown', 'mousedown', 'pointerup', 'mouseup'].forEach(function (t) {
      try { el.dispatchEvent(t.indexOf('pointer') === 0 && window.PointerEvent ? new PointerEvent(t, o) : new MouseEvent(t, o)); } catch (e) { }
    });
    el.click();
    seen.set(el, (seen.get(el) || 0) + 1);
    lastClick = Date.now();
    LOG.clicks.push({ what: what, label: label(el).slice(0, 60), at: lastClick - T0 });
    return true;
  } catch (e) { return false; }
}
var T0 = Date.now();

// ---------------------------------------------------------------- switches (second layer)
function isOn(el) { return el.tagName === 'INPUT' ? !!el.checked : el.getAttribute('aria-checked') === 'true'; }
function toggleText(el) {
  var t = '';
  try {
    var l = el.closest('label') || (el.id && el.ownerDocument.querySelector('label[for="' + el.id.replace(/"/g, '') + '"]'));
    if (l) t = l.textContent || '';
  } catch (e) { }
  t = t || el.getAttribute('aria-label') || '';
  for (var p = el.parentElement, i = 0; !t && p && i < 3; p = p.parentElement, i++) if ((p.textContent || '').length < 160) t = p.textContent;
  return lc(t);
}

function scan() {
  tick++;
  if (Date.now() > DEADLINE || LOG.clicks.length >= MAX_CLICKS) return stop('finished');
  if (Date.now() - lastClick < 600) return;                    // give the pop-up a moment to react to the last click
  var els = flat();
  var pick = null, save = null, fast = null, settingsList = [], accepts = [];
  for (var i = 0; i < els.length; i++) {
    var el = els[i];
    var tag = el.tagName;
    if (!fast) { try { if (el.matches(FAST_SEL) && pressable(el, 'reject')) fast = el; } catch (e) { } }
    var clickable = tag === 'BUTTON' || tag === 'A' || tag === 'SUMMARY' || (tag === 'INPUT' && /^(button|submit)$/i.test(el.type)) ||
      el.getAttribute('role') === 'button' || el.hasAttribute('onclick') || (el.hasAttribute('tabindex') && tag !== 'INPUT' && (el.textContent || '').length < 80);
    if (!clickable && (tag === 'DIV' || tag === 'SPAN' || tag === 'LI' || tag === 'P' || tag === 'LABEL') && el.childElementCount < 4 && (el.textContent || '').length < 40) {
      try { clickable = (el.ownerDocument.defaultView || window).getComputedStyle(el).cursor === 'pointer'; } catch (e) { }   // a div wired up with a click listener
    }
    if (!clickable) continue;
    var t = label(el);
    if (!t) continue;
    if (!pick && isReject(t) && pressable(el, 'reject') && owner(el)) pick = el;
    else if (isSettings(t) && pressable(el, 'settings')) { var sh = owner(el); if (sh) settingsList.push({ el: el, host: sh }); }
    else if (!save && isSave(t) && pressable(el, 'save') && owner(el)) save = el;
    if (isAcceptish(t) && !pick) accepts.push(el);
  }
  if (fast && owner(fast)) return act(fast, 'reject (known)');
  if (pick) { rejectedAt = Date.now(); return act(pick, 'reject'); }

  // a pop-up that is showing its category switches: optional ones off, then save
  var changed = false, panel = null;
  for (var k = 0; k < els.length; k++) {
    var sw = els[k];
    var isSwitch = (sw.tagName === 'INPUT' && sw.type === 'checkbox') || sw.getAttribute('role') === 'switch' || sw.getAttribute('role') === 'checkbox';
    if (!isSwitch || sw.disabled || sw.getAttribute('aria-disabled') === 'true') continue;
    if (!shown(sw)) continue;
    var host = owner(sw);
    if (!host) continue;
    if (!settingsOpened && Date.now() - T0 > 8000) continue;     // a preferences panel that turned up late and that we did not open: leave it
    panel = host;
    var tt = toggleText(sw);
    if (isOn(sw) && !NECESSARY.test(tt) && (seen.get(sw) || 0) < 2) { press(sw, 'switch off: ' + tt.slice(0, 40)); changed = true; }
  }
  if (changed) return;                                            // the next scan finds the save button once the switches settled
  if (panel && save && panel.contains(save)) return act(save, 'save choices');
  if (settingsOpened < 2) {
    for (var q = 0; q < settingsList.length; q++) {                // only in a pop-up that also offers to accept: a real consent prompt
      var sh2 = settingsList[q].host, ok = false;
      for (var a = 0; a < accepts.length && !ok; a++) ok = inside(sh2, accepts[a]);
      if (ok) { settingsOpened++; return act(settingsList[q].el, 'open settings'); }
    }
  }
  // right after rejecting, a pop-up may ask to confirm
  if (save && Date.now() - rejectedAt < 8000) return act(save, 'confirm');
}
function act(el, what) { LOG.state = what; return press(el, what); }
function stop(why) {
  LOG.state = LOG.clicks.length ? 'done' : 'nothing found';
  if (mo) { mo.disconnect(); mo = null; }
  if (timer) { clearInterval(timer); timer = 0; }
}

['pointerdown', 'mousedown', 'keydown', 'touchstart'].forEach(function (t) {
  try { document.addEventListener(t, function (e) { if (e.isTrusted) lastInput = Date.now(); }, true); } catch (e) { }
});
scan();
var soon = 0;
mo = new MutationObserver(function () { clearTimeout(soon); soon = setTimeout(scan, 250); });
mo.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'hidden', 'aria-hidden', 'open'] });
timer = setInterval(scan, 800);
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

    private const string TplCaptions = """
// Grab video captions from the network
// Watches fetch/XHR and the resource timeline for subtitle files (WebVTT, SRT, TTML, YouTube
// timedtext, HLS subtitle playlists), parses them and keeps them in window.__brainCaptions:
//   .list()  .text(id)  .srt(id)  .vtt(id)  .get(id)  .tracks  .notes   (no id = newest track of this page)
if (window.__brainCaptions) return;
var MAX_BODY = 5e6, MAX_TRACKS = 30, MAX_PLAYLISTS = 6, MAX_SEGMENTS = 800;
var SKIP = /\.(jpe?g|png|gif|webp|avif|svg|ico|woff2?|ttf|css|m?js|mp4|m4s|m4a|webm|ts|aac|mp3|ogg|wasm)(\?|#|$)/i;
var FILE = /\.(vtt|webvtt|srt|ttml2?|dfxp|m3u8)(\?|#|$)/i;
var HINT = /timedtext|caption|subtitle|texttrack|text_track|transcript|[?&](?:fmt|format)=(?:vtt|webvtt|srt|ttml|json3|srv\d)\b/i;
var TYPE = /text\/vtt|subrip|ttml|dfxp|mpegurl/i;
var ARROW = /((?:\d+:)?\d+:\d+(?:[.,]\d+)?)\s*-->\s*((?:\d+:)?\d+:\d+(?:[.,]\d+)?)/;
var tracks = [], seen = {}, notes = [], lists = 0, nextId = 1, selId = 0, drawn = '', hidden = false, timer, host, root;
var origFetch = window.fetch, origOpen = XMLHttpRequest.prototype.open, origSend = XMLHttpRequest.prototype.send;

function note(m) { notes.push(String(m).slice(0, 200)); if (notes.length > 40) notes.shift(); }
function abs(u, base) { try { return new URL(String(u), base || location.href).href; } catch (e) { return ''; } }
function wanted(u) { return !SKIP.test(u) && (FILE.test(u) || HINT.test(u)); }
function decode(s) { var ta = document.createElement('textarea'); ta.innerHTML = s; return ta.value; }
function tidy(s) { return s.split('\n').map(function (l) { return l.trim(); }).filter(Boolean).join('\n'); }
function clean(s) { return tidy(decode(String(s).replace(/<[^>]*>/g, ''))); }
function pad(n, w) { n = String(n); while (n.length < w) n = '0' + n; return n; }
function secs(v) {
  var m = /^(?:(\d+):)?(\d+):(\d+)(?:[.,](\d+))?$/.exec(String(v || '').trim());
  return m ? (+(m[1] || 0)) * 3600 + (+m[2]) * 60 + (+m[3]) + (m[4] ? +('0.' + m[4]) : 0) : NaN;
}
function stamp(s, sep) {
  var ms = Math.round(s * 1000);
  return pad(Math.floor(ms / 3600000), 2) + ':' + pad(Math.floor(ms / 60000) % 60, 2) + ':' + pad(Math.floor(ms / 1000) % 60, 2) + sep + pad(ms % 1000, 3);
}
function langOf(u) {
  // not hl=: on YouTube that is the language of the page, not of the captions
  var m = /[?&]tlang=([\w-]+)/i.exec(u) || /[?&](?:lang|language|locale)=([\w-]+)/i.exec(u) ||
    /[\/_.-]([a-z]{2,3}(?:-[A-Za-z]{2,4})?)\.(?:vtt|webvtt|srt|ttml2?|dfxp)(?:[?#]|$)/.exec(u);
  return m ? m[1] : '';
}
// pages like youtube.com/watch change address without reloading: tracks belong to the page they were found on
function pageKey(h) {
  try {
    var u = new URL(h), v = u.searchParams.get('v');
    return u.origin + u.pathname + (v ? '?v=' + v : u.search);
  } catch (e) { return h; }
}
function here() { var k = pageKey(location.href); return tracks.filter(function (t) { return t.key === k; }); }

// ---- parsers: each returns [{s, e, t}] (start, end in seconds, text)
function parseVtt(t) {
  var cues = [];
  t.replace(/\r\n?/g, '\n').split(/\n{2,}/).forEach(function (block) {
    var lines = block.split('\n'), i = 0;
    while (i < lines.length && !ARROW.test(lines[i])) i++;
    if (i === lines.length || /^(NOTE|STYLE|REGION)\b/.test(lines[0])) return;
    var m = ARROW.exec(lines[i]), txt = clean(lines.slice(i + 1).join('\n'));
    if (txt) cues.push({ s: secs(m[1]), e: secs(m[2]), t: txt });
  });
  return cues;
}
function parseJson3(o) {
  var cues = [];
  (o.events || []).forEach(function (e) {
    var txt = tidy((e.segs || []).map(function (g) { return g.utf8 || ''; }).join(''));
    if (txt) cues.push({ s: (e.tStartMs || 0) / 1000, e: ((e.tStartMs || 0) + (e.dDurationMs || 0)) / 1000, t: txt });
  });
  return cues;
}
function nodeText(n) {
  var s = '';
  for (var c = n.firstChild; c; c = c.nextSibling) {
    if (c.nodeType === 3) s += c.nodeValue.replace(/\s+/g, ' ');
    else if (c.nodeType === 1) s += c.localName === 'br' ? '\n' : nodeText(c);
  }
  return s;
}
function ttTime(v, tick) {
  var m = /^(\d+(?:\.\d+)?)(h|ms|m|s|t)$/.exec(String(v || '').trim());
  if (!m) return secs(String(v || '').replace(/^(\d+:\d+:\d+):\d+$/, '$1'));
  var n = +m[1];
  return m[2] === 'h' ? n * 3600 : m[2] === 'm' ? n * 60 : m[2] === 's' ? n : m[2] === 'ms' ? n / 1000 : n / tick;
}
function parseXml(t) {
  var d = new DOMParser().parseFromString(t, 'text/xml'), r = d.documentElement, cues = [], els, i, e, a, b, txt;
  if (!r || d.getElementsByTagName('parsererror').length) return null;
  var name = r.localName.toLowerCase();
  if (name === 'tt') {
    var tick = +(r.getAttributeNS('http://www.w3.org/ns/ttml#parameter', 'tickRate') || r.getAttribute('ttp:tickRate') || 1);
    els = d.getElementsByTagNameNS('*', 'p');
    for (i = 0; i < els.length; i++) {
      e = els[i];
      a = ttTime(e.getAttribute('begin'), tick);
      b = e.getAttribute('end') ? ttTime(e.getAttribute('end'), tick) : a + ttTime(e.getAttribute('dur'), tick);
      txt = tidy(nodeText(e));
      if (txt && !isNaN(a)) cues.push({ s: a, e: b, t: txt });
    }
    return cues.length ? { format: 'ttml', cues: cues } : null;
  }
  if (name !== 'transcript' && name !== 'timedtext') return null;
  els = d.getElementsByTagName('*');
  for (i = 0; i < els.length; i++) {
    e = els[i];
    if (e.localName === 'text' && e.hasAttribute('start')) {         // youtube, older format (seconds, entities encoded twice)
      a = +e.getAttribute('start'); b = a + +(e.getAttribute('dur') || 0); txt = tidy(decode(nodeText(e)));
    } else if (e.localName === 'p' && e.hasAttribute('t')) {         // youtube srv3 (milliseconds)
      a = +e.getAttribute('t') / 1000; b = a + +(e.getAttribute('d') || 0) / 1000; txt = tidy(nodeText(e));
    } else continue;
    if (txt) cues.push({ s: a, e: b, t: txt });
  }
  return cues.length ? { format: 'xml', cues: cues } : null;
}
function parse(t) {
  var head = t.slice(0, 2000).trim(), c;
  if (/^WEBVTT/.test(head) || (head.charAt(0) !== '<' && ARROW.test(head))) {
    c = parseVtt(t);
    return c.length ? { format: /^WEBVTT/.test(head) ? 'vtt' : 'srt', cues: c } : null;
  }
  if (head.charAt(0) === '{') {
    try { var o = JSON.parse(t); c = o && o.events ? parseJson3(o) : []; } catch (e) { c = []; }
    return c.length ? { format: 'json3', cues: c } : null;
  }
  return head.charAt(0) === '<' ? parseXml(t) : null;
}

// ---- tracks
function add(url, format, cues, meta) {
  if (!cues.length || tracks.length >= MAX_TRACKS) return;
  cues.sort(function (a, b) { return a.s - b.s; });
  cues.forEach(function (c) { if (!(c.e > c.s)) c.e = c.s + 2; });
  var lang = (meta && meta.lang) || langOf(url);
  var sig = lang + '|' + cues.length + '|' + cues[0].t + '|' + cues[cues.length - 1].s;
  if (tracks.some(function (t) { return t.sig === sig; })) return;
  var tr = {
    id: nextId++, sig: sig, url: url, format: format, lang: lang, label: (meta && meta.label) || '',
    auto: /[?&]kind=asr\b/.test(url), cues: cues, page: location.href, key: pageKey(location.href), title: document.title, at: Date.now()
  };
  tracks.push(tr);
  selId = tr.id; hidden = false;
  draw();
}
function ingest(url, body, meta) {
  if (typeof body !== 'string' || !body || body.length > MAX_BODY) return;
  var t = body.replace(/^\uFEFF/, '');
  if (/^\s*#EXTM3U/.test(t)) return playlist(url, t, meta || {});
  var r = parse(t);
  if (r) add(url, r.format, r.cues, meta);
  else note('not captions: ' + url.slice(0, 120));
}
function playlist(url, t, meta) {
  var lines = t.split(/\r?\n/), segs;
  if (/#EXT-X-STREAM-INF|#EXT-X-MEDIA:/.test(t)) {                   // master playlist: follow its subtitle renditions
    lines.forEach(function (ln) {
      var u = /^#EXT-X-MEDIA:/.test(ln) && /TYPE=SUBTITLES/.test(ln) && /URI="([^"]+)"/.exec(ln);
      if (!u) return;
      var lang = /LANGUAGE="([^"]*)"/.exec(ln), name = /NAME="([^"]*)"/.exec(ln), sub = abs(u[1], url);
      if (sub && !seen[sub]) { seen[sub] = 2; fetchIt(sub, { lang: lang ? lang[1] : '', label: name ? name[1] : '', hls: true }); }
    });
    return;
  }
  segs = lines.filter(function (l) { return l && l.charAt(0) !== '#'; }).map(function (l) { return abs(l, url); });
  if (!segs.length || segs.length > MAX_SEGMENTS) return;
  if (!meta.hls && !segs.every(function (s) { return /\.(vtt|webvtt|srt|ttml2?)(\?|#|$)/i.test(s); })) return;
  segs.forEach(function (s) { seen[s] = 2; });
  Promise.all(segs.map(function (s) {
    return origFetch.call(window, s).then(function (r) { return r.text(); });
  })).then(function (parts) {
    var dup = {}, cues = [];
    parts.forEach(function (p) {
      parseVtt(p).forEach(function (c) { var k = c.s + '|' + c.t; if (!dup[k]) { dup[k] = 1; cues.push(c); } });
    });
    for (var i = tracks.length - 1; i >= 0; i--)                     // a player may have loaded single segments first
      if (segs.indexOf(tracks[i].url) >= 0) tracks.splice(i, 1);
    add(url, 'vtt', cues, meta);
    draw();
  }).catch(function (e) { note('subtitle playlist failed: ' + url.slice(0, 120) + ' ' + e); });
}
function fetchIt(url, meta) {
  if (tracks.length >= MAX_TRACKS) return;
  origFetch.call(window, url).then(function (r) {
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return r.text();
  }).then(function (b) { ingest(url, b, meta); })
    .catch(function (e) { note('could not read ' + url.slice(0, 120) + ': ' + e); });
}

// ---- finding them: live traffic first (we get the body for free), then the resource timeline for what loaded before us
function watch(url, type, body, orig) {
  url = abs(url);
  if (!url || seen[url] === 2) return;
  if (!(seen[orig] === 1 || wanted(url) || TYPE.test(type || ''))) return;
  seen[url] = 2;
  if (orig) seen[orig] = 2;
  try {
    Promise.resolve(body()).then(function (b) {
      if (typeof b === 'string') ingest(url, b); else fetchIt(url);
    }, function () { fetchIt(url); });
  } catch (e) { fetchIt(url); }
}
window.fetch = function (input) {
  var p = origFetch.apply(this, arguments);
  try {
    var u = abs((input && input.url) || input);
    if (u && !seen[u] && wanted(u)) seen[u] = 1;
    p.then(function (res) {
      try {
        if (res.ok) watch(res.url || u, res.headers.get('content-type'), function () { return res.clone().text(); }, u);
      } catch (e) { }
    }, function () { });
  } catch (e) { }
  return p;
};
XMLHttpRequest.prototype.open = function (method, u) {
  try {
    this.__brainCapUrl = abs(u);
    if (this.__brainCapUrl && !seen[this.__brainCapUrl] && wanted(this.__brainCapUrl)) seen[this.__brainCapUrl] = 1;
  } catch (e) { }
  return origOpen.apply(this, arguments);
};
XMLHttpRequest.prototype.send = function () {
  var x = this;
  try {
    x.addEventListener('load', function () {
      try {
        if (x.status < 200 || x.status > 299) return;
        watch(x.responseURL || x.__brainCapUrl, x.getResponseHeader('content-type'), function () {
          if (x.responseType === '' || x.responseType === 'text') return x.responseText;
          if (x.responseType === 'arraybuffer') return new TextDecoder().decode(x.response);
          if (x.responseType === 'json') return JSON.stringify(x.response);
        }, x.__brainCapUrl);
      } catch (e) { }
    });
  } catch (e) { }
  return origSend.apply(this, arguments);
};
function fromTimeline(entries) {
  entries.forEach(function (e) {
    try {
      var u = e.name;
      if (seen[u] || /^(img|image|css|script|link|iframe|frame|beacon|font|navigation)$/.test(e.initiatorType) || !wanted(u)) return;
      if (/\.m3u8/i.test(u) && lists++ >= MAX_PLAYLISTS) return;
      seen[u] = 2;
      fetchIt(u);
    } catch (x) { }
  });
}
try {
  fromTimeline(performance.getEntriesByType('resource'));
  new PerformanceObserver(function (l) { fromTimeline(l.getEntries()); }).observe({ entryTypes: ['resource'] });
} catch (e) { note('resource timeline unavailable: ' + e); }

// <track> files and cues the page's own player created
function scan() {
  var again = false;
  try {
    document.querySelectorAll('track[src]').forEach(function (t) {
      var u = abs(t.src);
      if ((t.kind === 'subtitles' || t.kind === 'captions') && u && !seen[u]) { seen[u] = 2; fetchIt(u, { lang: t.srclang, label: t.label }); }
    });
    document.querySelectorAll('video').forEach(function (v) {
      for (var i = 0; i < v.textTracks.length; i++) {
        var tt = v.textTracks[i], n = tt.cues ? tt.cues.length : 0;
        if ((tt.kind !== 'subtitles' && tt.kind !== 'captions') || !n || tt.__brainN === -1) continue;
        if (tt.__brainN !== n) { tt.__brainN = n; again = true; continue; }   // still filling up
        tt.__brainN = -1;
        var cues = [];
        for (var j = 0; j < n; j++) {
          var txt = clean(tt.cues[j].text || '');
          if (txt) cues.push({ s: tt.cues[j].startTime, e: tt.cues[j].endTime, t: txt });
        }
        add('textTrack:' + (tt.label || tt.language || i), 'textTrack', cues, { lang: tt.language, label: tt.label });
      }
    });
  } catch (e) { note('scan: ' + e); }
  if (again) later(1500);
  draw();                                                            // the page may have changed since the chip was drawn
}
function later(ms) { clearTimeout(timer); timer = setTimeout(scan, ms); }
['loadedmetadata', 'loadeddata', 'play', 'playing'].forEach(function (ev) {
  document.addEventListener(ev, function () { later(600); }, true);
});
document.addEventListener('load', function (e) { if (e.target && e.target.tagName === 'TRACK') later(300); }, true);

// ---- the CC chip
function mk(tag, css, txt, click) {
  var n = document.createElement(tag);
  n.style.cssText = css;
  if (txt != null) n.textContent = txt;
  if (click) n.addEventListener('click', click);
  return n;
}
function copy(s, b) {
  var label = b.textContent;
  function done(ok) { b.textContent = ok ? 'Copied' : 'Failed'; setTimeout(function () { b.textContent = label; }, 1200); }
  function fallback() {
    var ta = mk('textarea', 'position:fixed;opacity:0', null);
    ta.value = s;
    root.appendChild(ta); ta.select();
    var ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { }
    root.removeChild(ta);
    done(ok);
  }
  try {
    if (navigator.clipboard && navigator.clipboard.writeText) { navigator.clipboard.writeText(s).then(function () { done(true); }, fallback); return; }
  } catch (e) { }
  fallback();
}
function draw() {
  var cur = here(), tr = cur.filter(function (t) { return t.id === selId; })[0] || cur[cur.length - 1];
  var want = API.ui === false || hidden || !tr ? '' : cur.map(function (t) { return t.id + ':' + t.cues.length; }).join() + '|' + tr.id;
  if (want === drawn) return;
  drawn = want;
  if (!want) { if (host) host.style.display = 'none'; return; }
  if (!host) {
    host = mk('div', 'all:initial;position:fixed;left:14px;bottom:14px;z-index:2147483647');
    root = host.attachShadow({ mode: 'open' });
    (document.body || document.documentElement).appendChild(host);
  }
  host.style.display = '';
  while (root.firstChild) root.removeChild(root.firstChild);
  var bar = mk('div', 'display:flex;align-items:center;gap:8px;padding:6px 8px 6px 10px;border-radius:999px;background:rgba(24,24,27,.92);color:#fff;font:12px/1 -apple-system,system-ui,sans-serif;box-shadow:0 4px 18px rgba(0,0,0,.35)');
  var btn = 'border:0;border-radius:999px;padding:5px 9px;background:rgba(255,255,255,.16);color:#fff;font:inherit;cursor:pointer';
  function name(t) { return (t.label || t.lang || 'captions') + (t.auto ? ' (auto)' : '') + ' · ' + t.cues.length + ' lines'; }
  bar.appendChild(mk('b', 'background:#fff;color:#111;border-radius:4px;padding:2px 4px;font-size:10px', 'CC'));
  if (cur.length > 1) {
    var pick = mk('select', 'border:0;border-radius:6px;padding:3px;background:rgba(255,255,255,.16);color:#fff;font:inherit;max-width:220px');
    cur.forEach(function (t) {
      var o = mk('option', 'color:#000', name(t));
      o.value = t.id; o.selected = t === tr;
      pick.appendChild(o);
    });
    pick.addEventListener('change', function () { selId = +pick.value; draw(); });
    bar.appendChild(pick);
  } else bar.appendChild(mk('span', '', name(tr)));
  var b1 = mk('button', btn, 'Copy text'), b2 = mk('button', btn, 'Copy SRT');
  b1.addEventListener('click', function () { copy(API.text(tr.id), b1); });
  b2.addEventListener('click', function () { copy(API.srt(tr.id), b2); });
  bar.appendChild(b1); bar.appendChild(b2);
  bar.appendChild(mk('button', btn + ';padding:5px 8px', '×', function () { hidden = true; draw(); }));
  root.appendChild(bar);
}

var API = window.__brainCaptions = {
  ui: true, tracks: tracks, notes: notes, scan: scan,
  list: function () {
    var k = pageKey(location.href);
    return tracks.map(function (t) { return { id: t.id, lang: t.lang, label: t.label, auto: t.auto, format: t.format, lines: t.cues.length, url: t.url, page: t.page, current: t.key === k }; });
  },
  get: function (id) { return id == null ? here().pop() : tracks.filter(function (t) { return t.id === id; })[0]; },
  text: function (id) { var t = API.get(id); return t ? t.cues.map(function (c) { return c.t; }).join('\n') : ''; },
  srt: function (id) {
    var t = API.get(id);
    return t ? t.cues.map(function (c, i) { return (i + 1) + '\n' + stamp(c.s, ',') + ' --> ' + stamp(c.e, ',') + '\n' + c.t + '\n'; }).join('\n') : '';
  },
  vtt: function (id) {
    var t = API.get(id);
    return t ? 'WEBVTT\n\n' + t.cues.map(function (c) { return stamp(c.s, '.') + ' --> ' + stamp(c.e, '.') + '\n' + c.t + '\n'; }).join('\n') : '';
  }
};
scan();
later(2500);
""";
}
