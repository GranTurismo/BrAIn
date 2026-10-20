using System.Runtime.InteropServices;

namespace BrAIn.Browser.Native;

/// <summary>Win32 window helpers. Windows-only call sites.</summary>
internal static class Win32
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Client-area size in physical pixels (the WebView2 views' bounds).</summary>
    internal static (int W, int H) ClientSize(IntPtr hwnd)
        => hwnd != IntPtr.Zero && GetClientRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxNative(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Plain topmost information box (the self-test report).</summary>
    internal static void MessageBox(string text, string caption)
        => MessageBoxNative(IntPtr.Zero, text, caption, 0x40 /*MB_ICONINFORMATION*/ | 0x40000 /*MB_TOPMOST*/);

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder sb, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    /// <summary>Direct child windows of <paramref name="hwnd"/> (not their descendants).</summary>
    internal static List<IntPtr> DirectChildren(IntPtr hwnd)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(hwnd, (h, _) => { if (GetParent(h) == hwnd) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    /// <summary>Raises a child window above its siblings without moving, resizing or activating it.</summary>
    internal static void BringToTop(IntPtr child)
    {
        const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010;
        if (child != IntPtr.Zero) SetWindowPos(child, IntPtr.Zero /*HWND_TOP*/, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
    }

    /// <summary>Diagnostics: the window's size and its child windows (top of z-order first, as enumerated).</summary>
    internal static string DescribeWindow(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder();
        GetWindowRect(hwnd, out var wr);
        var (cw, ch) = ClientSize(hwnd);
        sb.Append($"main hwnd {hwnd} window {wr.Right - wr.Left}x{wr.Bottom - wr.Top} client {cw}x{ch}");
        EnumChildWindows(hwnd, (h, _) =>
        {
            var cls = new System.Text.StringBuilder(64);
            GetClassNameW(h, cls, 64);
            GetWindowRect(h, out var r);
            sb.Append($"\n   child {h} parent {GetParent(h)} {cls} {(IsWindowVisible(h) ? "visible" : "hidden")} {r.Right - r.Left}x{r.Bottom - r.Top} @{r.Left},{r.Top}");
            return true;
        }, IntPtr.Zero);
        return sb.ToString();
    }

    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsThickFrame = 0x00040000, WsMinimizeBox = 0x00020000,
        WsMaximizeBox = 0x00010000, WsSysMenu = 0x00080000;
    private const long WsExAppWindow = 0x00040000, WsExToolWindow = 0x00000080;
    private const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpFrameChanged = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    internal const int SwRestore = 9;
    internal const int WmNclButtonDown = 0x00A1;
    internal const int HtCaption = 2;

    // Resize hit codes for WM_NCLBUTTONDOWN (wParam) — JS sends the number.
    internal const int HtLeft = 10, HtRight = 11, HtTop = 12,
        HtTopLeft = 13, HtTopRight = 14, HtBottom = 15,
        HtBottomLeft = 16, HtBottomRight = 17;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    internal static void Raise(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    /// <summary>Hands mouse capture back to the OS and posts a non-client
    /// button-down, entering the native move/size modal loop. hitCode is a
    /// HT* value: HTCAPTION drags the window (with Aero Snap), HTLEFT..17
    /// resizes. Runs the classic borderless-window trick — works on the
    /// chromeless popup where no real non-client area exists.</summary>
    internal static void BeginNcDrag(IntPtr hwnd, int hitCode)
    {
        if (hwnd == IntPtr.Zero) return;
        ReleaseCapture();
        GetCursorPos(out var p);
        PostMessage(hwnd, WmNclButtonDown, (IntPtr)hitCode,
            (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)));
    }

    /// <summary>Chromeless windows are WS_POPUP: no taskbar button, no
    /// resize frame, no DWM shadow. Re-add THICKFRAME (invisible non-client
    /// border → native edge resizing + shadow + snap metrics) and
    /// APPWINDOW so the app keeps its taskbar/Alt-Tab presence.</summary>
    internal static void ApplyBorderlessChrome(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlStyle, (IntPtr)(style | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu));
            var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)((ex | WsExAppWindow) & ~WsExToolWindow));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }
        catch { /* best effort */ }
    }

    /// <summary>Asks DWM for rounded window corners (Win11 default look;
    /// ignored on Win10).</summary>
    internal static void RoundCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var pref = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
        }
        catch { /* older OS — skip */ }
    }

    // ---------------- clipboard ----------------

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    /// <summary>Unicode text on the clipboard, or null. Serves the page
    /// menu's Paste command — WebView2's in-page clipboard.readText needs a
    /// permission grant real apps can't assume.</summary>
    internal static string? ClipboardText()
    {
        const uint CfUnicodeText = 13;
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var h = GetClipboardData(CfUnicodeText);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>Replaces the clipboard with Unicode text. Used by the
    /// browser_clipboard MCP tool.</summary>
    internal static bool SetClipboardText(string text)
    {
        const uint CfUnicodeText = 13, GmemMoveable = 0x2;
        var bytes = (text.Length + 1) * 2;
        var h = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
        if (h == IntPtr.Zero) return false;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) { GlobalFree(h); return false; }
        try
        {
            Marshal.Copy((text + "\0").ToCharArray(), 0, p, text.Length + 1);
        }
        finally { GlobalUnlock(h); }
        if (!OpenClipboard(IntPtr.Zero)) { GlobalFree(h); return false; }
        try
        {
            EmptyClipboard();
            // On success the system owns the handle; free it only on failure.
            if (SetClipboardData(CfUnicodeText, h) != IntPtr.Zero) return true;
            GlobalFree(h);
            return false;
        }
        finally { CloseClipboard(); }
    }

    // ---------------- consent prompt (password-vault unlock) ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo
    {
        public uint cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern uint CredUIPromptForWindowsCredentialsW(
        ref CredUiInfo pUiInfo, uint dwAuthError, ref uint pulAuthPackage,
        IntPtr pvInAuthBuffer, uint ulInAuthBufferSize,
        IntPtr ppvOutAuthBuffer, ref uint pulOutAuthBufferSize,
        [MarshalAs(UnmanagedType.Bool)] ref bool pfSave, uint dwFlags);

    /// <summary>Windows Security credential dialog pinned to the current user —
    /// PIN, fingerprint and face tiles are verified by the Hello credential
    /// providers before the call returns. We discard the packed credential
    /// buffer; only the verified/not-verified outcome matters.</summary>
    internal static bool ConsentPrompt(IntPtr hwndParent, string reason)
    {
        const uint Generic = 0x1, EnumerateCurrentUser = 0x200;
        var info = new CredUiInfo
        {
            cbSize = (uint)Marshal.SizeOf<CredUiInfo>(),
            hwndParent = hwndParent,
            pszMessageText = reason,
            pszCaptionText = "BrAIn Passwords",
            hbmBanner = IntPtr.Zero,
        };
        uint authPackage = 0;
        bool save = false;
        var buf = Marshal.AllocHGlobal(64 * 1024);
        uint bufSize = 64 * 1024;
        try
        {
            var rc = CredUIPromptForWindowsCredentialsW(ref info, 0, ref authPackage,
                IntPtr.Zero, 0, buf, ref bufSize, ref save,
                Generic | EnumerateCurrentUser);
            return rc == 0; // 0 = verified; 1223 (cancelled) or error → denied
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---------------- DPAPI (password-vault key protection) ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, IntPtr szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const uint CryptProtectUiForbidden = 0x1;

    /// <summary>Per-user DPAPI protection — the vault key file can only be
    /// unwrapped by the same Windows account, no prompt, no key UI.</summary>
    internal static byte[]? DpapiProtect(byte[] data)
    {
        var input = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            if (!CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out var output)) return null;
            try
            {
                var buf = new byte[output.cbData];
                Marshal.Copy(output.pbData, buf, 0, output.cbData);
                return buf;
            }
            finally { LocalFree(output.pbData); }
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }

    internal static byte[]? DpapiUnprotect(byte[] data)
    {
        var input = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out var output)) return null;
            try
            {
                var buf = new byte[output.cbData];
                Marshal.Copy(output.pbData, buf, 0, output.cbData);
                return buf;
            }
            finally { LocalFree(output.pbData); }
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }
}
