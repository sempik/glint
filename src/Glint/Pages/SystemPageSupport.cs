using System.Windows;
using System.Windows.Media;

namespace Glint.Pages;

/// <summary>Shared look for the small load graphics on the "Система" tab.</summary>
static class LoadPalette
{
    /// <summary>Above this a bar turns red.</summary>
    public const double HotPercent = 90;

    public static Brush Accent(FrameworkElement e) => (Brush)e.FindResource("AccentFillBrush");
    public static Brush Danger(FrameworkElement e) => (Brush)e.FindResource("DangerBrush");
    public static Brush Track(FrameworkElement e) => (Brush)e.FindResource("CardHoverBrush");

    /// <summary>Equalizer-style vertical gradient (accent 2 at the bottom, accent at the top).</summary>
    public static Brush Vertical(FrameworkElement e) => (Brush)e.FindResource("AccentVerticalBrush");

    /// <summary>Fading accent fill under a sparkline.</summary>
    public static Brush Area(FrameworkElement e) => (Brush)e.FindResource("AccentAreaBrush");

    // All brushes are looked up on every render: ThemeService replaces them and the page invalidates the graphs.
}

/// <summary>Thin rounded progress track, 0..100. Red above <see cref="LoadPalette.HotPercent"/>.</summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MeterBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public MeterBar()
    {
        Height = 4;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = h / 2;
        dc.DrawRoundedRectangle(LoadPalette.Track(this), null, new Rect(0, 0, w, h), r, r);

        var v = double.IsFinite(Value) ? Math.Clamp(Value, 0, 100) : 0;
        if (v <= 0) return;
        // Never thinner than the corner radius, so a tiny value still reads as a dot, not a glitch.
        var fill = Math.Max(h, w * v / 100);
        dc.DrawRoundedRectangle(v > LoadPalette.HotPercent ? LoadPalette.Danger(this) : LoadPalette.Accent(this), null, new Rect(0, 0, fill, h), r, r);
    }
}

/// <summary>Area sparkline of the last minute (0..100), newest value at the right edge.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>How many samples span the full width; fewer samples fill from the right.</summary>
    public int Capacity { get; set; } = Services.SystemMonitor.HistoryLength;

    Pen? _stroke;
    Brush? _area;
    int _themeVersion = -1;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Faint baseline so an idle machine still shows where the graph is.
        dc.DrawRectangle(LoadPalette.Track(this), null, new Rect(0, h - 1, w, 1));

        var values = Values;
        if (values is null || values.Length < 2) return;

        // Rebuild the cached pen after a theme change.
        if (_stroke is null || _themeVersion != Services.ThemeService.Version)
        {
            _themeVersion = Services.ThemeService.Version;
            _stroke = new Pen(LoadPalette.Accent(this), 1.5) { LineJoin = PenLineJoin.Round };
            _stroke.Freeze();
            _area = LoadPalette.Area(this);
        }

        var step = w / Math.Max(1, Capacity - 1);
        var top = 1.0; // keep the stroke inside the box at 100%
        double X(int i) => w - (values.Length - 1 - i) * step;
        double Y(double v) => top + (h - top - 1) * (1 - Math.Clamp(double.IsFinite(v) ? v : 0, 0, 100) / 100);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(X(0), Y(values[0])), false, false);
            for (var i = 1; i < values.Length; i++) g.LineTo(new Point(X(i), Y(values[i])), true, true);
        }
        line.Freeze();

        var fill = new StreamGeometry();
        using (var g = fill.Open())
        {
            g.BeginFigure(new Point(X(0), h), true, true);
            for (var i = 0; i < values.Length; i++) g.LineTo(new Point(X(i), Y(values[i])), false, false);
            g.LineTo(new Point(w, h), false, false);
        }
        fill.Freeze();

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        dc.DrawGeometry(_area, null, fill);
        dc.DrawGeometry(null, _stroke, line);
        dc.Pop();
    }
}

/// <summary>One thin vertical bar per logical processor.</summary>
public sealed class CoreStrip : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(CoreStrip), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        var w = ActualWidth;
        var h = ActualHeight;
        if (values is null || values.Count == 0 || w <= 0 || h <= 0) return;

        var fill = LoadPalette.Vertical(this);
        var track = LoadPalette.Track(this);
        var danger = LoadPalette.Danger(this);

        // Gap shrinks on many-core machines so bars never vanish; 64+ threads still get a hairline each.
        var gap = values.Count > 32 ? 1.0 : 3.0;
        var barWidth = Math.Max(1, (w - gap * (values.Count - 1)) / values.Count);
        var r = Math.Min(1.5, barWidth / 2);
        for (var i = 0; i < values.Count; i++)
        {
            var x = i * (barWidth + gap);
            if (x >= w) break;
            dc.DrawRoundedRectangle(track, null, new Rect(x, 0, barWidth, h), r, r);
            var v = double.IsFinite(values[i]) ? Math.Clamp(values[i], 0, 100) : 0;
            if (v <= 0) continue;
            var bar = Math.Max(2, h * v / 100);
            dc.DrawRoundedRectangle(v > LoadPalette.HotPercent ? danger : fill, null, new Rect(x, h - bar, barWidth, bar), r, r);
        }
    }
}
