using System.Windows;
using System.Windows.Media;
using Glint.Services;

namespace Glint.Mascots;

/// <summary>
/// Draws one character in a small fixed box: the body in a square of <see cref="BodySize"/> and, beside it, a narrow
/// lane for the extras (Zzz, thought dots, "!", notes). A new pose is drawn into a retained drawing that OnRender
/// placed once, so a frame touches neither layout nor anything outside this box (InvalidateVisual would also
/// run an arrange pass each time).
/// </summary>
public sealed class MascotView : FrameworkElement
{
    /// <summary>
    /// Animated by <see cref="MascotDirector"/> as its frame clock. A WPF animation (not a timer) so its frames
    /// coincide with the equalizer's instead of adding to them.
    /// </summary>
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(double), typeof(MascotView),
        new PropertyMetadata(0.0, (d, _) => ((MascotView)d).FrameTick?.Invoke()));

    readonly DrawingGroup _drawing = new();
    MascotDesign? _design;
    MascotPalette? _palette;
    int _paletteVersion = -1;
    double _body = 24;
    bool _mirror;
    bool _themeColors;
    MascotPose _pose = MascotPose.Rest;

    public MascotView()
    {
        IsHitTestVisible = false;
        Focusable = false;
        // A theme change (or new accents with "в цветах темы") repaints the character once.
        Loaded += (_, _) => ThemeService.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
    }

    public double Frame
    {
        get => (double)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    internal Action? FrameTick { get; set; }

    public MascotDesign? Design => _design;
    public double BodySize => _body;

    /// <summary>Sets the character and the box: body size, total height, whether there is a lane for extras and on which side.</summary>
    public void Configure(MascotDesign design, double bodySize, double height, bool lane, bool mirror, bool themeColors)
    {
        _design = design;
        _body = bodySize;
        _mirror = mirror;
        _themeColors = themeColors;
        _palette = null;
        Width = bodySize + (lane ? Math.Round(bodySize * 0.45) : 0);
        Height = height;
        Redraw();
    }

    public MascotPose Pose
    {
        get => _pose;
        set
        {
            if (value == _pose) return;
            _pose = value;
            Redraw();
        }
    }

    /// <summary>The point between the eyes at rest, in this element's coordinates (for aiming the gaze).</summary>
    public Point EyeCenter
    {
        get
        {
            if (_design is null) return new Point(Width / 2, Height / 2);
            var (x, y) = MascotPainter.BodyOrigin(_body, Width, Height, _mirror);
            return new Point(x + _body * 0.5, y + _body * _design.EyeLeft.Y);
        }
    }

    void OnThemeChanged(object? sender, EventArgs e) => Redraw();

    /// <summary>Replaces the retained drawing; WPF re-renders just this element's area.</summary>
    void Redraw()
    {
        using var dc = _drawing.Open();
        if (_design is null) return;
        if (_palette is null || _paletteVersion != ThemeService.Version)
        {
            _palette = MascotPalette.For(_design, _themeColors);
            _paletteVersion = ThemeService.Version;
        }
        // The box size is fixed by Configure, so the explicit Width/Height are the render size too.
        MascotPainter.Draw(dc, _design, _body, Width, Height, _mirror, _pose, _palette);
    }

    protected override void OnRender(DrawingContext dc) => dc.DrawDrawing(_drawing);
}

/// <summary>The shared part of every character: posing the body, the face, and the extras in the lane.</summary>
static class MascotPainter
{
    /// <summary>Top-left of the body square: at the outer end of the box, slightly below centre (it sits on the hem).</summary>
    public static (double X, double Y) BodyOrigin(double s, double width, double height, bool mirror) =>
        (mirror ? width - s : 0, Math.Round((height - s) / 2 + s * 0.04));

