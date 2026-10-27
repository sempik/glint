using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Glint.Core;

namespace Glint.Services;

/// <summary>A named accent pair plus background tint shown as a swatch in Настройки.</summary>
public sealed record ThemePreset(string Id, string Name, string Accent, string Accent2, string Tint);

/// <summary>A background tint the surface color is built from.</summary>
public sealed record SurfaceTint(string Id, string Name, Color Color);

/// <summary>
/// Builds the color resources from <see cref="AppSettings"/> and swaps them into
/// <see cref="Application.Resources"/> by key. Top-level keys win over the merged Theme.xaml,
/// so every <c>DynamicResource</c> / <c>SetResourceReference</c> user repaints immediately.
/// Code that draws on its own (tray icon, graphs) listens to <see cref="ThemeChanged"/>.
/// </summary>
public static class ThemeService
{
    public const string DefaultPreset = "aurora";
    public const string CustomPreset = "custom";
    public const string DefaultAccent = "#A78BFA";
    public const string DefaultAccent2 = "#2DD4BF";
    public const string DefaultTint = "graphite";
    public const double DefaultSurfaceOpacity = 0.92;
    // No blur behind the surface, so below ~80% text gets unreadable over bright windows.
    public const double MinSurfaceOpacity = 0.8, MaxSurfaceOpacity = 1.0;
    /// <summary>Accent is used as text/icon foreground, so it needs at least this contrast against the tint.</summary>
    public const double MinAccentContrast = 3.0;

    public static IReadOnlyList<ThemePreset> Presets { get; } = new[]
    {
        new ThemePreset("aurora", "Аврора", "#A78BFA", "#2DD4BF", "graphite"),
        new ThemePreset("sunset", "Закат", "#FB923C", "#F472B6", "warm"),
        new ThemePreset("ocean", "Океан", "#3B82F6", "#22D3EE", "navy"),
        new ThemePreset("lime", "Лайм", "#A3E635", "#22C55E", "black"),
        new ThemePreset("rose", "Роза", "#F472B6", "#C084FC", "plum"),
        new ThemePreset("ember", "Уголь", "#EF4444", "#F59E0B", "black"),
        new ThemePreset("mono", "Моно", "#E5E7EB", "#9CA3AF", "black"),
    };

    public static IReadOnlyList<SurfaceTint> Tints { get; } = new[]
    {
        new SurfaceTint("graphite", "Графит", Color.FromRgb(0x12, 0x12, 0x18)),
        new SurfaceTint("black", "Чёрный", Color.FromRgb(0x0A, 0x0A, 0x0C)),
        new SurfaceTint("warm", "Тёплый", Color.FromRgb(0x1A, 0x15, 0x12)),
        new SurfaceTint("navy", "Синий", Color.FromRgb(0x0E, 0x14, 0x24)),
        new SurfaceTint("plum", "Слива", Color.FromRgb(0x16, 0x0F, 0x1E)),
    };

    /// <summary>Accent of the current theme, for code that draws without WPF resources (tray icon).</summary>
    public static Color Accent { get; private set; } = Color.FromRgb(0xA7, 0x8B, 0xFA);
    public static Color Accent2 { get; private set; } = Color.FromRgb(0x2D, 0xD4, 0xBF);
    public static Color Tint { get; private set; } = Color.FromRgb(0x12, 0x12, 0x18);

    /// <summary>Bumped on every real change; lets renderers drop cached pens/brushes.</summary>
    public static int Version { get; private set; }

    /// <summary>Raised on the UI thread after the resources were replaced.</summary>
    public static event EventHandler? ThemeChanged;

    static string? s_signature;

    /// <summary>Fixes invalid values in place (bad HEX, unknown tint, out-of-range opacity).</summary>
    public static void Normalize(AppSettings s)
    {
        s.AccentHex = TryParseHex(s.AccentHex, out var a) ? ToHex(a) : DefaultAccent;
        s.Accent2Hex = TryParseHex(s.Accent2Hex, out var b) ? ToHex(b) : DefaultAccent2;
        if (Tints.All(t => t.Id != s.SurfaceTint)) s.SurfaceTint = DefaultTint;
        if (!double.IsFinite(s.SurfaceOpacity)) s.SurfaceOpacity = DefaultSurfaceOpacity;
        s.SurfaceOpacity = Math.Clamp(s.SurfaceOpacity, MinSurfaceOpacity, MaxSurfaceOpacity);
        // The stored id always follows the actual colors (hand edits, replaced bad HEX).
        s.ThemePreset = MatchPreset(s);
    }

    /// <summary>Copies a preset's colors into the settings (does not save).</summary>
    public static void ApplyPreset(AppSettings s, ThemePreset p)
    {
        s.ThemePreset = p.Id;
        s.AccentHex = p.Accent;
        s.Accent2Hex = p.Accent2;
        s.SurfaceTint = p.Tint;
    }

    /// <summary>Id of the preset whose accents and tint equal the settings, else "custom".</summary>
    public static string MatchPreset(AppSettings s) =>
        Presets.FirstOrDefault(p =>
            string.Equals(p.Accent, s.AccentHex, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Accent2, s.Accent2Hex, StringComparison.OrdinalIgnoreCase)
            && p.Tint == s.SurfaceTint)?.Id ?? CustomPreset;

