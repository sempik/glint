using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Glint.Core;
using Glint.Services;
using Glint.Services.Indicators;

namespace Glint.Pill;

/// <summary>
/// Live indicators: tiny tinted badges next to the clock (mic, camera, recording, VPN, downloads), matching
/// chips with details in the panel header, and the current lyrics line. Everything here is redrawn only when
/// a monitor reports a change; nothing animates.
/// </summary>
public partial class PillWindow
{
    const double BadgeSize = 14, BadgeStep = 18, LyricsExtraWidth = 172;
    // Progress rings move in 1/40 steps, so a running download repaints the pill only every few percent.
    const double ProgressSteps = 40;

    string? _badgeSignature;
    bool _lyricShown;
    // Gives up the lyric slot only after playback has stayed stopped (or lyric-less) for a moment.
    readonly DispatcherTimer _lyricHide = new() { Interval = TimeSpan.FromSeconds(4) };
    bool _headerDirty = true;
    readonly Dictionary<string, (Button Button, TextBlock Icon, TextBlock Label)> _chips = new();
    string? _chipOrder;

    double IndicatorExtraWidth =>
        PillIndicators.Children.Count * BadgeStep + (_lyricShown ? LyricsExtraWidth : 0) + RecordingExtraWidth + WeatherExtraWidth + VoiceExtraWidth
        + AgentExtraWidth + MascotExtraWidth;

    void InitIndicators()
    {
        IndicatorHub.Shared.Changed += (_, _) => RefreshIndicators();
        _lyricHide.Tick += (_, _) => { _lyricHide.Stop(); ApplyLyricLine(hideNow: true); };
        LyricsService.Shared.Changed += (_, _) => UpdateLyricLine();
        AppServices.Settings.Changed += (_, _) => UpdateLyricLine();
        AppServices.Media.Changed += (_, _) => Dispatcher.BeginInvoke(UpdateLyricLine);
        // The header is only looked at while the panel is open; catch up when it opens.
        PanelView.IsVisibleChanged += (_, _) => { if (PanelView.IsVisible && _headerDirty) UpdateHeaderChips(); };
    }

    void RefreshIndicators()
    {
        // Chip-only indicators (screen recording) have their own element in the pill.
        var items = IndicatorHub.Shared.Items.Where(i => !i.ChipOnly).ToList();
        // Only what the badge draws: label and tooltip text change often (sizes, times) and aren't visible in the pill.
        var signature = string.Join("|", items.Select(i => $"{i.Id}:{i.Glyph}:{i.Brush}:{(i.Progress is double p ? Math.Round(p * ProgressSteps) : -1)}"));
        if (signature != _badgeSignature)
        {
            _badgeSignature = signature;
            PillIndicators.Children.Clear();
            foreach (var i in items) PillIndicators.Children.Add(BuildBadge(i));
            ResizePillForIndicators();
        }
        else
        {
            // Same look; keep the tooltips current for anyone hovering with a long expand delay.
            for (var n = 0; n < items.Count && n < PillIndicators.Children.Count; n++)
                ((FrameworkElement)PillIndicators.Children[n]).ToolTip = items[n].Tooltip;
        }

        if (PanelView.IsVisible) UpdateHeaderChips();
        else _headerDirty = true;
    }

    void ResizePillForIndicators()
    {
        // Crowding decides whether the weather gets its own slot or takes turns with the clock.
        UpdateWeatherSlot();
        // The panel and flashes have their own size; the pill catches up when it comes back.
        if (_state == State.Pill && Math.Abs(_shellTargetWidth - CurrentPillWidth) > 0.5)
            AnimateShell(CurrentPillWidth, PillHeight);
    }

    /// <summary>A soft tinted disc with a small glyph; a thin ring shows progress when it is known.</summary>
    FrameworkElement BuildBadge(Indicator i)
    {
        var grid = new Grid
        {
            Width = BadgeSize,
            Height = BadgeSize,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = i.Tooltip,
        };
        var disc = new Ellipse { Opacity = 0.24 };
        disc.SetResourceReference(Shape.FillProperty, i.Brush);
        grid.Children.Add(disc);

        var glyph = new TextBlock { Style = (Style)FindResource("Icon"), Text = i.Glyph, FontSize = 7.5 };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, i.Brush);
        grid.Children.Add(glyph);

