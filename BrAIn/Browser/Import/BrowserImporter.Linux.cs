using System.Runtime.Versioning;
using System.Text.Json;
using BrAIn.Browser.Linux;

namespace BrAIn.Browser.Import;

/// <summary>
/// The Linux half of the browser importer: where the other browsers keep their profiles on a Linux desktop
/// (native, Snap and Flatpak installs), how Chromium's Linux cookie/password encryption is undone, and the
/// plumbing that keeps those reads safe. It imports exactly what the macOS importer does — history, bookmarks,
/// cookies and Chromium passwords from Chrome, Chromium, Brave, Edge, Vivaldi and Opera, history/bookmarks/cookies
/// from Firefox — with the same rules: databases are only ever read from private temp copies, cookie values and
/// plaintext passwords never leave the host process (the controller hands them straight to the cookie store /
/// the encrypted vault), and a Firefox/Safari-style keystore that cannot be read yields nothing.
///
/// Anything unreadable (permissions, a locked keyring, a damaged database, a secret store that never answers)
/// is skipped with a one-line reason: logged as <c>[imp] ...</c> and handed to the import page through
/// <see cref="TakeWarnings"/>. Nothing here throws to the caller and nothing waits forever.
/// </summary>
public static partial class BrowserImporter
{
    // ---------------- where the profiles live ----------------

    private sealed record LinuxChromiumRoot(string Key, string Pretty, string Where, string Root, string Label);

    private static string LinuxHome => OsPaths.Home();

    private static string LinuxConfigHome
    {
        get
        {
            var x = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return !string.IsNullOrWhiteSpace(x) && Path.IsPathRooted(x) ? x : Path.Combine(LinuxHome, ".config");
        }
    }

    private static IEnumerable<LinuxChromiumRoot> LinuxChromiumRoots()
    {
        var home = LinuxHome;
        var cfg = LinuxConfigHome;
        string Flat(string app, string sub) => Path.Combine(home, ".var", "app", app, "config", sub);
        return new[]
        {
            new LinuxChromiumRoot("chrome",   "Google Chrome",  "",        Path.Combine(cfg, "google-chrome"), "Chrome Safe Storage"),
            new LinuxChromiumRoot("chrome",   "Google Chrome",  "Flatpak", Flat("com.google.Chrome", "google-chrome"), "Chrome Safe Storage"),
            new LinuxChromiumRoot("chromium", "Chromium",       "",        Path.Combine(cfg, "chromium"), "Chromium Safe Storage"),
            new LinuxChromiumRoot("chromium", "Chromium",       "Snap",    Path.Combine(home, "snap", "chromium", "common", "chromium"), "Chromium Safe Storage"),
            new LinuxChromiumRoot("chromium", "Chromium",       "Flatpak", Flat("org.chromium.Chromium", "chromium"), "Chromium Safe Storage"),
            new LinuxChromiumRoot("brave",    "Brave",          "",        Path.Combine(cfg, "BraveSoftware", "Brave-Browser"), "Brave Safe Storage"),
            new LinuxChromiumRoot("brave",    "Brave",          "Snap",    Path.Combine(home, "snap", "brave", "current", ".config", "BraveSoftware", "Brave-Browser"), "Brave Safe Storage"),
            new LinuxChromiumRoot("brave",    "Brave",          "Flatpak", Flat("com.brave.Browser", Path.Combine("BraveSoftware", "Brave-Browser")), "Brave Safe Storage"),
            new LinuxChromiumRoot("edge",     "Microsoft Edge", "",        Path.Combine(cfg, "microsoft-edge"), "Microsoft Edge Safe Storage"),
            new LinuxChromiumRoot("edge",     "Microsoft Edge", "Flatpak", Flat("com.microsoft.Edge", "microsoft-edge"), "Microsoft Edge Safe Storage"),
            new LinuxChromiumRoot("vivaldi",  "Vivaldi",        "",        Path.Combine(cfg, "vivaldi"), "Vivaldi Safe Storage"),
            new LinuxChromiumRoot("vivaldi",  "Vivaldi",        "Flatpak", Flat("com.vivaldi.Vivaldi", "vivaldi"), "Vivaldi Safe Storage"),
            new LinuxChromiumRoot("opera",    "Opera",          "",        Path.Combine(cfg, "opera"), "Opera Safe Storage"),
            new LinuxChromiumRoot("opera",    "Opera",          "Flatpak", Flat("com.opera.Opera", "opera"), "Opera Safe Storage"),
        };
    }

