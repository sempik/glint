using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Glint.Core;
using Glint.Pages;

namespace Glint.Pill;

public partial class PillWindow : Window, IPillNotifier
{
    public const int HomeTab = 0, ClipboardTab = 1, ShelfTab = 2, TextTab = 3, SystemTab = 4, SettingsTab = 5;

    enum State { Pill, Flash, Expanded }

    // Unscaled sizes in DIPs. The page area inside the panel is about 650 x 330.
    const double PillWidth = 200, PillMediaWidth = 250, PillHeight = 36;
    const double FlashWidth = 380, FlashHeight = 56, FlashCompactHeight = 44;
    const double PanelWidth = 680, PanelHeight = 400;
    const double WindowWidth = 720, WindowHeight = 470;
    // Collapsed pill plus the Shell's top (8) and bottom (30, room for the shadow) margins.
    const double CompactHeight = PillHeight + 38;

    static int s_openMenus;
    static int s_modalDepth;

    readonly (string Glyph, string Tip, Func<FrameworkElement> Create)[] _tabs =
    {
        (Glyphs.Home, "Главная", () => new HomePage()),
        (Glyphs.Clipboard, "Буфер", () => new ClipboardPage()),
        (Glyphs.Shelf, "Полка", () => new ShelfPage()),
        (Glyphs.Text, "Текст", () => new TextToolsPage()),
        (Glyphs.Gauge, "Система", () => new SystemPage()),
        (Glyphs.Settings, "Настройки", () => new SettingsPage()),
    };
    readonly Dictionary<int, FrameworkElement> _pages = new();
    readonly List<RadioButton> _tabButtons = new();
    readonly List<ScaleTransform> _bars = new();

    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _hoverIn = new();
    readonly DispatcherTimer _hoverOut = new() { Interval = TimeSpan.FromMilliseconds(380) };
    readonly DispatcherTimer _flashTimer = new();
    readonly DispatcherTimer _visibilityRules = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    // Important messages (timers, reminders) wait here until the pill can show them; they never expire.
    readonly List<FlashMessage> _important = new();

    State _state = State.Pill;
    int _currentTab = HomeTab;
    bool _pinnedOpen;
    bool _hiddenByRule;
    bool _hasMedia;
    IntPtr _hwnd;
    FlashMessage? _pendingFlash;
    FlashMessage? _currentImportant;

    sealed record FlashMessage(string Title, string? Subtitle, string Glyph, TimeSpan? Duration, DateTime At);

    double _scale = 1;
    bool _compact = true;
    int _shellAnimation;
    bool? _equalizerRunning;
    double? _dragAnchor;
    double _dragStartOffset;
    double? _dragOffset;
    double _shellTargetWidth = PillWidth;

