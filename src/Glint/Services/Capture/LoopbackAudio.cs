using System.Diagnostics;
using System.Runtime.InteropServices;
using static Glint.Services.VolumeService.Interop;

namespace Glint.Services.Capture;

/// <summary>
/// What the default speakers play, captured with WASAPI loopback and turned into 16-bit stereo PCM on a
/// continuous timeline that starts at a given moment. Windows sends no loopback data at all while nothing
/// plays, so gaps are filled with silence by the clock; that keeps the sound in step with the video.
/// </summary>
internal sealed class LoopbackAudio : IDisposable
{
    const int AUDCLNT_SHAREMODE_SHARED = 0;
    const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    const uint AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR = 0x4;
    const ushort WAVE_FORMAT_PCM = 1, WAVE_FORMAT_IEEE_FLOAT = 3, WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    readonly object _lock = new();
    readonly Queue<byte[]> _chunks = new();
    long _startTicks;
    readonly int _rate;
    readonly int _channels;
    readonly int _bits;
    readonly bool _float;
    readonly IAudioClient _client;
    readonly IAudioCaptureClient _capture;
    readonly Thread _thread;
    readonly AutoResetEvent _dataReady = new(false);
    volatile bool _stopping;
    volatile bool _deviceLost;
    bool _disposed;
    // Frames placed on the timeline so far (captured or silence), and frames handed out.
    long _written;
    long _queuedBytes;

    /// <summary>Sample rate of the PCM handed out (the mix rate, or 48 kHz if the AAC encoder can't take the mix rate).</summary>
    public int SampleRate { get; }
    public const int Channels = 2;
    const int BytesPerFrame = Channels * 2;

