namespace BrAIn.Browser.Linux;

/// <summary>
/// Opening straight into fullscreen (Linux counterpart of <c>Mac/WkLaunch.cs</c>). Photino shows the window the
/// moment it is created, at 1440×900; the app keeps it off screen until the first page has settled and then maps
/// it already fullscreen, so there is no "opens normal and then goes fullscreen". Whatever goes wrong,
/// <see cref="Reveal"/> is also called from a watchdog, so the window can never stay invisible.
/// </summary>
internal static class LinuxLaunch
{
    private static volatile bool _hidden;

    /// <summary>True from <see cref="Hide"/> until <see cref="Reveal"/> ran.</summary>
    internal static bool Hidden => _hidden;

    /// <summary>Main thread. Takes the app's window off screen until <see cref="Reveal"/>.</summary>
    internal static void Hide()
    {
        foreach (var w in LinuxWindow.AllWindows())
        {
            if (Gtk.gtk_widget_get_visible(w) == 0) continue;
            Gtk.gtk_widget_hide(w);
            _hidden = true;
        }
    }

    /// <summary>Main thread. Shows the window, fullscreen when asked (the hint is set before it is mapped, so it
    /// appears at its final size).</summary>
    internal static void Reveal(bool fullscreen)
    {
        if (!_hidden) return;
        _hidden = false;
        foreach (var w in LinuxWindow.AllWindows())
        {
            if (Gtk.gtk_widget_get_visible(w) != 0) continue;
            if (fullscreen) Gtk.gtk_window_fullscreen(w);
            Gtk.gtk_widget_show(w);
            Gtk.gtk_window_present(w);
        }
    }
}
