using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glint.Core;
using Glint.Pages;

namespace Glint.Pill;

/// <summary>
/// Behaviour options from Настройки → Поведение: auto-hide (the resting pill slides up behind the top edge,
/// leaving a thin strip) and click-through (the collapsed pill lets clicks pass; Ctrl over it brings it back).
/// Also keeps the tab strip inside the panel when header chips crowd it.
/// </summary>
public partial class PillWindow
{
    // How much of the tucked pill stays on screen, in DIPs at 100%: enough to find with the mouse.
    const double TuckStrip = 4;
    static readonly TimeSpan TuckDelay = TimeSpan.FromSeconds(1.6);

    readonly DispatcherTimer _tuckTimer = new() { Interval = TuckDelay };
    // Only runs in click-through mode: GetAsyncKeyState is a cheap read, nothing is drawn.
    readonly DispatcherTimer _ctrlProbe = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly List<TextBlock> _tabLabels = new();

    bool _tucked;
    bool _clickThroughApplied;
    bool _ctrlOverride;
    bool _compactTabs;

    void InitBehavior()
    {
        _tuckTimer.Tick += (_, _) => { _tuckTimer.Stop(); if (CanTuck) SetTucked(true); };
        _ctrlProbe.Tick += (_, _) => ProbeCtrl();
        Shell.MouseEnter += (_, _) => { _tuckTimer.Stop(); if (_tucked) SetTucked(false); };
        Shell.MouseLeave += (_, _) => ScheduleTuck();
        AppServices.Settings.Changed += (_, _) => ApplyBehavior();
        PanelHeader.SizeChanged += (_, _) => FitTabsSoon();
    }

    void ApplyBehavior()
    {
        var s = AppServices.Settings.Current;
        if (!s.AutoHide)
        {
            _tuckTimer.Stop();
            if (_tucked) SetTucked(false);
        }
        else if (_tucked)
        {
            // The pill size may have changed: keep exactly the strip showing.
            ShellShift.BeginAnimation(TranslateTransform.YProperty, null);
            ShellShift.Y = TuckedY;
        }
        else
        {
            ScheduleTuck();
        }
        UpdateClickThrough();
    }

    // ───────────── Auto-hide ─────────────

    bool CanTuck =>
        AppServices.Settings.Current.AutoHide && _state == State.Pill && !_hiddenByRule && _dragAnchor is null
        && !Shell.IsMouseOver && s_openMenus == 0 && !_recShown && !_voiceShown;

    /// <summary>Starts (or restarts) the wait before the resting pill tucks itself away.</summary>
    void ScheduleTuck()
    {
        if (!AppServices.Settings.Current.AutoHide || _tucked) return;
        _tuckTimer.Stop();
        _tuckTimer.Start();
    }

    /// <summary>Brings a tucked pill back (a flash, the panel, a hover on the strip).</summary>
    void Reveal()
    {
        _tuckTimer.Stop();
        if (_tucked) SetTucked(false);
    }

    double TuckedY => -((Shell.Margin.Top + PillHeight) * _scale - TuckStrip);

    void SetTucked(bool tucked)
    {
        _tucked = tucked;
        var anim = new DoubleAnimation(tucked ? TuckedY : 0, TimeSpan.FromMilliseconds(tucked ? 260 : 180))
        {
            EasingFunction = new CubicEase { EasingMode = tucked ? EasingMode.EaseIn : EasingMode.EaseOut },
        };
        // Only the small resting window moves, but every frame is still a software-rendered layered update.
        Timeline.SetDesiredFrameRate(anim, 30);
        ShellShift.BeginAnimation(TranslateTransform.YProperty, anim);
        // Nobody sees the equalizer (or the agent dot breathing, or the character) behind the edge.
        UpdateMedia();
        UpdateAgentPulse();
        UpdateMascotLive();
    }

    // ───────────── Click-through ─────────────

