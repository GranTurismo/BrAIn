using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// The Fullscreen API (<c>element.requestFullscreen</c>, a player's fullscreen button). WebKitGTK asks through the
/// view's "enter-fullscreen" / "leave-fullscreen" signals; left alone it would fullscreen the whole window with
/// our toolbar and side panel still on top of the video. So the chrome steps aside while a page is fullscreen:
/// the toolbar view and side panel are hidden and the page area grows to the whole window. If the app window was
/// already fullscreen (it opens that way) it stays that way when the page leaves fullscreen.
/// </summary>
internal static unsafe class LinuxFullscreen
{
    private static bool _active;
    private static bool _appWasFullscreen;
    private static IntPtr _view;

    internal static bool Active => _active;

    internal static void Connect(IntPtr view)
    {
        GLib.Connect(view, "enter-fullscreen", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&OnEnter);
        GLib.Connect(view, "leave-fullscreen", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&OnLeave);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnEnter(IntPtr view, IntPtr data)
    {
        try
        {
            var win = LinuxWindow.Window;
            _appWasFullscreen = LinuxWindow.IsFullScreen(win);
            _view = view;
            _active = true;
            LinuxWindow.SetChromeHidden(true);
        }
        catch { }
        return 0; // let WebKit fullscreen the window
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnLeave(IntPtr view, IntPtr data)
    {
        try { Leave(); } catch { }
        return 0;
    }

    private static void Leave()
    {
        var win = LinuxWindow.Window;
        _active = false;
        _view = IntPtr.Zero;
        LinuxWindow.SetChromeHidden(false);
        LinuxBar.Layout(win);
        // WebKit unfullscreens the window right after this handler; put the app back the way it was
        if (_appWasFullscreen) GLib.RunOnMainLoop(() => GLib.Every(80, () => { LinuxWindow.SetFullScreen(win, true); return false; }));
    }

    /// <summary>The fullscreen page's view was destroyed (its tab closed). A view that goes away this way may never emit
    /// "leave-fullscreen", which would leave the toolbar hidden for good: undo what entering did.</summary>
    internal static void ViewDestroyed(IntPtr view)
    {
        if (!_active || view != _view) return;
        try { Leave(); } catch { }
    }
}