    public static void Draw(DrawingContext dc, MascotDesign d, double s, double width, double height, bool mirror, MascotPose p, MascotPalette pal)
    {
        var (bodyX, bodyY) = BodyOrigin(s, width, height, mirror);
        // A jump stops about where the pill's edge is; only the largest size, swaying, pokes out a little.
        var hopRoom = Math.Max(1.5, Math.Min(s * 0.2, bodyY + 0.5));
        var hop = Math.Clamp(p.Hop * d.Bounce, 0, 1.15) * hopRoom;
        var squash = Math.Clamp(1 + p.SquashDelta * d.Squish, 0.8, 1.3);

        var m = Matrix.Identity;
        // Squash and sway pivot on the feet, so the character stays planted.
        m.ScaleAt(squash, 1 / squash, s / 2, s);
        if (p.Tilt != 0) m.RotateAt(p.Tilt * d.Sway, s / 2, s * 0.92);
        m.Translate(bodyX, bodyY - hop);
        dc.PushTransform(new MatrixTransform(m));
        d.DrawBody(dc, s, pal, p);
        DrawFace(dc, d, s, pal, p);
        dc.Pop();

        DrawExtras(dc, d, s, bodyX, bodyY - hop, mirror, pal, p);
    }

    static void DrawFace(DrawingContext dc, MascotDesign d, double s, MascotPalette pal, MascotPose p)
    {
        var r = d.EyeRadius * s;
        var range = d.LookRange * s;
        var ox = Math.Clamp(p.LookX, -1, 1) * range;
        var oy = Math.Clamp(p.LookY, -1, 1) * range * 0.8;
        var left = new Point(d.EyeLeft.X * s, d.EyeLeft.Y * s);
        var right = new Point(d.EyeRight.X * s, d.EyeRight.Y * s);

        if (d.Cheeks)
        {
            var cl = new Point(d.CheekLeft.X * s, d.CheekLeft.Y * s);
            dc.DrawEllipse(pal.Cheek, null, cl, r * 1.15, r * 0.7);
            dc.DrawEllipse(pal.Cheek, null, new Point(s - cl.X, cl.Y), r * 1.15, r * 0.7);
        }

        var linePen = MascotShapes.RoundPen(pal.Eye, Math.Max(1, r * 0.55));
        switch (p.Face)
        {
            case MascotFace.Sleep:
                // Closed: a soft downward curve.
                foreach (var c in new[] { left, right })
                    dc.DrawGeometry(null, linePen, MascotShapes.Quad(new Point(c.X - r, c.Y), new Point(c.X, c.Y + r * 0.9), new Point(c.X + r, c.Y)));
                break;
            case MascotFace.Happy:
                // Squeezed shut with joy: upside-down arcs.
                foreach (var c in new[] { left, right })
                    dc.DrawGeometry(null, linePen, MascotShapes.Quad(new Point(c.X - r, c.Y + r * 0.35), new Point(c.X, c.Y - r * 1.1), new Point(c.X + r, c.Y + r * 0.35)));
                break;
            default:
                var size = p.Face == MascotFace.Alert ? r * 1.18 : p.Face == MascotFace.Worried ? r * 0.9 : r;
                var open = 1 - Math.Clamp(p.Blink, 0, 1);
                foreach (var c in new[] { left, right })
                {
                    var at = new Point(c.X + ox, c.Y + oy);
                    if (open < 0.25) dc.DrawLine(linePen, new Point(at.X - size, at.Y), new Point(at.X + size, at.Y));
                    else d.DrawEye(dc, at, size, size * d.EyeTall * open, pal);
                }
                break;
        }

        DrawMouth(dc, d, s, pal, p, ox * 0.5, oy * 0.4);
    }

