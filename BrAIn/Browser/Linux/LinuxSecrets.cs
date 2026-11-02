using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BrAIn.Browser.Linux;

/// <summary>
/// The freedesktop Secret Service (GNOME Keyring, KWallet, KeePassXC, ...) through libsecret — the Linux
/// counterpart of the macOS Keychain wrapper (<c>Mac/WkKeychain.cs</c>). Two jobs: keep the password vault's key
/// (<see cref="LookupVaultKey"/> / <see cref="StoreVaultKey"/>) and read the "&lt;Browser&gt; Safe Storage" password
/// other browsers keep there (<see cref="LookupChromiumSafeStorage"/>, for the importer).
///
/// Only the non-varargs <c>*v_sync</c> entry points are used: attributes travel in a <c>GHashTable</c> and the
/// <c>SecretSchema</c> struct is built by hand (layout checked against the C headers, see <see cref="NewSchema"/>).
/// Every call is bounded by a <c>GCancellable</c> timeout, so a keyring that is stuck behind an unlock prompt
/// nobody answers can delay the caller but never hang it. Calls block the calling thread (libsecret iterates a
/// private main context), so they are safe off the GTK thread and merely slow on it.
///
/// Nothing here logs a secret or an attribute value; callers report libsecret's own error text.
/// </summary>
internal static unsafe partial class LinuxSecrets
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGio = "libgio-2.0.so.0";
    private const string LibGLib = "libglib-2.0.so.0";

    /// <summary>Longest a single keyring call may take. An "unlock the login keyring" prompt is a person typing a
    /// password; anything slower than this is treated as "not available" so the app falls back instead of freezing.</summary>
    internal const int DefaultTimeoutMs = 45_000;

    /// <summary>The limit actually applied (tests shorten it).</summary>
    internal static int TimeoutMs = DefaultTimeoutMs;

    internal enum Status
    {
        /// <summary>The item exists and its secret was read.</summary>
        Found,
        /// <summary>The Secret Service answered and holds no such item (safe to create one).</summary>
        NotFound,
        /// <summary>An item exists but its secret could not be read (locked collection, unlock prompt dismissed).
        /// Never create or overwrite anything in this state: the real item is still there.</summary>
        Locked,
        /// <summary>No library, no session bus, no daemon, a timeout or any other error.</summary>
        Unavailable,
    }

    // ---- SecretSchema (libsecret-1, LP64: verified with a C program on aarch64) ----
    //   name @0 · flags @8 · attributes[32] @16, each { const char *name @0; int type @8 } = 16 bytes · reserved → 592 bytes
    private const int SchemaSize = 592, SchemaFlagsOffset = 8, SchemaAttrsOffset = 16, SchemaAttrStride = 16, SchemaMaxAttrs = 32;
    private const int SchemaDontMatchName = 2;                 // SECRET_SCHEMA_DONT_MATCH_NAME
    private const int SearchAll = 2, SearchUnlock = 4;          // SecretSearchFlags (NONE = 0)
    private const string GenericSchema = "org.freedesktop.Secret.Generic";
    private const string DefaultCollection = "default";         // SECRET_COLLECTION_DEFAULT

    // ---- libsecret ----
    [LibraryImport(LibSecret)] private static partial IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [LibraryImport(LibSecret)] private static partial IntPtr secret_password_lookupv_nonpageable_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [LibraryImport(LibSecret)] private static partial int secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);
    [LibraryImport(LibSecret)] private static partial void secret_password_free(IntPtr password);
    [LibraryImport(LibSecret)] private static partial IntPtr secret_service_search_sync(IntPtr service, IntPtr schema, IntPtr attributes, int flags, IntPtr cancellable, out IntPtr error);
    [LibraryImport(LibSecret)] private static partial IntPtr secret_service_get_sync(int flags, IntPtr cancellable, out IntPtr error);
    [LibraryImport(LibSecret)] private static partial IntPtr secret_collection_for_alias_sync(IntPtr service, IntPtr alias, int flags, IntPtr cancellable, out IntPtr error);
    [LibraryImport(LibSecret)] private static partial IntPtr secret_item_get_label(IntPtr item);
    [LibraryImport(LibSecret)] private static partial int secret_item_get_locked(IntPtr item);
    [LibraryImport(LibSecret)] private static partial IntPtr secret_item_get_attributes(IntPtr item);
    [LibraryImport(LibSecret)] private static partial int secret_item_load_secret_sync(IntPtr item, IntPtr cancellable, out IntPtr error);
    [LibraryImport(LibSecret)] private static partial IntPtr secret_item_get_secret(IntPtr item);
    [LibraryImport(LibSecret)] private static partial IntPtr secret_value_get(IntPtr value, out nuint length);
    [LibraryImport(LibSecret)] private static partial void secret_value_unref(IntPtr value);
    // ---- gio / glib bits GLib.cs does not carry ----
    [LibraryImport(LibGio)] private static partial IntPtr g_cancellable_new();
    [LibraryImport(LibGio)] private static partial void g_cancellable_cancel(IntPtr cancellable);
    [LibraryImport(LibGLib)] private static partial IntPtr g_hash_table_lookup(IntPtr table, IntPtr key);
    [LibraryImport(LibGLib)] private static partial void g_hash_table_unref(IntPtr table);

    private static bool _noNonpageable;

    private static bool Supported(out string? why)
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8) { why = "the Secret Service is only wired up for 64-bit Linux"; return false; }
        why = null;
        return true;
    }

    // =====================================================================================================
    // The password vault's key
    // =====================================================================================================

    private static readonly (string Name, string Value)[] VaultAttrs =
    {
        ("application", "BrAIn"), ("service", "BrAIn Passwords"), ("account", "store-key"),
    };

    /// <summary>The vault key, kept as base64 text (the Secret Service stores text). <paramref name="key"/> is the
    /// decoded 32 bytes when <see cref="Status.Found"/> — an item of any other shape counts as not found.</summary>
    internal static Status LookupVaultKey(out byte[]? key, out string? why)
    {
        key = null;
        var st = Lookup(VaultAttrs, out var secret, out why);
        if (st != Status.Found) return st;
        try
        {
            key = DecodeKey(secret!);
            if (key is null) { why = "the stored vault key has an unexpected format"; return Status.Unavailable; }
            return Status.Found;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>Stores (replaces) the vault key. Callers only do this after <see cref="Status.NotFound"/>.</summary>
    internal static bool StoreVaultKey(byte[] key, out string? why)
    {
        var b64 = new byte[Base64.GetMaxEncodedToUtf8Length(key.Length)];
        try
        {
            Base64.EncodeToUtf8(key, b64, out _, out var written);
            return Store("BrAIn password vault key", VaultAttrs, b64.AsSpan(0, written), out why);
        }
        finally { CryptographicOperations.ZeroMemory(b64); }
    }

    private static byte[]? DecodeKey(byte[] b64Utf8)
    {
        var buf = new byte[48];
        var text = b64Utf8.AsSpan();
        while (text.Length > 0 && text[^1] is (byte)'\n' or (byte)'\r' or (byte)' ') text = text[..^1]; // an editor may have added one
        try
        {
            if (Base64.DecodeFromUtf8(text, buf, out _, out var written) != System.Buffers.OperationStatus.Done || written != 32) return null;
            return buf.AsSpan(0, 32).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buf); }
    }

    // =====================================================================================================
    // Generic lookup / store
    // =====================================================================================================

    /// <summary>First item whose attributes match (an item with extra attributes matches too).</summary>
    internal static Status Lookup(IReadOnlyList<(string Name, string Value)> attrs, out byte[]? secret, out string? why, int timeoutMs = -1)
    {
        secret = null;
        if (timeoutMs < 0) timeoutMs = TimeoutMs;
        if (!Supported(out why)) return Status.Unavailable;
        var native = new List<IntPtr>();
        IntPtr schema = IntPtr.Zero, table = IntPtr.Zero;
        try
        {
            schema = NewSchema(GenericSchema, SchemaDontMatchName, attrs, native);
            table = NewTable(attrs, native);
            IntPtr pw = IntPtr.Zero, err = IntPtr.Zero;
            Run(timeoutMs, c => pw = LookupNative(schema, table, c, out err));
            if (err != IntPtr.Zero) { why = GLib.TakeError(err); return Status.Unavailable; }
            if (pw != IntPtr.Zero)
            {
                try { secret = CopyCString(pw); return Status.Found; }
                finally { secret_password_free(pw); }
            }
            // NULL without an error is both "no such item" and "found, but the unlock prompt was dismissed".
            // A search that never unlocks and never reads secrets tells them apart, so a locked keyring is
            // never mistaken for an empty one (which would invite overwriting the real item).
            var n = CountMatching(schema, table, timeoutMs, out why);
            if (n < 0) return Status.Unavailable;
            if (n > 0) { why = "the keyring is locked (unlock it and try again)"; return Status.Locked; }
            return Status.NotFound;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            why = "libsecret is not installed";
            return Status.Unavailable;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return Status.Unavailable;
        }
        finally { Free(native, schema, table); }
    }

    /// <summary>Stores a text secret in the default collection, replacing an item with the same attributes.</summary>
    internal static bool Store(string label, IReadOnlyList<(string Name, string Value)> attrs, ReadOnlySpan<byte> secretUtf8, out string? why, int timeoutMs = -1)
    {
        if (timeoutMs < 0) timeoutMs = TimeoutMs;
        if (!Supported(out why)) return false;
        var native = new List<IntPtr>();
        IntPtr schema = IntPtr.Zero, table = IntPtr.Zero, pw = IntPtr.Zero;
        try
        {
            // Storing into a default keyring that does not exist makes the daemon ask the user to CREATE one (a
            // "choose a password" dialog, or nothing at all on a headless box and a stall until the timeout).
            // That is the user's decision to make in their keyring manager, not a side effect of saving a key:
            // without a default keyring this reports "unavailable" at once and the caller uses its file fallback.
            if (!HasDefaultCollection(timeoutMs, out why)) return false;
            schema = NewSchema(GenericSchema, 0, attrs, native);
            table = NewTable(attrs, native);
            var pwLen = secretUtf8.Length;
            pw = (IntPtr)NativeMemory.AllocZeroed((nuint)pwLen + 1);
            secretUtf8.CopyTo(new Span<byte>((void*)pw, pwLen));
            native.Add(Marshal.StringToCoTaskMemUTF8(DefaultCollection));
            var coll = native[^1];
            native.Add(Marshal.StringToCoTaskMemUTF8(label));
            var lab = native[^1];
            var ok = 0;
            IntPtr err = IntPtr.Zero;
            Run(timeoutMs, c => ok = secret_password_storev_sync(schema, table, coll, lab, pw, c, out err));
            if (err != IntPtr.Zero) { why = GLib.TakeError(err); return false; }
            if (ok == 0) { why = "the Secret Service refused to store the item"; return false; }
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            why = "libsecret is not installed";
            return false;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
        finally
        {
            if (pw != IntPtr.Zero)
            {
                new Span<byte>((void*)pw, secretUtf8.Length).Clear();
                NativeMemory.Free((void*)pw);
            }
            Free(native, schema, table);
        }
    }

    // =====================================================================================================
    // Chromium-family "<Browser> Safe Storage"
    // =====================================================================================================

    private static readonly string[] ChromiumSchemas = { "chrome_libsecret_os_crypt_password_v2", "chrome_libsecret_os_crypt_password" };

    /// <summary>The password a Chromium-family browser keeps under "&lt;Browser&gt; Safe Storage" (label), used to
    /// derive its v11 key. Items are found by Chromium's schema names, then by the <c>application</c> attribute
    /// (forks may rename the schema), and picked by label — and by the attribute when several browsers share a
    /// label — because the attribute value differs between builds and forks and Chrome/Chromium can even share it.
    /// Unlocking a locked keyring may prompt.</summary>
    internal static Status LookupChromiumSafeStorage(string label, string[] applications, out byte[]? secret, out string? why, int timeoutMs = -1)
    {
        secret = null;
        if (timeoutMs < 0) timeoutMs = TimeoutMs;
        if (!Supported(out why)) return Status.Unavailable;
        bool sawLocked = false, sawError = false;
        string? lastWhy = null;
        var queries = ChromiumSchemas.Select(n => ("xdg:schema", n)).Concat(applications.Select(a => ("application", a))).ToArray();
        try
        {
            foreach (var query in queries)
            {
                var native = new List<IntPtr>();
                IntPtr table = IntPtr.Zero, list = IntPtr.Zero;
                try
                {
                    table = NewTable(new[] { query }, native);
                    IntPtr err = IntPtr.Zero;
                    // no SecretSchema: the xdg:schema pseudo-attribute is not part of any schema, so nothing to validate
                    Run(timeoutMs, c => list = secret_service_search_sync(IntPtr.Zero, IntPtr.Zero, table, SearchAll | SearchUnlock, c, out err));
                    if (err != IntPtr.Zero) { lastWhy = GLib.TakeError(err); sawError = true; break; } // service down: further queries would fail the same way
                    if (list == IntPtr.Zero) continue;

                    var best = IntPtr.Zero;
                    var bestScore = 0;
                    var appKey = Marshal.StringToCoTaskMemUTF8("application");
                    native.Add(appKey);
                    foreach (var item in GLib.ListItems(list))
                    {
                        var lab = GLib.TakeStr(secret_item_get_label(item));
                        var attrs = secret_item_get_attributes(item);
                        string? app = null;
                        if (attrs != IntPtr.Zero)
                        {
                            app = GLib.Str(g_hash_table_lookup(attrs, appKey));
                            g_hash_table_unref(attrs);
                        }
                        var score = (string.Equals(lab, label, StringComparison.Ordinal) ? 2 : 0)
                                  + (app is not null && Array.IndexOf(applications, app) >= 0 ? 1 : 0);
                        if (score > bestScore) { best = item; bestScore = score; }
                    }
                    if (best == IntPtr.Zero) continue;

                    IntPtr serr = IntPtr.Zero;
                    var loaded = 0;
                    Run(timeoutMs, c => loaded = secret_item_load_secret_sync(best, c, out serr));
                    if (serr != IntPtr.Zero)
                    {
                        lastWhy = GLib.TakeError(serr);
                        if (lastWhy?.Contains("locked", StringComparison.OrdinalIgnoreCase) == true) sawLocked = true; else sawError = true;
                        continue;
                    }
                    var value = loaded != 0 ? secret_item_get_secret(best) : IntPtr.Zero;
                    if (value == IntPtr.Zero) { lastWhy = "the keyring is locked (unlock it and try again)"; sawLocked = true; continue; }
                    try
                    {
                        var p = secret_value_get(value, out var len);
                        if (p != IntPtr.Zero && len > 0)
                        {
                            secret = new ReadOnlySpan<byte>((void*)p, (int)len).ToArray();
                            why = null;
                            return Status.Found;
                        }
                    }
                    finally { secret_value_unref(value); }
                }
                finally
                {
                    if (list != IntPtr.Zero)
                    {
                        foreach (var item in GLib.ListItems(list)) GLib.g_object_unref(item);
                        GLib.g_list_free(list);
                    }
                    Free(native, IntPtr.Zero, table);
                }
            }

            // Nothing usable. Absent, or present but locked with the unlock prompt failed or dismissed? A search that
            // never unlocks can still see locked items (GNOME Keyring matches them on the hashed application
            // attribute, not on xdg:schema), and an item knows whether it is locked.
            if (!sawLocked && !sawError)
            {
                foreach (var app in applications)
                {
                    var native = new List<IntPtr>();
                    var table = NewTable(new[] { ("application", app) }, native);
                    IntPtr list = IntPtr.Zero, err = IntPtr.Zero;
                    try
                    {
                        Run(timeoutMs, c => list = secret_service_search_sync(IntPtr.Zero, IntPtr.Zero, table, SearchAll, c, out err));
                        if (err != IntPtr.Zero) { lastWhy = GLib.TakeError(err); sawError = true; break; }
                        if (list != IntPtr.Zero && GLib.ListItems(list).Any(i => secret_item_get_locked(i) != 0))
                        {
                            lastWhy = "the keyring is locked (unlock it and try again)";
                            sawLocked = true;
                            break;
                        }
                    }
                    finally
                    {
                        if (list != IntPtr.Zero)
                        {
                            foreach (var item in GLib.ListItems(list)) GLib.g_object_unref(item);
                            GLib.g_list_free(list);
                        }
                        Free(native, IntPtr.Zero, table);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            why = "libsecret is not installed";
            return Status.Unavailable;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return Status.Unavailable;
        }
        why = lastWhy;
        return sawLocked ? Status.Locked : sawError ? Status.Unavailable : Status.NotFound;
    }

    // =====================================================================================================
    // plumbing
    // =====================================================================================================

    private static IntPtr LookupNative(IntPtr schema, IntPtr table, IntPtr cancellable, out IntPtr err)
    {
        if (!_noNonpageable)
        {
            try { return secret_password_lookupv_nonpageable_sync(schema, table, cancellable, out err); }
            catch (EntryPointNotFoundException) { _noNonpageable = true; }
        }
        return secret_password_lookupv_sync(schema, table, cancellable, out err);
    }

    private static bool HasDefaultCollection(int timeoutMs, out string? why)
    {
        why = null;
        var alias = Marshal.StringToCoTaskMemUTF8(DefaultCollection);
        try
        {
            IntPtr service = IntPtr.Zero, coll = IntPtr.Zero, err = IntPtr.Zero;
            Run(timeoutMs, c =>
            {
                service = secret_service_get_sync(0 /* SECRET_SERVICE_NONE */, c, out err);
                if (service != IntPtr.Zero) coll = secret_collection_for_alias_sync(service, alias, 0 /* SECRET_COLLECTION_NONE */, c, out err);
            });
            try
            {
                if (err != IntPtr.Zero) { why = GLib.TakeError(err); return false; }
                if (coll == IntPtr.Zero) { why = "no default keyring exists (create one with your desktop's keyring manager)"; return false; }
                return true;
            }
            finally
            {
                if (coll != IntPtr.Zero) GLib.g_object_unref(coll);
                if (service != IntPtr.Zero) GLib.g_object_unref(service);
            }
        }
        finally { Marshal.FreeCoTaskMem(alias); }
    }

    /// <summary>How many items match, without unlocking anything or reading a secret (so it never prompts).
    /// -1 when the search itself failed.</summary>
    private static int CountMatching(IntPtr schema, IntPtr table, int timeoutMs, out string? why)
    {
        why = null;
        IntPtr list = IntPtr.Zero, err = IntPtr.Zero;
        Run(timeoutMs, c => list = secret_service_search_sync(IntPtr.Zero, schema, table, SearchAll, c, out err));
        if (err != IntPtr.Zero) { why = GLib.TakeError(err); return -1; }
        if (list == IntPtr.Zero) return 0;
        var n = (int)GLib.g_list_length(list);
        foreach (var item in GLib.ListItems(list)) GLib.g_object_unref(item);
        GLib.g_list_free(list);
        return n;
    }

    /// <summary>Runs one blocking libsecret call with a cancellable that a timer fires after
    /// <paramref name="timeoutMs"/>; the call then returns with a "cancelled" error instead of hanging.</summary>
    private static void Run(int timeoutMs, Action<IntPtr> call)
    {
        var cancellable = g_cancellable_new();
        var gate = new object();
        var done = false;
        using var timer = new Timer(_ => { lock (gate) { if (!done) g_cancellable_cancel(cancellable); } }, null, timeoutMs, Timeout.Infinite);
        try { call(cancellable); }
        finally
        {
            lock (gate) { done = true; }
            GLib.g_object_unref(cancellable);
        }
    }

    /// <summary>Builds a SecretSchema in unmanaged memory; the attribute names are exactly the ones the caller will
    /// pass, all of type string. Strings are tracked in <paramref name="native"/> for freeing.</summary>
    private static IntPtr NewSchema(string name, int flags, IReadOnlyList<(string Name, string Value)> attrs, List<IntPtr> native)
    {
        if (attrs.Count == 0 || attrs.Count > SchemaMaxAttrs) throw new ArgumentException("1.." + SchemaMaxAttrs + " attributes");
        var s = (byte*)NativeMemory.AllocZeroed(SchemaSize);
        var nm = Marshal.StringToCoTaskMemUTF8(name);
        native.Add(nm);
        *(IntPtr*)s = nm;
        *(int*)(s + SchemaFlagsOffset) = flags;
        for (var i = 0; i < attrs.Count; i++)
        {
            var an = Marshal.StringToCoTaskMemUTF8(attrs[i].Name);
            native.Add(an);
            var a = s + SchemaAttrsOffset + i * SchemaAttrStride;
            *(IntPtr*)a = an;
            *(int*)(a + 8) = 0; // SECRET_SCHEMA_ATTRIBUTE_STRING; the zeroed entry after the last one ends the list
        }
        return (IntPtr)s;
    }

    private static IntPtr NewTable(IReadOnlyList<(string Name, string Value)> attrs, List<IntPtr> native)
    {
        var t = GLib.g_hash_table_new(GLib.StrHash, GLib.StrEqual);
        foreach (var (k, v) in attrs)
        {
            var kp = Marshal.StringToCoTaskMemUTF8(k);
            var vp = Marshal.StringToCoTaskMemUTF8(v);
            native.Add(kp);
            native.Add(vp);
            GLib.g_hash_table_insert(t, kp, vp);
        }
        return t;
    }

    private static void Free(List<IntPtr> native, IntPtr schema, IntPtr table)
    {
        if (table != IntPtr.Zero) GLib.g_hash_table_destroy(table); // keys/values are ours: not owned by the table
        if (schema != IntPtr.Zero) NativeMemory.Free((void*)schema);
        foreach (var p in native) Marshal.FreeCoTaskMem(p);
    }

    private static byte[] CopyCString(IntPtr p)
    {
        var n = 0;
        while (((byte*)p)[n] != 0) n++;
        return new ReadOnlySpan<byte>((void*)p, n).ToArray();
    }
}