    static PillWindow()
    {
        // A context menu lives in its own popup window, so the pill would think the mouse left.
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((_, _) => s_openMenus++));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((_, _) => s_openMenus = Math.Max(0, s_openMenus - 1)));
    }

    /// <summary>Wrap modal dialogs opened from the panel so it doesn't collapse behind them.</summary>
    public static void BeginModal() => s_modalDepth++;

    public static void EndModal() => s_modalDepth = Math.Max(0, s_modalDepth - 1);

    public PillWindow()
    {
        InitializeComponent();
        BuildTabs();
        BuildEqualizer();

        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm");
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("HH:mm");

        _hoverIn.Tick += (_, _) => { _hoverIn.Stop(); if (_dragAnchor is null) Expand(); };
        _hoverOut.Tick += (_, _) => { _hoverOut.Stop(); TryCollapse(); };
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); EndFlash(); };
        _visibilityRules.Tick += (_, _) => ApplyVisibilityRules();
        _visibilityRules.Start();

        Shell.MouseEnter += OnShellMouseEnter;
        Shell.MouseLeave += (_, _) => { _hoverIn.Stop(); if (_state == State.Expanded) _hoverOut.Start(); };
        Deactivated += (_, _) => { if (_state == State.Expanded && !Shell.IsMouseOver) _hoverOut.Start(); };

        DragEnter += OnDragEnter;
        DragOver += (_, e) => { SetDropEffect(e); e.Handled = true; };
        DragLeave += (_, _) => _hoverOut.Start();
        // DragLeave also fires when moving between children, and a child (the dock, a TextBox) may handle
        // the DragEnter that follows; tunneling events always reach the window, so the panel stays open.
        PreviewDragEnter += (_, _) => _hoverOut.Stop();
        PreviewDragOver += (_, _) => _hoverOut.Stop();
        Drop += OnDrop;

        foreach (var handle in new UIElement[] { PillView, FlashView, PanelHeader })
            handle.MouseLeftButtonDown += OnDragHandleDown;
        Shell.MouseMove += OnDragMove;
        Shell.MouseLeftButtonUp += OnDragUp;
        Shell.LostMouseCapture += OnDragCaptureLost;
        DpiChanged += (_, _) => ApplyLayout();

        PinButton.Click += (_, _) => SetPinned(!_pinnedOpen);
        CommandBarButton.Click += (_, _) => AppServices.ToggleCommandBar();

        AppServices.Media.Changed += (_, _) => Dispatcher.BeginInvoke(UpdateMedia);
        Glint.Services.AudioActivity.Shared.Changed += (_, _) => Dispatcher.BeginInvoke(UpdateMedia);
        Glint.Services.AudioActivity.Shared.Start();
        AppServices.Settings.Changed += (_, _) => ApplyLayout();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(ApplyLayout);
        SetShadow(full: false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: no Alt+Tab entry.
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW);
        ApplyLayout();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 would destroy the window that hosts the hotkey and clipboard hooks while the app keeps running.
        if (!App.IsExiting) e.Cancel = true;
        base.OnClosing(e);
    }

    void ApplyLayout()
    {
        var scale = Math.Clamp(AppServices.Settings.Current.Scale, 0.8, 2.0);
        // Never let the expanded panel run off a small screen.
        var work = SystemParameters.WorkArea;
        if (work.Width > 0 && work.Height > 0)
            scale = Math.Max(0.5, Math.Min(scale, Math.Min(work.Width / WindowWidth, work.Height / WindowHeight)));
        RootScale.ScaleX = RootScale.ScaleY = scale;
        // The cached surface sits under RootScale; render it at the final size so the stroke stays crisp.
        ShellCache.RenderAtScale = scale * VisualTreeHelper.GetDpi(this).DpiScaleX;
        _scale = scale;
        ApplyWindowSize();
        _hoverIn.Interval = TimeSpan.FromMilliseconds(Math.Max(0, AppServices.Settings.Current.HoverDelayMs));
    }

    /// <summary>
    /// Rendering is software-only and a layered window re-uploads its whole surface on every frame, so a
    /// resting pill keeps the window only as tall as itself; the panel, flashes and animations get the full height.
    /// Only the height changes: a layered window shows its previous bitmap until the next frame, so moving its
    /// origin would make the pill jump for a frame, while growing or shrinking downward leaves it in place.
    /// </summary>
    void ApplyWindowSize()
    {
        Width = WindowWidth * _scale;
        Height = (_compact ? CompactHeight : WindowHeight) * _scale;
        // The window stays on screen; near an edge the shell is shifted inside it (see ShiftFor).
        var screen = ScreenWidth;
        Left = Math.Clamp(screen / 2 + CurrentOffset - Width / 2, 0, Math.Max(0, screen - Width));
        Top = 0;
        ShellShift.BeginAnimation(TranslateTransform.XProperty, null);
        ShellShift.X = ShiftFor(_shellTargetWidth);
    }

    /// <summary>
    /// Primary screen width in this window's DIPs. SystemParameters converts with the DPI cached at startup,
    /// which goes stale when the display scale changes while Glint runs.
    /// </summary>
    double ScreenWidth => Glint.Pages.HomeNative.GetSystemMetrics(SM_CXSCREEN) / VisualTreeHelper.GetDpi(this).DpiScaleX;

    const int SM_CXSCREEN = 0;

    /// <summary>The pill itself may go right up to a screen edge, clear of the browser tabs it would cover.</summary>
    double ClampOffset(double offset)
    {
        var room = Math.Max(0, ScreenWidth / 2 - (PillMediaWidth / 2 + 8) * _scale);
        return Math.Clamp(offset, -room, room);
    }

    double CurrentOffset => ClampOffset(_dragOffset ?? AppServices.Settings.Current.PillOffset);

    /// <summary>
    /// Shift inside the window that puts a shell of this width under the chosen spot, limited so the shell
    /// never leaves the window: the pill sits at the edge, the wide panel slides back onto the screen.
    /// </summary>
    double ShiftFor(double shellWidth)
    {
        var shift = ScreenWidth / 2 + CurrentOffset - (Left + Width / 2);
        var room = Math.Max(0, (Width - (shellWidth + 40) * _scale) / 2);
        return Math.Clamp(shift, -room, room);
    }

    // ───────────── Dragging along the top edge ─────────────

    // The pill can cover browser tabs, so it can be moved sideways by the pill itself or the panel header.
    // Only horizontal: the panel opens downward and must stay attached to the top edge.
    // Shell holds the capture: the handle itself may fade out mid-drag (panel opening, flash ending).

    void OnDragHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SavePillOffset(0);
            e.Handled = true;
            return;
        }
        if (!Shell.CaptureMouse()) return;
        _dragAnchor = PointToScreen(e.GetPosition(this)).X;
        _dragStartOffset = CurrentOffset;
        _dragOffset = null;
        // Nothing may swap the view under the cursor while the button is held.
        _hoverIn.Stop();
        _flashTimer.Stop();
        e.Handled = true;
    }

    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragAnchor is not double anchor || e.LeftButton != MouseButtonState.Pressed) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var delta = (PointToScreen(e.GetPosition(this)).X - anchor) / dpi;
        // A small threshold so a plain click on the pill doesn't nudge it.
        if (_dragOffset is null && Math.Abs(delta) < 4) return;
        if (_dragOffset is null) Mouse.OverrideCursor = Cursors.SizeWE;
        _dragOffset = ClampOffset(_dragStartOffset + delta);
        ApplyWindowSize();
    }

    void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragAnchor is null) return;
        Shell.ReleaseMouseCapture();
    }

    void OnDragCaptureLost(object sender, MouseEventArgs e)
    {
        if (_dragAnchor is null) return;
        _dragAnchor = null;
        Mouse.OverrideCursor = null;
        if (_dragOffset is double offset) SavePillOffset(offset);
        _dragOffset = null;
        // Resume what the press paused: the flash countdown, and the hover that opens the panel.
        if (_state == State.Flash) _flashTimer.Start();
        if (Shell.IsMouseOver) { if (_state != State.Expanded) _hoverIn.Start(); }
        else if (_state == State.Expanded) _hoverOut.Start();
    }

    void SavePillOffset(double offset)
    {
        try
        {
            AppServices.Settings.Current.PillOffset = Math.Round(offset);
            AppServices.Settings.Save();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        ApplyWindowSize();
    }

    void SetCompact(bool compact)
    {
        if (compact == _compact) return;
        _compact = compact;
        ApplyWindowSize();
    }

    // ───────────── Tabs ─────────────

    void BuildTabs()
    {
        for (var i = 0; i < _tabs.Length; i++)
        {
            var index = i;
            var icon = new TextBlock { Style = (Style)FindResource("Icon"), Text = _tabs[i].Glyph, FontSize = 13 };
            // The active tab also shows its name, like a segmented control.
            var label = new TextBlock { Text = _tabs[i].Tip, Margin = new Thickness(6, 0, 0, 0), FontSize = 12, Visibility = Visibility.Collapsed };
            var button = new RadioButton
            {
                Style = (Style)FindResource("TabButton"),
                GroupName = "PillTabs",
                ToolTip = _tabs[i].Tip,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
            };
            // Implicit TextBlock styles would override inheritance, so follow the button's foreground explicitly.
            var follow = new System.Windows.Data.Binding(nameof(Foreground)) { Source = button };
            icon.SetBinding(TextBlock.ForegroundProperty, follow);
            label.SetBinding(TextBlock.ForegroundProperty, follow);
            button.Checked += (_, _) => { label.Visibility = Visibility.Visible; ShowTab(index); };
            button.Unchecked += (_, _) => label.Visibility = Visibility.Collapsed;
            // Like browser tabs: dragging a file over a tab opens it, e.g. Home to pin a shortcut.
            button.DragEnter += (_, _) => button.IsChecked = true;
            _tabButtons.Add(button);
            TabStrip.Children.Add(button);
        }
        _tabButtons[HomeTab].IsChecked = true;
    }

    void ShowTab(int index)
    {
        _currentTab = index;
        if (!_pages.TryGetValue(index, out var page))
        {
            page = _tabs[index].Create();
            _pages[index] = page;
        }
        PageHost.Content = page;
    }

    // ───────────── Expand / collapse ─────────────

    void OnShellMouseEnter(object sender, MouseEventArgs e)
    {
        _hoverOut.Stop();
        if (_state != State.Expanded && !_hiddenByRule) _hoverIn.Start();
    }

    /// <summary>Open the panel, optionally switching to a tab (see the *Tab constants).</summary>
    public void Expand(int? tab = null)
    {
        if (_hiddenByRule) return;
        if (tab is int t && t != _currentTab) _tabButtons[t].IsChecked = true;
        else if (PageHost.Content is null) ShowTab(_currentTab);

        _flashTimer.Stop();
        if (_state == State.Expanded) return;
        // An important message cut short by the panel is shown again after it collapses.
        if (_state == State.Flash) RequeueCurrentImportant();
        _state = State.Expanded;

        Fade(PillView, false);
        Fade(FlashView, false);
        Fade(PanelView, true, delayMs: 90);
        Shell.CornerRadius = new CornerRadius(26);
        SetShadow(full: true);
        AnimateShell(PanelWidth, PanelHeight);
    }

    void TryCollapse()
    {
        if (_state != State.Expanded || _pinnedOpen) return;
        if (Shell.IsMouseOver || s_modalDepth > 0) return;
        if (s_openMenus > 0 || Mouse.Captured is not null)
        {
            // Closing the menu or releasing capture outside the panel raises no new MouseLeave; check again shortly.
            _hoverOut.Start();
            return;
        }
        // While the user is typing inside the panel, wait until they click elsewhere (Deactivated).
        if (IsActive && Keyboard.FocusedElement is TextBoxBase) return;
        Collapse();
    }

    void Collapse()
    {
        if (_state != State.Expanded) return;
        _state = State.Pill;
        Fade(PanelView, false);
        Fade(PillView, true, delayMs: 120);
        Shell.CornerRadius = new CornerRadius(PillHeight / 2);
        SetShadow(full: false);
        AnimateShell(CurrentPillWidth, PillHeight);

        if (_hiddenByRule) _pendingFlash = null;
        else TryShowQueued();
    }

    void SetPinned(bool pinned)
    {
        _pinnedOpen = pinned;
        PinIcon.Text = pinned ? Glyphs.Unpin : Glyphs.Pin;
        // A resource reference, so a theme change repaints the pinned icon too.
        PinIcon.SetResourceReference(TextBlock.ForegroundProperty, pinned ? "AccentBrush" : "MutedBrush");
        PinButton.ToolTip = pinned ? "Разрешить сворачивание" : "Не сворачивать";
        if (!pinned) _hoverOut.Start();
    }

    double CurrentPillWidth => _hasMedia ? PillMediaWidth : PillWidth;

    void AnimateShell(double width, double height)
    {
        var duration = TimeSpan.FromMilliseconds(340);
        var ease = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };
        SetCompact(false);
        // Near a screen edge the panel slides inward as it grows, and the pill slides back as it shrinks.
        _shellTargetWidth = width;
        ShellShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(ShiftFor(width), duration) { EasingFunction = ease });
        var generation = ++_shellAnimation;
        var widthAnim = new DoubleAnimation(width, duration) { EasingFunction = ease };
        // Shrink back only after the last animation settles on the plain pill (the pill's fade-in ends earlier).
        widthAnim.Completed += (_, _) => { if (generation == _shellAnimation && _state == State.Pill) SetCompact(true); };
        Shell.BeginAnimation(WidthProperty, widthAnim);
        Shell.BeginAnimation(HeightProperty, new DoubleAnimation(height, duration) { EasingFunction = ease });
    }

    /// <summary>
    /// A layered window takes clicks wherever alpha is non-zero, so a wide shadow around the small pill
    /// would swallow clicks on browser tabs underneath. Keep it tight unless the panel is open.
    /// </summary>
    void SetShadow(bool full)
    {
        ShellShadow.BlurRadius = full ? 26 : 10;
        ShellShadow.ShadowDepth = full ? 6 : 2;
        ShellShadow.Opacity = full ? 0.45 : 0.35;
    }

    static void Fade(UIElement element, bool show, int delayMs = 0)
    {
        var anim = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 200 : 110))
        {
            BeginTime = TimeSpan.FromMilliseconds(show ? delayMs : 0),
        };
        if (show)
        {
            element.Visibility = Visibility.Visible;
        }
        else
        {
            anim.Completed += (_, _) =>
            {
                // Ignore if something faded it back in meanwhile.
                if (element.Opacity == 0) element.Visibility = Visibility.Collapsed;
            };
        }
        element.BeginAnimation(OpacityProperty, anim);
    }

    // ───────────── Flash ─────────────

    public void Flash(string title, string? subtitle = null, string glyph = Glyphs.Info, TimeSpan? duration = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Flash(title, subtitle, glyph, duration));
            return;
        }
        // A hide decided before (e.g. for a window that has since closed) must not swallow the message.
        ApplyVisibilityRules();
        // Don't show over a fullscreen app.
        if (_hiddenByRule) return;
        var message = new FlashMessage(title, subtitle, glyph, duration, DateTime.UtcNow);
        // Don't interrupt an open panel or an important message: keep the latest and show it afterwards.
        if (_state == State.Expanded || (_state == State.Flash && _currentImportant is not null))
        {
            _pendingFlash = message;
            return;
        }
        _pendingFlash = null;
        ShowFlash(message, important: false);
    }

    public void FlashImportant(string title, string? subtitle = null, string glyph = Glyphs.Info, TimeSpan? duration = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => FlashImportant(title, subtitle, glyph, duration));
            return;
        }
        ApplyVisibilityRules();
        var message = new FlashMessage(title, subtitle, glyph, duration, DateTime.UtcNow);
        if (_hiddenByRule || _state == State.Expanded || (_state == State.Flash && _currentImportant is not null))
        {
            _important.Add(message);
            // The pill can't show it right now; let Windows tell the user as well.
            if (_hiddenByRule || _state == State.Expanded) AppServices.SystemNotify?.Invoke(title, subtitle);
            return;
        }
        ShowFlash(message, important: true);
    }

    void ShowFlash(FlashMessage m, bool important)
    {
        _currentImportant = important ? m : null;

        FlashTitle.Text = m.Title;
        FlashSubtitle.Text = m.Subtitle ?? string.Empty;
        FlashSubtitle.Visibility = string.IsNullOrEmpty(m.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        FlashIcon.Text = m.Glyph;

        var compact = string.IsNullOrEmpty(m.Subtitle);
        FlashIconBg.Width = FlashIconBg.Height = compact ? 28 : 34;
        FlashIconBg.CornerRadius = new CornerRadius(compact ? 14 : 17);

        if (_state != State.Flash)
        {
            _state = State.Flash;
            Fade(PillView, false);
            Fade(FlashView, true, delayMs: 80);
        }
        var height = compact ? FlashCompactHeight : FlashHeight;
        Shell.CornerRadius = new CornerRadius(height / 2);
        AnimateShell(FlashWidth, height);

        _flashTimer.Interval = m.Duration ?? TimeSpan.FromSeconds(2.4);
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    /// <summary>Shows the next queued important message, else a recent pending one. Returns true if something is shown.</summary>
    bool TryShowQueued()
    {
        if (_important.Count > 0)
        {
            var next = _important[0];
            _important.RemoveAt(0);
            ShowFlash(next, important: true);
            return true;
        }
        if (_pendingFlash is { } p)
        {
            _pendingFlash = null;
            if (DateTime.UtcNow - p.At < TimeSpan.FromSeconds(10))
            {
                ShowFlash(p, important: false);
                return true;
            }
        }
        return false;
    }

    void RequeueCurrentImportant()
    {
        if (_currentImportant is { } c) _important.Insert(0, c);
        _currentImportant = null;
    }

    void EndFlash()
    {
        if (_state != State.Flash) return;
        _flashTimer.Stop();
        if (_hiddenByRule)
        {
            RequeueCurrentImportant();
        }
        else
        {
            _currentImportant = null;
            if (TryShowQueued()) return;
        }
        _state = State.Pill;
        Fade(FlashView, false);
        Fade(PillView, true, delayMs: 100);
        Shell.CornerRadius = new CornerRadius(PillHeight / 2);
        AnimateShell(CurrentPillWidth, PillHeight);
        if (Shell.IsMouseOver) _hoverIn.Start();
    }

    // ───────────── Media indicator ─────────────

    void BuildEqualizer()
    {
        for (var i = 0; i < 4; i++)
        {
            var scale = new ScaleTransform(1, 0.35);
            _bars.Add(scale);
            var bar = new Rectangle
            {
                Width = 3,
                Height = 14,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(1, 0, 1, 0),
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = scale,
            };
            // ThemeService swaps this brush when the user changes colors.
            bar.SetResourceReference(Shape.FillProperty, "AccentVerticalBrush");
            Equalizer.Children.Add(bar);
        }
    }

    void SetEqualizerRunning(bool running)
    {
        // Media events arrive often (timeline updates); restarting the animations each time is wasted work.
        if (_equalizerRunning == running) return;
        _equalizerRunning = running;
        double[] periods = { 420, 310, 520, 370 };
        for (var i = 0; i < _bars.Count; i++)
        {
            if (running)
            {
                var anim = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(periods[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                // Rendering is software-only and every frame re-uploads the whole layered window; a few bouncing
                // bars look the same at 24 fps and cost a fraction of the CPU of 60.
                Timeline.SetDesiredFrameRate(anim, 24);
                _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
            else
            {
                _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _bars[i].ScaleY = 0.3;
            }
        }
    }

    void UpdateMedia()
    {
        var media = AppServices.Media.Current;
        var hadMedia = _hasMedia;
        // Some players (Web Audio, many Electron apps) never show up in the system media controls; if one is
        // audibly playing while the known session is paused or absent, that session's info is stale anyway.
        var audibleOnly = media?.IsPlaying != true && Glint.Services.AudioActivity.Shared.AudibleApp is not null;
        if (audibleOnly) media = null;
        _hasMedia = media is not null || audibleOnly;

        if (media?.Thumbnail is { } art)
        {
            PillArtBrush.ImageSource = art;
            PillArt.Visibility = Visibility.Visible;
            PillDot.Visibility = Visibility.Collapsed;
        }
        else
        {
            PillArt.Visibility = Visibility.Collapsed;
            PillDot.Visibility = Visibility.Visible;
        }

        Equalizer.Visibility = _hasMedia ? Visibility.Visible : Visibility.Collapsed;
        // Nobody sees it while the pill is hidden over a fullscreen app, so don't spend frames on it.
        SetEqualizerRunning((media?.IsPlaying == true || audibleOnly) && !_hiddenByRule);

        if (_state == State.Pill && hadMedia != _hasMedia) AnimateShell(CurrentPillWidth, PillHeight);
    }

    // ───────────── Drag & drop to shelf ─────────────

    // The shelf keeps a reference, not a copy, so never show Explorer's "Move" cursor.
    static void SetDropEffect(DragEventArgs e) =>
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;

    void OnDragEnter(object sender, DragEventArgs e)
    {
        SetDropEffect(e);
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _hoverOut.Stop();
        // An open panel keeps its tab, so files can reach the Home dock; hovering a tab switches to it.
        if (_state != State.Expanded) Expand(ShelfTab);
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            AppServices.Shelf.Add(paths);
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
        }
    }

    // ───────────── Fullscreen / per-app hiding ─────────────

    void ApplyVisibilityRules()
    {
        var settings = AppServices.Settings.Current;
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return;
        // Our own windows (eyedropper overlay, command bar, dialogs) are never a reason to hide.
        Native.GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return;

        var hide = settings.HideInFullscreen && IsFullscreenOnOurMonitor(fg);
        if (!hide && settings.HiddenInApps is { Count: > 0 } hiddenApps)
        {
            var name = Native.GetProcessName(fg);
            hide = hiddenApps.Any(a => a is not null && string.Equals(a.Replace(".exe", "", StringComparison.OrdinalIgnoreCase), name, StringComparison.OrdinalIgnoreCase));
        }
        SetHiddenByRule(hide);
    }

    bool IsFullscreenOnOurMonitor(IntPtr fg)
    {
        if (fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow()) return false;
        var cls = Native.GetClassName(fg);
        if (cls is "WorkerW" or "Progman" or "Shell_TrayWnd") return false;
        // With an auto-hide taskbar a maximized window with a title bar covers the monitor too; that isn't fullscreen.
        if (Native.IsZoomed(fg) && (Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64() & Native.WS_CAPTION) == Native.WS_CAPTION)
            return false;

        var monitor = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor != Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST)) return false;

        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info) || !Native.GetWindowRect(fg, out var r)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    void SetHiddenByRule(bool hide)
    {
        if (hide == _hiddenByRule) return;
        _hiddenByRule = hide;
        if (hide)
        {
            _pinnedOpen = false;
            SetPinned(false);
            Collapse();
            EndFlash();
        }
        Root.Visibility = hide ? Visibility.Hidden : Visibility.Visible;
        UpdateMedia();
        // Reminders that fired while hidden are shown as soon as the pill is back.
        if (!hide && _state == State.Pill) TryShowQueued();
    }
}
