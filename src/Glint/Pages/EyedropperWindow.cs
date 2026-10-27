using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glint.Core;

namespace Glint.Pages;

/// <summary>
/// Fullscreen picker over the whole virtual screen. A floating loupe follows the cursor;
/// left click copies the HEX colour, Esc or right click cancels.
/// </summary>
public sealed class EyedropperWindow : Window
{
    static EyedropperWindow? s_current;

    readonly Canvas _canvas = new();
    readonly Border _loupe;
    readonly Border _swatch;
    readonly TextBlock _hexText;
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
    IntPtr _hwnd;
    bool _finished;

    /// <summary>Opens the picker, or focuses the one already open.</summary>
    public static void Start()
    {
        try
        {
            if (s_current is not null)
            {
                s_current.Activate();
                return;
            }
            s_current = new EyedropperWindow();
            s_current.Show();
            s_current.Activate();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            s_current = null;
        }
    }

    EyedropperWindow()
    {
        Title = "Glint — пипетка";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Focusable = true;
        // Alpha 1 keeps the layered window hit-testable while staying visually invisible.
        var veil = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        veil.Freeze();
        Background = veil;

        // Rough DIP bounds so the first frame lands in the right place; exact physical bounds follow.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _swatch = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Res<Brush>("DividerBrush"),
            Background = Brushes.Black,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _hexText = new TextBlock { Text = "#000000", FontSize = 14, FontWeight = FontWeights.SemiBold };
        var hint = new TextBlock { Text = "Клик — копировать · Esc — отмена", Margin = new Thickness(0, 2, 0, 0) };
        if (TryFindResource("MutedText") is Style muted) hint.Style = muted;

        _loupe = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(8, 8, 12, 8),
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    _swatch,
                    new StackPanel
                    {
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { _hexText, hint },
                    },
                },
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.45,
                Color = Colors.Black,
            },
        };
        // Theme brushes as resource references, so the loupe follows the current colors.
        _loupe.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _loupe.SetResourceReference(Border.BorderBrushProperty, "AccentStrokeBrush");
        _canvas.Children.Add(_loupe);
        Content = _canvas;

        _timer.Tick += (_, _) => UpdateLoupe();
        MouseMove += (_, _) => UpdateLoupe();
        MouseLeftButtonDown += (_, e) => { e.Handled = true; Pick(); };
        MouseRightButtonDown += (_, e) => { e.Handled = true; Finish(null); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(null);
            else if (e.Key is Key.Enter or Key.Space) Pick();
        };
        // Alt+Tab or a click on another monitor's app must not leave an invisible wall behind.
        Deactivated += (_, _) => Dispatcher.InvokeAsync(() => Finish(null));
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(ApplyPhysicalBounds);
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (ReferenceEquals(s_current, this)) s_current = null;
        };
    }

    T? Res<T>(string key) where T : class => TryFindResource(key) as T;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW);
        // Keep our own veil and loupe out of screen reads so GetPixel sees the real desktop.
        HomeNative.SetWindowDisplayAffinity(_hwnd, HomeNative.WDA_EXCLUDEFROMCAPTURE);
        ApplyPhysicalBounds();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Focus();
        UpdateLoupe();
        _timer.Start();
    }

    // DIP bounds can't describe mixed-DPI layouts, so size the window in physical pixels.
    void ApplyPhysicalBounds()
    {
        if (_hwnd == IntPtr.Zero || _finished) return;
        HomeNative.SetWindowPos(_hwnd, HomeNative.HWND_TOPMOST,
            HomeNative.GetSystemMetrics(HomeNative.SM_XVIRTUALSCREEN),
            HomeNative.GetSystemMetrics(HomeNative.SM_YVIRTUALSCREEN),
            HomeNative.GetSystemMetrics(HomeNative.SM_CXVIRTUALSCREEN),
            HomeNative.GetSystemMetrics(HomeNative.SM_CYVIRTUALSCREEN),
            0);
    }

    Color? ReadColorUnderCursor(out HomeNative.POINT cursor)
    {
        if (!HomeNative.GetCursorPos(out cursor)) return null;
        return HomeNative.ReadScreenPixel(cursor.X, cursor.Y) is { } c ? Color.FromRgb(c.R, c.G, c.B) : null;
    }

    static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    void UpdateLoupe()
    {
        if (_finished) return;
        try
        {
            if (ReadColorUnderCursor(out var cursor) is not { } color) return;

            _hexText.Text = ToHex(color);
            if (_swatch.Background is not SolidColorBrush current || current.Color != color)
                _swatch.Background = new SolidColorBrush(color);

            var p = _canvas.PointFromScreen(new Point(cursor.X, cursor.Y));
            var w = _loupe.ActualWidth;
            var h = _loupe.ActualHeight;
            var x = p.X + 20;
            var y = p.Y + 24;
            // Flip to the other side of the cursor near the right/bottom edges.
            if (x + w > _canvas.ActualWidth - 4) x = p.X - 20 - w;
            if (y + h > _canvas.ActualHeight - 4) y = p.Y - 20 - h;
            Canvas.SetLeft(_loupe, Math.Max(4, x));
            Canvas.SetTop(_loupe, Math.Max(4, y));
            _loupe.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // PointFromScreen throws if the window is mid-teardown; nothing worth surfacing.
            App.LogError(ex);
        }
    }

    void Pick()
    {
        if (_finished) return;
        string? hex = null;
        try
        {
            if (ReadColorUnderCursor(out _) is { } color) hex = ToHex(color);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        Finish(hex);
    }

    void Finish(string? hex)
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }

        if (hex is null) return;
        try
        {
            System.Windows.Clipboard.SetText(hex);
            AppServices.Notifier?.Flash("Цвет скопирован", hex, Glyphs.Eyedropper);
        }
        catch (Exception ex)
        {
            // Another app can hold the clipboard open; tell the user rather than fail silently.
            App.LogError(ex);
            AppServices.Notifier?.Flash("Буфер обмена занят", hex, Glyphs.Info);
        }
    }
}
