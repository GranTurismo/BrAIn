namespace BrAIn.Browser.Linux;

/// <summary>The desktop clipboard (Linux counterpart of <c>Mac/WkClipboard.cs</c>): the page-menu Paste command,
/// the browser_clipboard tool and "Copy image". GTK main thread only.</summary>
internal static class LinuxClipboard
{
    public static string? Text()
    {
        var p = Gtk.gtk_clipboard_wait_for_text(Gtk.Clipboard());
        return GLib.TakeStr(p);
    }

    public static bool SetText(string text)
    {
        var cb = Gtk.Clipboard();
        Gtk.gtk_clipboard_set_text(cb, text, -1);
        Gtk.gtk_clipboard_store(cb); // hand the text to a clipboard manager so it outlives the app
        return true;
    }

    /// <summary>Puts a picture on the clipboard as a bitmap every app can paste (GTK offers it as PNG, JPEG, BMP
    /// and TIFF). <paramref name="pixels"/> is width×height; a huge picture would need hundreds of MB decoded.</summary>
    public static bool SetImage(byte[] data, string mime, long pixels)
    {
        if (data.Length == 0 || pixels > 64_000_000) return false;
        var pb = Pixbuf.Decode(data, mime is "image/png" or "image/jpeg" or "image/gif" or "image/webp" ? mime : null);
        if (pb == IntPtr.Zero) return false;
        try
        {
            Gtk.gtk_clipboard_set_image(Gtk.Clipboard(), pb);
            return true;
        }
        finally { GLib.g_object_unref(pb); }
    }
}
