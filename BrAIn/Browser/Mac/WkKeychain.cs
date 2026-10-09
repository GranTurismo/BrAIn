using System.Runtime.InteropServices;

namespace BrAIn.Browser.Mac;

/// <summary>
/// Generic-password Keychain item holding the password vault's AES key —
/// service "BrAIn Passwords", account "store-key". Uses Security.framework's
/// SecItem* C API; query dictionaries are NSMutableDictionary (toll-free
/// bridged to CFDictionary) built through the ObjC runtime.
///
/// Keys live in the app's own item, so reads after the first write don't
/// prompt. On ad-hoc-signed builds a binary change may make macOS ask once
/// ("BrAIn wants to access…") — expected until the app ships with a stable
/// signature; any failure falls back to the file key.
/// </summary>
internal static partial class WkKeychain
{
    private const string LibSec = "/System/Library/Frameworks/Security.framework/Security";
    private const string LibCf = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(LibSec)] private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [LibraryImport(LibSec)] private static partial int SecItemAdd(IntPtr attributes, IntPtr result);
    [LibraryImport(LibSec)] private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);
    [LibraryImport(LibCf)] private static partial void CFRelease(IntPtr obj);

    private const int ErrSecSuccess = 0, ErrSecDuplicateItem = -25299;
    private const string Service = "BrAIn Passwords";
    private const string Account = "store-key";

    private static IntPtr _lib;
    private static readonly Dictionary<string, IntPtr> _consts = new();

    /// <summary>Resolves a Security.framework global CFString constant
    /// (kSecClass etc.) — the symbol's value IS the CFStringRef.</summary>
    private static IntPtr K(string name)
    {
        lock (_consts)
        {
            if (!_consts.TryGetValue(name, out var v))
            {
                try
                {
                    if (_lib == IntPtr.Zero) _lib = NativeLibrary.Load(LibSec);
                    v = Marshal.ReadIntPtr(NativeLibrary.GetExport(_lib, name));
                }
                catch { v = IntPtr.Zero; }
                _consts[name] = v;
            }
            return v;
        }
    }

    private static IntPtr NsData(byte[] bytes)
    {
        unsafe
        {
            fixed (byte* p = bytes)
                return ObjC.Send(ObjC.GetClass("NSData"),
                    ObjC.Sel("dataWithBytes:length:"), (IntPtr)p, (IntPtr)bytes.Length);
        }
    }

    private static IntPtr TrueNum()
        => ObjC.Send(ObjC.GetClass("NSNumber"), ObjC.Sel("numberWithBool:"), (IntPtr)1);

    /// <summary>NSMutableDictionary built from alternating (key, value)
    /// pairs — both must be ObjC objects (NSString/NSData/NSNumber).</summary>
    private static IntPtr Dict(params IntPtr[] kv)
    {
        var d = ObjC.Send(ObjC.Send(ObjC.GetClass("NSMutableDictionary"),
            ObjC.Sel("alloc")), ObjC.Sel("init"));
        var set = ObjC.Sel("setObject:forKey:");
        for (var i = 0; i + 1 < kv.Length; i += 2)
        {
            if (kv[i] == IntPtr.Zero || kv[i + 1] == IntPtr.Zero) continue;
            ObjC.Send(d, set, kv[i + 1], kv[i]);
        }
        return d;
    }

    private static IntPtr BaseQuery() => Dict(
        K("kSecClass"), K("kSecClassGenericPassword"),
        K("kSecAttrService"), ObjC.NsString(Service),
        K("kSecAttrAccount"), ObjC.NsString(Account));

    /// <summary>The stored key bytes, or null when absent/unreadable.</summary>
    public static byte[]? Read()
    {
        try
        {
            var q = Dict(
                K("kSecClass"), K("kSecClassGenericPassword"),
                K("kSecAttrService"), ObjC.NsString(Service),
                K("kSecAttrAccount"), ObjC.NsString(Account),
                K("kSecReturnData"), TrueNum(),
                K("kSecMatchLimit"), K("kSecMatchLimitOne"));
            if (SecItemCopyMatching(q, out var res) != ErrSecSuccess || res == IntPtr.Zero)
                return null;
            try
            {
                var len = (int)ObjC.SendNInt(res, ObjC.Sel("length"));
                var p = ObjC.Send(res, ObjC.Sel("bytes"));
                if (p == IntPtr.Zero || len <= 0) return null;
                var buf = new byte[len];
                Marshal.Copy(p, buf, 0, len);
                return buf;
            }
            finally { CFRelease(res); }
        }
        catch { return null; }
    }

    /// <summary>Stores (or updates) the key item. False when Keychain refused
    /// — caller then falls back to the file key.</summary>
    public static bool Write(byte[] key)
    {
        try
        {
            var data = NsData(key);
            var add = Dict(
                K("kSecClass"), K("kSecClassGenericPassword"),
                K("kSecAttrService"), ObjC.NsString(Service),
                K("kSecAttrAccount"), ObjC.NsString(Account),
                K("kSecValueData"), data);
            var rc = SecItemAdd(add, IntPtr.Zero);
            if (rc == ErrSecDuplicateItem)
                rc = SecItemUpdate(BaseQuery(), Dict(K("kSecValueData"), data));
            return rc == ErrSecSuccess;
        }
        catch { return false; }
    }
}
