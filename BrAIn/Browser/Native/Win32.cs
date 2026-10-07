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
}
