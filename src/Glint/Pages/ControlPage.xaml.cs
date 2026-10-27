using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Glint.Core;
using Glint.Services;
using Glint.Services.Control;
using Windows.Devices.Radios;

namespace Glint.Pages;

/// <summary>
/// Пульт: Wi-Fi and Bluetooth switches, Windows and Glint theme, Bluetooth headphones and Windows VPN
/// connections. Everything is read only while the tab is on screen: radios report their own changes, the
/// lists are re-read every few seconds (two small phonebook files and one Bluetooth query without a radio inquiry).
/// </summary>
public partial class ControlPage : UserControl
{
    static readonly TimeSpan ListRefresh = TimeSpan.FromSeconds(4);

    readonly DispatcherTimer _refresh = new() { Interval = ListRefresh };
    readonly HashSet<string> _busy = new();
    bool _watching;
    bool _radioBusy;
    string? _audioSignature;
    string? _vpnSignature;

    public ControlPage()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => RefreshLists();
        // The pill caches pages and hides the panel instead of unloading it, so watch visibility too.
        Loaded += (_, _) => UpdateWatching();
        Unloaded += (_, _) => UpdateWatching();
        IsVisibleChanged += (_, _) => UpdateWatching();
    }

    void UpdateWatching()
    {
        var wanted = IsLoaded && IsVisible;
        if (wanted == _watching) return;
        _watching = wanted;
        if (wanted)
        {
            RadioControl.Shared.Changed += OnRadioChanged;
            RadioControl.Shared.Listen();
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
            _refresh.Start();
            RefreshRadios();
            RefreshLists();
            UpdateThemeTiles();
        }
        else
        {
            RadioControl.Shared.Changed -= OnRadioChanged;
            RadioControl.Shared.Unlisten();
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            ThemeService.ThemeChanged -= OnThemeChanged;
            _refresh.Stop();
        }
    }

    void OnRadioChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        RefreshRadios();
        // Paired headphones vanish and return with the Bluetooth radio; the empty-list text depends on it too.
        _audioSignature = null;
        RefreshAudio();
    });

    void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshVpn);

    void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateThemeTiles);

    void OnThemeChanged(object? sender, EventArgs e) => UpdateThemeTiles();

    // ───────────── Radios ─────────────

    async void RefreshRadios()
    {
        try
        {
            var wifi = await RadioControl.Shared.GetAsync(RadioKind.WiFi);
            var bt = await RadioControl.Shared.GetAsync(RadioKind.Bluetooth);
            ShowRadio(WifiTile, WifiIcon, WifiStatus, wifi);
            ShowRadio(BluetoothTile, BluetoothIcon, BluetoothStatus, bt);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static void ShowRadio(Button tile, TextBlock icon, TextBlock status, RadioInfo info)
    {
        status.Text = info.Problem ?? (info.On ? "Включён" : "Выключен");
        tile.ToolTip = info.Problem;
        // A problem always disables the tile, and the trimmed status line may not fit the whole text.
        ToolTipService.SetShowOnDisabled(tile, true);
        tile.IsEnabled = info.Present && info.CanToggle;
        SetTileOn(tile, icon, info.On);
    }

    /// <summary>An "on" tile gets the accent icon and the brighter card fill, like a pressed switch.</summary>
    static void SetTileOn(Button tile, TextBlock icon, bool on)
    {
        icon.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentBrush" : "MutedBrush");
        tile.SetResourceReference(BackgroundProperty, on ? "CardHoverBrush" : "CardBrush");
    }

    void WifiTile_Click(object sender, RoutedEventArgs e) => ToggleRadio(RadioKind.WiFi, WifiStatus);

    void BluetoothTile_Click(object sender, RoutedEventArgs e) => ToggleRadio(RadioKind.Bluetooth, BluetoothStatus);

    async void ToggleRadio(RadioKind kind, TextBlock status)
    {
        if (_radioBusy) return;
        _radioBusy = true;
        try
        {
            var current = await RadioControl.Shared.GetAsync(kind);
            if (!current.Present || !current.CanToggle) return;
            status.Text = current.On ? "Выключаю…" : "Включаю…";
            var error = await RadioControl.Shared.SetAsync(kind, !current.On);
            RefreshRadios();
            if (error is not null) AppServices.Notifier.Flash(kind == RadioKind.WiFi ? "Wi-Fi" : "Bluetooth", error, Glyphs.Info, TimeSpan.FromSeconds(3));
            // Paired headphones appear or vanish with the Bluetooth radio.
            if (kind == RadioKind.Bluetooth)
            {
                _audioSignature = null;
                RefreshAudio();
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            _radioBusy = false;
        }
    }

    // ───────────── Theme ─────────────

    void UpdateThemeTiles()
    {
        var windowsLight = WindowsTheme.AppsUseLight;
        WindowsThemeIcon.Text = windowsLight ? Glyphs.Sun : Glyphs.Moon;
        WindowsThemeStatus.Text = windowsLight ? "Светлая" : "Тёмная";
        SetTileOn(WindowsThemeTile, WindowsThemeIcon, true);

        var mode = AppServices.Settings.Current.ThemeMode;
        GlintThemeStatus.Text = ThemeService.ThemeModes.FirstOrDefault(m => m.Id == mode).Name ?? "Тёмная";
        GlintThemeIcon.Text = ThemeService.IsLight ? Glyphs.Sun : Glyphs.Moon;
        SetTileOn(GlintThemeTile, GlintThemeIcon, true);
    }

    void WindowsThemeTile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowsTheme.Set(!WindowsTheme.AppsUseLight);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppServices.Notifier.Flash("Не удалось сменить тему Windows", null, Glyphs.Info, TimeSpan.FromSeconds(3));
        }
        // The registry is already written; Glint in "как в Windows" mode follows on the broadcast.
        UpdateThemeTiles();
    }

    void GlintThemeTile_Click(object sender, RoutedEventArgs e)
    {
        var s = AppServices.Settings.Current;
        var modes = ThemeService.ThemeModes;
        var index = modes.ToList().FindIndex(m => m.Id == s.ThemeMode);
        s.ThemeMode = modes[(index + 1) % modes.Count].Id;
        try
        {
            AppServices.Settings.Save();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        UpdateThemeTiles();
    }

    // ───────────── Lists ─────────────

    void RefreshLists()
    {
        RefreshAudio();
        RefreshVpn();
    }

    async void RefreshAudio()
    {
        try
        {
            var devices = await Task.Run(BluetoothAudio.List);
            if (!_watching) return;
            var signature = string.Join("|", devices.Select(d => $"{d.Address}:{d.Name}:{d.Connected}")) + "|" + string.Join(",", _busy);
            if (signature == _audioSignature) return;
            _audioSignature = signature;
            AudioList.Children.Clear();
            foreach (var d in devices) AudioList.Children.Add(BuildAudioRow(d));
            AudioEmpty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (devices.Count == 0)
            {
                // With the radio off Windows lists nothing, which isn't the same as having nothing paired.
                var bt = await RadioControl.Shared.GetAsync(RadioKind.Bluetooth);
                AudioEmpty.Text = !bt.Present ? "Bluetooth на этом компьютере не найден"
                    : !bt.On ? "Bluetooth выключен — включите его плиткой выше"
                    : "Нет сопряжённых Bluetooth-наушников или колонок";
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    async void RefreshVpn()
    {
        try
        {
            var profiles = await Task.Run(VpnProfiles.List);
            if (!_watching) return;
            var signature = string.Join("|", profiles.Select(p => $"{p.Name}:{p.Connected}")) + "|" + string.Join(",", _busy);
            if (signature == _vpnSignature) return;
            _vpnSignature = signature;
            VpnList.Children.Clear();
            foreach (var p in profiles) VpnList.Children.Add(BuildVpnRow(p));
            VpnEmpty.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    FrameworkElement BuildAudioRow(BluetoothAudioDevice d)
    {
        var key = $"bt:{d.Address}";
        var status = _busy.Contains(key) ? (d.Connected ? "Отключаю…" : "Подключаю…") : d.Connected ? "Подключено" : "Не подключено";
        return BuildRow(d.IsHeadset ? Glyphs.Headphones : Glyphs.Speaker, d.Name, status, d.Connected, key, () =>
        {
            var error = BluetoothAudio.SetConnected(d.Address, !d.Connected);
            // The driver answers at once and connects in the background; give it a moment before re-reading.
            if (error is null) Thread.Sleep(d.Connected ? 1000 : 2500);
            return error;
        }, AudioHint, RefreshAudio);
    }

    FrameworkElement BuildVpnRow(VpnProfile p)
    {
        var key = $"vpn:{p.Name}";
        var status = _busy.Contains(key) ? (p.Connected ? "Отключаю…" : "Подключаю…") : p.Connected ? "Подключено" : "Отключено";
        return BuildRow(Glyphs.Vpn, p.Name, status, p.Connected, key, () => VpnProfiles.SetConnected(p, !p.Connected), VpnHint, RefreshVpn);
    }

    /// <summary>Icon · name over status · one button that connects or disconnects.</summary>
    FrameworkElement BuildRow(string glyph, string name, string status, bool connected, string key, Func<string?> toggle, TextBlock hint, Action refresh)
    {
        var busy = _busy.Contains(key);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock { Style = (Style)FindResource("Icon"), Text = glyph, FontSize = 15, Margin = new Thickness(2, 0, 10, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, connected ? "AccentBrush" : "MutedBrush");
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontSize = 12.5, ToolTip = name });
        text.Children.Add(new TextBlock { Style = (Style)FindResource("MutedText"), Text = status, FontSize = 11 });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var label = new TextBlock { Text = connected ? "Отключить" : "Подключить", FontSize = 12 };
        label.SetResourceReference(TextBlock.ForegroundProperty, connected ? "TextBrush" : "AccentBrush");
        var button = new Button
        {
            Style = (Style)FindResource("TileButton"),
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = !busy,
            Content = label,
        };
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);

        button.Click += async (_, _) =>
        {
            if (!_busy.Add(key)) return;
            hint.Visibility = Visibility.Collapsed;
            refresh();
            string? error;
            try
            {
                error = await Task.Run(toggle);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                error = "Не получилось";
            }
            _busy.Remove(key);
            if (error is not null)
            {
                hint.Text = $"{name}: {error}";
                hint.Visibility = Visibility.Visible;
            }
            refresh();
        };
        return grid;
    }

    // ───────────── Links ─────────────

    void BluetoothSettings_Click(object sender, RoutedEventArgs e) => OpenSettings("ms-settings:bluetooth");

    void VpnSettings_Click(object sender, RoutedEventArgs e) => OpenSettings("ms-settings:network-vpn");

    static void OpenSettings(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
