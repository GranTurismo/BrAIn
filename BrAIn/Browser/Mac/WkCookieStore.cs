using BrAIn.Browser.Import;

namespace BrAIn.Browser.Mac;

/// <summary>
/// Writes cookies into the WKWebView's data store through Objective-C:
/// NSHTTPCookie cookieWithProperties: → WKHTTPCookieStore setCookie:.
/// Call on the UI thread. HttpOnly can't be expressed through the public
/// NSHTTPCookie property keys — imported cookies lose that flag
/// (value, domain, secure and expiry are preserved).
/// </summary>
internal static class WkCookieStore
{
    public static int SetCookies(IntPtr webview, IReadOnlyList<ImportedCookie> cookies)
    {
        var cfg = ObjC.Send(webview, ObjC.Sel("configuration"));
        if (cfg == IntPtr.Zero) return 0;
        var ds = ObjC.Send(cfg, ObjC.Sel("websiteDataStore"));
        if (ds == IntPtr.Zero) return 0;
        var store = ObjC.Send(ds, ObjC.Sel("httpCookieStore"));
        if (store == IntPtr.Zero) return 0;
        var n = 0;
        foreach (var c in cookies)
            if (SetOne(store, c)) n++;
        return n;
    }

    private static bool SetOne(IntPtr store, ImportedCookie c)
    {
        try
        {
            var dict = ObjC.Send(
                ObjC.Send(ObjC.GetClass("NSMutableDictionary"), ObjC.Sel("alloc")),
                ObjC.Sel("init"));
            void Put(string key, IntPtr val) =>
                ObjC.Send(dict, ObjC.Sel("setObject:forKey:"), val, ObjC.NsString(key));

            if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Domain)) return false;
            Put("Name", ObjC.NsString(c.Name));
            Put("Value", ObjC.NsString(c.Value ?? ""));
            Put("Domain", ObjC.NsString(c.Domain));
            Put("Path", ObjC.NsString(string.IsNullOrEmpty(c.Path) ? "/" : c.Path));
            if (c.Secure) Put("Secure", ObjC.NsString("TRUE"));
            if (c.Expires is { } e)
                Put("Expires", ObjC.SendDoubleArg(
                    ObjC.GetClass("NSDate"),
                    ObjC.Sel("dateWithTimeIntervalSince1970:"), e));

            var cookie = ObjC.Send(ObjC.GetClass("NSHTTPCookie"),
                ObjC.Sel("cookieWithProperties:"), dict);
            if (cookie == IntPtr.Zero) return false;
            ObjC.Send(store, ObjC.Sel("setCookie:completionHandler:"), cookie, IntPtr.Zero);
            return true;
        }
        catch { return false; }
    }
}
