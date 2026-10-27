using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glint.Core;
using Glint.Services.Weather;

namespace Glint.Pages;

/// <summary>
/// Погода: conditions now, the next 24 hours and 7 days. Works from the forecast WeatherService already holds;
/// coming into view asks for a refresh only when the forecast is 30+ minutes old. Its only timer ("обновлено
/// N мин назад", the hour strip moving on) ticks once a minute and only while the tab is on screen.
/// </summary>
public partial class WeatherPage : UserControl
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMinutes(1) };
    bool _watching;
    string? _iconKey;
    string? _hoursKey;
    string? _daysKey;
    Action? _emptyAction;

    public WeatherPage()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Render();
        // The pill caches pages and hides the panel instead of unloading it, so watch visibility too.
        Loaded += (_, _) => UpdateWatching();
        Unloaded += (_, _) => UpdateWatching();
        IsVisibleChanged += (_, _) => UpdateWatching();
        HoursScroll.PreviewMouseWheel += OnHoursWheel;
    }

    void UpdateWatching()
    {
        var wanted = IsLoaded && IsVisible;
        if (wanted == _watching) return;
        _watching = wanted;
        if (wanted)
        {
            WeatherService.Shared.Changed += OnWeatherChanged;
            AppServices.Settings.Changed += OnWeatherChanged;
            _tick.Start();
            Render();
            WeatherService.Shared.RefreshIfStale();
        }
        else
        {
            WeatherService.Shared.Changed -= OnWeatherChanged;
            AppServices.Settings.Changed -= OnWeatherChanged;
            _tick.Stop();
        }
    }

    // Both are raised on the UI thread.
    void OnWeatherChanged(object? sender, EventArgs e) => Render();

    void Render()
    {
        var service = WeatherService.Shared;
        var s = AppServices.Settings.Current.Weather;
        if (!WeatherService.Active)
        {
            ShowEmpty(s.HasPlace ? "Погода выключена" : "Город не выбран",
                "Выберите город — температура появится рядом с часами, а здесь прогноз на сутки и неделю. Данные с Open-Meteo, раз в 30 минут",
                s.HasPlace ? "Включить в настройках" : "Выбрать город", OpenSettings);
            return;
        }
        if (service.Forecast is not { } f)
        {
            if (service.LastFailed && !service.IsRefreshing)
                ShowEmpty("Нет связи с Open-Meteo", "Прогноз появится, когда будет интернет; Glint попробует снова через 5 минут", "Повторить", service.RefreshNow);
            else
                ShowEmpty("Загружаю прогноз…", $"{s.Place}: погода с Open-Meteo", null, null);
            return;
        }

        EmptyView.Visibility = Visibility.Collapsed;
        ForecastView.Visibility = Visibility.Visible;
        RenderNow(f, s);
        RenderHours(f);
        RenderDays(f);
    }

    void ShowEmpty(string title, string text, string? button, Action? action)
    {
        ForecastView.Visibility = Visibility.Collapsed;
        EmptyView.Visibility = Visibility.Visible;
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        _emptyAction = action;
        SetupButton.Visibility = button is null ? Visibility.Collapsed : Visibility.Visible;
        if (button is not null && SetupButton.Content is TextBlock label) label.Text = button;
    }

    void RenderNow(WeatherForecast f, WeatherSettings s)
    {
        var service = WeatherService.Shared;
        var fresh = DateTime.UtcNow - f.FetchedUtc < TimeSpan.FromMinutes(45);
        var now = service.Current ?? new WeatherDisplay(f.Now.Temperature, f.Now.Code, f.Now.IsDay);

        var iconKey = $"{now.Code}:{now.IsDay}";
        if (iconKey != _iconKey)
        {
            _iconKey = iconKey;
            NowIcon.Content = WeatherIcons.Create(now.Code, now.IsDay, 50);
        }
        NowTemp.Text = FormatTemperature(now.Temperature, signed: true);
        NowCondition.Text = now.Description;
        NowDetails.Text = fresh
            ? $"Ощущается как {FormatTemperature(f.Now.Apparent, signed: true)} · ветер {f.Now.Wind:0} м/с · влажность {f.Now.Humidity}%"
            : "Прогноз на этот час";

        var rain = service.RainSummary();
        RainText.Text = rain ?? string.Empty;
        RainText.Visibility = RainIcon.Visibility = rain is null ? Visibility.Collapsed : Visibility.Visible;
        var wetSoon = rain is not null && !rain.StartsWith("Без", StringComparison.Ordinal);
        RainIcon.SetResourceReference(TextBlock.ForegroundProperty, wetSoon ? "AccentBrush" : "MutedBrush");
        RainText.SetResourceReference(TextBlock.ForegroundProperty, wetSoon ? "TextBrush" : "MutedBrush");

        PlaceText.Text = s.Place ?? string.Empty;
        PlaceText.ToolTip = string.IsNullOrEmpty(s.PlaceDetails) ? s.Place : $"{s.Place}, {s.PlaceDetails}";
        UpdatedText.Text = service.IsRefreshing ? "Обновляю…"
            : service.LastFailed ? $"Нет связи · данные {Ago(f.FetchedUtc)}"
            : $"Обновлено {Ago(f.FetchedUtc)}";
        RefreshButton.IsEnabled = !service.IsRefreshing;
    }

    void RenderHours(WeatherForecast f)
    {
        var hours = WeatherService.Shared.NextHours(24);
        // Rebuilt only when the hour moves on or a new forecast arrives, not on every minute tick.
        var key = $"{f.FetchedUtc.Ticks}:{hours.FirstOrDefault()?.Time}";
        if (key == _hoursKey) return;
        _hoursKey = key;
        HoursPanel.Children.Clear();
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var h in hours)
        {
            var local = f.LocalTime(h.Time);
            var isNow = h.Time <= nowUnix && nowUnix < h.Time + 3600;
            var label = new TextBlock
            {
                Text = isNow ? "Сейчас" : local.ToString("HH:mm", Ru),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            // Midnight starts a new day: the accent marks it, the tooltip names it.
            label.SetResourceReference(TextBlock.ForegroundProperty, local.Hour == 0 && !isNow ? "AccentBrush" : isNow ? "TextBrush" : "MutedBrush");
            var cell = new StackPanel
            {
                Width = 46,
                ToolTip = $"{Capitalize(local.ToString("dddd, d MMMM, HH:mm", Ru))}: {OpenMeteo.Describe(h.Code)}",
                Children =
                {
                    label,
                    WithMargin(WeatherIcons.Create(h.Code, h.IsDay, 22), new Thickness(0, 4, 0, 3)),
                    new TextBlock { Text = FormatTemperature(h.Temperature, signed: false), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    Chance(h.PrecipitationChance),
                },
            };
            HoursPanel.Children.Add(cell);
        }
        HoursScroll.ScrollToHorizontalOffset(0);
    }

    void RenderDays(WeatherForecast f)
    {
        var days = WeatherService.Shared.NextDays(7);
        var key = $"{f.FetchedUtc.Ticks}:{days.FirstOrDefault()?.Date:yyyyMMdd}";
        if (key == _daysKey) return;
        _daysKey = key;
        DaysPanel.Children.Clear();
        DaysPanel.Columns = Math.Max(1, days.Count);
        var today = f.LocalNow.Date;
        foreach (var d in days)
        {
            var name = d.Date == today ? "Сегодня" : d.Date == today.AddDays(1) ? "Завтра" : Capitalize(d.Date.ToString("ddd, d", Ru));
            var range = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            range.Children.Add(new TextBlock { Text = FormatTemperature(d.Max, signed: false), FontSize = 14, FontWeight = FontWeights.SemiBold });
            range.Children.Add(new TextBlock { Style = (Style)FindResource("MutedText"), Text = FormatTemperature(d.Min, signed: false), FontSize = 12.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom });
            var cell = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"{Capitalize(d.Date.ToString("dddd, d MMMM", Ru))}: {OpenMeteo.Describe(d.Code)}, {FormatTemperature(d.Min, true)}…{FormatTemperature(d.Max, true)}",
                Children =
                {
                    new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
                    WithMargin(WeatherIcons.Create(d.Code, true, 36), new Thickness(0, 8, 0, 7)),
                    range,
                    Chance(d.PrecipitationChance),
                },
            };
            DaysPanel.Children.Add(cell);
        }
    }

    /// <summary>Chance of rain under a cell; an empty line below 20% keeps the cells aligned.</summary>
    TextBlock Chance(int? percent)
    {
        var t = new TextBlock { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        t.Text = percent is int p && p >= 20 ? $"{p}%" : " ";
        if (percent is int q && q >= 20) t.ToolTip = $"Вероятность осадков {q}%";
        return t;
    }

    static FrameworkElement WithMargin(FrameworkElement e, Thickness margin)
    {
        e.Margin = margin;
        e.HorizontalAlignment = HorizontalAlignment.Center;
        return e;
    }

    /// <summary>"5°", "−3°" (a real minus sign); with <paramref name="signed"/> "+5°" as in Russian forecasts.</summary>
    public static string FormatTemperature(double t, bool signed)
    {
        var n = (int)Math.Round(t, MidpointRounding.AwayFromZero);
        return n < 0 ? $"−{-n}°" : signed && n > 0 ? $"+{n}°" : $"{n}°";
    }

    static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age < TimeSpan.FromMinutes(1)) return "только что";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} мин назад";
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? $"в {local:HH:mm}" : $"{local:dd.MM} в {local:HH:mm}";
    }

    static string Capitalize(string s) => s.Length > 0 ? char.ToUpper(s[0], Ru) + s[1..] : s;

    void OnHoursWheel(object sender, MouseWheelEventArgs e)
    {
        if (HoursScroll.ScrollableWidth <= 0) return;
        HoursScroll.ScrollToHorizontalOffset(HoursScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    void RefreshButton_Click(object sender, RoutedEventArgs e) => WeatherService.Shared.RefreshNow();

    void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        // The gear next to the place always opens settings; the empty-state button does what its label says.
        if (ReferenceEquals(sender, SetupButton) && _emptyAction is { } action) action();
        else OpenSettings();
    }

    void OpenSettings() => (Window.GetWindow(this) as Glint.Pill.PillWindow)?.ShowWeatherSettings();
}
