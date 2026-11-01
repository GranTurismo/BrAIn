using System.Text.Json;

namespace BrAIn.Browser;

/// <summary>
/// "Copy image" and "Save image" in the right-click menu. chrome.js hands over the picture's bytes when the
/// page lets it read them (same site, or CORS), otherwise only its address and this side downloads it with the
/// page as referrer. A web page can send these messages as well, without any click — so everything here treats
/// the request as untrusted: only the tab the user is looking at, a few per half minute, the content is
/// identified from its bytes, any file name a page suggests gets the extension of what the file really is, and
/// every copy or save is announced on the page and listed on the downloads shelf.
/// </summary>
public sealed partial class BrowserController
{
    private const int ImgMaxMessage = 56 * 1024 * 1024;     // base64 text of ImageIo.MaxBytes plus the envelope
    private readonly Queue<long> _imgStamps = new();
    private readonly SemaphoreSlim _imgSave = new(1, 1);          // picking a free file name and writing it, one at a time
    private long _imgTold;

    private bool ImgAllowed()
    {
        var now = Environment.TickCount64;
        lock (_imgStamps)
        {
            while (_imgStamps.Count > 0 && now - _imgStamps.Peek() > 30_000) _imgStamps.Dequeue();
            if (_imgStamps.Count >= 10) return false;
            _imgStamps.Enqueue(now);
            return true;
        }
    }

    private void PushImageToast(BrowserTab tab, string msg, bool bad)
    {
        if (_noToolbar || tab.IsClosed) return;
        var json = JsonSerializer.Serialize(new { b = 102, m = msg, bad });
        try { InvokeOnUi(() => { if (!tab.IsClosed) SendUi(tab, json); }); } catch { }
    }

    /// <summary>imgcopy / imgsave. <paramref name="arg"/> is JSON: d = the bytes as base64, u = the picture's address, n = a name.</summary>
    private async Task ImageAsync(BrowserTab tab, bool save, string arg)
    {
        if (tab.IsClosed || Tabs.Active?.Id != tab.Id) return;           // the user right-clicked in the tab they are looking at
        if (!ImgAllowed())
        {
            if (Environment.TickCount64 - _imgTold > 5000) { _imgTold = Environment.TickCount64; PushImageToast(tab, "That's a lot of images at once — try again in a moment.", true); }
            return;
        }
        string? what = null;
        try
        {
            if (arg.Length is 0 or > ImgMaxMessage) { PushImageToast(tab, "That image is too large.", true); return; }
            using var doc = JsonDocument.Parse(arg);
            var root = doc.RootElement;
            string? Str(string k) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var b64 = Str("d"); var url = Str("u"); var suggested = Str("n");
            var http = url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uu) && uu.Scheme is "http" or "https";

            byte[]? data = null;
            if (!string.IsNullOrEmpty(b64))
            {
                if (b64.Length > ImageIo.MaxBytes / 3 * 4 + 8) { PushImageToast(tab, "That image is too large.", true); return; }
                data = Convert.FromBase64String(b64);
            }
            else if (http)
            {
                var (got, err) = await ImageIo.FetchAsync(url!, tab.LastUrl, ImageIo.HostIsLocal(tab.LastUrl), CancellationToken.None);
                if (got is null) { PushImageToast(tab, err ?? "Couldn't get that image.", true); return; }
                data = got;
            }
            if (data is null || data.Length > ImageIo.MaxBytes) { PushImageToast(tab, data is null ? "Couldn't get that image." : "That image is too large.", true); return; }

            var kind = ImageIo.Sniff(data);
            if (kind is null) { PushImageToast(tab, "That isn't a picture BrAIn can " + (save ? "save" : "copy") + " (PNG, JPEG, GIF, WebP, BMP or AVIF).", true); return; }
            if ((long)kind.Width * kind.Height > ImageIo.MaxPixels) { PushImageToast(tab, "That image is too large.", true); return; }

            if (save)
            {
                var name = ImageFileName(suggested, http ? url : null, kind);
                await _imgSave.WaitAsync();
                try
                {
                    var item = Downloads.Begin(http ? url! : tab.LastUrl);
                    try
                    {
                        var path = Downloads.DestinationFor(item, name);
                        await File.WriteAllBytesAsync(path, data);
                        Downloads.Finish(item, path);
                        what = $"saved {path} ({data.Length / 1024} KB)";
                        PushImageToast(tab, "Saved " + item.Name + " to Downloads", false);
                    }
                    catch (Exception ex) { Downloads.Fail(item, ex.Message); throw; }
                }
                finally { _imgSave.Release(); }
            }
            else
            {
                string? problem = null;
                if (IsMac) problem = InvokeOnUi(() => Mac.WkClipboard.SetImage(data, kind.Mime, (long)kind.Width * kind.Height)) ? null : "Couldn't put the image on the clipboard.";
                else if (OperatingSystem.IsWindows())
                    problem = kind.Mime != "image/png" ? "Copying works for PNG pictures here — use Save image for this one."
                        : Native.Win32.SetClipboardPng(data) ? null : "Couldn't put the image on the clipboard.";
                else problem = "Copying images isn't supported on this system yet.";
                if (problem is not null) { PushImageToast(tab, problem, true); return; }
                what = $"copied {kind.Mime} {kind.Width}x{kind.Height} ({data.Length / 1024} KB)";
                PushImageToast(tab, "Image copied", false);
            }
            Log("image " + what);
        }
        catch (FormatException) { PushImageToast(tab, "Couldn't read that image.", true); }
        catch (JsonException) { }
        catch (Exception ex)
        {
            Log("image " + (save ? "save" : "copy") + " failed: " + ex.Message);
            PushImageToast(tab, "Couldn't " + (save ? "save" : "copy") + " that image.", true);
        }
    }

    /// <summary>A file name for the picture: what the page suggested or the address ends in, cleaned up, with the
    /// extension of what the bytes really are (a page cannot name its way to "photo.exe" or "photo.html").</summary>
    private static string ImageFileName(string? suggested, string? url, ImageIo.Kind kind)
    {
        var raw = suggested;
        if (string.IsNullOrWhiteSpace(raw) && url is not null)
            try { raw = Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath)); } catch { }
        var clean = Downloads.Sanitize(raw);
        var stem = Path.GetFileNameWithoutExtension(clean).Trim().TrimStart('.', ' ');
        if (stem.Length > 100) stem = stem[..100].TrimEnd();
        if (stem.Length == 0 || stem == "..") stem = "image";
        return stem + "." + kind.Ext;
    }
}
