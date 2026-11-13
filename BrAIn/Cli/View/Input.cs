using System.Text;

namespace BrAIn.Cli.View;

[Flags]
internal enum Mods { None = 0, Shift = 1, Alt = 2, Ctrl = 4, Super = 8 }

internal abstract record Ev;
/// <summary>A key with an X keysym (named keys, and letters pressed with Ctrl/Alt).</summary>
internal sealed record KeyEv(nuint Keysym, Mods Mods) : Ev;
/// <summary>Plain text typed or pasted.</summary>
internal sealed record TextEv(string Text, bool Pasted = false) : Ev;
/// <summary>Mouse: Button 1-3 (0 = none, for motion), wheel 4-7; X/Y are cells (1-based) or pixels when Pixel is set.</summary>
internal sealed record MouseEv(int Button, bool Press, bool Motion, int X, int Y, Mods Mods, bool Pixel) : Ev;
internal sealed record FocusEv(bool In) : Ev;

/// <summary>
/// Turns the bytes a terminal sends into keys, text and mouse events: UTF-8 text, control characters (Ctrl+letter), xterm
/// CSI/SS3 key sequences with modifiers, the kitty keyboard protocol (CSI … u), SGR mouse reports (also in pixels, 1016),
/// bracketed paste and focus reports. A lone ESC is the Escape key once nothing follows it within a moment.
/// </summary>
internal sealed class InputParser
{
    private readonly List<byte> _buf = [];
    private readonly StringBuilder _paste = new();
    private bool _pasting;
    internal bool PixelMouse;

    // X keysyms
    internal const nuint Return = 0xff0d, Tab = 0xff09, BackSpace = 0xff08, Escape = 0xff1b, Delete = 0xffff, Insert = 0xff63,
        Home = 0xff50, Left = 0xff51, Up = 0xff52, Right = 0xff53, Down = 0xff54, PageUp = 0xff55, PageDown = 0xff56, End = 0xff57,
        F1 = 0xffbe, Space = 0x20;

    /// <summary>Feeds bytes; returns the complete events. <paramref name="flushEscape"/>: a pending lone ESC is the Escape key.</summary>
    internal List<Ev> Feed(ReadOnlySpan<byte> data, bool flushEscape = false)
    {
        _buf.AddRange(data.ToArray());
        var evs = new List<Ev>();
        var i = 0;
        var text = new StringBuilder();
        void FlushText() { if (text.Length > 0) { evs.Add(new TextEv(text.ToString())); text.Clear(); } }
        while (i < _buf.Count)
        {
            var b = _buf[i];
            if (_pasting)
            {
                // everything up to ESC [ 201 ~ is pasted text
                var end = IndexOf(_buf, "\e[201~"u8, i);
                if (end < 0) { _paste.Append(Encoding.UTF8.GetString(_buf.GetRange(i, _buf.Count - i).ToArray())); i = _buf.Count; break; }
                _paste.Append(Encoding.UTF8.GetString(_buf.GetRange(i, end - i).ToArray()));
                evs.Add(new TextEv(_paste.ToString(), Pasted: true));
                _paste.Clear();
                _pasting = false;
                i = end + 6;
                continue;
            }
            if (b == 0x1b)
            {
                if (i + 1 >= _buf.Count)
                {
                    if (!flushEscape) break; // wait: the rest of a sequence may still come
                    FlushText();
                    evs.Add(new KeyEv(Escape, Mods.None));
                    i++;
                    continue;
                }
                var n = _buf[i + 1];
                if (n == '[')
                {
                    var (len, ev) = Csi(i);
                    if (len == 0) break; // incomplete
                    FlushText();
                    if (ev is not null) evs.Add(ev);
                    i += len;
                    continue;
                }
                if (n == 'O')
                {
                    if (i + 2 >= _buf.Count) break;
                    FlushText();
                    var f = _buf[i + 2];
                    nuint? ks = f switch { (byte)'A' => Up, (byte)'B' => Down, (byte)'C' => Right, (byte)'D' => Left, (byte)'H' => Home, (byte)'F' => End,
                        (byte)'P' => F1, (byte)'Q' => F1 + 1, (byte)'R' => F1 + 2, (byte)'S' => F1 + 3, (byte)'M' => Return, _ => null };
                    if (ks is { } k) evs.Add(new KeyEv(k, Mods.None));
                    i += 3;
                    continue;
                }
                if (n is (byte)'P' or (byte)']' or (byte)'_' or (byte)'^')
                {
                    // a string the terminal sent (an answer): skip to ST (ESC \) or BEL
                    var st = i + 2;
                    while (st < _buf.Count && _buf[st] != 0x07 && !(_buf[st] == 0x1b && st + 1 < _buf.Count && _buf[st + 1] == '\\')) st++;
                    if (st >= _buf.Count) break;
                    i = _buf[st] == 0x07 ? st + 1 : st + 2;
                    continue;
                }
                // ESC + key = Alt+key
                FlushText();
                var (len2, ev2) = Plain(i + 1);
                if (len2 == 0) break;
                if (ev2 is KeyEv ke) evs.Add(ke with { Mods = ke.Mods | Mods.Alt });
                else if (ev2 is TextEv te) evs.Add(new KeyEv(X11.KeysymFor(char.ConvertToUtf32(te.Text, 0)), Mods.Alt));
                i += 1 + len2;
                continue;
            }
            var (l, e) = Plain(i);
            if (l == 0) break;
            if (e is TextEv t2) text.Append(t2.Text);
            else { FlushText(); if (e is not null) evs.Add(e); }
            i += l;
        }
        FlushText();
        _buf.RemoveRange(0, i);
        return evs;
    }

