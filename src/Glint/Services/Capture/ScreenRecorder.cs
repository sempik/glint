using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Glint.Services.Capture;

public sealed record RecordingOptions(int FrameRate, bool SystemAudio, bool Cursor);

/// <summary>
/// Records one monitor (or a rectangle of it) to an MP4 file: Windows.Graphics.Capture hands over frames as
/// GPU textures, they are copied (cropped) on the GPU and fed through a MediaStreamSource into MediaTranscoder,
/// which encodes H.264 on the graphics card's encoder when there is one. Optional system sound comes from WASAPI
/// loopback and is encoded as AAC. Nothing passes through the CPU but the sound.
/// Frames arrive only when the screen changes; a still screen repeats the last frame a few times a second.
/// </summary>
public sealed class ScreenRecorder : IAsyncDisposable
{
    // H.264 hardware encoders top out around 4096 x 2304; bigger areas are scaled down by the transcoder.
    const int MaxEncodeWidth = 4096, MaxEncodeHeight = 2304;
    static readonly TimeSpan RepeatAfter = TimeSpan.FromMilliseconds(250);

    readonly object _gate = new();
    readonly AutoResetEvent _frameReady = new(false);
    readonly D3D11Interop.Device _device;
    readonly GraphicsCaptureItem _item;
    readonly D3D11Interop.D3D11_BOX _box;
    readonly int _width, _height;
    readonly long _frameInterval;
    readonly LoopbackAudio? _audio;
    readonly MediaStreamSource _source;
    readonly VideoStreamDescriptor _videoStream;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    IRandomAccessStream? _output;
    Task? _transcode;
    long _start;
    long _lastAccepted;
    long _lastVideoTimestamp = -1;
    long _audioFrames;
    IDirect3DSurface? _latest;
    long _latestTicks;
    bool _latestFresh;
    volatile bool _stopping;
    int _faultRaised;
    readonly object _faultGate = new();
    Action<Exception?>? _faulted;
    // A fault raised before ScreenTools subscribed, kept for the subscriber.
    bool _faultPending;
    Exception? _pendingFault;

    /// <summary>The MP4 being written.</summary>
    public string Path { get; }
    public DateTime StartedAt { get; private set; }
    public bool HasAudio => _audio is not null;
    /// <summary>Encoded size in pixels (after any scaling).</summary>
    public (int Width, int Height) OutputSize { get; }

    /// <summary>
    /// Raised once, on any thread, when the recording can't go on by itself: the monitor went away, the disk
    /// is full, the encoder failed. Null means a clean end (monitor closed). Call <see cref="StopAsync"/> then.
    /// A fault from before anyone subscribed (capture starts inside <see cref="StartAsync"/>) is delivered on subscribing.
    /// </summary>
    public event Action<Exception?>? Faulted
    {
        add
        {
            bool replay;
            Exception? error;
            lock (_faultGate)
            {
                _faulted += value;
                replay = _faultPending;
                error = _pendingFault;
                _faultPending = false;
                _pendingFault = null;
            }
            if (replay) Invoke(value, error);
        }
        remove
        {
            lock (_faultGate) _faulted -= value;
        }
    }

