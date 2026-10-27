using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Glint.Core;
using Path = System.Windows.Shapes.Path;
using Glint.Services.Capture;

namespace Glint.Capture;

/// <summary>
/// Colour picker over a frozen snapshot. A loupe follows the cursor with the pixels around it enlarged on a
/// grid, the centre pixel framed, and its HEX and RGB values. Click copies HEX (Ctrl+click: rgb(...)), arrow keys
/// move one pixel (Shift: ten), Esc or right click cancels. The picture is frozen, so nothing polls the screen;
/// only the loupe repaints as the mouse moves.
/// </summary>
public sealed class EyedropperWindow : SnapshotWindow
{
    // An odd number of pixels so one sits in the middle; each drawn as a Cell x Cell square.
    const int Pixels = 11, Cell = 13;
    const double Zoom = Pixels * Cell;

    static EyedropperWindow? s_current;

    readonly Border _loupe;
    readonly ImageBrush _zoom;
    readonly Border _swatch;
    readonly TextBlock _hexText = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Consolas, Segoe UI") };
    readonly TextBlock _rgbText = new() { FontSize = 11.5 };
    Color _color;

    EyedropperWindow(ScreenSnapshot shot) : base(shot, shot.Image)
    {
        Title = "Glint — пипетка";
        _zoom = new ImageBrush(shot.Image) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_zoom, BitmapScalingMode.NearestNeighbor);

        var magnifier = new Grid
        {
            Width = Zoom,
            Height = Zoom,
            Clip = new RectangleGeometry(new Rect(0, 0, Zoom, Zoom), 9, 9),
            Children =
            {
                new Rectangle { Fill = _zoom },
                new Path { Data = GridLines(), Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80)), StrokeThickness = 1 },
                // Dark outer and light inner frame: visible on any colour.
                new Rectangle { Width = Cell + 2, Height = Cell + 2, Stroke = Brushes.Black, StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Pixels / 2 * Cell - 1, Pixels / 2 * Cell - 1, 0, 0) },
                new Rectangle { Width = Cell, Height = Cell, Stroke = Brushes.White, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Pixels / 2 * Cell, Pixels / 2 * Cell, 0, 0) },
            },
        };

        _swatch = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        _rgbText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var hint = new TextBlock { Text = "Клик — HEX · Ctrl+клик — RGB\nСтрелки — точнее · Esc — отмена", FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.None };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        _loupe = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(8),
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden,
            Child = new StackPanel
            {
                Children =
                {
                    magnifier,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children =
                        {
                            _swatch,
                            new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _hexText, _rgbText } },
                        },
                    },
                    hint,
                },
            },
        };
        // Theme brushes as resource references, so the loupe follows the current colors.
        _loupe.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
        _loupe.SetResourceReference(Border.BorderBrushProperty, "AccentStrokeBrush");
        Layer.Children.Add(_loupe);

        MouseMove += (_, e) => UpdateLoupe(ToPixel(e.GetPosition(Layer)));
        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Pick((Keyboard.Modifiers & ModifierKeys.Control) != 0);
        };
        MouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            Cancel();
        };
        PreviewKeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(s_current, this)) s_current = null;
            ScreenTools.ReleaseSnapshotSoon();
        };
    }

    /// <summary>Opens the picker over a snapshot, or focuses the one already open. UI thread.</summary>
    public static void Open(ScreenSnapshot shot)
    {
        if (s_current is not null)
        {
            s_current.Activate();
            return;
        }
        s_current = new EyedropperWindow(shot);
        s_current.Show();
    }

    public static bool IsOpen => s_current is not null;

    protected override void OnShown() => UpdateLoupe(CursorPixel(Shot));

    protected override void Cancel() => Finish(null);

    static Geometry GridLines()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            for (var i = 1; i < Pixels; i++)
            {
                // Half-pixel offsets keep the 1-DIP lines crisp.
                var at = i * Cell + 0.5;
                ctx.BeginFigure(new Point(at, 0), false, false);
                ctx.LineTo(new Point(at, Zoom), true, false);
                ctx.BeginFigure(new Point(0, at), false, false);
                ctx.LineTo(new Point(Zoom, at), true, false);
            }
        }
        g.Freeze();
        return g;
    }

    void UpdateLoupe(Point pixel)
    {
        if (Finished) return;
        var x = (int)Math.Floor(pixel.X);
        var y = (int)Math.Floor(pixel.Y);
        _color = Shot.PixelAt(x, y);
        _zoom.Viewbox = new Rect(x - Pixels / 2, y - Pixels / 2, Pixels, Pixels);
        _hexText.Text = ToHex(_color);
        _rgbText.Text = $"RGB {_color.R}, {_color.G}, {_color.B}";
        if (_swatch.Background is not SolidColorBrush current || current.Color != _color) _swatch.Background = new SolidColorBrush(_color);

        _loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = _loupe.DesiredSize.Width;
        var h = _loupe.DesiredSize.Height;
        var m = Shot.MonitorAt(x, y);
        var px = ToDip(pixel.X);
        var py = ToDip(pixel.Y);
        var left = px + 22;
        var top = py + 22;
        // Flip to the other side of the cursor near the right/bottom edges of this monitor.
        if (left + w > ToDip(m.X + m.Width) - 4) left = px - 22 - w;
        if (top + h > ToDip(m.Y + m.Height) - 4) top = py - 22 - h;
        Canvas.SetLeft(_loupe, Math.Max(ToDip(m.X) + 4, left));
        Canvas.SetTop(_loupe, Math.Max(ToDip(m.Y) + 4, top));
        _loupe.Visibility = Visibility.Visible;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => (0, 0),
        };
        if (dx != 0 || dy != 0)
        {
            e.Handled = true;
            if (GetCursorPos(out var p))
            {
                SetCursorPos(p.X + dx, p.Y + dy);
                UpdateLoupe(CursorPixel(Shot));
            }
        }
        else if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            Pick((Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }
    }

    static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    void Pick(bool rgb)
    {
        UpdateLoupe(CursorPixel(Shot));
        Finish(rgb ? $"rgb({_color.R}, {_color.G}, {_color.B})" : ToHex(_color));
    }

    void Finish(string? value)
    {
        if (!BeginFinish() || value is null) return;
        if (ScreenTools.CopyText(value)) AppServices.Notifier?.Flash("Цвет скопирован", value, Glyphs.Eyedropper);
        else AppServices.Notifier?.Flash("Буфер обмена занят", value, Glyphs.Info);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetCursorPos(int x, int y);
}
