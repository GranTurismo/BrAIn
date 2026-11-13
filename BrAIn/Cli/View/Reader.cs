using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BrAIn.Cli.View;

/// <summary>
/// The viewer's reader (Ctrl+O): the page as readable text — the same Markdown <c>brain read</c> prints — with every link
/// numbered. Scroll with the arrows, PgUp/PgDn, Space or the wheel; type a link's number and Enter (or click it) to follow it;
/// o or Ctrl+L to type an address; Backspace goes back; r reloads the text. Handy where pictures are coarse (plain terminals)
/// or the connection is slow.
/// </summary>
internal sealed partial class Reader
{
    private sealed record Run(string Text, string Sgr, int Link);

    private List<List<Run>> _lines = [];
    private readonly List<string> _links = [];
    private string _markdown = "";
    private int _top, _cols = 80, _rows = 24;
    private bool _dirty = true, _loading;
    private string _number = "";
    private string? _edit;              // the address being typed (o / Ctrl+L), null when not editing
    private string? _note;
    private readonly List<(int Row, int C0, int C1, int Link)> _hits = [];

    internal string? Prompt => _edit is not null ? "Go to: " + _edit + "▏"
        : _number.Length > 0 ? $"Link {_number} — Enter to follow, Esc to cancel"
        : _loading ? "Loading…" : _note;

    internal void Relayout(int cols, int rows)
    {
        _cols = cols; _rows = rows;
        Wrap();
    }

    internal void Load(Viewer v)
    {
        _loading = true;
        _dirty = true;
        v.Wake();
        Task.Run(() =>
        {
            try
            {
                using var s = McpSession.Open() ?? throw new IOException("BrAIn is not answering");
                var r = s.Call("browser_markdown", new JsonObject { ["maxLength"] = 300000, ["images"] = false });
                _markdown = r.Text;
                _top = 0;
                _note = $"{_links.Count} links · number + Enter follows one · o: address · Backspace: back";
            }
            catch (Exception ex) { _markdown = "Could not read the page: " + ex.Message; }
            _loading = false;
            Wrap();
            _note = $"{_links.Count} links · number+Enter follows · o: address · Backspace: back · r: reload";
            v.Flash(_note!, 5000);
            v.Wake();
        });
    }

    private void Act(Viewer v, Func<McpSession, ToolResult> call)
    {
        _loading = true;
        _dirty = true;
        v.Wake();
        Task.Run(() =>
        {
            try
            {
                using var s = McpSession.Open() ?? throw new IOException("BrAIn is not answering");
                var r = call(s);
                if (r.IsError) v.Flash(r.Text.Split('\n')[0], 4000);
            }
            catch (Exception ex) { v.Flash(ex.Message, 4000); }
            Load(v);
        });
    }

    // ---------------------------------------------------------------- input

