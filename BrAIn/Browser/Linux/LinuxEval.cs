using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Evaluates JavaScript in a WebKitGTK view and hands back what it returned as a string — straight through WebKit's own
/// API, not the page's message channel. Used by the terminal viewer's text layer (reading where the text is on the page
/// and in the toolbar), which must not appear in the agent activity log and must not depend on page scripts: a page view
/// is evaluated in an isolated script world, where the page's own JavaScript cannot see or change what we run.
/// Start on the GTK main thread; the task completes on the main loop.
/// </summary>
internal static unsafe class LinuxEval
{
    private static readonly ConcurrentDictionary<nint, TaskCompletionSource<string?>> Pending = new();
    private static long _next;

    internal static bool Available => WebKit.Has("webkit_web_view_evaluate_javascript");

    /// <summary>The script's result: its string value, JSON for anything else, null for undefined/null or an error.</summary>
    internal static Task<string?> EvalAsync(IntPtr view, string js, string? world)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (view == IntPtr.Zero || !Available) { tcs.SetResult(null); return tcs.Task; }
        var id = (nint)Interlocked.Increment(ref _next);
        Pending[id] = tcs;
        WebKit.webkit_web_view_evaluate_javascript(view, js, -1, world, null, IntPtr.Zero,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&Done, id);
        return tcs.Task;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void Done(IntPtr view, IntPtr result, IntPtr data)
    {
        if (!Pending.TryRemove(data, out var tcs)) return;
        try
        {
            var v = WebKit.webkit_web_view_evaluate_javascript_finish(view, result, out var err);
            if (err != IntPtr.Zero) GLib.g_error_free(err);
            if (v == IntPtr.Zero) { tcs.TrySetResult(null); return; }
            try
            {
                string? s = null;
                if (WebKit.jsc_value_is_undefined(v) == 0 && WebKit.jsc_value_is_null(v) == 0)
                {
                    var p = WebKit.jsc_value_is_string(v) != 0 ? WebKit.jsc_value_to_string(v) : WebKit.jsc_value_to_json(v, 0);
                    if (p != IntPtr.Zero)
                    {
                        s = Marshal.PtrToStringUTF8(p);
                        GLib.g_free(p);
                    }
                }
                tcs.TrySetResult(s);
            }
            finally { GLib.g_object_unref(v); }
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
    }
}
