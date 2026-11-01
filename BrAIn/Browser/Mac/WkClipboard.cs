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

    /// <summary>Puts a picture on the clipboard: the original bytes where macOS has a standard type for them
    /// (PNG, JPEG, GIF), plus a TIFF rendering so every app can paste it. Call on the UI thread.</summary>
    public static bool SetImage(byte[] data, string mime, long pixels)
    {
        if (data.Length == 0) return false;
        IntPtr nsData;
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { nsData = ObjC.Send(ObjC.GetClass("NSData"), ObjC.Sel("dataWithBytes:length:"), pin.AddrOfPinnedObject(), (IntPtr)data.Length); }
        finally { pin.Free(); }                                   // dataWithBytes:length: copies
        if (nsData == IntPtr.Zero) return false;
        var uti = mime switch { "image/png" => "public.png", "image/jpeg" => "public.jpeg", "image/gif" => "com.compuserve.gif", _ => null };
        IntPtr img = IntPtr.Zero, tiff = IntPtr.Zero;
        try
        {
            // a huge picture would need hundreds of MB as TIFF: those paste in their original format only
            if (pixels <= 36_000_000)
            {
                img = ObjC.Send(ObjC.Send(ObjC.GetClass("NSImage"), ObjC.Sel("alloc")), ObjC.Sel("initWithData:"), nsData);
                if (img != IntPtr.Zero) tiff = ObjC.Send(img, ObjC.Sel("TIFFRepresentation"));
            }
            if (uti is null && tiff == IntPtr.Zero) return false;
            if (pixels <= 36_000_000 && tiff == IntPtr.Zero) return false;      // the system cannot read it: not a picture worth pasting
            var pb = ObjC.Send(ObjC.GetClass("NSPasteboard"), ObjC.Sel("generalPasteboard"));
            if (pb == IntPtr.Zero) return false;
            ObjC.Send(pb, ObjC.Sel("clearContents"));
            if (uti is not null) ObjC.Send(pb, ObjC.Sel("setData:forType:"), nsData, ObjC.NsString(uti));
            if (tiff != IntPtr.Zero) ObjC.Send(pb, ObjC.Sel("setData:forType:"), tiff, ObjC.NsString("public.tiff"));
            return true;
        }
        finally { if (img != IntPtr.Zero) ObjC.Send(img, ObjC.Sel("release")); }
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
