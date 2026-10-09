namespace BrAIn.Browser.Mac;

/// <summary>
/// Installs the page helpers (brain.js + chrome.js) as a WKUserScript at
/// document-start. Without this the chrome bar only mounted after
/// LoadCompleted's evaluateJavaScript round-trip — the toolbar and DevTools
/// vanished for the entire navigation. A document-start script mounts the
/// bar while the new document is still parsing; WebKit runs it regardless
/// of the page's CSP (injected source, no eval).
/// </summary>
internal static class WkUserScript
{
    /// <param name="source">Full JS program; must be self-guarding
    /// (chrome.js bails on an existing window.__brainChrome).</param>
    public static void Install(IntPtr webview, string source)
    {
        var cfg = ObjC.Send(webview, ObjC.Sel("configuration"));
        if (cfg == IntPtr.Zero) return;
        var ucc = ObjC.Send(cfg, ObjC.Sel("userContentController"));
        if (ucc == IntPtr.Zero) return;
        var cls = ObjC.GetClass("WKUserScript");
        if (cls == IntPtr.Zero) return;
        var nsSrc = ObjC.NsString(source);
        // WKUserScriptInjectionTimeAtDocumentStart = 0, forMainFrameOnly = YES
        var script = ObjC.SendStrIntBool(
            ObjC.Send(cls, ObjC.Sel("alloc")),
            ObjC.Sel("initWithSource:injectionTime:forMainFrameOnly:"),
            nsSrc, 0, 1);
        if (script == IntPtr.Zero) return;
        ObjC.Send(ucc, ObjC.Sel("addUserScript:"), script);
        ObjC.Send(script, ObjC.Sel("release")); // controller retains it
    }
}