    /// <summary>Restores the default look (Аврора, graphite, 92%).</summary>
    public static void Reset(AppSettings s)
    {
        ApplyPreset(s, Presets[0]);
        s.SurfaceOpacity = DefaultSurfaceOpacity;
    }

    public static void Apply(AppSettings s)
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => Apply(s));
            return;
        }

        var accent = TryParseHex(s.AccentHex, out var a) ? a : ParseOrThrow(DefaultAccent);
        var accent2 = TryParseHex(s.Accent2Hex, out var b) ? b : ParseOrThrow(DefaultAccent2);
        var tint = (Tints.FirstOrDefault(t => t.Id == s.SurfaceTint) ?? Tints[0]).Color;
        var opacity = double.IsFinite(s.SurfaceOpacity)
            ? Math.Clamp(s.SurfaceOpacity, MinSurfaceOpacity, MaxSurfaceOpacity)
            : DefaultSurfaceOpacity;

        var signature = $"{accent}|{accent2}|{tint}|{opacity.ToString("0.###", CultureInfo.InvariantCulture)}";
        if (signature == s_signature) return;
        s_signature = signature;

        Accent = accent;
        Accent2 = accent2;
        Tint = tint;

        var surface = WithAlpha(tint, (byte)Math.Round(opacity * 255));
        // A touch of the accent keeps cards from looking grey on colored themes.
        var card = WithAlpha(Mix(Colors.White, accent, 0.10), 0x1A);
        var cardHover = WithAlpha(Mix(Colors.White, accent, 0.12), 0x29);
        // Menus/tooltips sit on top of anything, so they are nearly opaque and a bit lighter than the pill.
        var menu = WithAlpha(Mix(Mix(tint, Colors.White, 0.04), accent, 0.03), 0xF0);
        var scrollThumb = WithAlpha(Mix(Colors.White, accent, 0.25), 0x40);

        var r = app.Resources;
        r["AccentColor"] = accent;
        r["Accent2Color"] = accent2;
        r["SurfaceColor"] = surface;
        r["CardColor"] = card;
        r["CardHoverColor"] = cardHover;

        r["AccentBrush"] = Solid(accent);
        r["Accent2Brush"] = Solid(accent2);
        r["SurfaceBrush"] = Solid(surface);
        r["CardBrush"] = Solid(card);
        r["CardHoverBrush"] = Solid(cardHover);
        r["MenuBackgroundBrush"] = Solid(menu);
        r["ScrollThumbBrush"] = Solid(scrollThumb);
        r["WarningBrush"] = Solid(Color.FromRgb(0xFB, 0xBF, 0x24));

        r["AccentFillBrush"] = Freeze(new LinearGradientBrush(accent, accent2, new Point(0, 0), new Point(1, 0)));
        r["AccentVerticalBrush"] = Freeze(new LinearGradientBrush(accent2, accent, new Point(0, 1), new Point(0, 0)));
        r["AccentStrokeBrush"] = Freeze(new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(WithAlpha(accent, 0x99), 0),
                new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(WithAlpha(accent2, 0x99), 1),
            },
            new Point(0, 0), new Point(1, 1)));
        r["AccentAreaBrush"] = Freeze(new LinearGradientBrush(WithAlpha(accent, 0x55), WithAlpha(accent, 0x00), 90));

        Version++;
        try
        {
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ───────────── Color helpers ─────────────

    /// <summary>Accepts #RGB, #RRGGBB, RGB, RRGGBB (whitespace allowed around).</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (text is null) return false;
        var t = text.Trim();
        if (t.StartsWith('#')) t = t[1..];
        // HexNumber would also accept inner whitespace ("# ab"), so check the digits first.
        if (!t.All(Uri.IsHexDigit)) return false;
        if (t.Length == 3) t = string.Concat(t[0], t[0], t[1], t[1], t[2], t[2]);
        if (t.Length != 6) return false;
        if (!uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>WCAG relative luminance (alpha ignored).</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG contrast ratio between two colors, 1..21.</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>True when the accent stays readable as text on the given tint.</summary>
    public static bool IsReadableAccent(Color accent, string? tintId)
    {
        var tint = (Tints.FirstOrDefault(t => t.Id == tintId) ?? Tints[0]).Color;
        return Contrast(accent, tint) >= MinAccentContrast;
    }

    static Color ParseOrThrow(string hex) =>
        TryParseHex(hex, out var c) ? c : throw new ArgumentException("Bad built-in color", nameof(hex));

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Linear mix: t=0 → a, t=1 → b. Alpha of a.</summary>
    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        a.A,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    static SolidColorBrush Solid(Color c) => Freeze(new SolidColorBrush(c));

    static T Freeze<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    public static LinearGradientBrush Gradient(string hex1, string hex2)
    {
        var a = TryParseHex(hex1, out var c1) ? c1 : Colors.Gray;
        var b = TryParseHex(hex2, out var c2) ? c2 : Colors.Gray;
        return Freeze(new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1)));
    }
}
