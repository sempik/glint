using System.Windows;
using System.Windows.Media;

namespace Glint.Mascots;

/// <summary>The characters offered in Настройки → Персонаж. All are Glint's own drawings, built from simple shapes.</summary>
public static class MascotDesigns
{
    public static IReadOnlyList<MascotDesign> All { get; } = new MascotDesign[]
    {
        new Puff(), new Whisker(), new Boo(), new Bolt(), new Jelly(), new Spark(), new Sprout(),
    };

    public static MascotDesign? Find(string? id) => All.FirstOrDefault(d => d.Id == id);
}

/// <summary>Пух: a soft round blob with a single curl on top.</summary>
sealed class Puff : MascotDesign
{
    public override string Id => "puff";
    public override string Name => "Пух";
    public override string Blurb => "Мягкий и круглый, с завитком на макушке";
    public override Color Body => Color.FromRgb(0x8F, 0xD3, 0xFF);
    public override double HeadTop => 0.14;

    public override void DrawBody(DrawingContext dc, double s, MascotPalette pal, MascotPose pose)
    {
        var outline = pal.OutlinePen(s);
        dc.DrawGeometry(null, outline, Shape(s, "curl", "M 0.5,0.16 C 0.46,0.06 0.56,0.01 0.61,0.07"));
        dc.DrawGeometry(pal.Body, outline, Shape(s, "body",
            "M 0.5,0.14 C 0.78,0.14 0.95,0.40 0.95,0.64 C 0.95,0.86 0.76,0.96 0.5,0.96 C 0.24,0.96 0.05,0.86 0.05,0.64 C 0.05,0.40 0.22,0.14 0.5,0.14 Z"));
        dc.DrawEllipse(pal.Gloss, null, new Point(0.31 * s, 0.33 * s), 0.09 * s, 0.055 * s);
    }
}

/// <summary>Усик: a tiny round-faced creature with pointed ears and whiskers.</summary>
sealed class Whisker : MascotDesign
{
    public override string Id => "whisker";
    public override string Name => "Усик";
    public override string Blurb => "Ушастый, с усами и полосками на лбу";
    public override Color Body => Color.FromRgb(0xF7, 0xB2, 0x67);
    public override Color Detail => Color.FromRgb(0xFF, 0xC9, 0xD6);
    public override Point EyeLeft => new(0.36, 0.59);
    public override Point Mouth => new(0.5, 0.735);
    public override double MouthWidth => 0.075;
    public override double HeadTop => 0.08;

    public override void DrawBody(DrawingContext dc, double s, MascotPalette pal, MascotPose pose)
    {
        var outline = pal.OutlinePen(s);
        dc.DrawGeometry(pal.Body, outline, Shape(s, "earL", "M 0.13,0.50 L 0.20,0.08 L 0.46,0.31 Z"));
        dc.DrawGeometry(pal.Body, outline, Shape(s, "earR", "M 0.87,0.50 L 0.80,0.08 L 0.54,0.31 Z"));
        dc.DrawGeometry(pal.Detail, null, Shape(s, "innerL", "M 0.20,0.39 L 0.235,0.18 L 0.37,0.31 Z"));
        dc.DrawGeometry(pal.Detail, null, Shape(s, "innerR", "M 0.80,0.39 L 0.765,0.18 L 0.63,0.31 Z"));
        dc.DrawGeometry(pal.Body, outline, Cached(s, "head", size => new EllipseGeometry(new Point(0.5 * size, 0.6 * size), 0.42 * size, 0.36 * size)));
        var stripes = MascotShapes.RoundPen(pal.Shade, Math.Max(0.9, s * 0.045));
        dc.DrawGeometry(null, stripes, Shape(s, "stripes", "M 0.5,0.27 L 0.5,0.35 M 0.42,0.29 L 0.43,0.34 M 0.58,0.29 L 0.57,0.34"));
        var whiskers = MascotShapes.RoundPen(pal.Outline, Math.Max(0.7, s * 0.028));
        dc.DrawGeometry(null, whiskers, Shape(s, "whiskers",
            "M 0.19,0.70 L 0.02,0.66 M 0.19,0.75 L 0.03,0.78 M 0.81,0.70 L 0.98,0.66 M 0.81,0.75 L 0.97,0.78"));
        dc.DrawGeometry(pal.Mouth, null, Shape(s, "nose", "M 0.465,0.665 L 0.535,0.665 L 0.5,0.705 Z"));
    }

