using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Glint.Services;

// ───────────── Snapshot (frozen data, safe to hand to any thread) ─────────────

public sealed record CpuSnapshot(
    string Name,
    // 0..100, null until two samples exist.
    double? Load,
    // Per logical processor, in OS order. Empty if the counters are missing.
    IReadOnlyList<double> CoreLoads,
    double? ClockMhz,
    int LogicalCores,
    int ProcessCount,
    TimeSpan Uptime);

public sealed record MemorySnapshot(ulong Total, ulong Used, double Percent, ulong CommitUsed, ulong CommitLimit);

public sealed record GpuSnapshot(
    string Name,
    double? Load,
    double? TemperatureC,
    ulong? MemoryUsed,
    ulong? MemoryTotal,
    double? PowerW,
    double? PowerLimitW,
    double? FanPercent,
    double? ClockMhz);

public sealed record DriveSnapshot(string Name, string Label, ulong Total, ulong Free)
{
    public ulong Used => Total - Math.Min(Free, Total);
    public double Percent => Total == 0 ? 0 : Used * 100.0 / Total;
}

public sealed record DiskActivitySnapshot(double? ActivePercent, double? ReadBytesPerSec, double? WriteBytesPerSec);

public sealed record NetworkSnapshot(double? DownBytesPerSec, double? UpBytesPerSec);

/// <summary>Processes with the same name are summed, like Task Manager groups them.</summary>
public sealed record ProcessLoad(string Name, int Count, double CpuPercent, long WorkingSet);

public sealed record SystemSnapshot(
    DateTime Time,
    CpuSnapshot Cpu,
    MemorySnapshot Memory,
    GpuSnapshot? Gpu,
    IReadOnlyList<DriveSnapshot> Drives,
    DiskActivitySnapshot DiskActivity,
    NetworkSnapshot Network,
    IReadOnlyList<ProcessLoad> TopProcesses,
    // Oldest first, up to SystemMonitor.HistoryLength values of 0..100.
    double[] CpuHistory,
    double[] GpuHistory,
    double[] MemoryHistory);

