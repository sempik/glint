using System.Text;
using Glint.Core;
using Microsoft.Win32;

namespace Glint.Services.Indicators;

/// <summary>
/// Steam downloads. Each game has steamapps\appmanifest_ID.acf with its name, state flags and byte counters,
/// which Steam rewrites while it downloads. Watchers on the library folders wake us when a manifest changes;
/// a 3-second re-read runs only while some game is actually downloading. A game counts only while Steam runs
/// and its counters keep moving, so bits left behind by a crash don't hold the badge forever.
/// </summary>
public sealed class SteamMonitor : IndicatorMonitor
{
    const string IdPrefix = "steam.";

    // EAppState bits that mean "work in progress".
    const long UpdateRequired = 2, FullyInstalled = 4, UpdateRunning = 256, UpdatePaused = 512, UpdateStarted = 1024,
        Preallocating = 524288, Downloading = 1048576, Staging = 2097152, Committing = 4194304;
    const long Busy = UpdateRunning | UpdateStarted | Preallocating | Downloading | Staging | Committing;

    // Busy bits that haven't moved for this long were left by a closed Steam or an update queued forever.
    // Long enough for preallocating a big game on a slow disk, when nothing in the manifest moves either.
    static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(10);

    sealed record AppState(string AppId, string Name, long Flags, long ToDownload, long Downloaded, long Staged)
    {
        public bool Busy => (Flags & SteamMonitor.Busy) != 0 && (Flags & UpdatePaused) == 0;
        public (long, long, long) Progress => (Flags, Downloaded, Staged);
    }

    readonly List<FileSystemWatcher> _watchers = new();
    readonly Dictionary<string, AppState> _downloading = new();
    // Last seen progress of each busy app and when it last moved.
    readonly Dictionary<string, ((long, long, long) Progress, DateTime Since)> _moved = new();
    readonly object _gate = new();
    List<string> _libraries = new();
    Timer? _debounce, _poll;

    public SteamMonitor(IndicatorHub hub) : base(hub) { }

    protected override void Start()
    {
        _libraries = FindLibraries();
        foreach (var steamapps in _libraries)
        {
            var w = new FileSystemWatcher(steamapps)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            // Steam may write a temp file and rename it over the manifest, so listen to everything in the folder.
            FileSystemEventHandler onChange = (_, e) => { if (IsManifest(e.Name)) ScheduleScan(); };
            w.Changed += onChange;
            w.Created += onChange;
            w.Renamed += (_, e) => { if (IsManifest(e.Name) || IsManifest(e.OldName)) ScheduleScan(); };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        if (_libraries.Count == 0) return;
        _debounce = new Timer(_ => Scan(initial: false));
        _poll = new Timer(_ => Scan(initial: false));
        // Games that were already downloading when Glint started.
        ThreadPool.QueueUserWorkItem(_ => Scan(initial: true));
    }

    protected override void Stop()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _debounce?.Dispose();
        _poll?.Dispose();
        _debounce = _poll = null;
        lock (_gate)
        {
            _downloading.Clear();
            _moved.Clear();
        }
        Hub.RemoveAll(IdPrefix);
    }

