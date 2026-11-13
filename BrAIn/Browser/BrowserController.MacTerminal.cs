using System.Text.Json;
using BrAIn.Browser.Mac;

namespace BrAIn.Browser;

/// <summary>
/// macOS terminal mode (brain view on a Mac): the frame the viewer shows, the input it sends and the text layer, made inside
/// BrAIn because another process may not look into this one's windows (Mac/MacView.cs). Popup windows are placed over the
/// page in the frame — centred, with a title strip and a close box — and input is routed to whichever window is under the
/// point. A drop-down list would open a native menu on the user's real screen and hold the app in its menu loop, so a click on
/// one hands its options to the viewer, which lets the user pick in the terminal instead.
/// </summary>
public sealed partial class BrowserController
{
    private int _viewW, _viewH;                 // the size the viewer asked for (points)
    private IntPtr _macSelView;                 // the web view of the drop-down list being picked
    private long _lastPress; private double _pressX, _pressY; private int _clicks;
    private bool _macDown;

    private IntPtr MacMainWindow() => MacWindow.WindowByNumber(_nsWindowNumber);

    private const double PopHeader = 28;

    /// <summary>Where a popup sits in the frame (top-left points): its page, the title strip above it, the close box.</summary>
    private static (MacWindow.CGRect Content, MacWindow.CGRect Header, MacWindow.CGRect Close) PopupPlace(double w, double h, double pw, double ph)
    {
        pw = Math.Max(160, Math.Min(pw, w - 40));
        ph = Math.Max(100, Math.Min(ph, h - 80));
        var x = Math.Round((w - pw) / 2);
        var y = Math.Round((h - ph - PopHeader) / 2 + PopHeader);
        var content = new MacWindow.CGRect { X = x, Y = y, W = pw, H = ph };
        var header = new MacWindow.CGRect { X = x, Y = y - PopHeader, W = pw, H = PopHeader };
        var close = new MacWindow.CGRect { X = x + pw - 30, Y = y - PopHeader, W = 30, H = PopHeader };
        return (content, header, close);
    }

    private static bool In(MacWindow.CGRect r, double x, double y) => x >= r.X && y >= r.Y && x < r.X + r.W && y < r.Y + r.H;

    private (double W, double H) MacCanvas(IntPtr win)
    {
        var (cw, ch) = MacView.ContentSize(win);
        return (_viewW > 0 ? _viewW : cw, _viewH > 0 ? _viewH : ch);
    }

