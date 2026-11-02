using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BrAIn.Browser.Linux;

/// <summary>
/// "Is a person really here?" before the password vault reveals, fills or deletes something — the counterpart of
/// Touch ID on macOS (<c>Mac/WkLaAuth.cs</c>) and the Windows credential prompt. Shows a native dialog; the
/// answer comes back through a task so the caller never blocks the GTK main loop.
///
/// Where PAM can verify the user's own account password (<see cref="LinuxPam"/>) the dialog asks for it: a
/// password entry, three attempts, the check on a worker thread (PAM stalls on a wrong password by design),
/// Esc / Cancel = no. Where it cannot — no libpam, no PAM service, an account without a password, a sandbox
/// that strips the setgid helper PAM needs, a PAM stack that answers "unavailable" — it falls back to the plain
/// Allow / Cancel question this class always had, so nobody is locked out of their own vault.
/// <c>BRAIN_PAM_SERVICE</c> picks the PAM service (default: the first of login, sudo, system-auth that exists);
/// <c>BRAIN_PAM_SERVICE=none</c> turns the password check off.
/// </summary>
internal static unsafe partial class LinuxAuth
{
    private const string LibGtk = "libgtk-3.so.0";
    private const int MaxTries = 3;

    [LibraryImport(LibGtk)] private static partial void gtk_widget_set_sensitive(IntPtr w, int sensitive);
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] private static partial void gtk_label_set_markup(IntPtr label, string markup);
    [LibraryImport(LibGtk)] private static partial void gtk_entry_set_input_purpose(IntPtr entry, int purpose);
    [LibraryImport(LibGtk)] private static partial void gtk_entry_set_width_chars(IntPtr entry, int n);

    public static Task<bool> EvaluateAsync(string reason, Action<Action> invokeOnUi)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Plan? plan = null;
        try { plan = GetPlan(); } catch { /* → plain prompt */ }
        invokeOnUi(() =>
        {
            try
            {
                if (plan is null) ShowAllow(reason, tcs);
                else new PasswordDialog(reason, plan, tcs).Show();
            }
            catch { tcs.TrySetResult(false); }
        });
        return tcs.Task;
    }

    // =====================================================================================================
    // Can PAM verify this user here?
    // =====================================================================================================

    private sealed record Plan(string Service, string User);

    private static Plan? _plan;
    private static bool _planDone;
    private static volatile bool _planBroken;
    private static readonly object _planGate = new();

    private static Plan? GetPlan()
    {
        if (_planBroken) return null;
        lock (_planGate)
        {
            if (!_planDone)
            {
                _planDone = true;
                _plan = ComputePlan(out var why);
                if (_plan is null) Console.Error.WriteLine("[auth] account-password check unavailable (" + why + "); using the Allow/Cancel prompt");
            }
            return _plan;
        }
    }

    private static Plan? ComputePlan(out string why)
    {
        why = "";
        var forced = Environment.GetEnvironmentVariable("BRAIN_PAM_SERVICE");
        if (string.Equals(forced, "none", StringComparison.OrdinalIgnoreCase)) { why = "disabled by BRAIN_PAM_SERVICE=none"; return null; }
        if (!OperatingSystem.IsLinux()) { why = "not Linux"; return null; }
        if (!LinuxPam.IsAvailable()) { why = "libpam.so.0 is not installed"; return null; }
        var user = Environment.UserName;
        if (string.IsNullOrEmpty(user)) { why = "unknown user name"; return null; }

        string? service = null;
        foreach (var s in string.IsNullOrWhiteSpace(forced) ? new[] { "login", "sudo", "system-auth" } : new[] { forced })
            if (PamServiceExists(s)) { service = s; break; }
        if (service is null) { why = "no PAM service configuration found"; return null; }

        // An unprivileged process verifies a password through pam_unix's setgid helper; a sandbox with no_new_privs
        // strips that, and every password would be refused. (Root, or a readable /etc/shadow, needs no helper.)
        if (NoNewPrivs() && Environment.UserName != "root" && !CanOpen("/etc/shadow")) { why = "no_new_privs is set, so PAM's password helper cannot run"; return null; }

        // An account with a locked or empty password has nothing PAM could check: don't ask for it.
        var status = PasswdStatus();
        if (status is not null && (status.StartsWith("L", StringComparison.Ordinal) || status == "NP")) { why = "this account has no usable password (passwd -S: " + status + ")"; return null; }
        return new Plan(service, user);
    }

    private static bool PamServiceExists(string s)
    {
        if (s.Length == 0 || s.Contains('/')) return false;
        foreach (var dir in new[] { "/etc/pam.d", "/usr/lib/pam.d", "/usr/etc/pam.d" })
            if (File.Exists(Path.Combine(dir, s))) return true;
        return false;
    }

    private static bool NoNewPrivs()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("NoNewPrivs:", StringComparison.Ordinal)) return line.AsSpan(11).Trim().SequenceEqual("1");
        }
        catch { }
        return false;
    }

    private static bool CanOpen(string path)
    {
        try { using var f = File.OpenRead(path); return true; }
        catch { return false; }
    }

    /// <summary>Second field of <c>passwd -S</c> for the current user: P/PS (usable), L/LK (locked), NP (none);
    /// null when it cannot be told (no passwd, LDAP/SSSD accounts, any error) — then PAM gets to try.</summary>
    private static string? PasswdStatus()
    {
        foreach (var exe in new[] { "/usr/bin/passwd", "/bin/passwd", "/run/wrappers/bin/passwd" })
        {
            if (!File.Exists(exe)) continue;
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
                };
                psi.ArgumentList.Add("-S");
                psi.Environment["LC_ALL"] = "C";
                using var p = Process.Start(psi);
                if (p is null) return null;
                p.StandardInput.Close();
                if (!p.WaitForExit(3000)) { try { p.Kill(true); } catch { } return null; }
                if (p.ExitCode != 0) return null;
                var parts = p.StandardOutput.ReadToEnd().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? parts[1] : null;
            }
            catch { return null; }
        }
        return null;
    }

    // =====================================================================================================
    // The password dialog
    // =====================================================================================================

    private sealed class PasswordDialog
    {
        private readonly string _reason;
        private readonly Plan _plan;
        private readonly TaskCompletionSource<bool> _tcs;
        private IntPtr _dlg, _entry, _msg, _ok, _token;
        private int _tries;
        private bool _busy, _closed;

        public PasswordDialog(string reason, Plan plan, TaskCompletionSource<bool> tcs) { _reason = reason; _plan = plan; _tcs = tcs; }

        public void Show()
        {
            _dlg = Gtk.gtk_dialog_new();
            Gtk.gtk_window_set_title(_dlg, "BrAIn Passwords");
            var main = LinuxWindow.Window;
            if (main != IntPtr.Zero) Gtk.gtk_window_set_transient_for(_dlg, main);
            Gtk.gtk_window_set_modal(_dlg, 1);
            Gtk.gtk_window_set_position(_dlg, 4 /* GTK_WIN_POS_CENTER_ON_PARENT */);
            var area = Gtk.gtk_dialog_get_content_area(_dlg);
            Gtk.gtk_container_set_border_width(area, 18);
            Gtk.gtk_box_set_spacing(area, 10);

            AddLabel(area, "BrAIn wants to " + _reason + ".");
            AddLabel(area, "Enter the password for " + _plan.User + " to confirm it's you.");

            _entry = Gtk.gtk_entry_new();
            Gtk.gtk_entry_set_visibility(_entry, 0);
            gtk_entry_set_input_purpose(_entry, 8 /* GTK_INPUT_PURPOSE_PASSWORD */);
            gtk_entry_set_width_chars(_entry, 30);
            Gtk.gtk_entry_set_activates_default(_entry, 1);
            Gtk.gtk_box_pack_start(area, _entry, 0, 0, 0);

            _msg = AddLabel(area, " ");

            Gtk.gtk_dialog_add_button(_dlg, "Cancel", Gtk.ResponseCancel);
            _ok = Gtk.gtk_dialog_add_button(_dlg, "Continue", Gtk.ResponseOk);
            Gtk.gtk_dialog_set_default_response(_dlg, Gtk.ResponseOk);
            _token = GLib.Stash(this);
            GLib.Connect(_dlg, "response", (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnResponse, _token);
            Gtk.gtk_widget_show_all(_dlg);
            Gtk.gtk_window_present(_dlg);
            Gtk.gtk_widget_grab_focus(_entry);
        }

        private static IntPtr AddLabel(IntPtr area, string text)
        {
            var label = Gtk.gtk_label_new(text);
            Gtk.gtk_label_set_line_wrap(label, 1);
            Gtk.gtk_label_set_max_width_chars(label, 52);
            Gtk.gtk_label_set_xalign(label, 0f);
            Gtk.gtk_box_pack_start(area, label, 0, 0, 0);
            return label;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void OnResponse(IntPtr dlg, int response, IntPtr token)
        {
            var d = GLib.Peek<PasswordDialog>(token);
            if (d is null || d._closed) return;
            try
            {
                if (response == Gtk.ResponseOk) { if (!d._busy) d.Submit(); }
                else d.Finish(false); // Cancel, Esc, window close
            }
            catch { d.Finish(false); }
        }

        private void Submit()
        {
            var text = Gtk.gtk_entry_get_text(_entry);
            var pw = CopyCString(text);
            if (pw.Length == 0) { SetMessage("Enter your password."); return; } // a blank password is never right — don't spend an attempt
            // GTK3 cannot scrub an entry, but replacing the text with a filler of the same byte length overwrites its
            // copy in place (same allocation); then empty it. (Writing through the const pointer would corrupt the entry.)
            Gtk.gtk_entry_set_text(_entry, new string('*', pw.Length));
            Gtk.gtk_entry_set_text(_entry, "");

            _tries++;
            _busy = true;
            gtk_widget_set_sensitive(_entry, 0);
            gtk_widget_set_sensitive(_ok, 0);
            SetMessage("Checking…");

            var service = _plan.Service;
            var user = _plan.User;
            _ = Task.Run(() =>
            {
                LinuxPam.Verdict v;
                var code = 0;
                try { v = LinuxPam.Authenticate(service, user, pw, out code); }
                catch { v = LinuxPam.Verdict.Unusable; }
                finally { CryptographicOperations.ZeroMemory(pw); }
                GLib.RunOnMainLoop(() => OnVerdict(v, code));
            });
        }

        private void OnVerdict(LinuxPam.Verdict v, int code)
        {
            if (_closed) return; // cancelled while PAM was still thinking
            _busy = false;
            if (v == LinuxPam.Verdict.Ok) { Finish(true); return; }

            if (v == LinuxPam.Verdict.Unusable && _tries == 1)
            {
                // PAM cannot check anything here: remember that and ask the plain question instead.
                _planBroken = true;
                Console.Error.WriteLine("[auth] PAM cannot verify this account (code " + code + "); using the Allow/Cancel prompt");
                Close();
                ShowAllow(_reason, _tcs);
                return;
            }
            if (_tries >= MaxTries) { Finish(false); return; }

            var left = MaxTries - _tries;
            SetMessage(v switch
            {
                LinuxPam.Verdict.Denied => "This account can't be used to sign in right now.",
                LinuxPam.Verdict.Unusable => "The password service did not answer. Try again.",
                _ => "Incorrect password. " + left + (left == 1 ? " try" : " tries") + " left.",
            }, error: true);
            gtk_widget_set_sensitive(_entry, 1);
            gtk_widget_set_sensitive(_ok, 1);
            Gtk.gtk_widget_grab_focus(_entry);
        }

        private void SetMessage(string text, bool error = false)
        {
            var esc = text.Replace("&", "&amp;").Replace("<", "&lt;");
            gtk_label_set_markup(_msg, error ? "<span foreground=\"#c01c28\">" + esc + "</span>" : esc);
        }

        private void Close()
        {
            _closed = true;
            GLib.Unstash<PasswordDialog>(_token);
            Gtk.gtk_widget_destroy(_dlg);
        }

        private void Finish(bool ok)
        {
            _tcs.TrySetResult(ok);
            Close();
        }
    }

    private static byte[] CopyCString(IntPtr p)
    {
        if (p == IntPtr.Zero) return Array.Empty<byte>();
        var n = 0;
        while (((byte*)p)[n] != 0) n++;
        return new ReadOnlySpan<byte>((void*)p, n).ToArray();
    }

    // =====================================================================================================
    // The plain Allow / Cancel question (no password to check on this machine)
    // =====================================================================================================

    private sealed class Ask { public readonly TaskCompletionSource<bool> Tcs; public Ask(TaskCompletionSource<bool> tcs) { Tcs = tcs; } }

    private static void ShowAllow(string reason, TaskCompletionSource<bool> tcs)
    {
        var ask = new Ask(tcs);
        var dlg = Gtk.gtk_dialog_new();
        Gtk.gtk_window_set_title(dlg, "BrAIn Passwords");
        var main = LinuxWindow.Window;
        if (main != IntPtr.Zero) Gtk.gtk_window_set_transient_for(dlg, main);
        Gtk.gtk_window_set_modal(dlg, 1);
        Gtk.gtk_window_set_position(dlg, 4);
        var area = Gtk.gtk_dialog_get_content_area(dlg);
        Gtk.gtk_container_set_border_width(area, 18);
        var label = Gtk.gtk_label_new("BrAIn wants to " + reason + ".");
        Gtk.gtk_label_set_line_wrap(label, 1);
        Gtk.gtk_label_set_xalign(label, 0f);
        Gtk.gtk_box_pack_start(area, label, 0, 0, 0);
        Gtk.gtk_dialog_add_button(dlg, "Cancel", Gtk.ResponseCancel);
        Gtk.gtk_dialog_add_button(dlg, "Allow", Gtk.ResponseOk);
        Gtk.gtk_dialog_set_default_response(dlg, Gtk.ResponseCancel);
        GLib.Connect(dlg, "response", (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnAllowResponse, GLib.Stash(ask));
        Gtk.gtk_widget_show_all(dlg);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void OnAllowResponse(IntPtr dlg, int response, IntPtr token)
    {
        var ask = GLib.Unstash<Ask>(token);
        ask?.Tcs.TrySetResult(response == Gtk.ResponseOk);
        Gtk.gtk_widget_destroy(dlg);
    }
}
