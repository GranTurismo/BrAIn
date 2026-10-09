using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// LocalAuthentication interop — LAPolicyDeviceOwnerAuthentication prompts for
/// Touch ID and falls back to the Mac password. evaluatePolicy takes an ObjC
/// reply block; we hand-build a global block literal (_NSConcreteGlobalBlock +
/// invoke fn pointer) whose callback completes a TaskCompletionSource.
/// Blocks passed to evaluatePolicy are copied by the API — a global block's
/// copy is itself, so the single shared literal stays valid for every call.
/// </summary>
internal static class WkLaAuth
{
    private const nint DeviceOwnerAuthentication = 2; // biometrics w/ passcode fallback

    // Block literal ABI (64-bit): isa@0, flags@8, reserved@12, invoke@16, descriptor@24.
    private const int BlockIsGlobal = 1 << 28;

    private static IntPtr _replyBlock;
    private static TaskCompletionSource<bool>? _pending;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ReplyImp(IntPtr block, byte success, IntPtr error)
    {
        _pending?.TrySetResult(success != 0);
    }

    /// <summary>One shared global block for all evaluatePolicy calls —
    /// auths are serialized at the call site so a single pending TCS suffices.</summary>
    private static unsafe IntPtr ReplyBlock()
    {
        if (_replyBlock != IntPtr.Zero) return _replyBlock;
        var lib = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        var isa = NativeLibrary.GetExport(lib, "_NSConcreteGlobalBlock");
        var desc = Marshal.AllocHGlobal(16);
        Marshal.WriteInt64(desc, 0, 0);   // reserved
        Marshal.WriteInt64(desc, 8, 32);  // sizeof literal
        var literal = Marshal.AllocHGlobal(32);
        Marshal.WriteIntPtr(literal, 0, isa);
        Marshal.WriteInt32(literal, 8, BlockIsGlobal);
        Marshal.WriteInt32(literal, 12, 0);
        Marshal.WriteIntPtr(literal, 16,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr, void>)&ReplyImp);
        Marshal.WriteIntPtr(literal, 24, desc);
        return _replyBlock = literal;
    }

    /// <summary>Presents Touch ID / Mac-password UI and resolves with the
    /// result. evaluatePolicy is issued on the UI thread (NSString + LAContext
    /// want the main autorelease pool); the reply lands on LA's own queue.</summary>
    public static async Task<bool> EvaluateAsync(string reason, Action<Action> invokeOnUi)
    {
        var ctx = ObjC.Send(ObjC.Send(ObjC.GetClass("LAContext"), ObjC.Sel("alloc")),
            ObjC.Sel("init"));
        if (ctx == IntPtr.Zero) return false;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs; // caller serializes auths — only one pending reply
        try
        {
            invokeOnUi(() => ObjC.SendNIntObj2(ctx,
                ObjC.Sel("evaluatePolicy:localizedReason:reply:"),
                DeviceOwnerAuthentication, ObjC.NsString(reason), ReplyBlock()));
            return await tcs.Task;
        }
        finally
        {
            if (ReferenceEquals(_pending, tcs)) _pending = null;
            ObjC.Send(ctx, ObjC.Sel("release"));
        }
    }
}
