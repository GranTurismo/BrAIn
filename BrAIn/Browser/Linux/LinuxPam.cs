using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Checks the signed-in user's own account password against PAM (<c>libpam.so.0</c>) — what lets the Linux password
/// gate be a real "is it you?" check like Touch ID / the Windows credential prompt instead of a bare Allow button.
///
/// The conversation callback answers the password prompt from a byte array held only for the duration of the
/// call; responses are malloc'd because PAM frees (and scrubs) them itself. <c>pam_authenticate</c> is slow on
/// failure by design (fail delay), so <see cref="Authenticate"/> blocks and belongs on a worker thread.
/// An unprivileged user verifying their OWN password works through pam_unix's setgid <c>unix_chkpwd</c> helper.
/// </summary>
internal static unsafe partial class LinuxPam
{
    private const string LibPam = "libpam.so.0";

    // pam_message.msg_style
    private const int PromptEchoOff = 1, PromptEchoOn = 2, ErrorMsg = 3, TextInfo = 4;
    // return codes (security/_pam_types.h)
    private const int Success = 0, OpenErr = 1, SymbolErr = 2, ServiceErr = 3, SystemErr = 4, BufErr = 5, AuthInfoUnavail = 9,
        UserUnknown = 10, NewAuthtokReqd = 12, ConvErr = 19, Abort = 26, ModuleUnknown = 28;
    private const int DisallowNullAuthtok = 1;

    internal enum Verdict
    {
        /// <summary>Authenticated (a merely expired password counts: it was the right one).</summary>
        Ok,
        /// <summary>The password was refused.</summary>
        Wrong,
        /// <summary>The password was right but the account may not log in right now (locked out, expired account).</summary>
        Denied,
        /// <summary>PAM cannot verify anything on this machine (no such service, unknown user, helper missing, ...).</summary>
        Unusable,
    }

    [LibraryImport(LibPam, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int pam_start(string service, string user, IntPtr conv, out IntPtr handle);
    [LibraryImport(LibPam)] private static partial int pam_authenticate(IntPtr handle, int flags);
    [LibraryImport(LibPam)] private static partial int pam_acct_mgmt(IntPtr handle, int flags);
    [LibraryImport(LibPam)] private static partial int pam_end(IntPtr handle, int status);

    private sealed class Ctx
    {
        public byte[] Password = Array.Empty<byte>();
        public byte[] User = Array.Empty<byte>();
    }

    /// <summary>True when libpam can be loaded at all.</summary>
    internal static bool IsAvailable()
    {
        try { return NativeLibrary.TryLoad(LibPam, out _); }
        catch { return false; }
    }

    /// <summary>pam_start → pam_authenticate → pam_acct_mgmt → pam_end for <paramref name="user"/> under
    /// <paramref name="service"/>. <paramref name="password"/> is read, never modified or kept. Blocking.</summary>
    internal static Verdict Authenticate(string service, string user, byte[] password, out int code)
    {
        code = -1;
        var ctx = new Ctx { Password = password, User = Encoding.UTF8.GetBytes(user) };
        var token = GLib.Stash(ctx);
        var conv = (byte*)NativeMemory.AllocZeroed(16);          // struct pam_conv { conv @0; appdata_ptr @8 }
        var handle = IntPtr.Zero;
        var last = 0;
        try
        {
            *(IntPtr*)conv = (IntPtr)(delegate* unmanaged[Cdecl]<int, IntPtr*, IntPtr*, IntPtr, int>)&Conv;
            *(IntPtr*)(conv + 8) = token;
            last = pam_start(service, user, (IntPtr)conv, out handle);
            if (last != Success) { code = last; handle = IntPtr.Zero; return Verdict.Unusable; }

            last = pam_authenticate(handle, DisallowNullAuthtok);
            code = last;
            if (last != Success) return Classify(last);

            var acct = pam_acct_mgmt(handle, 0);
            last = acct;
            if (acct == Success || acct == NewAuthtokReqd) return Verdict.Ok;
            code = acct;
            return Classify(acct) == Verdict.Unusable ? Verdict.Unusable : Verdict.Denied;
        }
        finally
        {
            if (handle != IntPtr.Zero) pam_end(handle, last);
            NativeMemory.Free(conv);
            GLib.Unstash<Ctx>(token);
            CryptographicWipe(ctx.User);
        }
    }

    private static Verdict Classify(int rc) => rc switch
    {
        OpenErr or SymbolErr or ServiceErr or SystemErr or BufErr or AuthInfoUnavail or UserUnknown or ConvErr or Abort or ModuleUnknown => Verdict.Unusable,
        _ => Verdict.Wrong, // AUTH_ERR, MAXTRIES, PERM_DENIED, CRED_INSUFFICIENT, ... — the password did not check out
    };

    private static void CryptographicWipe(byte[] b) => System.Security.Cryptography.CryptographicOperations.ZeroMemory(b);

    /// <summary>The conversation function PAM calls for prompts: the password prompt gets the password, a user-name
    /// prompt the user name, informational/error texts need no answer. Anything else aborts the conversation.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Conv(int numMsg, IntPtr* msgs, IntPtr* resp, IntPtr appdata)
    {
        byte* arr = null;
        try
        {
            var ctx = GLib.Peek<Ctx>(appdata);
            if (ctx is null || msgs == null || resp == null || numMsg <= 0 || numMsg > 32) return ConvErr;
            // struct pam_response { char *resp @0; int resp_retcode @8 } — calloc'd: PAM free()s the array and each resp
            arr = (byte*)NativeMemory.AllocZeroed((nuint)numMsg, 16);
            for (var i = 0; i < numMsg; i++)
            {
                var m = (byte*)msgs[i];                          // Linux-PAM: an array of pointers to struct pam_message
                var style = *(int*)m;                            // { int msg_style @0; const char *msg @8 }
                if (style is ErrorMsg or TextInfo) continue;
                if (style is not (PromptEchoOff or PromptEchoOn)) { Release(arr, numMsg); return ConvErr; }
                var answer = style == PromptEchoOff ? ctx.Password : ctx.User;
                var buf = (byte*)NativeMemory.Alloc((nuint)answer.Length + 1);
                answer.AsSpan().CopyTo(new Span<byte>(buf, answer.Length));
                buf[answer.Length] = 0;
                *(IntPtr*)(arr + i * 16) = (IntPtr)buf;
            }
            *resp = (IntPtr)arr;
            return Success;
        }
        catch
        {
            if (arr != null) Release(arr, numMsg);
            return ConvErr;
        }
    }

    private static void Release(byte* arr, int n)
    {
        for (var i = 0; i < n && i < 32; i++)
        {
            var p = *(byte**)(arr + i * 16);
            if (p == null) continue;
            var len = 0;
            while (p[len] != 0) len++;
            new Span<byte>(p, len).Clear();
            NativeMemory.Free(p);
        }
        NativeMemory.Free(arr);
    }
}