    internal void Handle(Ev ev, Viewer v)
    {
        var page = Math.Max(1, _rows - 2);
        switch (ev)
        {
            case MouseEv { Button: 4, Press: true }: Scroll(-3); return;
            case MouseEv { Button: 5, Press: true }: Scroll(3); return;
            case MouseEv { Button: 1, Press: true, Motion: false } m:
                {
                    var (cx, cy) = v.Cell(m);
                    foreach (var h in _hits)
                        if (h.Row == cy && cx >= h.C0 && cx <= h.C1) { Follow(h.Link, v); return; }
                    return;
                }
            case TextEv t when _edit is not null: _edit += t.Text.Replace("\n", ""); _dirty = true; return;
            case KeyEv k when _edit is not null:
                if (k.Keysym == InputParser.Return && _edit.Trim().Length > 0)
                {
                    var url = _edit.Trim();
                    _edit = null;
                    Act(v, s => s.Call("browser_navigate", new JsonObject { ["url"] = url }));
                }
                else if (k.Keysym == InputParser.Escape) _edit = null;
                else if (k.Keysym == InputParser.BackSpace && _edit.Length > 0) _edit = _edit[..^1];
                _dirty = true;
                return;
            case TextEv t when t.Text.All(char.IsAsciiDigit): _number += t.Text; _dirty = true; return;
            case TextEv t:
                switch (t.Text)
                {
                    case "j": Scroll(1); break;
                    case "k": Scroll(-1); break;
                    case " ": Scroll(page); break;
                    case "b": Scroll(-page); break;
                    case "g": _top = 0; _dirty = true; break;
                    case "G": _top = Math.Max(0, _lines.Count - _rows); _dirty = true; break;
                    case "o": _edit = v.Url; _dirty = true; break;
                    case "r": Load(v); break;
                }
                return;
            case KeyEv k:
                {
                    var ctrl = k.Mods.HasFlag(Mods.Ctrl);
                    if (ctrl && k.Keysym == 'l') { _edit = v.Url; _dirty = true; return; }
                    switch (k.Keysym)
                    {
                        case InputParser.Down: Scroll(1); break;
                        case InputParser.Up: Scroll(-1); break;
                        case InputParser.PageDown: Scroll(page); break;
                        case InputParser.PageUp: Scroll(-page); break;
                        case InputParser.Home: _top = 0; _dirty = true; break;
                        case InputParser.End: _top = Math.Max(0, _lines.Count - _rows); _dirty = true; break;
                        case InputParser.Return when _number.Length > 0:
                            if (int.TryParse(_number, out var n)) Follow(n, v);
                            _number = "";
                            break;
                        case InputParser.BackSpace when _number.Length > 0: _number = _number[..^1]; _dirty = true; break;
                        case InputParser.BackSpace: Act(v, s => s.Call("browser_back")); break;
                        case InputParser.Escape when _number.Length > 0: _number = ""; _dirty = true; break;
                        case InputParser.Escape: v.SetTextMode(false); break;
                    }
                    return;
                }
        }
    }

    private void Follow(int n, Viewer v)
    {
        if (n < 1 || n > _links.Count) { v.Flash($"There is no link {n}", 2500); return; }
        var url = _links[n - 1];
        v.Flash("Opening " + url, 3000);
        Act(v, s => s.Call("browser_navigate", new JsonObject { ["url"] = url }));
    }

    private void Scroll(int by)
    {
        var t = Math.Clamp(_top + by, 0, Math.Max(0, _lines.Count - _rows));
        if (t != _top) { _top = t; _dirty = true; }
    }

