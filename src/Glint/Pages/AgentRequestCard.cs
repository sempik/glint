using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glint.Core;
using Glint.Services.Agents;

namespace Glint.Pages;

/// <summary>
/// One request from a coding agent with its answer buttons: a permission (what it wants to run or change, then
/// Разрешить / Всегда / Запретить) or a question (its options, one question at a time). Used by the pill's request
/// view and by the Агенты tab; the buttons answer through <see cref="AgentHub"/> directly.
/// </summary>
public sealed class AgentRequestCard : StackPanel
{
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    readonly bool _roomy;
    readonly Dictionary<string, string> _answers = new();
    readonly HashSet<string> _picked = new();
    int _question;
    int _position = 1, _count = 1;
    TextBlock? _meta;
    string? _metaText;

    public AgentRequest Request { get; }

    /// <summary>The user answered here (any button). The pill uses it to hand the keyboard back.</summary>
    public event EventHandler? Acted;

    /// <summary>The content changed size (next question); the pill re-measures its shell.</summary>
    public event EventHandler? LayoutChanged;

    /// <param name="roomy">More preview lines: the Агенты tab has the space, the pill doesn't.</param>
    public AgentRequestCard(AgentRequest request, bool roomy = false)
    {
        Request = request;
        _roomy = roomy;
        Build();
    }

    /// <summary>"2 из 3" when several requests wait; the pill shows the oldest first.</summary>
    public void SetPosition(int position, int count)
    {
        _position = position;
        _count = count;
        Tick();
    }

    /// <summary>Refreshes the counter and the time left. Called once a second; repaints only when the text changes.</summary>
    public void Tick()
    {
        if (_meta is null) return;
        var parts = new List<string>(3);
        if (Request.Kind == AgentRequestKind.Question && Request.Questions.Count > 1) parts.Add($"вопрос {_question + 1} из {Request.Questions.Count}");
        if (_count > 1) parts.Add($"{_position} из {_count}");
        var left = Request.Remaining;
        // Minutes while there is plenty of time, seconds for the last one: one repaint a minute until then.
        parts.Add(left > TimeSpan.FromSeconds(60) ? $"{Math.Ceiling(left.TotalMinutes):0} мин" : $"0:{Math.Max(0, (int)left.TotalSeconds):00}");
        var text = string.Join(" · ", parts);
        if (text == _metaText) return;
        _metaText = text;
        _meta.Text = text;
        _meta.ToolTip = "Потом агент спросит в терминале как обычно";
    }

    void Build()
    {
        Children.Clear();
        _metaText = null;
        var question = Request.Kind == AgentRequestKind.Question ? Request.Questions[_question] : null;
        Children.Add(BuildHeader(question));
        if (question is null) Children.Add(BuildPreview());
        else BuildQuestion(question);
        Children.Add(BuildButtons(question));
        Tick();
    }