    private static IEnumerable<(string Root, string Where)> LinuxFirefoxRoots()
    {
        var home = LinuxHome;
        return new[]
        {
            (Path.Combine(home, ".mozilla", "firefox"), ""),
            (Path.Combine(LinuxConfigHome, "mozilla", "firefox"), ""),                      // XDG layout (newer Firefox)
            (Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox"), "Snap"),
            (Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox"), "Flatpak"),
        };
    }

    private const string LinuxKeyringNote = "decrypts via the system keyring (may prompt)";

    // ---------------- scan ----------------

    private static List<BrowserSource> ScanLinux()
    {
        var found = new List<BrowserSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (_warnGate) _warnings.Clear();
        if (LinuxHome.Length == 0) return found; // no home folder: relative paths would search the working directory

        foreach (var b in LinuxChromiumRoots())
        {
            try
            {
                if (!Directory.Exists(b.Root)) continue;
                var baseName = b.Where.Length > 0 ? $"{b.Pretty} ({b.Where})" : b.Pretty;
                var id = b.Where.Length > 0 ? $"{b.Key}-{b.Where.ToLowerInvariant()}" : b.Key;
                foreach (var prof in LinuxChromiumProfiles(b.Root))
                {
                    if (!seen.Add(LinuxCanonical(prof.Dir))) continue; // the same folder reached through a symlink or two roots
                    var hist = SqlCount(Path.Combine(prof.Dir, "History"), "urls");
                    var bm = BookmarkCount(Path.Combine(prof.Dir, "Bookmarks"));
                    var ck = SqlCount(LinuxCookiesDb(prof.Dir) ?? Path.Combine(prof.Dir, "Cookies"), "cookies");
                    var pw = SqlCount(Path.Combine(prof.Dir, "Login Data"), "logins",
                        "where blacklisted_by_user=0 and length(password_value)>0");
                    var name = prof.Name == "Default" ? baseName : $"{baseName} — {prof.Name}";
                    var src = new BrowserSource($"chromium:{id}:{prof.Name}", name, "chromium", prof.Dir,
                        hist, bm, ck, pw, b.Label, LinuxKeyringNote);
                    var problems = TakeWarnings(src);
                    if (hist + bm + ck + pw == 0 && problems is null) continue;
                    found.Add(problems is null ? src : src with { Note = LinuxKeyringNote + " · " + problems });
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[imp] {b.Pretty} at {b.Root}: {ex.Message}"); }
        }

        foreach (var (root, where) in LinuxFirefoxRoots())
        {
            try
            {
                foreach (var (dir, isDefault) in LinuxFirefoxProfiles(root))
                {
                    if (!seen.Add(LinuxCanonical(dir))) continue;
                    var hist = SqlCount(Path.Combine(dir, "places.sqlite"), "moz_places");
                    var bm = FirefoxBookmarkCount(dir);
                    var ck = SqlCount(Path.Combine(dir, "cookies.sqlite"), "moz_cookies");
                    var baseName = where.Length > 0 ? $"Firefox ({where})" : "Firefox";
                    var name = isDefault && !found.Any(f => f.Name == baseName) ? baseName : $"{baseName} — {Path.GetFileName(dir)}";
                    var src = new BrowserSource($"firefox:{dir}", name, "firefox", dir, hist, bm, ck, 0, null, null);
                    var problems = TakeWarnings(src);
                    if (hist + bm + ck == 0 && problems is null) continue;
                    found.Add(problems is null ? src : src with { Note = problems });
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[imp] Firefox at {root}: {ex.Message}"); }
        }
        return found;
    }

    /// <summary>The profiles inside one user-data folder: <c>Default</c>, <c>Profile N</c>, and — for Opera, which
    /// keeps its single profile directly in the user-data folder — the folder itself.</summary>
    private static List<(string Name, string Dir)> LinuxChromiumProfiles(string root)
    {
        var list = new List<(string, string)>();
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch (Exception ex) { Console.Error.WriteLine($"[imp] cannot list {root}: {ex.Message}"); dirs = Array.Empty<string>(); }
        Array.Sort(dirs, StringComparer.Ordinal);
        foreach (var d in dirs)
        {
            var n = Path.GetFileName(d);
            if (n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal)) list.Add((n, d));
        }
        if (LinuxHasProfileData(root)) list.Add(("Default", root));
        return list;
    }

    private static bool LinuxHasProfileData(string dir)
        => File.Exists(Path.Combine(dir, "History")) || File.Exists(Path.Combine(dir, "Bookmarks"))
        || File.Exists(Path.Combine(dir, "Login Data")) || LinuxCookiesDb(dir) is not null;

    /// <summary>Chromium moved the cookie database to <c>Network/Cookies</c> in version 96; older profiles keep it
    /// directly in the profile folder.</summary>
    private static string? LinuxCookiesDb(string profileDir)
    {
        var net = Path.Combine(profileDir, "Network", "Cookies");
        if (File.Exists(net)) return net;
        var old = Path.Combine(profileDir, "Cookies");
        return File.Exists(old) ? old : null;
    }

    private static string LinuxCanonical(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir).TrimEnd('/');
            var target = new DirectoryInfo(full).ResolveLinkTarget(true);
            return (target?.FullName ?? full).TrimEnd('/');
        }
        catch { return dir; }
    }

    /// <summary>Profile folders from Firefox's profiles.ini: the install's default first, then the profile marked
    /// Default=1, then the rest. Relative paths are relative to the folder holding the ini.</summary>
    private static List<(string Dir, bool IsDefault)> LinuxFirefoxProfiles(string root)
    {
        var result = new List<(string, bool)>();
        var ini = Path.Combine(root, "profiles.ini");
        string[] lines;
        try { lines = File.Exists(ini) ? File.ReadAllLines(ini) : Array.Empty<string>(); }
        catch (Exception ex) { Console.Error.WriteLine($"[imp] cannot read {ini}: {ex.Message}"); return result; }

        var installDefaults = new List<string>();
        var marked = new List<string>();
        var others = new List<string>();
        string? section = null;
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Flush()
        {
            if (section is null) return;
            string Resolve(string p, bool relative) => relative ? Path.Combine(root, p) : p;
            if (section.StartsWith("Install", StringComparison.OrdinalIgnoreCase) && kv.TryGetValue("Default", out var d) && d.Length > 0)
                installDefaults.Add(Resolve(d, true));
            else if (section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) && kv.TryGetValue("Path", out var p) && p.Length > 0)
            {
                var dir = Resolve(p, !kv.TryGetValue("IsRelative", out var rel) || rel != "0");
                (kv.TryGetValue("Default", out var def) && def == "1" ? marked : others).Add(dir);
            }
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { Flush(); section = line[1..^1]; kv.Clear(); continue; }
            var eq = line.IndexOf('=');
            if (eq > 0) kv[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        Flush();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var firstIsDefault = true;
        foreach (var d in installDefaults.Concat(marked).Concat(others))
        {
            var full = d.TrimEnd('/');
            if (!seen.Add(full) || !Directory.Exists(full)) continue;
            result.Add((full, firstIsDefault));
            firstIsDefault = false;
        }
        return result;
    }

    // ---------------- cookies ----------------

    private static List<ImportedCookie> LinuxChromiumCookies(BrowserSource src)
    {
        var list = new List<ImportedCookie>();
        var db = LinuxCookiesDb(src.Dir);
        if (db is null) return list;
        var rows = SqliteJson(db,
            "select host_key,name,value,hex(encrypted_value) as ev,expires_utc as t,is_secure,is_httponly,path from cookies");
        if (rows.ValueKind != JsonValueKind.Array) return list;

        var keys = ChromeCrypto.LinuxKeys(src.Keychain ?? "Chrome Safe Storage"); // the keyring is only asked when a v11 value shows up
        int needsKey = 0, unreadable = 0;
        foreach (var el in rows.EnumerateArray())
        {
            try
            {
                var host = el.GetProperty("host_key").GetString() ?? "";
                var hex = el.TryGetProperty("ev", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                string? val;
                if (string.IsNullOrEmpty(hex))
                {
                    // Unencrypted store (e.g. a build without OSCrypt): the plain column is the value.
                    val = el.TryGetProperty("value", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
                    if (string.IsNullOrEmpty(val)) continue;
                }
                else
                {
                    val = ChromeCrypto.DecryptLinux(HexToBytes(hex), keys, host, out var why);
                    if (val is null)
                    {
                        if (why == ChromeCrypto.LinuxFail.NeedsKey) needsKey++; else unreadable++;
                        continue;
                    }
                }
                var exp = el.TryGetProperty("t", out var te) && te.ValueKind == JsonValueKind.Number ? te.GetDouble() : 0;
                list.Add(new ImportedCookie(
                    el.GetProperty("name").GetString() ?? "",
                    val,
                    host,
                    el.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/",
                    exp > 0 ? exp / 1_000_000.0 - 11644473600.0 : null, // µs→s, 1601→1970
                    el.TryGetProperty("is_secure", out var s) && s.GetInt32() == 1,
                    el.TryGetProperty("is_httponly", out var h) && h.GetInt32() == 1));
            }
            catch { unreadable++; }
        }
        LinuxWarnSkipped(db, "cookie", needsKey, unreadable, keys);
        return list;
    }

    // ---------------- passwords ----------------

    private static List<ImportedLogin> LinuxChromiumPasswords(BrowserSource src)
    {
        var list = new List<ImportedLogin>();
        var db = Path.Combine(src.Dir, "Login Data");
        var rows = SqliteJson(db,
            "select origin_url,username_value,hex(password_value) as pv from logins" +
            " where blacklisted_by_user=0 and length(password_value)>0");
        if (rows.ValueKind != JsonValueKind.Array) return list;

        var keys = ChromeCrypto.LinuxKeys(src.Keychain ?? "Chrome Safe Storage");
        int needsKey = 0, unreadable = 0;
        foreach (var el in rows.EnumerateArray())
        {
            try
            {
                var hex = el.GetProperty("pv").GetString();
                var origin = el.GetProperty("origin_url").GetString();
                if (string.IsNullOrEmpty(hex) || string.IsNullOrEmpty(origin)) continue;
                var pass = ChromeCrypto.DecryptPasswordLinux(HexToBytes(hex), keys, origin, out var why);
                if (string.IsNullOrEmpty(pass))
                {
                    if (why == ChromeCrypto.LinuxFail.NeedsKey) needsKey++; else unreadable++;
                    continue;
                }
                list.Add(new ImportedLogin(origin,
                    el.TryGetProperty("username_value", out var u) ? u.GetString() ?? "" : "",
                    pass));
            }
            catch { unreadable++; }
        }
        LinuxWarnSkipped(db, "password", needsKey, unreadable, keys);
        return list;
    }

    private static void LinuxWarnSkipped(string db, string what, int needsKey, int unreadable, ChromeCrypto.LinuxKeySet keys)
    {
        if (needsKey > 0)
            Warn(db, $"{needsKey} {what}{(needsKey == 1 ? "" : "s")} skipped: " + (keys.V11Problem is null
                ? "the browser's key was not found in the system keyring"
                : "the browser's key is in the system keyring, which is " + keys.V11Problem));
        if (unreadable > 0)
            Warn(db, $"{unreadable} {what}{(unreadable == 1 ? "" : "s")} could not be decrypted");
    }

    // ---------------- safe temp copies & the in-process SQLite reader ----------------

    /// <summary>A private (0700) folder for the database copies: the runtime dir when there is one (per-user,
    /// RAM-backed), else the temp dir. Copies of cookie and login databases must not be readable by other users —
    /// and v10 values are decryptable by anyone, the key is the public string "peanuts".</summary>
    [SupportedOSPlatform("linux")]
    private static string LinuxTempDir()
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var name = "brain-imp-" + Guid.NewGuid().ToString("N");
        var rt = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(rt) && Directory.Exists(rt))
        {
            try
            {
                var dir = Path.Combine(rt, name);
                Directory.CreateDirectory(dir, Private);
                return dir;
            }
            catch { /* fall through to the temp dir */ }
        }
        var tmp = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(tmp, Private);
        return tmp;
    }

    /// <summary>True when libsqlite3 handled the query (the result — or the failure, which is noted as a
    /// warning — is in <paramref name="result"/>); false when the library is missing and the <c>sqlite3</c> tool
    /// should be tried instead.</summary>
    private static bool LinuxQuery(string dbPath, string copy, string sql, out JsonElement result)
    {
        result = default;
        if (!LinuxSqlite.TryQuery(copy, sql, out var doc, out var error)) return false;
        using (doc)
        {
            if (error is not null) Warn(dbPath, $"could not read {Path.GetFileName(dbPath)} ({error})");
            else if (doc is not null) result = doc.RootElement.Clone();
        }
        return true;
    }

    private static void LinuxNoteReadError(string dbPath, Exception ex)
    {
        var why = ex is UnauthorizedAccessException ? "permission denied"
            : ex is System.ComponentModel.Win32Exception ? "neither libsqlite3 nor the sqlite3 tool is installed"
            : ex.Message;
        Warn(dbPath, $"could not read {Path.GetFileName(dbPath)} ({why})");
    }

    // ---------------- warnings the import page can show ----------------

    private static readonly object _warnGate = new();
    private static readonly List<(string Path, string Msg)> _warnings = new();

    private static void Warn(string path, string msg)
    {
        Console.Error.WriteLine("[imp] " + msg + " — " + path);
        lock (_warnGate)
            if (!_warnings.Contains((path, msg))) _warnings.Add((path, msg));
    }

    /// <summary>Why parts of <paramref name="src"/> were skipped since the last call (one line), or null.
    /// Only Linux produces these; the import page shows them under the result.</summary>
    public static string? TakeWarnings(BrowserSource src)
    {
        var dir = src.Dir.TrimEnd('/') + "/";
        lock (_warnGate)
        {
            var mine = _warnings.Where(w => w.Path.StartsWith(dir, StringComparison.Ordinal)).ToList();
            if (mine.Count == 0) return null;
            _warnings.RemoveAll(w => mine.Contains(w));
            return string.Join(" · ", mine.Select(m => m.Msg).Distinct());
        }
    }
}
