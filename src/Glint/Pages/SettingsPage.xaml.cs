using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Glint.Core;
using Glint.Services;

namespace Glint.Pages;

public partial class SettingsPage : UserControl
{
    const string DefaultHotkey = "Alt+Space";
    const string GitHubUrl = "https://github.com/sempik/glint";

    readonly ObservableCollection<string> _hiddenApps = new();
    readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    // Live preview while dragging the opacity slider, without rebuilding brushes on every pixel.
    readonly DispatcherTimer _themeDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };

    // Sliders raise ValueChanged while InitializeComponent applies Minimum/Maximum; ignore those.
    bool _loading = true;
    bool _scaleDragging;
    bool _capturing;

    public SettingsPage()
    {
        InitializeComponent();

        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); Save(); };
        // The panel swaps pages out; don't lose a pending change.
        Unloaded += (_, _) => { if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); Save(); } };

        HiddenAppsList.ItemsSource = _hiddenApps;
        _hiddenApps.CollectionChanged += (_, _) => UpdateHiddenAppsEmpty();

        VersionText.Text = $"Glint {GetVersion()}";
        _themeDebounce.Tick += (_, _) => { _themeDebounce.Stop(); ThemeService.Apply(S); };
        BuildThemeControls();
        LoadFromSettings();
        _loading = false;
    }

    static AppSettings S => AppServices.Settings.Current;

    void LoadFromSettings()
    {
        var s = S;
        ScaleSlider.Value = Math.Round(Math.Clamp(s.Scale, 0.8, 2.0) * 10) * 10;
        ScaleValue.Text = $"{ScaleSlider.Value:0}%";

        HoverSlider.Value = Math.Clamp(s.HoverDelayMs, 0, 600);
        HoverValue.Text = $"{HoverSlider.Value:0} мс";

        ClipSlider.Value = Math.Clamp(s.ClipboardHistoryLimit, 50, 1000);
        ClipValue.Text = $"{ClipSlider.Value:0}";

        FullscreenToggle.IsChecked = s.HideInFullscreen;
        // The registry is the source of truth; the user may have removed the entry elsewhere.
        AutostartToggle.IsChecked = Autostart.IsEnabled;

        HotkeyBox.Text = s.CommandBarHotkey;

        LoadThemeFromSettings();

        _hiddenApps.Clear();
        foreach (var app in s.HiddenInApps) _hiddenApps.Add(app);
        UpdateHiddenAppsEmpty();
    }

    void Save()
    {
        try
        {
            AppServices.Settings.Save();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void SaveSoon()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    static string GetVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.1.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }

    // ───────────── Оформление ─────────────

    static readonly string[] QuickColors =
    {
        "#F87171", "#FB923C", "#FBBF24", "#A3E635", "#22C55E", "#2DD4BF",
        "#22D3EE", "#3B82F6", "#818CF8", "#A78BFA", "#F472B6", "#E5E7EB",
    };

    readonly List<(string Id, Border Ring, TextBlock Check, TextBlock Label)> _presetViews = new();
    readonly List<(string Id, Border Ring, TextBlock Label)> _tintViews = new();
    // Which accent a quick dot sets: the HEX box that last had focus, Акцент 1 by default.
    bool _dotsTargetSecond;

    void BuildThemeControls()
    {
        foreach (var preset in ThemeService.Presets)
        {
            var check = new TextBlock
            {
                Style = (Style)FindResource("Icon"),
                Text = Glyphs.Check,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                // Dark check on light presets (Моно), white otherwise.
                Foreground = IsLight(preset.Accent) ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White,
                Visibility = Visibility.Collapsed,
            };
            var fill = new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = ThemeService.Gradient(preset.Accent, preset.Accent2),
                Child = check,
            };
            var ring = new Border
            {
                Width = 52,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                Child = fill,
            };
            var label = new TextBlock { Text = preset.Name, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            var button = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Width = 76,
                Padding = new Thickness(0, 2, 0, 2),
                ToolTip = $"{preset.Name}: {preset.Accent} → {preset.Accent2}",
                Content = new StackPanel { Children = { ring, label } },
            };
            var p = preset;
            button.Click += (_, _) =>
            {
                ThemeService.ApplyPreset(S, p);
                CommitTheme(refreshBoxes: true);
            };
            PresetPanel.Children.Add(button);
            _presetViews.Add((preset.Id, ring, check, label));
        }

        foreach (var hex in QuickColors)
        {
            var dot = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = ThemeService.Gradient(hex, hex),
            };
            var button = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = hex,
                Content = dot,
            };
            var h = hex;
            button.Click += (_, _) =>
            {
                if (_dotsTargetSecond) S.Accent2Hex = h;
                else S.AccentHex = h;
                S.ThemePreset = ThemeService.CustomPreset;
                CommitTheme(refreshBoxes: true);
            };
            QuickDotsPanel.Children.Add(button);
        }

        foreach (var tint in ThemeService.Tints)
        {
            var dot = new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = new System.Windows.Media.SolidColorBrush(tint.Color),
                BorderThickness = new Thickness(1),
                BorderBrush = (System.Windows.Media.Brush)FindResource("DividerBrush"),
            };
            var ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                Child = dot,
            };
            var label = new TextBlock { Text = tint.Name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            var button = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Width = 64,
                Padding = new Thickness(0, 2, 0, 2),
                Content = new StackPanel { Children = { ring, label } },
            };
            var id = tint.Id;
            button.Click += (_, _) =>
            {
                // The tint is part of a preset; CommitTheme re-matches the preset (or marks it custom).
                S.SurfaceTint = id;
                CommitTheme(refreshBoxes: false);
            };
            TintPanel.Children.Add(button);
            _tintViews.Add((id, ring, label));
        }
        UpdateDotsTarget();
    }

    static bool IsLight(string hex) =>
        ThemeService.TryParseHex(hex, out var c) && (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255 > 0.7;

    void LoadThemeFromSettings()
    {
        var s = S;
        Accent1Box.Text = s.AccentHex;
        Accent2Box.Text = s.Accent2Hex;
        OpacitySlider.Value = Math.Round(Math.Clamp(s.SurfaceOpacity, ThemeService.MinSurfaceOpacity, ThemeService.MaxSurfaceOpacity) * 100);
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        UpdateThemeSelection();
    }

    /// <summary>Applies the theme right away, saves, and refreshes the swatch selection.</summary>
    void CommitTheme(bool refreshBoxes)
    {
        _themeDebounce.Stop();
        // Hand-picked colors that happen to equal a preset light up that preset again.
        S.ThemePreset = ThemeService.MatchPreset(S);
        ThemeService.Apply(S);
        // Stale invalid text is dropped too, so the box never disagrees with the reset hint.
        if (refreshBoxes || !ThemeService.TryParseHex(Accent1Box.Text, out _)) Accent1Box.Text = S.AccentHex;
        if (refreshBoxes || !ThemeService.TryParseHex(Accent2Box.Text, out _)) Accent2Box.Text = S.Accent2Hex;
        ResetColorHint();
        UpdateThemeSelection();
        _saveDebounce.Stop();
        Save();
    }

    const string ColorHintText = "Введите HEX и нажмите Enter или выберите точку";

    void ResetColorHint()
    {
        ColorHint.Text = ColorHintText;
        ColorHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
    }

    void ShowColorError(string text)
    {
        ColorHint.Text = text;
        ColorHint.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
    }

    void UpdateThemeSelection()
    {
        var text = (System.Windows.Media.Brush)FindResource("TextBrush");
        var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
        var clear = System.Windows.Media.Brushes.Transparent;
        foreach (var (id, ring, check, label) in _presetViews)
        {
            var on = id == S.ThemePreset;
            ring.BorderBrush = on ? text : clear;
            check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            label.Foreground = on ? text : muted;
        }
        foreach (var (id, ring, label) in _tintViews)
        {
            var on = id == S.SurfaceTint;
            ring.BorderBrush = on ? text : clear;
            label.Foreground = on ? text : muted;
        }
    }

    void UpdateDotsTarget()
    {
        QuickDotsTarget.Text = _dotsTargetSecond ? "→ Акцент 2" : "→ Акцент 1";
        var text = (System.Windows.Media.Brush)FindResource("TextBrush");
        Accent1Swatch.BorderBrush = _dotsTargetSecond ? System.Windows.Media.Brushes.Transparent : text;
        Accent2Swatch.BorderBrush = _dotsTargetSecond ? text : System.Windows.Media.Brushes.Transparent;
    }

    void AccentBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _dotsTargetSecond = ReferenceEquals(sender, Accent2Box);
        UpdateDotsTarget();
    }

    void AccentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || sender is not TextBox box) return;
        // Preview the typed color on the swatch only; the theme changes on Enter / focus loss.
        var swatch = ReferenceEquals(box, Accent2Box) ? Accent2Swatch : Accent1Swatch;
        if (ThemeService.TryParseHex(box.Text, out var c))
            swatch.Background = new System.Windows.Media.SolidColorBrush(c);
        else
            swatch.SetResourceReference(Border.BackgroundProperty, ReferenceEquals(box, Accent2Box) ? "Accent2Brush" : "AccentBrush");
    }

    void AccentBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitAccentBox(box);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            box.Text = ReferenceEquals(box, Accent2Box) ? S.Accent2Hex : S.AccentHex;
            ResetColorHint();
        }
    }

    void AccentBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) CommitAccentBox(box);
    }

    void CommitAccentBox(TextBox box)
    {
        var second = ReferenceEquals(box, Accent2Box);
        var current = second ? S.Accent2Hex : S.AccentHex;
        if (!ThemeService.TryParseHex(box.Text, out var c))
        {
            if (box.Text.Trim().Length == 0)
            {
                box.Text = current;
                return;
            }
            ShowColorError("Неверный цвет: нужен #RGB или #RRGGBB");
            return;
        }
        var hex = ThemeService.ToHex(c);
        box.Text = hex;
        if (string.Equals(hex, current, StringComparison.OrdinalIgnoreCase))
        {
            ResetColorHint();
            return;
        }
        if (!ThemeService.IsReadableAccent(c, S.SurfaceTint))
        {
            // Accent is used as text and icon color; a too-dark one disappears on the surface.
            ShowColorError("Слишком тёмный цвет: плохо виден на фоне");
            return;
        }
        if (second) S.Accent2Hex = hex;
        else S.AccentHex = hex;
        S.ThemePreset = ThemeService.CustomPreset;
        CommitTheme(refreshBoxes: false);
    }

    void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue is null) return;
        OpacityValue.Text = $"{e.NewValue:0}%";
        if (_loading) return;
        S.SurfaceOpacity = Math.Clamp(e.NewValue / 100.0, ThemeService.MinSurfaceOpacity, ThemeService.MaxSurfaceOpacity);
        _themeDebounce.Stop();
        _themeDebounce.Start();
        SaveSoon();
    }

    void ThemeReset_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Reset(S);
        _loading = true;
        OpacitySlider.Value = Math.Round(S.SurfaceOpacity * 100);
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        _loading = false;
        CommitTheme(refreshBoxes: true);
    }

    // ───────────── Вид ─────────────

    void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleValue is null) return;
        ScaleValue.Text = $"{e.NewValue:0}%";
        if (_loading) return;
        S.Scale = Math.Clamp(e.NewValue / 100.0, 0.8, 2.0);
        // Saving resizes the window; doing that mid-drag would move the slider under the cursor.
        if (!_scaleDragging) SaveSoon();
    }

    void ScaleSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _scaleDragging = true;
        _saveDebounce.Stop();
    }

    void ScaleSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _scaleDragging = false;
        if (_loading) return;
        _saveDebounce.Stop();
        Save();
    }

    void HoverSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HoverValue is null) return;
        HoverValue.Text = $"{e.NewValue:0} мс";
        if (_loading) return;
        S.HoverDelayMs = (int)Math.Round(e.NewValue);
        SaveSoon();
    }

    // ───────────── Поведение ─────────────

    void FullscreenToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.HideInFullscreen = FullscreenToggle.IsChecked == true;
        Save();
    }

    void AutostartToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Autostart.Set(AutostartToggle.IsChecked == true);
        Save();

        // Reflect what actually happened if the registry write failed.
        var actual = Autostart.IsEnabled;
        if ((AutostartToggle.IsChecked == true) != actual)
        {
            _loading = true;
            AutostartToggle.IsChecked = actual;
            _loading = false;
        }
    }

    void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _capturing = true;
        HotkeyBox.Text = "Нажмите сочетание…";
        HotkeyHint.Text = "Esc — отмена";
        HotkeyHint.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _capturing = false;
        HotkeyBox.Text = S.CommandBarHotkey;
        if (HotkeyHint.Text == "Esc — отмена")
            HotkeyHint.Text = "Кликните по полю и нажмите новое сочетание";
    }

    void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        // Let Tab move focus normally.
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;
        e.Handled = true;

        if (key == Key.Escape)
        {
            EndCapture();
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return;

        var mods = Keyboard.Modifiers;
        var parts = new List<string>(5);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        // A bare letter as a global hotkey would swallow normal typing everywhere.
        var isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (parts.Count == 0 && !isFunctionKey)
        {
            ShowHotkeyError("Нужен модификатор: Ctrl, Alt, Shift или Win");
            return;
        }

        parts.Add(key.ToString());
        var gesture = string.Join("+", parts);

        if (!HotkeyManager.TryParse(gesture, out _, out _))
        {
            ShowHotkeyError("Это сочетание не поддерживается");
            return;
        }

        S.CommandBarHotkey = gesture;
        Save();
        HotkeyHint.Text = "Кликните по полю и нажмите новое сочетание";
        EndCapture();
    }

    void ShowHotkeyError(string message)
    {
        HotkeyHint.Text = message;
        HotkeyHint.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
    }

    void EndCapture()
    {
        _capturing = false;
        HotkeyBox.Text = S.CommandBarHotkey;
        // Move focus off the box so the next click starts a fresh capture.
        Keyboard.ClearFocus();
    }

    void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        HotkeyHint.Text = "Кликните по полю и нажмите новое сочетание";
        HotkeyHint.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        if (S.CommandBarHotkey == DefaultHotkey) return;
        S.CommandBarHotkey = DefaultHotkey;
        HotkeyBox.Text = DefaultHotkey;
        Save();
    }

    // ───────────── Буфер обмена ─────────────

    void ClipSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ClipValue is null) return;
        ClipValue.Text = $"{e.NewValue:0}";
        if (_loading) return;
        S.ClipboardHistoryLimit = (int)Math.Round(e.NewValue);
        SaveSoon();
    }

    // ───────────── Скрывать в приложениях ─────────────

    void UpdateHiddenAppsEmpty()
    {
        HiddenAppsEmpty.Visibility = _hiddenApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenAppsList.Visibility = _hiddenApps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void HiddenAppInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddHiddenApp();
    }

    void AddHiddenApp_Click(object sender, RoutedEventArgs e) => AddHiddenApp();

    void AddHiddenApp()
    {
        var name = NormalizeProcessName(HiddenAppInput.Text);
        HiddenAppInput.Text = string.Empty;
        if (name.Length == 0) return;
        if (S.HiddenInApps.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))) return;

        S.HiddenInApps.Add(name);
        _hiddenApps.Add(name);
        Save();
    }

    static string NormalizeProcessName(string? input)
    {
        var name = (input ?? string.Empty).Trim().Trim('"');
        // Accept a pasted full path too.
        if (name.IndexOfAny(new[] { '\\', '/' }) >= 0) name = System.IO.Path.GetFileName(name);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Trim();
    }

    void RemoveHiddenApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string name) return;
        S.HiddenInApps.RemoveAll(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        _hiddenApps.Remove(name);
        Save();
    }

    // ───────────── О программе ─────────────

    void GitHub_Click(object sender, RoutedEventArgs e) => Open(GitHubUrl);

    void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsStore.DataDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
