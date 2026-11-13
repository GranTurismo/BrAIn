using System.Runtime.InteropServices;

namespace BrAIn.Cli;

/// <summary>The few libc calls the command line needs on macOS and Linux (terminal modes and size, the native environment,
/// sessions). .NET's own <see cref="Environment.SetEnvironmentVariable(string, string)"/> only changes the managed copy of
/// the environment on Unix: GTK, Xlib and the WebKit helper processes read the native one, so it is set here as well.</summary>
internal static partial class Posix
{
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int setenv(string name, string value, int overwrite);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int unsetenv(string name);

    [LibraryImport("libc", SetLastError = true)] internal static partial int setsid();
    [LibraryImport("libc")] internal static partial uint geteuid();
    [LibraryImport("libc")] internal static partial int isatty(int fd);
    [LibraryImport("libc", SetLastError = true)] private static partial int tcgetattr(int fd, byte[] termios);
    [LibraryImport("libc", SetLastError = true)] private static partial int tcsetattr(int fd, int when, byte[] termios);
    [LibraryImport("libc")] private static partial void cfmakeraw(byte[] termios);
    [LibraryImport("libc", SetLastError = true)] private static partial int ioctl(int fd, nuint request, ref WinSize ws);

    // ioctl is variadic. Apple's arm64 convention passes variadic arguments on the stack, not in registers, so a plain
    // three-argument call hands it garbage (and it writes the size somewhere random). Filling the eight argument registers
    // first puts the pointer exactly where ioctl reads its first variadic argument.
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl_AppleArm64(int fd, nuint request, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, ref WinSize ws);
    [LibraryImport("libc", SetLastError = true)] internal static partial int kill(int pid, int sig);

    /// <summary>Sets a variable in BOTH the managed and the native environment (null removes it).</summary>
    internal static void SetEnv(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            if (value is null) unsetenv(name);
            else setenv(name, value, 1);
        }
        catch { /* no libc (cannot happen on macOS/Linux) */ }
    }

    internal static bool IsTty(int fd)
    {
        try { return isatty(fd) == 1; } catch { return false; }
    }

    internal static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        try { return kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == 1 /* EPERM: exists, not ours */; }
        catch { return false; }
    }

    // ---------------------------------------------------------------- terminal

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinSize { public ushort Rows, Cols, XPixel, YPixel; }

    /// <summary>The terminal's size in cells and (when the terminal tells) in pixels. False when fd is not a terminal.</summary>
    internal static bool TermSize(int fd, out WinSize ws)
    {
        ws = default;
        try
        {
            nuint req = OperatingSystem.IsMacOS() ? 0x40087468u : 0x5413u; // TIOCGWINSZ
            var rc = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? ioctl_AppleArm64(fd, req, 0, 0, 0, 0, 0, 0, ref ws)
                : ioctl(fd, req, ref ws);
            return rc == 0 && ws.Cols > 0 && ws.Rows > 0;
        }
        catch { return false; }
    }

    /// <summary>Puts the terminal into raw mode (no echo, no line editing, no signals from keys, no output translation) and
    /// returns the previous settings for <see cref="RestoreTerm"/>. The struct differs between macOS and Linux, so it is
    /// handled as opaque bytes and cfmakeraw does the flag work.</summary>
    internal static byte[]? RawTerm(int fd)
    {
        try
        {
            var saved = new byte[256];
            if (tcgetattr(fd, saved) != 0) return null;
            var raw = (byte[])saved.Clone();
            cfmakeraw(raw);
            return tcsetattr(fd, 0 /* TCSANOW */, raw) == 0 ? saved : null;
        }
        catch { return null; }
    }

    internal static void RestoreTerm(int fd, byte[]? saved)
    {
        if (saved is null) return;
        try { tcsetattr(fd, 0, saved); } catch { }
    }
}
