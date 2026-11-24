using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// Camera and microphone for web pages (macOS). Photino's own WKUIDelegate answers
/// -webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler: with "prompt", and WebKit then
/// shows its own sheet at every page load — a video call site asked again at every call. BrAInUiDelegate (see
/// <see cref="WkPopup"/>) sends that request here instead:
///   - a site that was allowed or refused before gets the same answer without a question (<see cref="SitePermissions"/>);
///   - otherwise one question, with a "Remember my choice for this site" box that is ticked by default;
///   - private tabs and Ghost mode always ask and never remember or read what was remembered;
///   - <c>--grant-permissions</c> allows everything; a terminal-only (invisible) window falls back to Photino's behaviour,
///     because a question nobody can see would hang the page.
/// macOS' own camera / microphone switch (System Settings → Privacy) still applies on top of this.
/// Everything here runs on the main thread, inside the engine's callback.
/// </summary>
internal static unsafe partial class WkPermissions
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    [LibraryImport(LibSystem, EntryPoint = "_Block_copy")]
    private static partial IntPtr BlockCopy(IntPtr block);

    [LibraryImport(LibSystem, EntryPoint = "_Block_release")]
    private static partial void BlockRelease(IntPtr block);

    /// <summary>The original delegate's version of the request — what Photino did before we stood in front of it.</summary>
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void SendMedia(IntPtr recv, IntPtr sel, IntPtr webView, IntPtr origin, IntPtr frame, nint type, IntPtr handler);

    /// <summary>--grant-permissions / BRAIN_GRANT_PERMISSIONS: allow without asking.</summary>
    internal static bool GrantAll;
    internal static Action<string>? Log;

    // WKMediaCaptureType
    private const nint TypeCamera = 0, TypeMic = 1, TypeBoth = 2;
    // WKPermissionDecision
    private const nint Prompt = 0, Grant = 1, Deny = 2;

    internal static IntPtr SelRequest => ObjC.Sel("webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:");

    /// <summary>Requests that arrived while a question for the same site and kind is on screen: answered with it.</summary>
    private static readonly Dictionary<string, List<IntPtr>> _waiting = new();

    /// <summary>Answer to the engine's request. <paramref name="orig"/> is the delegate Photino installed (may be zero).</summary>
    internal static void OnRequest(IntPtr orig, IntPtr webView, IntPtr origin, IntPtr frame, nint type, IntPtr handler)
    {
        try
        {
            if (handler == IntPtr.Zero) return;
            if (GrantAll) { Answer(handler, Grant); return; }
            if (MacView.Headless || origin == IntPtr.Zero)
            {
                // nobody could see a question: do what Photino did
                if (orig != IntPtr.Zero && ObjC.SendBool(orig, ObjC.Sel("respondsToSelector:"), SelRequest) != 0)
                    SendMedia(orig, SelRequest, webView, origin, frame, type, handler);
                else Answer(handler, Deny);
                return;
            }

            var host = ObjC.NsStringToString(ObjC.Send(origin, ObjC.Sel("host"))) ?? "";
            var site = SitePermissions.OriginOf(ObjC.NsStringToString(ObjC.Send(origin, ObjC.Sel("protocol"))), host,
                ObjC.SendNInt(origin, ObjC.Sel("port")));
            var kinds = type switch
            {
                TypeCamera => new[] { SitePermissions.Camera },
                TypeMic => new[] { SitePermissions.Microphone },
                _ => new[] { SitePermissions.Camera, SitePermissions.Microphone },
            };
            var priv = WkTabs.IsPrivateView(webView);

            if (site is not null && !priv)
            {
                var states = kinds.Select(k => SitePermissions.Get(site, k)).ToList();
                if (states.Any(s => s == false)) { Log?.Invoke($"camera/microphone for {site}: refused before, not asked again"); Answer(handler, Deny); return; }
                if (states.All(s => s == true)) { Log?.Invoke($"camera/microphone for {site}: allowed before, not asked again"); Answer(handler, Grant); return; }
            }

            // a second request for the same thing while the question is open waits for the same answer
            var key = (priv ? "p|" : "n|") + (site ?? host) + "|" + string.Join('+', kinds);
            if (_waiting.TryGetValue(key, out var queue)) { queue.Add(BlockCopy(handler)); return; }
            _waiting[key] = new List<IntPtr>();

            nint decision;
            try
            {
                var canRemember = site is not null && !priv;
                var allow = Ask(host.Length > 0 ? host : "This page", type, canRemember, out var remembered);
                decision = allow ? Grant : Deny;
                if (remembered && site is not null) SitePermissions.Set(site, allow, kinds);
                Log?.Invoke($"camera/microphone for {site ?? host}: asked, {(allow ? "allowed" : "refused")}{(remembered ? " and remembered" : "")}");
            }
            catch (Exception ex) { Log?.Invoke("camera/microphone question failed: " + ex.Message); decision = Deny; }

            Answer(handler, decision);
            if (_waiting.Remove(key, out var late))
                foreach (var h in late) { Answer(h, decision); BlockRelease(h); }
        }
        catch (Exception ex)
        {
            Log?.Invoke("camera/microphone request failed: " + ex.Message);
            try { Answer(handler, Deny); } catch { }
        }
    }

    // ObjC block layout (64-bit): isa, int flags, int reserved, invoke fn pointer at +16.
    private static void Answer(IntPtr block, nint decision)
        => ((delegate* unmanaged<IntPtr, nint, void>)Marshal.ReadIntPtr(block, 16))(block, decision);

    /// <summary>The question. true = allow. <paramref name="remember"/> comes back as the box's state when it was offered.</summary>
    private static bool Ask(string host, nint type, bool canRemember, out bool rememberChoice)
    {
        var remember = canRemember;
        var what = type switch { TypeCamera => "your camera", TypeMic => "your microphone", _ => "your camera and microphone" };
        var alert = ObjC.Send(ObjC.Send(ObjC.GetClass("NSAlert"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        ObjC.Send(alert, ObjC.Sel("setMessageText:"), ObjC.NsString($"Allow {host} to use {what}?"));
        ObjC.Send(alert, ObjC.Sel("setInformativeText:"), ObjC.NsString(remember
            ? "This site is asking to start a call or recording. BrAIn can remember your answer, so it will not ask again."
            : "This site is asking to start a call or recording. A private tab never remembers permissions."));
        ObjC.Send(alert, ObjC.Sel("addButtonWithTitle:"), ObjC.NsString("Allow"));
        ObjC.Send(alert, ObjC.Sel("addButtonWithTitle:"), ObjC.NsString("Don’t Allow"));
        IntPtr box = IntPtr.Zero;
        if (remember)
        {
            ObjC.Send(alert, ObjC.Sel("setShowsSuppressionButton:"), (IntPtr)1);
            box = ObjC.Send(alert, ObjC.Sel("suppressionButton"));
            if (box != IntPtr.Zero)
            {
                ObjC.Send(box, ObjC.Sel("setTitle:"), ObjC.NsString("Remember my choice for this site"));
                ObjC.Send(box, ObjC.Sel("setState:"), (IntPtr)1);
            }
        }
        // above the full-screen browser window, so the question cannot end up hidden behind it
        var win = ObjC.Send(alert, ObjC.Sel("window"));
        if (win != IntPtr.Zero) ObjC.Send(win, ObjC.Sel("setLevel:"), (IntPtr)3);

        var answer = ObjC.SendNInt(alert, ObjC.Sel("runModal"));
        rememberChoice = remember && box != IntPtr.Zero && ObjC.SendNInt(box, ObjC.Sel("state")) != 0;
        ObjC.Send(alert, ObjC.Sel("release"));
        return answer == 1000; // NSAlertFirstButtonReturn
    }
}