    internal bool Pending => _buf.Count > 0;

    /// <summary>One byte or UTF-8 character outside an escape sequence.</summary>
    private (int, Ev?) Plain(int i)
    {
        var b = _buf[i];
        switch (b)
        {
            case 0x0d: case 0x0a: return (1, new KeyEv(Return, Mods.None));
            case 0x09: return (1, new KeyEv(Tab, Mods.None));
            case 0x7f: case 0x08: return (1, new KeyEv(BackSpace, Mods.None));
            case 0x00: return (1, new KeyEv(Space, Mods.Ctrl));
        }
        if (b < 0x1b) return (1, new KeyEv((nuint)('a' + b - 1), Mods.Ctrl));
        if (b < 0x20) return (1, new KeyEv((nuint)("\\]^_"[b - 0x1c]), Mods.Ctrl));
        var len = b < 0x80 ? 1 : b >= 0xf0 ? 4 : b >= 0xe0 ? 3 : b >= 0xc0 ? 2 : 1;
        if (i + len > _buf.Count) return (0, null);
        return (len, new TextEv(Encoding.UTF8.GetString(_buf.GetRange(i, len).ToArray())));
    }

    /// <summary>ESC [ params final. Returns its length (0 = not complete yet) and the event (null = ignored).</summary>
    private (int, Ev?) Csi(int i)
    {
        var j = i + 2;
        while (j < _buf.Count && (_buf[j] < 0x40 || _buf[j] > 0x7e)) j++;
        if (j >= _buf.Count) return (0, null);
        var len = j - i + 1;
        var body = Encoding.ASCII.GetString(_buf.GetRange(i + 2, j - i - 2).ToArray());
        var fin = (char)_buf[j];
        // SGR mouse: ESC [ < b ; x ; y M/m
        if (body.StartsWith('<') && fin is 'M' or 'm')
        {
            var p = body[1..].Split(';');
            if (p.Length < 3 || !int.TryParse(p[0], out var cb) || !int.TryParse(p[1], out var x) || !int.TryParse(p[2], out var y)) return (len, null);
            var mods = ((cb & 4) != 0 ? Mods.Shift : 0) | ((cb & 8) != 0 ? Mods.Alt : 0) | ((cb & 16) != 0 ? Mods.Ctrl : 0);
            var motion = (cb & 32) != 0;
            int btn;
            if ((cb & 64) != 0) btn = 4 + (cb & 3);                  // wheel: 4 up, 5 down, 6 left, 7 right
            else if ((cb & 128) != 0) btn = 8 + (cb & 3);            // back / forward
            else btn = (cb & 3) == 3 ? 0 : (cb & 3) + 1;             // 1 left, 2 middle, 3 right
            if (btn == 2) btn = 2; else if (btn == 3) btn = 3;
            return (len, new MouseEv(btn, fin == 'M', motion, x, y, mods, PixelMouse));
        }
        if (fin == 'I' && body.Length == 0) return (len, new FocusEv(true));
        if (fin == 'O' && body.Length == 0) return (len, new FocusEv(false));
        if (body.StartsWith('?') || body.StartsWith('>') || body.Contains('$')) return (len, null); // answers to our questions
        var ps = body.Split(';');
        int P(int k, int def) => k < ps.Length && int.TryParse(ps[k].Split(':')[0], out var v) ? v : def;
        var m = ModsOf(P(1, 1));
        switch (fin)
        {
            case 'A': return (len, new KeyEv(Up, m));
            case 'B': return (len, new KeyEv(Down, m));
            case 'C': return (len, new KeyEv(Right, m));
            case 'D': return (len, new KeyEv(Left, m));
            case 'H': return (len, new KeyEv(Home, m));
            case 'F': return (len, new KeyEv(End, m));
            case 'Z': return (len, new KeyEv(Tab, Mods.Shift));
            case 'P': return (len, new KeyEv(F1, m));
            case 'Q': return (len, new KeyEv(F1 + 1, m));
            case 'S': return (len, new KeyEv(F1 + 3, m));
            case '~':
                {
                    var code = P(0, 0);
                    if (code == 200) { _pasting = true; return (len, null); }
                    if (code == 201) return (len, null);
                    nuint? ks = code switch
                    {
                        1 or 7 => Home, 2 => Insert, 3 => Delete, 4 or 8 => End, 5 => PageUp, 6 => PageDown,
                        11 => F1, 12 => F1 + 1, 13 => F1 + 2, 14 => F1 + 3, 15 => F1 + 4, 17 => F1 + 5, 18 => F1 + 6, 19 => F1 + 7,
                        20 => F1 + 8, 21 => F1 + 9, 23 => F1 + 10, 24 => F1 + 11, _ => null,
                    };
                    return (len, ks is { } k ? new KeyEv(k, m) : null);
                }
            case 'u':
                {
                    // kitty keyboard protocol: ESC [ code ; mods u
                    var code = P(0, 0);
                    nuint ks = code switch
                    {
                        13 => Return, 9 => Tab, 127 => BackSpace, 27 => Escape, 8 => BackSpace,
                        57399 or 57400 or 57401 or 57402 or 57403 or 57404 or 57405 or 57406 or 57407 or 57408 => (nuint)('0' + code - 57399),
                        57414 => Return,
                        >= 57344 => 0,   // modifier keys and other functional keys on their own: nothing to send
                        _ => X11.KeysymFor(code),
                    };
                    if (ks == 0) return (len, null);
                    // plain text keys with only Shift arrive as text normally; keep them as text here too
                    if (code is >= 32 and < 57344 && (m & ~Mods.Shift) == 0)
                        return (len, new TextEv(char.ConvertFromUtf32(m.HasFlag(Mods.Shift) && code < 128 ? char.ToUpperInvariant((char)code) : code)));
                    return (len, new KeyEv(ks, m));
                }
        }
        return (len, null);
    }

    private static Mods ModsOf(int p)
    {
        var v = Math.Max(0, p - 1);
        return ((v & 1) != 0 ? Mods.Shift : 0) | ((v & 2) != 0 ? Mods.Alt : 0) | ((v & 4) != 0 ? Mods.Ctrl : 0) | ((v & 8) != 0 ? Mods.Super : 0);
    }

    private static int IndexOf(List<byte> buf, ReadOnlySpan<byte> pat, int from)
    {
        for (var i = from; i + pat.Length <= buf.Count; i++)
        {
            var ok = true;
            for (var k = 0; k < pat.Length && ok; k++) ok = buf[i + k] == pat[k];
            if (ok) return i;
        }
        return -1;
    }
}
