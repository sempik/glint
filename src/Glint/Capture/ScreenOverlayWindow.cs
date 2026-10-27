using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Glint.Services.Capture;

namespace Glint.Capture;

/// <summary>What a plain click picks in the overlay: the window under the cursor, or (for recording) the monitor.</summary>
public enum OverlayMode { Window, Monitor }

/// <summary>
/// Area picker for screenshots, QR, text recognition and recording. The screen is shown frozen and dimmed;
/// dragging cuts a bright rectangle out of it, while hovering highlights the window (or monitor) a click would
/// take. Only the bright rectangle and its frame move, so each mouse move repaints just that area.
/// </summary>
public sealed class ScreenOverlayWindow : SnapshotWindow
{
    const double DragThreshold = 4;

    readonly OverlayMode _mode;
    // Callers go on to open the editor or a recorder; that must not run inside this window's mouse or close handler.
    readonly TaskCompletionSource<Int32Rect?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Rectangle _bright = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly Rectangle _frame = new() { IsHitTestVisible = false, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    readonly ImageBrush _brightBrush;
    readonly Border _sizeLabel;
    readonly TextBlock _sizeText = new() { FontSize = 12, FontWeight = FontWeights.SemiBold };
    readonly Border _hint;
    Point? _start;
    bool _dragging;
    Int32Rect? _shown;

    ScreenOverlayWindow(ScreenSnapshot shot, OverlayMode mode, string hint) : base(shot, shot.Dimmed)
    {
        _mode = mode;
        _brightBrush = new ImageBrush(shot.Image) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_brightBrush, BitmapScalingMode.NearestNeighbor);
        _bright.Fill = _brightBrush;
        _frame.SetResourceReference(Shape.StrokeProperty, "AccentBrush");

        _sizeLabel = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 3, 7, 3),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _sizeText,
        };
        _sizeLabel.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
        _hint = CreateHint(hint);

        Layer.Children.Add(_bright);
        Layer.Children.Add(_frame);
        Layer.Children.Add(_sizeLabel);
        Layer.Children.Add(_hint);

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            // Right click first drops a selection in progress, then closes.
            if (_dragging || _start is not null)
            {
                _dragging = false;
                _start = null;
                ReleaseMouseCapture();
                Highlight(HoverRect(CursorPixel(Shot)));
            }
            else
            {
                Cancel();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            // Enter takes the whole monitor under the cursor.
            if (e.Key != Key.Enter) return;
            var c = CursorPixel(Shot);
            Finish(Shot.MonitorAt((int)c.X, (int)c.Y));
        };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>
    /// Shows the picker over the snapshot and returns the chosen rectangle in snapshot pixels, or null if
    /// cancelled. UI thread.
    /// </summary>
    public static Task<Int32Rect?> PickAsync(ScreenSnapshot shot, OverlayMode mode, string hint)
    {
        var window = new ScreenOverlayWindow(shot, mode, hint);
        window.Show();
        return window._result.Task;
    }

    protected override void OnShown()
    {
        PlaceHint(_hint);
        Highlight(HoverRect(CursorPixel(Shot)));
    }

    protected override void Cancel() => Finish(null);

    void Finish(Int32Rect? rect)
    {
        if (Finished) return;
        // Before closing: Close() raises Closed right away, and its handler would answer null first.
        _result.TrySetResult(rect is { Width: > 0, Height: > 0 } r ? Shot.Clip(r) : null);
        BeginFinish();
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _start = ToPixel(e.GetPosition(Layer));
        _dragging = false;
        CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        var p = ToPixel(e.GetPosition(Layer));
        if (_start is Point s && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && (Math.Abs(p.X - s.X) > DragThreshold * Dpi || Math.Abs(p.Y - s.Y) > DragThreshold * Dpi)) _dragging = true;
            if (_dragging) Highlight(Shot.Clip(FromPoints(s, p)));
            return;
        }
        Highlight(HoverRect(p));
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_start is null) return;
        ReleaseMouseCapture();
        var p = ToPixel(e.GetPosition(Layer));
        if (_dragging)
        {
            var rect = Shot.Clip(FromPoints(_start.Value, p));
            _start = null;
            _dragging = false;
            if (rect.Width >= 4 && rect.Height >= 4) Finish(rect);
            else Highlight(HoverRect(p));
            return;
        }
        _start = null;
        Finish(HoverRect(p));
    }

    static Int32Rect FromPoints(Point a, Point b)
    {
        var x1 = (int)Math.Floor(Math.Min(a.X, b.X));
        var y1 = (int)Math.Floor(Math.Min(a.Y, b.Y));
        var x2 = (int)Math.Ceiling(Math.Max(a.X, b.X));
        var y2 = (int)Math.Ceiling(Math.Max(a.Y, b.Y));
        return new Int32Rect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>The topmost window under the point, else its monitor; recording always picks the monitor.</summary>
    Int32Rect HoverRect(Point p)
    {
        var x = (int)p.X;
        var y = (int)p.Y;
        if (_mode == OverlayMode.Window)
        {
            foreach (var w in Shot.Windows)
                if (ScreenSnapshot.Contains(w, x, y)) return Shot.Clip(w);
        }
        return Shot.MonitorAt(x, y);
    }

    void Highlight(Int32Rect rect)
    {
        if (_shown is { } old && old.Equals(rect)) return;
        _shown = rect;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            _bright.Visibility = _frame.Visibility = _sizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var left = ToDip(rect.X);
        var top = ToDip(rect.Y);
        var width = ToDip(rect.Width);
        var height = ToDip(rect.Height);

        _brightBrush.Viewbox = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
        Canvas.SetLeft(_bright, left);
        Canvas.SetTop(_bright, top);
        _bright.Width = width;
        _bright.Height = height;
        _bright.Visibility = Visibility.Visible;

        // The frame sits just outside the bright area, so it never hides the edge pixels being chosen.
        Canvas.SetLeft(_frame, left - 2);
        Canvas.SetTop(_frame, top - 2);
        _frame.Width = width + 4;
        _frame.Height = height + 4;
        _frame.Visibility = Visibility.Visible;

        _sizeText.Text = $"{rect.Width} × {rect.Height}";
        _sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelTop = top - _sizeLabel.DesiredSize.Height - 6;
        // No room above (top edge of a monitor): tuck it inside the corner.
        if (labelTop < ToDip(Shot.MonitorAt(rect.X, rect.Y).Y) + 2) labelTop = top + 6;
        Canvas.SetLeft(_sizeLabel, left + (labelTop > top ? 6 : 0));
        Canvas.SetTop(_sizeLabel, labelTop);
        _sizeLabel.Visibility = Visibility.Visible;
    }
}
