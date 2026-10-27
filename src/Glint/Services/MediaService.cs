using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glint.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Glint.Services;

/// <summary>
/// Follows the system "current" media session (GSMTC).
/// <see cref="Current"/> (same as <see cref="GetSnapshot"/>) extrapolates Position on every read while playing,
/// so the UI can poll it on a timer for a smooth progress bar; players only push timeline updates on seek/state changes.
/// </summary>
public sealed class MediaService : IMediaService
{
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);
    static readonly TimeSpan StaleTimeline = TimeSpan.FromSeconds(1);
    const int MaxThumbnailBytes = 8 * 1024 * 1024;
    const int ThumbnailDecodeWidth = 160;

    static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Edge",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["308046B0AF4A39CB"] = "Firefox",
        ["opera"] = "Opera",
        ["brave"] = "Brave",
        ["spotify"] = "Spotify",
        ["Microsoft.ZuneMusic"] = "Медиаплеер",
        ["Microsoft.ZuneVideo"] = "Кино и ТВ",
        ["telegram"] = "Telegram",
        ["discord"] = "Discord",
        ["vlc"] = "VLC",
        ["aimp"] = "AIMP",
    };

    sealed record Snapshot(MediaInfo Info, DateTimeOffset Anchor, double Rate);

    readonly object _gate = new();
    readonly SemaphoreSlim _refreshLock = new(1, 1);
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    volatile Snapshot? _snapshot;
    int _generation;

    // Touched only inside _refreshLock.
    volatile bool _thumbDirty = true;
    string? _thumbKey;
    ImageSource? _thumb;
    DateTimeOffset _resumeFor, _resumeAnchor;

    public event EventHandler? Changed;

    public MediaInfo? Current => GetSnapshot();

    /// <summary>Latest media state with Position advanced to "now" when playing. Safe from any thread.</summary>
    public MediaInfo? GetSnapshot()
    {
        var s = _snapshot;
        if (s is null) return null;
        var info = s.Info;
        if (!info.IsPlaying || info.Duration <= TimeSpan.Zero) return info;

        var elapsed = DateTimeOffset.Now - s.Anchor;
        if (elapsed <= TimeSpan.Zero) return info;
        var position = info.Position + TimeSpan.FromTicks((long)(elapsed.Ticks * s.Rate));
        if (position > info.Duration) position = info.Duration;
        return info with { Position = position };
    }

    public async Task InitializeAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        lock (_gate) _manager = manager;
        manager.CurrentSessionChanged += OnCurrentSessionChanged;
        manager.SessionsChanged += OnSessionsChanged;
        BindSession();
        // Off the UI thread: thumbnail reads and property queries can take a while for some players.
        await Task.Run(() => RefreshAsync());
    }

    public Task PlayPauseAsync() => SendAsync(s => s.TryTogglePlayPauseAsync());
    public Task NextAsync() => SendAsync(s => s.TrySkipNextAsync());
    public Task PreviousAsync() => SendAsync(s => s.TrySkipPreviousAsync());

    async Task SendAsync(Func<GlobalSystemMediaTransportControlsSession, global::Windows.Foundation.IAsyncOperation<bool>> command)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate) session = _session;
        if (session is null) return;
        try
        {
            await command(session);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        ScheduleRefresh();
    }

    // ───────────── Session binding ─────────────

    void BindSession()
    {
        try
        {
            lock (_gate)
            {
                var next = _manager?.GetCurrentSession();
                if (_session is not null) Unhook(_session);
                _session = next;
                if (next is not null) Hook(next);
                _thumbDirty = true;
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void Hook(GlobalSystemMediaTransportControlsSession session)
    {
        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
    }

    void Unhook(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch (Exception)
        {
            // The player may already be gone; nothing left to unsubscribe from.
        }
    }

    void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        BindSession();
        ScheduleRefresh();
    }

    void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        // CurrentSessionChanged is not always raised when the last session closes.
        BindSession();
        ScheduleRefresh();
    }

    void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        // Many players publish the title first and the artwork a moment later under the same title.
        _thumbDirty = true;
        ScheduleRefresh();
    }

    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => ScheduleRefresh();

    void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => ScheduleRefresh();

    // ───────────── Refresh ─────────────

    void ScheduleRefresh()
    {
        // Trailing debounce: players fire bursts of property/playback/timeline events on every track change.
        var generation = Interlocked.Increment(ref _generation);
        _ = Task.Run(async () =>
        {
            await Task.Delay(Debounce).ConfigureAwait(false);
            if (Volatile.Read(ref _generation) != generation) return;
            await RefreshAsync().ConfigureAwait(false);
        });
    }

    async Task RefreshAsync()
    {
        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            GlobalSystemMediaTransportControlsSession? session;
            lock (_gate) session = _session;
            _snapshot = session is null ? null : await BuildAsync(session).ConfigureAwait(false);
        }
        catch (COMException)
        {
            // The session died underneath us (player closed); the manager events will rebind.
            _snapshot = null;
        }
        catch (Exception ex)
        {
            _snapshot = null;
            App.LogError(ex);
        }
        finally
        {
            _refreshLock.Release();
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    async Task<Snapshot?> BuildAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var playback = session.GetPlaybackInfo();
        var status = playback?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed) return null;

        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
        }
        catch (COMException)
        {
            // Some players reject the query mid track change; show what we have and wait for the next event.
        }

        var app = FriendlyAppName(session.SourceAppUserModelId);
        var title = props?.Title ?? string.Empty;
        var artist = props?.Artist ?? string.Empty;
        if (string.IsNullOrWhiteSpace(artist)) artist = props?.AlbumArtist ?? string.Empty;
        var playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var rate = playback?.PlaybackRate ?? 1.0;
        if (rate <= 0 || double.IsNaN(rate)) rate = 1.0;

        var key = $"{app}\n{title}\n{artist}";
        var sameTrack = key == _thumbKey;
        var now = DateTimeOffset.Now;
        var position = TimeSpan.Zero;
        var duration = TimeSpan.Zero;
        var anchor = now;

        var timeline = session.GetTimelineProperties();
        if (timeline is not null)
        {
            duration = timeline.EndTime - timeline.StartTime;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            position = timeline.Position - timeline.StartTime;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (duration > TimeSpan.Zero && position > duration) position = duration;

            var updated = timeline.LastUpdatedTime;
            if (updated.Year >= 2000 && updated <= now) anchor = updated;

            // Players often don't touch the timeline on resume, leaving LastUpdatedTime at the pause moment.
            // Extrapolating from there would skip ahead by the whole pause, so restart the clock at the resume.
            if (!sameTrack) _resumeAnchor = default;
            var previous = _snapshot;
            if (playing && sameTrack && previous is { Info.IsPlaying: false } && now - anchor > StaleTimeline)
            {
                _resumeFor = updated;
                _resumeAnchor = now;
            }
            if (playing && _resumeAnchor != default && updated == _resumeFor) anchor = _resumeAnchor;
        }

        if (!sameTrack || _thumbDirty)
        {
            _thumbDirty = false;
            var fresh = await LoadThumbnailAsync(props?.Thumbnail).ConfigureAwait(false);
            // Keep the old artwork if a re-read of the same track came back empty.
            if (fresh is not null || !sameTrack) _thumb = fresh;
            _thumbKey = key;
        }

        var info = new MediaInfo(title, artist, app, playing, position, duration, _thumb);
        return new Snapshot(info, anchor, rate);
    }

    static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            byte[] bytes;
            using (var stream = await reference.OpenReadAsync())
            {
                var size = stream.Size;
                if (size == 0 || size > MaxThumbnailBytes) return null;

                using var buffer = new MemoryStream((int)size);
                using (var reader = new DataReader(stream))
                {
                    ulong total = 0;
                    while (total < size)
                    {
                        var loaded = await reader.LoadAsync((uint)(size - total));
                        if (loaded == 0) break;
                        var chunk = new byte[loaded];
                        reader.ReadBytes(chunk);
                        buffer.Write(chunk, 0, chunk.Length);
                        total += loaded;
                    }
                    reader.DetachStream();
                }
                bytes = buffer.ToArray();
            }
            if (bytes.Length == 0) return null;

            // Decode on the UI dispatcher: creating WPF imaging objects on pool threads spins up a hidden
            // Dispatcher (with a message window) per thread. The image is frozen, so any thread can use it.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return Decode(bytes);
            return await dispatcher.InvokeAsync(() => Decode(bytes));
        }
        catch (Exception)
        {
            // Broken or unsupported artwork just means no cover.
            return null;
        }
    }

    static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = ThumbnailDecodeWidth;
            image.StreamSource = new MemoryStream(bytes, writable: false);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"C:\...\Spotify.exe", "SpotifyAB.SpotifyMusic_x!Spotify", "MSEdge" -> "Spotify", "Spotify", "Edge".</summary>
    static string FriendlyAppName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return string.Empty;
        var name = appId.Trim();

        var bang = name.LastIndexOf('!');
        if (bang >= 0 && bang < name.Length - 1) name = name[(bang + 1)..];
        var slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0 && slash < name.Length - 1) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (KnownApps.TryGetValue(name, out var known)) return known;

        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        if (KnownApps.TryGetValue(name, out known)) return known;

        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
