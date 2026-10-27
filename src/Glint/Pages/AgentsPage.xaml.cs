using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Shapes;
using System.Windows.Threading;
using Glint.Core;
using Glyphs = Glint.Core.Glyphs;
using Glint.Services.Agents;

namespace Glint.Pages;

/// <summary>
/// Агенты: every Claude Code / Codex session Glint has heard from, most urgent first, with the requests that wait
/// for an answer on top. Updated in place on a hub change while the tab is on screen (a burst of tool events is one
/// update), plus one tick a minute for the "3 мин" ages and one a second for a request's countdown while one is
/// open; nothing runs while the tab is hidden.
/// </summary>
public partial class AgentsPage : UserControl
{
    readonly DispatcherTimer _ages = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    // Kept across updates, so a half-answered multi-question request doesn't start over and a button being pressed
    // stays in the tree.
    readonly Dictionary<Guid, Border> _cards = new();
    readonly Dictionary<string, SessionRow> _rows = new();
    bool _watching;
    bool _rebuildQueued;

    /// <summary>One session's card, kept while the session lives; an update only changes its texts.</summary>
    sealed class SessionRow
    {
        public required Border Root { get; init; }
        public required Ellipse Dot { get; init; }
        public required Run Project { get; init; }
        public required Run Agent { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Detail { get; init; }
        public required Button Terminal { get; init; }
        public string? Brush { get; set; }
        public IntPtr Window { get; set; }
    }

    public AgentsPage()
    {
        InitializeComponent();
        _ages.Tick += (_, _) => Rebuild();
        // Only the countdown text changes, and a card repaints only when it does (once a minute until the last one).
        _countdown.Tick += (_, _) =>
        {
            foreach (var frame in _cards.Values) (frame.Child as AgentRequestCard)?.Tick();
        };
        // The pill caches pages and hides the panel instead of unloading it, so watch visibility too.
        Loaded += (_, _) => UpdateWatching();
        Unloaded += (_, _) => UpdateWatching();
        IsVisibleChanged += (_, _) => UpdateWatching();
    }

    void UpdateWatching()
    {
        var wanted = IsLoaded && IsVisible;
        if (wanted == _watching) return;
        _watching = wanted;
        if (wanted)
        {
            AgentHub.Shared.Changed += OnHubChanged;
            _ages.Start();
            Rebuild();
        }
        else
        {
            AgentHub.Shared.Changed -= OnHubChanged;
            _ages.Stop();
            _countdown.Stop();
        }
    }

