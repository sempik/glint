using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glint.Core;
using Glint.Services.Indicators;

namespace Glint.Pages;

/// <summary>
/// The Индикаторы card. Each toggle names its <see cref="IndicatorSettings"/> property in Tag, so adding an
/// indicator only needs a bool property and a CheckBox. IndicatorHub picks up the change on save.
/// </summary>
public partial class SettingsPage
{
    static IndicatorSettings Ind => AppServices.Settings.Current.Indicators;

    IEnumerable<CheckBox> IndicatorToggles =>
        LogicalTreeHelper.GetChildren(IndicatorsPanel).OfType<CheckBox>().Where(c => c.Tag is string);

    static PropertyInfo? IndicatorProperty(CheckBox box) =>
        box.Tag is string name ? typeof(IndicatorSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance) : null;

    void LoadIndicatorSettings()
    {
        foreach (var box in IndicatorToggles)
            if (IndicatorProperty(box) is { PropertyType: var t } p && t == typeof(bool)) box.IsChecked = (bool)p.GetValue(Ind)!;
        ObsPortBox.Text = Ind.ObsWebSocketPort.ToString();
        UpdateObsPasswordHint();
        LoadVpnIgnored();
        UpdateIndicatorDependents();
    }

    void IndicatorToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox box || IndicatorProperty(box) is not { } p) return;
        p.SetValue(Ind, box.IsChecked == true);
        UpdateIndicatorDependents();
        Save();
    }

    /// <summary>Sub-options only make sense while their parent indicator is on.</summary>
    void UpdateIndicatorDependents()
    {
        foreach (var box in IndicatorToggles)
        {
            box.IsEnabled = box.Tag switch
            {
                "VpnIpLookup" => Ind.Vpn,
                "ObsWebSocket" => Ind.Obs,
                "LyricsInPill" => Ind.Lyrics,
                _ => true,
            };
            box.Opacity = box.IsEnabled ? 1 : 0.45;
        }
        VpnIgnorePanel.IsEnabled = Ind.Vpn;
        VpnIgnorePanel.Opacity = Ind.Vpn ? 1 : 0.45;
        var obsSocket = Ind.Obs && Ind.ObsWebSocket;
        ObsConnectionRow.Visibility = obsSocket ? Visibility.Visible : Visibility.Collapsed;
        ObsPasswordHint.Visibility = obsSocket ? Visibility.Visible : Visibility.Collapsed;
    }

    void ObsPortBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (int.TryParse(ObsPortBox.Text.Trim(), out var port) && port is >= 1 and <= 65535)
        {
            if (port != Ind.ObsWebSocketPort)
            {
                Ind.ObsWebSocketPort = port;
                Save();
            }
        }
        ObsPortBox.Text = Ind.ObsWebSocketPort.ToString();
    }

    void ObsPasswordSave_Click(object sender, RoutedEventArgs e)
    {
        // An empty box clears the password (for a server with authentication off).
        Ind.ObsPasswordProtected = SecretBox.Protect(ObsPasswordBox.Password);
        ObsPasswordBox.Clear();
        Save();
        UpdateObsPasswordHint(saved: true);
    }

    void UpdateObsPasswordHint(bool saved = false)
    {
        var has = !string.IsNullOrEmpty(Ind.ObsPasswordProtected);
        ObsPasswordHint.Text = has
            ? (saved ? "Пароль сохранён в зашифрованном виде" : "Пароль задан (хранится зашифрованным)")
            : (saved ? "Пароль убран: подключение без пароля" : "Пароль не задан");
    }

    // ───────────── Не считать VPN ─────────────

    readonly ObservableCollection<string> _vpnIgnored = new();
    // Tunnels that count as a VPN right now; offered as one-click additions.
    readonly ObservableCollection<string> _vpnFound = new();
    List<string> _vpnDetected = new();

    void LoadVpnIgnored()
    {
        VpnIgnoredList.ItemsSource = _vpnIgnored;
        VpnFoundList.ItemsSource = _vpnFound;
        _vpnIgnored.Clear();
        foreach (var name in Ind.VpnIgnoredAdapters) _vpnIgnored.Add(name);
        UpdateVpnLists();
        _ = DetectVpnAdaptersAsync();
    }

    async Task DetectVpnAdaptersAsync()
    {
        try
        {
            // Listing adapters can take a moment; the page shouldn't wait for it.
            _vpnDetected = await Task.Run(() => VpnMonitor.FindVpnAdapters(Array.Empty<string>()));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        UpdateVpnLists();
    }

    void UpdateVpnLists()
    {
        _vpnFound.Clear();
        foreach (var name in _vpnDetected)
            if (!_vpnIgnored.Contains(name, StringComparer.OrdinalIgnoreCase)) _vpnFound.Add(name);
        VpnIgnoredList.Visibility = _vpnIgnored.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VpnFoundList.Visibility = _vpnFound.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void AddVpnIgnored(string? input)
    {
        var name = (input ?? string.Empty).Trim();
        if (name.Length == 0 || Ind.VpnIgnoredAdapters.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        Ind.VpnIgnoredAdapters.Add(name);
        _vpnIgnored.Add(name);
        UpdateVpnLists();
        // VpnMonitor restarts with the new list on save.
        Save();
    }

    void VpnIgnoreInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddVpnIgnored(VpnIgnoreInput.Text);
        VpnIgnoreInput.Text = string.Empty;
    }

    void AddVpnIgnored_Click(object sender, RoutedEventArgs e)
    {
        AddVpnIgnored(VpnIgnoreInput.Text);
        VpnIgnoreInput.Text = string.Empty;
    }

    void AddFoundVpn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string name) AddVpnIgnored(name);
    }

    void RemoveVpnIgnored_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string name) return;
        Ind.VpnIgnoredAdapters.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        _vpnIgnored.Remove(name);
        UpdateVpnLists();
        Save();
    }
}
