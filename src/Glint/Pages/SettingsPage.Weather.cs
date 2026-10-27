using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glint.Core;
using Glint.Services.Weather;

namespace Glint.Pages;

/// <summary>Настройки → Погода: on/off, city search (Open-Meteo geocoding, only on Найти), pill and rain options.</summary>
public partial class SettingsPage
{
    CancellationTokenSource? _citySearch;

    void BuildWeatherControls()
    {
        // A city chosen elsewhere or weather switched off from the tab: keep the card in step when it comes back.
        Loaded += (_, _) =>
        {
            _loading = true;
            LoadWeatherSettings();
            _loading = false;
        };
    }

    void LoadWeatherSettings()
    {
        var w = S.Weather;
        WeatherToggle.IsChecked = w.Enabled;
        WeatherPillToggle.IsChecked = w.ShowInPill;
        WeatherRainToggle.IsChecked = w.RainAlert;
        WeatherPlaceText.Text = w.HasPlace ? w.Place ?? "Выбранное место" : "Не выбран — найдите ниже";
        WeatherPlaceText.SetResourceReference(TextBlock.ForegroundProperty, w.HasPlace ? "TextBrush" : "MutedBrush");
        WeatherPlaceDetails.Text = w.PlaceDetails ?? string.Empty;
        WeatherPlaceDetails.Visibility = w.HasPlace && !string.IsNullOrEmpty(w.PlaceDetails) ? Visibility.Visible : Visibility.Collapsed;
        UpdateWeatherDependents();
    }

    /// <summary>The pill and rain options only matter while weather is on.</summary>
    void UpdateWeatherDependents()
    {
        foreach (var box in new[] { WeatherPillToggle, WeatherRainToggle })
        {
            box.IsEnabled = S.Weather.Enabled;
            box.Opacity = box.IsEnabled ? 1 : 0.45;
        }
    }

    void WeatherToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (WeatherToggle.IsChecked == true && !S.Weather.HasPlace)
        {
            // Nothing to show without a place: point at the search instead.
            _loading = true;
            WeatherToggle.IsChecked = false;
            _loading = false;
            ShowCityStatus("Сначала найдите и выберите город", error: true);
            CitySearchBox.Focus();
            return;
        }
        S.Weather.Enabled = WeatherToggle.IsChecked == true;
        UpdateWeatherDependents();
        Save();
    }

    void WeatherOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Weather.ShowInPill = WeatherPillToggle.IsChecked == true;
        S.Weather.RainAlert = WeatherRainToggle.IsChecked == true;
        Save();
    }

    // ───────────── City search ─────────────

    void CitySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Fires during InitializeComponent, before the placeholder exists.
        if (CitySearchPlaceholder is null) return;
        CitySearchPlaceholder.Visibility = CitySearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void CitySearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SearchCities();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // Let go of the search too, so its cancellation ends quietly instead of as a network error.
            _citySearch?.Cancel();
            _citySearch = null;
            CitySearchButton.IsEnabled = true;
            CitySearchBox.Clear();
            CityResults.Children.Clear();
            HideCityStatus();
        }
    }

    void CitySearch_Click(object sender, RoutedEventArgs e) => SearchCities();

    async void SearchCities()
    {
        var name = CitySearchBox.Text.Trim();
        if (name.Length < 2)
        {
            ShowCityStatus("Введите хотя бы две буквы", error: true);
            return;
        }
        _citySearch?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _citySearch = cts;
        CityResults.Children.Clear();
        ShowCityStatus("Ищу…", error: false);
        CitySearchButton.IsEnabled = false;
        try
        {
            var places = await WeatherService.SearchAsync(name, cts.Token);
            if (cts != _citySearch) return;
            if (places.Count == 0)
            {
                ShowCityStatus($"«{name}» не найден. Попробуйте иначе или латиницей", error: true);
                return;
            }
            HideCityStatus();
            foreach (var p in places) CityResults.Children.Add(BuildCityRow(p));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cts != _citySearch) return;
            ShowCityStatus("Не удалось связаться с Open-Meteo. Проверьте интернет", error: true);
        }
        finally
        {
            if (cts == _citySearch)
            {
                _citySearch = null;
                CitySearchButton.IsEnabled = true;
            }
            cts.Dispose();
        }
    }

    Button BuildCityRow(GeoPlace place)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = place.Name, FontSize = 13 });
        if (place.Details.Length > 0)
            text.Children.Add(new TextBlock { Style = (Style)FindResource("MutedText"), Text = place.Details, Margin = new Thickness(0, 1, 0, 0) });
        var button = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 5, 10, 5),
            Focusable = false,
            Content = text,
            ToolTip = $"{place.Latitude:0.##}, {place.Longitude:0.##}",
        };
        button.Click += (_, _) =>
        {
            WeatherService.ChoosePlace(place);
            CityResults.Children.Clear();
            CitySearchBox.Clear();
            _loading = true;
            LoadWeatherSettings();
            _loading = false;
            ShowCityStatus($"Выбрано: {place.Name}. Прогноз появится через пару секунд", error: false);
        };
        return button;
    }

    void ShowCityStatus(string text, bool error)
    {
        CitySearchStatus.Text = text;
        CitySearchStatus.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "MutedBrush");
        CitySearchStatus.Visibility = Visibility.Visible;
    }

    void HideCityStatus() => CitySearchStatus.Visibility = Visibility.Collapsed;

    /// <summary>Scrolls to the weather card; without a city the search box gets the focus (and keeps the panel open).</summary>
    public void RevealWeather()
    {
        Dispatcher.BeginInvoke(() =>
        {
            WeatherCard.BringIntoView();
            if (!S.Weather.HasPlace) CitySearchBox.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