    void OnHubChanged(object? sender, EventArgs e)
    {
        // Two busy agents report several tool calls a second; one update after the burst is enough.
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _rebuildQueued = false;
            if (_watching) Rebuild();
        }, DispatcherPriority.Background);
    }

    void Rebuild()
    {
        var hub = AgentHub.Shared;
        var requests = hub.Requests;
        // By state, then by when it entered it: unlike the hub's "most recently active", two working agents don't
        // swap places on every tool call, so rows only move when something really changes.
        var sessions = hub.Sessions.OrderBy(s => s.Urgency).ThenByDescending(s => s.StateSince).ToList();

        foreach (var gone in _cards.Keys.Where(id => requests.All(r => r.Id != id)).ToList()) _cards.Remove(gone);
        foreach (var gone in _rows.Keys.Where(key => sessions.All(s => s.Key != key)).ToList()) _rows.Remove(gone);

        Summary.Text = !hub.IsListening ? "АГЕНТЫ · ПРИЁМ ВЫКЛЮЧЕН"
            : requests.Count > 0 ? $"АГЕНТЫ · ЖДУТ ОТВЕТА: {requests.Count}"
            : sessions.Count > 0 ? $"АГЕНТЫ · СЕССИЙ: {sessions.Count}"
            : "АГЕНТЫ";

        var wanted = new List<UIElement>(requests.Count + sessions.Count);
        foreach (var request in requests)
        {
            if (!_cards.TryGetValue(request.Id, out var frame))
            {
                var card = new AgentRequestCard(request, roomy: true);
                card.SetPosition(1, 1);
                frame = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8), Child = card };
                frame.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
                frame.BorderThickness = new Thickness(1);
                _cards[request.Id] = frame;
            }
            wanted.Add(frame);
        }
        foreach (var session in sessions)
        {
            if (!_rows.TryGetValue(session.Key, out var row))
            {
                row = BuildRow(session.Key);
                _rows[session.Key] = row;
            }
            UpdateRow(row, session);
            wanted.Add(row.Root);
        }
        // The children change only when the set or the order does.
        if (!List.Children.Cast<UIElement>().SequenceEqual(wanted))
        {
            List.Children.Clear();
            foreach (var element in wanted) List.Children.Add(element);
        }
        _countdown.IsEnabled = requests.Count > 0;

        var empty = requests.Count == 0 && sessions.Count == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty) UpdateEmptyText(hub.IsListening);
    }

    void UpdateEmptyText(bool listening)
    {
        if (!listening)
        {
            EmptyTitle.Text = "Приём событий выключен";
            EmptyHint.Text = "Включите его в Настройках → Агенты";
            return;
        }
        // Two small file reads, only when the list is empty and on screen.
        var connected = AgentInstaller.GetStatus(AgentKind.Claude).Status == HookStatus.Connected
            || AgentInstaller.GetStatus(AgentKind.Codex).Status == HookStatus.Connected;
        EmptyTitle.Text = connected ? "Агенты пока молчат" : "Агенты не подключены";
        EmptyHint.Text = connected
            ? "Запустите Claude Code или Codex — их сессии появятся здесь"
            : "Подключите Claude Code или Codex в Настройках → Агенты";
    }

    SessionRow BuildRow(string key)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(dot);

        var name = new TextBlock();
        var project = new Run { FontWeight = FontWeights.SemiBold };
        name.Inlines.Add(project);
        var agent = new Run();
        agent.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
        name.Inlines.Add(agent);

        var detail = new TextBlock { Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, 2, 0, 0) };
        var text = new StackPanel { Margin = new Thickness(10, 0, 8, 0), Children = { name, detail } };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        SessionRow? row = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var terminal = IconButton(Glyphs.Terminal, "Показать терминал", () => AgentProcesses.Focus(row!.Window));
        buttons.Children.Add(terminal);
        buttons.Children.Add(IconButton(Glyphs.Close, "Убрать из списка (вернётся со следующим событием)", () => AgentHub.Shared.Forget(key)));
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        row = new SessionRow
        {
            Root = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(12, 9, 8, 9), Margin = new Thickness(0, 0, 0, 6), Child = grid },
            Dot = dot,
            Project = project,
            Agent = agent,
            Name = name,
            Detail = detail,
            Terminal = terminal,
        };
        return row;
    }

    /// <summary>Puts the session's current state into its row; unchanged values don't repaint anything.</summary>
    static void UpdateRow(SessionRow row, AgentSession session)
    {
        var shown = session.Shown;
        var brush = AgentSession.StateBrush(shown);
        if (brush != row.Brush)
        {
            row.Brush = brush;
            row.Dot.SetResourceReference(Shape.FillProperty, brush);
        }
        row.Project.Text = session.Project;
        row.Agent.Text = " · " + session.AgentName;
        row.Name.ToolTip = string.IsNullOrEmpty(session.Cwd) ? null : session.Cwd;

        var state = $"{AgentSession.StateText(shown)} · {Ago(shown == session.State ? session.StateSince : session.LastActivity)}";
        row.Detail.Text = string.IsNullOrEmpty(session.Detail) ? state : $"{state} — {session.Detail}";
        row.Detail.ToolTip = session.Detail;

        row.Window = session.Window;
        row.Terminal.Visibility = session.Window != IntPtr.Zero ? Visibility.Visible : Visibility.Collapsed;
    }

    Button IconButton(string glyph, string tip, Action action)
    {
        var icon = new TextBlock { Style = (Style)FindResource("Icon"), Text = glyph, FontSize = 12 };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var button = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 5, 8, 5), ToolTip = tip, Content = icon, Focusable = false };
        button.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        };
        return button;
    }

    static string Ago(DateTime since)
    {
        var age = DateTime.Now - since;
        if (age < TimeSpan.FromMinutes(1)) return "только что";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} мин";
        return $"{(int)age.TotalHours} ч";
    }

    void Settings_Click(object sender, RoutedEventArgs e) => (AppServices.Notifier as Glint.Pill.PillWindow)?.ShowAgentSettings();
}