    static bool IsManifest(string? name) =>
        name is not null && name.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) && name.Contains(".acf", StringComparison.OrdinalIgnoreCase);

    void ScheduleScan() => _debounce?.Change(TimeSpan.FromMilliseconds(700), Timeout.InfiniteTimeSpan);

    void Scan(bool initial)
    {
        // Runs on timer and pool threads, where an exception would end the whole process.
        try
        {
            ScanManifests(initial);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void ScanManifests(bool initial)
    {
        if (!Running) return;
        var states = new List<AppState>();
        foreach (var lib in _libraries)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
                    if (ReadManifest(file) is { } s) states.Add(s);
            }
            catch (Exception)
            {
                // A library on a disconnected drive; the others still count.
            }
        }

        // A copied library or a stray "appmanifest_570 - Copy.acf" can list the same game twice.
        var flagged = states.Where(s => s.Busy).GroupBy(s => s.AppId).ToDictionary(g => g.Key, g => g.First());
        // Busy bits stay in the manifest when Steam is closed or crashes mid-download.
        var steamRunning = flagged.Count > 0 && IsSteamRunning();

        lock (_gate)
        {
            var time = DateTime.UtcNow;
            foreach (var id in _moved.Keys.Where(id => !flagged.ContainsKey(id)).ToList()) _moved.Remove(id);
            var busyNow = new Dictionary<string, AppState>();
            foreach (var s in flagged.Values)
            {
                if (!_moved.TryGetValue(s.AppId, out var seen) || seen.Progress != s.Progress) _moved[s.AppId] = (s.Progress, time);
                else if (time - seen.Since > StallLimit) continue;
                if (steamRunning) busyNow[s.AppId] = s;
            }
            foreach (var old in _downloading.Values.ToList())
            {
                if (busyNow.ContainsKey(old.AppId)) continue;
                _downloading.Remove(old.AppId);
                Hub.Remove(IdPrefix + old.AppId);
                var now = states.FirstOrDefault(s => s.AppId == old.AppId);
                // Done only if Steam marks it fully installed with nothing left to update (not paused, stalled or removed).
                if (!initial && now is not null && !now.Busy && (now.Flags & FullyInstalled) != 0 && (now.Flags & (UpdateRequired | UpdatePaused)) == 0)
                    AppServices.Notifier.Flash("Загрузка завершена", now.Name, Glyphs.Game, TimeSpan.FromSeconds(3));
            }
            foreach (var s in busyNow.Values)
            {
                _downloading[s.AppId] = s;
                double? progress = s.ToDownload > 0 ? Math.Clamp((double)s.Downloaded / s.ToDownload, 0, 1) : null;
                var detail = s.ToDownload > 0
                    ? $"{IndicatorNative.FormatBytes(s.Downloaded)} из {IndicatorNative.FormatBytes(s.ToDownload)}"
                    : "подготовка";
                Hub.Set(new Indicator(IdPrefix + s.AppId, Glyphs.Game, "Accent2Brush", $"Steam: {s.Name}\n{detail}")
                {
                    Label = progress is double p ? $"{p:P0}" : null,
                    Progress = progress,
                    Order = 21,
                    Click = OpenSteamDownloads,
                }, this);
            }
            // Steam rewrites manifests only now and then; re-read on a slow beat while something is in flight.
            _poll?.Change(_downloading.Count > 0 ? TimeSpan.FromSeconds(3) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    static void OpenSteamDownloads()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("steam://open/downloads") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static AppState? ReadManifest(string path)
    {
        try
        {
            string text;
            // Steam may be writing it right now.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                text = reader.ReadToEnd();
            var root = Vdf.Parse(text);
            if (root.Get("AppState") is not Vdf.Node app) return null;
            var id = app.Value("appid");
            if (string.IsNullOrEmpty(id)) return null;
            return new AppState(id, app.Value("name") ?? id, Long(app.Value("StateFlags")), Long(app.Value("BytesToDownload")),
                Long(app.Value("BytesDownloaded")), Long(app.Value("BytesStaged")));
        }
        catch (Exception)
        {
            return null;
        }
    }

    static long Long(string? s) => long.TryParse(s, out var v) ? v : 0;

    static bool IsSteamRunning()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("steam");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>steamapps folders of every Steam library on this PC.</summary>
    internal static List<string> FindLibraries()
    {
        var result = new List<string>();
        var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (string.IsNullOrWhiteSpace(steam)) return result;
        steam = Path.GetFullPath(steam.Replace('/', '\\'));
        var main = Path.Combine(steam, "steamapps");
        if (Directory.Exists(main)) result.Add(main);
        try
        {
            var vdf = Path.Combine(main, "libraryfolders.vdf");
            if (File.Exists(vdf) && Vdf.Parse(File.ReadAllText(vdf)).Get("libraryfolders") is Vdf.Node folders)
            {
                foreach (var (_, entry) in folders.Children)
                {
                    if (entry is not Vdf.Node lib || lib.Value("path") is not { Length: > 0 } p) continue;
                    var apps = Path.Combine(p, "steamapps");
                    if (Directory.Exists(apps) && !result.Contains(apps, StringComparer.OrdinalIgnoreCase)) result.Add(apps);
                }
            }
        }
        catch (Exception)
        {
            // Unreadable library list: the main library is still watched.
        }
        return result;
    }
}

/// <summary>Minimal reader for Valve's KeyValues text format ("key" "value" and "key" { ... }).</summary>
internal static class Vdf
{
    public sealed class Node
    {
        public List<(string Key, object Value)> Children { get; } = new();

        public object? Get(string key) =>
            Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        public string? Value(string key) => Get(key) as string;
    }

    public static Node Parse(string text)
    {
        var pos = 0;
        return ParseNode(text, ref pos);
    }

    static Node ParseNode(string text, ref int pos)
    {
        var node = new Node();
        while (true)
        {
            var key = NextToken(text, ref pos, out var keyQuoted);
            if (key is null || (!keyQuoted && key == "}")) return node;
            var value = NextToken(text, ref pos, out var valueQuoted);
            if (value is null) return node;
            if (!valueQuoted && value == "{") node.Children.Add((key, ParseNode(text, ref pos)));
            else node.Children.Add((key, value));
        }
    }

    static string? NextToken(string text, ref int pos, out bool quoted)
    {
        quoted = false;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            // Line comments.
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            break;
        }
        if (pos >= text.Length) return null;

        if (text[pos] is '{' or '}')
            return text[pos++].ToString();

        var sb = new StringBuilder();
        if (text[pos] == '"')
        {
            quoted = true;
            pos++;
            while (pos < text.Length && text[pos] != '"')
            {
                if (text[pos] == '\\' && pos + 1 < text.Length)
                {
                    pos++;
                    sb.Append(text[pos] switch { 'n' => '\n', 't' => '\t', _ => text[pos] });
                }
                else
                {
                    sb.Append(text[pos]);
                }
                pos++;
            }
            pos++; // closing quote
            return sb.ToString();
        }
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"')) sb.Append(text[pos++]);
        return sb.ToString();
    }
}
