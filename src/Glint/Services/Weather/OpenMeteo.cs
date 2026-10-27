using System.Globalization;
using System.Text.Json;

namespace Glint.Services.Weather;

/// <summary>What an icon draws for a WMO weather code.</summary>
public enum WeatherKind { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Showers, Sleet, Snow, Thunder }

public sealed record WeatherNow(double Temperature, double Apparent, int Humidity, double Wind, int Code, bool IsDay, double Precipitation);

/// <summary>Times are Unix seconds; <see cref="WeatherForecast.LocalTime"/> turns them into the place's clock.</summary>
public sealed record WeatherHour(long Time, double Temperature, int Code, int? PrecipitationChance, bool IsDay);

public sealed record WeatherDay(DateTime Date, int Code, double Max, double Min, int? PrecipitationChance);

/// <summary>A 15-minute slot ending at <see cref="Time"/>: precipitation in mm and snowfall in cm over those minutes.</summary>
public sealed record WeatherQuarter(long Time, double Precipitation, double Snowfall);

public sealed record WeatherForecast(
    double Latitude,
    double Longitude,
    int UtcOffsetSeconds,
    DateTime FetchedUtc,
    WeatherNow Now,
    List<WeatherHour> Hours,
    List<WeatherDay> Days,
    List<WeatherQuarter> Quarters)
{
    /// <summary>Wall-clock time at the forecast's place (Open-Meteo picks its time zone).</summary>
    public DateTime LocalTime(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix + UtcOffsetSeconds).DateTime;

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime LocalNow => LocalTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}

public sealed record GeoPlace(string Name, string? Region, string? Country, double Latitude, double Longitude)
{
    /// <summary>"Московская область, Россия" for the list under the search box.</summary>
    public string Details => string.Join(", ", new[] { Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s) && s != Name));
}

/// <summary>Rain in the next hours from the 15-minute forecast.</summary>
public readonly record struct RainOutlook(bool WetNow, long? StartsAt, bool Snow, double Peak);

/// <summary>
/// Open-Meteo (open-meteo.com): free, no key. Request URLs, response parsing, WMO codes and the rain lookahead.
/// No app dependencies, so throwaway harnesses can link this file as is.
/// </summary>
public static class OpenMeteo
{
    /// <summary>Precipitation per 15 minutes that counts as rain: about 0.4 mm an hour, a light rain you'd notice.</summary>
    public const double WetMm = 0.1;

    public static string ForecastUrl(double latitude, double longitude) =>
        "https://api.open-meteo.com/v1/forecast"
        + $"?latitude={latitude.ToString("0.####", CultureInfo.InvariantCulture)}&longitude={longitude.ToString("0.####", CultureInfo.InvariantCulture)}"
        + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,is_day,precipitation,weather_code,wind_speed_10m"
        + "&hourly=temperature_2m,weather_code,precipitation_probability,is_day"
        + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max"
        + "&minutely_15=precipitation,snowfall&past_minutely_15=1&forecast_minutely_15=12"
        + "&forecast_days=7&timezone=auto&timeformat=unixtime&wind_speed_unit=ms";

    public static string SearchUrl(string name) =>
        $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=8&language=ru&format=json";

    // ───────────── Parsing ─────────────

    public static WeatherForecast Parse(string json, DateTime fetchedUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
            throw new InvalidDataException(root.TryGetProperty("reason", out var r) ? r.GetString() : "Open-Meteo error");

        var offset = root.TryGetProperty("utc_offset_seconds", out var o) && o.TryGetInt32(out var ov) ? ov : 0;
        var c = root.GetProperty("current");
        var now = new WeatherNow(
            Num(c, "temperature_2m") ?? throw new InvalidDataException("No current temperature"),
            Num(c, "apparent_temperature") ?? Num(c, "temperature_2m")!.Value,
            (int)Math.Round(Num(c, "relative_humidity_2m") ?? 0),
            Num(c, "wind_speed_10m") ?? 0,
            (int)(Num(c, "weather_code") ?? 0),
            (Num(c, "is_day") ?? 1) > 0,
            Num(c, "precipitation") ?? 0);

        var hours = new List<WeatherHour>();
        if (root.TryGetProperty("hourly", out var h))
        {
            var time = Longs(h, "time");
            var temp = Nums(h, "temperature_2m");
            var code = Nums(h, "weather_code");
            var chance = Nums(h, "precipitation_probability");
            var day = Nums(h, "is_day");
            for (var i = 0; i < time.Count; i++)
            {
                if (At(temp, i) is not double t) continue;
                hours.Add(new WeatherHour(time[i], t, (int)(At(code, i) ?? 0), At(chance, i) is double p ? (int)Math.Round(p) : null, (At(day, i) ?? 1) > 0));
            }
        }

        var days = new List<WeatherDay>();
        if (root.TryGetProperty("daily", out var d))
        {
            var time = Longs(d, "time");
            var code = Nums(d, "weather_code");
            var max = Nums(d, "temperature_2m_max");
            var min = Nums(d, "temperature_2m_min");
            var chance = Nums(d, "precipitation_probability_max");
            for (var i = 0; i < time.Count; i++)
            {
                if (At(max, i) is not double hi || At(min, i) is not double lo) continue;
                // Daily times are the place's midnight in Unix seconds.
                var date = DateTimeOffset.FromUnixTimeSeconds(time[i] + offset).DateTime.Date;
                days.Add(new WeatherDay(date, (int)(At(code, i) ?? 0), hi, lo, At(chance, i) is double p ? (int)Math.Round(p) : null));
            }
        }

        var quarters = new List<WeatherQuarter>();
        if (root.TryGetProperty("minutely_15", out var m))
        {
            var time = Longs(m, "time");
            var rain = Nums(m, "precipitation");
            var snow = Nums(m, "snowfall");
            for (var i = 0; i < time.Count; i++)
                quarters.Add(new WeatherQuarter(time[i], At(rain, i) ?? 0, At(snow, i) ?? 0));
        }

        var lat = root.TryGetProperty("latitude", out var la) && la.TryGetDouble(out var lav) ? lav : 0;
        var lon = root.TryGetProperty("longitude", out var lo2) && lo2.TryGetDouble(out var lov) ? lov : 0;
        return new WeatherForecast(lat, lon, offset, fetchedUtc, now, hours, days, quarters);
    }

