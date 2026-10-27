using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Glint.Core;

namespace Glint.Services;

/// <summary>
/// Exchange rates for the command bar (opt-in, Настройки → Командная строка → Курсы валют). Daily Bank of Russia
/// rates from cbr-xml-daily.ru, with open.er-api.com as the fallback; both are free and need no key. Rates are
/// kept in %AppData%\Glint\rates.json and refreshed at most every 6 hours, and only when someone asks for a rate,
/// so an idle Glint makes no requests at all.
/// </summary>
public sealed class CurrencyRates
{
    public static CurrencyRates Shared { get; } = new();

    /// <summary>Rubles for one unit of the currency; <see cref="Previous"/> is the prior day's (Bank of Russia only).</summary>
    public sealed record Rate(string Code, string? Name, double RubPerUnit, double? Previous);

    public sealed record Snapshot(string Source, DateTime Date, DateTime FetchedUtc, Dictionary<string, Rate> Rates);

    const string CbrUrl = "https://www.cbr-xml-daily.ru/daily_json.js";
    const string FallbackUrl = "https://open.er-api.com/v6/latest/RUB";
    const string CacheFile = "rates.json";
    public const string CbrSource = "cbr";
    public const string FallbackSource = "er-api";
    static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);
    // After a failure, wait before trying again so a typo-laden query doesn't fire a request per keystroke.
    static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    static readonly HttpClient Http = CreateClient();

    readonly object _gate = new();
    Snapshot? _snapshot;
    bool _loaded;
    Task? _refresh;
    DateTime _lastAttemptUtc;

    /// <summary>Raised on a thread-pool thread when a refresh finishes, successfully or not.</summary>
    public event EventHandler? Updated;

    /// <summary>True while a request is in flight.</summary>
    public bool IsRefreshing
    {
        get { lock (_gate) return _refresh is { IsCompleted: false }; }
    }

    /// <summary>True when the last attempt failed (no network, both services down).</summary>
    public bool LastFailed { get; private set; }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Glint (+https://github.com/sempik/glint)");
        return client;
    }

    static bool Enabled => AppServices.Settings?.Current.CurrencyRates == true;

    /// <summary>
    /// Any thread. The rates on hand (possibly stale) or null; starts a background refresh when they're older than
    /// 6 hours. Returns null and touches nothing when the feature is off.
    /// </summary>
    public Snapshot? Get()
    {
        if (!Enabled) return null;
        EnsureLoaded();
        Snapshot? current;
        lock (_gate) current = _snapshot;
        if (current is null || DateTime.UtcNow - current.FetchedUtc > FreshFor) StartRefresh(force: false);
        return current;
    }

    /// <summary>Retry right away (the "попробовать снова" row).</summary>
    public void ForceRefresh()
    {
        if (Enabled) StartRefresh(force: true);
    }

    void StartRefresh(bool force)
    {
        lock (_gate)
        {
            if (_refresh is { IsCompleted: false }) return;
            if (!force && DateTime.UtcNow - _lastAttemptUtc < RetryAfter) return;
            _lastAttemptUtc = DateTime.UtcNow;
            _refresh = Task.Run(RefreshAsync);
        }
    }

    async Task RefreshAsync()
    {
        Snapshot? fresh = null;
        try
        {
            fresh = await FetchCbrAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            App.LogError(ex);
        }
        if (fresh is null)
        {
            try
            {
                fresh = await FetchFallbackAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                App.LogError(ex);
            }
        }

        LastFailed = fresh is null;
        if (fresh is not null)
        {
            lock (_gate) _snapshot = fresh;
            try
            {
                SettingsStore.WriteJson(CacheFile, fresh);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
        try
        {
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
        }
        var cached = SettingsStore.ReadJson<Snapshot>(CacheFile);
        if (cached?.Rates is { Count: > 0 })
        {
            // Rebuilt case-insensitive; JSON brings back an ordinal dictionary.
            var rates = new Dictionary<string, Rate>(cached.Rates, StringComparer.OrdinalIgnoreCase);
            lock (_gate) _snapshot ??= cached with { Rates = rates, FetchedUtc = DateTime.SpecifyKind(cached.FetchedUtc, DateTimeKind.Utc) };
        }
    }

    // ───────────── Sources ─────────────

    /// <summary>{"Date": "...", "Valute": {"USD": {"CharCode": "USD", "Nominal": 1, "Name": "...", "Value": 92.5, "Previous": 92.1}}}</summary>
    static async Task<Snapshot?> FetchCbrAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var json = await Http.GetStringAsync(CbrUrl, cts.Token);
        return ParseCbr(json, DateTime.UtcNow);
    }

    public static Snapshot? ParseCbr(string json, DateTime fetchedUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var date = root.TryGetProperty("Date", out var d) && d.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto.DateTime
            : DateTime.Today;
        var rates = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in root.GetProperty("Valute").EnumerateObject())
        {
            var e = v.Value;
            var code = e.TryGetProperty("CharCode", out var c) ? c.GetString() ?? v.Name : v.Name;
            var nominal = e.TryGetProperty("Nominal", out var n) && n.TryGetDouble(out var nv) && nv > 0 ? nv : 1;
            if (!e.TryGetProperty("Value", out var val) || !val.TryGetDouble(out var value) || value <= 0) continue;
            double? previous = e.TryGetProperty("Previous", out var pr) && pr.TryGetDouble(out var pv) && pv > 0 ? pv / nominal : null;
            var name = e.TryGetProperty("Name", out var nm) ? nm.GetString() : null;
            rates[code] = new Rate(code.ToUpperInvariant(), name, value / nominal, previous);
        }
        return rates.Count == 0 ? null : new Snapshot(CbrSource, date, fetchedUtc, rates);
    }

    /// <summary>{"result": "success", "time_last_update_unix": 1700000000, "rates": {"USD": 0.0108, ...}} — units per ruble.</summary>
    static async Task<Snapshot?> FetchFallbackAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var json = await Http.GetStringAsync(FallbackUrl, cts.Token);
        return ParseFallback(json, DateTime.UtcNow);
    }

    public static Snapshot? ParseFallback(string json, DateTime fetchedUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("result", out var r) && r.GetString() != "success") return null;
        var date = root.TryGetProperty("time_last_update_unix", out var t) && t.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime
            : DateTime.Now;
        var rates = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in root.GetProperty("rates").EnumerateObject())
        {
            if (!p.Value.TryGetDouble(out var perRuble) || perRuble <= 0 || p.Name.Equals("RUB", StringComparison.OrdinalIgnoreCase)) continue;
            rates[p.Name] = new Rate(p.Name.ToUpperInvariant(), null, 1 / perRuble, null);
        }
        return rates.Count == 0 ? null : new Snapshot(FallbackSource, date, fetchedUtc, rates);
    }

    /// <summary>Rubles per unit; 1 for the ruble itself, null when the source doesn't list it.</summary>
    public static double? RubPer(Snapshot s, string code) =>
        code.Equals("RUB", StringComparison.OrdinalIgnoreCase) ? 1 : s.Rates.TryGetValue(code, out var r) ? r.RubPerUnit : null;
}
