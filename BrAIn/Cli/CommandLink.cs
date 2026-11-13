using System.Diagnostics;
using System.Text.RegularExpressions;
using BrAIn.Browser;

namespace BrAIn.Cli;

/// <summary>
/// macOS: the app installs its own `brain` command. A Mac app is dragged into place or updated in place, so nothing ever runs an
/// installer that could put the command on the PATH (on Linux install.sh does). A few seconds after start BrAIn links
/// <c>brain</c> to its own program in a folder that is on the user's PATH — asked from their login shell, own folders first —
/// unless a `brain` already exists. It never replaces a different program of that name, keeps a link that points at a moved or
/// replaced BrAIn current, and remembers that it made one: a link the user deleted is not made again.
/// </summary>
internal static partial class CommandLink
{
    internal static void EnsureLater()
    {
        if (!OperatingSystem.IsMacOS()) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            try
            {
                var r = Ensure();
                if (r is not null) BrowserController.Log("brain command: " + r);
            }
            catch (Exception ex) { BrowserController.Log("brain command: not linked (" + ex.Message + ")"); }
        });
    }

    internal static string? Ensure()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !exe.Contains(".app/Contents/MacOS/", StringComparison.Ordinal)) return null; // a development build
        var home = OsPaths.Home();
        var marker = Path.Combine(ControlPaths.StoreDir(), "brain-command.txt");

        // settled before: keep that link pointing at a BrAIn that exists; if the user removed it, leave it removed
        if (File.Exists(marker))
        {
            var known = File.ReadLines(marker).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(known) ? null : Repoint(known, exe);
        }

        var path = LoginPath();
        var candidates = new[] { Path.Combine(home, ".local", "bin"), Path.Combine(home, "bin"), "/usr/local/bin", "/opt/homebrew/bin" };

        // is there a `brain` already, anywhere the shell looks?
        foreach (var dir in path.Concat(candidates).Distinct())
        {
            var p = Path.Combine(dir, "brain");
            var fi = new FileInfo(p);
            if (fi.LinkTarget is { } target)
            {
                if (!target.Contains("BrAIn.app/Contents/MacOS/", StringComparison.Ordinal)) return null; // someone else's
                Remember(marker, p);   // made by hand or by an older BrAIn: from now on it is the one kept current
                return Repoint(p, exe);
            }
            if (fi.Exists) return null; // a different program called brain: leave it alone
        }

        // a folder on the PATH (own folders first, made if missing); otherwise ~/.local/bin
        bool Usable(string d) => Directory.Exists(d) ? Writable(d) : d.StartsWith(home + "/", StringComparison.Ordinal);
        var onPath = candidates.FirstOrDefault(d => path.Contains(d) && Usable(d))
                     ?? path.FirstOrDefault(d => d.StartsWith(home + "/", StringComparison.Ordinal) && Directory.Exists(d) && Writable(d));
        var folder = onPath ?? Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(folder);
        var link = Path.Combine(folder, "brain");
        File.CreateSymbolicLink(link, exe);
        Remember(marker, link);
        return "linked " + link + " -> " + exe + (onPath is null ? " (that folder is not on your PATH yet)" : "");
    }

    /// <summary>A link of ours whose BrAIn is gone (the app was moved or renamed) is pointed at this one.</summary>
    private static string? Repoint(string link, string exe)
    {
        var fi = new FileInfo(link);
        if (fi.LinkTarget is not { } target) return null;                                           // removed, or now a different file
        if (!target.Contains("BrAIn.app/Contents/MacOS/", StringComparison.Ordinal)) return null;   // someone else's now
        if (target == exe || File.Exists(target)) return null;                                     // fine as it is
        File.Delete(link);
        File.CreateSymbolicLink(link, exe);
        return "updated " + link + " -> " + exe;
    }

    private static void Remember(string marker, string link)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, link + "\n");
    }

    private static bool Writable(string dir)
    {
        try
        {
            var t = Path.Combine(dir, $".brain-write-test-{Environment.ProcessId}");
            File.WriteAllText(t, "");
            File.Delete(t);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The PATH the user's login shell sets up (a Finder-started app only gets launchd's short default).</summary>
    private static string[] LoginPath()
    {
        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = "/bin/zsh";
            var psi = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-ilc");
            psi.ArgumentList.Add("printf '__BRAIN_PATH__%s__BRAIN_PATH__' \"$PATH\"");
            using var p = Process.Start(psi);
            if (p is null) return [];
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            if (!read.Wait(4000)) { try { p.Kill(true); } catch { } return []; }
            var m = Marker().Match(read.Result);
            return m.Success ? m.Groups[1].Value.Split(':', StringSplitOptions.RemoveEmptyEntries) : [];
        }
        catch { return []; }
    }

    [GeneratedRegex("__BRAIN_PATH__(.*?)__BRAIN_PATH__", RegexOptions.Singleline)]
    private static partial Regex Marker();
}
