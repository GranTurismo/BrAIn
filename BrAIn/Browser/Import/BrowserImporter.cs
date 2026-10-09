using System.Diagnostics;
using System.Text.Json;

namespace BrAIn.Browser.Import;

/// <summary>A detected importable browser profile.</summary>
public sealed record BrowserSource(
    string Id,      // "chromium:chrome:Default" / "firefox:/path/profile" / "safari"
    string Name,    // display name
    string Kind,    // chromium | firefox | safari
    string Dir,     // profile directory
    int Hist, int Bm, int Ck, int Pw,
    string? Keychain, // chromium: Safe Storage service for cookie/login decrypt
    string? Note);  // e.g. "needs Full Disk Access"

public sealed record ImportedBookmark(string Url, string Title);
public sealed record ImportedCookie(
    string Name, string Value, string Domain, string Path,
    double? Expires, bool Secure, bool HttpOnly);
/// <summary>origin_url + username + decrypted password from a Chromium
/// Login Data database. Firefox/Safari logins aren't readable without their
/// own keystores, so only Chromium-family sources produce these.</summary>
public sealed record ImportedLogin(string Origin, string User, string Pass);

/// <summary>
/// Reads other browsers' on-disk stores. All reads happen on temp copies —
/// browsers hold live WAL locks on their real databases. SQLite is shelled
/// out to the system's sqlite3 (-json output): zero new dependencies and the
/// same engine Safari/Firefox/Chrome all use.
/// </summary>
public static class BrowserImporter
{
    // (key, app-support dir, keychain service for cookie encryption)
    private static readonly (string Key, string Dir, string Keychain)[] ChromiumBrowsers =
    {
        ("chrome",  "Google/Chrome",                              "Chrome Safe Storage"),
        ("edge",    "Microsoft Edge",                             "Microsoft Edge Safe Storage"),
        ("brave",   "BraveSoftware/Brave-Browser",                "Brave Safe Storage"),
        ("arc",     "Arc/User Data",                              "Arc Safe Storage"),
        ("vivaldi", "Vivaldi",                                    "Vivaldi Safe Storage"),
        ("chromium","Chromium",                                   "Chromium Safe Storage"),
        ("opera",   "com.operasoftware.Opera",                    "Opera Safe Storage"),
    };

    private static string AppSupport =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support");

    // ---------------- scan ----------------

    public static List<BrowserSource> Scan()
    {
        var found = new List<BrowserSource>();
        foreach (var (key, dir, kc) in ChromiumBrowsers)
        {
            var root = Path.Combine(AppSupport, dir);
            if (!Directory.Exists(root)) continue;
            var pretty = key switch
            {
                "chrome" => "Google Chrome", "edge" => "Microsoft Edge",
                "brave" => "Brave", "arc" => "Arc", "vivaldi" => "Vivaldi",
                "chromium" => "Chromium", _ => "Opera",
            };
            foreach (var prof in ChromiumProfiles(root))
            {
                var name = prof.Name == "Default" ? pretty : $"{pretty} — {prof.Name}";
                var hist = SqlCount(Path.Combine(prof.Dir, "History"), "urls");
                var bm = BookmarkCount(Path.Combine(prof.Dir, "Bookmarks"));
                var ck = SqlCount(Path.Combine(prof.Dir, "Cookies"), "cookies");
                var pw = SqlCount(Path.Combine(prof.Dir, "Login Data"), "logins",
                    "where blacklisted_by_user=0 and length(password_value)>0");
                if (hist + bm + ck + pw == 0) continue;
                found.Add(new BrowserSource(
                    $"chromium:{key}:{prof.Name}", name, "chromium", prof.Dir,
                    hist, bm, ck, pw, kc, ChromiumCookieNote()));
            }
        }

        foreach (var prof in FirefoxProfiles())
        {
            var hist = SqlCount(Path.Combine(prof, "places.sqlite"), "moz_places");
            var bm = FirefoxBookmarkCount(prof);
            var ck = SqlCount(Path.Combine(prof, "cookies.sqlite"), "moz_cookies");
            if (hist + bm + ck == 0) continue;
            var name = prof.EndsWith("default-release") ? "Firefox" : $"Firefox — {prof[(prof.LastIndexOf('/') + 1)..]}";
            found.Add(new BrowserSource($"firefox:{prof}", name, "firefox", prof, hist, bm, ck, 0, null, null));
        }

        var safari = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Safari");
        if (Directory.Exists(safari))
        {
            var hist = SqlCount(Path.Combine(safari, "History.db"), "history_items");
            var bm = SafariBookmarkCount(Path.Combine(safari, "Bookmarks.plist"));
            var ck = SafariCookies().Count;
            // Empty stores + missing files both look like zero rows — the only
            // visible failure is ~/Library/Safari being FDA-gated (read throws).
            var blocked = hist + bm + ck == 0 && !CanReadDir(safari)
                ? "grant Full Disk Access to import"
                : (string?)null;
            if (hist + bm + ck > 0 || blocked is not null)
                found.Add(new BrowserSource("safari", "Safari", "safari", safari,
                    hist, bm, ck, 0, null, blocked));
        }
        return found;
    }

