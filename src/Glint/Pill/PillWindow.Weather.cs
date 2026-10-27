using System.Windows;
using Glint.Core;
using Glint.Pages;
using Glint.Services.Weather;

namespace Glint.Pill;

/// <summary>
/// Weather next to the clock: a small icon and the temperature. When the pill is already crowded (lyrics, a
/// recording, dictation or three badges) it takes the clock's place for a few seconds out of every twelve instead
/// of making the pill wider. The swap rides on the clock's one-second tick and is a plain visibility change.
/// </summary>
public partial class PillWindow
{
    const double WeatherSlotWidth = 52;
    // Clock for the first 8 seconds of each 12, weather for the last 4.
    const int RotationPeriod = 12, RotationWeatherFrom = 8;

    bool _weatherShown;
    bool _weatherRotating;
    int _weatherTick;
    string? _weatherIconKey;

    double WeatherExtraWidth => _weatherShown && !_weatherRotating ? WeatherSlotWidth : 0;

    void InitWeather()
    {
        // Both are raised on the UI thread.
        WeatherService.Shared.Changed += (_, _) => UpdatePillWeather();
        AppServices.Settings.Changed += (_, _) => UpdatePillWeather();
        _clock.Tick += (_, _) => TickWeather();
    }

    void UpdatePillWeather()
    {
        var s = AppServices.Settings.Current.Weather;
        var now = s.ShowInPill && WeatherService.Active ? WeatherService.Shared.Current : null;
        if (now is not null)
        {
            var key = $"{now.Code}:{now.IsDay}";
            if (key != _weatherIconKey)
            {
                _weatherIconKey = key;
                PillWeatherIcon.Content = WeatherIcons.Create(now.Code, now.IsDay, 16);
            }
            var text = WeatherPage.FormatTemperature(now.Temperature, signed: false);
            if (PillWeatherTemp.Text != text) PillWeatherTemp.Text = text;
            PillWeather.ToolTip = $"{s.Place}: {WeatherPage.FormatTemperature(now.Temperature, signed: true)}, {now.Description.ToLowerInvariant()}";
        }
        var shown = now is not null;
        if (shown == _weatherShown) return;
        _weatherShown = shown;
        ResizePillForIndicators();
    }

    /// <summary>Own slot or taking turns with the clock; called on every change that can crowd the pill.</summary>
    void UpdateWeatherSlot()
    {
        var crowded = _lyricShown || _recShown || _voiceShown || _agentShown || PillIndicators.Children.Count >= 3;
        var rotating = _weatherShown && crowded;
        if (rotating != _weatherRotating)
        {
            _weatherRotating = rotating;
            _weatherTick = 0;
        }
        ApplyWeatherSlot();
    }

    void ApplyWeatherSlot()
    {
        if (!_weatherShown)
        {
            PillWeather.Visibility = Visibility.Collapsed;
            ClockText.Visibility = Visibility.Visible;
            return;
        }
        var weatherTurn = _weatherRotating && _weatherTick >= RotationWeatherFrom;
        ClockText.Visibility = weatherTurn ? Visibility.Collapsed : Visibility.Visible;
        PillWeather.Visibility = !_weatherRotating || weatherTurn ? Visibility.Visible : Visibility.Collapsed;
        PillWeather.Margin = new Thickness(weatherTurn ? 0 : 9, 0, 0, 0);
    }

    void TickWeather()
    {
        if (_weatherRotating)
        {
            _weatherTick = (_weatherTick + 1) % RotationPeriod;
            // Swap only when someone can see it; otherwise just keep counting.
            if ((_weatherTick is 0 or RotationWeatherFrom) && _state == State.Pill && !_hiddenByRule && !_tucked) ApplyWeatherSlot();
        }
        // An aging observation gives way to the hourly forecast; once a minute is plenty.
        if (DateTime.Now.Second == 0 && WeatherService.Active) UpdatePillWeather();
    }
}
