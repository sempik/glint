using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Glint.Services;

namespace Glint.Mascots;

/// <summary>
/// One character: its colours, its body drawn in a square box of the given size, and where its face sits in that
/// box (as fractions). Eyes, mouth, cheeks and the little extras (Zzz, thought dots, "!", notes) are shared and
/// drawn by <see cref="MascotPainter"/>. Body geometry is built once per size and frozen.
/// </summary>
public abstract class MascotDesign
{
    readonly Dictionary<(double Size, string Key), Geometry> _geometry = new();

    public abstract string Id { get; }
    public abstract string Name { get; }
    /// <summary>One line for the picker's tooltip.</summary>
    public abstract string Blurb { get; }
    public abstract Color Body { get; }
    /// <summary>Second colour: inner ears, leaves, the antenna light.</summary>
    public virtual Color Detail => Body;
    /// <summary>Set for characters whose eyes are not the usual dark dots (the robot's screen).</summary>
    public virtual Color? EyeColor => null;

    public virtual Point EyeLeft => new(0.36, 0.58);
    public Point EyeRight => new(1 - EyeLeft.X, EyeLeft.Y);
    public virtual double EyeRadius => 0.072;
    /// <summary>Eye height to width.</summary>
    public virtual double EyeTall => 1.2;
    /// <summary>How far the eyes travel inside the face, fraction of the size.</summary>
    public virtual double LookRange => 0.045;
    public virtual Point Mouth => new(0.5, 0.72);
    public virtual double MouthWidth => 0.085;
    public virtual bool Cheeks => true;
    public virtual Point CheekLeft => new(EyeLeft.X - 0.12, EyeLeft.Y + 0.13);
    /// <summary>Top of the head (fraction); the extras float from there.</summary>
    public virtual double HeadTop => 0.12;
    /// <summary>Multipliers for the dance: hop height, squash and sway.</summary>
    public virtual double Bounce => 1;
    public virtual double Squish => 1;
    public virtual double Sway => 1;

    public abstract void DrawBody(DrawingContext dc, double s, MascotPalette pal, MascotPose pose);

    /// <summary>An open eye; the default is a dark oval with a shine.</summary>
    public virtual void DrawEye(DrawingContext dc, Point c, double rx, double ry, MascotPalette pal)
    {
        dc.DrawEllipse(pal.Eye, null, c, rx, ry);
        if (ry > rx * 0.7 && pal.Shine is not null)
            dc.DrawEllipse(pal.Shine, null, new Point(c.X - rx * 0.32, c.Y - ry * 0.36), rx * 0.36, rx * 0.36);
    }

    /// <summary>The resting mouth; a small smile unless the character has its own.</summary>
    public virtual void DrawRestingMouth(DrawingContext dc, Point c, double w, Pen pen) =>
        dc.DrawGeometry(null, pen, MascotShapes.Quad(new Point(c.X - w, c.Y), new Point(c.X, c.Y + w * 0.95), new Point(c.X + w, c.Y)));

    /// <summary>A frozen geometry for this size, built on first use.</summary>
    protected Geometry Cached(double s, string key, Func<double, Geometry> build)
    {
        if (_geometry.TryGetValue((s, key), out var g)) return g;
        g = build(s);
        if (g.CanFreeze) g.Freeze();
        // Sizes change only from Настройки; a handful of entries per character at most.
        _geometry[(s, key)] = g;
        return g;
    }

    /// <summary>A path from the unit box, scaled to the size: shapes are written once in 0..1 coordinates.</summary>
    protected Geometry Shape(double s, string key, string unitPath) => Cached(s, key, size => MascotShapes.Scaled(unitPath, size));
}

/// <summary>Brushes for one character in the current theme; rebuilt when the theme changes (ThemeService.Version).</summary>
public sealed class MascotPalette
{
    public required Brush Body { get; init; }
    public required Brush Shade { get; init; }
    public required Brush Detail { get; init; }
    public required Brush Outline { get; init; }
    public required Brush DetailOutline { get; init; }
    public required Brush Eye { get; init; }
    public Brush? Shine { get; init; }
    public required Brush Mouth { get; init; }
    public required Brush Cheek { get; init; }
    public required Brush Gloss { get; init; }
    public required Brush Accent { get; init; }
    public required Brush Accent2 { get; init; }
    public required Brush Muted { get; init; }
    public required Brush Faint { get; init; }
    public required Brush Warning { get; init; }