    private static bool CanReadDir(string dir)
    {
        try { Directory.GetFiles(dir); return true; } catch { return false; }
    }

    private static IEnumerable<(string Name, string Dir)> ChromiumProfiles(string root)
    {
        foreach (var d in Directory.GetDirectories(root))
        {
            var n = Path.GetFileName(d);
            if (n == "Default" || n.StartsWith("Profile "))
                yield return (n, d);
        }
    }

    private static IEnumerable<string> FirefoxProfiles()
    {
        var root = Path.Combine(AppSupport, "Firefox");
        var ini = Path.Combine(root, "profiles.ini");
        if (!File.Exists(ini)) yield break;
        // Prefer the marked default-release; fall back to any profile with data.
        string? def = null;
        var others = new List<string>();
        foreach (var line in File.ReadAllLines(ini))
        {
            if (!line.StartsWith("Path=", StringComparison.Ordinal)) continue;
            var p = Path.Combine(root, line[5..]);
            if (line.Contains("default-release") || File.Exists(Path.Combine(p, "places.sqlite")))
            {
                if (line[5..].Contains("default-release")) def ??= p;
                else others.Add(p);
            }
        }
        if (def is not null) yield return def;
        foreach (var o in others) if (o != def) yield return o;
    }

    // ---------------- history ----------------

