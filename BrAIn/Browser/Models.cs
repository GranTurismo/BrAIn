namespace BrAIn.Browser;

public sealed record RectD(double X, double Y, double Width, double Height);

public enum MouseButton { Left, Right, Middle }

public sealed record ConsoleEntry(DateTime Time, string Level, string Text, string? Url, int? Line);

public sealed record NetworkEntry(
    DateTime Time,
    string RequestId,
    string Method,
    string Url,
    int? Status,
    string? ResourceType,
    long BytesReceived,
    string? Error,
    Dictionary<string, string>? RequestHeaders = null,
    Dictionary<string, string>? ResponseHeaders = null,
    string? PostData = null,
    NetExtra? X = null);

/// <summary>What only the WebKit recorder (Assets/netrec.js) can tell about a request — timing, the response text, WebSocket
/// frames. Null for entries from the Windows CDP feed, which the panel simply shows without these tabs.</summary>
public sealed record NetExtra(
    string? Tab = null,            // short id of the tab it belongs to
    string? Doc = null,            // which document of that tab (a navigation starts a new one)
    string? Kind = null,           // document | fetch | xhr | script | stylesheet | image | font | media | websocket | other
    double StartMs = 0,            // start, ms after the document began
    double DurationMs = 0,
    double DocOrigin = 0,          // epoch ms of the document's start
    string? StatusText = null,
    string? Mime = null,
    string? Protocol = null,       // h2, http/1.1 ...
    long DecodedBytes = 0,
    NetTiming? Timing = null,
    string? ResponseBody = null,   // text responses only, capped (256 KB)
    bool BodyTruncated = false,
    long BodyLength = 0,
    bool BodyDropped = false,      // evicted by the recorder's memory budget
    bool Redirected = false,
    string? FinalUrl = null,
    bool Done = false,
    IReadOnlyList<NetFrame>? Frames = null,
    int FrameCount = 0,
    string? CloseInfo = null);

public sealed record NetTiming(double Queue, double Dns, double Connect, double Tls, double Wait, double Receive);

/// <summary>One WebSocket message: Dir is "s" (sent) or "r" (received).</summary>
public sealed record NetFrame(string Dir, double AtMs, int Size, string? Text);

public sealed record CookieInfo(
    string Name,
    string Value,
    string? Domain,
    string? Path,
    double? Expires,
    bool HttpOnly,
    bool Secure);

/// <summary>One visited URL — feeds the omnibox dropdown, new-tab tiles and
/// the about:history page. Time is unix seconds; Visits counts every load.</summary>
public sealed record HistoryEntry(string Url, string? Title, string? Icon, long Time, int Visits);

/// <summary>What browser_emulate asks of a driver. Null members are left as they are; Reset undoes every override first.</summary>
public sealed record Emulation(
    string? UserAgent = null,
    string? ColorScheme = null,   // "light" | "dark"
    bool? Offline = null,
    double? Latitude = null,
    double? Longitude = null,
    bool Reset = false);

public sealed record NavigateResult(bool Ok, string Url, string? Title, string? Error)
{
    public static NavigateResult Success(string url, string? title) => new(true, url, title, null);
    public static NavigateResult Fail(string url, string error) => new(false, url, null, error);
}

/// <summary>Search provider — Query is the URL prefix the escaped query
/// appends to. Picked in the ⋮ menu / palette, persisted in prefs.json.</summary>
public sealed record SearchEngine(string Id, string Name, string Query);

public sealed record KeyModifiers(bool Alt, bool Ctrl, bool Shift, bool Meta)
{
    public static readonly KeyModifiers None = new(false, false, false, false);

    public int ToCdpMask() =>
        (Alt ? 1 : 0) | (Ctrl ? 2 : 0) | (Meta ? 4 : 0) | (Shift ? 8 : 0);

    public static KeyModifiers Parse(string? modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers)) return None;
        var parts = modifiers.Split(new[] { '+', ',', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool alt = parts.Any(p => p.Equals("alt", StringComparison.OrdinalIgnoreCase));
        bool ctrl = parts.Any(p => p.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("control", StringComparison.OrdinalIgnoreCase));
        bool shift = parts.Any(p => p.Equals("shift", StringComparison.OrdinalIgnoreCase));
        bool meta = parts.Any(p => p.Equals("meta", StringComparison.OrdinalIgnoreCase) || p.Equals("cmd", StringComparison.OrdinalIgnoreCase) || p.Equals("command", StringComparison.OrdinalIgnoreCase));
        return new KeyModifiers(alt, ctrl, shift, meta);
    }
}
