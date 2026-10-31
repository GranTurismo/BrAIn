using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// Ghost mode with "Keep my cookies &amp; logins": the tabs get their own ephemeral website data
/// store (like a private tab), but it is first filled with a copy of the cookies in the normal
/// store, so the user's signed-in sites open signed in. The copy only goes one way — whatever the
/// ghost tabs set afterwards (new logins, trackers, logouts) lives in memory and is dropped with
/// the last ghost tab; the real store is never written to. Only cookies are copied; sites that
/// keep their sign-in in localStorage / IndexedDB open signed out.
///
/// getAllCookies: / setCookie:completionHandler: take ObjC blocks. Like WkLaAuth, they are
/// hand-built global block literals; the state they need lives in statics (seeding is serialized
/// by the caller, one at a time).
/// </summary>
internal static partial class WkTabs
{
    private static IntPtr _ghostStore;          // the seeded ephemeral store; zero until the first ghost tab asks
    private static IntPtr _seedDst;             // its cookie store, while seeding
    private static int _seedLeft;               // setCookie calls still to complete
    private static TaskCompletionSource? _seedTcs;
    private static IntPtr _gotBlock, _setBlock;

    private const int BlockIsGlobal = 1 << 28;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void GotCookiesImp(IntPtr block, IntPtr cookies)
    {
        try
        {
            var dst = _seedDst;
            var n = cookies == IntPtr.Zero ? 0 : (int)ObjC.ArrayCount(cookies);
            if (n == 0 || dst == IntPtr.Zero) { _seedTcs?.TrySetResult(); return; }
            Volatile.Write(ref _seedLeft, n);
            var done = SetDoneBlock();
            for (var i = 0; i < n; i++)
                ObjC.Send(dst, ObjC.Sel("setCookie:completionHandler:"), ObjC.ArrayAt(cookies, i), done);
        }
        catch { _seedTcs?.TrySetResult(); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void CookieSetImp(IntPtr block)
    {
        if (Interlocked.Decrement(ref _seedLeft) <= 0) _seedTcs?.TrySetResult();
    }

    private static unsafe IntPtr GlobalBlock(void* invoke)
    {
        var lib = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        var isa = NativeLibrary.GetExport(lib, "_NSConcreteGlobalBlock");
        var desc = Marshal.AllocHGlobal(16);
        Marshal.WriteInt64(desc, 0, 0);   // reserved
        Marshal.WriteInt64(desc, 8, 32);  // sizeof literal
        var blk = Marshal.AllocHGlobal(32);
        Marshal.WriteIntPtr(blk, 0, isa);
        Marshal.WriteInt32(blk, 8, BlockIsGlobal);
        Marshal.WriteInt32(blk, 12, 0);
        Marshal.WriteIntPtr(blk, 16, (IntPtr)invoke);
        Marshal.WriteIntPtr(blk, 24, desc);
        return blk;
    }

    private static unsafe IntPtr GotCookiesBlock() =>
        _gotBlock != IntPtr.Zero ? _gotBlock
            : _gotBlock = GlobalBlock((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&GotCookiesImp);

    private static unsafe IntPtr SetDoneBlock() =>
        _setBlock != IntPtr.Zero ? _setBlock
            : _setBlock = GlobalBlock((delegate* unmanaged[Cdecl]<IntPtr, void>)&CookieSetImp);

    /// <summary>True while the seeded ghost store exists (it lives until the last ghost tab closes).</summary>
    internal static bool HasGhostStore => _ghostStore != IntPtr.Zero;

    /// <summary>Main thread. Creates the ghost store and starts copying the normal store's cookies
    /// into it; the task completes once they are all in (or at once when there is nothing to copy,
    /// or the store already exists). The caller serializes calls and bounds the wait.</summary>
    internal static Task SeedGhostStore()
    {
        if (_ghostStore != IntPtr.Zero || _boot == IntPtr.Zero) return Task.CompletedTask;
        var ds = ObjC.Send(ObjC.GetClass("WKWebsiteDataStore"), ObjC.Sel("nonPersistentDataStore"));
        if (ds == IntPtr.Zero) return Task.CompletedTask;
        ObjC.Send(ds, ObjC.Sel("retain"));
        _ghostStore = ds;

        var cfg = ObjC.Send(_boot, ObjC.Sel("configuration"));
        var real = cfg == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(cfg, ObjC.Sel("websiteDataStore"));
        var src = real == IntPtr.Zero ? IntPtr.Zero : ObjC.Send(real, ObjC.Sel("httpCookieStore"));
        var dst = ObjC.Send(ds, ObjC.Sel("httpCookieStore"));
        if (src == IntPtr.Zero || dst == IntPtr.Zero) return Task.CompletedTask; // the store works, it just starts empty

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _seedTcs = tcs;
        _seedDst = dst;
        ObjC.Send(src, ObjC.Sel("getAllCookies:"), GotCookiesBlock());
        return tcs.Task;
    }
}
