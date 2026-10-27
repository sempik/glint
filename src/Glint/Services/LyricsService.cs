using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Glint.Core;

namespace Glint.Services;

/// <summary>
/// Synced lyrics for the current GSMTC track from lrclib.net (opt-in, Настройки → Индикаторы).
/// Only the artist, title and length are sent. The current line follows the media position with a
/// single one-shot timer aimed at the next line, so there's no polling between lines; seeks and pauses arrive
/// as media events and re-aim it.
/// </summary>
public sealed partial class LyricsService
{
    public static LyricsService Shared { get; } = new();

    public sealed record Line(TimeSpan At, string Text);

    static readonly HttpClient Http = CreateClient();
    const int CacheSize = 40;
    const int MinLines = 4;

    // Track key -> lines (empty list = looked up, nothing found). UI thread only.
    readonly Dictionary<string, IReadOnlyList<Line>> _cache = new();
    readonly LinkedList<string> _cacheOrder = new();
    readonly DispatcherTimer _next = new();
    bool _started;
    string? _trackKey;
    IReadOnlyList<Line> _lines = Array.Empty<Line>();
    int _index = -1;

    /// <summary>Current line or null (no lyrics, paused before the first line, feature off). UI thread.</summary>
    public string? CurrentLine { get; private set; }

    /// <summary>True when the current track has synced lyrics loaded.</summary>
    public bool HasLyrics => _lines.Count > 0;

    /// <summary>Raised on the UI thread when <see cref="CurrentLine"/> changes.</summary>
    public event EventHandler? Changed;

    static HttpClient CreateClient()
    {
        // The search endpoint can take half a minute when the service is busy; it all runs in the background.
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        // lrclib asks clients to identify themselves.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Glint (+https://github.com/sempik/glint)");
        return client;
    }