    // ---------------------------------------------------------------- layout and drawing

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]*)(?:\s+""[^""]*"")?\)|\[([^\]]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)|\*\*([^*]+)\*\*|`([^`]+)`")]
    private static partial Regex Inline();

    /// <summary>Markdown → styled runs, wrapped to the width. Links become "text[n]".</summary>
    private void Wrap()
    {
        _links.Clear();
        var lines = new List<List<Run>>();
        var inCode = false;
        foreach (var raw in _markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```")) { inCode = !inCode; continue; }
            if (!inCode && line.Contains('|'))
            {
                // tables read as rows of cells: unescape \|, drop the | --- | separators and empty cells
                line = Regex.Replace(line, @"\\+\|", "|");
                line = Regex.Replace(line, @"\|\s*:?-{3,}:?\s*(?=\|)", "|");
                line = Regex.Replace(line, @"(\|\s*){2,}", "| ").Trim().Trim('|').Trim();
                if (line.Length == 0) continue;
            }
            var runs = new List<Run>();
            if (inCode) runs.Add(new Run(line, "\e[2m", 0));
            else
            {
                var baseSgr = "";
                var h = Regex.Match(line, "^(#{1,6})\\s+(.*)$");
                if (h.Success) { line = h.Groups[2].Value; baseSgr = h.Groups[1].Length <= 2 ? "\e[1;4m" : "\e[1m"; }
                else if (line.StartsWith("> ")) { line = "│ " + line[2..]; baseSgr = "\e[3m"; }
                var pos = 0;
                foreach (Match m in Inline().Matches(line))
                {
                    if (m.Index > pos) runs.Add(new Run(line[pos..m.Index], baseSgr, 0));
                    if (m.Groups[2].Success && m.Groups[1].Success && m.Value.StartsWith('!'))
                        runs.Add(new Run($"[image{(m.Groups[1].Value.Length > 0 ? ": " + m.Groups[1].Value : "")}]", "\e[2m", 0));
                    else if (m.Groups[4].Success)
                    {
                        _links.Add(m.Groups[4].Value);
                        var n = _links.Count;
                        runs.Add(new Run(m.Groups[3].Value.Length > 0 ? m.Groups[3].Value : m.Groups[4].Value, baseSgr + "\e[4;36m", n));
                        runs.Add(new Run($"[{n}]", "\e[33m", n));
                    }
                    else if (m.Groups[5].Success) runs.Add(new Run(m.Groups[5].Value, baseSgr + "\e[1m", 0));
                    else if (m.Groups[6].Success) runs.Add(new Run(m.Groups[6].Value, baseSgr + "\e[2m", 0));
                    pos = m.Index + m.Length;
                }
                if (pos < line.Length) runs.Add(new Run(line[pos..], baseSgr, 0));
            }
            lines.AddRange(WrapRuns(runs, Math.Max(10, _cols - 2)));
        }
        _lines = lines;
        _top = Math.Clamp(_top, 0, Math.Max(0, _lines.Count - _rows));
        _dirty = true;
    }

    private static IEnumerable<List<Run>> WrapRuns(List<Run> runs, int width)
    {
        var cur = new List<Run>();
        var used = 0;
        if (runs.Count == 0) { yield return cur; yield break; }
        foreach (var r in runs)
        {
            var text = r.Text;
            while (text.Length > 0)
            {
                var room = width - used;
                if (Width(text) <= room) { cur.Add(r with { Text = text }); used += Width(text); break; }
                // break at the last space that fits, else hard
                var cut = Fit(text, room);
                var sp = text.LastIndexOf(' ', Math.Max(0, cut - 1), Math.Max(0, cut));
                if (sp > 0 && cut < text.Length) cut = sp + 1;
                if (cut <= 0 && used > 0) { yield return cur; cur = []; used = 0; continue; }
                if (cut <= 0) cut = Math.Max(1, Fit(text, width));
                cur.Add(r with { Text = text[..cut].TrimEnd() });
                yield return cur;
                cur = []; used = 0;
                text = text[cut..].TrimStart();
            }
        }
        yield return cur;
    }

    private static int Fit(string s, int cols)
    {
        var w = 0;
        for (var i = 0; i < s.Length; i++)
        {
            w += CharWidth(s[i]);
            if (w > cols) return i;
        }
        return s.Length;
    }

    internal static int Width(string s) { var w = 0; foreach (var c in s) w += CharWidth(c); return w; }

    private static int CharWidth(char c)
        => char.IsLowSurrogate(c) ? 0
         : c is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣' or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦'
             || char.IsHighSurrogate(c) ? 2 : 1;

    private string _drawnPrompt = "";

    internal void Render(Term t)
    {
        var prompt = Prompt ?? "";
        if (!_dirty && prompt == _drawnPrompt) return;
        _drawnPrompt = prompt;
        if (!_dirty) return; // only the status line changed: the viewer redraws it
        _dirty = false;
        _hits.Clear();
        var sb = new StringBuilder();
        for (var row = 0; row < _rows; row++)
        {
            sb.Append($"\e[{row + 1};1H\e[0m\e[2K ");
            var i = _top + row;
            if (i >= _lines.Count) continue;
            var col = 2;
            foreach (var r in _lines[i])
            {
                sb.Append("\e[0m").Append(r.Sgr).Append(r.Text);
                var w = Width(r.Text);
                if (r.Link > 0) _hits.Add((row + 1, col, col + w - 1, r.Link));
                col += w;
            }
        }
        sb.Append("\e[0m");
        if (_lines.Count > _rows)
        {
            // a scroll position marker in the last column
            var pos = (int)((long)_top * (_rows - 1) / Math.Max(1, _lines.Count - _rows));
            sb.Append($"\e[{pos + 1};{_cols}H\e[2m█\e[0m");
        }
        t.Put(sb.ToString());
    }
}
