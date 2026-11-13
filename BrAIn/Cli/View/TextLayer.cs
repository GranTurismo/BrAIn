using System.Text.Json;

namespace BrAIn.Cli.View;

/// <summary>One answer of the browser's text layer (see TextGridJs): runs of text at terminal cells, page first, then the
/// toolbar (drawn over it). Page text under the toolbar's opaque popups (menus, suggestions, the palette) is left out, so the
/// two never mix.</summary>
internal sealed class TextLayer
{
    /// <summary>Group: runs in the same clipping box (laid out one after another up to GroupEnd, the box's right edge cell).</summary>
    internal readonly record struct Run(int Row, int Col, string Text, int Fg, int Flags, int Max, bool Field, bool Page, int Group, int GroupEnd,
        int Level = 0);

    internal readonly List<Run> Runs = [];
    /// <summary>The toolbar's opaque popups, in cells (col0,row0,col1,row1 exclusive): page text is not drawn under them.</summary>
    internal readonly List<(int C0, int R0, int C1, int R1)> Covers = [];
    /// <summary>Popup windows, in cells: nothing of the page or the toolbar shows under them.</summary>
    internal readonly List<(int C0, int R0, int C1, int R1)> Popups = [];
    private int _level;
    internal (int Row, int Col)? Caret;
    internal bool Grid;
    private bool _caretFocused;

    /// <summary>Flags: 1 bold, 2 italic, 4 underline, 8 dim.</summary>
    private int _k = 8;

    internal static TextLayer? Parse(JsonElement? reply, int m, int k = 8)
    {
        if (reply is not { ValueKind: JsonValueKind.Object } o || o.TryGetProperty("error", out _)) return null;
        var t = new TextLayer { _k = Math.Max(1, k) };
        var strip = o.TryGetProperty("strip", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : 0;
        // page text starts under the toolbar strip; the toolbar's popups hide it where they are (Covers)
        if (o.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object)
            t.Add(page, minRow: strip <= 0 ? 0 : (strip + m - 1) / m, isPage: true);
        if (o.TryGetProperty("bar", out var bar) && bar.ValueKind == JsonValueKind.Object)
        {
            t._level = 1;
            t.Add(bar, minRow: 0, isPage: false);
            if (bar.TryGetProperty("occ", out var occ) && occ.ValueKind == JsonValueKind.Array)
                foreach (var b in occ.EnumerateArray())
                    if (b.GetArrayLength() == 4) t.Covers.Add((b[0].GetInt32(), b[1].GetInt32(), b[2].GetInt32(), b[3].GetInt32()));
        }
        // popup windows (sign-in, payment) lie over everything: their title bar (title and a ✕ over GTK's close button) and page
        if (o.TryGetProperty("popups", out var pops) && pops.ValueKind == JsonValueKind.Array)
            foreach (var pw in pops.EnumerateArray())
            {
                int I(string name) => pw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                int x = I("x"), y = I("y"), w = I("w"), h = I("h"), viewY = I("viewY");
                if (w <= 0 || h <= 0) continue;
                var cw = t._k;
                int c0 = x / cw, r0 = y / m, c1 = (x + w + cw - 1) / cw, r1 = (y + h + m - 1) / m;
                t.Popups.Add((c0, r0, c1, r1));
                t._level = 2;
                var title = pw.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                var headerRow = (y + Math.Max(m, viewY - y) / 2) / m;
                if (viewY - y >= m / 2 && headerRow < r1)
                {
                    var room = Math.Max(1, c1 - c0 - 6);
                    if (title.Length > room) title = title[..Math.Max(1, room - 1)] + "…";
                    t.Runs.Add(new Run(headerRow, c0 + 1, title, 0xf2f2f2, 1, room, false, false, -1, int.MaxValue, 2));
                    t.Runs.Add(new Run(headerRow, Math.Max(c0, c1 - 3), "✕", 0xf2f2f2, 1, 1, false, false, -1, int.MaxValue, 2));
                }
                if (pw.TryGetProperty("page", out var pp) && pp.ValueKind == JsonValueKind.Object)
                    t.Add(pp, minRow: (viewY + m / 2) / m, isPage: false);
            }
        return t;
    }

    private void Add(JsonElement part, int minRow, bool isPage, int maxRow = int.MaxValue)
    {
        if (part.TryGetProperty("grid", out var g) && g.ValueKind == JsonValueKind.True) Grid = true;
        var styles = new List<(int Rgb, int Flags)>();
        if (part.TryGetProperty("styles", out var st) && st.ValueKind == JsonValueKind.Array)
            foreach (var s in st.EnumerateArray())
            {
                var a = s.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                styles.Add(a.Length >= 4 ? ((a[0] & 255) << 16 | (a[1] & 255) << 8 | (a[2] & 255), a[3]) : (0xcccccc, 0));
            }
        if (part.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            foreach (var r in runs.EnumerateArray())
            {
                if (r.GetArrayLength() < 5) continue;
                var row = r[0].GetInt32();
                if (row < minRow || row > maxRow) continue;
                var si = r[3].GetInt32();
                var (rgb, fl) = si >= 0 && si < styles.Count ? styles[si] : (0xcccccc, 0);
                var len = r.GetArrayLength();
                Runs.Add(new Run(row, r[1].GetInt32(), r[2].GetString() ?? "", rgb, fl, r[4].GetInt32(),
                    len > 5 && r[5].GetInt32() == 1, isPage, len > 6 ? r[6].GetInt32() + _level * 100000 : -1,
                    len > 7 ? r[7].GetInt32() : int.MaxValue, _level));
            }
        // the caret of the document that has the keyboard; the page's when neither says so (no window manager to tell)
        if (part.TryGetProperty("caret", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 2)
        {
            var row = c[0].GetInt32();
            var focused = part.TryGetProperty("focus", out var fo) && fo.ValueKind == JsonValueKind.True;
            if (row >= minRow && row <= maxRow && (focused || (isPage && Caret is null))) { Caret = (row, c[1].GetInt32()); _caretFocused = focused; }
            else if (_level == 2 && !focused && _caretFocused == false && Caret is null) Caret = (row, c[1].GetInt32());
        }
        else if (!isPage && _caretFocused == false && part.TryGetProperty("focus", out var bf) && bf.ValueKind == JsonValueKind.True)
            Caret = null; // the toolbar has the keyboard but no caret: the page's caret is not where typing goes
    }
}