    static bool Enabled => AppServices.Settings.Current.Indicators.Lyrics;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _next.Tick += (_, _) => { _next.Stop(); Sync(); };
        AppServices.Media.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(OnMediaChanged);
        AppServices.Settings.Changed += (_, _) => OnMediaChanged();
        OnMediaChanged();
    }

    void OnMediaChanged()
    {
        var media = AppServices.Media?.Current;
        if (!Enabled || media is null || string.IsNullOrWhiteSpace(media.Title))
        {
            _trackKey = null;
            SetLines(Array.Empty<Line>());
            return;
        }

        var key = TrackKey(media);
        if (key != _trackKey)
        {
            _trackKey = key;
            SetLines(Array.Empty<Line>());
            if (_cache.TryGetValue(key, out var cached)) SetLines(cached);
            else _ = FetchAsync(key, media);
        }
        Sync();
    }

    static string TrackKey(MediaInfo m) => $"{m.Artist}\n{m.Title}\n{(int)m.Duration.TotalSeconds}";

    async Task FetchAsync(string key, MediaInfo media)
    {
        IReadOnlyList<Line> lines;
        // Players publish the title first and the length a moment later; skipping tracks also flips through
        // titles fast. Ask only once the track has settled.
        await Task.Delay(800).ConfigureAwait(true);
        if (key != _trackKey) return;
        if (AppServices.Media?.Current is { } settled && TrackKey(settled) == key) media = settled;
        try
        {
            lines = await LookupAsync(media).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Offline or the service is down: no lyrics for now, try again on the next track change.
            return;
        }
        Remember(key, lines);
        if (key == _trackKey)
        {
            SetLines(lines);
            Sync();
        }
    }

    void Remember(string key, IReadOnlyList<Line> lines)
    {
        _cache[key] = lines;
        _cacheOrder.AddLast(key);
        while (_cacheOrder.Count > CacheSize)
        {
            _cache.Remove(_cacheOrder.First!.Value);
            _cacheOrder.RemoveFirst();
        }
    }

    void SetLines(IReadOnlyList<Line> lines)
    {
        _lines = lines;
        _index = -1;
        _next.Stop();
        SetCurrent(null);
    }

    /// <summary>Finds the line at the current position and aims the timer at the next one.</summary>
    void Sync()
    {
        _next.Stop();
        var media = AppServices.Media?.Current;
        if (_lines.Count == 0 || media is null)
        {
            SetCurrent(null);
            return;
        }
        // Small lead so a line appears as it's sung, not after.
        var position = media.Position + TimeSpan.FromMilliseconds(150);
        var index = -1;
        for (var i = 0; i < _lines.Count && _lines[i].At <= position; i++) index = i;
        _index = index;
        SetCurrent(index >= 0 && _lines[index].Text.Length > 0 ? _lines[index].Text : null);

        if (!media.IsPlaying || index + 1 >= _lines.Count) return;
        var wait = _lines[index + 1].At - position;
        // Re-check at least every few seconds: the player's clock and ours drift apart a little.
        _next.Interval = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds, 60, 4000));
        _next.Start();
    }

    void SetCurrent(string? line)
    {
        if (line == CurrentLine) return;
        CurrentLine = line;
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ───────────── lrclib.net ─────────────

    static async Task<IReadOnlyList<Line>> LookupAsync(MediaInfo media)
    {
        var (artist, title) = CleanUp(media.Artist, media.Title);
        var seconds = (int)Math.Round(media.Duration.TotalSeconds);

        // Exact match first: the service matches the length within a couple of seconds.
        if (artist.Length > 0 && seconds > 0)
        {
            var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}&duration={seconds}";
            using var response = await Http.GetAsync(url).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                // The database is crowd-sourced and has stubs; a real song has more than a couple of lines.
                if (Synced(doc.RootElement) is { } lrc && ParseLrc(lrc) is { Count: >= MinLines } exact) return exact;
            }
            else if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
        }

        // Then a search, preferring the closest length.
        var query = artist.Length > 0 ? $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}" : $"q={Uri.EscapeDataString(title)}";
        using (var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://lrclib.net/api/search?{query}").ConfigureAwait(false)))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<Line>();
            var best = doc.RootElement.EnumerateArray()
                .Where(e => Synced(e) is not null)
                .OrderBy(e => seconds <= 0 || !e.TryGetProperty("duration", out var d) || d.ValueKind != JsonValueKind.Number ? 0 : Math.Abs(d.GetDouble() - seconds))
                .Select(e => ParseLrc(Synced(e)!))
                .FirstOrDefault(lines => lines.Count >= MinLines);
            return best ?? Array.Empty<Line>();
        }
    }

    static string? Synced(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString())
            ? s.GetString()
            : null;

    [GeneratedRegex(@"\s*[\(\[](official|lyric|lyrics|audio|video|music video|hd|hq|4k|visualizer|клип|премьера)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.,]\d{1,3})?)\]")]
    private static partial Regex TimeTagRegex();

    /// <summary>
    /// Browser players report "Artist - Title (Official Video)" as the title and the channel as the artist;
    /// strip the noise and split the pair when the artist looks like a channel name.
    /// </summary>
    internal static (string Artist, string Title) CleanUp(string artist, string title)
    {
        title = NoiseRegex().Replace(title, string.Empty).Trim();
        artist = (artist ?? string.Empty).Replace(" - Topic", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && (artist.Length == 0 || title.StartsWith(artist, StringComparison.OrdinalIgnoreCase) || artist.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase)))
        {
            artist = title[..dash].Trim();
            title = title[(dash + 3)..].Trim();
        }
        return (artist, title);
    }

    /// <summary>"[01:02.34]line" (several tags per line allowed) -> sorted lines.</summary>
    internal static IReadOnlyList<Line> ParseLrc(string lrc)
    {
        var lines = new List<Line>();
        foreach (var raw in lrc.Split('\n'))
        {
            var matches = TimeTagRegex().Matches(raw);
            if (matches.Count == 0) continue;
            var text = raw[(matches[^1].Index + matches[^1].Length)..].Trim();
            foreach (Match m in matches)
            {
                var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                lines.Add(new Line(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds), text));
            }
        }
        lines.Sort((a, b) => a.At.CompareTo(b.At));
        return lines;
    }
}
