using Glint.Core;

namespace Glint.Services.Indicators;

/// <summary>
/// Browser downloads: Chromium browsers write "*.crdownload", Firefox "*.part", Opera "*.opdownload", and the
/// file gets its real name when it's done. A FileSystemWatcher on the Downloads folder reports those renames;
/// sizes are sampled once a second only while something is downloading, and every 10 s while one is paused.
/// </summary>
public sealed class DownloadMonitor : IndicatorMonitor
{
    const string Id = "download";
    static readonly string[] TempExtensions = { ".crdownload", ".part", ".opdownload", ".partial", ".download", ".tmp" };
    // A download that hasn't grown for this long is paused or stalled: hide it, but keep checking it slowly,
    // because resuming only appends to the file and fires no event.
    static readonly TimeSpan Stale = TimeSpan.FromMinutes(2);
    static readonly TimeSpan PausedCheck = TimeSpan.FromSeconds(10);
    // Paused this long: abandoned, stop looking.
    static readonly TimeSpan Abandoned = TimeSpan.FromDays(1);

    sealed class Entry
    {
        public long Size;
        public long PreviousSize = -1;
        public DateTime LastGrowth = DateTime.UtcNow;
        public bool Grown;
        public bool Paused;
        public double Speed;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _active = new(StringComparer.OrdinalIgnoreCase);
    FileSystemWatcher? _watcher;
    Timer? _timer;
    TimeSpan? _period;
    string _folder = string.Empty;

    public DownloadMonitor(IndicatorHub hub) : base(hub) { }

    protected override void Start()
    {
        _folder = Glint.Pages.HomeNative.GetDownloadsFolder();
        if (!Directory.Exists(_folder)) return;
        _timer = new Timer(_ => Tick());
        _watcher = new FileSystemWatcher(_folder)
        {
            // Names only: size and write events would fire hundreds of times per download.
            NotifyFilter = NotifyFilters.FileName,
            IncludeSubdirectories = false,
        };
        _watcher.Created += (_, e) => Track(e.FullPath);
        _watcher.Renamed += OnRenamed;
        _watcher.Deleted += (_, e) => Forget(e.FullPath);
        _watcher.EnableRaisingEvents = true;

        // Downloads already running when Glint starts.
        try
        {
            foreach (var path in Directory.EnumerateFiles(_folder))
                if (IsTemp(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(1)) Track(path);
        }
        catch (Exception)
        {
            // An unreadable folder just means no initial list.
        }
    }

    protected override void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _timer?.Dispose();
        _timer = null;
        lock (_gate)
        {
            _active.Clear();
            _period = null;
        }
        Hub.Remove(Id);
    }

    static bool IsTemp(string path) =>
        TempExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    void Track(string path)
    {
        if (!IsTemp(path)) return;
        lock (_gate)
        {
            if (_active.ContainsKey(path)) return;
            _active[path] = new Entry();
            // Sample once a second while something downloads; the timer slows down or stops itself after.
            SetPeriod(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Caller holds the lock. Null stops the timer.</summary>
    void SetPeriod(TimeSpan? period)
    {
        if (period == _period) return;
        _period = period;
        if (period is TimeSpan p) _timer?.Change(p, p);
        else _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    void Forget(string path)
    {
        bool removed;
        lock (_gate) removed = _active.Remove(path);
        if (removed) Publish();
    }

    void OnRenamed(object sender, RenamedEventArgs e)
    {
        var wasTemp = IsTemp(e.OldFullPath);
        Entry? entry;
        lock (_gate)
        {
            _active.Remove(e.OldFullPath, out entry);
        }
        if (IsTemp(e.FullPath))
        {
            // "Unconfirmed 123.crdownload" -> "name.zip.crdownload": still the same download.
            Track(e.FullPath);
            return;
        }
        if (!wasTemp) return;
        // A real download ends with a rename to the final name. Skip ".tmp" files that never grew: those are
        // other apps' scratch files, not downloads. A browser's own temp file always is one, even untracked.
        if (entry is { Grown: true } || !e.OldFullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            var path = e.FullPath;
            var name = Path.GetFileName(path);
            AppServices.Notifier.FlashClickable("Загружено", name, Glyphs.Download, () => IndicatorNative.ShowInExplorer(path), TimeSpan.FromSeconds(5));
        }
        Publish();
    }

    void Tick()
    {
        if (!Running) return;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var (path, entry) in _active.ToList())
            {
                var size = IndicatorNative.TryGetLiveSize(path);
                if (size is null)
                {
                    _active.Remove(path);
                    continue;
                }
                var wasPaused = entry.Paused;
                if (entry.PreviousSize >= 0 && size > entry.PreviousSize)
                {
                    entry.Grown = true;
                    entry.LastGrowth = now;
                    entry.Paused = false;
                }
                // Smoothed bytes per second over the one-second ticks; a slow paused tick says nothing about speed.
                var delta = entry.PreviousSize < 0 || wasPaused ? 0 : Math.Max(0, size.Value - entry.PreviousSize);
                entry.Speed = entry.Speed * 0.6 + delta * 0.4;
                entry.PreviousSize = size.Value;
                entry.Size = size.Value;
                var quiet = now - entry.LastGrowth;
                // A ".tmp" that never grew is some app's scratch file, not a paused download.
                if (quiet > Abandoned || (quiet > Stale && !entry.Grown && path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
                    _active.Remove(path);
                else if (quiet > Stale)
                    entry.Paused = true;
                if (entry.Paused) entry.Speed = 0;
            }
            SetPeriod(_active.Count == 0 ? null : _active.Values.Any(e => !e.Paused) ? TimeSpan.FromSeconds(1) : PausedCheck);
        }
        Publish();
    }

    void Publish()
    {
        if (!Running) return;
        List<(string Path, Entry Entry)> shown;
        lock (_gate)
        {
            // A ".tmp" counts only once it actually grows like a download; paused ones wait off screen.
            shown = _active.Where(p => !p.Value.Paused && (p.Value.Grown || !p.Key.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
                .Select(p => (p.Key, p.Value)).ToList();
        }
        if (shown.Count == 0)
        {
            Hub.Remove(Id);
            return;
        }

        var total = shown.Sum(s => s.Entry.Size);
        var speed = shown.Sum(s => s.Entry.Speed);
        var lines = shown.Select(s => $"{DisplayName(s.Path)} — {IndicatorNative.FormatBytes(s.Entry.Size)}");
        var tooltip = (shown.Count == 1 ? "Загрузка: " : $"Загрузки ({shown.Count}):\n") + string.Join("\n", lines);
        if (speed > 1024) tooltip += $"\n{IndicatorNative.FormatBytes((long)speed)}/с";
        var folder = _folder;
        Hub.Set(new Indicator(Id, Glyphs.Download, "AccentBrush", tooltip)
        {
            // Browsers don't tell how big the file will be, so show how much has arrived instead of a percentage.
            Label = IndicatorNative.FormatBytes(total),
            Order = 20,
            Click = () => IndicatorNative.ShowInExplorer(shown.Count == 1 ? shown[0].Path : folder),
        }, this);
    }

    /// <summary>"photo.jpg.crdownload" -> "photo.jpg".</summary>
    static string DisplayName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var ext in TempExtensions)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return name;
    }
}
