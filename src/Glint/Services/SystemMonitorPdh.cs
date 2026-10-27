using System.Runtime.InteropServices;

namespace Glint.Services;

/// <summary>
/// Thin wrapper over a PDH query. Counters are added by their English paths (works on any UI language),
/// and every read fails soft: a missing counter or a not-yet-primed rate just yields null.
/// Not thread-safe; the monitor uses it from its sampling thread only.
/// </summary>
sealed class PdhQuery : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x200;
    const uint PDH_FMT_NOCAP100 = 0x8000;
    const uint PDH_MORE_DATA = 0x800007D2;
    const uint PDH_CSTATUS_VALID_DATA = 0, PDH_CSTATUS_NEW_DATA = 1;

    IntPtr _query;

    public PdhQuery()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    /// <summary>Returns the counter handle, or IntPtr.Zero if this counter doesn't exist on this machine.</summary>
    public IntPtr Add(string englishPath)
    {
        if (_query == IntPtr.Zero) return IntPtr.Zero;
        return PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;
    }

    /// <summary>Takes a sample of every counter. Rate counters need two collects before they have a value.</summary>
    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    public double? Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var value) != 0) return null;
        return IsValid(value) ? value.DoubleValue : null;
    }

    /// <summary>All instances of a wildcard counter, e.g. "\Processor Information(*)\% Processor Utility".</summary>
    public List<(string Instance, double Value)>? Instances(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        // Instances can appear between the two calls (GPU engines of new processes), so retry a couple of times.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            var status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
            if (status != PDH_MORE_DATA || size == 0) return null;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buffer);
                if (status == PDH_MORE_DATA) continue;
                if (status != 0) return null;

                var stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
                var result = new List<(string, double)>((int)count);
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(buffer + i * stride);
                    if (!IsValid(item.Value)) continue;
                    result.Add((Marshal.PtrToStringUni(item.Name) ?? string.Empty, item.Value.DoubleValue));
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    static bool IsValid(in PDH_FMT_COUNTERVALUE v) =>
        (v.CStatus == PDH_CSTATUS_VALID_DATA || v.CStatus == PDH_CSTATUS_NEW_DATA) && double.IsFinite(v.DoubleValue);

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    // ───────────── Interop ─────────────

    // The value is a union with 8-byte alignment, so on both x86 and x64 it starts at offset 8.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);
}
