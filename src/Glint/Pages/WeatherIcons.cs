using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Glint.Services.Weather;

namespace Glint.Pages;

/// <summary>
/// Glint's own small weather icons, drawn from a few circles and strokes in a 24×24 box: sun, moon, cloud, drops,
/// flakes, a bolt and fog lines. Everything is filled geometry (strokes are widened once), frozen and shared, so
/// an icon is a handful of Paths; the sun and the cloud follow the theme brushes.
/// </summary>
public static class WeatherIcons
{
    // Rain reads as blue whatever the accents are; this one has enough contrast on dark and light surfaces.
    static readonly Brush RainBrush = Frozen(Color.FromRgb(0x4F, 0x9D, 0xF5));

    static readonly Geometry Sun = Union(new EllipseGeometry(new Point(12, 12), 4.3, 4.3), Rays(new Point(12, 12), 6.8, 9.4, 1.8));
    static readonly Geometry Moon = Frozen(Geometry.Combine(
        new EllipseGeometry(new Point(11.5, 12.5), 7.2, 7.2), new EllipseGeometry(new Point(15.6, 9), 6.2, 6.2), GeometryCombineMode.Exclude, null));

    static readonly Geometry Cloud = Frozen(CloudShape());
    // Raised and a little smaller, to leave room for drops or flakes under it.
    static readonly Geometry CloudHigh = Transformed(CloudShape(), new TransformGroup { Children = { new ScaleTransform(0.86, 0.86, 12.8, 5), new TranslateTransform(0, -1.6) } });
    // Lower right, with the sun or moon peeking out above.
    static readonly Geometry CloudFront = Transformed(CloudShape(), new ScaleTransform(0.8, 0.8, 22, 21));

    static readonly Geometry SmallSun = Union(new EllipseGeometry(new Point(8.5, 8.5), 3.4, 3.4), Rays(new Point(8.5, 8.5), 5.4, 7.4, 1.5));
    static readonly Geometry SmallMoon = Frozen(Geometry.Combine(
        new EllipseGeometry(new Point(8.6, 8.8), 5, 5), new EllipseGeometry(new Point(11.6, 6.4), 4.3, 4.3), GeometryCombineMode.Exclude, null));

    static readonly Geometry Drops = Strokes(1.7, (8.6, 17.6, 7.3, 21.2), (12.8, 17.6, 11.5, 21.2), (17, 17.6, 15.7, 21.2));
    static readonly Geometry Drizzle = Strokes(1.5, (10, 18, 9.3, 20), (15, 18, 14.3, 20));
    static readonly Geometry Flakes = Union(Dot(8, 18.6), Dot(12.6, 20.6), Dot(17, 18.6), Dot(10.2, 22.4), Dot(14.8, 22.4));
    static readonly Geometry SleetDrops = Strokes(1.7, (8.6, 17.6, 7.3, 21.2), (17, 17.6, 15.7, 21.2));
    static readonly Geometry SleetFlakes = Union(Dot(12.4, 19.2), Dot(11.4, 22.6));
    static readonly Geometry Bolt = Frozen(Geometry.Parse("M13.2,14.6 L9.4,19.8 L12.1,19.8 L10.9,23.6 L15.6,18 L12.9,18 L14.6,14.6 Z"));
    static readonly Geometry FogLines = Strokes(1.6, (4.5, 18.4, 19.5, 18.4), (7.5, 21.4, 16.5, 21.4));

    /// <summary>An icon for a WMO code, <paramref name="size"/> DIPs square.</summary>
    public static FrameworkElement Create(int code, bool isDay, double size)
    {
        var box = new Grid { Width = 24, Height = 24 };
        switch (OpenMeteo.Kind(code))
        {
            case WeatherKind.Clear:
                if (isDay) Add(box, Sun, "WarningBrush");
                else Add(box, Moon, "TextBrush", 0.8);
                break;
            case WeatherKind.PartlyCloudy:
                AddBehindCloud(box, isDay ? SmallSun : SmallMoon, isDay ? "WarningBrush" : "TextBrush", isDay ? 1 : 0.7, CloudFront);
                Add(box, CloudFront, "TextBrush", 0.9);
                break;
            case WeatherKind.Cloudy:
                Add(box, Cloud, "TextBrush", 0.9);
                break;
            case WeatherKind.Fog:
                Add(box, CloudHigh, "TextBrush", 0.75);
                Add(box, FogLines, "MutedBrush");
                break;
            case WeatherKind.Drizzle:
                Add(box, CloudHigh, "TextBrush", 0.9);
                Add(box, Drizzle, RainBrush);
                break;
            case WeatherKind.Rain:
                Add(box, CloudHigh, "TextBrush", 0.9);
                Add(box, Drops, RainBrush);
                break;
            case WeatherKind.Showers:
                // A shower comes with sun in between, so the sun peeks out (by day).
                if (isDay) AddBehindCloud(box, SmallSun, "WarningBrush", 1, CloudHigh);
                Add(box, CloudHigh, "TextBrush", 0.9);
                Add(box, Drops, RainBrush);
                break;
            case WeatherKind.Sleet:
                Add(box, CloudHigh, "TextBrush", 0.9);
                Add(box, SleetDrops, RainBrush);
                Add(box, SleetFlakes, "TextBrush");
                break;
            case WeatherKind.Snow:
                Add(box, CloudHigh, "TextBrush", 0.9);
                Add(box, Flakes, "TextBrush");
                break;
            case WeatherKind.Thunder:
                Add(box, CloudHigh, "TextBrush", 0.9);
                Add(box, Bolt, "WarningBrush");
                break;
        }
        return new Viewbox { Width = size, Height = size, Child = box, IsHitTestVisible = false };
    }

