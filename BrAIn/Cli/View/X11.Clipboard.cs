using System.Runtime.InteropServices;
using System.Text;

namespace BrAIn.Cli.View;

/// <summary>Copying out of the mirrored browser: when its clipboard changes (XFixes tells us) the viewer reads the text and
/// hands it to the user's own terminal (OSC 52), so Ctrl+C in the remote page lands on the laptop's clipboard.</summary>
internal sealed unsafe partial class X11
{
    [LibraryImport(LibX)] private static partial IntPtr XCreateSimpleWindow(IntPtr dpy, IntPtr parent, int x, int y, uint w, uint h, uint bw, nuint border, nuint bg);
    [LibraryImport(LibX)] private static partial int XDestroyWindow(IntPtr dpy, IntPtr w);
    [LibraryImport(LibX, StringMarshalling = StringMarshalling.Utf8)] private static partial nuint XInternAtom(IntPtr dpy, string name, int onlyIfExists);
    [LibraryImport(LibX)] private static partial int XConvertSelection(IntPtr dpy, nuint selection, nuint target, nuint property, IntPtr requestor, nuint time);
    [LibraryImport(LibX)] private static partial int XPending(IntPtr dpy);
    [LibraryImport(LibX)] private static partial int XNextEvent(IntPtr dpy, byte* ev);
    [LibraryImport(LibX)] private static partial int XGetWindowProperty(IntPtr dpy, IntPtr w, nuint prop, nint offset, nint length, int delete,
        nuint reqType, out nuint actualType, out int actualFormat, out nuint nitems, out nuint bytesAfter, out IntPtr data);
    [LibraryImport("libXfixes.so.3")] private static partial int XFixesQueryExtension(IntPtr dpy, out int eventBase, out int errorBase);
    [LibraryImport("libXfixes.so.3")] private static partial void XFixesSelectSelectionInput(IntPtr dpy, IntPtr w, nuint selection, nuint mask);

    private IntPtr _clipWin;
    private nuint _aClipboard, _aUtf8, _aProp;
    private int _fixesBase = -1;

    /// <summary>Starts watching the clipboard's owner. False when XFixes is not there (copying out is then off).</summary>
    internal bool WatchClipboard()
    {
        try
        {
            if (XFixesQueryExtension(_dpy, out _fixesBase, out _) == 0) return false;
            _clipWin = XCreateSimpleWindow(_dpy, _root, -10, -10, 1, 1, 0, 0, 0);
            _aClipboard = XInternAtom(_dpy, "CLIPBOARD", 0);
            _aUtf8 = XInternAtom(_dpy, "UTF8_STRING", 0);
            _aProp = XInternAtom(_dpy, "BRAIN_VIEW_CLIP", 0);
            XFixesSelectSelectionInput(_dpy, _clipWin, _aClipboard, 1 /* SetSelectionOwnerNotifyMask */);
            XFlush(_dpy);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>True when the clipboard got a new owner since the last call (somebody copied). Never blocks.</summary>
    internal bool ClipboardChanged()
    {
        if (_clipWin == IntPtr.Zero) return false;
        var changed = false;
        var ev = stackalloc byte[192];
        while (XPending(_dpy) > 0)
        {
            XNextEvent(_dpy, ev);
            if (*(int*)ev == _fixesBase /* + XFixesSelectionNotify (0) */) changed = true;
        }
        return changed;
    }

    /// <summary>The clipboard's text (UTF-8), or null when it has none or the owner does not answer within half a second.</summary>
    internal string? ReadClipboard()
    {
        if (_clipWin == IntPtr.Zero) return null;
        XConvertSelection(_dpy, _aClipboard, _aUtf8, _aProp, _clipWin, 0);
        XFlush(_dpy);
        var ev = stackalloc byte[192];
        var deadline = Environment.TickCount64 + 500;
        while (Environment.TickCount64 < deadline)
        {
            if (XPending(_dpy) == 0) { Thread.Sleep(5); continue; }
            XNextEvent(_dpy, ev);
            if (*(int*)ev != 31 /* SelectionNotify */) continue;
            var property = *(nuint*)(ev + 56);
            if (property == 0) return null; // no text on the clipboard
            if (XGetWindowProperty(_dpy, _clipWin, _aProp, 0, 1 << 22, 1, 0, out var type, out var format, out var n, out _, out var data) != 0 || data == IntPtr.Zero)
                return null;
            try
            {
                if (format != 8) return null;
                return Encoding.UTF8.GetString((byte*)data, (int)Math.Min(n, (nuint)int.MaxValue));
            }
            finally { XFree(data); }
        }
        return null;
    }
}
