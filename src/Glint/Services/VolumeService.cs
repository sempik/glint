using System.Runtime.InteropServices;

namespace Glint.Services;

/// <summary>
/// Master volume of the default playback endpoint via CoreAudio.
/// Changed fires for external changes and default-device switches. Our own setter calls carry an event
/// context and are skipped, so a slider being dragged is never fed its own (already stale) values back.
/// </summary>
public sealed class VolumeService : Glint.Core.IVolumeService, IDisposable
{
    static readonly TimeSpan RebindCooldown = TimeSpan.FromSeconds(2);

    readonly object _gate = new();
    readonly Guid _context = Guid.NewGuid();
    readonly SynchronizationContext? _sync;
    // Strong references: native code holds only CCWs, the GC must not collect these while registered.
    readonly VolumeCallback _volumeCallback;
    readonly DeviceCallback _deviceCallback;
    Interop.IMMDeviceEnumerator? _enumerator;
    Interop.IMMDevice? _device;
    Interop.IAudioEndpointVolume? _endpoint;
    DateTime _lastBindAttempt;
    bool _disposed;

    public event EventHandler? Changed;

    public VolumeService()
    {
        _sync = SynchronizationContext.Current;
        _volumeCallback = new VolumeCallback(this);
        _deviceCallback = new DeviceCallback(this);
        try
        {
            var type = Type.GetTypeFromCLSID(Interop.MMDeviceEnumeratorClsid, throwOnError: true)!;
            _enumerator = (Interop.IMMDeviceEnumerator)Activator.CreateInstance(type)!;
            _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        Bind();
    }

    public float Volume
    {
        get => Use(ep =>
        {
            ep.GetMasterVolumeLevelScalar(out var level);
            return level;
        }, 0f);
        set
        {
            if (float.IsNaN(value)) return;
            var level = Math.Clamp(value, 0f, 1f);
            Use(ep =>
            {
                var context = _context;
                ep.SetMasterVolumeLevelScalar(level, ref context);
                return true;
            }, false);
        }
    }

    public bool Muted
    {
        get => Use(ep =>
        {
            ep.GetMute(out var muted);
            return muted;
        }, false);
        set => Use(ep =>
        {
            var context = _context;
            ep.SetMute(value, ref context);
            return true;
        }, false);
    }

    T Use<T>(Func<Interop.IAudioEndpointVolume, T> action, T fallback)
    {
        lock (_gate)
        {
            if (_disposed) return fallback;
            if (_endpoint is null && DateTime.UtcNow - _lastBindAttempt > RebindCooldown) Bind();
            if (_endpoint is null) return fallback;
            try
            {
                return action(_endpoint);
            }
            catch (Exception)
            {
                // Endpoint invalidated (device unplugged, driver reset): rebind once and retry.
                Bind();
                if (_endpoint is null) return fallback;
                try
                {
                    return action(_endpoint);
                }
                catch (Exception)
                {
                    Unbind();
                    return fallback;
                }
            }
        }
    }

    void Bind()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Unbind();
            _lastBindAttempt = DateTime.UtcNow;
            if (_enumerator is null) return;

            Interop.IMMDevice? device = null;
            Interop.IAudioEndpointVolume? endpoint = null;
            try
            {
                _enumerator.GetDefaultAudioEndpoint(Interop.EDataFlow.eRender, Interop.ERole.eMultimedia, out device);
                var iid = typeof(Interop.IAudioEndpointVolume).GUID;
                device.Activate(ref iid, Interop.CLSCTX_ALL, IntPtr.Zero, out var activated);
                endpoint = (Interop.IAudioEndpointVolume)activated;
                endpoint.RegisterControlChangeNotify(_volumeCallback);
                _device = device;
                _endpoint = endpoint;
            }
            catch (Exception ex)
            {
                // E_NOTFOUND simply means no playback device right now; stay unbound until one appears.
                if (ex is not COMException) App.LogError(ex);
                if (endpoint is not null) Release(endpoint);
                if (device is not null) Release(device);
            }
        }
    }

    void Unbind()
    {
        var endpoint = _endpoint;
        var device = _device;
        _endpoint = null;
        _device = null;
        if (endpoint is not null)
        {
            try
            {
                endpoint.UnregisterControlChangeNotify(_volumeCallback);
            }
            catch (Exception)
            {
                // Already invalidated; the registration died with it.
            }
            Release(endpoint);
        }
        if (device is not null) Release(device);
    }

    static void Release(object comObject)
    {
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch (Exception)
        {
        }
    }

    void ScheduleRebind()
    {
        if (_disposed) return;
        // CoreAudio must not be re-entered from its own notification thread, and keeping all binding on the
        // UI thread keeps the RCWs in one apartment.
        void Run(object? _)
        {
            Bind();
            RaiseChanged();
        }
        if (_sync is not null) _sync.Post(Run, null);
        else ThreadPool.QueueUserWorkItem(Run);
    }

    void RaiseChanged()
    {
        if (_disposed) return;
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Unbind();
            _disposed = true;
            if (_enumerator is not null)
            {
                try
                {
                    _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
                }
                catch (Exception)
                {
                }
                Release(_enumerator);
                _enumerator = null;
            }
        }
    }

    internal sealed class VolumeCallback : Interop.IAudioEndpointVolumeCallback
    {
        readonly VolumeService _owner;
        public VolumeCallback(VolumeService owner) => _owner = owner;

        public int OnNotify(IntPtr notifyData)
        {
            try
            {
                // AUDIO_VOLUME_NOTIFICATION_DATA starts with guidEventContext.
                if (notifyData != IntPtr.Zero && Marshal.PtrToStructure<Guid>(notifyData) == _owner._context) return 0;
                _owner.RaiseChanged();
            }
            catch (Exception)
            {
                // Never let an exception cross back into the audio service.
            }
            return 0;
        }
    }

    internal sealed class DeviceCallback : Interop.IMMNotificationClient
    {
        readonly VolumeService _owner;
        public DeviceCallback(VolumeService owner) => _owner = owner;

        public int OnDeviceStateChanged(string deviceId, uint newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(Interop.EDataFlow flow, Interop.ERole role, string? defaultDeviceId)
        {
            try
            {
                if (flow == Interop.EDataFlow.eRender && role == Interop.ERole.eMultimedia) _owner.ScheduleRebind();
            }
            catch (Exception)
            {
            }
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, Interop.PropertyKey key) => 0;
    }

    internal static class Interop
    {
        public static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public const int CLSCTX_ALL = 0x17;

        public enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
        public enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey
        {
            public Guid FormatId;
            public int PropertyId;
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
            void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            void RegisterEndpointNotificationCallback(IMMNotificationClient client);
            void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            void OpenPropertyStore(int access, out IntPtr properties);
            void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            void GetState(out uint state);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioEndpointVolume
        {
            void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
            void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
            void GetChannelCount(out uint channelCount);
            void SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
            void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            void GetMasterVolumeLevel(out float levelDb);
            void GetMasterVolumeLevelScalar(out float level);
            void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
            void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
            void GetChannelVolumeLevel(uint channel, out float levelDb);
            void GetChannelVolumeLevelScalar(uint channel, out float level);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            void GetVolumeStepInfo(out uint step, out uint stepCount);
            void VolumeStepUp(ref Guid eventContext);
            void VolumeStepDown(ref Guid eventContext);
            void QueryHardwareSupport(out uint hardwareSupportMask);
            void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
        }

        [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioEndpointVolumeCallback
        {
            [PreserveSig] int OnNotify(IntPtr notifyData);
        }

        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMNotificationClient
        {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
        }
    }
}
