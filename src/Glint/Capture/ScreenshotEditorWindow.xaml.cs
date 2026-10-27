using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Glint.Core;
using Path = System.Windows.Shapes.Path;
using Glint.Services;

namespace Glint.Capture;

/// <summary>
/// Markup for a fresh screenshot: pen, highlighter, arrow, rectangle, ellipse, text, pixelation and crop,
/// in a few colours and three sizes, with undo/redo. Marks are ordinary WPF shapes on a canvas the size of the
/// picture (one unit = one pixel), so the result is rendered once at full resolution when copied or saved.
/// Crop only changes which part is shown and exported, so it can be undone like anything else.
/// </summary>
public partial class ScreenshotEditorWindow : Window
{
    enum Tool { Pen, Highlighter, Arrow, Rectangle, Ellipse, Text, Pixelate, Crop }

    /// <summary>Everything undo needs: which marks exist (marks themselves never change once placed) and the crop.</summary>
    sealed record EditorState(UIElement[] Items, Int32Rect Crop);

    static readonly (Tool Tool, string Glyph, string Name, Key Key)[] Tools =
    {
        (Tool.Pen, Glyphs.Pen, "Перо", Key.P),
        (Tool.Highlighter, Glyphs.Highlighter, "Маркер", Key.H),
        (Tool.Arrow, Glyphs.Arrow, "Стрелка", Key.A),
        (Tool.Rectangle, Glyphs.Rectangle, "Прямоугольник", Key.R),
        (Tool.Ellipse, Glyphs.Ellipse, "Эллипс", Key.E),
        (Tool.Text, Glyphs.Font, "Текст", Key.T),
        (Tool.Pixelate, Glyphs.Redact, "Скрыть область (пикселизация)", Key.B),
        (Tool.Crop, Glyphs.Crop, "Обрезать", Key.C),
    };

    static readonly (string Name, Color Color)[] Palette =
    {
        ("Красный", Color.FromRgb(0xEF, 0x44, 0x44)),
        ("Оранжевый", Color.FromRgb(0xF9, 0x73, 0x16)),
        ("Жёлтый", Color.FromRgb(0xFA, 0xCC, 0x15)),
        ("Зелёный", Color.FromRgb(0x22, 0xC5, 0x5E)),
        ("Синий", Color.FromRgb(0x3B, 0x82, 0xF6)),
        ("Белый", Colors.White),
        ("Чёрный", Color.FromRgb(0x11, 0x11, 0x14)),
    };

    // Line widths and text sizes per size step, in pixels at 100% scaling.
    static readonly double[] Strokes = { 3, 5, 9 };
    static readonly double[] TextSizes = { 18, 28, 44 };

    readonly BitmapSource _image;
    readonly double _unit;
    readonly List<UIElement> _items = new();
    readonly Stack<EditorState> _undo = new();
    readonly Stack<EditorState> _redo = new();
    readonly List<(Tool Tool, RadioButton Button)> _toolButtons = new();
    readonly List<Border> _colorRings = new();
    readonly List<Border> _sizeRings = new();
    Int32Rect _crop;
    Tool _tool = Tool.Arrow;
    Color _color = Palette[0].Color;
    int _size = 1;
    Point? _start;
    FrameworkElement? _preview;
    TextBox? _editing;
    bool _closeArmed;
    bool _handedOff;
    bool _exporting;
    bool _closed;

    ScreenshotEditorWindow(BitmapSource image, double unit)
    {
        InitializeComponent();
        _image = image.Format == PixelFormats.Bgr32 || image.Format == PixelFormats.Bgra32 ? image : Freeze(new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0));
        _unit = Math.Max(1, unit);
        _crop = new Int32Rect(0, 0, _image.PixelWidth, _image.PixelHeight);
        Background = new SolidColorBrush(ThemeService.Tint);

        BaseImage.Source = _image;
        BaseImage.Width = Surface.Width = _image.PixelWidth;
        BaseImage.Height = Surface.Height = _image.PixelHeight;
        RenderOptions.SetBitmapScalingMode(Fit, BitmapScalingMode.HighQuality);

        BuildTools();
        BuildColors();
        BuildSizes();
        ApplyCrop();
        SizeToScreen();

