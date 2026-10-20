namespace BrAIn.Browser.Mac;

/// <summary>NSPasteboard read for the page-menu Paste command — WKWebView in
/// this context exposes no navigator.clipboard, so text paste goes host-side.</summary>
internal static class WkClipboard
{
    public static string? Text()
    {
        var pb = ObjC.Send(ObjC.GetClass("NSPasteboard"), ObjC.Sel("generalPasteboard"));
        if (pb == IntPtr.Zero) return null;
        // NSPasteboardTypeString == "public.utf8-plain-text"
        var s = ObjC.Send(pb, ObjC.Sel("stringForType:"), ObjC.NsString("public.utf8-plain-text"));
        return ObjC.NsStringToString(s);
    }

    public static bool SetText(string text)
    {
        var pb = ObjC.Send(ObjC.GetClass("NSPasteboard"), ObjC.Sel("generalPasteboard"));
        if (pb == IntPtr.Zero) return false;
        ObjC.Send(pb, ObjC.Sel("clearContents"));
        ObjC.Send(pb, ObjC.Sel("setString:forType:"), ObjC.NsString(text), ObjC.NsString("public.utf8-plain-text"));
        return true;
    }
}
