using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Checks, before anything else runs, that the one system library .NET needs and cannot do without is there: OpenSSL 3
/// (<c>libssl.so.3</c>; 1.1 works too). .NET's crypto and TLS on Linux sit on top of it — the update check, hashing a
/// favicon name, the password vault — and when no version it knows can be loaded the runtime does not throw: it prints
/// "No usable version of libssl was found" and <c>abort()</c>s the whole process, a few seconds after the window appeared
/// (nothing in managed code can catch that). Fedora 45 ships OpenSSL 4 (<c>libssl.so.4</c>) and keeps OpenSSL 3 in a
/// separate package, so a stock Fedora 45 desktop hit exactly that. Here the user gets an explanation instead.
/// </summary>
internal static unsafe class LinuxPreflight
{
    /// <summary>True when .NET will find an OpenSSL it can use. Otherwise the reason has been shown (stderr, plus a dialog for
    /// a desktop launch) and the caller should exit.</summary>
    internal static bool Ok()
    {
        try
        {
            // the user knows better (a custom build, a non-standard soname): .NET reads this itself, so stay out of its way
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLR_OPENSSL_VERSION_OVERRIDE"))) return true;
            foreach (var name in new[] { "libssl.so.3", "libssl.so.1.1", "libssl.so.1.0.0", "libssl.so.10" })
                if (NativeLibrary.TryLoad(name, out var h)) { NativeLibrary.Free(h); return true; }
        }
        catch { return true; } // the check itself must never be the thing that stops the browser
        Explain();
        return false;
    }

    private static void Explain()
    {
        var (distro, hint) = Hint();
        var text =
            "BrAIn needs the OpenSSL 3 library (libssl.so.3), and this system does not provide it.\n\n" +
            (distro == "fedora"
                ? "Fedora 45 and newer ship OpenSSL 4, which the .NET runtime inside BrAIn cannot use yet; OpenSSL 3 is a separate package.\n\n"
                : "") +
            "Install it with:\n    " + hint + "\n\nThen start BrAIn again.";
        Console.Error.WriteLine("[BrAIn] " + text.Replace("\n", "\n[BrAIn] "));
        // a desktop launch (no terminal, nobody reading stderr) gets a window; an MCP client that started us over pipes only
        // gets stderr and the exit status — a dialog would hang its launch until somebody clicked it
        if (!LaunchedByPipe()) try { ShowDialog(text); } catch { }
    }

    /// <summary>(distro id, install command) for the OpenSSL 3 package.</summary>
    private static (string, string) Hint()
    {
        string id = "", like = "";
        try
        {
            foreach (var l in File.ReadAllLines("/etc/os-release"))
            {
                if (l.StartsWith("ID=")) id = l[3..].Trim('"');
                else if (l.StartsWith("ID_LIKE=")) like = l[8..].Trim('"');
            }
        }
        catch { }
        var all = " " + id + " " + like + " ";
        if (all.Contains(" fedora ") || all.Contains(" rhel ") || all.Contains(" centos "))
            return ("fedora", "sudo dnf install openssl3-libs        (older Fedora / RHEL: sudo dnf install openssl-libs)");
        if (all.Contains(" debian ") || all.Contains(" ubuntu ")) return ("debian", "sudo apt install libssl3");
        if (all.Contains(" suse ") || all.Contains(" opensuse ")) return ("suse", "sudo zypper install libopenssl3");
        if (all.Contains(" arch ")) return ("arch", "sudo pacman -S openssl   (it must provide libssl.so.3)");
        return (id, "the package that provides libssl.so.3 (OpenSSL 3.x)");
    }

    /// <summary>True when stdin is a pipe or socket: an MCP client started us. A desktop launcher gives /dev/null, a terminal a tty.</summary>
    private static bool LaunchedByPipe()
    {
        try
        {
            var t = new FileInfo("/proc/self/fd/0").LinkTarget ?? "";
            return t.StartsWith("pipe:", StringComparison.Ordinal) || t.StartsWith("socket:", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static void ShowDialog(string text)
    {
        int argc = 0;
        if (Gtk.gtk_init_check(ref argc, IntPtr.Zero) == 0) return; // no display: stderr is all there is
        var dlg = Gtk.gtk_dialog_new();
        Gtk.gtk_window_set_title(dlg, "BrAIn can't start");
        Gtk.gtk_window_set_position(dlg, 1 /* GTK_WIN_POS_CENTER */);
        Gtk.gtk_window_set_default_size(dlg, 560, 10); // as wide as the text wants, as tall as the text needs
        var area = Gtk.gtk_dialog_get_content_area(dlg);
        Gtk.gtk_container_set_border_width(area, 18);
        var label = Gtk.gtk_label_new(text);
        Gtk.gtk_label_set_line_wrap(label, 1);
        Gtk.gtk_label_set_xalign(label, 0f);
        Gtk.gtk_label_set_selectable(label, 1); // so the command can be copied
        Gtk.gtk_label_set_max_width_chars(label, 70);
        Gtk.gtk_container_add(area, label);
        Gtk.gtk_dialog_add_button(dlg, "Close", Gtk.ResponseOk);
        Gtk.gtk_widget_show_all(dlg);
        Gtk.gtk_dialog_run(dlg);
        Gtk.gtk_widget_destroy(dlg);
    }
}
