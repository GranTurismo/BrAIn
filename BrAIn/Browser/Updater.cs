using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrAIn.Browser;

/// <summary>
/// Self-update from GitHub releases: check → download (with SHA-256 verify) →
/// apply on restart. macOS swaps the .app bundle via a detached helper script so
/// the running process never replaces itself; Windows hands off to the NSIS
/// installer (elevation prompt is built in). The chrome asks for state through
/// <see cref="View"/> and acts on <see cref="Changed"/>.
/// </summary>
public sealed class Updater
{
    public enum Phase { Idle, Checking, UpToDate, Available, Downloading, Ready, Error, Disabled }

    private const string Repo = "GranTurismo/BrAIn";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        // api.github.com 403s without a UA.
        DefaultRequestHeaders = { { "User-Agent", "BrAIn-Updater" }, { "Accept", "application/vnd.github+json" } },
        Timeout = TimeSpan.FromSeconds(30),
    };

    private sealed record RelAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);
    private sealed record RelInfo(
        [property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("assets")] RelAsset[] Assets);

    private readonly object _lock = new();
    private Phase _phase = Phase.Idle;
    private string _version = "";
    private string _error = "";
    private int _pct;
    private RelAsset? _asset;
    private string? _file;          // downloaded installer/dmg, ready to apply
    private DateTime _checkedAt;

    /// <summary>Raised whenever <see cref="View"/> would change (any thread).</summary>
    public event Action? Changed;

    public static string Current =>
        (typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0)).ToString(3);

    /// <summary>Last phase + version as a short log line ("available 1.0.3").</summary>
    public override string ToString()
    {
        lock (_lock) return _version.Length > 0 ? $"{_phase.ToString().ToLowerInvariant()} {_version}" : _phase.ToString().ToLowerInvariant();
    }

    /// <summary>Serializable state for the chrome.</summary>
    public object View()
    {
        lock (_lock)
            return new
            {
                ph = _phase.ToString().ToLowerInvariant(),
                cur = Current,
                v = _version,
                pct = _pct,
                err = _error,
            };
    }

    private void Set(Phase p, string? ver = null, int pct = 0, string err = "")
    {
        lock (_lock) { _phase = p; if (ver is not null) _version = ver; _pct = pct; _error = err; }
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>True once a check ran recently enough that re-checking is pointless.</summary>
    private bool Fresh => (DateTime.UtcNow - _checkedAt) < TimeSpan.FromHours(6);

    /// <summary>Ask GitHub what the latest release is. No-op if a check is already
    /// running or the last one is fresh.</summary>
    public async Task CheckAsync(bool force, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_phase is Phase.Checking or Phase.Downloading) return;
            if (_phase == Phase.Disabled) return;
            if (_phase == Phase.Ready && !force) return;
            if (!force && Fresh && _phase != Phase.Error) return;
            _phase = Phase.Checking; _error = "";
        }
        try { Changed?.Invoke(); } catch { }

        // Dev builds (dotnet run, no .app / non-installed exe) can check but not apply.
        var canApply = InstallRoot() is not null;
        try
        {
            var rel = await Http.GetFromJsonAsync<RelInfo>(
                $"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (rel is null || rel.Tag.Length == 0) throw new InvalidOperationException("empty release payload");

            var latest = ParseVersion(rel.Tag);
            var current = ParseVersion(Current);
            var asset = PickAsset(rel);
            _checkedAt = DateTime.UtcNow;
            lock (_lock) _asset = asset;

            if (latest <= current) { Set(Phase.UpToDate, rel.Tag.TrimStart('v')); return; }
            if (asset is null || !canApply)
            {
                Set(canApply ? Phase.Error : Phase.Disabled, rel.Tag.TrimStart('v'),
                    err: canApply ? "no installer for this platform in the release" : "dev build — download manually");
                return;
            }
            Set(Phase.Available, rel.Tag.TrimStart('v'));
        }
        catch (Exception ex)
        {
            Set(Phase.Error, err: Friendly(ex));
        }
    }

    /// <summary>Download the pending release asset and verify its SHA-256.</summary>
    public async Task DownloadAsync(CancellationToken ct)
    {
        RelAsset? asset;
        lock (_lock)
        {
            if (_phase != Phase.Available || _asset is null) return;
            asset = _asset;
            _phase = Phase.Downloading; _pct = 0;
        }
        try { Changed?.Invoke(); } catch { }

        var tmp = Path.Combine(Path.GetTempPath(), "brain-update-" + asset.Name);
        try
        {
            await using (var src = await Http.GetStreamAsync(asset.Url, ct))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[256 * 1024];
                long got = 0;
                int n;
                var last = 0;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    var pct = asset.Size > 0 ? (int)(got * 100 / asset.Size) : 0;
                    if (pct != last) { last = pct; Set(Phase.Downloading, pct: Math.Min(99, pct)); }
                }
            }

            // GitHub publishes a sha256 digest per asset; verify when present.
            var sha = asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.Ordinal)
                ? d[7..] : null;
            if (sha is not null)
            {
                await using var fs = File.OpenRead(tmp);
                var hex = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (!hex.Equals(sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("checksum mismatch — download may be corrupt");
            }

            lock (_lock) _file = tmp;
            Set(Phase.Ready, pct: 100);
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            Set(Phase.Error, err: Friendly(ex));
        }
    }

    /// <summary>Hand off to the platform installer and exit so it can replace us.</summary>
    public void ApplyAndRelaunch()
    {
        string? file, root;
        lock (_lock)
        {
            if (_phase != Phase.Ready || _file is null) return;
            file = _file;
            root = InstallRoot();
        }
        if (root is null) { Set(Phase.Error, err: "cannot locate the installed app"); return; }

        if (OperatingSystem.IsMacOS())
        {
            // A detached script swaps the .app once this process is gone.
            var script = Path.Combine(Path.GetTempPath(), "brain-update.sh");
            File.WriteAllText(script, $"""
                #!/bin/bash
                while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.2; done
                MNT=$(hdiutil attach -nobrowse -readonly "{file}" | grep -o '/Volumes/.*' | head -1)
                [ -n "$MNT" ] && ditto "$MNT/BrAIn.app" "{root}" && hdiutil detach "$MNT" -quiet
                rm -f "{file}"
                open "{root}"
                rm -f "{script}"
                """);
            Process.Start(new ProcessStartInfo("/bin/bash", script)
            { CreateNoWindow = true, UseShellExecute = false });
            Environment.Exit(0);
        }
        else if (OperatingSystem.IsWindows())
        {
            // Silent install, then relaunch once the installer exits. The NSIS
            // package carries its own UAC elevation, so one consent prompt shows.
            var script = Path.Combine(Path.GetTempPath(), "brain-update.cmd");
            File.WriteAllText(script,
                $"@echo off\r\nstart /wait \"\" \"{file}\" /S\r\nstart \"\" \"{Path.Combine(root, "BrAIn.exe")}\"\r\ndel \"{file}\"\r\ndel \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + script)
            { CreateNoWindow = true, UseShellExecute = false });
            Environment.Exit(0);
        }
    }

    /// <summary>Where the running app lives: the .app root on macOS, the install
    /// dir on Windows. Null for dev/unpacked runs — those can't self-update.</summary>
    private static string? InstallRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            // …/BrAIn.app/Contents/MacOS → the .app
            if (dir.EndsWith("/Contents/MacOS", StringComparison.Ordinal))
            {
                var app = Path.GetDirectoryName(Path.GetDirectoryName(dir));
                if (app?.EndsWith(".app", StringComparison.Ordinal) == true) return app;
            }
            return null;
        }
        if (OperatingSystem.IsWindows())
        {
            var exe = Environment.ProcessPath;
            return exe is not null ? Path.GetDirectoryName(exe) : null;
        }
        return null;
    }

    private RelAsset? PickAsset(RelInfo rel)
    {
        if (OperatingSystem.IsMacOS())
        {
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return rel.Assets.FirstOrDefault(a =>
                a.Name.Contains("macOS", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains(arch, StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase));
        }
        if (OperatingSystem.IsWindows())
            return rel.Assets.FirstOrDefault(a =>
                a.Name.StartsWith("BrAIn-Setup", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static Version ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
    }

    private static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException => "couldn't reach github.com",
        TaskCanceledException => "request timed out",
        _ => ex.Message,
    };
}
