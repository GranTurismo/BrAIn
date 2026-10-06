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
    string? Error);

public sealed record CookieInfo(
    string Name,
    string Value,
    string? Domain,
    string? Path,
    double? Expires,
    bool HttpOnly,
    bool Secure);

public sealed record NavigateResult(bool Ok, string Url, string? Title, string? Error)
{
    public static NavigateResult Success(string url, string? title) => new(true, url, title, null);
    public static NavigateResult Fail(string url, string error) => new(false, url, null, error);
}

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
