using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Glint.Services.Control;

/// <summary>A paired Bluetooth headset or speaker.</summary>
public sealed record BluetoothAudioDevice(ulong Address, string Name, bool Connected, bool IsHeadset);

/// <summary>
/// Paired Bluetooth audio devices with one-press connect/disconnect.
/// Connecting goes the way the Windows sound flyout does it: the Bluetooth audio driver's kernel-streaming filter
/// takes a one-shot "reconnect" or "disconnect" request (KSPROPSETID_BtAudio). Devices without such a filter
/// (an unusual driver) fall back to turning the A2DP/HFP services off and on through bthprops, which also
/// makes Windows reconnect but reinstalls the audio endpoints, so it is only the second choice.
/// </summary>
public static partial class BluetoothAudio
{
    // Major device class "Audio/Video" in the Class of Device field.
    const uint MajorClassMask = 0x1F00, MajorAudioVideo = 0x0400;
    // Minor classes (bits 2..7) of the audio major class that are headsets/hands-free (mic + speaker).
    static readonly uint[] HeadsetMinor = { 1, 2 };

    static readonly Guid A2dpSink = new("0000110B-0000-1000-8000-00805F9B34FB");
    static readonly Guid Handsfree = new("0000111E-0000-1000-8000-00805F9B34FB");
    static readonly Guid Headset = new("00001108-0000-1000-8000-00805F9B34FB");
    static readonly Guid KsCategoryAudio = new("6994AD04-93EF-11D0-A3CC-00A0C9223196");
    static readonly Guid KsPropSetBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    const uint KsPropertyOneShotReconnect = 0, KsPropertyOneShotDisconnect = 1;

