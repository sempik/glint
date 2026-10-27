using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glint.Core;
using Glint.Services.Capture;

namespace Glint.Capture;

/// <summary>
/// Base for the full-screen pickers (area selection, eyedropper): a borderless window over the whole virtual
/// screen showing a frozen <see cref="ScreenSnapshot"/>. Opaque on purpose: with software rendering a layered
/// window this big would re-upload every pixel of every monitor on each mouse move, while an opaque one only
/// repaints the small areas that change. It stays cloaked until its first frame is drawn, so it appears in one
/// step without a white flash; since it shows the same picture as the screen, the switch is invisible.
/// </summary>
public abstract class SnapshotWindow : Window
{
    protected ScreenSnapshot Shot { get; }
    /// <summary>Free-positioned layer above the picture, in DIPs.</summary>
    protected Canvas Layer { get; } = new();
    protected IntPtr Hwnd { get; private set; }
    /// <summary>Physical pixels per DIP for this window.</summary>
    protected double Dpi { get; private set; } = 1;
    protected bool Finished { get; private set; }

    protected SnapshotWindow(ScreenSnapshot shot, ImageSource background)
    {
        Shot = shot;
        Title = "Glint";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Focusable = true;
        Background = Brushes.Black;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // Rough DIP bounds so the first frame lands in the right place; exact physical bounds follow.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // One window pixel is one snapshot pixel; no smoothing, so it looks exactly like the screen did.
        var image = new Image { Source = background, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Content = new Grid { Children = { image, Layer } };

        // Alt+Tab or a click on another app must not leave a frozen screen behind.
        Deactivated += (_, _) => Dispatcher.InvokeAsync(Cancel);
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(ApplyPhysicalBounds);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Cancel();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(Hwnd, Native.WS_EX_TOOLWINDOW);
        SetCloaked(true);
        ApplyPhysicalBounds();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // Let the first full frame reach the screen before uncloaking.
        Dispatcher.BeginInvoke(() =>
        {
            if (Finished) return;
            SetCloaked(false);
            Activate();
            Focus();
            OnShown();
        }, DispatcherPriority.Background);
    }

    /// <summary>Called once the window is visible and has focus.</summary>
    protected virtual void OnShown() { }

    /// <summary>Esc, right click, focus lost.</summary>
    protected abstract void Cancel();

    // DIP bounds can't describe mixed-DPI layouts, so size the window in physical pixels.
    void ApplyPhysicalBounds()
    {
        if (Hwnd == IntPtr.Zero || Finished) return;
        SetWindowPos(Hwnd, HWND_TOPMOST, Shot.Left, Shot.Top, Shot.Width, Shot.Height, 0);
        Dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    /// <summary>Mouse position (DIPs on the layer) to snapshot pixels.</summary>
    protected Point ToPixel(Point dip) => new(dip.X * Dpi, dip.Y * Dpi);

    protected double ToDip(double pixels) => pixels / Dpi;

    /// <summary>The cursor in snapshot pixels.</summary>
    protected static Point CursorPixel(ScreenSnapshot shot) =>
        GetCursorPos(out var p) ? new Point(p.X - shot.Left, p.Y - shot.Top) : new Point(shot.Width / 2.0, shot.Height / 2.0);

    /// <summary>Marks the picker done and closes it; false if it was already finishing.</summary>
    protected bool BeginFinish()
    {
        if (Finished) return false;
        Finished = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return true;
    }

    /// <summary>A rounded hint card ("Esc — отмена") in the theme's colors.</summary>
    protected Border CreateHint(string text)
    {
        var hint = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 8, 14, 8),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = text, FontSize = 12.5 },
        };
        hint.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
        hint.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        return hint;
    }

    /// <summary>Puts the hint at the top centre of the monitor the cursor is on.</summary>
    protected void PlaceHint(Border hint)
    {
        var c = CursorPixel(Shot);
        var m = Shot.MonitorAt((int)c.X, (int)c.Y);
        hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(hint, ToDip(m.X + m.Width / 2.0) - hint.DesiredSize.Width / 2);
        Canvas.SetTop(hint, ToDip(m.Y) + 24);
    }

    void SetCloaked(bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(Hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }

    const int DWMWA_CLOAK = 13;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