    private static string? NsStr(IntPtr ns)
    {
        if (ns == IntPtr.Zero) return null;
        var p = ObjC.Send(ns, ObjC.Sel("UTF8String"));
        return p == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p);
    }

    private string MacSetViewSize(int w, int h)
    {
        w = Math.Clamp(w, 320, 4000); h = Math.Clamp(h, 240, 3000);
        InvokeOnUi(() =>
        {
            var win = MacMainWindow();
            if (win == IntPtr.Zero) return;
            _viewW = w; _viewH = h;
            MacView.SetContentSize(win, w, h);
            MacView.KeepRendering(win);
        });
        return JsonSerializer.Serialize(new { ok = true, w, h });
    }

    // ---------------------------------------------------------------- the frame

    /// <summary>One frame of the invisible window (and its popups) as BGRx pixels, the viewer's size.</summary>
    internal async Task<(int W, int H, byte[]? Px)> MacFrameAsync()
    {
        if (!IsMac || !Headless) return (0, 0, null);
        var items = new List<(Task<IntPtr>? Img, MacWindow.CGRect Rect, bool Header)>();
        double W = 0, H = 0;
        InvokeOnUi(() =>
        {
            var win = MacMainWindow();
            if (win == IntPtr.Zero) return;
            (W, H) = MacCanvas(win);
            MacView.KeepRendering(win);
            var page = Tabs.Active?.NativeView ?? IntPtr.Zero;
            if (page != IntPtr.Zero && ObjC.Send(page, ObjC.Sel("window")) == win)
            {
                var r = MacView.RectInContent(page);
                items.Add((MacView.Snapshot(page, r.W), r, false));
            }
            var bar = WkBar.BarView;
            if (bar != IntPtr.Zero && (ObjC.SendNInt(bar, ObjC.Sel("isHidden")) & 0xFF) == 0)
            {
                var r = MacView.RectInContent(bar);
                items.Add((MacView.Snapshot(bar, r.W), r, false));
            }
            foreach (var (pv, pw) in WkPopup.OpenWindows())
            {
                MacView.KeepRendering(pw);
                var (pcw, pch) = MacView.ContentSize(pw);
                var place = PopupPlace(W, H, pcw, pch);
                if (place.Content.W < pcw - 1 || place.Content.H < pch - 1) MacView.SetContentSize(pw, place.Content.W, place.Content.H);
                items.Add((null, place.Header, true));
                items.Add((MacView.Snapshot(pv, place.Content.W), place.Content, false));
            }
        });
        if (W <= 0 || H <= 0) return (0, 0, null);
        var all = Task.WhenAll(items.Where(i => i.Img is not null).Select(i => i.Img!));
        await Task.WhenAny(all, Task.Delay(3000));
        var layers = items.Select(i => new MacView.Layer(
            i.Img is { IsCompletedSuccessfully: true } t ? t.Result : IntPtr.Zero, i.Rect, i.Header)).ToList();
        var px = MacView.Compose((int)W, (int)H, layers);
        return ((int)W, (int)H, px);
    }

    // ---------------------------------------------------------------- input

    /// <summary>One input event from the viewer (JSON: t = move | btn | wheel | key | text | selectset). Returns JSON: ok, or
    /// for a click on a drop-down list its options ("select").</summary>
    internal async Task<string> MacInputAsync(JsonElement e)
    {
        if (!IsMac || !Headless) return "{\"error\":\"not a headless BrAIn\"}";
        var t = e.TryGetProperty("t", out var tt) ? tt.GetString() : "";
        double D(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        bool B(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

        if (t == "selectset")
        {
            var view = _macSelView;
            if (view == IntPtr.Zero) return "{\"ok\":false}";
            var js = "(function(id,i){var s=(window.__brainSels||{})[id];if(!s)return 'gone';s.selectedIndex=i;" +
                     "s.dispatchEvent(new Event('input',{bubbles:true}));s.dispatchEvent(new Event('change',{bubbles:true}));return 'ok'})(" +
                     (long)D("id") + "," + (int)D("index") + ")";
            Task<string?>? r = null;
            InvokeOnUi(() => r = MacView.Eval(view, js, "brain-view"));
            var res = r is null ? null : await r;
            return JsonSerializer.Serialize(new { ok = res == "ok" });
        }

        if (t is "key" or "text")
        {
            InvokeOnUi(() =>
            {
                var target = KeyWindow();
                if (target == IntPtr.Zero) return;
                if (t == "text")
                {
                    var s = e.TryGetProperty("s", out var sv) ? sv.GetString() ?? "" : "";
                    foreach (var rune in s.EnumerateRunes())
                    {
                        var ch = rune.ToString();
                        if (ch is "\r" or "\n") { MacView.Key(target, 36, "\r", "\r", 0); continue; }
                        MacView.Key(target, ch.Length == 1 ? MacView.LetterCode(ch[0]) : (ushort)0, ch, ch, 0);
                    }
                    return;
                }
                var ks = (nuint)D("ks");
                bool ctrl = B("ctrl"), alt = B("alt"), shift = B("shift"), sup = B("sup");
                nuint mods = (shift ? MacView.Shift : 0) | (alt ? MacView.Option : 0) | (sup ? MacView.Command : 0);
                if (MacView.NamedKey(ks) is { } named)
                {
                    if (ctrl) mods |= MacView.Control;
                    MacView.Key(target, named.Code, named.Chars, named.Chars, mods);
                    return;
                }
                var cp = ks < 0x100 ? (int)ks : ks >= 0x01000000 ? (int)(ks - 0x01000000) : 0;
                if (cp <= 0) return;
                var c = char.ConvertFromUtf32(cp);
                // a terminal's Ctrl+letter is what Command+letter is on a Mac (copy, paste, new tab, address bar …)
                if (ctrl) mods |= MacView.Command;
                var chars = shift && c.Length == 1 ? c.ToUpperInvariant() : c;
                MacView.Key(target, c.Length == 1 ? MacView.LetterCode(c[0]) : (ushort)0, chars, c.ToLowerInvariant(), mods);
            });
            return "{\"ok\":true}";
        }

        // pointer events: which window is under the point, and where in it
        double x = D("x"), y = D("y");
        var btn = (int)D("b");
        var press = B("p");
        IntPtr win = IntPtr.Zero, pageView = IntPtr.Zero; double lx = x, ly = y, px = 0, py = 0; var closeView = IntPtr.Zero; var onHeader = false;
        InvokeOnUi(() =>
        {
            var main = MacMainWindow();
            if (main == IntPtr.Zero) return;
            var (W, H) = MacCanvas(main);
            foreach (var (pv, pw) in Enumerable.Reverse(WkPopup.OpenWindows()))
            {
                var (pcw, pch) = MacView.ContentSize(pw);
                var place = PopupPlace(W, H, pcw, pch);
                if (In(place.Close, x, y)) { closeView = pv; return; }
                if (In(place.Header, x, y)) { onHeader = true; return; }
                if (In(place.Content, x, y)) { win = pw; pageView = pv; lx = x - place.Content.X; ly = y - place.Content.Y; px = lx; py = ly; return; }
            }
            win = main;
            var page = Tabs.Active?.NativeView ?? IntPtr.Zero;
            if (page != IntPtr.Zero)
            {
                var r = MacView.RectInContent(page);
                if (In(r, x, y) && !In(WkBar.BarView != IntPtr.Zero ? MacView.RectInContent(WkBar.BarView) : default, x, y))
                { pageView = page; px = x - r.X; py = y - r.Y; }
            }
        });
        if (closeView != IntPtr.Zero)
        {
            if (t == "btn" && press && btn == 1) InvokeOnUi(() => WkPopup.Close(closeView));
            return "{\"ok\":true}";
        }
        if (onHeader || win == IntPtr.Zero) return "{\"ok\":true}";

        if (t == "btn" && press && btn == 1 && pageView != IntPtr.Zero)
        {
            // a drop-down list: its options go to the viewer instead of a native menu
            var js = "(function(x,y){var e=document.elementFromPoint(x,y);var s=e&&e.closest&&e.closest('select');" +
                     "if(!s||s.disabled||s.multiple)return '';var id=window.__brainSelN=(window.__brainSelN||0)+1;" +
                     "(window.__brainSels=window.__brainSels||{})[id]=s;s.focus();" +
                     "return JSON.stringify({id:id,index:s.selectedIndex,options:[].map.call(s.options,function(o){return o.text})})})(" +
                     px.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + py.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            Task<string?>? r = null;
            var pv = pageView;
            InvokeOnUi(() => r = MacView.Eval(pv, js, "brain-view"));
            var sel = r is null ? null : await Task.WhenAny(r, Task.Delay(1500)) == r ? r.Result : null;
            if (!string.IsNullOrEmpty(sel))
            {
                _macSelView = pv;
                return "{\"select\":" + sel + "}";
            }
        }

        var target = win;
        InvokeOnUi(() =>
        {
            switch (t)
            {
                case "move":
                    MacView.Mouse(target, lx, ly, _macDown ? 6 : 5, 0, 0);
                    break;
                case "wheel":
                    var n = Math.Max(1, (int)D("n")) * 3;
                    MacView.Scroll(target, lx, ly, btn == 4 ? n : btn == 5 ? -n : 0, btn == 6 ? n : btn == 7 ? -n : 0);
                    break;
                case "btn":
                    if (btn == 3) return; // a right click would open a native menu on the real screen
                    if (press)
                    {
                        MacView.KeyTarget = target == MacMainWindow() ? IntPtr.Zero : target;
                        var now = Environment.TickCount64;
                        _clicks = now - _lastPress < 450 && Math.Abs(x - _pressX) < 5 && Math.Abs(y - _pressY) < 5 ? _clicks + 1 : 1;
                        _lastPress = now; _pressX = x; _pressY = y;
                    }
                    if (btn == 1) _macDown = press;
                    var type = btn == 2 ? (press ? 25 : 26) : (press ? 1 : 2);
                    MacView.Mouse(target, lx, ly, type, _clicks, 0);
                    break;
            }
        });
        return "{\"ok\":true}";
    }

    /// <summary>The window keys go to: the popup last opened or clicked if it is still open, else the browser window.</summary>
    private IntPtr KeyWindow()
    {
        var k = MacView.KeyTarget;
        if (k != IntPtr.Zero && WkPopup.OpenWindows().Any(p => p.Window == k)) return k;
        return MacMainWindow();
    }

    // ---------------------------------------------------------------- text layer

    private async Task<string> MacTextGridAsync(int k, int m, int css)
    {
        Task<string?> barTask = Task.FromResult<string?>(null), pageTask = Task.FromResult<string?>(null);
        var popups = new List<(Task<string?> Text, MacWindow.CGRect Content, MacWindow.CGRect Header, string Title)>();
        int barH = 0, strip = 0;
        InvokeOnUi(() =>
        {
            var win = MacMainWindow();
            if (win == IntPtr.Zero) return;
            var (W, H) = MacCanvas(win);
            var bar = WkBar.BarView;
            if (bar != IntPtr.Zero && (ObjC.SendNInt(bar, ObjC.Sel("isHidden")) & 0xFF) == 0)
            {
                var br = MacView.RectInContent(bar);
                barH = (int)br.H;
                barTask = MacView.Eval(bar, Cli.TextGridJs.Source + $"({k},{m},{(int)br.X},{(int)br.Y},-1)", null);
            }
            var page = Tabs.Active?.NativeView ?? IntPtr.Zero;
            if (page != IntPtr.Zero && ObjC.Send(page, ObjC.Sel("window")) == win)
            {
                var r = MacView.RectInContent(page);
                strip = (int)r.Y;
                pageTask = MacView.Eval(page, Cli.TextGridJs.Source + $"({k},{m},{(int)r.X},{(int)r.Y},{css})", "brain-view");
            }
            foreach (var (pv, pw) in WkPopup.OpenWindows())
            {
                var (pcw, pch) = MacView.ContentSize(pw);
                var place = PopupPlace(W, H, pcw, pch);
                var title = NsStr(ObjC.Send(pv, ObjC.Sel("title"))) ?? "";
                popups.Add((MacView.Eval(pv, Cli.TextGridJs.Source + $"({k},{m},{(int)place.Content.X},{(int)place.Content.Y},{css})", "brain-view"),
                    place.Content, place.Header, title));
            }
        });
        var all = Task.WhenAll(new[] { barTask, pageTask }.Concat(popups.Select(p => p.Text)));
        await Task.WhenAny(all, Task.Delay(4000));
        static string Part(Task<string?> t) => t.IsCompletedSuccessfully && t.Result is { Length: > 0 } s && s[0] == '{' ? s : "null";
        var pops = string.Join(",", popups.Select(p =>
            $"{{\"page\":{Part(p.Text)},\"x\":{(int)p.Header.X},\"y\":{(int)p.Header.Y},\"w\":{(int)p.Content.W},\"h\":{(int)(p.Content.H + PopHeader)}," +
            $"\"viewY\":{(int)p.Content.Y},\"title\":{JsonSerializer.Serialize(p.Title)}}}"));
        return $"{{\"bar\":{Part(barTask)},\"page\":{Part(pageTask)},\"barH\":{barH},\"strip\":{strip},\"popups\":[{pops}]}}";
    }
}