    static void DrawMouth(DrawingContext dc, MascotDesign d, double s, MascotPalette pal, MascotPose p, double ox, double oy)
    {
        var w = d.MouthWidth * s;
        var c = new Point(d.Mouth.X * s + ox, d.Mouth.Y * s + oy);
        var pen = MascotShapes.RoundPen(pal.Mouth, Math.Max(0.9, s * 0.045));
        switch (p.Face)
        {
            case MascotFace.Happy:
                dc.DrawGeometry(pal.Mouth, pen, MascotShapes.Quad(new Point(c.X - w * 1.1, c.Y - w * 0.2), new Point(c.X, c.Y + w * 1.9), new Point(c.X + w * 1.1, c.Y - w * 0.2), closed: true));
                break;
            case MascotFace.Sing:
                dc.DrawEllipse(pal.Mouth, null, new Point(c.X, c.Y + w * 0.15), w * (0.4 + 0.3 * p.Open), w * (0.45 + 0.55 * p.Open));
                break;
            case MascotFace.Alert:
                dc.DrawEllipse(pal.Mouth, null, new Point(c.X, c.Y + w * 0.15), w * 0.38, w * 0.45);
                break;
            case MascotFace.Sleep:
                dc.DrawEllipse(pal.Mouth, null, new Point(c.X, c.Y + w * 0.1), w * 0.26, w * 0.22);
                break;
            case MascotFace.Think:
                // Pursed to one side.
                dc.DrawLine(pen, new Point(c.X - w * 0.45, c.Y + w * 0.2), new Point(c.X + w * 0.6, c.Y - w * 0.05));
                break;
            case MascotFace.Worried:
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(c.X - w, c.Y + w * 0.35), false, false);
                    ctx.BezierTo(new Point(c.X - w * 0.5, c.Y - w * 0.15), new Point(c.X + w * 0.5, c.Y + w * 0.65), new Point(c.X + w, c.Y + w * 0.15), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
                break;
            }
            default:
                d.DrawRestingMouth(dc, c, w, pen);
                break;
        }
    }

    /// <summary>Extras float in the lane on the side of the pill's centre, upright whatever the body does.</summary>
    static void DrawExtras(DrawingContext dc, MascotDesign d, double s, double bodyX, double bodyTop, bool mirror, MascotPalette pal, MascotPose p)
    {
        var dir = mirror ? -1 : 1;
        var sideX = mirror ? bodyX + s * 0.1 : bodyX + s * 0.9;
        var top = bodyTop + s * d.HeadTop;
        var thin = Math.Max(0.9, s * 0.045);

        if (p.Face == MascotFace.Sleep)
        {
            // Three z's drifting up and out, the oldest fading at the top.
            var pen = MascotShapes.RoundPen(pal.Muted, thin);
            for (var i = 0; i < 3; i++)
            {
                var t = (i + p.Zzz) / 3;
                var size = s * (0.1 + 0.08 * t);
                var x = sideX + dir * (s * 0.02 + t * s * 0.3);
                if (mirror) x -= size;
                var y = bodyTop + s * 0.45 - t * s * 0.52;
                var alpha = t < 0.15 ? t / 0.15 : t > 0.8 ? (1 - t) / 0.2 : 1;
                dc.PushOpacity(Math.Clamp(alpha, 0, 1));
                dc.DrawGeometry(null, pen, MascotShapes.Polyline(false,
                    new Point(x, y), new Point(x + size, y), new Point(x, y + size), new Point(x + size, y + size)));
                dc.Pop();
            }
        }
        else if (p.Face == MascotFace.Think)
        {
            // A trail of thought dots, growing away from the head; the lit ones step along.
            for (var i = 0; i < 3; i++)
            {
                var k = (i + 1) / 3.0;
                var center = new Point(sideX + dir * (s * 0.03 + k * s * 0.27), top + s * 0.42 - k * s * 0.36);
                dc.DrawEllipse(i < p.Dots ? pal.Accent : pal.Faint, null, center, s * (0.035 + 0.018 * i), s * (0.035 + 0.018 * i));
            }
        }
        else if (p.Face == MascotFace.Alert)
        {
            // "!" in the warning colour: the agent is waiting for an answer.
            var x = sideX + dir * s * 0.2;
            var y = top + s * 0.02;
            dc.DrawRoundedRectangle(pal.Warning, null, new Rect(x - s * 0.045, y, s * 0.09, s * 0.26), s * 0.045, s * 0.045);
            dc.DrawEllipse(pal.Warning, null, new Point(x, y + s * 0.36), s * 0.05, s * 0.05);
        }

        if (p.Note > 0)
        {
            // A note rising from a strong beat, swaying a little and fading out.
            var t = p.Note;
            var x = sideX + dir * (s * 0.14 + Math.Sin(t * Math.PI * 2) * s * 0.04);
            var y = bodyTop + s * 0.6 - t * s * 0.5;
            var stemX = x + s * 0.06;
            dc.PushOpacity(Math.Clamp(1 - t * t, 0, 1));
            dc.DrawEllipse(pal.Accent2, null, new Point(x, y), s * 0.07, s * 0.055);
            var pen = MascotShapes.RoundPen(pal.Accent2, thin);
            dc.DrawGeometry(null, pen, MascotShapes.Polyline(false,
                new Point(stemX, y), new Point(stemX, y - s * 0.27), new Point(stemX + s * 0.09, y - s * 0.19)));
            dc.Pop();
        }
    }
}
