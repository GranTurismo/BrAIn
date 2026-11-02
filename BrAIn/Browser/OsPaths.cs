namespace BrAIn.Browser;

/// <summary>
/// Per-OS folders that must still resolve on a brand-new account. .NET's own
/// <c>GetFolderPath</c> returns an <i>empty string</i> on Unix when the folder does not exist yet
/// (a fresh user has no <c>~/.local/share</c>), and an empty base turns "BrAIn/profile-N" into a path
/// relative to wherever the app was started — read-only for an installed copy. That crashed the app
/// at startup on minimal Linux setups, so everything goes through here.
/// </summary>
internal static class OsPaths
{
    /// <summary>Base for BrAIn's own data folder: <c>~/Library/Application Support</c> on macOS,
    /// <c>%LOCALAPPDATA%</c> on Windows, <c>$XDG_DATA_HOME</c> (default <c>~/.local/share</c>) on Linux.
    /// The folder is not created here.</summary>
    internal static string LocalAppData()
    {
        var p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrEmpty(p)) return p;
        var home = Home();
        return home.Length > 0 ? Path.Combine(home, ".local", "share") : Path.GetTempPath();
    }

    /// <summary>Where bulky, re-creatable data (the web engine's disk cache) belongs: <c>$XDG_CACHE_HOME</c>
    /// (default <c>~/.cache</c>) on Linux, a "Cache" folder next to the app data elsewhere.</summary>
    internal static string CacheHome()
    {
        if (OperatingSystem.IsLinux())
        {
            var x = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (!string.IsNullOrWhiteSpace(x) && Path.IsPathRooted(x)) return x;
            var home = Home();
            if (home.Length > 0) return Path.Combine(home, ".cache");
        }
        return Path.Combine(LocalAppData(), "BrAIn", "Cache");
    }

    /// <summary>Linux: makes <paramref name="dir"/> owner-only (0700), creating it when missing and tightening an existing
    /// one. Cookies, history and the vault live there, and many distributions leave home folders world-readable (0755).
    /// Does nothing on other systems (their per-user folders are already private).</summary>
    internal static void MakePrivate(string dir)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(dir)) return;
        try
        {
            Directory.CreateDirectory(dir);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* an unusual filesystem (no modes) is not worth failing the launch for */ }
    }

    /// <summary>The user's home folder, or empty when the system cannot say.</summary>
    internal static string Home()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>Where downloads land. Linux honours the desktop's (possibly localized) Downloads
    /// folder from <c>~/.config/user-dirs.dirs</c>, falling back to <c>~/Downloads</c>.</summary>
    internal static string Downloads()
    {
        var home = Home();
        if (OperatingSystem.IsLinux())
        {
            var dir = XdgUserDir("XDG_DOWNLOAD_DIR", home);
            if (dir is not null) return dir;
        }
        return Path.Combine(home.Length > 0 ? home : Path.GetTempPath(), "Downloads");
    }

    /// <summary>One entry of <c>user-dirs.dirs</c> ("XDG_DOWNLOAD_DIR=\"$HOME/Téléchargements\"").</summary>
    private static string? XdgUserDir(string key, string home)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(env) && Path.IsPathRooted(env)) return env;
            var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(cfg)) cfg = Path.Combine(home, ".config");
            var file = Path.Combine(cfg, "user-dirs.dirs");
            if (!File.Exists(file)) return null;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (!line.StartsWith(key + "=", StringComparison.Ordinal)) continue;
                var v = line[(key.Length + 1)..].Trim().Trim('"');
                v = v.Replace("$HOME", home, StringComparison.Ordinal);
                // "$HOME/" alone is the desktop's way of saying "no such folder"
                if (v.Length > 0 && Path.IsPathRooted(v) && v.TrimEnd('/') != home.TrimEnd('/')) return v;
            }
        }
        catch { /* fall back to ~/Downloads */ }
        return null;
    }
}