/// <summary>
/// Hardware load sampler for the "Система" tab. Samples once per second on a background thread, but only
/// while someone is subscribed, so it costs nothing when the tab isn't on screen.
/// Pure P/Invoke (GetSystemTimes, PDH, NVML, GlobalMemoryStatusEx): no WMI, no drivers, no admin rights.
/// CPU temperature is deliberately absent: Windows exposes it only to kernel drivers.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    public const int HistoryLength = 60;
    const int IntervalMs = 1000;
    const int TopProcessCount = 5;
    const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    static readonly Lazy<SystemMonitor> s_shared = new(() => new SystemMonitor());

    /// <summary>The app-wide instance; created on first use.</summary>
    public static SystemMonitor Shared => s_shared.Value;

    internal static bool IsSharedCreated => s_shared.IsValueCreated;

    readonly object _gate = new();
    readonly object _sampleLock = new();
    readonly AutoResetEvent _wake = new(false);
    readonly List<Action<SystemSnapshot>> _handlers = new();
    bool _running;
    bool _disposed;

    // Sampler state below is touched only under _sampleLock (in practice: the sampling thread).
    bool _initialized;
    string _cpuName = "Процессор";
    PdhQuery? _pdh;
    IntPtr _coreUtility, _coreTime, _perfPercent, _frequency;
    IntPtr _diskIdle, _diskRead, _diskWrite;
    IntPtr _gpuEngine, _gpuMemory;
    NvmlGpu? _nvml;
    string? _fallbackGpuName;
    ulong? _fallbackGpuMemory;

    long _lastStamp;
    (long Idle, long Kernel, long User)? _lastCpuTimes;
    Dictionary<string, (long Rx, long Tx)> _lastNet = new();
    Dictionary<(int Pid, long Start), TimeSpan> _lastProcTimes = new();

    readonly History _cpuHistory = new(), _gpuHistory = new(), _memHistory = new();

    /// <summary>Most recent snapshot, or null before the first sample.</summary>
    public SystemSnapshot? Latest { get; private set; }

    /// <summary>
    /// Starts sampling (if not running yet) and calls <paramref name="onSample"/> on the sampling thread
    /// after every sample. Dispose the result to unsubscribe; sampling stops with the last subscriber.
    /// </summary>
    public IDisposable Subscribe(Action<SystemSnapshot> onSample)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _handlers.Add(onSample);
            if (!_running)
            {
                _running = true;
                new Thread(Run) { IsBackground = true, Name = "Glint.SystemMonitor", Priority = ThreadPriority.BelowNormal }.Start();
            }
        }
        return new Subscription(this, onSample);
    }

    void Unsubscribe(Action<SystemSnapshot> onSample)
    {
        lock (_gate)
        {
            // Wake the thread so it notices it has no audience and exits now rather than after the next sample.
            if (_handlers.Remove(onSample) && _handlers.Count == 0) _wake.Set();
        }
    }

    sealed class Subscription(SystemMonitor owner, Action<SystemSnapshot> handler) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unsubscribe(handler);
        }
    }

    void Run()
    {
        try
        {
            lock (_sampleLock)
            {
                if (_disposed) return;
                EnsureInitialized();
                // After a long pause old history would draw a misleading "last minute", so drop it. A short
                // pause (panel re-hovered) keeps it, matching the cached Latest the page shows on reopen.
                // Old baselines would average the whole pause into the first values: prime new ones either way.
                if (Latest is not { } last || DateTime.Now - last.Time > TimeSpan.FromSeconds(5))
                {
                    _cpuHistory.Clear();
                    _gpuHistory.Clear();
                    _memHistory.Clear();
                }
                Sample(record: false);
            }

            // First real sample comes sooner so the tab fills quickly.
            var wait = IntervalMs / 2;
            while (true)
            {
                _wake.WaitOne(wait);
                lock (_gate)
                {
                    // Clear _running in the same critical section that decides to exit, so a Subscribe that
                    // lands right after this sees it false and starts a new thread.
                    if (_disposed || _handlers.Count == 0)
                    {
                        _running = false;
                        return;
                    }
                }

                var started = Environment.TickCount64;
                SystemSnapshot snapshot;
                lock (_sampleLock)
                {
                    if (_disposed) return;
                    snapshot = Sample(record: true);
                }
                Latest = snapshot;

                Action<SystemSnapshot>[] handlers;
                lock (_gate) handlers = _handlers.ToArray();
                foreach (var handler in handlers)
                {
                    try
                    {
                        handler(snapshot);
                    }
                    catch (Exception ex)
                    {
                        App.LogError(ex);
                    }
                }
                wait = (int)Math.Clamp(IntervalMs - (Environment.TickCount64 - started), 100, IntervalMs);
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            lock (_gate) _running = false;
        }
        // The remaining early returns happen only after Dispose, when Subscribe throws anyway.
    }

    // ───────────── Setup ─────────────

    void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        _cpuName = ReadCpuName() ?? _cpuName;

        _pdh = new PdhQuery();
        // "% Processor Utility" is what Task Manager shows; older systems only have "% Processor Time".
        _coreUtility = _pdh.Add(@"\Processor Information(*)\% Processor Utility");
        if (_coreUtility == IntPtr.Zero) _coreTime = _pdh.Add(@"\Processor(*)\% Processor Time");
        _perfPercent = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _frequency = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        _diskIdle = _pdh.Add(@"\PhysicalDisk(_Total)\% Idle Time");
        _diskRead = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");

        _nvml = NvmlGpu.TryOpen();
        if (_nvml is null)
        {
            // Any vendor: Windows' own GPU counters (the ones Task Manager uses).
            _gpuEngine = _pdh.Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage");
            _gpuMemory = _pdh.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
            (_fallbackGpuName, _fallbackGpuMemory) = ReadDisplayAdapter();
        }
    }

    static string? ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = (key?.GetValue("ProcessorNameString") as string)?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>First real display adapter from the registry (the Basic Display Adapter is skipped).</summary>
    static (string? Name, ulong? Memory) ReadDisplayAdapter()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null) return (null, null);
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)).Order())
            {
                try
                {
                    using var key = cls.OpenSubKey(sub);
                    var name = (key?.GetValue("DriverDesc") as string)?.Trim();
                    if (string.IsNullOrEmpty(name) || name.StartsWith("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
                    ulong? memory = key?.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l when l > 0 => (ulong)l,
                        byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                        _ => null,
                    };
                    return (name, memory);
                }
                catch (Exception)
                {
                    // Some subkeys (e.g. "Properties") are locked down; skip them.
                }
            }
        }
        catch (Exception)
        {
            // Unreadable class key: fall back to a generic name.
        }
        return (null, null);
    }

    // ───────────── Sampling ─────────────

    SystemSnapshot Sample(bool record)
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastStamp == 0 ? 0 : (now - _lastStamp) / (double)Stopwatch.Frequency;
        _lastStamp = now;

        _pdh?.Collect();

        var processes = SampleProcesses(seconds, out var processCount);
        var cpu = SampleCpu(processCount);
        var memory = SampleMemory();
        var gpu = SampleGpu();
        var drives = SampleDrives();
        var disk = new DiskActivitySnapshot(
            _pdh?.Value(_diskIdle) is double idle ? Math.Clamp(100 - idle, 0, 100) : null,
            _pdh?.Value(_diskRead),
            _pdh?.Value(_diskWrite));
        var network = SampleNetwork(seconds);

        if (record)
        {
            _cpuHistory.Add(cpu.Load ?? 0);
            _gpuHistory.Add(gpu?.Load ?? 0);
            _memHistory.Add(memory.Percent);
        }

        return new SystemSnapshot(
            DateTime.Now, cpu, memory, gpu, drives, disk, network, processes,
            _cpuHistory.ToArray(), _gpuHistory.ToArray(), _memHistory.ToArray());
    }

    CpuSnapshot SampleCpu(int processCount)
    {
        double? load = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (_lastCpuTimes is { } last)
            {
                // Kernel time includes idle time.
                var total = (kernel - last.Kernel) + (user - last.User);
                if (total > 0) load = Math.Clamp(100.0 * (total - (idle - last.Idle)) / total, 0, 100);
            }
            _lastCpuTimes = (idle, kernel, user);
        }

        var cores = new List<(long Order, double Value)>();
        double? utilityTotal = null;
        var instances = _pdh?.Instances(_coreUtility != IntPtr.Zero ? _coreUtility : _coreTime);
        if (instances is not null)
        {
            foreach (var (instance, value) in instances)
            {
                if (instance == "_Total")
                {
                    utilityTotal = Math.Clamp(value, 0, 100);
                    continue;
                }
                if (instance.EndsWith("_Total", StringComparison.Ordinal)) continue;
                // "Processor Information" names are "group,index"; "Processor" names are just "index".
                var comma = instance.IndexOf(',');
                var groupPart = comma < 0 ? "0" : instance[..comma];
                var indexPart = comma < 0 ? instance : instance[(comma + 1)..];
                if (!int.TryParse(groupPart, out var group) || !int.TryParse(indexPart, out var index)) continue;
                cores.Add(((long)group << 32 | (uint)index, Math.Clamp(value, 0, 100)));
            }
        }
        cores.Sort((a, b) => a.Order.CompareTo(b.Order));

        // Prefer Processor Utility for the total too, so the number matches Task Manager on Windows 10/11.
        if (_coreUtility != IntPtr.Zero && utilityTotal is not null) load = utilityTotal;

        double? clock = null;
        if (_pdh?.Value(_perfPercent) is double perf && _pdh.Value(_frequency) is double baseMhz && baseMhz > 0 && perf > 0)
            clock = perf * baseMhz / 100;

        return new CpuSnapshot(
            _cpuName, load, cores.Select(c => c.Value).ToArray(), clock,
            Environment.ProcessorCount, processCount, TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    static MemorySnapshot SampleMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0) return new MemorySnapshot(0, 0, 0, 0, 0);
        var used = status.ullTotalPhys - status.ullAvailPhys;
        // "Page file" here is the commit limit (RAM + page files), and its free part is the uncommitted rest.
        return new MemorySnapshot(
            status.ullTotalPhys, used, used * 100.0 / status.ullTotalPhys,
            status.ullTotalPageFile - status.ullAvailPageFile, status.ullTotalPageFile);
    }

    GpuSnapshot? SampleGpu()
    {
        if (_nvml is not null)
        {
            try
            {
                return _nvml.Read();
            }
            catch (Exception ex)
            {
                // A driver update can pull NVML from under us; switch to the generic counters.
                App.LogError(ex);
                _nvml.Dispose();
                _nvml = null;
                _gpuEngine = _pdh?.Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage") ?? IntPtr.Zero;
                _gpuMemory = _pdh?.Add(@"\GPU Adapter Memory(*)\Dedicated Usage") ?? IntPtr.Zero;
                (_fallbackGpuName, _fallbackGpuMemory) = ReadDisplayAdapter();
                return null;
            }
        }

        double? load = null;
        if (_pdh?.Instances(_gpuEngine) is { } engines)
        {
            // Instance: "pid_1234_luid_0x00000000_0x0000D1C2_phys_0_eng_0_engtype_3D". Sum per adapter, take the busiest.
            var perAdapter = new Dictionary<string, double>();
            foreach (var (instance, value) in engines)
            {
                var luid = AdapterKey(instance);
                perAdapter[luid] = perAdapter.GetValueOrDefault(luid) + value;
            }
            load = perAdapter.Count == 0 ? 0 : Math.Clamp(perAdapter.Values.Max(), 0, 100);
        }

        ulong? memUsed = null;
        if (_pdh?.Instances(_gpuMemory) is { Count: > 0 } memories)
            memUsed = (ulong)Math.Max(0, memories.Max(m => m.Value));

        if (load is null && memUsed is null && _fallbackGpuName is null) return null;
        return new GpuSnapshot(_fallbackGpuName ?? "Видеокарта", load, null, memUsed, _fallbackGpuMemory, null, null, null, null);
    }

    static string AdapterKey(string instance)
    {
        var start = instance.IndexOf("luid_", StringComparison.Ordinal);
        if (start < 0) return instance;
        var end = instance.IndexOf("_phys", start, StringComparison.Ordinal);
        return end < 0 ? instance[start..] : instance[start..end];
    }

    static IReadOnlyList<DriveSnapshot> SampleDrives()
    {
        var result = new List<DriveSnapshot>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    result.Add(new DriveSnapshot(drive.Name.TrimEnd('\\'), drive.VolumeLabel, (ulong)drive.TotalSize, (ulong)drive.TotalFreeSpace));
                }
                catch (Exception)
                {
                    // A drive can vanish or lock up between enumeration and the query.
                }
            }
        }
        catch (Exception)
        {
            // Enumeration itself failing just means no drive list this second.
        }
        return result;
    }

    NetworkSnapshot SampleNetwork(double seconds)
    {
        var current = new Dictionary<string, (long Rx, long Tx)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    var stats = nic.GetIPStatistics();
                    current[nic.Id] = (stats.BytesReceived, stats.BytesSent);
                }
                catch (Exception)
                {
                    // Adapters can disappear mid-enumeration (VPN disconnect).
                }
            }
        }
        catch (Exception)
        {
            return new NetworkSnapshot(null, null);
        }

        double? down = null, up = null;
        if (seconds > 0 && _lastNet.Count > 0)
        {
            long rx = 0, tx = 0;
            foreach (var (id, now) in current)
            {
                if (!_lastNet.TryGetValue(id, out var before)) continue;
                // Counters reset when an adapter reconnects; ignore that interval for it.
                rx += Math.Max(0, now.Rx - before.Rx);
                tx += Math.Max(0, now.Tx - before.Tx);
            }
            down = rx / seconds;
            up = tx / seconds;
        }
        _lastNet = current;
        return new NetworkSnapshot(down, up);
    }

    IReadOnlyList<ProcessLoad> SampleProcesses(double seconds, out int count)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            count = 0;
            return Array.Empty<ProcessLoad>();
        }

        count = processes.Length;
        var current = new Dictionary<(int, long), TimeSpan>(processes.Length);
        var groups = new Dictionary<string, (int Count, double Cpu, long Ram)>(StringComparer.OrdinalIgnoreCase);
        var budget = seconds * Environment.ProcessorCount;

        foreach (var p in processes)
        {
            try
            {
                if (p.Id == 0) continue; // System Idle Process
                var name = p.ProcessName;
                var ram = p.WorkingSet64;
                double cpu = 0;
                try
                {
                    // Protected processes deny these; they still count with their memory.
                    var key = (p.Id, p.StartTime.Ticks);
                    var total = p.TotalProcessorTime;
                    current[key] = total;
                    if (budget > 0 && _lastProcTimes.TryGetValue(key, out var before))
                        cpu = Math.Max(0, (total - before).TotalSeconds) * 100 / budget;
                }
                catch (Exception)
                {
                }
                var g = groups.GetValueOrDefault(name);
                groups[name] = (g.Count + 1, g.Cpu + cpu, g.Ram + ram);
            }
            catch (Exception)
            {
                // The process exited during the scan.
            }
            finally
            {
                p.Dispose();
            }
        }
        _lastProcTimes = current;

        return groups
            .OrderByDescending(g => g.Value.Cpu)
            .ThenByDescending(g => g.Value.Ram)
            .Take(TopProcessCount)
            .Select(g => new ProcessLoad(g.Key, g.Value.Count, Math.Min(100, g.Value.Cpu), g.Value.Ram))
            .ToArray();
    }

    // ───────────── Lifetime ─────────────

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _handlers.Clear();
        }
        _wake.Set();
        // Waits for an in-flight sample to finish, so native handles aren't closed under it.
        lock (_sampleLock)
        {
            _pdh?.Dispose();
            _pdh = null;
            _nvml?.Dispose();
            _nvml = null;
        }
    }

    /// <summary>Fixed-size ring of the last <see cref="HistoryLength"/> values.</summary>
    sealed class History
    {
        readonly double[] _items = new double[HistoryLength];
        int _start, _count;

        public void Add(double value)
        {
            _items[(_start + _count) % HistoryLength] = value;
            if (_count < HistoryLength) _count++;
            else _start = (_start + 1) % HistoryLength;
        }

        public void Clear() => _start = _count = 0;

        public double[] ToArray()
        {
            var result = new double[_count];
            for (var i = 0; i < _count; i++) result[i] = _items[(_start + i) % HistoryLength];
            return result;
        }
    }

    // ───────────── Interop ─────────────

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    // FILETIMEs, read as 64-bit counts of 100 ns.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
