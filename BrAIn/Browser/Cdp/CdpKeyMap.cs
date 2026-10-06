namespace BrAIn.Browser.Cdp;

/// <summary>Maps friendly key names to CDP key event parameters.</summary>
internal static class CdpKeyMap
{
    /// <summary>Returns (code, windowsVirtualKeyCode, text).</summary>
    public static (string Code, int Vk, string? Text) Lookup(string key)
    {
        if (key.Length == 1)
        {
            var c = key[0];
            var upper = char.ToUpperInvariant(c);
            var code = char.IsLetter(upper) ? "Key" + upper
                : char.IsDigit(upper) ? "Digit" + upper
                : "Unidentified";
            var vk = char.IsLetterOrDigit(upper) ? upper : 0;
            return (code, vk, key);
        }

        return key.ToLowerInvariant() switch
        {
            "enter" or "return" => ("Enter", 13, "\r"),
            "tab" => ("Tab", 9, null),
            "escape" or "esc" => ("Escape", 27, null),
            "backspace" => ("Backspace", 8, null),
            "delete" => ("Delete", 46, null),
            "arrowup" or "up" => ("ArrowUp", 38, null),
            "arrowdown" or "down" => ("ArrowDown", 40, null),
            "arrowleft" or "left" => ("ArrowLeft", 37, null),
            "arrowright" or "right" => ("ArrowRight", 39, null),
            "home" => ("Home", 36, null),
            "end" => ("End", 35, null),
            "pageup" => ("PageUp", 33, null),
            "pagedown" => ("PageDown", 34, null),
            "insert" => ("Insert", 45, null),
            "space" or " " => ("Space", 32, " "),
            "f1" => ("F1", 112, null),
            "f2" => ("F2", 113, null),
            "f3" => ("F3", 114, null),
            "f4" => ("F4", 115, null),
            "f5" => ("F5", 116, null),
            "f6" => ("F6", 117, null),
            "f7" => ("F7", 118, null),
            "f8" => ("F8", 119, null),
            "f9" => ("F9", 120, null),
            "f10" => ("F10", 121, null),
            "f11" => ("F11", 122, null),
            "f12" => ("F12", 123, null),
            "shift" => ("ShiftLeft", 16, null),
            "control" or "ctrl" => ("ControlLeft", 17, null),
            "alt" => ("AltLeft", 18, null),
            "meta" or "cmd" or "command" => ("MetaLeft", 91, null),
            "capslock" => ("CapsLock", 20, null),
            "printscreen" => ("PrintScreen", 44, null),
            "scrolllock" => ("ScrollLock", 145, null),
            "pause" => ("Pause", 19, null),
            "contextmenu" => ("ContextMenu", 93, null),
            _ => ("Unidentified", 0, null),
        };
    }
}
