using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
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
/// installer (elevation prompt is built in); Linux unpacks the release tarball
/// beside the install folder and lets a detached shell helper swap the two folders
/// (see <see cref="LinuxInstallRoot"/> for which installs qualify). The chrome asks
/// for state through <see cref="View"/> and acts on <see cref="Changed"/>.
///
/// The toolbar shows an update card while <c>show</c> is set — switched on by anything the
/// user starts (Check for updates, Update now), never by the quiet check at launch. The card
/// walks through checking → up to date / available → downloading (bytes + percent, cancel)
/// → verifying → installing, and shows errors with a retry.
/// </summary>
public sealed class Updater
{
    public enum Phase { Idle, Checking, UpToDate, Available, Downloading, Verifying, Ready, Installing, Error, Disabled }

    private const string Repo = "GranTurismo/BrAIn";

    /// <summary>How long "checking" stays up for a check the user asked for. The request itself takes a
    /// fraction of a second, which reads as "nothing happened"; this lets the animation be seen.</summary>
    internal static int MinCheckShowMs = 2300;

    /// <summary>Where the latest release is read from (tests point it at a local server).</summary>
    internal static string Feed = $"https://api.github.com/repos/{Repo}/releases/latest";

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
    private long _got, _size;       // download progress in bytes
    private bool _show;             // the toolbar's update card is up
    private bool _userFlow;         // the user has started something: errors may open the card
    private CancellationTokenSource? _dlCts;
    private RelAsset? _asset;
    private string? _file;          // downloaded installer/dmg/tarball, ready to apply
    private int _applying;          // Linux: the archive is being unpacked / handed to the helper
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
                got = _got,
                size = _size,
                err = _error,
                show = _show,
            };
    }

    private void Set(Phase p, string? ver = null, int pct = 0, string err = "", long got = 0)
    {
        lock (_lock)
        {
            _phase = p; if (ver is not null) _version = ver; _pct = pct; _error = err; _got = got;
            if (p == Phase.Error && _userFlow) _show = true; // a failure the user is waiting on must be seen
        }
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Put the update card up (the user asked about updates).</summary>
    public void Show()
    {
        lock (_lock) { _show = true; _userFlow = true; }
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Close the card. A download in progress carries on.</summary>
    public void Dismiss()
    {
        lock (_lock) _show = false;
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Stop a download in progress; the update stays available.</summary>
    public void Cancel()
    {
        lock (_lock) _dlCts?.Cancel();
    }

    /// <summary>True once a check ran recently enough that re-checking is pointless.</summary>
    private bool Fresh => (DateTime.UtcNow - _checkedAt) < TimeSpan.FromHours(6);

    /// <summary>Ask GitHub what the latest release is. No-op if a check is already
    /// running or the last one is fresh.</summary>
    public async Task CheckAsync(bool force, CancellationToken ct, bool user = false)
    {
        bool run;
        lock (_lock)
        {
            if (user) { _show = true; _userFlow = true; }
            run = !(_phase is Phase.Checking or Phase.Downloading or Phase.Verifying or Phase.Installing or Phase.Disabled
                    || (_phase == Phase.Ready && !force)
                    || (!force && Fresh && _phase != Phase.Error));
            if (run) { _phase = Phase.Checking; _error = ""; }
        }
        // Even when nothing starts (already checking / downloading), a user's click raises the card.
        if (run || user) try { Changed?.Invoke(); } catch { }
        if (!run) return;

        // Dev builds (dotnet run, no .app / non-installed exe) can check but not apply.
        var canApply = InstallRoot() is not null;
        var t0 = Environment.TickCount64;
        async Task Linger()
        {
            if (!user) return; // the quiet check at launch shows nothing, so there is nothing to hold
            var left = (int)(MinCheckShowMs - (Environment.TickCount64 - t0));
            if (left > 0) await Task.Delay(left, CancellationToken.None);
        }
        try
        {
            var rel = await Http.GetFromJsonAsync<RelInfo>(
                Feed, ct);
            if (rel is null || rel.Tag.Length == 0) throw new InvalidOperationException("empty release payload");

            var latest = ParseVersion(rel.Tag);
            var current = ParseVersion(Current);
            var asset = PickAsset(rel);
            _checkedAt = DateTime.UtcNow;
            lock (_lock) _asset = asset;

            await Linger();
            if (latest <= current)
            {
                Set(Phase.UpToDate, rel.Tag.TrimStart('v'));
                // "You're up to date" is a confirmation, not a dialog: it clears itself.
                var stamp = _checkedAt;
                _ = Task.Delay(4500, CancellationToken.None).ContinueWith(_ =>
                {
                    lock (_lock)
                    {
                        if (_phase != Phase.UpToDate || _checkedAt != stamp || !_show) return;
                        _show = false;
                    }
                    try { Changed?.Invoke(); } catch { }
                }, TaskScheduler.Default);
                return;
            }
            if (asset is null || !canApply)
            {
                Set(canApply ? Phase.Error : Phase.Disabled, rel.Tag.TrimStart('v'),
                    err: canApply ? "no installer for this platform in the release" : NoApplyReason());
                return;
            }
            Set(Phase.Available, rel.Tag.TrimStart('v'));
        }
        catch (Exception ex)
        {
            await Linger();
            Set(Phase.Error, err: Friendly(ex));
        }
    }

    /// <summary>Download the pending release asset and verify its SHA-256.</summary>
    public async Task DownloadAsync(CancellationToken ct)
    {
        RelAsset? asset;
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_phase != Phase.Available || _asset is null) return;
            asset = _asset;
            _phase = Phase.Downloading; _pct = 0; _got = 0; _size = asset.Size; _error = "";
            _dlCts = cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        try { Changed?.Invoke(); } catch { }

        var tmp = Path.Combine(UpdateDir(), "brain-update-" + Path.GetFileName(asset.Name));
        try
        {
            await using (var src = await Http.GetStreamAsync(asset.Url, cts.Token))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[256 * 1024];
                long got = 0;
                int n;
                var last = -1;
                var lastAt = Environment.TickCount64;
                while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    got += n;
                    var pct = asset.Size > 0 ? (int)(got * 100 / asset.Size) : 0;
                    // a push per percent, or a few per second when the size is unknown
                    if (pct != last || (asset.Size <= 0 && Environment.TickCount64 - lastAt > 250))
                    {
                        last = pct; lastAt = Environment.TickCount64;
                        Set(Phase.Downloading, pct: Math.Min(99, pct), got: got);
                    }
                }
            }
            Set(Phase.Verifying, pct: 100, got: asset.Size > 0 ? asset.Size : _got);

            // GitHub publishes a sha256 digest per asset; verify when present.
            var sha = asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.Ordinal)
                ? d[7..] : null;
            if (sha is not null)
            {
                await using var fs = File.OpenRead(tmp);
                var hex = Convert.ToHexString(await SHA256.HashDataAsync(fs, cts.Token)).ToLowerInvariant();
                if (!hex.Equals(sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("checksum mismatch — download may be corrupt");
            }

            lock (_lock) _file = tmp;
            Set(Phase.Ready, pct: 100);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // the user pressed Cancel: back to "update available"
            try { File.Delete(tmp); } catch { }
            Set(Phase.Available);
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            Set(Phase.Error, err: Friendly(ex));
        }
        finally
        {
            lock (_lock) { if (ReferenceEquals(_dlCts, cts)) _dlCts = null; }
            cts.Dispose();
        }
    }

    /// <summary>What the "Update now" button does, whatever state the updater is in: check if it
    /// does not know yet (or last failed), download, then install and restart.</summary>
    public async Task UpdateAsync(CancellationToken ct)
    {
        Show();
        Phase p;
        lock (_lock) p = _phase;
        if (p is Phase.Idle or Phase.UpToDate or Phase.Error) await CheckAsync(true, ct, user: true);
        lock (_lock) p = _phase;
        if (p == Phase.Available) await DownloadAsync(ct);
        lock (_lock) p = _phase;
        if (p == Phase.Ready) await ApplyAsync();
    }

    /// <summary>Show "installing" for a beat so the user sees what is happening, then hand off and exit.</summary>
    public async Task ApplyAsync()
    {
        lock (_lock) { if (_phase != Phase.Ready || _file is null) return; }
        Set(Phase.Installing, pct: 100);
        await Task.Delay(1400);
        try { ApplyAndRelaunch(); }
        catch (Exception ex) { Set(Phase.Error, err: ex.Message); }
    }

    /// <summary>Hand off to the platform installer and exit so it can replace us.</summary>
    public void ApplyAndRelaunch()
    {
        string? file;
        lock (_lock)
        {
            if (_phase is not (Phase.Ready or Phase.Installing) || _file is null) return;
            file = _file;
        }
        var root = InstallRoot();   // outside the lock: on Linux this reads the install folder
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
        else if (OperatingSystem.IsLinux())
        {
            // The tarball is unpacked HERE, while the app is still running: a damaged download fails with a message
            // on the update card and the installed copy is never touched. Only the swap is left to a detached helper.
            if (Interlocked.Exchange(ref _applying, 1) == 1) return;   // a second click while the first is working
            string fresh;
            try { fresh = StageLinuxUpdate(file!, root); }
            catch
            {
                Volatile.Write(ref _applying, 0);
                try { File.Delete(file!); } catch { }   // whatever it was, it is no good: "Try again" downloads a new one
                lock (_lock) _file = null;
                throw;
            }
            try { StartLinuxHelper(file!, root, fresh); }
            catch { Volatile.Write(ref _applying, 0); TryDeleteTree(fresh); throw; }
            Environment.Exit(0);
        }
    }

    // ---------------- Linux ----------------

    /// <summary>The file in a release folder that lists the folder's entries (written by <c>scripts/make-linux-dist.sh</c>).</summary>
    internal const string LinuxManifest = "release.txt";
    private const string LinuxExe = "BrAIn";
    private const string DevBuild = "dev build — download manually";

    /// <summary>
    /// Linux: the folder the running program lives in, when it is a release folder this user can replace — otherwise null,
    /// and <paramref name="why"/> says so on the update card. A self-update swaps the WHOLE folder for the new one, so it is
    /// only ever done to a folder that holds nothing but what a release put there (<see cref="LinuxManifest"/>, shipped in the
    /// archive, lists its entries; anything else means "not ours") and whose parent the user can write to: the new version is
    /// unpacked beside the old one and the two are swapped by renaming. Dev builds, distro packages, a sudo-installed /opt copy
    /// and read-only mounts therefore stay on "download manually". <paramref name="exe"/> is for tests (default: this process).
    /// </summary>
    internal static string? LinuxInstallRoot(out string why, string? exe = null)
    {
        why = DevBuild;
        try
        {
            exe ??= Environment.ProcessPath;
            // `dotnet BrAIn.dll` runs as the dotnet host: its folder is the SDK, never ours to replace
            if (string.IsNullOrEmpty(exe) || Path.GetFileName(exe) != LinuxExe) return null;
            var root = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(root)) return null;
            var manifest = Path.Combine(root, LinuxManifest);
            if (!File.Exists(manifest)) return null;   // not an unpacked release (dotnet publish output, a distro build…)

            var mine = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadLines(manifest))
            {
                var line = raw.Trim();
                if (line.Length > 0 && line[0] != '#') mine.Add(line);
            }
            if (!mine.Contains(LinuxExe)) return null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                var name = Path.GetFileName(entry);
                if (mine.Contains(name)) continue;
                why = $"{root} also holds “{name}”, which isn't part of BrAIn, so it can't be replaced safely — update by hand";
                return null;
            }

            var parent = Path.GetDirectoryName(root);
            if (string.IsNullOrEmpty(parent) || !CanCreateIn(parent))
            {
                why = "installed for all users — update it with your package manager, or run install.sh from the new release with sudo";
                return null;
            }
            why = "";
            return root;
        }
        catch (Exception ex) { why = "can't read the install folder: " + ex.Message; return null; }
    }

    /// <summary>True when this process can create (and remove) files in <paramref name="dir"/> — the probe is the
    /// answer, so read-only mounts and ACLs count without a second set of rules.</summary>
    private static bool CanCreateIn(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".brain-write-test-{Environment.ProcessId}");
            using var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Linux: unpacks the downloaded release beside <paramref name="root"/> (same folder, so the swap is a rename) and returns
    /// the new folder, <c>&lt;root&gt;.new</c>. The installed copy is not touched. Before returning, the new build is asked for
    /// its version: a wrong-architecture or broken build fails here, with the old version still installed and running.
    /// On any failure the half-unpacked folders are removed and the exception says what was wrong.
    /// </summary>
    internal static string StageLinuxUpdate(string archive, string root)
    {
        root = root.TrimEnd('/');
        var parent = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("cannot locate the installed app");
        var name = Path.GetFileName(root);
        var fresh = root + ".new";
        var unpack = Path.Combine(parent, $".{name}.unpack-{Environment.ProcessId}");
        try
        {
            foreach (var stale in Directory.EnumerateDirectories(parent, $".{name}.unpack-*")) TryDeleteTree(stale);
            TryDeleteTree(fresh);
            Directory.CreateDirectory(unpack);
            using (var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
                TarFile.ExtractToDirectory(gz, unpack, overwriteFiles: false);   // refuses entries that would land outside `unpack`

            var tops = Directory.GetFileSystemEntries(unpack);
            if (tops.Length != 1 || !Directory.Exists(tops[0])) throw new InvalidDataException("unexpected layout");
            var top = tops[0];
            var exe = Path.Combine(top, LinuxExe);
            if (!File.Exists(exe) || !File.Exists(Path.Combine(top, LinuxManifest))) throw new InvalidDataException("not a BrAIn release");
            if (!OperatingSystem.IsWindows())   // (the tarball keeps the mode; this only guards against one made without it)
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            SmokeTest(exe);

            Directory.Move(top, fresh);
            Directory.Delete(unpack);
            return fresh;
        }
        catch (Exception ex)
        {
            TryDeleteTree(unpack);
            TryDeleteTree(fresh);
            if (ex is InvalidOperationException) throw;   // SmokeTest's: already says what is wrong
            throw new InvalidOperationException("couldn't unpack the update (" + ex.Message + ")", ex);
        }
    }

    /// <summary><c>BrAIn --version</c> on the freshly unpacked build: it must start and print a version. That is all it
    /// can prove (the window libraries are only loaded later), but it catches a wrong-architecture archive or a
    /// build that crashes on startup before anything is replaced.</summary>
    private static void SmokeTest(string exe)
    {
        var psi = new ProcessStartInfo(exe, "--version")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("did not start"); }
        catch (Exception ex) { throw new InvalidOperationException("the new version does not run on this system: " + ex.Message, ex); }
        using (p)
        {
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(true); } catch { }
                throw new InvalidOperationException("the new version does not start (timed out)");
            }
            if (p.ExitCode != 0 || !Version.TryParse(err.GetAwaiter().GetResult().Trim(), out _))
                throw new InvalidOperationException("the new version does not run on this system");
        }
    }

    /// <summary>Linux: writes the helper script and starts it detached, in its own session and on pipes of its own (so it
    /// neither dies with a closing terminal nor keeps an MCP client's stdio open after the app is gone).</summary>
    private void StartLinuxHelper(string archive, string root, string fresh)
    {
        var dir = Path.GetDirectoryName(archive)!;
        var script = Path.Combine(dir, "brain-update.sh");
        File.WriteAllText(script, LinuxHelper.Replace("\r\n", "\n"));
        var storeDir = Path.Combine(OsPaths.LocalAppData(), "BrAIn");
        try { Directory.CreateDirectory(storeDir); } catch { storeDir = dir; }
        var psi = new ProcessStartInfo("/bin/sh")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { script, Environment.ProcessId.ToString(), root, fresh, LinuxExe, archive, Path.Combine(storeDir, "update.log") })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start the update helper");
        try { p.StandardInput.Close(); } catch { }
    }

    private static void TryDeleteTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    /// <summary>The helper: waits for the app to exit, swaps the folders (the installed one stays as <c>.old</c> until the
    /// new one is in place and is put back if the swap fails), relaunches whatever is installed now and cleans up.
    /// Arguments: app pid, install folder, new folder, executable name, downloaded archive, log file.</summary>
    internal const string LinuxHelper = """
        #!/bin/sh
        PID=$1 ROOT=$2 NEW=$3 EXE=$4 FILE=$5 LOG=$6
        OLD="$ROOT.old"
        # own session: closing the terminal BrAIn was started from must not stop the update half way
        if [ -z "$BRAIN_UPDATE_DETACHED" ] && command -v setsid >/dev/null 2>&1; then
          BRAIN_UPDATE_DETACHED=1 exec setsid /bin/sh "$0" "$@"
        fi
        trap '' HUP
        exec </dev/null >"$LOG" 2>&1
        say() { printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"; }
        # running = exists and is not a zombie (exited, but its parent never collected it: that is gone, as far as we care)
        alive() {
          kill -0 "$PID" 2>/dev/null || return 1
          case $(sed -n 's/^State:[[:space:]]*\(.\).*/\1/p' "/proc/$PID/status" 2>/dev/null) in Z|X) return 1 ;; esac
          return 0
        }
        say "waiting for BrAIn (pid $PID) to exit"
        t0=$(date +%s)
        while alive; do
          dt=$(( $(date +%s) - t0 ))
          # closing takes a moment, not half a minute: a process that hangs is stopped rather than blocking the update forever
          if [ "$dt" -ge 40 ]; then say "pid $PID would not go away: carrying on"; break
          elif [ "$dt" -ge 30 ]; then kill -KILL "$PID" 2>/dev/null
          elif [ "$dt" -ge 20 ]; then kill -TERM "$PID" 2>/dev/null; fi
          sleep 0.2 2>/dev/null || sleep 1
        done
        rm -rf "$OLD"
        swapped=0
        if [ ! -d "$NEW" ]; then
          say "the new version is missing ($NEW): keeping the installed one"
        elif [ -e "$OLD" ]; then
          say "cannot clear $OLD: keeping the installed one"
        elif mv "$ROOT" "$OLD"; then
          if mv "$NEW" "$ROOT"; then
            swapped=1; say "swapped in the new version"
          else
            say "could not move the new version into place: rolling back"
            if mv "$OLD" "$ROOT"; then say "rolled back"; else say "ROLLBACK FAILED: the previous version is in $OLD"; fi
          fi
        else
          say "could not move the installed version aside: keeping it"
        fi
        [ "$swapped" = 1 ] || rm -rf "$NEW"
        rm -f "$FILE"
        if [ -x "$ROOT/$EXE" ]; then
          say "relaunching $ROOT/$EXE"
          ( cd "$HOME" 2>/dev/null || cd /; exec "$ROOT/$EXE" </dev/null >/dev/null 2>&1 ) &
        else
          say "nothing to relaunch at $ROOT/$EXE"
        fi
        if [ "$swapped" = 1 ]; then
          chmod -R u+w "$OLD" 2>/dev/null
          rm -rf "$OLD" && say "removed the previous version"
        fi
        say "done"
        rm -f "$0"
        """;

    private static string UpdateDir()
    {
        if (!OperatingSystem.IsLinux()) return Path.GetTempPath();
        // Linux: a private folder of the user's own, not a shared /tmp where another user could plant a link at the
        // predictable file name (the download and the helper script are written there).
        try
        {
            var dir = Path.Combine(OsPaths.CacheHome(), "BrAIn", "update");
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return dir;
        }
        catch { return Directory.CreateTempSubdirectory("brain-update-").FullName; }
    }

    private static string NoApplyReason()
    {
        if (!OperatingSystem.IsLinux()) return DevBuild;
        LinuxInstallRoot(out var why);
        return why.Length > 0 ? why : DevBuild;
    }

    /// <summary>Where the running app lives: the .app root on macOS, the install
    /// dir on Windows, the release folder on Linux. Null for dev/unpacked runs — those can't self-update.</summary>
    private static string? InstallRoot()
    {
        if (OperatingSystem.IsLinux()) return LinuxInstallRoot(out _);
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
        if (OperatingSystem.IsLinux())
        {
            // BrAIn-<version>-linux-<x64|arm64>.tar.gz. The architecture of the running PROCESS, not the machine's: the
            // archive replaces this very binary (they differ only under emulation, where the new build must match the old).
            var arch = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };
            return arch is null ? null : rel.Assets.FirstOrDefault(a =>
                a.Name.StartsWith("BrAIn-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith($"-linux-{arch}.tar.gz", StringComparison.OrdinalIgnoreCase));
        }
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
