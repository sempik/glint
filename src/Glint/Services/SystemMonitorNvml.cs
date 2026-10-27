using System.Runtime.InteropServices;
using System.Text;

namespace Glint.Services;

/// <summary>
/// NVIDIA GPU readings through NVML (nvml.dll ships with the driver). Bound at runtime, so machines without
/// an NVIDIA card simply get null from <see cref="TryOpen"/>. Each reading is optional: a call the driver
/// doesn't support (fan on a passive card, power on some laptops) leaves that value null.
/// </summary>
sealed class NvmlGpu : IDisposable
{
    const int NVML_SUCCESS = 0;
    const int NVML_ERROR_UNINITIALIZED = 1;
    const int NVML_ERROR_DRIVER_NOT_LOADED = 9;
    const int NVML_ERROR_GPU_IS_LOST = 15;
    const int NVML_ERROR_LIB_RM_VERSION_MISMATCH = 18;
    const int NVML_TEMPERATURE_GPU = 0;
    const int NVML_CLOCK_GRAPHICS = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct NvmlUtilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NoArgsFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CountFn(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HandleFn(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NameFn(IntPtr device, [Out] byte[] name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UtilizationFn(IntPtr device, out NvmlUtilization utilization);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int MemoryFn(IntPtr device, out NvmlMemory memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UIntFn(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UIntByKindFn(IntPtr device, int kind, out uint value);

    readonly IntPtr _lib;
    readonly IntPtr _device;
    readonly NoArgsFn? _shutdown;
    readonly UtilizationFn? _utilization;
    readonly MemoryFn? _memory;
    readonly UIntByKindFn? _temperature, _clock;
    readonly UIntFn? _power, _powerLimit, _fan;
    bool _disposed;

    public string Name { get; }

    NvmlGpu(IntPtr lib, IntPtr device, string name, NoArgsFn? shutdown)
    {
        _lib = lib;
        _device = device;
        _shutdown = shutdown;
        Name = name;
        _utilization = Bind<UtilizationFn>(lib, "nvmlDeviceGetUtilizationRates");
        _memory = Bind<MemoryFn>(lib, "nvmlDeviceGetMemoryInfo");
        _temperature = Bind<UIntByKindFn>(lib, "nvmlDeviceGetTemperature");
        _clock = Bind<UIntByKindFn>(lib, "nvmlDeviceGetClockInfo");
        _power = Bind<UIntFn>(lib, "nvmlDeviceGetPowerUsage");
        _powerLimit = Bind<UIntFn>(lib, "nvmlDeviceGetEnforcedPowerLimit");
        _fan = Bind<UIntFn>(lib, "nvmlDeviceGetFanSpeed");
    }

    /// <summary>Loads NVML and opens the first NVIDIA GPU, or returns null.</summary>
    public static NvmlGpu? TryOpen()
    {
        if (!TryLoad(out var lib)) return null;
        try
        {
            var init = Bind<NoArgsFn>(lib, "nvmlInit_v2");
            var shutdown = Bind<NoArgsFn>(lib, "nvmlShutdown");
            var getCount = Bind<CountFn>(lib, "nvmlDeviceGetCount_v2");
            var getHandle = Bind<HandleFn>(lib, "nvmlDeviceGetHandleByIndex_v2");
            if (init is null || getCount is null || getHandle is null || init() != NVML_SUCCESS)
            {
                NativeLibrary.Free(lib);
                return null;
            }

            if (getCount(out var count) != NVML_SUCCESS || count == 0 || getHandle(0, out var device) != NVML_SUCCESS)
            {
                shutdown?.Invoke();
                NativeLibrary.Free(lib);
                return null;
            }

            var name = "NVIDIA GPU";
            if (Bind<NameFn>(lib, "nvmlDeviceGetName") is { } getName)
            {
                var buffer = new byte[96];
                if (getName(device, buffer, (uint)buffer.Length) == NVML_SUCCESS)
                {
                    var length = Array.IndexOf(buffer, (byte)0);
                    var text = Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length).Trim();
                    if (text.Length > 0) name = text;
                }
            }
            return new NvmlGpu(lib, device, name, shutdown);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            NativeLibrary.Free(lib);
            return null;
        }
    }

    static bool TryLoad(out IntPtr lib)
    {
        // The driver puts nvml.dll in System32; old drivers only had it under NVSMI. Search System32 only,
        // so a stray nvml.dll in the app folder, current folder or PATH is never loaded.
        if (NativeLibrary.TryLoad("nvml.dll", typeof(NvmlGpu).Assembly, DllImportSearchPath.System32, out lib)) return true;
        var nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
        return File.Exists(nvsmi) && NativeLibrary.TryLoad(nvsmi, out lib);
    }

    static T? Bind<T>(IntPtr lib, string export) where T : Delegate =>
        NativeLibrary.TryGetExport(lib, export, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    public GpuSnapshot Read()
    {
        double? load = null, temperature = null, power = null, powerLimit = null, fan = null, clock = null;
        ulong? memUsed = null, memTotal = null;
        var fatal = NVML_SUCCESS;

        // Unsupported readings just stay null; a lost GPU or driver (TDR, driver update) fails every call
        // for good, so report it and let the caller switch to the generic counters.
        bool Ok(int result)
        {
            if (result is NVML_ERROR_UNINITIALIZED or NVML_ERROR_DRIVER_NOT_LOADED or NVML_ERROR_GPU_IS_LOST or NVML_ERROR_LIB_RM_VERSION_MISMATCH)
                fatal = result;
            return result == NVML_SUCCESS;
        }

        if (_utilization is not null && Ok(_utilization(_device, out var u))) load = Math.Min(100, u.Gpu);
        if (_temperature is not null && Ok(_temperature(_device, NVML_TEMPERATURE_GPU, out var t))) temperature = t;
        if (_memory is not null && Ok(_memory(_device, out var m)) && m.Total > 0)
        {
            memUsed = m.Used;
            memTotal = m.Total;
        }
        // Power is reported in milliwatts.
        if (_power is not null && Ok(_power(_device, out var mw))) power = mw / 1000.0;
        if (_powerLimit is not null && Ok(_powerLimit(_device, out var limit)) && limit > 0) powerLimit = limit / 1000.0;
        if (_fan is not null && Ok(_fan(_device, out var f))) fan = Math.Min(100, f);
        if (_clock is not null && Ok(_clock(_device, NVML_CLOCK_GRAPHICS, out var mhz)) && mhz > 0) clock = mhz;

        if (fatal != NVML_SUCCESS) throw new InvalidOperationException($"NVML lost the GPU (error {fatal}).");
        return new GpuSnapshot(Name, load, temperature, memUsed, memTotal, power, powerLimit, fan, clock);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _shutdown?.Invoke();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        NativeLibrary.Free(_lib);
    }
}
