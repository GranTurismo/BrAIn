using System.Text.Json;

namespace BrAIn.Browser;

/// <summary>
/// What a site was allowed (or refused) to do — camera, microphone, location, notifications — remembered per site
/// across launches, so a video call site asks once, not at every call. Keyed by origin (<c>https://meet.example.com</c>)
/// and kind. Kept in <c>site-permissions.json</c> next to the other stores. Private tabs and Ghost mode neither read nor
/// write it (the platform layers decide that: this class only stores).
/// </summary>
internal static class SitePermissions
{
    internal const string Camera = "camera", Microphone = "microphone", Location = "location",
        Notifications = "notifications", Clipboard = "clipboard";

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Dictionary<string, bool>> _map = new(StringComparer.OrdinalIgnoreCase);
    private static string? _path;

    /// <summary>Points the store at its folder and loads it. Called once at start-up; before that every call is a no-op
    /// (nothing is remembered, nothing is found), which is what a sandboxed test harness wants.</summary>
    internal static void Init(string storeDir)
    {
        lock (_lock)
        {
            _path = Path.Combine(storeDir, "site-permissions.json");
            _map.Clear();
            try
            {
                if (!File.Exists(_path)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(_path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
                foreach (var site in doc.RootElement.EnumerateObject())
                {
                    if (site.Value.ValueKind != JsonValueKind.Object) continue;
                    var kinds = new Dictionary<string, bool>();
                    foreach (var k in site.Value.EnumerateObject())
                        if (k.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) kinds[k.Name] = k.Value.GetBoolean();
                    if (kinds.Count > 0) _map[site.Name] = kinds;
                }
            }
            catch { /* unreadable file: start empty, the next change rewrites it */ }
        }
    }

    /// <summary>The scheme://host[:port] a decision is filed under, or null for something that is not a web origin.</summary>
    internal static string? OriginOf(string? scheme, string? host, long port)
    {
        if (string.IsNullOrEmpty(scheme) || string.IsNullOrEmpty(host)) return null;
        scheme = scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https")) return null;
        var def = scheme == "https" ? 443 : 80;
        return port > 0 && port != def ? $"{scheme}://{host.ToLowerInvariant()}:{port}" : $"{scheme}://{host.ToLowerInvariant()}";
    }

    internal static string? OriginOf(string? url)
    {
        try { var u = new Uri(url ?? ""); return OriginOf(u.Scheme, u.Host, u.Port); }
        catch { return null; }
    }

    /// <summary>true = allowed, false = refused, null = never decided (ask).</summary>
    internal static bool? Get(string origin, string kind)
    {
        lock (_lock) return _map.TryGetValue(origin, out var k) && k.TryGetValue(kind, out var v) ? v : null;
    }

    internal static void Set(string origin, bool allow, params string[] kinds)
    {
        lock (_lock)
        {
            if (_path is null) return;
            if (!_map.TryGetValue(origin, out var k)) _map[origin] = k = new Dictionary<string, bool>();
            foreach (var kind in kinds) k[kind] = allow;
            Save();
        }
    }

    /// <summary>Forgets one kind for a site, or everything for it when <paramref name="kind"/> is null.</summary>
    internal static void Forget(string origin, string? kind = null)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(origin, out var k)) return;
            if (kind is null) _map.Remove(origin); else { k.Remove(kind); if (k.Count == 0) _map.Remove(origin); }
            Save();
        }
    }

    /// <summary>Everything remembered, for a settings page: origin → kind → allowed.</summary>
    internal static Dictionary<string, Dictionary<string, bool>> All()
    {
        lock (_lock) return _map.ToDictionary(e => e.Key, e => new Dictionary<string, bool>(e.Value));
    }

    private static void Save()
    {
        try { if (_path is not null) File.WriteAllText(_path, JsonSerializer.Serialize(_map)); }
        catch { /* disk full / locked — the decision still holds for this run */ }
    }
}