        CropHost.MouseLeftButtonDown += OnDown;
        CropHost.MouseMove += OnMove;
        CropHost.MouseLeftButtonUp += OnUp;
        CropHost.LostMouseCapture += (_, _) => { if (_start is not null) CancelDrag(); };
        UndoButton.Click += (_, _) => Undo();
        RedoButton.Click += (_, _) => Redo();
        CopyButton.Click += (_, _) => CopyAndClose();
        SaveButton.Click += (_, _) => SaveAndClose();
        SaveAsButton.Click += (_, _) => SaveAs();
        PreviewKeyDown += OnKey;
        DpiChanged += (_, _) => ApplyDpi();
    }

    /// <summary>Opens the editor for a captured picture; <paramref name="unit"/> is the source monitor's scale (1.5 at 150%).</summary>
    public static void Open(BitmapSource image, double unit)
    {
        var window = new ScreenshotEditorWindow(image, unit);
        window.Show();
        window.Activate();
    }

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // A dark title bar to match the dark palette.
        var dark = ThemeService.IsLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        ApplyDpi();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The title-bar X and Alt+F4 get the same second chance as Esc before unsaved marks are thrown away.
        if (_items.Count > 0 && !_handedOff && !_closeArmed)
        {
            e.Cancel = true;
            _closeArmed = true;
            UpdateStatus("Пометки не сохранены — закройте ещё раз, чтобы выйти без сохранения");
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    /// <summary>Never shown bigger than 1:1 in physical pixels, so the picture stays sharp.</summary>
    void ApplyDpi() => CropHost.LayoutTransform = new ScaleTransform(1 / VisualTreeHelper.GetDpi(this).DpiScaleX, 1 / VisualTreeHelper.GetDpi(this).DpiScaleY);

    /// <summary>Big enough for the picture at 1:1 where possible, never more than 90% of the monitor it opens on.</summary>
    void SizeToScreen()
    {
        try
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(cursor);
            var scale = MonitorScale(cursor.X, cursor.Y);
            var work = screen.WorkingArea;
            var maxW = work.Width / scale * 0.9;
            var maxH = work.Height / scale * 0.9;
            Width = Math.Clamp(_image.PixelWidth / scale + 60, MinWidth, Math.Max(MinWidth, maxW));
            Height = Math.Clamp(_image.PixelHeight / scale + 150, MinHeight, Math.Max(MinHeight, maxH));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Width = 960;
            Height = 640;
        }
    }

    // ───────────── Toolbar ─────────────

    void BuildTools()
    {
        foreach (var (tool, glyph, name, key) in Tools)
        {
            var icon = new TextBlock { Style = (Style)FindResource("Icon"), Text = glyph, FontSize = 14 };
            var button = new RadioButton
            {
                Style = (Style)FindResource("TabButton"),
                GroupName = "EditorTools",
                ToolTip = $"{name} ({key})",
                Content = icon,
                Focusable = false,
                IsChecked = tool == _tool,
            };
            // Implicit TextBlock styles would override inheritance, so follow the button's foreground explicitly.
            icon.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = button });
            var t = tool;
            button.Checked += (_, _) => SetTool(t);
            _toolButtons.Add((tool, button));
            ToolPanel.Children.Add(button);
        }
    }

    void BuildColors()
    {
        for (var i = 0; i < Palette.Length; i++)
        {
            var (name, color) = Palette[i];
            var dot = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80)), StrokeThickness = 1 };
            var ring = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Child = dot };
            var button = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(2), ToolTip = name, Content = ring, Focusable = false };
            var index = i;
            button.Click += (_, _) => SetColor(index);
            _colorRings.Add(ring);
            ColorPanel.Children.Add(button);
        }
        SetColor(0);
    }

    void BuildSizes()
    {
        string[] names = { "Тонко", "Средне", "Толсто" };
        double[] dots = { 4, 7, 11 };
        for (var i = 0; i < Strokes.Length; i++)
        {
            var dot = new Ellipse { Width = dots[i], Height = dots[i] };
            dot.SetResourceReference(Shape.FillProperty, "TextBrush");
            var ring = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Child = dot };
            var button = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(2), ToolTip = $"{names[i]} ({i + 1})", Content = ring, Focusable = false };
            var index = i;
            button.Click += (_, _) => SetSize(index);
            _sizeRings.Add(ring);
            SizePanel.Children.Add(button);
        }
        SetSize(1);
    }

    void SetTool(Tool tool)
    {
        CommitText();
        _tool = tool;
        foreach (var (t, button) in _toolButtons)
            if (t == tool && button.IsChecked != true) button.IsChecked = true;
        CropHost.Cursor = tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        UpdateStatus();
    }

    void SetColor(int index)
    {
        _color = Palette[index].Color;
        for (var i = 0; i < _colorRings.Count; i++)
        {
            if (i == index) _colorRings[i].SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            else _colorRings[i].BorderBrush = Brushes.Transparent;
        }
        // A text being typed takes the new colour too.
        if (_editing is not null) _editing.Foreground = _editing.CaretBrush = new SolidColorBrush(_color);
    }

    void SetSize(int index)
    {
        _size = index;
        for (var i = 0; i < _sizeRings.Count; i++)
        {
            if (i == index) _sizeRings[i].SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            else _sizeRings[i].BorderBrush = Brushes.Transparent;
        }
        if (_editing is not null) _editing.FontSize = TextSizes[_size] * _unit;
    }

    double Stroke => Strokes[_size] * _unit;

    Brush ColorBrush(byte alpha = 0xFF) => Freeze(new SolidColorBrush(Color.FromArgb(alpha, _color.R, _color.G, _color.B)));

    void UpdateStatus(string? message = null)
    {
        StatusText.Text = message ?? $"{_crop.Width} × {_crop.Height}" + (_tool switch
        {
            Tool.Text => " · щёлкните, где начать текст; Enter — готово, Shift+Enter — новая строка",
            Tool.Crop => " · выделите, что оставить",
            Tool.Pixelate => " · выделите, что скрыть",
            Tool.Rectangle or Tool.Ellipse => " · Shift — ровный квадрат или круг",
            Tool.Arrow => " · Shift — под углом 45°",
            _ => string.Empty,
        });
    }

    void UpdateButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    // ───────────── Drawing ─────────────

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _closeArmed = false;
        var p = Clamp(e.GetPosition(Surface));
        if (_editing is not null)
        {
            CommitText();
            // The click that ends a text doesn't also start the next one.
            if (_tool == Tool.Text) return;
        }
        if (_tool == Tool.Text)
        {
            BeginText(p);
            return;
        }

        _start = p;
        CropHost.CaptureMouse();
        _preview = _tool switch
        {
            Tool.Pen => new Polyline
            {
                Stroke = ColorBrush(),
                StrokeThickness = Stroke,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Points = { p },
            },
            // A wide translucent line drawn as one shape, so crossing itself doesn't darken.
            Tool.Highlighter => new Polyline
            {
                Stroke = ColorBrush(0x66),
                StrokeThickness = Stroke * 3.2 + 6 * _unit,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Points = { p },
            },
            Tool.Arrow => new Path { Stroke = ColorBrush(), Fill = ColorBrush(), StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round },
            Tool.Rectangle => new Rectangle { Stroke = ColorBrush(), StrokeThickness = Stroke, RadiusX = 2 * _unit, RadiusY = 2 * _unit },
            Tool.Ellipse => new Ellipse { Stroke = ColorBrush(), StrokeThickness = Stroke },
            // Crop and pixelate show a dashed marquee while dragging.
            _ => new Rectangle
            {
                Stroke = Brushes.White,
                StrokeThickness = 1.5 * _unit,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 3, Color = Colors.Black, Opacity = 0.8 },
            },
        };
        _preview.IsHitTestVisible = false;
        Surface.Children.Add(_preview);
        UpdatePreview(p, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is null || _preview is null || e.LeftButton != MouseButtonState.Pressed) return;
        UpdatePreview(Clamp(e.GetPosition(Surface)), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not Point start || _preview is null) return;
        var end = Clamp(e.GetPosition(Surface));
        var preview = _preview;
        _start = null;
        _preview = null;
        CropHost.ReleaseMouseCapture();

        switch (_tool)
        {
            case Tool.Pen:
            case Tool.Highlighter:
                var line = (Polyline)preview;
                // A click without moving leaves a dot.
                if (line.Points.Count == 1) line.Points.Add(new Point(start.X + 0.5, start.Y + 0.5));
                Commit(preview);
                break;
            case Tool.Arrow:
                if ((end - start).Length >= 4 * _unit) Commit(preview);
                else Surface.Children.Remove(preview);
                break;
            case Tool.Rectangle:
            case Tool.Ellipse:
                if (preview.Width >= 3 && preview.Height >= 3) Commit(preview);
                else Surface.Children.Remove(preview);
                break;
            case Tool.Pixelate:
                Surface.Children.Remove(preview);
                var hide = PixelRect(start, end, false);
                if (hide.Width >= 3 && hide.Height >= 3)
                {
                    var image = Pixelate(hide);
                    Surface.Children.Add(image);
                    Commit(image);
                }
                break;
            case Tool.Crop:
                Surface.Children.Remove(preview);
                var crop = Intersect(PixelRect(start, end, false), _crop);
                if (crop.Width >= 8 && crop.Height >= 8)
                {
                    PushUndo();
                    _crop = crop;
                    ApplyCrop();
                }
                break;
        }
    }

    void CancelDrag()
    {
        if (_preview is not null) Surface.Children.Remove(_preview);
        _preview = null;
        _start = null;
    }

    void UpdatePreview(Point p, bool shift)
    {
        if (_start is not Point s || _preview is null) return;
        switch (_preview)
        {
            case Polyline line:
                var last = line.Points[^1];
                // Skip sub-pixel jitter; it only adds points.
                if ((p - last).Length >= 1) line.Points.Add(p);
                break;
            case Path arrow:
                if (shift) p = SnapAngle(s, p);
                arrow.Data = ArrowGeometry(s, p, Stroke);
                break;
            default:
                var r = PixelRect(s, p, shift && _tool is Tool.Rectangle or Tool.Ellipse);
                Canvas.SetLeft(_preview, r.X);
                Canvas.SetTop(_preview, r.Y);
                _preview.Width = r.Width;
                _preview.Height = r.Height;
                break;
        }
    }

    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _image.PixelWidth), Math.Clamp(p.Y, 0, _image.PixelHeight));

    /// <summary>The rectangle between two points in whole pixels; square keeps the shorter side for both.</summary>
    static Int32Rect PixelRect(Point a, Point b, bool square)
    {
        if (square)
        {
            var side = Math.Min(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            b = new Point(a.X + Math.Sign(b.X - a.X) * side, a.Y + Math.Sign(b.Y - a.Y) * side);
        }
        var x1 = (int)Math.Floor(Math.Min(a.X, b.X));
        var y1 = (int)Math.Floor(Math.Min(a.Y, b.Y));
        var x2 = (int)Math.Ceiling(Math.Max(a.X, b.X));
        var y2 = (int)Math.Ceiling(Math.Max(a.Y, b.Y));
        return new Int32Rect(x1, y1, x2 - x1, y2 - y1);
    }

    static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return new Int32Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    static Point SnapAngle(Point a, Point b)
    {
        var v = b - a;
        var angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return a + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    /// <summary>Shaft plus a filled head; the head grows with the line width but never swallows a short arrow.</summary>
    static Geometry ArrowGeometry(Point from, Point to, double thickness)
    {
        var v = to - from;
        var length = v.Length;
        var group = new GeometryGroup();
        if (length < 0.5) return group;
        v /= length;
        var head = Math.Min(length * 0.6, thickness * 3.2 + 10);
        var normal = new Vector(-v.Y, v.X) * head * 0.55;
        var back = to - v * head;

        var shaft = new PathFigure { StartPoint = from, IsFilled = false, IsClosed = false };
        shaft.Segments.Add(new LineSegment(to - v * head * 0.7, true));
        var tip = new PathFigure { StartPoint = to, IsFilled = true, IsClosed = true };
        tip.Segments.Add(new LineSegment(back + normal, true));
        tip.Segments.Add(new LineSegment(back - normal, true));
        group.Children.Add(new PathGeometry(new[] { shaft, tip }));
        group.Freeze();
        return group;
    }

    /// <summary>Big blocks of averaged colour over the original picture; blocks scale with the area so text can't be read back.</summary>
    Image Pixelate(Int32Rect r)
    {
        r = Intersect(r, new Int32Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
        var stride = r.Width * 4;
        var px = new byte[stride * r.Height];
        _image.CopyPixels(r, px, stride, 0);
        var block = Math.Max(4, (int)Math.Max(Math.Round(9 * _unit), Math.Min(r.Width, r.Height) / 6.0));
        for (var by = 0; by < r.Height; by += block)
        {
            for (var bx = 0; bx < r.Width; bx += block)
            {
                var bw = Math.Min(block, r.Width - bx);
                var bh = Math.Min(block, r.Height - by);
                long b = 0, g = 0, red = 0, a = 0;
                for (var y = by; y < by + bh; y++)
                {
                    for (var x = bx; x < bx + bw; x++)
                    {
                        var i = y * stride + x * 4;
                        b += px[i];
                        g += px[i + 1];
                        red += px[i + 2];
                        a += px[i + 3];
                    }
                }
                var n = bw * bh;
                for (var y = by; y < by + bh; y++)
                {
                    for (var x = bx; x < bx + bw; x++)
                    {
                        var i = y * stride + x * 4;
                        px[i] = (byte)(b / n);
                        px[i + 1] = (byte)(g / n);
                        px[i + 2] = (byte)(red / n);
                        px[i + 3] = (byte)(a / n);
                    }
                }
            }
        }
        var bitmap = Freeze(BitmapSource.Create(r.Width, r.Height, 96, 96, _image.Format, null, px, stride));
        var image = new Image { Source = bitmap, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, r.X);
        Canvas.SetTop(image, r.Y);
        return image;
    }

    // ───────────── Text ─────────────

    void BeginText(Point p)
    {
        var size = TextSizes[_size] * _unit;
        var box = new TextBox
        {
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = ColorBrush(),
            CaretBrush = ColorBrush(),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            Padding = new Thickness(2 * _unit),
            MinWidth = size,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
        };
        Canvas.SetLeft(box, p.X - 4 * _unit);
        Canvas.SetTop(box, p.Y - size * 0.75);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                CommitText();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelText();
            }
        };
        Surface.Children.Add(box);
        _editing = box;
        // The box can take focus only once it is laid out; clicking elsewhere afterwards finishes the text.
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_editing, box)) return;
            Keyboard.Focus(box);
            box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(CommitText);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Turns the text being typed into a plain mark (with a soft outline so it reads on any background).</summary>
    void CommitText()
    {
        if (_editing is not { } box) return;
        _editing = null;
        Surface.Children.Remove(box);
        var text = box.Text.TrimEnd();
        if (text.Length == 0) return;
        var color = ((SolidColorBrush)box.Foreground).Color;
        var light = color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 140;
        var block = new TextBlock
        {
            Text = text,
            FontSize = box.FontSize,
            FontWeight = box.FontWeight,
            FontFamily = box.FontFamily,
            Foreground = box.Foreground,
            TextTrimming = TextTrimming.None,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 4 * _unit, Color = light ? Colors.Black : Colors.White, Opacity = 0.7 },
        };
        // Where the text sat inside the box (its padding plus the editor's own small inset).
        Canvas.SetLeft(block, Canvas.GetLeft(box) + box.Padding.Left + 2);
        Canvas.SetTop(block, Canvas.GetTop(box) + box.Padding.Top);
        Surface.Children.Add(block);
        Commit(block);
        Focus();
    }

    void CancelText()
    {
        if (_editing is not { } box) return;
        _editing = null;
        Surface.Children.Remove(box);
        Focus();
    }

    // ───────────── Undo / redo / crop ─────────────

    EditorState Current() => new(_items.ToArray(), _crop);

    void PushUndo()
    {
        _undo.Push(Current());
        _redo.Clear();
        UpdateButtons();
    }

    /// <summary>A finished mark (already on the canvas) becomes part of the picture.</summary>
    void Commit(UIElement element)
    {
        PushUndo();
        _items.Add(element);
        _closeArmed = false;
    }

    void Undo()
    {
        CancelText();
        if (_undo.Count == 0) return;
        _redo.Push(Current());
        Restore(_undo.Pop());
    }

    void Redo()
    {
        CancelText();
        if (_redo.Count == 0) return;
        _undo.Push(Current());
        Restore(_redo.Pop());
    }

    void Restore(EditorState state)
    {
        _items.Clear();
        _items.AddRange(state.Items);
        Surface.Children.Clear();
        Surface.Children.Add(BaseImage);
        foreach (var item in _items) Surface.Children.Add(item);
        _crop = state.Crop;
        ApplyCrop();
        UpdateButtons();
    }

    void ApplyCrop()
    {
        CropHost.Width = _crop.Width;
        CropHost.Height = _crop.Height;
        Surface.RenderTransform = new TranslateTransform(-_crop.X, -_crop.Y);
        UpdateStatus();
    }

    // ───────────── Result ─────────────

    /// <summary>Picture plus marks at full resolution, cropped.</summary>
    BitmapSource Render()
    {
        CommitText();
        var shown = Surface.RenderTransform;
        Surface.RenderTransform = Transform.Identity;
        try
        {
            // The render picks up the visual's transform from layout; without a pass it would still shift by the crop.
            Surface.UpdateLayout();
            var full = new RenderTargetBitmap(_image.PixelWidth, _image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            full.Render(Surface);
            return Freeze(new CroppedBitmap(full, _crop));
        }
        finally
        {
            Surface.RenderTransform = shown;
        }
    }

    // The PNG is encoded off the UI thread; meanwhile a second Copy or Save is ignored.
    async void CopyAndClose()
    {
        if (_exporting) return;
        _exporting = true;
        try
        {
            var bitmap = Render();
            var png = await ScreenTools.EncodePngAsync(bitmap);
            if (ScreenTools.CopyImage(bitmap, png)) AppServices.Notifier.Flash("Скриншот скопирован", $"{bitmap.PixelWidth} × {bitmap.PixelHeight}", Glyphs.Camera);
            else AppServices.Notifier.Flash("Буфер обмена занят", "Попробуйте ещё раз", Glyphs.Info);
            _handedOff = true;
            if (!_closed) Close();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (!_closed) UpdateStatus("Не удалось скопировать");
        }
        finally
        {
            _exporting = false;
        }
    }

    async void SaveAndClose()
    {
        if (_exporting) return;
        _exporting = true;
        try
        {
            var bitmap = Render();
            var png = await ScreenTools.EncodePngAsync(bitmap);
            var path = await ScreenTools.SavePngAsync(png);
            ScreenTools.CopyImage(bitmap, png);
            ScreenTools.FlashSaved(path);
            _handedOff = true;
            if (!_closed) Close();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (!_closed) UpdateStatus("Не удалось сохранить: " + ex.Message);
        }
        finally
        {
            _exporting = false;
        }
    }

    void SaveAs()
    {
        try
        {
            var bitmap = Render();
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить скриншот",
                Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg",
                FileName = System.IO.Path.GetFileName(ScreenTools.NewScreenshotPath()),
                InitialDirectory = ScreenTools.PicturesFolder,
                AddExtension = true,
            };
            Directory.CreateDirectory(ScreenTools.PicturesFolder);
            if (dialog.ShowDialog(this) != true) return;
            BitmapEncoder encoder = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || dialog.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(dialog.FileName)) encoder.Save(stream);
            _handedOff = true;
            UpdateStatus("Сохранено: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            UpdateStatus("Не удалось сохранить: " + ex.Message);
        }
    }

    // ───────────── Keyboard ─────────────

    void OnKey(object sender, KeyEventArgs e)
    {
        // While typing a text, keys belong to it (its own handler takes Enter and Esc).
        if (_editing is not null) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z when shift:
                case Key.Y:
                    Redo();
                    break;
                case Key.Z:
                    Undo();
                    break;
                case Key.C:
                    CopyAndClose();
                    break;
                case Key.S when shift:
                    SaveAs();
                    break;
                case Key.S:
                    SaveAndClose();
                    break;
                default:
                    return;
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_start is not null)
            {
                CropHost.ReleaseMouseCapture();
                CancelDrag();
                return;
            }
            // Marks would be lost; ask for a second Esc.
            if (_items.Count > 0 && !_handedOff && !_closeArmed)
            {
                _closeArmed = true;
                UpdateStatus("Пометки не сохранены — Esc ещё раз, чтобы закрыть без сохранения");
                return;
            }
            Close();
            return;
        }
        if (e.Key is >= Key.D1 and <= Key.D3)
        {
            SetSize(e.Key - Key.D1);
            e.Handled = true;
            return;
        }
        foreach (var (tool, _, _, key) in Tools)
        {
            if (e.Key != key) continue;
            SetTool(tool);
            e.Handled = true;
            return;
        }
    }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Scaling of the monitor at a physical point (1.5 at 150%).</summary>
    internal static double MonitorScale(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0) return dpiX / 96.0;
        }
        catch (Exception)
        {
            // Older systems without shcore: assume 100%.
        }
        return 1;
    }

    const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