    public LoopbackAudio()
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid)!)!;
        enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var device);
        var iid = IID_IAudioClient;
        device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var clientObj);
        _client = (IAudioClient)clientObj;

        _client.GetMixFormat(out var format);
        try
        {
            var tag = (ushort)Marshal.ReadInt16(format, 0);
            _channels = Marshal.ReadInt16(format, 2);
            _rate = Marshal.ReadInt32(format, 4);
            _bits = Marshal.ReadInt16(format, 14);
            if (tag == WAVE_FORMAT_EXTENSIBLE)
            {
                // WAVEFORMATEXTENSIBLE: the sub-format GUID starts at byte 24 and repeats the format tag in its first bytes.
                var sub = Marshal.PtrToStructure<Guid>(format + 24);
                tag = (ushort)(sub.ToByteArray()[0] | (sub.ToByteArray()[1] << 8));
            }
            _float = tag == WAVE_FORMAT_IEEE_FLOAT;
            if (!_float && tag != WAVE_FORMAT_PCM) throw new NotSupportedException($"Mix format {tag}");
            if (_float && _bits != 32) throw new NotSupportedException($"Float {_bits} bit");
            if (!_float && _bits is not (16 or 24 or 32)) throw new NotSupportedException($"PCM {_bits} bit");
            if (_channels < 1) throw new NotSupportedException("No channels");

            // 200 ms of buffer is plenty for a thread that drains it every 10 ms.
            _client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 2_000_000, 0, format, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
        }
        // The AAC encoder takes 44.1 and 48 kHz only.
        SampleRate = _rate is 44100 or 48000 ? _rate : 48000;

        var captureIid = IID_IAudioCaptureClient;
        _client.GetService(ref captureIid, out var captureObj);
        _capture = (IAudioCaptureClient)captureObj;
        _thread = new Thread(Run) { IsBackground = true, Name = "Glint loopback audio", Priority = ThreadPriority.AboveNormal };
    }

    /// <param name="startTicks">QPC time in 100 ns units (the clock capture frames use) where the timeline begins.</param>
    public void Start(long startTicks)
    {
        _startTicks = startTicks;
        try
        {
            _client.Start();
        }
        catch (Exception ex)
        {
            // The stream is already part of the video file; keep it alive with silence.
            App.LogError(ex);
            _deviceLost = true;
        }
        _thread.Start();
    }

    /// <summary>Waits until at least <paramref name="minBytes"/> are ready (or the timeout passes), then takes everything queued.</summary>
    public byte[]? Take(int minBytes, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_lock)
            {
                if (_queuedBytes >= minBytes || (_stopping && _queuedBytes > 0) || DateTime.UtcNow >= deadline)
                {
                    if (_queuedBytes == 0) return _stopping ? null : Array.Empty<byte>();
                    var result = new byte[_queuedBytes];
                    var offset = 0;
                    while (_chunks.Count > 0)
                    {
                        var c = _chunks.Dequeue();
                        Buffer.BlockCopy(c, 0, result, offset, c.Length);
                        offset += c.Length;
                    }
                    _queuedBytes = 0;
                    return result;
                }
                if (_stopping) return null;
            }
            _dataReady.WaitOne(TimeSpan.FromMilliseconds(20));
        }
    }

    public static long NowTicks() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    void Run()
    {
        try
        {
            while (!_stopping && !_deviceLost)
            {
                Thread.Sleep(10);
                Drain();
                PadSilenceToClock();
            }
        }
        catch (Exception ex)
        {
            // The device went away (unplugged headphones, a new default device); the rest of the recording gets silence.
            App.LogError(ex);
        }
        while (!_stopping)
        {
            Thread.Sleep(20);
            PadSilenceToClock();
        }
    }

    void Drain()
    {
        while (true)
        {
            _capture.GetNextPacketSize(out var packet);
            if (packet == 0) return;
            var hr = _capture.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (hr != 0 || frames == 0)
            {
                // AUDCLNT_S_BUFFER_EMPTY
                if (frames > 0) _capture.ReleaseBuffer(frames);
                return;
            }
            try
            {
                var pcm = Convert(data, (int)frames, (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
                Place(pcm, (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0 ? null : (long)qpc);
            }
            finally
            {
                _capture.ReleaseBuffer(frames);
            }
        }
    }

    /// <summary>Puts a captured packet on the timeline where its timestamp says it belongs; without a usable one, right after the last.</summary>
    void Place(byte[] pcm, long? qpcTicks)
    {
        var frames = pcm.Length / BytesPerFrame;
        var skip = 0L;
        lock (_lock)
        {
            var at = qpcTicks is long qpc ? (long)((qpc - _startTicks) * (SampleRate / 10_000_000.0)) : _written;
            // The silence padding keeps the timeline within a fraction of a second of the clock, so a jump of more
            // than a second is a broken timestamp; trusting it would drop all sound or fill the track with silence.
            if (Math.Abs(at - _written) > SampleRate) at = _written;
            // Small jitter is normal; only correct for real gaps (silence while nothing played) and overlaps.
            var tolerance = SampleRate / 50;
            if (at > _written + tolerance) Enqueue(new byte[(at - _written) * BytesPerFrame], at - _written);
            else if (at < _written - tolerance) skip = _written - at;
            if (skip >= frames) return;
            var take = frames - (int)skip;
            if (skip == 0)
            {
                Enqueue(pcm, take);
            }
            else
            {
                var part = new byte[take * BytesPerFrame];
                Buffer.BlockCopy(pcm, (int)skip * BytesPerFrame, part, 0, part.Length);
                Enqueue(part, take);
            }
        }
        _dataReady.Set();
    }

    /// <summary>While nothing plays no packets arrive; keep the timeline moving with silence, a little behind the clock.</summary>
    void PadSilenceToClock()
    {
        var due = (long)((NowTicks() - _startTicks) * (SampleRate / 10_000_000.0)) - SampleRate / 10;
        lock (_lock)
        {
            if (due <= _written) return;
            Enqueue(new byte[(due - _written) * BytesPerFrame], due - _written);
        }
        _dataReady.Set();
    }

    void Enqueue(byte[] pcm, long frames)
    {
        // Packets from before the start belong to nobody.
        if (frames <= 0) return;
        _chunks.Enqueue(pcm);
        _queuedBytes += pcm.Length;
        _written += frames;
    }

    /// <summary>Mix format (float or integer, any channel count and rate) to 16-bit stereo at <see cref="SampleRate"/>.</summary>
    byte[] Convert(IntPtr data, int frames, bool silent)
    {
        var outFrames = _rate == SampleRate ? frames : (int)((long)frames * SampleRate / _rate);
        var result = new byte[outFrames * BytesPerFrame];
        if (silent || outFrames == 0) return result;

        var bytesPerSample = _bits / 8;
        var raw = new byte[frames * _channels * bytesPerSample];
        Marshal.Copy(data, raw, 0, raw.Length);
        for (var i = 0; i < outFrames; i++)
        {
            // Nearest source frame is enough for the rare 96/192 kHz mix formats.
            var src = _rate == SampleRate ? i : (int)((long)i * _rate / SampleRate);
            for (var ch = 0; ch < Channels; ch++)
            {
                // Mono is duplicated; 5.1 and up keep front left and right.
                var c = Math.Min(ch, _channels - 1);
                var offset = (src * _channels + c) * bytesPerSample;
                float v = _float
                    ? BitConverter.ToSingle(raw, offset)
                    : _bits switch
                    {
                        16 => BitConverter.ToInt16(raw, offset) / 32768f,
                        24 => ((raw[offset] << 8 | raw[offset + 1] << 16 | raw[offset + 2] << 24) >> 8) / 8388608f,
                        _ => BitConverter.ToInt32(raw, offset) / 2147483648f,
                    };
                var s = (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);
                var o = (i * Channels + ch) * 2;
                result[o] = (byte)s;
                result[o + 1] = (byte)(s >> 8);
            }
        }
        return result;
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _dataReady.Set();
        try
        {
            if (_thread.IsAlive) _thread.Join(500);
            _client.Stop();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try
        {
            Marshal.ReleaseComObject(_capture);
            Marshal.ReleaseComObject(_client);
        }
        catch (Exception)
        {
            // Already gone with the device.
        }
        _dataReady.Dispose();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }
}
