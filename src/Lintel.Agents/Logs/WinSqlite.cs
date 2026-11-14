// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Lintel.Agents.Logs;

/// <summary>Minimal read-only SQLite over the winsqlite3.dll that ships with Windows 10+ (no native package).</summary>
internal sealed class WinSqlite : IDisposable
{
    private const string Dll = "winsqlite3";
    private const int Ok = 0, Row = 100, ReadOnly = 1, NoMutex = 0x8000, ReadWriteCreate = 6;
    private IntPtr db;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr stmt, IntPtr tail);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int column);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr stmt, int column);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr stmt, int column);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, IntPtr error);

    /// <summary>A row the callback may read; valid only during the callback.</summary>
    internal readonly struct Cursor(IntPtr stmt)
    {
        public long Int(int column) => sqlite3_column_int64(stmt, column);
        public bool IsNull(int column) => sqlite3_column_type(stmt, column) == 5;

        public string? Text(int column)
        {
            var p = sqlite3_column_text(stmt, column);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p, sqlite3_column_bytes(stmt, column));
        }
    }

    private WinSqlite(IntPtr db) => this.db = db;

    /// <summary>Null when the file cannot be opened (missing, locked, not a database).</summary>
    public static WinSqlite? OpenReadOnly(string path) => Open(path, ReadOnly | NoMutex);

    /// <summary>For tests: creates a scratch database.</summary>
    public static WinSqlite? OpenReadWrite(string path) => Open(path, ReadWriteCreate | NoMutex);

    private static WinSqlite? Open(string path, int flags)
    {
        try
        {
            if (sqlite3_open_v2(path, out var handle, flags, IntPtr.Zero) != Ok)
            {
                if (handle != IntPtr.Zero) sqlite3_close(handle);
                return null;
            }
            sqlite3_busy_timeout(handle, 2000);
            return new WinSqlite(handle);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool Exec(string sql) => sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == Ok;

    /// <summary>Runs a query; the callback returns false to stop. False when the SQL does not compile.</summary>
    public bool Query(string sql, Func<Cursor, bool> row, params long[] binds)
    {
        if (sqlite3_prepare_v2(db, sql, -1, out var stmt, IntPtr.Zero) != Ok) return false;
        try
        {
            for (var i = 0; i < binds.Length; i++) sqlite3_bind_int64(stmt, i + 1, binds[i]);
            while (sqlite3_step(stmt) == Row)
                if (!row(new Cursor(stmt))) break;
            return true;
        }
        finally { sqlite3_finalize(stmt); }
    }

    public long? Scalar(string sql, params long[] binds)
    {
        long? result = null;
        Query(sql, c => { result = c.IsNull(0) ? null : c.Int(0); return false; }, binds);
        return result;
    }

    public bool TableExists(string name) =>
        Scalar($"SELECT 1 FROM sqlite_master WHERE type='table' AND name='{name.Replace("'", "''")}'") is 1;

    public void Dispose()
    {
        if (db == IntPtr.Zero) return;
        sqlite3_close(db);
        db = IntPtr.Zero;
    }
}
