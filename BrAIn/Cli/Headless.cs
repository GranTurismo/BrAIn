using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BrAIn.Cli;

/// <summary>
/// Linux without a display (a server, a container, an SSH session): BrAIn brings its own invisible screen. An X server that
/// draws into memory (Xvfb) is started before GTK, on a free display number, protected by a cookie only this user can read,
/// and the window lives there. Nothing about the browser changes — the same WebKit engine and the same toolbar draw into that
/// screen — and <c>brain view</c> mirrors it into a terminal.
///
/// Also started here: a private session bus. A bus borrowed from the SSH login goes away at logout, and GLib ends a process
/// whose session bus closes (it raises SIGTERM), which would take a background browser down with the login.
/// </summary>
internal static class Headless
{
    /// <summary>The virtual screen. Windows are sized to the viewer's terminal, so it only has to be large enough for any of them.</summary>
    internal const int ScreenW = 2560, ScreenH = 1600;

    internal static bool Active { get; private set; }
    internal static string? Display { get; private set; }
    internal static string? XAuthority { get; private set; }

    private static Process? _xvfb, _bus;

    /// <summary>True when a GUI session is reachable (an X11 or Wayland display).</summary>
    internal static bool HasDisplay()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
           || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>Starts the virtual screen (and a private bus) and points this process's environment at them. Must run
    /// before anything touches GTK. False with a message for the user when it cannot.</summary>
    internal static bool Start(string storeDir, bool daemon, out string? error)
    {
        error = null;
        var xvfb = Which("Xvfb");
        if (xvfb is null)
        {
            error = "BrAIn needs Xvfb to run without a display (it gives the browser an invisible screen).\n" + InstallHint();
            return false;
        }
        var runDir = Path.Combine(storeDir, "headless");
        Browser.OsPaths.MakePrivate(storeDir);
        Browser.OsPaths.MakePrivate(runDir);

        // The display is reachable through a socket every local user can open, so it is locked with a cookie that only this
        // account can read: other users of the server can neither watch the browser nor type into it.
        var auth = Path.Combine(runDir, "Xauthority");
        WriteXAuthority(auth, RandomNumberGenerator.GetBytes(16));

        // In a session of its own: an X server takes SIGHUP as "reset", which with -terminate would end it — and the browser —
        // when the terminal that started us closes.
        var psi = Detached(xvfb);
        psi.RedirectStandardOutput = true; // -displayfd 1: the server writes the display number it took there
        psi.RedirectStandardInput = true;
        foreach (var a in new[]
                 {
                     "-displayfd", "1", "-screen", "0", $"{ScreenW}x{ScreenH}x24", "-nolisten", "tcp", "-auth", auth,
                     "-br",         // black, not the X stipple, behind the window
                     "-terminate",  // ends by itself when its last client (this browser) is gone, also after a crash
                     "+extension", "XTEST",
                 }) psi.ArgumentList.Add(a);
        try { _xvfb = Process.Start(psi); }
        catch (Exception ex) { error = "Could not start Xvfb: " + ex.Message; return false; }
        if (_xvfb is null) { error = "Could not start Xvfb."; return false; }

        var line = ReadLine(_xvfb.StandardOutput, TimeSpan.FromSeconds(15));
        if (line is null || !int.TryParse(line.Trim(), out var num))
        {
            try { _xvfb.Kill(); } catch { }
            error = "Xvfb did not start (no display number within 15 s). Its messages are above.";
            return false;
        }
        Display = ":" + num;
        XAuthority = auth;
        Posix.SetEnv("DISPLAY", Display);
        Posix.SetEnv("XAUTHORITY", auth);
        Posix.SetEnv("WAYLAND_DISPLAY", null);
        Posix.SetEnv("GDK_BACKEND", "x11");
        Posix.SetEnv("GDK_SCALE", null);
        Posix.SetEnv("GDK_DPI_SCALE", null);
        Posix.SetEnv("NO_AT_BRIDGE", "1"); // no accessibility bus to talk to

        // A runtime folder that outlives the login (logind removes /run/user/<uid> with the last session).
        if (daemon || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")))
        {
            var xdg = Path.Combine(runDir, "runtime");
            Browser.OsPaths.MakePrivate(xdg);
            Posix.SetEnv("XDG_RUNTIME_DIR", xdg);
        }
        StartBus();
        Active = true;
        return true;
    }

    /// <summary>A session bus of our own, ended when this process is (a tiny shell watches the pid). Without dbus-daemon the
    /// bus is switched off instead, so GLib does not try to autolaunch one: secrets then use their key-file fallback.</summary>
    private static void StartBus()
    {
        var dbus = Which("dbus-daemon");
        if (dbus is not null)
        {
            try
            {
                var psi = Detached("/bin/sh");
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardInput = true;
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("\"$0\" --session --nofork --nopidfile --print-address=1 & d=$!; " +
                                     "while kill -0 \"$1\" 2>/dev/null; do sleep 2; done; kill \"$d\" 2>/dev/null");
                psi.ArgumentList.Add(dbus);
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                _bus = Process.Start(psi);
                var addr = _bus is null ? null : ReadLine(_bus.StandardOutput, TimeSpan.FromSeconds(5));
                if (!string.IsNullOrWhiteSpace(addr))
                {
                    Posix.SetEnv("DBUS_SESSION_BUS_ADDRESS", addr.Trim());
                    return;
                }
            }
            catch { }
        }
        Posix.SetEnv("DBUS_SESSION_BUS_ADDRESS", "disabled:");
    }

    /// <summary>Runs a program in a new session (setsid(1), from util-linux, when there is one). The child is not a process
    /// group leader, so setsid execs in place and the pid stays the program's.</summary>
    private static ProcessStartInfo Detached(string program)
    {
        var setsid = Which("setsid");
        var psi = new ProcessStartInfo(setsid ?? program) { UseShellExecute = false };
        if (setsid is not null) psi.ArgumentList.Add(program);
        return psi;
    }

    /// <summary>macOS terminal mode: headless without a screen of our own (the window is invisible instead).</summary>
    internal static void MarkActive() => Active = true;

    internal static void Stop()
    {
        try { if (_xvfb is { HasExited: false }) _xvfb.Kill(); } catch { }
        try { if (_bus is { HasExited: false }) _bus.Kill(true); } catch { }
    }

    /// <summary>An Xauthority file with one wildcard entry (any host, any display number): the display number is only
    /// known after Xvfb started, and the server itself takes every cookie in the file it is given.</summary>
    private static void WriteXAuthority(string path, byte[] cookie)
    {
        using var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void Field(ReadOnlySpan<byte> b) { U16(b.Length); ms.Write(b); }
        U16(0xFFFF);                       // FamilyWild
        Field(ReadOnlySpan<byte>.Empty);   // address
        Field(ReadOnlySpan<byte>.Empty);   // display number
        Field("MIT-MAGIC-COOKIE-1"u8);
        Field(cookie);
        File.WriteAllBytes(path, []);
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static string? ReadLine(StreamReader r, TimeSpan timeout)
    {
        var t = Task.Run(r.ReadLine);
        return t.Wait(timeout) ? t.Result : null;
    }

    internal static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin").Split(':')
                     .Concat(["/usr/bin", "/usr/local/bin", "/bin"]))
        {
            if (dir.Length == 0) continue;
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    internal static string InstallHint()
    {
        var id = "";
        try
        {
            foreach (var l in File.ReadLines("/etc/os-release"))
                if (l.StartsWith("ID=") || l.StartsWith("ID_LIKE=")) id += " " + l[(l.IndexOf('=') + 1)..].Trim('"');
        }
        catch { }
        if (id.Contains("fedora") || id.Contains("rhel") || id.Contains("centos")) return "Install it with:  sudo dnf install xorg-x11-server-Xvfb";
        if (id.Contains("arch")) return "Install it with:  sudo pacman -S xorg-server-xvfb";
        if (id.Contains("suse")) return "Install it with:  sudo zypper install xorg-x11-server-Xvfb";
        return "Install it with:  sudo apt install xvfb";
    }

    /// <summary>Daemon: leave the terminal's session (no SIGHUP when the SSH login that started us ends) and ignore a
    /// hang-up that still arrives.</summary>
    internal static void Detach()
    {
        try { Posix.setsid(); } catch { }
        try { _hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => c.Cancel = true); } catch { }
    }

    private static PosixSignalRegistration? _hup;
}
