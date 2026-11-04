using System.Globalization;

namespace BrAIn.Browser;

/// <summary>
/// macOS fullscreen: the real traffic lights are gone, so the toolbar's reserved left inset (<c>.bar.mtl</c>) is empty — BrAIn's
/// memory use is shown there, live. The toolbar learns that the window is fullscreen from the state push (<c>fs</c>); while it
/// is, a once-a-second sampler sends <c>{"b":110,"mb":n}</c> (app + WebKit helper processes, see <see cref="Mac.MacMemory"/>).
/// Nothing runs while the window is not fullscreen.
/// </summary>
public sealed partial class BrowserController
{
    private volatile bool _macFs;
    private Timer? _memTimer;

    /// <summary>Main thread (window size changed): note whether the macOS window is fullscreen, tell the toolbar when it changes,
    /// and run the memory sampler only while it is.</summary>
    private void UpdateMacFullscreen(IntPtr nsWin)
    {
        if (!IsMac || nsWin == IntPtr.Zero || _noToolbar) return;
        bool fs;
        try { fs = Mac.MacWindow.IsFullScreen(nsWin); } catch { return; }
        if (fs == _macFs) return;
        _macFs = fs;
        PushChromeStateAll(); // the state carries fs: the toolbar shows or hides the readout
        if (fs) _memTimer ??= new Timer(_ => SampleMemory(), null, 150, 1000);
        else { _memTimer?.Dispose(); _memTimer = null; }
    }

    private void SampleMemory()
    {
        try
        {
            if (!_macFs) return;
            var mb = Mac.MacMemory.TreeFootprintMb();
            if (mb is null) return;
            var json = "{\"b\":110,\"mb\":" + Math.Round(mb.Value).ToString(CultureInfo.InvariantCulture) + "}";
            InvokeOnUi(() =>
            {
                if (SplitOn) { if (NativeShell.BarExists) NativeShell.BarEval("window.__brainBarPush&&window.__brainBarPush(" + json + ")"); }
                else if (Tabs.Main is { IsClosed: false } t) SendUi(t, json);
            });
        }
        catch { /* window mid-close */ }
    }
}
