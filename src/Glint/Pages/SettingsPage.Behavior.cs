using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glint.Core;
using Glint.Services;

namespace Glint.Pages;

/// <summary>Theme mode (dark / light / follow Windows), auto-hide, click-through and the automatic update check.</summary>
public partial class SettingsPage
{
    readonly List<(string Id, RadioButton Button)> _modeButtons = new();

    void BuildThemeModeButtons()
    {
        foreach (var (id, name) in ThemeService.ThemeModes)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("TabButton"),
                GroupName = "ThemeMode",
                Content = new TextBlock { Text = name, FontSize = 12 },
                ToolTip = id == ThemeService.SystemMode ? "Светлая или тёмная — как выбрано для приложений в Windows" : null,
            };
            // The template's ContentPresenter sets the foreground; the implicit TextBlock style would override it.
            ((TextBlock)button.Content).SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
            var mode = id;
            button.Checked += (_, _) =>
            {
                if (_loading || S.ThemeMode == mode) return;
                S.ThemeMode = mode;
                CommitTheme(refreshBoxes: false);
                UpdateTintDots();
            };
            ThemeModePanel.Children.Add(button);
            _modeButtons.Add((id, button));
        }
    }

    void LoadBehaviorSettings()
    {
        var s = S;
        foreach (var (id, button) in _modeButtons) button.IsChecked = id == s.ThemeMode;
        UpdateTintDots();
        AutoHideToggle.IsChecked = s.AutoHide;
        ClickThroughToggle.IsChecked = s.ClickThrough;
        AutoUpdateToggle.IsChecked = s.AutoUpdateCheck;
        CurrencyToggle.IsChecked = s.CurrencyRates;
    }

    /// <summary>The background swatches show the shade that is actually used: dark tints, or their light twins.</summary>
    void UpdateTintDots()
    {
        var light = ThemeService.IsLight;
        for (var i = 0; i < _tintViews.Count && i < ThemeService.Tints.Count; i++)
        {
            var tint = ThemeService.Tints.First(t => t.Id == _tintViews[i].Id);
            if (_tintViews[i].Ring.Child is Border dot) dot.Background = new SolidColorBrush(light ? tint.Light : tint.Color);
        }
    }

    void BehaviorToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox toggle) return;
        // Only the clicked toggle: the others may be stale if the tray or the command bar changed them meanwhile.
        var on = toggle.IsChecked == true;
        if (ReferenceEquals(toggle, AutoHideToggle)) S.AutoHide = on;
        else if (ReferenceEquals(toggle, ClickThroughToggle)) S.ClickThrough = on;
        else if (ReferenceEquals(toggle, AutoUpdateToggle)) S.AutoUpdateCheck = on;
        else if (ReferenceEquals(toggle, CurrencyToggle)) S.CurrencyRates = on;
        Save();
        if (ReferenceEquals(toggle, ClickThroughToggle) && on)
            AppServices.Notifier.Flash("Клики проходят сквозь пилюлю", "Ctrl + наведение — вернуть; настройки — в трее", Glyphs.Info, TimeSpan.FromSeconds(3.5));
    }

    // ───────────── Changes made outside this page ─────────────

    // The pill caches pages, and the tray, the command bar, the Пульт theme tile and Windows (in "system" mode)
    // change these settings too, so the page re-reads them while it is shown.
    void WatchOutsideChanges()
    {
        Loaded += (_, _) =>
        {
            AppServices.Settings.Changed -= OnSettingsChanged;
            AppServices.Settings.Changed += OnSettingsChanged;
            ThemeService.ThemeChanged -= OnThemeChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
            SyncBehaviorSettings();
        };
        Unloaded += (_, _) =>
        {
            AppServices.Settings.Changed -= OnSettingsChanged;
            ThemeService.ThemeChanged -= OnThemeChanged;
        };
    }

    void OnSettingsChanged(object? sender, EventArgs e) => SyncBehaviorSettings();

    void OnThemeChanged(object? sender, EventArgs e) => UpdateTintDots();

    void SyncBehaviorSettings()
    {
        var s = S;
        var wasLoading = _loading;
        _loading = true;
        foreach (var (id, button) in _modeButtons)
            if ((button.IsChecked == true) != (id == s.ThemeMode)) button.IsChecked = id == s.ThemeMode;
        SyncToggle(AutoHideToggle, s.AutoHide);
        SyncToggle(ClickThroughToggle, s.ClickThrough);
        SyncToggle(AutoUpdateToggle, s.AutoUpdateCheck);
        SyncToggle(CurrencyToggle, s.CurrencyRates);
        _loading = wasLoading;
        UpdateThemeSelection();
        UpdateTintDots();
    }

    static void SyncToggle(CheckBox toggle, bool value)
    {
        if ((toggle.IsChecked == true) != value) toggle.IsChecked = value;
    }

    // ───────────── Update found in the background ─────────────

    void WatchAvailableUpdate()
    {
        Loaded += (_, _) =>
        {
            UpdateService.AvailableChanged -= OnAvailableChanged;
            UpdateService.AvailableChanged += OnAvailableChanged;
            ShowKnownUpdate();
        };
        Unloaded += (_, _) => UpdateService.AvailableChanged -= OnAvailableChanged;
    }

    void OnAvailableChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ShowKnownUpdate);

    /// <summary>An update found by the automatic check turns the button into "Обновить до …" right away.</summary>
    void ShowKnownUpdate()
    {
        if (_updating || UpdateService.Available is not { } update || _update?.Version == update.Version) return;
        _update = update;
        UpdateStatus.Text = $"Доступна версия {update.Version.ToString(3)}, у вас {GetVersion()}";
        UpdateButtonText.Text = UpdateService.CanSelfUpdate && !_pageOnly ? $"Обновить до {update.Version.ToString(3)}" : "Открыть страницу релиза";
    }

    /// <summary>Scrolls to the update card; the pill calls this when the "Доступно обновление" flash is clicked.</summary>
    public void RevealUpdate()
    {
        ShowKnownUpdate();
        // The page may have just been created; wait for layout so the card has a position.
        Dispatcher.BeginInvoke(() => UpdateCard.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
