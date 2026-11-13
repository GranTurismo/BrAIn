using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BrAIn.Cli.View;

/// <summary>Where the viewer's picture comes from and where its input goes: an X screen on Linux (read and typed into
/// directly, <see cref="X11Screen"/>), the browser itself on macOS (<see cref="MacScreen"/>).</summary>
internal interface IScreen : IDisposable
{
    bool Capture(Frame f);
    void Move(int x, int y);
    /// <summary>Returns what the browser answered when it is more than "done" (macOS: a drop-down list's options).</summary>
    string? Button(int x, int y, uint button, bool press);
    void Wheel(int x, int y, uint button, int notches);
    void Key(nuint keysym, bool ctrl, bool alt, bool shift, bool super);
    void Type(string text);
    /// <summary>Copying out of the browser needs a bridge (a remote X screen); on the same Mac it shares the clipboard.</summary>
    bool WatchClipboard();
    bool ClipboardChanged();
    string? ReadClipboard();
    /// <summary>A key the terminal's Ctrl+letter becomes: Ctrl on Linux, Command on a Mac (said in the status line).</summary>
    string CtrlName { get; }
}

/// <summary>Linux: the browser's invisible X screen — one connection captures (the viewer's main thread), the other plays
/// input (its input thread), because Xlib connections are not shared between threads.</summary>
internal sealed class X11Screen(X11 cap, X11 inp) : IScreen
{
    public bool Capture(Frame f) => cap.Capture(f, 0, 0);
    public void Move(int x, int y) => inp.Move(x, y);

    public string? Button(int x, int y, uint button, bool press)
    {
        // no window manager on that screen: the window under the click (a popup, the browser) gets the keyboard first
        if (press) ControlClient.Ask($"focusat {x} {y}", TimeSpan.FromSeconds(2));
        inp.Button(x, y, button, press);
        return null;
    }

    public void Wheel(int x, int y, uint button, int notches) => inp.Wheel(x, y, button, notches);
    public void Key(nuint keysym, bool ctrl, bool alt, bool shift, bool super) => inp.Key(keysym, ctrl, alt, shift, super);
    public void Type(string text) => inp.Type(text);
    public bool WatchClipboard() => cap.WatchClipboard();
    public bool ClipboardChanged() => cap.ClipboardChanged();
    public string? ReadClipboard() => cap.ReadClipboard();
    public string CtrlName => "Ctrl";

    public void Dispose()
    {
        try { inp.Dispose(); } catch { }
        try { cap.Dispose(); } catch { }
    }
}

/// <summary>macOS: the frame is made inside BrAIn (it may look into its own windows; this process may not) and fetched over the
/// control socket; input is sent there as small JSON events. One connection per request — they are local and short.</summary>
internal sealed class MacScreen : IScreen
{
    public bool Capture(Frame f)
    {
        using var s = ControlClient.TryConnect();
        if (s is null) return false;
        s.ReceiveTimeout = 8000;
        using var ns = new NetworkStream(s);
        ns.Write("frame\n"u8);
        var head = ReadLine(ns);
        if (head is null) return false;
        var j = JsonDocument.Parse(head).RootElement;
        int w = j.GetProperty("w").GetInt32(), h = j.GetProperty("h").GetInt32(), len = j.GetProperty("len").GetInt32();
        if (len <= 0) return false;
        var buf = len == f.Px.Length && w == f.W && h == f.H ? f.Px : new byte[len];
        var got = 0;
        while (got < len)
        {
            var n = ns.Read(buf, got, len - got);
            if (n <= 0) return false;
            got += n;
        }
        if (!ReferenceEquals(buf, f.Px))
        {
            // the window did not take the size yet: copy what overlaps
            for (var y = 0; y < Math.Min(h, f.H); y++)
                Buffer.BlockCopy(buf, y * w * 4, f.Px, y * f.Stride, Math.Min(w, f.W) * 4);
        }
        return true;
    }

    private static string? ReadLine(Stream s)
    {
        var b = new List<byte>(64);
        var one = new byte[1];
        while (b.Count < 4096)
        {
            if (s.Read(one, 0, 1) <= 0) return null;
            if (one[0] == '\n') return Encoding.UTF8.GetString(b.ToArray());
            b.Add(one[0]);
        }
        return null;
    }

    private static string? Send(JsonObject ev)
    {
        var r = ControlClient.Ask("input " + ev.ToJsonString(), TimeSpan.FromSeconds(4));
        return r?.GetRawText();
    }

    public void Move(int x, int y) => Send(new JsonObject { ["t"] = "move", ["x"] = x, ["y"] = y });

    public string? Button(int x, int y, uint button, bool press)
    {
        var r = Send(new JsonObject { ["t"] = "btn", ["x"] = x, ["y"] = y, ["b"] = button, ["p"] = press });
        return r is not null && r.Contains("\"select\"", StringComparison.Ordinal) ? r : null;
    }

    public void Wheel(int x, int y, uint button, int notches)
        => Send(new JsonObject { ["t"] = "wheel", ["x"] = x, ["y"] = y, ["b"] = button, ["n"] = notches });

    public void Key(nuint keysym, bool ctrl, bool alt, bool shift, bool super)
        => Send(new JsonObject { ["t"] = "key", ["ks"] = (long)keysym, ["ctrl"] = ctrl, ["alt"] = alt, ["shift"] = shift, ["sup"] = super });

    public void Type(string text) => Send(new JsonObject { ["t"] = "text", ["s"] = text });

    internal static void PickOption(long id, int index)
        => Send(new JsonObject { ["t"] = "selectset", ["id"] = id, ["index"] = index });

    public bool WatchClipboard() => false;  // same Mac, same clipboard: Cmd+C in the browser already lands on it
    public bool ClipboardChanged() => false;
    public string? ReadClipboard() => null;
    public string CtrlName => "⌘";
    public void Dispose() { }
}
