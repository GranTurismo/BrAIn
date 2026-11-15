using Photino.NET;

namespace BrAIn.Browser;

/// <summary>
/// Full control — a mode the user turns on from the toolbar, after a warning, in which an AI agent
/// connected over MCP acts without asking first:
///   - Seatbelt stops asking before risky clicks (paying, deleting, sending, agreeing);
///   - site scripts the agent writes run right away, and it may change or delete the user's own;
///   - flows the agent saves are active at once instead of waiting for review;
///   - it may turn Ghost mode off even when the user turned it on.
/// Saved passwords stay out of reach, and Pause / Cancel agent keep working.
///
/// By default it is not saved: it ends after the chosen time or when BrAIn closes. The user can
/// instead pick "never" in the warning (fullctl:on:always) and it is remembered — prefs.json "fcr" —
/// so it is on again at every launch until the user turns it off (turning it off forgets it).
/// Only the user can turn it on or remember it — the command is accepted from the toolbar view, and
/// where the toolbar runs inside web pages (no separate toolbar view) a native dialog that page
/// scripts can't click confirms it first. No MCP tool can switch it on, and it is refused while an
/// agent is producing real mouse or keyboard input (which on macOS could reach the toolbar itself)
/// or for a few seconds after.
/// </summary>
public sealed partial class BrowserController
{
    private volatile bool _fcOn;
    private long _fcUntilMs; // unix ms; 0 = until BrAIn closes
    private Timer? _fcTimer;
    private volatile bool _fcRemember; // "never turn it off": on again at every launch (prefs.json "fcr")

    /// <summary>The agent may act without asking (see the class summary).</summary>
    public bool FullControl
    {
        get
        {
            if (!_fcOn) return false;
            var until = Interlocked.Read(ref _fcUntilMs);
            return until == 0 || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < until;
        }
    }

    /// <summary>{fc} in the b:9 state: whether it is on, when it ends (0 = when BrAIn closes) and whether
    /// it is remembered across launches.</summary>
    private object FullControlObj() => new { on = FullControl, until = FullControl ? Interlocked.Read(ref _fcUntilMs) : 0, always = FullControl && _fcRemember };

    /// <summary>The remembered choice from prefs.json: when the user chose "never", it starts on again.</summary>
    private void LoadFullControlPref(System.Text.Json.JsonElement prefs)
    {
        if (!prefs.TryGetProperty("fcr", out var fcr) || fcr.ValueKind != System.Text.Json.JsonValueKind.True) return;
        _fcRemember = true;
        Interlocked.Exchange(ref _fcUntilMs, 0);
        _fcOn = true;
        Log("full control ON (remembered from the last time; turn it off from the toolbar to forget it)");
    }

    // MCP tools that produce real mouse / keyboard input. On macOS that input is posted at the OS
    // level, so it could reach BrAIn's own toolbar too: full control is never switched on while
    // one of these is running or for a few seconds after, so an agent can't click its own way in.
    private static readonly HashSet<string> AgentInputTools = new(StringComparer.Ordinal)
    {
        "browser_click", "browser_click_at", "browser_double_click", "browser_hover", "browser_type",
        "browser_type_text", "browser_press_key", "browser_select_option", "browser_set_checked",
        "browser_scroll", "browser_scroll_to", "browser_upload",
    };
    private int _agInputBusy;
    private long _agInputAt;

    private void NoteAgentInput(string? tool, int delta)
    {
        if (tool is null || !AgentInputTools.Contains(tool)) return;
        Interlocked.Exchange(ref _agInputAt, Environment.TickCount64);
        if (Interlocked.Add(ref _agInputBusy, delta) < 0) Interlocked.Exchange(ref _agInputBusy, 0);
    }

    private bool AgentInputJustNow()
        => Volatile.Read(ref _agInputBusy) > 0 || Environment.TickCount64 - Interlocked.Read(ref _agInputAt) < 3000;

    /// <summary>fullctl:on:&lt;minutes&gt; (0 = until BrAIn closes) | fullctl:on:always (remembered) | fullctl:off</summary>
    private void FullControlCommand(string arg, bool fromBar)
    {
        var p = arg.Split(':');
        if (p[0] == "off") { SetFullControl(false, 0); return; }
        if (p[0] != "on" || p.Length != 2) return;
        var remember = p[1] == "always";
        var minutes = 0;
        if (!remember && (!int.TryParse(p[1], out minutes) || minutes is < 0 or > 24 * 60)) return;
        if (AgentInputJustNow())
        {
            Log("full control: refused while an agent was using the mouse or keyboard");
            PushFullControlRefused("An AI agent was just using the mouse or keyboard, so full control wasn't turned on. Only you can turn it on — try again in a few seconds.");
            return;
        }
        // The toolbar runs inside web pages here, so a page could have sent this: ask natively.
        if (!fromBar && !ConfirmFullControlNatively(minutes, remember)) { PushFullControlRefused(""); return; }
        SetFullControl(true, minutes, remember);
    }

    /// <summary>{b:98}: the warning stays open and shows why nothing changed.</summary>
    private void PushFullControlRefused(string why)
    {
        var t = UiTab();
        if (_noToolbar || t is null || t.IsClosed) return;
        var json = System.Text.Json.JsonSerializer.Serialize(new { b = 98, why });
        try { InvokeOnUi(() => { if (!t.IsClosed) SendUi(t, json); }); } catch { }
    }

    private bool ConfirmFullControlNatively(int minutes, bool remember)
    {
        var win = _mainWindow;
        if (win is null) return false;
        var span = remember ? "every time BrAIn starts, until you turn it off" : minutes == 0 ? "until BrAIn closes" : minutes < 60 ? $"for {minutes} minutes" : $"for {minutes / 60} hour{(minutes >= 120 ? "s" : "")}";
        try
        {
            return InvokeOnUi(() => win.ShowMessage(
                "Turn on full control?",
                $"An AI agent connected to BrAIn will act as you, without asking first, {span}: " +
                "it can pay, delete, send and publish, run its own site scripts and activate flows. " +
                "Websites can contain hidden instructions that mislead an agent.\n\n" +
                "Only continue if you started this and trust the agent and its task.",
                PhotinoDialogButtons.OkCancel, PhotinoDialogIcon.Warning)) == PhotinoDialogResult.Ok;
        }
        catch (Exception ex) { Log("full control confirmation failed: " + ex.Message); return false; }
    }

    /// <param name="remember">Keep it on at every launch (only with on = true, minutes = 0). Turning it off forgets it.</param>
    private void SetFullControl(bool on, int minutes, bool remember = false)
    {
        _fcTimer?.Dispose();
        _fcTimer = null;
        Interlocked.Exchange(ref _fcUntilMs, on && minutes > 0 ? DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeMilliseconds() : 0);
        _fcOn = on;
        var keep = on && remember;
        var changed = _fcRemember != keep;
        _fcRemember = keep;
        if (on && minutes > 0)
            _fcTimer = new Timer(_ => { if (_fcOn && !FullControl) SetFullControl(false, 0); }, null,
                TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
        if (changed) SavePrefs();
        Log(on ? $"full control ON ({(remember ? "remembered: on at every start" : minutes == 0 ? "until BrAIn closes" : minutes + " min")})" : "full control off");
        PushChromeStateAll();
    }
}