    /// <summary>The little "w" under the nose.</summary>
    public override void DrawRestingMouth(DrawingContext dc, Point c, double w, Pen pen)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X - w, c.Y - w * 0.15), false, false);
            ctx.QuadraticBezierTo(new Point(c.X - w * 0.5, c.Y + w * 0.75), new Point(c.X, c.Y), true, true);
            ctx.QuadraticBezierTo(new Point(c.X + w * 0.5, c.Y + w * 0.75), new Point(c.X + w, c.Y - w * 0.15), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>Бу: a small friendly ghost with a wavy hem; floats more than it jumps.</summary>
sealed class Boo : MascotDesign
{
    public override string Id => "boo";
    public override string Name => "Бу";
    public override string Blurb => "Добрый призрак: парит и покачивается";
    public override Color Body => Color.FromRgb(0xEE, 0xF0, 0xFF);
    public override Point EyeLeft => new(0.38, 0.44);
    public override double EyeRadius => 0.068;
    public override double EyeTall => 1.45;
    public override Point Mouth => new(0.5, 0.6);
    public override Point CheekLeft => new(0.26, 0.55);
    public override double HeadTop => 0.08;
    public override double Bounce => 0.75;
    public override double Sway => 1.4;

    public override void DrawBody(DrawingContext dc, double s, MascotPalette pal, MascotPose pose)
    {
        dc.DrawGeometry(pal.Body, pal.OutlinePen(s), Shape(s, "body",
            "M 0.14,0.92 L 0.14,0.46 C 0.14,0.22 0.30,0.08 0.5,0.08 C 0.70,0.08 0.86,0.22 0.86,0.46 L 0.86,0.92 " +
            "Q 0.80,0.82 0.74,0.92 Q 0.68,1.0 0.62,0.92 Q 0.56,0.82 0.5,0.92 Q 0.44,1.0 0.38,0.92 Q 0.32,0.82 0.26,0.92 Q 0.20,1.0 0.14,0.92 Z"));
        dc.DrawEllipse(pal.Gloss, null, new Point(0.3 * s, 0.24 * s), 0.07 * s, 0.045 * s);
    }
}

/// <summary>Болтик: a boxy robot with a glowing face screen and an antenna light that blinks while it thinks.</summary>
sealed class Bolt : MascotDesign
{
    static readonly Brush ScreenBrush = Frozen(Color.FromRgb(0x26, 0x30, 0x3F));

    public override string Id => "bolt";
    public override string Name => "Болтик";
    public override string Blurb => "Робот с экраном вместо лица и лампочкой на антенне";
    public override Color Body => Color.FromRgb(0xB9, 0xC6, 0xD8);
    public override Color Detail => Color.FromRgb(0xFF, 0x7A, 0x7A);
    public override Color? EyeColor => Color.FromRgb(0x7D, 0xF9, 0xFF);
    public override Point EyeLeft => new(0.38, 0.55);
    public override double EyeRadius => 0.065;
    public override double EyeTall => 1.25;
    public override double LookRange => 0.03;
    public override Point Mouth => new(0.5, 0.69);
    public override double MouthWidth => 0.07;
    public override bool Cheeks => false;
    public override double HeadTop => 0.04;
    public override double Squish => 0.5;

    public override void DrawBody(DrawingContext dc, double s, MascotPalette pal, MascotPose pose)
    {
        var outline = pal.OutlinePen(s);
        dc.DrawGeometry(null, outline, Shape(s, "antenna", "M 0.5,0.23 L 0.5,0.1"));
        // The light follows the thought dots; otherwise it stays in the character's own colour.
        var light = pose.Face == MascotFace.Think && pose.Dots % 2 == 1 ? pal.Accent : pal.Detail;
        dc.DrawEllipse(light, outline, new Point(0.5 * s, 0.085 * s), 0.065 * s, 0.065 * s);
        dc.DrawGeometry(pal.Shade, outline, Cached(s, "ears", size => new GeometryGroup
        {
            Children =
            {
                new RectangleGeometry(new Rect(0.03 * size, 0.48 * size, 0.1 * size, 0.22 * size), 0.03 * size, 0.03 * size),
                new RectangleGeometry(new Rect(0.87 * size, 0.48 * size, 0.1 * size, 0.22 * size), 0.03 * size, 0.03 * size),
            },
        }));
        dc.DrawGeometry(pal.Body, outline, Cached(s, "head", size =>
            new RectangleGeometry(new Rect(0.1 * size, 0.22 * size, 0.8 * size, 0.7 * size), 0.18 * size, 0.18 * size)));
        dc.DrawGeometry(ScreenBrush, null, Cached(s, "screen", size =>
            new RectangleGeometry(new Rect(0.2 * size, 0.37 * size, 0.6 * size, 0.42 * size), 0.12 * size, 0.12 * size)));
    }

    /// <summary>Square-ish glowing pixels instead of round eyes.</summary>
    public override void DrawEye(DrawingContext dc, Point c, double rx, double ry, MascotPalette pal)
    {
        var r = Math.Min(rx, ry) * 0.55;
        dc.DrawRoundedRectangle(pal.Eye, null, new Rect(c.X - rx, c.Y - ry, rx * 2, ry * 2), r, r);
    }

    /// <summary>A flat line with a slight upturn: the robot's smile.</summary>
    public override void DrawRestingMouth(DrawingContext dc, Point c, double w, Pen pen) =>
        dc.DrawGeometry(null, pen, MascotShapes.Polyline(false,
            new Point(c.X - w, c.Y - w * 0.2), new Point(c.X - w * 0.6, c.Y + w * 0.15), new Point(c.X + w * 0.6, c.Y + w * 0.15), new Point(c.X + w, c.Y - w * 0.2)));

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Желе: a glossy slime drop that squishes the most when it dances.</summary>
sealed class Jelly : MascotDesign
{
    public override string Id => "jelly";
    public override string Name => "Желе";
    public override string Blurb => "Блестящая капля: пружинит сильнее всех";
    public override Color Body => Color.FromRgb(0x7E, 0xE0, 0x8A);
    public override Point EyeLeft => new(0.38, 0.6);
    public override double EyeRadius => 0.07;
    public override Point Mouth => new(0.5, 0.745);
    public override Point CheekLeft => new(0.25, 0.72);
    public override double HeadTop => 0.24;
    public override double Bounce => 0.9;
    public override double Squish => 1.8;

    public override void DrawBody(DrawingContext dc, double s, MascotPalette pal, MascotPose pose)
    {
        dc.DrawGeometry(pal.Body, pal.OutlinePen(s), Shape(s, "body",
            "M 0.05,0.93 C 0.05,0.62 0.20,0.24 0.5,0.24 C 0.80,0.24 0.95,0.62 0.95,0.93 " +
            "Q 0.88,0.99 0.80,0.95 Q 0.72,0.91 0.66,0.96 Q 0.58,1.0 0.5,0.95 Q 0.42,0.91 0.34,0.96 Q 0.26,1.0 0.20,0.95 Q 0.12,0.91 0.05,0.93 Z"));
        var gloss = MascotShapes.RoundPen(pal.Gloss, Math.Max(1, s * 0.05));
        dc.DrawGeometry(null, gloss, Shape(s, "gloss", "M 0.22,0.58 C 0.23,0.45 0.30,0.36 0.40,0.32"));
        dc.DrawEllipse(pal.Gloss, null, new Point(0.72 * s, 0.82 * s), 0.035 * s, 0.035 * s);
    }
}

/// <summary>Искра: a plump five-point star with rounded tips.</summary>
sealed class Spark : MascotDesign
{
    public override string Id => "spark";
    public override string Name => "Искра";
    public override string Blurb => "Пухлая звёздочка с круглыми лучами";
    public override Color Body => Color.FromRgb(0xFF, 0xD1, 0x66);
    public override Point EyeLeft => new(0.42, 0.56);
    public override double EyeRadius => 0.056;
    public override double LookRange => 0.035;
    public override Point Mouth => new(0.5, 0.665);
    public override double MouthWidth => 0.065;
    public override Point CheekLeft => new(0.33, 0.645);
    public override double HeadTop => 0.1;
    public override double Sway => 1.3;

    public override void DrawBody(DrawingContext dc, double s, MascotPalette pal, MascotPose pose)
    {
        dc.DrawGeometry(pal.Body, pal.OutlinePen(s), Cached(s, "star", size => MascotShapes.RoundedStar(size, new Point(0.5, 0.56), 0.47, 0.235)));
        dc.DrawEllipse(pal.Gloss, null, new Point(0.4 * s, 0.38 * s), 0.04 * s, 0.03 * s);
    }
}

/// <summary>Росток: a little bean with two leaves sprouting from its head.</summary>
sealed class Sprout : MascotDesign
{
    public override string Id => "sprout";
    public override string Name => "Росток";
    public override string Blurb => "Фасолинка с двумя листиками на макушке";
    public override Color Body => Color.FromRgb(0xF2, 0xD4, 0xA7);
    public override Color Detail => Color.FromRgb(0x6F, 0xD0, 0x8C);
    public override Point EyeLeft => new(0.38, 0.64);
    public override double EyeRadius => 0.068;
    public override Point Mouth => new(0.5, 0.775);
    public override Point CheekLeft => new(0.25, 0.76);
    public override double HeadTop => 0.04;

    public override void DrawBody(DrawingContext dc, double s, MascotPalette pal, MascotPose pose)
    {
        var leafPen = pal.DetailPen(s);
        dc.DrawGeometry(null, leafPen, Shape(s, "stem", "M 0.5,0.28 C 0.5,0.21 0.51,0.17 0.52,0.13"));
        dc.DrawGeometry(pal.Detail, leafPen, Shape(s, "leafL", "M 0.51,0.14 C 0.42,0.02 0.26,0.04 0.19,0.12 C 0.27,0.21 0.44,0.21 0.51,0.14 Z"));
        dc.DrawGeometry(pal.Detail, leafPen, Shape(s, "leafR", "M 0.53,0.13 C 0.60,0.0 0.78,0.0 0.85,0.08 C 0.77,0.18 0.60,0.20 0.53,0.13 Z"));
        dc.DrawGeometry(pal.Body, pal.OutlinePen(s), Shape(s, "body",
            "M 0.5,0.26 C 0.76,0.26 0.92,0.52 0.92,0.70 C 0.92,0.88 0.74,0.97 0.5,0.97 C 0.26,0.97 0.08,0.88 0.08,0.70 C 0.08,0.52 0.24,0.26 0.5,0.26 Z"));
        dc.DrawEllipse(pal.Gloss, null, new Point(0.3 * s, 0.45 * s), 0.07 * s, 0.045 * s);
    }
}
