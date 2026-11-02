using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Minimal GLib / GObject / GIO interop for the Linux (GTK3 + WebKitGTK 4.1) port — the counterpart of
/// <c>Mac/ObjC.cs</c>. Only usable on Linux: every call site is guarded by an OS check, and everything that
/// touches a widget must run on the GTK main thread (Photino's Invoke, i.e. <c>BrowserController.InvokeOnUi</c>).
///
/// Callbacks into managed code are static <c>[UnmanagedCallersOnly]</c> methods; they never throw (an exception
/// escaping into GLib would abort the process), and find their state through a pointer-keyed table rather than a
/// GCHandle so there is nothing to free.
/// </summary>
internal static unsafe partial class GLib
{
    private const string LibGLib = "libglib-2.0.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";
    private const string LibGio = "libgio-2.0.so.0";

    // ---- fundamental GTypes (G_TYPE_MAKE_FUNDAMENTAL(n) = n << 2) ----
    internal const nuint TypeBoolean = 5 << 2, TypeInt = 6 << 2, TypeUInt = 7 << 2, TypeEnum = 12 << 2,
        TypeDouble = 15 << 2, TypeString = 16 << 2, TypeObject = 20 << 2;

    // ---- GObject: references, signals, properties ----
    [LibraryImport(LibGObject)] internal static partial IntPtr g_object_ref(IntPtr obj);
    [LibraryImport(LibGObject)] internal static partial void g_object_unref(IntPtr obj);
    [LibraryImport(LibGObject)] internal static partial IntPtr g_object_ref_sink(IntPtr obj);
    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_object_set_data(IntPtr obj, string key, IntPtr data);
    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)] internal static partial IntPtr g_object_get_data(IntPtr obj, string key);
    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_object_set_property(IntPtr obj, string name, IntPtr gvalue);
    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_object_get_property(IntPtr obj, string name, IntPtr gvalue);
    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)] internal static partial IntPtr g_object_class_find_property(IntPtr objectClass, string name);
    [LibraryImport(LibGObject)] internal static partial IntPtr g_object_new_with_properties(nuint type, uint nProps, IntPtr* names, IntPtr values);
    [LibraryImport(LibGObject)] internal static partial IntPtr g_type_class_ref(nuint type);
    [LibraryImport(LibGObject)] internal static partial void g_type_class_unref(IntPtr cls);
    [LibraryImport(LibGObject)] internal static partial int g_type_check_instance_is_a(IntPtr instance, nuint type);
    [LibraryImport(LibGObject)] internal static partial IntPtr g_type_name(nuint type);
    [LibraryImport(LibGObject)] internal static partial IntPtr g_value_init(IntPtr gvalue, nuint type);
    [LibraryImport(LibGObject)] internal static partial void g_value_unset(IntPtr gvalue);
    [LibraryImport(LibGObject)] internal static partial void g_value_set_boolean(IntPtr gvalue, int v);
    [LibraryImport(LibGObject)] internal static partial void g_value_set_int(IntPtr gvalue, int v);
    [LibraryImport(LibGObject)] internal static partial void g_value_set_uint(IntPtr gvalue, uint v);
    [LibraryImport(LibGObject)] internal static partial void g_value_set_double(IntPtr gvalue, double v);
    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_value_set_string(IntPtr gvalue, string v);
    [LibraryImport(LibGObject)] internal static partial void g_value_set_object(IntPtr gvalue, IntPtr obj);
    [LibraryImport(LibGObject)] internal static partial int g_value_get_boolean(IntPtr gvalue);
    [LibraryImport(LibGObject)] internal static partial int g_value_get_int(IntPtr gvalue);
    [LibraryImport(LibGObject)] internal static partial double g_value_get_double(IntPtr gvalue);
    [LibraryImport(LibGObject)] internal static partial IntPtr g_value_get_string(IntPtr gvalue);
    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nuint g_signal_connect_data(IntPtr instance, string signal, IntPtr handler, IntPtr data, IntPtr destroyData, int flags);
    [LibraryImport(LibGObject)] internal static partial void g_signal_handler_disconnect(IntPtr instance, nuint id);
    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_signal_stop_emission_by_name(IntPtr instance, string signal);

    // ---- GLib ----
    [LibraryImport(LibGLib)] internal static partial void g_free(IntPtr p);
    [LibraryImport(LibGLib, StringMarshalling = StringMarshalling.Utf8)] internal static partial int g_setenv(string name, string value, int overwrite);
    [LibraryImport(LibGLib, StringMarshalling = StringMarshalling.Utf8)] internal static partial void g_unsetenv(string name);
    [LibraryImport(LibGLib, StringMarshalling = StringMarshalling.Utf8)] internal static partial IntPtr g_getenv(string name);
    [LibraryImport(LibGLib)] internal static partial IntPtr g_malloc0(nuint n);
    [LibraryImport(LibGLib)] internal static partial uint g_idle_add(IntPtr func, IntPtr data);
    [LibraryImport(LibGLib)] internal static partial uint g_timeout_add(uint ms, IntPtr func, IntPtr data);
    [LibraryImport(LibGLib)] internal static partial int g_source_remove(uint id);
    [LibraryImport(LibGLib)] internal static partial uint g_list_length(IntPtr list);
    [LibraryImport(LibGLib)] internal static partial void g_list_free(IntPtr list);
    [LibraryImport(LibGLib)] internal static partial IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);
    [LibraryImport(LibGLib)] internal static partial int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [LibraryImport(LibGLib)] internal static partial void g_hash_table_destroy(IntPtr table);
    [LibraryImport(LibGLib)] internal static partial IntPtr g_bytes_new(IntPtr data, nuint size);
    [LibraryImport(LibGLib)] internal static partial IntPtr g_bytes_get_data(IntPtr bytes, out nuint size);
    [LibraryImport(LibGLib)] internal static partial void g_bytes_unref(IntPtr bytes);
    [LibraryImport(LibGLib)] internal static partial void g_error_free(IntPtr error);
    [LibraryImport(LibGLib)] internal static partial IntPtr g_date_time_new_from_unix_utc(long t);
    [LibraryImport(LibGLib)] internal static partial long g_date_time_to_unix(IntPtr dt);
    [LibraryImport(LibGLib)] internal static partial void g_date_time_unref(IntPtr dt);
    [LibraryImport(LibGLib)] internal static partial void g_set_prgname([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [LibraryImport(LibGLib)] internal static partial void g_set_application_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    // ---- GIO ----
    [LibraryImport(LibGio, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int g_app_info_launch_default_for_uri(string uri, IntPtr context, out IntPtr error);

    // ---- helpers ----

    internal static IntPtr Sym(string lib, string name) => NativeLibrary.GetExport(NativeLibrary.Load(lib), name);

    private static IntPtr _strHash, _strEqual;
    internal static IntPtr StrHash => _strHash != IntPtr.Zero ? _strHash : _strHash = Sym(LibGLib, "g_str_hash");
    internal static IntPtr StrEqual => _strEqual != IntPtr.Zero ? _strEqual : _strEqual = Sym(LibGLib, "g_str_equal");

    /// <summary>UTF-8 C string → managed (null for a null pointer). Does not free.</summary>
    internal static string? Str(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    /// <summary>UTF-8 C string → managed, then g_free it (for functions that return an owned string).</summary>
    internal static string? TakeStr(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { g_free(p); }
    }

    /// <summary>Message of a GError* (domain u32 @0, code i32 @4, message char* @8) and frees it.</summary>
    internal static string? TakeError(IntPtr err)
    {
        if (err == IntPtr.Zero) return null;
        try { return Str(Marshal.ReadIntPtr(err, 8)); }
        finally { g_error_free(err); }
    }

    /// <summary>g_signal_connect for a plain unmanaged function pointer.</summary>
    internal static nuint Connect(IntPtr instance, string signal, void* handler, IntPtr data = default)
        => g_signal_connect_data(instance, signal, (IntPtr)handler, data, IntPtr.Zero, 0);

    internal static bool IsA(IntPtr instance, nuint type) => instance != IntPtr.Zero && g_type_check_instance_is_a(instance, type) != 0;

    /// <summary>Items of a GList* (data pointers). The list is not freed.</summary>
    internal static List<IntPtr> ListItems(IntPtr list)
    {
        var r = new List<IntPtr>();
        for (var n = list; n != IntPtr.Zero; n = Marshal.ReadIntPtr(n, 8)) r.Add(Marshal.ReadIntPtr(n, 0));
        return r;
    }

    /// <summary>One-shot call on the GTK main loop (from any thread).</summary>
    private static readonly Dictionary<nint, Action> _idle = new();
    private static long _idleSeq;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int IdleThunk(IntPtr data)
    {
        Action? a;
        lock (_idle) { _idle.Remove(data, out a); }
        try { a?.Invoke(); } catch { }
        return 0; // G_SOURCE_REMOVE
    }

    internal static void RunOnMainLoop(Action a)
    {
        var key = (nint)Interlocked.Increment(ref _idleSeq);
        lock (_idle) _idle[key] = a;
        g_idle_add((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&IdleThunk, key);
    }

    /// <summary>Calls <paramref name="a"/> on the main loop after <paramref name="ms"/> milliseconds.</summary>
    private static readonly Dictionary<nint, Func<bool>> _timers = new();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int TimerThunk(IntPtr data)
    {
        Func<bool>? f;
        lock (_timers) _timers.TryGetValue(data, out f);
        var again = false;
        try { again = f?.Invoke() ?? false; } catch { }
        if (!again) lock (_timers) _timers.Remove(data);
        return again ? 1 : 0;
    }

    /// <summary>Repeats on the main loop every <paramref name="ms"/> while <paramref name="f"/> returns true.</summary>
    internal static void Every(uint ms, Func<bool> f)
    {
        var key = (nint)Interlocked.Increment(ref _idleSeq);
        lock (_timers) _timers[key] = f;
        g_timeout_add(ms, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&TimerThunk, key);
    }

    // ---- GValue-based property access (no varargs: they are unsafe through P/Invoke) ----

    private const int GValueSize = 24;

    /// <summary>True when <paramref name="obj"/>'s class has a property of that name (so setting it cannot warn).</summary>
    internal static bool HasProp(IntPtr obj, string name)
    {
        if (obj == IntPtr.Zero) return false;
        var klass = Marshal.ReadIntPtr(obj); // GTypeInstance.g_class
        return g_object_class_find_property(klass, name) != IntPtr.Zero;
    }

    private static IntPtr NewValue(nuint type)
    {
        var v = g_malloc0(GValueSize);
        g_value_init(v, type);
        return v;
    }

    private static void SetProp(IntPtr obj, string name, nuint type, Action<IntPtr> fill)
    {
        if (!HasProp(obj, name)) return;
        var v = NewValue(type);
        try { fill(v); g_object_set_property(obj, name, v); }
        finally { g_value_unset(v); g_free(v); }
    }

    internal static void SetBool(IntPtr obj, string name, bool value) => SetProp(obj, name, TypeBoolean, v => g_value_set_boolean(v, value ? 1 : 0));
    internal static void SetInt(IntPtr obj, string name, int value) => SetProp(obj, name, TypeInt, v => g_value_set_int(v, value));
    internal static void SetDouble(IntPtr obj, string name, double value) => SetProp(obj, name, TypeDouble, v => g_value_set_double(v, value));
    internal static void SetString(IntPtr obj, string name, string value) => SetProp(obj, name, TypeString, v => g_value_set_string(v, value));

    internal static bool GetBool(IntPtr obj, string name)
    {
        if (!HasProp(obj, name)) return false;
        var v = NewValue(TypeBoolean);
        try { g_object_get_property(obj, name, v); return g_value_get_boolean(v) != 0; }
        finally { g_value_unset(v); g_free(v); }
    }

    internal static string? GetString(IntPtr obj, string name)
    {
        if (!HasProp(obj, name)) return null;
        var v = NewValue(TypeString);
        try { g_object_get_property(obj, name, v); return Str(g_value_get_string(v)); }
        finally { g_value_unset(v); g_free(v); }
    }

    /// <summary>g_object_new for object-valued construct properties: (name, GObject*) pairs.</summary>
    internal static IntPtr NewObject(nuint type, params (string Name, IntPtr Obj)[] props)
    {
        var names = new IntPtr[props.Length];
        var values = g_malloc0((nuint)(GValueSize * Math.Max(1, props.Length)));
        try
        {
            for (var i = 0; i < props.Length; i++)
            {
                names[i] = Marshal.StringToCoTaskMemUTF8(props[i].Name);
                var slot = values + i * GValueSize;
                g_value_init(slot, TypeObject);
                g_value_set_object(slot, props[i].Obj);
            }
            fixed (IntPtr* n = names)
                return g_object_new_with_properties(type, (uint)props.Length, n, values);
        }
        finally
        {
            for (var i = 0; i < props.Length; i++)
            {
                g_value_unset(values + i * GValueSize);
                if (names[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(names[i]);
            }
            g_free(values);
        }
    }

    /// <summary>g_object_new for string-valued construct properties (e.g. a data manager's directories).</summary>
    internal static IntPtr NewObjectStrings(nuint type, params (string Name, string Value)[] props)
    {
        var names = new IntPtr[props.Length];
        var values = g_malloc0((nuint)(GValueSize * Math.Max(1, props.Length)));
        try
        {
            for (var i = 0; i < props.Length; i++)
            {
                names[i] = Marshal.StringToCoTaskMemUTF8(props[i].Name);
                var slot = values + i * GValueSize;
                g_value_init(slot, TypeString);
                g_value_set_string(slot, props[i].Value);
            }
            fixed (IntPtr* n = names)
                return g_object_new_with_properties(type, (uint)props.Length, n, values);
        }
        finally
        {
            for (var i = 0; i < props.Length; i++)
            {
                g_value_unset(values + i * GValueSize);
                if (names[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(names[i]);
            }
            g_free(values);
        }
    }

    /// <summary>Pointer table for async-callback state: the token goes through <c>user_data</c>, the callback takes
    /// the state back out. Avoids GCHandles that would leak if a callback never fires.</summary>
    private static readonly Dictionary<nint, object> _state = new();

    internal static IntPtr Stash(object state)
    {
        var key = (nint)Interlocked.Increment(ref _idleSeq);
        lock (_state) _state[key] = state;
        return key;
    }

    internal static T? Unstash<T>(IntPtr key) where T : class
    {
        lock (_state) return _state.Remove(key, out var o) ? o as T : null;
    }

    internal static T? Peek<T>(IntPtr key) where T : class
    {
        lock (_state) return _state.TryGetValue(key, out var o) ? o as T : null;
    }
}
