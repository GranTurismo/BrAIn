using BrAIn.Browser.Import;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Writes imported cookies into a view's session through WebKitGTK's cookie manager (counterpart of
/// <c>Mac/WkCookieStore.cs</c>). Unlike the macOS path this keeps the HttpOnly flag.
/// Call on the GTK main thread.
/// </summary>
internal static class LinuxCookies
{
    public static int SetCookies(IntPtr webview, IReadOnlyList<ImportedCookie> cookies)
    {
        var mgr = WebKit.webkit_web_view_get_website_data_manager(webview);
        var cm = mgr == IntPtr.Zero ? IntPtr.Zero : WebKit.webkit_website_data_manager_get_cookie_manager(mgr);
        if (cm == IntPtr.Zero) return 0;
        var n = 0;
        foreach (var c in cookies)
            if (SetOne(cm, c)) n++;
        return n;
    }

    private static bool SetOne(IntPtr manager, ImportedCookie c)
    {
        try
        {
            if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Domain)) return false;
            var cookie = Soup.soup_cookie_new(c.Name, c.Value ?? "", c.Domain, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, -1);
            if (cookie == IntPtr.Zero) return false;
            try
            {
                if (c.Secure) Soup.soup_cookie_set_secure(cookie, 1);
                if (c.HttpOnly) Soup.soup_cookie_set_http_only(cookie, 1);
                if (c.Expires is { } e)
                {
                    var dt = GLib.g_date_time_new_from_unix_utc((long)e);
                    if (dt != IntPtr.Zero) { Soup.soup_cookie_set_expires(cookie, dt); GLib.g_date_time_unref(dt); }
                }
                WebKit.webkit_cookie_manager_add_cookie(manager, cookie, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            finally { Soup.soup_cookie_free(cookie); }
        }
        catch { return false; }
    }
}
