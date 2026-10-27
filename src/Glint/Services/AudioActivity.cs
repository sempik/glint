using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glint.Services;

/// <summary>
/// Notices apps that are producing sound on the default output, via per-session peak meters.
/// Covers players that never register with the system media controls (many Electron and Web Audio apps),
/// so the pill can still show that something is playing.
/// </summary>
public sealed class AudioActivity : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    // Music has quiet passages and gaps between tracks; don't flicker the indicator on every one.
    static readonly TimeSpan Hold = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Rebind = TimeSpan.FromSeconds(10);
    const float Threshold = 0.003f;

    public static AudioActivity Shared { get; } = new();

    readonly object _gate = new();
    readonly int _ownPid = Environment.ProcessId;
    System.Threading.Timer? _timer;
    Interop.IAudioSessionManager2? _manager;
    DateTime _boundAt;
    DateTime _lastHeard;
    string? _app;
    bool _disposed;
    int _polling;
    uint _namePid;
    string? _name;

    /// <summary>Raised on a pool thread when <see cref="AudibleApp"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Process name of the app heard most recently, or null when nothing has played for a moment.</summary>
    public string? AudibleApp { get { lock (_gate) return _app; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        }
    }

    void Poll()
    {
        // A slow audio service can make one tick outlast the interval; never touch the COM objects from two at once.
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        string? heard = null;
        try
        {
            // A player the system media controls already report as playing makes the meters redundant.
            if (Glint.Core.AppServices.Media?.Current?.IsPlaying != true) heard = LoudestApp();
        }
        catch (Exception)
        {
            // The device went away or audio service restarted; rebind on the next tick.
            _manager = null;
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }

        string? app;
        bool changed;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (heard is not null) _lastHeard = now;
            app = heard ?? (now - _lastHeard < Hold ? _app : null);
            changed = app != _app;
            _app = app;
        }
        if (!changed) return;
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    string? LoudestApp()
    {
        // Rebinding now and then follows a switch of the default output (speakers to headphones).
        if (_manager is null || DateTime.UtcNow - _boundAt > Rebind)
        {
            _manager = null;
            var type = Type.GetTypeFromCLSID(VolumeService.Interop.MMDeviceEnumeratorClsid, throwOnError: true)!;
            var enumerator = (VolumeService.Interop.IMMDeviceEnumerator)Activator.CreateInstance(type)!;
            enumerator.GetDefaultAudioEndpoint(VolumeService.Interop.EDataFlow.eRender, VolumeService.Interop.ERole.eMultimedia, out var device);
            var iid = typeof(Interop.IAudioSessionManager2).GUID;
            device.Activate(ref iid, VolumeService.Interop.CLSCTX_ALL, IntPtr.Zero, out var instance);
            _manager = (Interop.IAudioSessionManager2)instance;
            _boundAt = DateTime.UtcNow;
        }

        _manager.GetSessionEnumerator(out var sessions);
        sessions.GetCount(out var count);
        uint bestPid = 0;
        var bestPeak = Threshold;
        for (var i = 0; i < count; i++)
        {
            sessions.GetSession(i, out var control);
            if (control is not Interop.IAudioSessionControl2 control2 || control is not Interop.IAudioMeterInformation meter) continue;
            control2.GetProcessId(out var pid);
            // pid 0 is the system sounds session; our own process never counts.
            if (pid == 0 || pid == _ownPid) continue;
            meter.GetPeakValue(out var peak);
            if (peak > bestPeak)
            {
                bestPeak = peak;
                bestPid = pid;
            }
        }
        if (bestPid == 0) return null;
        // Looking a process up is far costlier than the meters; the same player keeps winning tick after tick.
        if (bestPid == _namePid && _name is not null) return _name;
        try
        {
            using var process = Process.GetProcessById((int)bestPid);
            _name = process.ProcessName;
        }
        catch (Exception)
        {
            _name = "audio";
        }
        _namePid = bestPid;
        return _name;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    // Internal: AudioPeakMeter reads the same meter interface on the whole endpoint.
    internal static class Interop
    {
        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2
        {
            void GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
            void GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
            void GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
            void RegisterSessionNotification(IntPtr notification);
            void UnregisterSessionNotification(IntPtr notification);
            void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
            void UnregisterDuckNotification(IntPtr notification);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator
        {
            void GetCount(out int count);
            void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl2
        {
            // IAudioSessionControl
            void GetState(out int state);
            void GetDisplayName(out IntPtr name);
            void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
            void GetIconPath(out IntPtr path);
            void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
            void GetGroupingParam(out Guid grouping);
            void SetGroupingParam(ref Guid grouping, IntPtr eventContext);
            void RegisterAudioSessionNotification(IntPtr notification);
            void UnregisterAudioSessionNotification(IntPtr notification);
            // IAudioSessionControl2
            void GetSessionIdentifier(out IntPtr id);
            void GetSessionInstanceIdentifier(out IntPtr id);
            void GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioMeterInformation
        {
            void GetPeakValue(out float peak);
        }
    }
}