        if (i.Progress is double p && p > 0)
        {
            var ring = new System.Windows.Shapes.Path { StrokeThickness = 1.4, Data = ProgressArc(Math.Round(p * ProgressSteps) / ProgressSteps, BadgeSize) };
            ring.SetResourceReference(Shape.StrokeProperty, i.Brush);
            grid.Children.Add(ring);
        }
        return grid;
    }

    /// <summary>Clockwise arc from twelve o'clock covering the given share of the circle.</summary>
    static Geometry ProgressArc(double share, double size)
    {
        var r = size / 2 - 0.7;
        var c = size / 2;
        if (share >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);
        var angle = share * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, share > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    // ───────────── Header chips ─────────────

    void UpdateHeaderChips()
    {
        _headerDirty = false;
        var items = IndicatorHub.Shared.Items;
        var order = string.Join("|", items.Select(i => i.Id));
        // Update in place when the set is the same, so a tooltip someone is reading doesn't close every second.
        if (order != _chipOrder)
        {
            _chipOrder = order;
            HeaderIndicators.Children.Clear();
            _chips.Clear();
            foreach (var i in items)
            {
                var chip = CreateChip(i.Id);
                _chips[i.Id] = chip;
                HeaderIndicators.Children.Add(chip.Button);
            }
        }
        foreach (var i in items)
        {
            if (!_chips.TryGetValue(i.Id, out var chip)) continue;
            chip.Icon.Text = i.Glyph;
            chip.Icon.SetResourceReference(TextBlock.ForegroundProperty, i.Brush);
            chip.Label.Text = i.Label ?? string.Empty;
            chip.Label.Visibility = string.IsNullOrEmpty(i.Label) ? Visibility.Collapsed : Visibility.Visible;
            chip.Button.ToolTip = i.Tooltip;
            chip.Button.Cursor = i.Click is null ? Cursors.Arrow : Cursors.Hand;
        }
        // More or wider chips may push into the tabs (see FitTabs).
        FitTabsSoon();
    }

    (Button Button, TextBlock Icon, TextBlock Label) CreateChip(string id)
    {
        var icon = new TextBlock { Style = (Style)FindResource("Icon"), FontSize = 12 };
        var label = new TextBlock { FontSize = 11.5, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 90 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var button = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(7, 5, 7, 5),
            Focusable = false,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
        };
        // Look the handler up at click time: monitors replace the indicator (and its action) as things change.
        button.Click += (_, _) =>
        {
            var action = IndicatorHub.Shared.Items.FirstOrDefault(i => i.Id == id)?.Click;
            if (action is null) return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        };
        return (button, icon, label);
    }

    // ───────────── Lyrics ─────────────

    void UpdateLyricLine() => ApplyLyricLine(hideNow: false);

    void ApplyLyricLine(bool hideNow)
    {
        var s = AppServices.Settings.Current.Indicators;
        var lyrics = LyricsService.Shared;
        // Dictation's live line takes the lyric's place; both together don't fit the pill.
        var enabled = s.Lyrics && s.LyricsInPill && !_voiceShown;
        // The slot stays for the whole song: blank lines (instrumental breaks) and the intro show a note instead of
        // collapsing, so the pill doesn't shrink and grow with every gap.
        var show = enabled && lyrics.HasLyrics && AppServices.Media.Current?.IsPlaying == true;
        if (show)
        {
            _lyricHide.Stop();
            PillLyric.Text = lyrics.CurrentLine ?? "♪";
            PillLyric.ToolTip = lyrics.CurrentLine;
        }
        else if (_lyricShown && enabled && !hideNow)
        {
            // A pause or the next track's lookup usually ends within seconds; keep the slot meanwhile.
            if (!lyrics.HasLyrics) PillLyric.Text = "♪";
            if (!_lyricHide.IsEnabled) _lyricHide.Start();
            return;
        }
        else
        {
            _lyricHide.Stop();
        }
        PillLyric.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show == _lyricShown) return;
        _lyricShown = show;
        ResizePillForIndicators();
    }
}
