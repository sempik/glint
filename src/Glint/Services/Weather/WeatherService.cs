using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Glint.Core;

namespace Glint.Services.Weather;

/// <summary>What the pill shows: the latest observation, or the forecast for this hour once that is older.</summary>
public sealed record WeatherDisplay(double Temperature, int Code, bool IsDay)
{
    public string Description => OpenMeteo.Describe(Code);
}

/// <summary>
/// Weather for the chosen place from Open-Meteo (opt-in, Настройки → Погода). While on, one forecast request at start
/// (unless the cached one is recent) and then every 30 minutes; off, it makes no requests and runs no timers.
/// The last forecast is cached in %AppData%\Glint\weather.json, so the pill has something to show right after a
/// restart or while offline. The rain warning works from the 15-minute precipitation forecast already on hand:
/// a one-shot timer is aimed at "an hour before the rain", no extra requests.
/// </summary>
public sealed class WeatherService
{
    public static WeatherService Shared { get; } = new();

    const string CacheFile = "weather.json";
    static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);
    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    // The refresh button can't hammer the service.
    static readonly TimeSpan MinGap = TimeSpan.FromMinutes(1);
    // Warn when the rain is at most this far off; the 15-minute forecast is only good for a couple of hours.
    const long RainLeadSeconds = 75 * 60;
    // A rain starting this soon after the one already announced (or seen) is the same rain.
    const long SameRainSeconds = 45 * 60;
    static readonly HttpClient Http = CreateClient();

    // Bound to the UI dispatcher: the command bar's "погода" may be the first to touch Shared, off-thread.
    readonly DispatcherTimer _refresh = new(DispatcherPriority.Normal, Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
    readonly DispatcherTimer _rainCheck = new(DispatcherPriority.Normal, Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
    readonly object _cacheLock = new();
    int _cacheVersion;
    bool _started;
    bool _fetching;
    DateTime _lastAttemptUtc;
    string? _placeKey;
    long? _rainAnnounced;

    sealed record Cache(string PlaceKey, WeatherForecast Forecast, long? RainAnnounced);

    public WeatherForecast? Forecast { get; private set; }
    public bool IsRefreshing => _fetching;
    /// <summary>True when the last request failed (offline, service down); the cached forecast is still shown.</summary>
    public bool LastFailed { get; private set; }

    /// <summary>Raised on the UI thread when the forecast, the refresh state or the on/off state changes.</summary>
    public event EventHandler? Changed;

    static WeatherSettings S => AppServices.Settings.Current.Weather;

    public static bool Active => S.Enabled && S.HasPlace;

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Glint (+https://github.com/sempik/glint)");
        return client;
    }

    /// <summary>UI thread, once at startup.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _refresh.Tick += (_, _) => { _refresh.Stop(); Fetch(); };
        _rainCheck.Tick += (_, _) => { _rainCheck.Stop(); CheckRain(); };
        AppServices.Settings.Changed += (_, _) => Apply();
        // A DispatcherTimer doesn't count the time asleep; after waking, a stale forecast is fetched once the
        // network is back.
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) OnUi(() => { if (Active) ScheduleRefresh(TimeSpan.FromSeconds(15)); });
        };
        Apply();
    }

    void Apply()
    {
        if (!Active)
        {
            _refresh.Stop();
            _rainCheck.Stop();
            _placeKey = null;
            if (Forecast is not null)
            {
                Forecast = null;
                RaiseChanged();
            }
            return;
        }

        var key = PlaceKey(S);
        if (key != _placeKey)
        {
            _placeKey = key;
            // The once-a-minute limit is per place: a new city gets its forecast right away.
            _lastAttemptUtc = default;
            var cached = SettingsStore.ReadJson<Cache>(CacheFile);
            // A cache for another city is no use here.
            var usable = cached?.PlaceKey == key && cached.Forecast?.Now is not null && cached.Forecast.Hours is not null ? cached : null;
            Forecast = usable is null ? null : Repair(usable.Forecast);
            _rainAnnounced = usable?.RainAnnounced;
            LastFailed = false;
            var age = Forecast is null ? TimeSpan.MaxValue : DateTime.UtcNow - Forecast.FetchedUtc;
            ScheduleRefresh(age >= RefreshEvery ? TimeSpan.FromSeconds(Forecast is null ? 0.3 : 3) : RefreshEvery - age);
            RaiseChanged();
        }
        // The rain warning may have just been switched on or off.
        CheckRain();
    }

    static string PlaceKey(WeatherSettings s) => FormattableString.Invariant($"{s.Latitude:F3},{s.Longitude:F3}");

    /// <summary>JSON brings lists back as empty or null; make sure nothing downstream sees null.</summary>
    static WeatherForecast Repair(WeatherForecast f) => f with
    {
        FetchedUtc = DateTime.SpecifyKind(f.FetchedUtc, DateTimeKind.Utc),
        Hours = f.Hours ?? new(),
        Days = f.Days ?? new(),
        Quarters = f.Quarters ?? new(),
    };

    /// <summary>The refresh button on the Погода tab; at most once a minute.</summary>
    public void RefreshNow()
    {
        if (!Active || _fetching) return;
        var wait = MinGap - (DateTime.UtcNow - _lastAttemptUtc);
        ScheduleRefresh(wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
    }

    /// <summary>The Погода tab came into view: fetch only if the forecast is due anyway.</summary>
    public void RefreshIfStale()
    {
        if (!Active || _fetching) return;
        if (Forecast is null || DateTime.UtcNow - Forecast.FetchedUtc >= RefreshEvery) RefreshNow();
    }

    void ScheduleRefresh(TimeSpan delay)
    {
        _refresh.Stop();
        _refresh.Interval = delay < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : delay;
        _refresh.Start();
    }

    async void Fetch()
    {
        if (!Active) return;
        if (_fetching)
        {
            // The request in flight may be for a city that is no longer chosen; it won't reschedule for this one.
            ScheduleRefresh(TimeSpan.FromSeconds(5));
            return;
        }
        var sinceLast = DateTime.UtcNow - _lastAttemptUtc;
        if (sinceLast < MinGap)
        {
            ScheduleRefresh(MinGap - sinceLast);
            return;
        }
        _fetching = true;
        _lastAttemptUtc = DateTime.UtcNow;
        RaiseChanged();

        var key = _placeKey;
        var url = OpenMeteo.ForecastUrl(S.Latitude!.Value, S.Longitude!.Value);
        var next = RefreshEvery;
        // The city changed or weather was switched off while the request ran; Apply has already rescheduled.
        bool Moved() => key != _placeKey || !Active;
        try
        {
            var json = await Http.GetStringAsync(url);
            var forecast = await Task.Run(() => OpenMeteo.Parse(json, DateTime.UtcNow));
            if (Moved()) return;
            Forecast = forecast;
            LastFailed = false;
            SaveCache();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            // Offline is normal for a laptop; only a response we can't read is worth a log line.
            if (ex is not (HttpRequestException or TaskCanceledException)) App.LogError(ex);
            // A failed request for the previous city says nothing about this one.
            if (!Moved()) LastFailed = true;
            next = RetryAfterFailure;
        }
        finally
        {
            _fetching = false;
            if (Moved())
            {
                // A new city is waiting for its first forecast.
                if (Active && Forecast is null) ScheduleRefresh(TimeSpan.Zero);
                RaiseChanged();
            }
            else
            {
                ScheduleRefresh(next);
                RaiseChanged();
                CheckRain();
            }
        }
    }

    void SaveCache()
    {
        if (Forecast is not { } f || _placeKey is not { } key) return;
        var cache = new Cache(key, f, _rainAnnounced);
        // A fetch and the rain check right after it can both save: one writer at a time (they share the .tmp file),
        // and a snapshot that a newer one has overtaken is skipped so the older state never lands last.
        var version = ++_cacheVersion;
        Task.Run(() =>
        {
            lock (_cacheLock)
            {
                if (version != Volatile.Read(ref _cacheVersion)) return;
                try
                {
                    SettingsStore.WriteJson(CacheFile, cache);
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                }
            }
        });
    }

    // ───────────── What to show ─────────────

    /// <summary>
    /// The conditions for the pill: the observation while it's under 45 minutes old, then this hour's forecast
    /// (the cache covers a week), null when nothing fits.
    /// </summary>
    public WeatherDisplay? Current
    {
        get
        {
            if (Forecast is not { } f) return null;
            if (DateTime.UtcNow - f.FetchedUtc < TimeSpan.FromMinutes(45)) return new WeatherDisplay(f.Now.Temperature, f.Now.Code, f.Now.IsDay);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var hour = f.Hours.LastOrDefault(h => h.Time <= now);
            return hour is null || now - hour.Time >= 3600 ? null : new WeatherDisplay(hour.Temperature, hour.Code, hour.IsDay);
        }
    }

    /// <summary>The hours from the current one on, at most <paramref name="count"/>.</summary>
    public IReadOnlyList<WeatherHour> NextHours(int count)
    {
        if (Forecast is not { } f) return Array.Empty<WeatherHour>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return f.Hours.Where(h => h.Time + 3600 > now).Take(count).ToList();
    }

    /// <summary>Days from today (at the place) on.</summary>
    public IReadOnlyList<WeatherDay> NextDays(int count)
    {
        if (Forecast is not { } f) return Array.Empty<WeatherDay>();
        var today = f.LocalNow.Date;
        return f.Days.Where(d => d.Date >= today).Take(count).ToList();
    }

    /// <summary>"Через ~40 мин дождь", "Дождь идёт", "Без осадков ближайшие 3 часа" for the Погода tab.</summary>
    public string? RainSummary()
    {
        if (Forecast is not { Quarters.Count: > 0 } f || DateTime.UtcNow - f.FetchedUtc > TimeSpan.FromHours(2)) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var r = OpenMeteo.Rain(f.Quarters, now);
        if (r.WetNow) return r.Snow ? "Сейчас идёт снег" : "Сейчас идёт дождь";
        if (r.StartsAt is long start)
        {
            var minutes = (start - now) / 60.0;
            var what = r.Snow ? "снег" : "дождь";
            return minutes < 8 ? $"Скоро {what}" : minutes < 90 ? $"Через ~{RoundTo5(minutes)} мин {what}" : $"{(r.Snow ? "Снег" : "Дождь")} около {f.LocalTime(start):HH:mm}";
        }
        var span = (f.Quarters[^1].Time - now) / 3600.0;
        return span >= 1.5 ? $"Без осадков ближайшие {Math.Floor(span):0} ч" : "Без осадков";
    }

    static int RoundTo5(double minutes) => Math.Max(5, (int)Math.Round(minutes / 5) * 5);

    // ───────────── Rain warning ─────────────

    /// <summary>
    /// Flashes once per rain, about an hour ahead. A rain counts as new only when it starts 45+ minutes after the
    /// last one announced or seen; with no rain left in the forecast the slate is wiped.
    /// </summary>
    void CheckRain()
    {
        _rainCheck.Stop();
        if (!Active || !S.RainAlert || Forecast is not { } f) return;
        // Too old to warn from; the next refresh brings a fresh outlook.
        if (DateTime.UtcNow - f.FetchedUtc > TimeSpan.FromHours(2)) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var r = OpenMeteo.Rain(f.Quarters, now);

        if (r.WetNow)
        {
            // Already raining: no "через", and this rain counts as announced.
            _rainAnnounced = now;
            AimRainCheck(TimeSpan.FromMinutes(15));
            return;
        }
        if (r.StartsAt is not long start)
        {
            if (_rainAnnounced is not null)
            {
                _rainAnnounced = null;
                SaveCache();
            }
            return;
        }
        if (_rainAnnounced is long known && start - known < SameRainSeconds) return;

        var lead = start - now;
        if (lead > RainLeadSeconds)
        {
            // Look again from the data on hand when the rain is an hour away.
            AimRainCheck(TimeSpan.FromSeconds(lead - 3600));
            return;
        }

        var what = r.Snow ? "снег" : "дождь";
        var title = lead < 8 * 60 ? $"Скоро {what}" : $"Через ~{RoundTo5(lead / 60.0)} мин {what}";
        var strength = r.Peak >= 1.5 ? (r.Snow ? "сильный снег" : "сильный дождь") : r.Peak < 0.3 ? (r.Snow ? "небольшой снег" : "небольшой дождь") : null;
        var subtitle = strength is null ? S.Place : $"{S.Place} · {strength}";
        _rainAnnounced = start;
        SaveCache();
        AppServices.Notifier.FlashClickable(title, subtitle, Glyphs.Cloud, () => (AppServices.Notifier as Glint.Pill.PillWindow)?.ShowWeather(), TimeSpan.FromSeconds(5));
    }

    void AimRainCheck(TimeSpan wait)
    {
        // The 30-minute refresh re-checks anyway; this only catches the hour mark in between.
        if (wait > RefreshEvery) return;
        _rainCheck.Interval = wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait;
        _rainCheck.Start();
    }

    // ───────────── City search ─────────────

    /// <summary>Open-Meteo geocoding with Russian names. Network; runs only when the user searches.</summary>
    public static async Task<List<GeoPlace>> SearchAsync(string name, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(OpenMeteo.SearchUrl(name), ct);
        return OpenMeteo.ParsePlaces(json);
    }

    /// <summary>Stores the place and turns weather on. UI thread.</summary>
    public static void ChoosePlace(GeoPlace place)
    {
        var s = S;
        s.Place = place.Name;
        s.PlaceDetails = place.Details;
        s.Latitude = Math.Round(place.Latitude, 4);
        s.Longitude = Math.Round(place.Longitude, 4);
        s.Enabled = true;
        AppServices.Settings.Save();
    }

    void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