    FrameworkElement BuildHeader(AgentQuestion? question)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Style = (Style)FindResource("Icon"),
            Text = question is null ? Glyphs.Agent : Glyphs.Question,
            FontSize = 13,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        var disc = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = icon, VerticalAlignment = VerticalAlignment.Top };
        disc.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        grid.Children.Add(disc);

        var title = new TextBlock
        {
            Text = question is null ? Request.Action : (question.Header.Length > 0 ? question.Header : "Вопрос"),
            FontWeight = FontWeights.SemiBold,
        };
        var session = Request.Session;
        var subtitle = new TextBlock
        {
            Style = (Style)FindResource("MutedText"),
            Text = $"{session.AgentName} · {session.Project}",
            Margin = new Thickness(0, 1, 0, 0),
        };
        if (session.Window != IntPtr.Zero)
        {
            // The terminal is one click away for anyone who wants the full context first.
            subtitle.Cursor = Cursors.Hand;
            subtitle.ToolTip = "Показать терминал";
            subtitle.MouseLeftButtonDown += (_, e) => e.Handled = true;
            subtitle.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AgentProcesses.Focus(session.Window);
            };
        }
        var text = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        _meta = new TextBlock { Style = (Style)FindResource("MutedText"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        Grid.SetColumn(_meta, 2);
        grid.Children.Add(_meta);
        return grid;
    }

    FrameworkElement BuildPreview()
    {
        // Whole, never trimmed: «Разрешить» approves exactly this text, so all of it has to be readable. A long
        // command scrolls inside the box instead of hiding its tail behind an ellipsis.
        var text = new TextBlock
        {
            Text = Request.Preview,
            FontFamily = Mono,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
        };
        var scroller = new ScrollViewer
        {
            Content = text,
            MaxHeight = _roomy ? 150 : 92,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        scroller.PreviewMouseWheel += (_, e) =>
        {
            var atEdge = e.Delta > 0 ? scroller.VerticalOffset <= 0 : scroller.VerticalOffset >= scroller.ScrollableHeight;
            if (!atEdge) return;
            // Nothing more to scroll here: the wheel goes to the list around the card (the Агенты tab).
            e.Handled = true;
            (scroller.Parent as UIElement)?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = scroller });
        };
        var box = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 10, 0, 0),
            Child = scroller,
        };
        box.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        if (!Request.PreviewCut) return box;
        var note = new TextBlock
        {
            Style = (Style)FindResource("MutedText"),
            Text = "Слишком длинно, чтобы показать целиком: разрешить можно только в терминале",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        return new StackPanel { Children = { box, note } };
    }

    void BuildQuestion(AgentQuestion question)
    {
        Children.Add(new TextBlock
        {
            Text = question.Text,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = _roomy ? 80 : 54,
            Margin = new Thickness(0, 10, 0, 0),
        });
        var options = new WrapPanel { Margin = new Thickness(-3, 6, -3, 0) };
        foreach (var option in question.Options)
        {
            var label = new TextBlock { Text = option.Label, FontWeight = FontWeights.SemiBold, FontSize = 12.5 };
            var content = new StackPanel { Children = { label } };
            if (!string.IsNullOrWhiteSpace(option.Description))
            {
                content.Children.Add(new TextBlock
                {
                    Style = (Style)FindResource("MutedText"),
                    Text = option.Description,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 30,
                    Margin = new Thickness(0, 1, 0, 0),
                });
            }
            var button = new Button
            {
                Style = (Style)FindResource("TileButton"),
                Margin = new Thickness(3),
                Padding = new Thickness(10, 6, 10, 6),
                MaxWidth = _roomy ? 300 : 214,
                Focusable = false,
                Content = content,
                ToolTip = option.Description,
            };
            var chosen = option.Label;
            button.Click += (_, _) =>
            {
                if (!question.MultiSelect)
                {
                    _answers[question.Text] = chosen;
                    NextQuestion();
                    return;
                }
                if (!_picked.Remove(chosen)) _picked.Add(chosen);
                var on = _picked.Contains(chosen);
                button.SetResourceReference(BackgroundProperty, on ? "CardHoverBrush" : "CardBrush");
                label.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentBrush" : "TextBrush");
                UpdateDoneButton();
            };
            options.Children.Add(button);
        }
        Children.Add(options);
    }

    Button? _done;

    void UpdateDoneButton()
    {
        if (_done is not null) _done.IsEnabled = _picked.Count > 0;
    }

    void NextQuestion()
    {
        _picked.Clear();
        _question++;
        if (_question >= Request.Questions.Count)
        {
            AgentHub.Shared.Answer(Request, _answers);
            Acted?.Invoke(this, EventArgs.Empty);
            return;
        }
        Build();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    FrameworkElement BuildButtons(AgentQuestion? question)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };

        var terminal = MakeButton("В терминале", "MutedBrush", ghost: true,
            "Не отвечать здесь: агент сразу покажет свой обычный вопрос в терминале");
        terminal.Click += (_, _) => Act(() => AgentHub.Shared.PassToTerminal(Request));
        DockPanel.SetDock(terminal, Dock.Right);
        row.Children.Add(terminal);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        if (question is null)
        {
            // Nobody approves a command they couldn't read to the end; the terminal shows it whole.
            if (!Request.PreviewCut)
            {
                var allow = MakeButton("Разрешить", "AccentBrush", ghost: false, null, bold: true);
                allow.Click += (_, _) => Act(() => AgentHub.Shared.Allow(Request));
                left.Children.Add(allow);
            }
            if (Request.AlwaysRule is { } rule && !Request.PreviewCut)
            {
                var always = MakeButton("Всегда", "TextBrush", ghost: false, $"Разрешить и больше не спрашивать: {rule}");
                always.Click += (_, _) => Act(() => AgentHub.Shared.Allow(Request, always: true));
                left.Children.Add(always);
            }
            var deny = MakeButton("Запретить", "DangerBrush", ghost: false, "Агент узнает, что вы запретили, и продолжит без этого");
            deny.Click += (_, _) => Act(() => AgentHub.Shared.Deny(Request));
            left.Children.Add(deny);
        }
        else
        {
            if (question.MultiSelect)
            {
                _done = MakeButton("Готово", "AccentBrush", ghost: false, "Можно выбрать несколько", bold: true);
                _done.IsEnabled = false;
                _done.Click += (_, _) =>
                {
                    // In the order the agent listed them, as its own dialog would send them.
                    _answers[question.Text] = string.Join(", ", question.Options.Select(o => o.Label).Where(_picked.Contains));
                    NextQuestion();
                };
                left.Children.Add(_done);
            }
            else
            {
                _done = null;
            }
            var skip = MakeButton("Не отвечать", "DangerBrush", ghost: true, "Агент узнает, что вы отказались отвечать");
            skip.Click += (_, _) => Act(() => AgentHub.Shared.Deny(Request));
            left.Children.Add(skip);
        }
        row.Children.Add(left);
        return row;
    }

    Button MakeButton(string text, string brush, bool ghost, string? tip, bool bold = false)
    {
        var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return new Button
        {
            Style = (Style)FindResource(ghost ? "GhostButton" : "TileButton"),
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false,
            Content = label,
            ToolTip = tip,
        };
    }

    void Act(Action answer)
    {
        try
        {
            answer();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        Acted?.Invoke(this, EventArgs.Empty);
    }
}