    /// <summary>
    /// WS_EX_TRANSPARENT lets every click fall through the layered window. Only the resting pill and flashes
    /// pass clicks: an open panel (from the tray, the hotkey or Ctrl + hover) always takes the mouse.
    /// </summary>
    void UpdateClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        var setting = AppServices.Settings.Current.ClickThrough;
        // An agent request card always takes clicks too: its buttons are the only way to answer it here.
        var want = setting && _state is not (State.Expanded or State.Request) && !_ctrlOverride;
        if (want != _clickThroughApplied)
        {
            _clickThroughApplied = want;
            if (want) Native.AddExStyle(_hwnd, Native.WS_EX_TRANSPARENT);
            else Native.RemoveExStyle(_hwnd, Native.WS_EX_TRANSPARENT);
        }
        var probe = setting && !_hiddenByRule && _state is not (State.Expanded or State.Request);
        if (probe && !_ctrlProbe.IsEnabled) _ctrlProbe.Start();
        else if (!probe && _ctrlProbe.IsEnabled) _ctrlProbe.Stop();
        if (!probe) _ctrlOverride = false;
    }

    void ProbeCtrl()
    {
        var ctrl = GetAsyncKeyState(VK_CONTROL) < 0;
        if (ctrl && !_ctrlOverride && CursorOverShell())
        {
            _ctrlOverride = true;
            UpdateClickThrough();
        }
        else if (_ctrlOverride && !ctrl && !Shell.IsMouseOver && _state is not (State.Expanded or State.Request))
        {
            _ctrlOverride = false;
            UpdateClickThrough();
        }
    }

    bool CursorOverShell()
    {
        if (!GetCursorPos(out var p) || !Shell.IsVisible) return false;
        try
        {
            // Through all transforms (scale, sideways shift, tuck), so a tucked strip counts too.
            var a = Shell.PointToScreen(new Point(0, 0));
            var b = Shell.PointToScreen(new Point(Shell.ActualWidth, Shell.ActualHeight));
            // The tucked pill is mostly above the screen; its strip still starts at the very top row.
            return p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y <= Math.Max(a.Y, b.Y) + 2 && p.Y >= Math.Min(a.Y, b.Y) - 2;
        }
        catch (InvalidOperationException)
        {
            // Not connected to a presentation source yet.
            return false;
        }
    }

    const int VK_CONTROL = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out POINT point);

    // ───────────── Update flash ─────────────

    /// <summary>Opens Настройки at the update card (the "Доступно обновление" flash leads here).</summary>
    public void ShowUpdate()
    {
        Activate();
        Expand(SettingsTab);
        if (_pages.TryGetValue(SettingsTab, out var page) && page is SettingsPage settings) settings.RevealUpdate();
    }

    // ───────────── Notes and reminders ─────────────

    /// <summary>Opens the Заметки tab (command bar "заметка", tray menu).</summary>
    public void ShowNotes()
    {
        Activate();
        Expand(NotesTab);
    }

    /// <summary>Opens a note in the Заметки tab (command bar search, the "Заметка сохранена" flash).</summary>
    public void ShowNote(Guid id)
    {
        ShowNotes();
        if (_pages.TryGetValue(NotesTab, out var page) && page is NotesPage notes) notes.ShowNote(id);
    }

    /// <summary>Opens Заметки → Напоминания (Главная's "все напоминания").</summary>
    public void ShowReminders()
    {
        ShowNotes();
        if (_pages.TryGetValue(NotesTab, out var page) && page is NotesPage notes) notes.ShowReminders();
    }

    // ───────────── Weather ─────────────

    /// <summary>Opens the Погода tab (the rain flash, the command bar's "погода").</summary>
    public void ShowWeather()
    {
        Activate();
        Expand(WeatherTab);
    }

    /// <summary>Opens Настройки at the weather card (city search).</summary>
    public void ShowWeatherSettings()
    {
        Activate();
        Expand(SettingsTab);
        if (_pages.TryGetValue(SettingsTab, out var page) && page is SettingsPage settings) settings.RevealWeather();
    }

    // ───────────── Tab strip fit ─────────────

    void FitTabsSoon() => Dispatcher.BeginInvoke(FitTabs, DispatcherPriority.Loaded);

    /// <summary>
    /// Tabs show icons and the active tab's name. When the header chips leave too little room, the name goes
    /// too and every tab is just its icon (the tooltip still names it), so the strip never runs under the chips.
    /// </summary>
    void FitTabs()
    {
        var width = PanelHeader.ActualWidth;
        if (width <= 0 || PanelHeader.Children.Count < 2 || PanelHeader.Children[0] is not FrameworkElement right) return;
        var active = _tabLabels.ElementAtOrDefault(_currentTab);
        if (active is null) return;
        // A collapsed TextBlock measures as 0, so the name's width comes from the text itself, not from layout.
        var labelWidth = TextWidth(active);
        // A horizontal StackPanel measures each tab at its full width, while the strip's own DesiredSize is
        // already cut to the room the DockPanel gave it, so add the buttons up (DesiredSize includes margins).
        var strip = _tabButtons.Sum(b => b.DesiredSize.Width);
        var stripWithoutLabel = strip - (active.Visibility == Visibility.Visible ? active.DesiredSize.Width : 0);
        var need = stripWithoutLabel + labelWidth + right.DesiredSize.Width + 8;
        // A little slack before the name comes back, so a chip label that grows by a digit doesn't flip it.
        var compact = _compactTabs ? need + 6 > width : need > width;
        if (compact == _compactTabs && active.Visibility == (compact ? Visibility.Collapsed : Visibility.Visible)) return;
        _compactTabs = compact;
        active.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    static double TextWidth(TextBlock t)
    {
        var text = new FormattedText(t.Text, System.Globalization.CultureInfo.CurrentUICulture, t.FlowDirection,
            new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(t).PixelsPerDip);
        return Math.Ceiling(text.WidthIncludingTrailingWhitespace) + t.Margin.Left + t.Margin.Right;
    }
}
