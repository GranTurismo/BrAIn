using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// Opening straight into fullscreen. The window used to be created at 1440×900 and sit there, visible,
/// while the first page loaded and the Space animation was requested — "it opens normal and then goes
/// fullscreen". Now the app's window is made invisible (alpha 0) the moment it exists and stays so until
/// macOS reports that fullscreen is reached; it then fades in already full screen. Whatever goes wrong,
/// <see cref="Reveal"/> is also called from a watchdog, so the window can never stay invisible.
///
/// The "did enter fullscreen" notification takes an ObjC block: a hand-built global block literal like
/// the ones in WkLaAuth / WkTabs.Ghost, with its state in statics (one fullscreen entry per launch).
/// </summary>
internal static class WkLaunch
{
    private static TaskCompletionSource? _entered;
    private static IntPtr _enteredBlock;
    private static volatile bool _hidden;

    /// <summary>True from <see cref="Hide"/> until <see cref="Reveal"/> ran.</summary>
    internal static bool Hidden => _hidden;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void EnteredImp(IntPtr block, IntPtr notification) => _entered?.TrySetResult();

    private static unsafe IntPtr EnteredBlock() =>
        _enteredBlock != IntPtr.Zero ? _enteredBlock
            : _enteredBlock = WkTabs.GlobalBlock((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&EnteredImp);

    private static void SetAlpha(IntPtr w, double a) => ObjC.SendDoubleArg(w, ObjC.Sel("setAlphaValue:"), a);

    /// <summary>Main thread. Makes the app's window invisible until <see cref="Reveal"/>.</summary>
    internal static void Hide()
    {
        foreach (var w in MacWindow.AllWindows())
        {
            if ((ObjC.SendNInt(w, ObjC.Sel("isVisible")) & 0xFF) == 0) continue;
            SetAlpha(w, 0);
            _hidden = true;
        }
    }

    /// <summary>Main thread. The returned task completes when macOS says the window finished entering
    /// fullscreen (the Space animation is over). Call before asking for fullscreen.</summary>
    internal static Task WatchFullScreen(IntPtr window)
    {
        if (window == IntPtr.Zero) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _entered = tcs;
        var center = ObjC.Send(ObjC.GetClass("NSNotificationCenter"), ObjC.Sel("defaultCenter"));
        ObjC.Send(center, ObjC.Sel("addObserverForName:object:queue:usingBlock:"),
            ObjC.NsString("NSWindowDidEnterFullScreenNotification"), window, IntPtr.Zero, EnteredBlock());
        return tcs.Task;
    }

    /// <summary>Main thread. Fades the window in (a quarter of a second).</summary>
    internal static void Reveal()
    {
        if (!_hidden) return;
        _hidden = false;
        foreach (var w in MacWindow.AllWindows())
        {
            if (ObjC.SendDouble(w, ObjC.Sel("alphaValue")) >= 1) continue;
            var ctxClass = ObjC.GetClass("NSAnimationContext");
            ObjC.Send(ctxClass, ObjC.Sel("beginGrouping"));
            ObjC.SendDoubleArg(ObjC.Send(ctxClass, ObjC.Sel("currentContext")), ObjC.Sel("setDuration:"), 0.25);
            ObjC.SendDoubleArg(ObjC.Send(w, ObjC.Sel("animator")), ObjC.Sel("setAlphaValue:"), 1.0);
            ObjC.Send(ctxClass, ObjC.Sel("endGrouping"));
        }
    }
}
