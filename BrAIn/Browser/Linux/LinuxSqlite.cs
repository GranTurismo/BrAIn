using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BrAIn.Browser.Linux;

/// <summary>
/// Runs a SELECT against a SQLite file in-process through the system's <c>libsqlite3.so.0</c> and hands the rows
/// back as the same JSON array the <c>sqlite3 -json</c> shell prints (one object per row; integers and floats as
/// numbers, text as strings, NULL as null, blobs as hex). The browser importer reads other browsers' databases
/// this way on Linux because the <c>sqlite3</c> command line tool is not installed by default on most desktops,
/// while libsqlite3 always is (WebKitGTK itself needs it).
///
/// Callers hand it a private <i>copy</i> of the database (never the live file), so nothing here takes or waits for
/// a lock; it still opens read-write only because SQLite must be able to create the -shm file next to a
/// WAL-mode copy (it falls back to read-only by itself when the copy is read-only).
/// </summary>
internal static unsafe partial class LinuxSqlite
{
    private const string Lib = "libsqlite3.so.0";
    private const int Row = 100, OpenReadWrite = 2, OpenNoMutex = 0x8000;
    private const int TypeInteger = 1, TypeFloat = 2, TypeText = 3, TypeBlob = 4; // SQLITE_NULL = 5

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial int sqlite3_open_v2(string file, out IntPtr db, int flags, IntPtr vfs);
    [LibraryImport(Lib)] private static partial int sqlite3_close(IntPtr db);
    [LibraryImport(Lib)] private static partial int sqlite3_busy_timeout(IntPtr db, int ms);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial int sqlite3_prepare_v2(IntPtr db, string sql, int nByte, out IntPtr stmt, IntPtr tail);
    [LibraryImport(Lib)] private static partial int sqlite3_step(IntPtr stmt);
    [LibraryImport(Lib)] private static partial int sqlite3_finalize(IntPtr stmt);
    [LibraryImport(Lib)] private static partial int sqlite3_column_count(IntPtr stmt);
    [LibraryImport(Lib)] private static partial IntPtr sqlite3_column_name(IntPtr stmt, int i);
    [LibraryImport(Lib)] private static partial int sqlite3_column_type(IntPtr stmt, int i);
    [LibraryImport(Lib)] private static partial long sqlite3_column_int64(IntPtr stmt, int i);
    [LibraryImport(Lib)] private static partial double sqlite3_column_double(IntPtr stmt, int i);
    [LibraryImport(Lib)] private static partial IntPtr sqlite3_column_text(IntPtr stmt, int i);
    [LibraryImport(Lib)] private static partial IntPtr sqlite3_column_blob(IntPtr stmt, int i);
    [LibraryImport(Lib)] private static partial int sqlite3_column_bytes(IntPtr stmt, int i);
    [LibraryImport(Lib)] private static partial IntPtr sqlite3_errmsg(IntPtr db);

    /// <summary>Returns false only when libsqlite3 itself cannot be used (the caller then falls back to the
    /// <c>sqlite3</c> tool). Otherwise true: <paramref name="rows"/> holds the result (null for zero rows) or
    /// <paramref name="error"/> says why the query failed (missing table, corrupt file, ...).</summary>
    internal static bool TryQuery(string dbPath, string sql, out JsonDocument? rows, out string? error)
    {
        rows = null;
        error = null;
        IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
        try
        {
            var rc = sqlite3_open_v2(dbPath, out db, OpenReadWrite | OpenNoMutex, IntPtr.Zero);
            if (rc != 0) { error = Err(db, rc); return true; }
            sqlite3_busy_timeout(db, 3000);
            rc = sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero);
            if (rc != 0) { error = Err(db, rc); return true; }

            var buf = new ArrayBufferWriter<byte>(4096);
            using var w = new Utf8JsonWriter(buf);
            w.WriteStartArray();
            var cols = sqlite3_column_count(stmt);
            var names = new string[cols];
            for (var i = 0; i < cols; i++) names[i] = Marshal.PtrToStringUTF8(sqlite3_column_name(stmt, i)) ?? "c" + i;
            var n = 0;
            while ((rc = sqlite3_step(stmt)) == Row)
            {
                n++;
                w.WriteStartObject();
                for (var i = 0; i < cols; i++)
                {
                    w.WritePropertyName(names[i]);
                    switch (sqlite3_column_type(stmt, i))
                    {
                        case TypeInteger: w.WriteNumberValue(sqlite3_column_int64(stmt, i)); break;
                        case TypeFloat:
                            var d = sqlite3_column_double(stmt, i);
                            if (double.IsFinite(d)) w.WriteNumberValue(d); else w.WriteNullValue();
                            break;
                        case TypeText:
                        {
                            var p = sqlite3_column_text(stmt, i);
                            var len = sqlite3_column_bytes(stmt, i);
                            w.WriteStringValue(p == IntPtr.Zero ? "" : Encoding.UTF8.GetString((byte*)p, len));
                            break;
                        }
                        case TypeBlob:
                        {
                            var p = sqlite3_column_blob(stmt, i);
                            var len = sqlite3_column_bytes(stmt, i);
                            w.WriteStringValue(p == IntPtr.Zero ? "" : Convert.ToHexString(new ReadOnlySpan<byte>((void*)p, len)));
                            break;
                        }
                        default: w.WriteNullValue(); break;
                    }
                }
                w.WriteEndObject();
            }
            if (rc != 101) { error = Err(db, rc); return true; } // SQLITE_DONE = 101
            w.WriteEndArray();
            w.Flush();
            if (n > 0) rows = JsonDocument.Parse(buf.WrittenMemory);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    private static string Err(IntPtr db, int rc)
        => (db == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(sqlite3_errmsg(db))) is { Length: > 0 } m ? m : "sqlite error " + rc;
}