    public Pen OutlinePen(double s) => MascotShapes.RoundPen(Outline, Math.Max(0.9, s * 0.045));
    public Pen DetailPen(double s) => MascotShapes.RoundPen(DetailOutline, Math.Max(0.9, s * 0.045));

    static readonly Color DarkEye = Color.FromRgb(0x24, 0x1F, 0x33);
    static readonly Color LightEye = Color.FromRgb(0xF5, 0xF5, 0xF7);

    public static MascotPalette For(MascotDesign d, bool themeColors)
    {
        var body = themeColors ? ThemeService.Accent : d.Body;
        var detail = themeColors ? ThemeService.Accent2 : d.Detail;
        var eye = d.EyeColor ?? (ThemeService.Luminance(body) < 0.18 ? LightEye : DarkEye);
        var text = ThemeService.Text;
        var warning = Application.Current?.TryFindResource("WarningBrush") is SolidColorBrush w ? w.Color : Color.FromRgb(0xFB, 0xBF, 0x24);
        return new MascotPalette
        {
            Body = Frozen(body),
            Shade = Frozen(ThemeService.Mix(body, Colors.Black, 0.18)),
            Detail = Frozen(detail),
            Outline = Frozen(ThemeService.WithAlpha(ThemeService.Mix(body, Colors.Black, 0.5), 0xE6)),
            DetailOutline = Frozen(ThemeService.WithAlpha(ThemeService.Mix(detail, Colors.Black, 0.5), 0xE6)),
            Eye = Frozen(eye),
            // Glowing eyes (the robot) have no shine; dark ones get a white glint.
            Shine = d.EyeColor is null && eye == DarkEye ? Frozen(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)) : null,
            Mouth = Frozen(eye),
            Cheek = Frozen(Color.FromArgb(0x66, 0xFF, 0x6F, 0x91)),
            Gloss = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
            Accent = Frozen(ThemeService.Accent),
            Accent2 = Frozen(ThemeService.Accent2),
            Muted = Frozen(ThemeService.WithAlpha(text, 0xC0)),
            Faint = Frozen(ThemeService.WithAlpha(text, 0x48)),
            Warning = Frozen(warning),
        };
    }

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Small geometry helpers shared by the characters and the painter.</summary>
public static partial class MascotShapes
{
    [GeneratedRegex(@"-?\d*\.?\d+")]
    private static partial Regex NumberPattern();

    /// <summary>Scales every number of a path written in the unit box (M, L, C, Q, Z only; no arcs, whose flags would scale too).</summary>
    public static Geometry Scaled(string unitPath, double s) =>
        Geometry.Parse(NumberPattern().Replace(unitPath, m =>
            (double.Parse(m.Value, CultureInfo.InvariantCulture) * s).ToString("0.###", CultureInfo.InvariantCulture)));

    public static Pen RoundPen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    public static StreamGeometry Quad(Point from, Point control, Point to, bool closed = false)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(from, closed, closed);
            ctx.QuadraticBezierTo(control, to, true, true);
        }
        g.Freeze();
        return g;
    }

    public static StreamGeometry Polyline(bool closed, params Point[] points)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(points[0], closed, closed);
            for (var i = 1; i < points.Length; i++) ctx.LineTo(points[i], true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>A five-point star with softly rounded tips, centre and radii in the unit box.</summary>
    public static Geometry RoundedStar(double s, Point center, double outer, double inner)
    {
        var points = new Point[10];
        for (var i = 0; i < 10; i++)
        {
            var angle = -Math.PI / 2 + i * Math.PI / 5;
            var r = (i % 2 == 0 ? outer : inner) * s;
            points[i] = new Point(center.X * s + r * Math.Cos(angle), center.Y * s + r * Math.Sin(angle));
        }
        static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            for (var i = 0; i < 10; i++)
            {
                var p = points[i];
                var prev = points[(i + 9) % 10];
                var next = points[(i + 1) % 10];
                // Tips get a wider curve than the inner corners.
                var t = i % 2 == 0 ? 0.2 : 0.12;
                var a = Lerp(p, prev, t);
                var b = Lerp(p, next, t);
                if (i == 0) ctx.BeginFigure(a, true, true);
                else ctx.LineTo(a, true, true);
                ctx.QuadraticBezierTo(p, b, true, true);
            }
        }
        return g;
    }
}
