using System.Runtime.InteropServices;

namespace Glint.Services;

/// <summary>
/// Peak level of everything the default output plays, read on demand from the endpoint's own meter (the one the
/// Windows volume flyout draws). The character reads it on its dance frames; one read is a single COM call.
/// The meter is bound on the first read and released as soon as nobody dances, so an idle Glint holds nothing.
/// UI thread only.
/// </summary>
public sealed class AudioPeakMeter
{
    // Rebinding now and then follows a switch of the default output (speakers to headphones).
    static readonly TimeSpan Rebind = TimeSpan.FromSeconds(15);
    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(5);

    public static AudioPeakMeter Shared { get; } = new();

    AudioActivity.Interop.IAudioMeterInformation? _meter;
    DateTime _boundAt;
    DateTime _failedAt = DateTime.MinValue;

    /// <summary>0..1, or null when there is no output device to read (or the audio service is restarting).</summary>
    public float? Read()
    {
        var now = DateTime.UtcNow;
        if (_meter is not null && now - _boundAt > Rebind) Release();
        if (_meter is null)
        {
            if (now - _failedAt < RetryAfterFailure) return null;
            if (!Bind())
            {
                _failedAt = now;
                return null;
            }
        }
        try
        {
            _meter!.GetPeakValue(out var peak);
            return float.IsFinite(peak) ? Math.Clamp(peak, 0f, 1f) : 0f;
        }
        catch (Exception)
        {
            // The device went away; try again in a moment.
            Release();
            _failedAt = now;
            return null;
        }
    }

    bool Bind()
    {
        VolumeService.Interop.IMMDeviceEnumerator? enumerator = null;
        VolumeService.Interop.IMMDevice? device = null;
        try
        {
            var type = Type.GetTypeFromCLSID(VolumeService.Interop.MMDeviceEnumeratorClsid, throwOnError: true)!;
            enumerator = (VolumeService.Interop.IMMDeviceEnumerator)Activator.CreateInstance(type)!;
            enumerator.GetDefaultAudioEndpoint(VolumeService.Interop.EDataFlow.eRender, VolumeService.Interop.ERole.eMultimedia, out device);
            var iid = typeof(AudioActivity.Interop.IAudioMeterInformation).GUID;
            device.Activate(ref iid, VolumeService.Interop.CLSCTX_ALL, IntPtr.Zero, out var instance);
            _meter = (AudioActivity.Interop.IAudioMeterInformation)instance;
            _boundAt = DateTime.UtcNow;
            return true;
        }
        catch (Exception)
        {
            // E_NOTFOUND: no playback device right now.
            _meter = null;
            return false;
        }
        finally
        {
            // The meter keeps its own reference to the endpoint.
            if (device is not null) ReleaseCom(device);
            if (enumerator is not null) ReleaseCom(enumerator);
        }
    }

    /// <summary>Lets go of the endpoint; the next <see cref="Read"/> binds again.</summary>
    public void Release()
    {
        var meter = _meter;
        _meter = null;
        if (meter is not null) ReleaseCom(meter);
    }

    static void ReleaseCom(object comObject)
    {
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch (Exception)
        {
        }
    }
}