    static void Add(Grid box, Geometry shape, string brushKey, double opacity = 1)
    {
        var path = new Path { Data = shape, Opacity = opacity };
        path.SetResourceReference(Shape.FillProperty, brushKey);
        box.Children.Add(path);
    }

    static void Add(Grid box, Geometry shape, Brush brush) => box.Children.Add(new Path { Data = shape, Fill = brush });

    /// <summary>The sun or moon with a thin gap cut around the cloud in front, so the two read apart.</summary>
    static void AddBehindCloud(Grid box, Geometry shape, string brushKey, double opacity, Geometry cloud) =>
        Add(box, CutGap(shape, cloud), brushKey, opacity);

    static readonly Dictionary<(Geometry, Geometry), Geometry> Cuts = new();

    static Geometry CutGap(Geometry shape, Geometry cloud)
    {
        if (Cuts.TryGetValue((shape, cloud), out var cut)) return cut;
        var gap = Geometry.Combine(cloud, cloud.GetWidenedPathGeometry(new Pen(Brushes.Black, 2.6)), GeometryCombineMode.Union, null);
        cut = Frozen(Geometry.Combine(shape, gap, GeometryCombineMode.Exclude, null));
        Cuts[(shape, cloud)] = cut;
        return cut;
    }

    // ───────────── Building blocks ─────────────

    static Geometry CloudShape()
    {
        Geometry g = new EllipseGeometry(new Point(8.6, 14.4), 4.2, 4.2);
        g = Geometry.Combine(g, new EllipseGeometry(new Point(13.6, 11.2), 5.4, 5.4), GeometryCombineMode.Union, null);
        g = Geometry.Combine(g, new EllipseGeometry(new Point(17.9, 15), 3.7, 3.7), GeometryCombineMode.Union, null);
        g = Geometry.Combine(g, new RectangleGeometry(new Rect(4.4, 14.4, 17.2, 4.3), 2.15, 2.15), GeometryCombineMode.Union, null);
        return g;
    }

    /// <summary>Eight short rays around a centre, as filled shapes.</summary>
    static Geometry Rays(Point c, double inner, double outer, double thickness)
    {
        var lines = new StreamGeometry();
        using (var ctx = lines.Open())
        {
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                var (sin, cos) = Math.SinCos(a);
                ctx.BeginFigure(new Point(c.X + inner * cos, c.Y + inner * sin), false, false);
                ctx.LineTo(new Point(c.X + outer * cos, c.Y + outer * sin), true, false);
            }
        }
        return Widen(lines, thickness);
    }

    static Geometry Strokes(double thickness, params (double X1, double Y1, double X2, double Y2)[] lines)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            foreach (var (x1, y1, x2, y2) in lines)
            {
                ctx.BeginFigure(new Point(x1, y1), false, false);
                ctx.LineTo(new Point(x2, y2), true, false);
            }
        }
        return Frozen(Widen(g, thickness));
    }

    static Geometry Widen(Geometry lines, double thickness) =>
        lines.GetWidenedPathGeometry(new Pen(Brushes.Black, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    static Geometry Dot(double x, double y) => new EllipseGeometry(new Point(x, y), 1.25, 1.25);

    static Geometry Union(params Geometry[] parts)
    {
        var g = parts[0];
        for (var i = 1; i < parts.Length; i++) g = Geometry.Combine(g, parts[i], GeometryCombineMode.Union, null);
        return Frozen(g);
    }

    static Geometry Transformed(Geometry g, Transform t) => Frozen(Geometry.Combine(g, Geometry.Empty, GeometryCombineMode.Union, t));

    static Geometry Frozen(Geometry g)
    {
        if (g.CanFreeze) g.Freeze();
        return g;
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