    /// <summary>Paired devices of the audio class; empty when Bluetooth is off or missing.</summary>
    public static List<BluetoothAudioDevice> List()
    {
        var result = new List<BluetoothAudioDevice>();
        foreach (var info in EnumeratePaired())
        {
            if ((info.ulClassofDevice & MajorClassMask) != MajorAudioVideo) continue;
            var minor = (info.ulClassofDevice >> 2) & 0x3F;
            var name = string.IsNullOrWhiteSpace(info.szName) ? FormatAddress(info.Address) : info.szName.Trim();
            result.Add(new BluetoothAudioDevice(info.Address, name, info.fConnected != 0, HeadsetMinor.Contains(minor)));
        }
        return result.OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Connects or disconnects; blocks for a moment, so call it off the UI thread. Returns an error or null.</summary>
    public static string? SetConnected(ulong address, bool connect)
    {
        var filters = FindFilters(address);
        if (filters.Count > 0)
        {
            var any = false;
            foreach (var path in filters)
                any |= SendOneShot(path, connect ? KsPropertyOneShotReconnect : KsPropertyOneShotDisconnect);
            if (any) return null;
        }
        return ToggleServices(address, connect);
    }

    // ───────────── Kernel-streaming route ─────────────

    /// <summary>
    /// Audio filter interfaces of this device. A2DP ones carry the address in their path; the hands-free filter
    /// doesn't, so it is matched through the container id it shares with the A2DP one.
    /// </summary>
    static List<string> FindFilters(ulong address)
    {
        var mine = new List<string>();
        var containers = new HashSet<Guid>();
        var handsfree = new List<(string Path, Guid? Container)>();
        var hex = address.ToString("x12");
        foreach (var (path, container) in EnumerateAudioFilters())
        {
            var lower = path.ToLowerInvariant();
            if (lower.Contains("bthenum#"))
            {
                if (AddressInPath().Match(lower) is { Success: true } m && m.Groups[1].Value == hex)
                {
                    mine.Add(path);
                    if (container is Guid g) containers.Add(g);
                }
            }
            else if (lower.Contains("bthhfenum#"))
            {
                handsfree.Add((path, container));
            }
        }
        foreach (var (path, container) in handsfree)
            if (container is Guid g && containers.Contains(g)) mine.Add(path);
        return mine;
    }

    // ...&0&e8eecc0d4b1a_c00000000#{...}: the 12 hex digits before "_c" are the device address.
    [GeneratedRegex(@"&([0-9a-f]{12})_c[0-9a-f]+#")]
    private static partial Regex AddressInPath();

    static IEnumerable<(string Path, Guid? Container)> EnumerateAudioFilters()
    {
        var category = KsCategoryAudio;
        var set = SetupDiGetClassDevs(ref category, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) yield break;
        try
        {
            for (uint i = 0; ; i++)
            {
                var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref category, i, ref iface)) break;
                var (path, container) = ReadInterface(set, ref iface);
                if (path is not null) yield return (path, container);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    static (string? Path, Guid? Container) ReadInterface(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
        if (needed == 0) return (null, null);
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            // cbSize is that of the fixed part: a DWORD plus one WCHAR, padded to 8 on x64.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            var devInfo = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, buffer, needed, out _, ref devInfo)) return (null, null);
            var path = Marshal.PtrToStringUni(buffer + 4);
            Guid? container = null;
            var key = DevpkeyContainerId;
            if (SetupDiGetDeviceProperty(set, ref devInfo, ref key, out var type, out var guid, 16, out _, 0) && type == DEVPROP_TYPE_GUID)
                container = guid;
            return (path, container);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static bool SendOneShot(string path, uint id)
    {
        using var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        var property = new KSPROPERTY { Set = KsPropSetBtAudio, Id = id, Flags = KSPROPERTY_TYPE_GET };
        return DeviceIoControl(handle, IOCTL_KS_PROPERTY, ref property, Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    // ───────────── Service fallback ─────────────

    static string? ToggleServices(ulong address, bool connect)
    {
        var device = EnumeratePaired().FirstOrDefault(d => d.Address == address);
        if (device.dwSize == 0) return "Устройство не найдено среди сопряжённых";
        var radio = FirstRadio();
        try
        {
            bool changed = false, lost = false;
            foreach (var service in new[] { A2dpSink, Handsfree, Headset })
            {
                var guid = service;
                // Disabling a service drops its connection; enabling it again makes Windows connect to it.
                var rc = BluetoothSetServiceState(radio, ref device, ref guid, BLUETOOTH_SERVICE_DISABLE);
                if (!connect)
                {
                    changed |= rc == 0;
                    continue;
                }
                // Enabled whatever the disable returned: after a disconnect through this route the service is
                // already off, and the disable fails for exactly the devices that need the enable.
                guid = service;
                var enabled = BluetoothSetServiceState(radio, ref device, ref guid, BLUETOOTH_SERVICE_ENABLE) == 0;
                changed |= enabled;
                // It was on a moment ago and is off now: say so rather than report a connect without, say, the mic.
                lost |= rc == 0 && !enabled;
            }
            if (lost) return "Подключено не полностью — часть профилей не включилась, проверьте в параметрах Bluetooth";
            if (!changed) return connect ? "Не удалось подключить: устройство не отвечает" : "Не удалось отключить";
            // A disconnect through services leaves them off until the next connect turns them back on.
            return null;
        }
        finally
        {
            if (radio != IntPtr.Zero) CloseHandle(radio);
        }
    }

    // ───────────── bthprops ─────────────

    static IEnumerable<BLUETOOTH_DEVICE_INFO> EnumeratePaired()
    {
        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = 1,
            fReturnRemembered = 1,
            fReturnConnected = 1,
            fReturnUnknown = 0,
            // No inquiry: only what Windows already knows, so the call returns at once.
            fIssueInquiry = 0,
        };
        var info = new BLUETOOTH_DEVICE_INFO { dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
        IntPtr find;
        try
        {
            find = BluetoothFindFirstDevice(ref search, ref info);
        }
        catch (DllNotFoundException)
        {
            yield break;
        }
        catch (EntryPointNotFoundException)
        {
            yield break;
        }
        if (find == IntPtr.Zero) yield break;
        try
        {
            var seen = new HashSet<ulong>();
            do
            {
                if ((info.fRemembered != 0 || info.fAuthenticated != 0) && seen.Add(info.Address)) yield return info;
                info = new BLUETOOTH_DEVICE_INFO { dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
            }
            while (BluetoothFindNextDevice(find, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(find);
        }
    }

    static IntPtr FirstRadio()
    {
        var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        var find = BluetoothFindFirstRadio(ref p, out var radio);
        if (find != IntPtr.Zero) BluetoothFindRadioClose(find);
        return find == IntPtr.Zero ? IntPtr.Zero : radio;
    }

    static string FormatAddress(ulong a) => string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (40 - 8 * i)) & 0xFF).ToString("X2")));

    // ───────────── Interop ─────────────

    const uint BLUETOOTH_SERVICE_DISABLE = 0, BLUETOOTH_SERVICE_ENABLE = 1;
    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint DEVPROP_TYPE_GUID = 0x0D;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    // CTL_CODE(FILE_DEVICE_KS, 0, METHOD_NEITHER, FILE_ANY_ACCESS)
    const uint IOCTL_KS_PROPERTY = 0x002F0003;
    const uint KSPROPERTY_TYPE_GET = 1;

    static DEVPROPKEY DevpkeyContainerId => new() { fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), pid = 2 };

    [StructLayout(LayoutKind.Sequential)]
    struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public int dwSize;
        public int fReturnAuthenticated;
        public int fReturnRemembered;
        public int fReturnUnknown;
        public int fReturnConnected;
        public int fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct BLUETOOTH_DEVICE_INFO
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected;
        public int fRemembered;
        public int fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BLUETOOTH_FIND_RADIO_PARAMS { public int dwSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct KSPROPERTY { public Guid Set; public uint Id; public uint Flags; }

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS search, ref BLUETOOTH_DEVICE_INFO info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool BluetoothFindNextDevice(IntPtr find, ref BLUETOOTH_DEVICE_INFO info);

    [DllImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool BluetoothFindDeviceClose(IntPtr find);

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr radio);

    [DllImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("bthprops.cpl")]
    static extern uint BluetoothSetServiceState(IntPtr radio, ref BLUETOOTH_DEVICE_INFO device, ref Guid service, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint needed, IntPtr devInfo);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint needed, ref SP_DEVINFO_DATA devInfo);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA devInfo, ref DEVPROPKEY key, out uint type, out Guid buffer, uint size, out uint required, uint flags);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeHandle device, uint code, ref KSPROPERTY input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
