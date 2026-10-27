using System.Runtime.InteropServices;
using System.Text;

namespace Glint.Services.Indicators;

/// <summary>
/// Read-only access to SQLite files through winsqlite3.dll, which ships with Windows 10 and 11,
/// so Glint needs no SQLite package or native library of its own.
/// </summary>
internal sealed class WinSqlite : IDisposable
{
    const string Dll = "winsqlite3.dll";
    const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    const int SQLITE_OPEN_READONLY = 0x1, SQLITE_OPEN_NOMUTEX = 0x8000;

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    IntPtr _db;

    WinSqlite(IntPtr db) => _db = db;

    /// <summary>Opens the file read-only; the writer (here the notification service) keeps working normally.</summary>
    public static WinSqlite? OpenReadOnly(string path)
    {
        var name = Encoding.UTF8.GetBytes(path + "\0");
        if (sqlite3_open_v2(name, out var db, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, IntPtr.Zero) != SQLITE_OK)
        {
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
            return null;
        }
        // The service may be mid-write; wait a little instead of failing.
        sqlite3_busy_timeout(db, 500);
        return new WinSqlite(db);
    }

    /// <summary>Runs a query with optional int64 parameters (?1, ?2, ...) and reads each row.</summary>
    public void Query(string sql, Action<Row> onRow, params long[] args)
    {
        var bytes = Encoding.UTF8.GetBytes(sql + "\0");
        if (sqlite3_prepare_v2(_db, bytes, bytes.Length, out var stmt, IntPtr.Zero) != SQLITE_OK || stmt == IntPtr.Zero)
            throw new InvalidOperationException("SQLite prepare failed");
        try
        {
            for (var i = 0; i < args.Length; i++) sqlite3_bind_int64(stmt, i + 1, args[i]);
            var row = new Row(stmt);
            int rc;
            while ((rc = sqlite3_step(stmt)) == SQLITE_ROW) onRow(row);
            // BUSY, IOERR and the like must not pass for "no rows".
            if (rc != SQLITE_DONE) throw new InvalidOperationException($"SQLite step failed ({rc})");
        }
        finally
        {
            sqlite3_finalize(stmt);
        }
    }

    public readonly struct Row
    {
        readonly IntPtr _stmt;

        public Row(IntPtr stmt) => _stmt = stmt;

        public long Int64(int col) => sqlite3_column_int64(_stmt, col);

        public string? Text(int col)
        {
            var p = sqlite3_column_text(_stmt, col);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p, sqlite3_column_bytes(_stmt, col));
        }

        public byte[] Blob(int col)
        {
            var p = sqlite3_column_blob(_stmt, col);
            var n = sqlite3_column_bytes(_stmt, col);
            if (p == IntPtr.Zero || n <= 0) return Array.Empty<byte>();
            var data = new byte[n];
            Marshal.Copy(p, data, 0, n);
            return data;
        }
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero) sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }
}