    ScreenRecorder(IntPtr monitor, Int32Rect? crop, string path, RecordingOptions options)
    {
        Path = path;
        _frameInterval = TimeSpan.TicksPerSecond / Math.Max(1, options.FrameRate);
        _device = new D3D11Interop.Device();
        try
        {
            _item = D3D11Interop.CreateItemForMonitor(monitor);
            var size = _item.Size;
            var area = crop ?? new Int32Rect(0, 0, size.Width, size.Height);
            var x = Math.Clamp(area.X, 0, size.Width - 2);
            var y = Math.Clamp(area.Y, 0, size.Height - 2);
            // NV12, which the encoder works in, needs even sizes.
            _width = Math.Max(2, Math.Min(area.Width, size.Width - x)) & ~1;
            _height = Math.Max(2, Math.Min(area.Height, size.Height - y)) & ~1;
            _box = new D3D11Interop.D3D11_BOX { Left = (uint)x, Top = (uint)y, Front = 0, Right = (uint)(x + _width), Bottom = (uint)(y + _height), Back = 1 };

            var scale = Math.Min(1.0, Math.Min((double)MaxEncodeWidth / _width, (double)MaxEncodeHeight / _height));
            OutputSize = ((int)(_width * scale) & ~1, (int)(_height * scale) & ~1);

            if (options.SystemAudio)
            {
                try
                {
                    _audio = new LoopbackAudio();
                }
                catch (Exception ex)
                {
                    // No speakers or an odd mix format: record the picture anyway.
                    App.LogError(ex);
                    _audio = null;
                }
            }

            var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_width, (uint)_height);
            input.FrameRate.Numerator = (uint)options.FrameRate;
            input.FrameRate.Denominator = 1;
            _videoStream = new VideoStreamDescriptor(input);
            _source = _audio is null
                ? new MediaStreamSource(_videoStream)
                : new MediaStreamSource(_videoStream, new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm((uint)_audio.SampleRate, LoopbackAudio.Channels, 16)));
            // Live source: hand samples over as soon as they exist.
            _source.BufferTime = TimeSpan.Zero;
            _source.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
            _source.SampleRequested += OnSampleRequested;

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device.Direct3D, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = options.Cursor;
            try
            {
                D3D11Interop.TryHideCaptureBorder(_session);
            }
            catch (Exception)
            {
                // Only cosmetic: Windows keeps its yellow frame around the screen.
            }
            _pool.FrameArrived += OnFrameArrived;
            _item.Closed += (_, _) => RaiseFault(null);
        }
        catch
        {
            _session?.Dispose();
            _pool?.Dispose();
            _audio?.Dispose();
            _device.Dispose();
            throw;
        }
    }

    /// <summary>Starts recording the monitor; <paramref name="crop"/> is in the monitor's own pixels.</summary>
    public static async Task<ScreenRecorder> StartAsync(IntPtr monitor, Int32Rect? crop, string path, RecordingOptions options)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows.Graphics.Capture is not available.");
        var recorder = new ScreenRecorder(monitor, crop, path, options);
        try
        {
            await recorder.BeginAsync(options);
            return recorder;
        }
        catch
        {
            await recorder.DisposeAsync();
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // A locked half-file is not worth another error.
            }
            throw;
        }
    }

    async Task BeginAsync(RecordingOptions options)
    {
        var profile = new MediaEncodingProfile
        {
            Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Mpeg4 },
            Video = new VideoEncodingProperties
            {
                Subtype = MediaEncodingSubtypes.H264,
                Width = (uint)OutputSize.Width,
                Height = (uint)OutputSize.Height,
                // Screen content needs more bits than camera video for crisp text: ~7.5 Mbit/s at 1080p30.
                Bitrate = (uint)Math.Clamp(OutputSize.Width * (double)OutputSize.Height * options.FrameRate * 0.12, 3_000_000, 40_000_000),
            },
            Audio = _audio is null ? null : AudioEncodingProperties.CreateAac((uint)_audio.SampleRate, LoopbackAudio.Channels, 192_000),
        };
        profile.Video.FrameRate.Numerator = (uint)options.FrameRate;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _output = await FileRandomAccessStream.OpenAsync(Path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateNew);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(_source, _output, profile);
        if (!prepared.CanTranscode) throw new InvalidOperationException("Transcode not possible: " + prepared.FailureReason);

        // Both clocks count from here: capture frames carry QPC time, and so do loopback packets.
        _start = LoopbackAudio.NowTicks();
        _audio?.Start(_start);
        _session!.StartCapture();
        StartedAt = DateTime.Now;
        _transcode = prepared.TranscodeAsync().AsTask();
        _ = _transcode.ContinueWith(t =>
        {
            if (t.IsFaulted && !_stopping) RaiseFault(t.Exception?.GetBaseException());
        }, TaskScheduler.Default);
    }

    void RaiseFault(Exception? error)
    {
        if (_stopping || Interlocked.Exchange(ref _faultRaised, 1) != 0) return;
        Action<Exception?>? handler;
        lock (_faultGate)
        {
            handler = _faulted;
            if (handler is null)
            {
                _faultPending = true;
                _pendingFault = error;
                return;
            }
        }
        Invoke(handler, error);
    }

    static void Invoke(Action<Exception?>? handler, Exception? error)
    {
        try
        {
            handler?.Invoke(error);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ───────────── Frames ─────────────

    /// <summary>Capture thread: copies the wanted part of each frame into a texture of its own, at most at the frame rate.</summary>
    void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null || _stopping) return;
            var ticks = frame.SystemRelativeTime.Ticks;
            if (ticks < _start) return;
            // A 144 Hz screen delivers 144 frames a second; keep about the chosen rate.
            if (_lastAccepted != 0 && ticks - _lastAccepted < _frameInterval * 3 / 4) return;
            _lastAccepted = ticks;

            var source = D3D11Interop.GetTexture(frame.Surface);
            IntPtr copy;
            try
            {
                // The monitor's resolution may have changed under a running recording; never read outside the frame.
                var desc = D3D11Interop.GetDesc(source);
                var box = _box;
                box.Right = Math.Min(box.Right, desc.Width);
                box.Bottom = Math.Min(box.Bottom, desc.Height);
                if (box.Right <= box.Left || box.Bottom <= box.Top) return;
                copy = _device.CreateTexture(_width, _height);
                _device.CopyRegion(copy, source, box);
            }
            finally
            {
                Marshal.Release(source);
            }

            IDirect3DSurface surface;
            try
            {
                surface = D3D11Interop.CreateSurface(copy);
            }
            finally
            {
                // The surface holds its own reference; the encoder will take another for as long as it needs.
                Marshal.Release(copy);
            }

            IDirect3DSurface? old;
            lock (_gate)
            {
                old = _latest;
                _latest = surface;
                _latestTicks = ticks;
                _latestFresh = true;
            }
            (old as IDisposable)?.Dispose();
            _frameReady.Set();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            RaiseFault(ex);
        }
    }

    void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();
        var video = ReferenceEquals(request.StreamDescriptor, _videoStream) || request.StreamDescriptor is VideoStreamDescriptor;
        // Waiting for the next frame or enough sound must not block the media pipeline's own thread.
        Task.Run(() =>
        {
            try
            {
                request.Sample = video ? NextVideoSample() : NextAudioSample();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                request.Sample = null;
                RaiseFault(ex);
            }
            finally
            {
                deferral.Complete();
            }
        });
    }

    /// <summary>The newest frame, or the last one again when the screen stood still; null ends the stream.</summary>
    MediaStreamSample? NextVideoSample()
    {
        var deadline = DateTime.UtcNow + RepeatAfter;
        while (!_stopping)
        {
            lock (_gate)
            {
                var repeat = !_latestFresh && DateTime.UtcNow >= deadline;
                if (_latest is not null && (_latestFresh || repeat))
                {
                    var timestamp = _latestFresh ? _latestTicks - _start : LoopbackAudio.NowTicks() - _start;
                    // Timestamps must keep growing, also across a repeated frame.
                    if (timestamp <= _lastVideoTimestamp) timestamp = _lastVideoTimestamp + TimeSpan.TicksPerMillisecond;
                    _lastVideoTimestamp = timestamp;
                    _latestFresh = false;
                    var sample = MediaStreamSample.CreateFromDirect3D11Surface(_latest, TimeSpan.FromTicks(timestamp));
                    sample.Duration = TimeSpan.FromTicks(_frameInterval);
                    return sample;
                }
            }
            _frameReady.WaitOne(TimeSpan.FromMilliseconds(20));
        }
        return null;
    }

    /// <summary>The next 40 ms or more of sound; null ends the stream.</summary>
    MediaStreamSample? NextAudioSample()
    {
        if (_audio is null) return null;
        var minBytes = _audio.SampleRate / 25 * LoopbackAudio.Channels * 2;
        while (true)
        {
            var pcm = _audio.Take(minBytes, TimeSpan.FromMilliseconds(400));
            if (pcm is null) return null;
            if (pcm.Length == 0) continue;
            var frames = pcm.Length / (LoopbackAudio.Channels * 2);
            var timestamp = TimeSpan.FromTicks(_audioFrames * TimeSpan.TicksPerSecond / _audio.SampleRate);
            _audioFrames += frames;
            var sample = MediaStreamSample.CreateFromBuffer(pcm.AsBuffer(), timestamp);
            sample.Duration = TimeSpan.FromTicks((long)frames * TimeSpan.TicksPerSecond / _audio.SampleRate);
            return sample;
        }
    }

    // ───────────── Stop ─────────────

    /// <summary>Ends both streams and waits for the encoder to finish the file. Throws if the file couldn't be finished.</summary>
    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _frameReady.Set();
        _audio?.Stop();
        try
        {
            _session?.Dispose();
            _pool?.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        _session = null;
        _pool = null;
        try
        {
            if (_transcode is not null)
            {
                // Finishing an MP4 only writes its index; if the encoder hangs, don't hang with it.
                var done = await Task.WhenAny(_transcode, Task.Delay(TimeSpan.FromSeconds(15)));
                if (done != _transcode) throw new TimeoutException("The encoder did not finish the file.");
                await _transcode;
            }
        }
        finally
        {
            await DisposeAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        _stopping = true;
        _frameReady.Set();
        try
        {
            _session?.Dispose();
            _pool?.Dispose();
            _audio?.Dispose();
            _output?.Dispose();
            _output = null;
            IDirect3DSurface? latest;
            lock (_gate)
            {
                latest = _latest;
                _latest = null;
            }
            (latest as IDisposable)?.Dispose();
            _device.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return ValueTask.CompletedTask;
    }
}