    public static List<HistoryEntry> ReadHistory(BrowserSource src)
    {
        var rows = src.Kind switch
        {
            "chromium" => SqliteJson(Path.Combine(src.Dir, "History"),
                "select url,title,visit_count,last_visit_time as t from urls where url like 'http%'"),
            "firefox" => SqliteJson(Path.Combine(src.Dir, "places.sqlite"),
                "select url,title,visit_count,last_visit_date as t from moz_places where hidden=0 and url like 'http%'"),
            _ => SqliteJson(Path.Combine(src.Dir, "History.db"),
                "select i.url, max(v.visit_time) as t, i.visit_count," +
                " (select title from history_visits where history_item=i.id order by visit_time desc limit 1) as title" +
                " from history_items i join history_visits v on v.history_item=i.id" +
                " where i.url like 'http%' group by i.id"),
        };
        var list = new List<HistoryEntry>();
        if (rows.ValueKind != JsonValueKind.Array) return list;
        var chromeEpoch = src.Kind == "chromium";
        var safariEpoch = src.Kind == "safari";
        foreach (var el in rows.EnumerateArray())
        {
            var url = el.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url)) continue;
            var title = el.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String
                ? ti.GetString() : null;
            var visits = el.TryGetProperty("visit_count", out var vc) && vc.ValueKind == JsonValueKind.Number
                ? vc.GetInt32() : 1;
            var raw = el.TryGetProperty("t", out var te) && te.ValueKind == JsonValueKind.Number
                ? te.GetDouble() : 0;
            var unix = chromeEpoch ? (long)(raw / 1_000_000.0 - 11644473600.0) // µs→s, 1601→1970 shift
                     : safariEpoch ? (long)(raw + 978307200.0)               // 2001→1970 epoch shift
                     : (long)(raw / 1_000_000.0);                            // firefox µs→s
            list.Add(new HistoryEntry(url, title, null, Math.Max(unix, 0), Math.Max(visits, 1)));
        }
        return list;
    }

    // ---------------- bookmarks ----------------

    public static List<ImportedBookmark> ReadBookmarks(BrowserSource src)
    {
        return src.Kind switch
        {
            "chromium" => ChromiumBookmarks(Path.Combine(src.Dir, "Bookmarks")),
            "firefox" => FirefoxBookmarks(src.Dir),
            _ => SafariBookmarks(Path.Combine(src.Dir, "Bookmarks.plist")),
        };
    }

    private static List<ImportedBookmark> ChromiumBookmarks(string path)
    {
        var list = new List<ImportedBookmark>();
        if (!File.Exists(path)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("roots", out var roots))
                foreach (var root in roots.EnumerateObject())
                    WalkChromeNodes(root.Value, list);
        }
        catch { }
        return list;
    }

    private static void WalkChromeNodes(JsonElement node, List<ImportedBookmark> list)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        if (node.TryGetProperty("type", out var t) && t.GetString() == "url" &&
            node.TryGetProperty("url", out var u) && u.GetString() is { } url && url.Length > 0)
        {
            var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? url : url;
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                list.Add(new ImportedBookmark(url, name));
        }
        if (node.TryGetProperty("children", out var ch))
            foreach (var c in ch.EnumerateArray()) WalkChromeNodes(c, list);
    }

    private static List<ImportedBookmark> FirefoxBookmarks(string dir)
    {
        var rows = SqliteJson(Path.Combine(dir, "places.sqlite"),
            "select p.url, b.title from moz_bookmarks b join moz_places p on p.id=b.fk" +
            " where b.type=1 and p.url like 'http%' and b.title is not null");
        var list = new List<ImportedBookmark>();
        if (rows.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in rows.EnumerateArray())
            if (el.TryGetProperty("url", out var u) && u.GetString() is { } url)
                list.Add(new ImportedBookmark(url,
                    el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : url));
        return list;
    }

    /// <summary>Bookmarks.plist is a binary plist that can hold blob objects
    /// plutil refuses to serialize as JSON — xml1 output keeps everything.
    /// Leaf entries carry URIDictionary(title) then URLString (alpha-sorted).</summary>
    private static List<ImportedBookmark> SafariBookmarks(string plist)
    {
        var byUrl = new Dictionary<string, string>();
        if (!File.Exists(plist)) return new();
        try
        {
            var xml = RunToolQuiet("plutil", "-convert", "xml1", "-o", "-", plist);
            static string Unesc(string s) => s
                .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                .Replace("&apos;", "'").Replace("&amp;", "&");
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(xml,
                    "<key>URIDictionary</key>\\s*<dict>\\s*<key>title</key>\\s*<string>(.*?)</string>\\s*</dict>\\s*<key>URLString</key>\\s*<string>(.*?)</string>"))
            {
                var url = Unesc(m.Groups[2].Value);
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    byUrl[url] = Unesc(m.Groups[1].Value);
            }
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(xml,
                    "<key>URLString</key>\\s*<string>(.*?)</string>\\s*<key>WebBookmarkType</key>\\s*<string>WebBookmarkTypeLeaf</string>"))
            {
                var url = Unesc(m.Groups[1].Value);
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    byUrl.TryAdd(url, url);
            }
        }
        catch { }
        return byUrl.Select(kv => new ImportedBookmark(kv.Key, kv.Value)).ToList();
    }

    // ---------------- cookies ----------------

    public static List<ImportedCookie> ReadCookies(BrowserSource src)
    {
        return src.Kind switch
        {
            "chromium" => ChromiumCookies(src),
            "firefox" => FirefoxCookies(src.Dir),
            _ => SafariCookies(), // binarycookies
        };
    }

    private static List<ImportedCookie> ChromiumCookies(BrowserSource src)
    {
        var db = Path.Combine(src.Dir, "Cookies");
        var rows = SqliteJson(db,
            "select host_key,name,hex(encrypted_value) as ev,expires_utc as t,is_secure,is_httponly,path from cookies");
        var list = new List<ImportedCookie>();
        if (rows.ValueKind != JsonValueKind.Array) return list;
        // Keychain lookup may pop a consent dialog once per browser vendor.
        var key = ChromeCrypto.SafeStorageKey(src.Keychain ?? "Chrome Safe Storage");
        if (key is null) return list;
        foreach (var el in rows.EnumerateArray())
        {
            try
            {
                var hex = el.GetProperty("ev").GetString();
                if (string.IsNullOrEmpty(hex)) continue;
                var val = ChromeCrypto.Decrypt(HexToBytes(hex), key,
                    el.GetProperty("host_key").GetString() ?? "");
                if (val is null) continue;
                var exp = el.TryGetProperty("t", out var te) && te.ValueKind == JsonValueKind.Number
                    ? te.GetDouble() : 0;
                list.Add(new ImportedCookie(
                    el.GetProperty("name").GetString() ?? "",
                    val,
                    el.GetProperty("host_key").GetString() ?? "",
                    el.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/",
                    exp > 0 ? exp / 1_000_000.0 - 11644473600.0 : null, // µs→s, 1601→1970
                    el.TryGetProperty("is_secure", out var s) && s.GetInt32() == 1,
                    el.TryGetProperty("is_httponly", out var h) && h.GetInt32() == 1));
            }
            catch { }
        }
        return list;
    }

    private static List<ImportedCookie> FirefoxCookies(string dir)
    {
        var rows = SqliteJson(Path.Combine(dir, "cookies.sqlite"),
            "select host,name,value,expiry as t,isSecure,path,isHttpOnly from moz_cookies");
        var list = new List<ImportedCookie>();
        if (rows.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in rows.EnumerateArray())
        {
            var exp = el.TryGetProperty("t", out var te) && te.ValueKind == JsonValueKind.Number
                ? te.GetDouble() : 0;
            // FF stores seconds on old schemas, milliseconds on new ones.
            if (exp > 1e12) exp /= 1000.0;
            list.Add(new ImportedCookie(
                el.GetProperty("name").GetString() ?? "",
                el.GetProperty("value").GetString() ?? "",
                el.GetProperty("host").GetString() ?? "",
                el.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/",
                exp > 0 ? exp : null,
                el.TryGetProperty("isSecure", out var s) && s.GetInt32() == 1,
                el.TryGetProperty("isHttpOnly", out var h) && h.GetInt32() == 1));
        }
        return list;
    }

    /// <summary>Safari's Cookies.binarycookies — magic 'cook', big-endian
    /// page table, little-endian records with offset-indexed strings.</summary>
    private static List<ImportedCookie> SafariCookies()
    {
        var list = new List<ImportedCookie>();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Cookies", "Cookies.binarycookies");
        byte[] data;
        try { data = File.ReadAllBytes(path); } catch { return list; }
        try
        {
            if (data.Length < 8 || data[0] != 'c' || data[1] != 'o' || data[2] != 'o' || data[3] != 'k')
                return list;
            var numPages = BE32(data, 4);
            var sizes = new int[numPages];
            for (var i = 0; i < numPages; i++) sizes[i] = BE32(data, 8 + i * 4);
            var off = 8 + numPages * 4;
            foreach (var size in sizes)
            {
                var page = off; off += size;
                var nCookies = LE32(data, page + 4);
                var recOff = page + 8;
                var offsets = new int[nCookies];
                for (var i = 0; i < nCookies; i++) offsets[i] = LE32(data, recOff + i * 4);
                var strBase = recOff + nCookies * 4;
                for (var i = 0; i < nCookies; i++)
                {
                    var r = page + offsets[i];
                    var flags = LE32(data, r + 8);
                    var expires = BitConverter.Int64BitsToDouble(LE64(data, r + 16)) + 978307200.0;
                    var domain = CStr(data, r + LE32(data, r + 28));
                    var name = CStr(data, r + LE32(data, r + 32));
                    var path2 = CStr(data, r + LE32(data, r + 36));
                    var value = CStr(data, r + LE32(data, r + 40));
                    if (string.IsNullOrEmpty(name)) continue;
                    list.Add(new ImportedCookie(name, value, domain,
                        string.IsNullOrEmpty(path2) ? "/" : path2,
                        expires > 0 ? expires : null,
                        (flags & 0x1) != 0, (flags & 0x4) != 0));
                }
            }
        }
        catch { }
        return list;
    }

    // ---------------- passwords ----------------

    /// <summary>Chromium "Login Data" → (origin, user, password). Firefox's
    /// logins.json needs the NSS key4.db handshake and Safari's passwords live
    /// in iCloud Keychain — neither is readable here, so both return empty.</summary>
    public static List<ImportedLogin> ReadPasswords(BrowserSource src)
    {
        var list = new List<ImportedLogin>();
        if (src.Kind != "chromium") return list;
        var rows = SqliteJson(Path.Combine(src.Dir, "Login Data"),
            "select origin_url,username_value,hex(password_value) as pv from logins" +
            " where blacklisted_by_user=0 and length(password_value)>0");
        if (rows.ValueKind != JsonValueKind.Array) return list;
        var key = OperatingSystem.IsWindows()
            ? WindowsLocalStateKey(Path.GetDirectoryName(src.Dir) ?? src.Dir)
            : ChromeCrypto.SafeStorageKey(src.Keychain ?? "Chrome Safe Storage");
        if (key is null) return list;
        foreach (var el in rows.EnumerateArray())
        {
            try
            {
                var hex = el.GetProperty("pv").GetString();
                var origin = el.GetProperty("origin_url").GetString();
                if (string.IsNullOrEmpty(hex) || string.IsNullOrEmpty(origin)) continue;
                var blob = HexToBytes(hex);
                var pass = OperatingSystem.IsWindows()
                    ? ChromeCrypto.DecryptPasswordWindows(blob, key)
                    : ChromeCrypto.DecryptPassword(blob, key, origin);
                if (string.IsNullOrEmpty(pass)) continue;
                list.Add(new ImportedLogin(origin,
                    el.TryGetProperty("username_value", out var u) ? u.GetString() ?? "" : "",
                    pass));
            }
            catch { }
        }
        return list;
    }

    /// <summary>"Local State" lives in the user-data root (parent of the
    /// profile dir) and carries the AES-256 key wrapped by DPAPI.</summary>
    private static byte[]? WindowsLocalStateKey(string userDataDir)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(userDataDir, "Local State")));
            var b64 = doc.RootElement.GetProperty("os_crypt")
                .GetProperty("encrypted_key").GetString();
            var blob = Convert.FromBase64String(b64!);
            if (blob.Length > 5 && blob[0] == 'D' && blob[1] == 'P' &&
                blob[2] == 'A' && blob[3] == 'P' && blob[4] == 'I')
                return Native.Win32.DpapiUnprotect(blob[5..]);
        }
        catch { }
        return null;
    }

    // ---------------- sqlite3 shell + helpers ----------------

    private static JsonElement SqliteJson(string dbPath, string sql)
    {
        if (!File.Exists(dbPath)) return default;
        var tmp = Path.Combine(Path.GetTempPath(), "brain-imp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            var copy = Path.Combine(tmp, "db.sqlite");
            CopyDb(dbPath, copy);
            // WAL/journal sidecars carry recent writes — copy them too.
            foreach (var ext in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(dbPath + ext)) CopyDb(dbPath + ext, copy + ext);
            var stdout = RunTool("sqlite3", "-json", copy, sql);
            if (string.IsNullOrWhiteSpace(stdout) || stdout == "[]") return default;
            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[imp] sqlite {dbPath}: {ex.Message}");
            return default;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Source browsers hold exclusive locks on live databases
    /// (Firefox flocks places.sqlite the whole session). Every .NET open
    /// requests a flock — even FileShare.ReadWrite — which the lock rejects.
    /// /bin/cp does a plain open()+read() with no lock and always works on
    /// macOS. Windows share-mode semantics differ; there a ReadWrite share
    /// gets through since Firefox/Chrome open their DBs share-friendly.</summary>
    private static void CopyDb(string src, string dst)
    {
        if (!OperatingSystem.IsWindows())
        {
            RunTool("cp", src, dst);
            return;
        }
        using var s = new FileStream(src, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var d = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        s.CopyTo(d);
    }

    private static int SqlCount(string dbPath, string table, string where = "")
    {
        var rows = SqliteJson(dbPath, $"select count(*) as n from {table} {where}");
        return rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0
            ? rows[0].GetProperty("n").GetInt32() : 0;
    }

    private static int BookmarkCount(string path)
    {
        return File.Exists(path) ? ChromiumBookmarks(path).Count : 0;
    }

    private static int FirefoxBookmarkCount(string dir)
        => SqlCount(Path.Combine(dir, "places.sqlite"), "moz_bookmarks");

    private static int SafariBookmarkCount(string plist)
        => File.Exists(plist) ? SafariBookmarks(plist).Count : 0;

    private static string RunTool(string exe, params string[] args)
        => RunTool(exe, false, args);

    /// <summary>Suppresses the rc!=0 stderr log — for expected failures like
    /// plutil on plists containing unserializable blob objects.</summary>
    private static string RunToolQuiet(string exe, params string[] args)
        => RunTool(exe, true, args);

    private static string RunTool(string exe, bool quiet, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync(); // drain both pipes — no deadlock
        var output = p.StandardOutput.ReadToEnd();
        var errout = errTask.GetAwaiter().GetResult();
        p.WaitForExit(15000);
        if (!quiet && p.ExitCode != 0 && !string.IsNullOrEmpty(errout))
            Console.Error.WriteLine($"[imp] {exe} rc={p.ExitCode}: {errout.Trim()[..Math.Min(errout.Trim().Length, 200)]}");
        return output;
    }

    private static string ChromiumCookieNote()
        => OperatingSystem.IsMacOS() ? "decrypts via Keychain (may prompt)"
           : "cookies unsupported; passwords from Chrome 127+ can't decrypt";

    private static byte[] HexToBytes(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (var i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    private static int BE32(byte[] b, int o)
        => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    private static int LE32(byte[] b, int o)
        => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

    private static long LE64(byte[] b, int o)
        => (long)(uint)LE32(b, o) | ((long)LE32(b, o + 4) << 32);

    private static string CStr(byte[] b, int o)
    {
        if (o <= 0 || o >= b.Length) return "";
        var end = Array.IndexOf(b, (byte)0, o);
        if (end < 0) end = b.Length;
        return System.Text.Encoding.UTF8.GetString(b, o, end - o);
    }
}
