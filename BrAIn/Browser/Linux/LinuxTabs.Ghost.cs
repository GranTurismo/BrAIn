using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Ghost mode with "Keep my cookies &amp; logins" (counterpart of <c>Mac/WkTabs.Ghost.cs</c>): the tabs get their
/// own ephemeral session (like a private tab), but it is first filled with a copy of the cookies in the normal
/// session, so the user's signed-in sites open signed in. The copy only goes one way — whatever the ghost tabs set
/// afterwards (new logins, trackers, logouts) lives in memory and is dropped with the last ghost tab; the real
/// session is never written to. Only cookies are copied; sites that keep their sign-in in localStorage /
/// IndexedDB open signed out.
/// </summary>
internal static unsafe partial class LinuxTabs
{
    private sealed class SeedState
    {
        public IntPtr Dst;
        public List<IntPtr> Cookies = new();
        public int Left;
        public readonly TaskCompletionSource Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>True while the seeded ghost session exists (it lives until the last ghost tab closes).</summary>
    internal static bool HasGhostStore => _ghostCtx != IntPtr.Zero;

    /// <summary>Main thread. Creates the ghost session and starts copying the normal session's cookies into it;
    /// the task completes once they are all in (or at once when there is nothing to copy, or the session already
    /// exists). The caller serializes calls and bounds the wait.</summary>
    internal static Task SeedGhostStore()
    {
        if (_ghostCtx != IntPtr.Zero) return Task.CompletedTask;
        var ctx = LinuxEngine.NewEphemeral();
        if (ctx == IntPtr.Zero) return Task.CompletedTask;
        _ghostCtx = ctx;

        // the store works, it just starts empty, when the engine cannot enumerate cookies (WebKitGTK < 2.44)
        if (!WebKit.Has("webkit_cookie_manager_get_all_cookies")) return Task.CompletedTask;
        var src = WebKit.webkit_website_data_manager_get_cookie_manager(
            WebKit.webkit_web_context_get_website_data_manager(LinuxEngine.Persistent));
        var dst = WebKit.webkit_website_data_manager_get_cookie_manager(
            WebKit.webkit_web_context_get_website_data_manager(ctx));
        if (src == IntPtr.Zero || dst == IntPtr.Zero) return Task.CompletedTask;

        var st = new SeedState { Dst = dst };
        WebKit.webkit_cookie_manager_get_all_cookies(src, IntPtr.Zero,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&GotCookies, GLib.Stash(st));
        return st.Tcs.Task;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void GotCookies(IntPtr source, IntPtr result, IntPtr token)
    {
        var st = GLib.Unstash<SeedState>(token);
        if (st is null) return;
        try
        {
            var list = WebKit.webkit_cookie_manager_get_all_cookies_finish(source, result, out var err);
            GLib.TakeError(err);
            st.Cookies = GLib.ListItems(list);
            GLib.g_list_free(list);
            if (st.Cookies.Count == 0) { st.Tcs.TrySetResult(); return; }
            st.Left = st.Cookies.Count;
            foreach (var c in st.Cookies)
                WebKit.webkit_cookie_manager_add_cookie(st.Dst, c, IntPtr.Zero,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&CookieAdded, GLib.Stash(st));
        }
        catch { st.Tcs.TrySetResult(); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void CookieAdded(IntPtr source, IntPtr result, IntPtr token)
    {
        var st = GLib.Unstash<SeedState>(token);
        if (st is null) return;
        try { WebKit.webkit_cookie_manager_add_cookie_finish(source, result, out var err); GLib.TakeError(err); } catch { }
        if (Interlocked.Decrement(ref st.Left) > 0) return;
        foreach (var c in st.Cookies) Soup.soup_cookie_free(c);
        st.Cookies.Clear();
        st.Tcs.TrySetResult();
    }
}

/// <summary>The toolkit-wide light/dark setting WebKitGTK derives <c>prefers-color-scheme</c> from.
/// Web content always sees a light appearance unless a tab asked for dark pages, so sites do not flip to their
/// dark style just because the desktop theme is dark (same rule as the macOS build).</summary>
internal static class LinuxAppearance
{
    private static bool? _last;

    internal static void Apply(bool dark)
    {
        if (_last == dark) return;
        _last = dark;
        var settings = Gtk.gtk_settings_get_default();
        GLib.SetBool(settings, "gtk-application-prefer-dark-theme", dark);
    }
}
