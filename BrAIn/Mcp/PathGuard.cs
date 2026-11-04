namespace BrAIn.Mcp;

/// <summary>
/// Files an AI agent may never read through the browser, however the request is worded: the places where this computer keeps
/// secrets (SSH and cloud credentials, key files, other browsers' profiles, the system's keychains) and BrAIn's own data (saved
/// passwords, cookies, the release-signing key). An agent that has been talked into "upload ~/.ssh/id_ed25519" by a web page
/// must not be able to — whether the file would go to a page through browser_upload, or be shown to the agent itself by
/// opening it as a file: address. The user can always do either by hand; this only closes the door that an agent walks through.
///
/// Paths are resolved first (links followed, <c>..</c> removed), so a harmless-looking link or folder that points at a protected
/// place is judged by where it really leads.
/// </summary>
internal static class PathGuard
{
    /// <summary>Folders (relative to the user's home) that hold credentials, keys or other programs' secrets.</summary>
    private static readonly string[] HomeFolders =
    {
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".config/gcloud", ".config/gh", ".config/op", ".password-store",
        ".local/share/keyrings", ".local/share/BrAIn", ".config/BrAIn", ".brain-release", ".mozilla", ".config/google-chrome", ".config/chromium",
        ".config/BraveSoftware", ".config/microsoft-edge", ".config/Signal", ".thunderbird", ".cache/BrAIn",
        "Library/Keychains", "Library/Cookies", "Library/Safari", "Library/Messages", "Library/Mail", "Library/Application Support/BrAIn",
        "Library/Application Support/Google/Chrome", "Library/Application Support/Firefox", "Library/Application Support/BraveSoftware",
        "Library/Application Support/Microsoft Edge", "Library/Application Support/Arc", "Library/Application Support/Signal",
        "Library/Application Support/1Password", "Library/Group Containers/group.com.apple.notes",
        "AppData/Roaming/Microsoft/Credentials", "AppData/Roaming/Microsoft/Protect", "AppData/Roaming/Mozilla", "AppData/Roaming/BrAIn",
        "AppData/Local/Google/Chrome", "AppData/Local/Microsoft/Edge", "AppData/Local/BraveSoftware", "AppData/Local/BrAIn",
    };

    /// <summary>Single files in the home folder.</summary>
    private static readonly string[] HomeFiles = { ".netrc", ".npmrc", ".pypirc", ".pgpass", ".git-credentials", ".bash_history", ".zsh_history", ".python_history", ".kube/config" };

    private static readonly string[] SystemPrefixes =
    {
        "/etc/shadow", "/etc/gshadow", "/etc/sudoers", "/etc/ssh", "/etc/ssl/private", "/etc/master.passwd", "/etc/security", "/root",
        "/private/etc/shadow", "/private/etc/sudoers", "/private/etc/ssh", "/private/etc/master.passwd", "/private/var/db", "/var/db",
        "/Library/Keychains", "/System/Library/Keychains",
        "C:/Windows/System32/config", "C:/ProgramData/Microsoft/Crypto",
    };

    private static readonly string[] SecretSuffixes = { ".pem", ".key", ".p12", ".pfx", ".kdbx", ".kdb", ".ppk", ".jks", ".keystore", ".gpg", ".asc", ".keychain", ".keychain-db", ".1pux", ".opvault" };

    /// <summary>The reason this path must not be read, or null when it is fine. <paramref name="storeDir"/> is BrAIn's own data folder.</summary>
    internal static string? Sensitive(string? path, string? storeDir = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string real;
        try { real = Real(path); }
        catch { return "that path could not be checked"; }
        var norm = real.Replace('\\', '/');
        var cmp = StringComparison.OrdinalIgnoreCase;

        var home = Norm(Real(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        if (home.Length > 1)
        {
            foreach (var f in HomeFolders)
                if (Under(norm, home + "/" + f, cmp)) return $"it is inside ~/{f}, where credentials and other programs' secrets are kept";
            foreach (var f in HomeFiles)
                if (norm.Equals(home + "/" + f, cmp)) return $"~/{f} holds credentials or private history";
        }
        if (!string.IsNullOrEmpty(storeDir) && (Under(norm, Norm(storeDir), cmp) || Under(norm, Norm(Real(storeDir)), cmp))) return "it is inside BrAIn's own data folder (saved passwords, cookies, settings)";
        foreach (var p in SystemPrefixes)
            if (Under(norm, p, cmp)) return "it is a system credential file";

        var name = Path.GetFileName(norm);
        if (name.Length > 0)
        {
            if (name.StartsWith("id_", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".pub", cmp)) return "it looks like an SSH private key";
            if (name.Equals(".env", cmp) || name.StartsWith(".env.", cmp)) return "environment files usually hold secrets";
            if (name.Equals("passwords.dat", cmp) || name.Equals("cookies.sqlite", cmp) || name.Equals("Cookies", StringComparison.Ordinal) || name.Equals("Login Data", cmp) || name.Equals("Web Data", cmp) || name.Equals("logins.json", cmp) || name.Equals("key4.db", cmp))
                return "it is a browser's saved passwords or cookies";
            foreach (var suf in SecretSuffixes)
                if (name.EndsWith(suf, cmp)) return $"{suf} files are key or credential stores";
        }
        return null;
    }

    /// <summary>The text an agent is given when <see cref="Sensitive"/> says no.</summary>
    internal static string Refusal(string reason)
        => $"BLOCKED: BrAIn does not let an AI agent read that file ({reason}). Do not look for another way to read it; if the user really wants it used, they can do that themselves.";

    /// <summary>The path with every link followed and <c>.</c>/<c>..</c> resolved, even for parts that do not exist yet.</summary>
    internal static string Real(string path)
    {
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            path = Uri.UnescapeDataString(new Uri(path).AbsolutePath);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "/";
        var cur = root;
        var depth = 0;
        foreach (var seg in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Combine(cur, seg);
            // follow a link in the middle or at the end of the path (a few hops: loops stop at the limit)
            while (depth < 40 && (Directory.Exists(cur) || File.Exists(cur)))
            {
                FileSystemInfo fi = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
                if (fi.LinkTarget is null) break;
                var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null) break;
                cur = target.FullName;
                depth++;
            }
        }
        return cur;
    }

    /// <summary>Is <paramref name="path"/> (links followed) inside <paramref name="folder"/> (links followed)?</summary>
    internal static bool IsInside(string path, string folder)
    {
        try { return Under(Norm(Real(path)), Norm(Real(folder)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');

    private static bool Under(string path, string folder, StringComparison cmp)
        => path.Equals(folder, cmp) || path.StartsWith(folder.TrimEnd('/') + "/", cmp);

    /// <summary>Where a tool may write: only inside <paramref name="dir"/>, never over a file that is already there.
    /// Returns the full path, or null (with <paramref name="why"/>) when the request wants to leave the folder.</summary>
    internal static string? InsideFolder(string requested, string dir, out string why)
    {
        why = "";
        try
        {
            var root = Real(dir);
            var full = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(dir, requested));
            var parent = Real(Path.GetDirectoryName(full) ?? dir);
            if (!Under(Norm(parent), Norm(root), StringComparison.OrdinalIgnoreCase))
            { why = $"files can only be saved inside {dir}"; return null; }
            return Path.Combine(parent, Path.GetFileName(full));
        }
        catch (Exception ex) { why = ex.Message; return null; }
    }
}