    public static List<GeoPlace> ParsePlaces(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<GeoPlace>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in results.EnumerateArray())
        {
            if (Str(e, "name") is not { } name || Num(e, "latitude") is not double lat || Num(e, "longitude") is not double lon) continue;
            list.Add(new GeoPlace(name, Str(e, "admin1"), Str(e, "country"), lat, lon));
        }
        return list;
    }

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    /// <summary>A numeric array; nulls (missing values) stay null.</summary>
    static List<double?> Nums(JsonElement e, string name)
    {
        var list = new List<double?>();
        if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in a.EnumerateArray()) list.Add(v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null);
        return list;
    }

    static List<long> Longs(JsonElement e, string name)
    {
        var list = new List<long>();
        if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in a.EnumerateArray()) if (v.TryGetInt64(out var l)) list.Add(l);
        return list;
    }

    static double? At(List<double?> list, int i) => i < list.Count ? list[i] : null;

    // ───────────── WMO weather codes ─────────────

    public static WeatherKind Kind(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        51 or 53 or 55 => WeatherKind.Drizzle,
        56 or 57 or 66 or 67 => WeatherKind.Sleet,
        61 or 63 or 65 => WeatherKind.Rain,
        80 or 81 or 82 => WeatherKind.Showers,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherKind.Snow,
        95 or 96 or 99 => WeatherKind.Thunder,
        _ => WeatherKind.Cloudy,
    };

    public static string Describe(int code) => code switch
    {
        0 => "Ясно",
        1 => "Почти ясно",
        2 => "Переменная облачность",
        3 => "Пасмурно",
        45 => "Туман",
        48 => "Туман с изморозью",
        51 => "Слабая морось",
        53 => "Морось",
        55 => "Сильная морось",
        56 or 57 => "Ледяная морось",
        61 => "Небольшой дождь",
        63 => "Дождь",
        65 => "Сильный дождь",
        66 or 67 => "Ледяной дождь",
        71 => "Небольшой снег",
        73 => "Снег",
        75 => "Сильный снег",
        77 => "Снежная крупа",
        80 => "Небольшой ливень",
        81 => "Ливень",
        82 => "Сильный ливень",
        85 => "Снегопад",
        86 => "Сильный снегопад",
        95 => "Гроза",
        96 or 99 => "Гроза с градом",
        _ => "Облачно",
    };

    // ───────────── Rain lookahead ─────────────

    /// <summary>
    /// Whether it's wet in the current 15 minutes and when the next wet slot starts. Open-Meteo sums each slot
    /// over the 15 minutes before its time, so a slot ending at t covers [t − 15 min, t).
    /// </summary>
    public static RainOutlook Rain(IReadOnlyList<WeatherQuarter> quarters, long nowUnix)
    {
        var wetNow = false;
        long? start = null;
        var snow = false;
        double peak = 0;
        foreach (var q in quarters)
        {
            if (q.Time <= nowUnix) continue;
            var wet = q.Precipitation >= WetMm;
            var from = q.Time - 900;
            if (from <= nowUnix)
            {
                wetNow = wet;
                if (wet) snow = q.Snowfall > 0;
                continue;
            }
            if (start is null)
            {
                if (!wet) continue;
                start = from;
                snow = q.Snowfall > 0;
            }
            // How hard it gets during its first hour.
            if (from - start.Value < 3600) peak = Math.Max(peak, q.Precipitation);
        }
        return new RainOutlook(wetNow, start, snow, peak);
    }
}
