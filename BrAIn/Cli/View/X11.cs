using System.Runtime.InteropServices;

namespace BrAIn.Cli.View;

/// <summary>
/// The viewer's link to the browser's invisible screen: it reads the screen's pixels (XGetImage) and plays the user's mouse
/// and keyboard into it as real input (the XTEST extension), exactly as if a mouse and keyboard were plugged into that
/// screen. So everything works as it does on a desktop — the page, BrAIn's toolbar, tabs, menus and GTK dialogs — and
/// clicks are trusted user gestures for the page.
///
/// One connection per thread (Xlib is not used from two threads at once): the viewer opens one to capture and one for input.
/// </summary>
internal sealed unsafe partial class X11 : IDisposable
{
    private const string LibX = "libX11.so.6", LibXtst = "libXtst.so.6";

    [LibraryImport(LibX)] private static partial int XInitThreads();
    [LibraryImport(LibX, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr XOpenDisplay(string? name);
    [LibraryImport(LibX)] private static partial int XCloseDisplay(IntPtr dpy);
    [LibraryImport(LibX)] private static partial IntPtr XDefaultRootWindow(IntPtr dpy);
    [LibraryImport(LibX)] private static partial int XDefaultScreen(IntPtr dpy);
    [LibraryImport(LibX)] private static partial IntPtr XGetImage(IntPtr dpy, IntPtr drawable, int x, int y, uint w, uint h, nuint planes, int format);
    [LibraryImport(LibX)] private static partial int XFlush(IntPtr dpy);
    [LibraryImport(LibX)] private static partial int XSync(IntPtr dpy, int discard);
    [LibraryImport(LibX)] private static partial int XFree(IntPtr p);
    [LibraryImport(LibX)] private static partial int XDisplayKeycodes(IntPtr dpy, out int min, out int max);
    [LibraryImport(LibX)] private static partial IntPtr XGetKeyboardMapping(IntPtr dpy, byte first, int count, out int perCode);
    [LibraryImport(LibX)] private static partial int XChangeKeyboardMapping(IntPtr dpy, int first, int perCode, nuint* syms, int count);
    [LibraryImport(LibX)] private static partial int XSetErrorHandler(IntPtr handler);

    [LibraryImport(LibXtst)] private static partial int XTestQueryExtension(IntPtr dpy, out int ev, out int err, out int maj, out int min);
    [LibraryImport(LibXtst)] private static partial int XTestFakeMotionEvent(IntPtr dpy, int screen, int x, int y, nuint delay);
    [LibraryImport(LibXtst)] private static partial int XTestFakeButtonEvent(IntPtr dpy, uint button, int press, nuint delay);
    [LibraryImport(LibXtst)] private static partial int XTestFakeKeyEvent(IntPtr dpy, uint keycode, int press, nuint delay);

    private IntPtr _dpy, _root;
    private int _screen;

    private X11() { }

    private static bool _init;

    /// <summary>Connects to the browser's display with its cookie. Null with a reason when it cannot.</summary>
    internal static X11? Open(string display, string? xauthority, out string? why)
    {
        why = null;
        try
        {
            if (!_init) { XInitThreads(); _init = true; IgnoreErrors(); }
            if (!string.IsNullOrEmpty(xauthority)) Posix.SetEnv("XAUTHORITY", xauthority);
            var dpy = XOpenDisplay(display);
            if (dpy == IntPtr.Zero) { why = $"cannot open the browser's screen {display}"; return null; }
            return new X11 { _dpy = dpy, _root = XDefaultRootWindow(dpy), _screen = XDefaultScreen(dpy) };
        }
        catch (DllNotFoundException ex) { why = "the X11 libraries are missing (" + ex.Message + ")"; return null; }
    }

    /// <summary>Xlib's default error handler ends the process on any protocol error (say, a window that closed while we
    /// asked about it). Errors are not fatal for a viewer.</summary>
    private static void IgnoreErrors()
    {
        try { XSetErrorHandler((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&OnError); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnError(IntPtr dpy, IntPtr ev) => 0;

    internal bool HasXTest()
    {
        try { return XTestQueryExtension(_dpy, out _, out _, out _, out _) != 0; }
        catch (DllNotFoundException) { return false; }
    }

    // ---------------------------------------------------------------- capture

    /// <summary>Copies the screen rectangle at (x,y) of the frame's size into the frame. False when the server refused.</summary>
    internal bool Capture(Frame f, int x, int y)
    {
        var img = XGetImage(_dpy, _root, x, y, (uint)f.W, (uint)f.H, nuint.MaxValue, 2 /* ZPixmap */);
        if (img == IntPtr.Zero) return false;
        try
        {
            // XImage (LP64): width 0, height 4, ..., data 16, byte_order 24, ..., bytes_per_line 44, bits_per_pixel 48,
            // f.destroy_image 96
            var p = (byte*)img;
            var data = *(byte**)(p + 16);
            var bpl = *(int*)(p + 44);
            var bpp = *(int*)(p + 48);
            if (bpp != 32 || data == null) return false;
            fixed (byte* dst = f.Px)
                for (var row = 0; row < f.H; row++)
                    Buffer.MemoryCopy(data + (long)row * bpl, dst + (long)row * f.Stride, f.Stride, f.Stride);
            return true;
        }
        finally
        {
            var destroy = *(delegate* unmanaged[Cdecl]<IntPtr, int>*)((byte*)img + 96);
            destroy(img);
        }
    }

    // ---------------------------------------------------------------- pointer

    internal void Move(int x, int y)
    {
        XTestFakeMotionEvent(_dpy, _screen, x, y, 0);
        XFlush(_dpy);
    }

    /// <summary>X button numbers: 1 left, 2 middle, 3 right, 4/5 wheel up/down, 6/7 wheel left/right, 8/9 back/forward.</summary>
    internal void Button(int x, int y, uint button, bool press)
    {
        XTestFakeMotionEvent(_dpy, _screen, x, y, 0);
        XTestFakeButtonEvent(_dpy, button, press ? 1 : 0, 0);
        XFlush(_dpy);
    }

    internal void Wheel(int x, int y, uint button, int notches = 1)
    {
        XTestFakeMotionEvent(_dpy, _screen, x, y, 0);
        for (var i = 0; i < notches; i++)
        {
            XTestFakeButtonEvent(_dpy, button, 1, 0);
            XTestFakeButtonEvent(_dpy, button, 0, 0);
        }
        XFlush(_dpy);
    }

    // ---------------------------------------------------------------- keyboard

    private Dictionary<nuint, (byte Code, int Level)>? _map;
    private readonly List<byte> _spare = [];
    private readonly Dictionary<nuint, byte> _borrowed = [];
    private readonly LinkedList<nuint> _lru = new();
    private int _min, _max;

    private const nuint ShiftL = 0xffe1, ControlL = 0xffe3, AltL = 0xffe9, SuperL = 0xffeb;

    private void LoadMap()
    {
        _map = [];
        _spare.Clear();
        XDisplayKeycodes(_dpy, out _min, out _max);
        var count = _max - _min + 1;
        var syms = XGetKeyboardMapping(_dpy, (byte)_min, count, out var per);
        if (syms == IntPtr.Zero) return;
        try
        {
            var p = (nuint*)syms;
            for (var i = 0; i < count; i++)
            {
                var any = false;
                for (var l = 0; l < per; l++)
                {
                    var s = p[i * per + l];
                    if (s == 0) continue;
                    any = true;
                    // levels 0 and 1 (plain, Shift) of group 1 are what we can press
                    if (l < 2 && !_map.ContainsKey(s)) _map[s] = ((byte)(_min + i), l);
                }
                if (!any && _min + i > 8) _spare.Add((byte)(_min + i));
            }
        }
        finally { XFree(syms); }
    }

    /// <summary>Presses and releases the key that produces <paramref name="keysym"/>, holding the given modifiers. A symbol
    /// the keyboard map does not have (ქ, é, 😀 …) is put on a spare key first — the trick xdotool uses — so any text can be typed.</summary>
    internal void Key(nuint keysym, bool ctrl = false, bool alt = false, bool shift = false, bool super = false)
    {
        if (_map is null) LoadMap();
        if (!_map!.TryGetValue(keysym, out var k))
        {
            var code = Borrow(keysym);
            if (code == 0) return;
            k = (code, 0);
        }
        var mods = new List<nuint>();
        if (ctrl) mods.Add(ControlL);
        if (alt) mods.Add(AltL);
        if (super) mods.Add(SuperL);
        if (shift || k.Level == 1) mods.Add(ShiftL);
        foreach (var m in mods) Press(m, true);
        XTestFakeKeyEvent(_dpy, k.Code, 1, 0);
        XTestFakeKeyEvent(_dpy, k.Code, 0, 0);
        for (var i = mods.Count - 1; i >= 0; i--) Press(mods[i], false);
        XFlush(_dpy);
    }

    private void Press(nuint modSym, bool down)
    {
        if (_map!.TryGetValue(modSym, out var m)) XTestFakeKeyEvent(_dpy, m.Code, down ? 1 : 0, 0);
    }

    private byte Borrow(nuint keysym)
    {
        if (_borrowed.TryGetValue(keysym, out var have))
        {
            _lru.Remove(keysym); _lru.AddFirst(keysym);
            return have;
        }
        byte code;
        if (_spare.Count > 0) { code = _spare[^1]; _spare.RemoveAt(_spare.Count - 1); }
        else if (_lru.Last is { } oldest)
        {
            code = _borrowed[oldest.Value];
            _borrowed.Remove(oldest.Value);
            _lru.RemoveLast();
        }
        else return 0;
        var syms = stackalloc nuint[2];
        syms[0] = keysym; syms[1] = keysym;
        XChangeKeyboardMapping(_dpy, code, 2, syms, 1);
        XSync(_dpy, 0);
        Thread.Sleep(15); // let the toolkit pick up the new map before the key arrives
        _borrowed[keysym] = code;
        _lru.AddFirst(keysym);
        return code;
    }

    /// <summary>The keysym for one Unicode code point (X: Latin-1 as is, everything else 0x01000000 + code point).</summary>
    internal static nuint KeysymFor(int cp) => cp switch
    {
        '\n' or '\r' => 0xff0d,
        '\t' => 0xff09,
        '\b' => 0xff08,
        >= 0x20 and <= 0x7e or >= 0xa0 and <= 0xff => (nuint)cp,
        _ => (nuint)(0x01000000 | cp),
    };

    internal void Type(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var cp = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i])) i++;
            if (cp == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
            Key(KeysymFor(cp));
        }
    }

    public void Dispose()
    {
        if (_dpy == IntPtr.Zero) return;
        try
        {
            // put the borrowed keys back to "nothing" so the map the browser sees stays as it was
            if (_borrowed.Count > 0)
            {
                var none = stackalloc nuint[2];
                foreach (var code in _borrowed.Values) XChangeKeyboardMapping(_dpy, code, 2, none, 1);
                XSync(_dpy, 0);
            }
            XCloseDisplay(_dpy);
        }
        catch { }
        _dpy = IntPtr.Zero;
    }
}
