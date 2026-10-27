using Glint.Core;
using Glint.Pages;
using Glint.Pill;
using Glint.Services.Weather;

namespace Glint.CommandBar;

/// <summary>"погода": the conditions from the forecast already on hand (no request of its own); Enter opens Погода.</summary>
public sealed class WeatherProvider : ICommandProvider
{
    static readonly string[] Keywords = { "погода", "weather", "температура", "дождь", "прогноз", "forecast" };

    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        var lower = query.Trim().ToLowerInvariant();
        if (lower.Length < 3) return Array.Empty<CommandResult>();
        var whole = Keywords.Any(k => lower.StartsWith(k, StringComparison.Ordinal));
        if (!whole && !Keywords.Any(k => k.StartsWith(lower, StringComparison.Ordinal))) return Array.Empty<CommandResult>();

        var s = AppServices.Settings.Current.Weather;
        if (!WeatherService.Active)
        {
            return new[]
            {
                new CommandResult
                {
                    Title = s.HasPlace ? "Погода выключена" : "Погода: город не выбран",
                    Subtitle = "Enter — открыть настройки погоды (Open-Meteo, раз в 30 минут)",
                    Hint = "Погода",
                    Glyph = Glyphs.Cloud,
                    Score = 90,
                    PrefixOnly = !whole,
                    Execute = () => (AppServices.Notifier as PillWindow)?.ShowWeatherSettings(),
                },
            };
        }

        var service = WeatherService.Shared;
        var now = service.Current;
        var today = service.NextDays(1).FirstOrDefault();
        var parts = new List<string>(3);
        if (today is not null) parts.Add($"Сегодня {WeatherPage.FormatTemperature(today.Min, true)}…{WeatherPage.FormatTemperature(today.Max, true)}");
        if (service.RainSummary() is { } rain) parts.Add(rain);
        return new[]
        {
            new CommandResult
            {
                Title = now is null
                    ? $"{s.Place}: прогноз ещё не загружен"
                    : $"{s.Place}: {WeatherPage.FormatTemperature(now.Temperature, true)}, {now.Description.ToLowerInvariant()}",
                Subtitle = parts.Count > 0 ? string.Join(" · ", parts) : "Enter — прогноз на сутки и неделю",
                Hint = "Погода",
                Glyph = Glyphs.Cloud,
                Score = 90,
                PrefixOnly = !whole,
                Execute = () => (AppServices.Notifier as PillWindow)?.ShowWeather(),
            },
        };
    }
}
