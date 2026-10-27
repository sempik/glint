using Glint.Core;

namespace Glint.CommandBar;

/// <summary>Start Menu shortcuts, rescanned in the background, ranked by name match plus launch history.</summary>
public sealed class AppIndex : ICommandProvider
{
    sealed record Entry(string Name, string Lower, string Initials, string Path);

    sealed class HistoryData
    {
        public Dictionary<string, int> Launches { get; set; } = new();
    }

    const string HistoryFile = "commandbar.json";
    const int MaxResults = 6;
    static readonly string[] Extensions = { ".lnk", ".url", ".exe", ".appref-ms" };

    public static AppIndex Shared { get; } = new();

    volatile Entry[] _entries = Array.Empty<Entry>();
    readonly object _historyLock = new();
    readonly object _writeLock = new();
    Dictionary<string, int> _launches = new(StringComparer.OrdinalIgnoreCase);
    bool _historyLoaded;
    System.Threading.Timer? _refresh;
    int _scanning;

    /// <summary>Starts the background scan (now, then every 10 minutes). Safe to call more than once.</summary>
    public void Start()
    {
        if (_refresh is not null) return;
        _refresh = new System.Threading.Timer(_ => Scan(), null, TimeSpan.Zero, TimeSpan.FromMinutes(10));
    }

    void Scan()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            EnsureHistory();
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
            };
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<Entry>();

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", options))
                {
                    if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileNameWithoutExtension(file);
                    // The same app often has a shortcut both for all users and the current user.
                    if (name.Length == 0 || IsNoise(name) || !seen.Add(name)) continue;
                    list.Add(new Entry(name, name.ToLowerInvariant(), Matcher.Initials(name), file));
                }
            }
            _entries = list.ToArray();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
    }

    static bool IsNoise(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("удалить", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("удаление", StringComparison.OrdinalIgnoreCase);

    void EnsureHistory()
    {
        lock (_historyLock)
        {
            if (_historyLoaded) return;
            _historyLoaded = true;
            var data = SettingsStore.ReadJson<HistoryData>(HistoryFile);
            if (data?.Launches is { } launches)
                _launches = new Dictionary<string, int>(launches, StringComparer.OrdinalIgnoreCase);
        }
    }

    double Boost(string path)
    {
        lock (_historyLock)
        {
            // Diminishing returns so one favourite doesn't bury every other prefix match.
            return _launches.TryGetValue(path, out var n) && n > 0 ? Math.Min(30, 8 * Math.Log2(1 + n)) : 0;
        }
    }

    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return Array.Empty<CommandResult>();

        var hits = new List<(Entry Entry, double Score)>();
        foreach (var e in _entries)
        {
            var s = Matcher.Score(e.Lower, e.Initials, q, fuzzy: true);
            if (s > 0) hits.Add((e, s + Boost(e.Path)));
        }
        ct.ThrowIfCancellationRequested();
        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Entry.Name.Length)
            .Take(MaxResults)
            .Select(h => ToResult(h.Entry, h.Score, "Приложение"))
            .ToList();
    }

    /// <summary>Most launched apps, for the empty query.</summary>
    public IEnumerable<CommandResult> Recent(int count)
    {
        List<KeyValuePair<string, int>> top;
        lock (_historyLock) top = _launches.OrderByDescending(kv => kv.Value).ToList();
        var byPath = _entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        return top
            .Where(kv => byPath.ContainsKey(kv.Key))
            .Take(count)
            .Select(kv => ToResult(byPath[kv.Key], 0, "Часто запускаемое"))
            .ToList();
    }

    CommandResult ToResult(Entry e, double score, string hint) => new()
    {
        Title = e.Name,
        Hint = hint,
        Glyph = Glyphs.Apps,
        IconPath = e.Path,
        Score = score,
        Path = e.Path,
        Execute = () => Launch(e.Path, elevated: false),
        ExecuteElevated = () => Launch(e.Path, elevated: true),
        CanPin = true,
    };

    void Launch(string path, bool elevated)
    {
        ShellRun.Open(path, elevated: elevated);
        RecordLaunch(path);
    }

    void RecordLaunch(string path)
    {
        Dictionary<string, int> snapshot;
        lock (_historyLock)
        {
            _launches[path] = _launches.TryGetValue(path, out var n) ? n + 1 : 1;
            snapshot = new Dictionary<string, int>(_launches);
        }
        Task.Run(() =>
        {
            try
            {
                lock (_writeLock) SettingsStore.WriteJson(HistoryFile, new HistoryData { Launches = snapshot });
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        });
    }
}
