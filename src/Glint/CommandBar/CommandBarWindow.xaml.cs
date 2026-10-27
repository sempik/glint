using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glint.Core;
using Glint.Services.Voice;

namespace Glint.CommandBar;

public partial class CommandBarWindow : Window
{
    enum ExecMode { Normal, RevealFolder, Elevated }

    // Unscaled DIPs; BarBorder has a 20 DIP side margin and 10 DIP top margin for the shadow.
    const double BarWidth = 620, SideMargin = 20, TopMargin = 10, TopOffset = 70;
    const int MaxRows = 8;
    const int ErrorCancelled = 1223; // user dismissed the UAC prompt

    readonly Calculator _calculator = new();
    readonly TimerProvider _timers = new();
    readonly CurrencyProvider _currency = new();
    readonly NotesProvider _noteCreate = new(NotesProvider.Mode.Create);
    readonly NotesProvider _noteSearch = new(NotesProvider.Mode.Search);
    readonly ScreenToolsProvider _screen = new();
    readonly WeatherProvider _weather = new();
    readonly VoiceProvider _voice = new();
    readonly SystemCommands _system = new();
    readonly WebSearchProvider _web = new();
    readonly TextBoxDictationTarget _dictation;
    readonly string _placeholderText;

    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(60) };
    readonly List<Border> _rows = new();
    List<CommandResult> _results = new();
    string? _renderedQuery;
    int _selected;
    int _generation;
    CancellationTokenSource? _searchCts;
    IntPtr _hwnd;

    public CommandBarWindow()
    {
        InitializeComponent();
        SoftShadow.Fill(BarShadow, BarBorder.CornerRadius.TopLeft, rings: 10, depth: 6, strength: 0.32);

        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RefreshAsync(); };
        QueryBox.TextChanged += (_, _) =>
        {
            Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _debounce.Stop();
            _debounce.Start();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => HideBar();
        AppServices.Settings.Changed += (_, _) => { if (IsVisible) ApplyLayout(); };
        // Rates arrive a moment after "100 usd" is typed; show them without another keystroke.
        Glint.Services.CurrencyRates.Shared.Updated += (_, _) => Dispatcher.BeginInvoke(() => { if (IsVisible) _ = RefreshAsync(); });

        // Dictated words go into the query box like typed ones; the phrase being heard shows in the placeholder.
        _placeholderText = Placeholder.Text;
        _dictation = new TextBoxDictationTarget(QueryBox, "Командная строка", ShowVoicePreview);
        MicButton.Click += (_, _) =>
        {
            QueryBox.Focus();
            DictationService.Shared.Toggle(_dictation);
        };
        DictationService.Shared.Changed += (_, _) => UpdateMic();

        AppIndex.Shared.Start();
        FileIndex.Shared.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: no Alt+Tab entry.
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 would destroy the window and break the next Toggle; app shutdown ignores this cancel.
        e.Cancel = true;
        HideBar();
        base.OnClosing(e);
    }

    public void Toggle()
    {
        try
        {
            if (IsVisible)
            {
                HideBar();
                return;
            }
            ApplyLayout();
            Show();
            Activate();
            if (_hwnd != IntPtr.Zero) Native.SetForegroundWindow(_hwnd);
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
            QueryBox.SelectAll();
            _debounce.Stop();
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void HideBar()
    {
        if (!IsVisible) return;
        _debounce.Stop();
        _searchCts?.Cancel();
        // Words for a box that is going away would only end up on the clipboard.
        DictationService.Shared.StopFor(_dictation);
        Hide();
    }

    void ShowVoicePreview(string? text) => Placeholder.Text = text is null ? _placeholderText : "Слышу: " + text;

    void UpdateMic()
    {
        var mine = ReferenceEquals(DictationService.Shared.Target, _dictation);
        MicIcon.SetResourceReference(TextBlock.ForegroundProperty, mine ? "DangerBrush" : "MutedBrush");
        MicButton.ToolTip = mine ? "Закончить диктовку" : "Сказать запрос голосом";
        if (!mine) Placeholder.Text = _placeholderText;
    }

    void ApplyLayout()
    {
        var settings = AppServices.Settings.Current;
        var scale = Math.Clamp(settings.Scale, 0.8, 2.0);
        RootScale.ScaleX = RootScale.ScaleY = scale;
        Width = (BarWidth + SideMargin * 2) * scale;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = (TopOffset - TopMargin) * scale;
        HotkeyChip.Text = string.IsNullOrWhiteSpace(settings.CommandBarHotkey) ? "Alt+Space" : settings.CommandBarHotkey;
    }

    // ───────────── Search ─────────────

    async Task RefreshAsync()
    {
        var query = QueryBox.Text;
        var generation = ++_generation;
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        List<CommandResult> results;
        try
        {
            results = await Task.Run(() => Search(query, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return;
        }
        // A newer keystroke already started another search.
        if (generation != _generation) return;
        _renderedQuery = query;
        Render(results);
    }

    /// <summary>Runs on a thread-pool thread.</summary>
    List<CommandResult> Search(string query, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            // Reminders keep for up to a year, and Enter on one cancels it: so only the two soonest, and never in the
            // first, pre-selected row. "таймер" lists them all.
            var quick = ScreenToolsProvider.QuickSuggestions().Concat(SystemCommands.QuickSuggestions()).ToList();
            var reminders = Run(_timers, q, ct).Take(2).ToList();
            var recent = SafeList(() => AppIndex.Shared.Recent(Math.Clamp(MaxRows - quick.Count - reminders.Count, 0, 3)));
            return quick.Concat(recent).Concat(reminders).Take(MaxRows).ToList();
        }

        if (q.StartsWith('>')) return Run(_system, q, ct).Take(MaxRows).ToList();

        // Fixed group order: calculator, currency, timers, quick note, screen tools, weather, dictation, apps,
        // files, found notes, system; the web fallback always keeps the last slot. Weather and dictation matched
        // by a bare keyword prefix ("про", "voi") are more likely an app being typed, so they go after the apps.
        var keywords = Run(_weather, q, ct).Concat(Run(_voice, q, ct)).ToList();
        var groups = new IEnumerable<CommandResult>[]
        {
            Run(_calculator, q, ct),
            Run(_currency, q, ct).Take(3),
            Run(_timers, q, ct),
            Run(_noteCreate, q, ct),
            Run(_screen, q, ct).Take(3),
            keywords.Where(r => !r.PrefixOnly),
            Run(AppIndex.Shared, q, ct).Take(5),
            keywords.Where(r => r.PrefixOnly),
            Run(FileIndex.Shared, q, ct).Take(5),
            Run(_noteSearch, q, ct).Take(3),
            Run(_system, q, ct).Take(3),
        };
        var tail = Run(_web, q, ct);
        ct.ThrowIfCancellationRequested();

        var list = groups.SelectMany(g => g).Take(MaxRows - tail.Count).ToList();
        list.AddRange(tail);
        return list;
    }

    static List<CommandResult> Run(ICommandProvider provider, string query, CancellationToken ct) =>
        SafeList(() => provider.Query(query, ct), rethrowCancel: true);

    static List<CommandResult> SafeList(Func<IEnumerable<CommandResult>> source, bool rethrowCancel = false)
    {
        try
        {
            return source().ToList();
        }
        catch (OperationCanceledException) when (rethrowCancel)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One broken provider must not take the others down with it.
            App.LogError(ex);
            return new List<CommandResult>();
        }
    }

    // ───────────── Rows ─────────────

    void Render(List<CommandResult> results)
    {
        _results = results;
        _rows.Clear();
        Rows.Children.Clear();
        for (var i = 0; i < results.Count; i++)
        {
            var row = BuildRow(results[i], i);
            _rows.Add(row);
            Rows.Children.Add(row);
        }
        var any = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = any;
        Footer.Visibility = any;
        Select(0);
    }

    Border BuildRow(CommandResult r, int index)
    {
        var glyph = new TextBlock
        {
            Style = (Style)FindResource("Icon"),
            Text = r.Glyph,
            FontSize = 16,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var iconHost = new Grid { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        iconHost.Children.Add(glyph);
        if (r.IconPath is { } iconPath) AttachIcon(iconHost, glyph, iconPath);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = r.Title, FontSize = 14 });
        if (!string.IsNullOrEmpty(r.Subtitle))
        {
            text.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("MutedText"),
                Text = r.Subtitle,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        var hint = new TextBlock
        {
            Style = (Style)FindResource("MutedText"),
            Text = r.Hint ?? string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(hint, 2);
        grid.Children.Add(iconHost);
        grid.Children.Add(text);
        grid.Children.Add(hint);

        var row = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 7, 12, 7),
            MinHeight = 44,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        // MouseMove rather than MouseEnter: re-rendered rows under a resting cursor shouldn't steal the selection.
        row.MouseMove += (_, _) => { if (_selected != index) Select(index); };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Select(index);
            Execute(ModeFromModifiers());
        };
        if (r.CanPin && r.Path is { } path) row.ContextMenu = BuildPinMenu(r, path, index);
        return row;
    }

    ContextMenu BuildPinMenu(CommandResult r, string path, int index)
    {
        var pin = new MenuItem();
        pin.Click += (_, _) =>
        {
            try
            {
                if (PinnedApps.IsPinned(path))
                {
                    PinnedApps.Unpin(path);
                    AppServices.Notifier.Flash("Откреплено", r.Title, Glyphs.Unpin);
                }
                else
                {
                    PinnedApps.Pin(new[] { path });
                    AppServices.Notifier.Flash("Закреплено на главной", r.Title, Glyphs.Pin);
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        };
        var reveal = new MenuItem { Header = "Показать в папке" };
        reveal.Click += (_, _) => { Select(index); Execute(ExecMode.RevealFolder); };
        var elevated = new MenuItem { Header = "От имени администратора" };
        elevated.Click += (_, _) => { Select(index); Execute(ExecMode.Elevated); };

        var menu = new ContextMenu { Items = { pin, reveal, elevated } };
        // The dock may have changed since the row was built.
        menu.Opened += (_, _) => pin.Header = PinnedApps.IsPinned(path) ? "Открепить с главной" : "Закрепить на главной";
        return menu;
    }

    void AttachIcon(Grid host, TextBlock glyph, string path)
    {
        if (IconCache.TryGet(path, out var cached))
        {
            if (cached is not null) SetImage(cached);
            return;
        }
        Task.Run(() => IconCache.Load(path)).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } image)
                Dispatcher.BeginInvoke(() => SetImage(image));
        }, TaskScheduler.Default);

        void SetImage(ImageSource image)
        {
            glyph.Visibility = Visibility.Collapsed;
            var img = new Image { Source = image, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            host.Children.Add(img);
        }
    }

    void Select(int index)
    {
        if (_rows.Count == 0)
        {
            _selected = 0;
            return;
        }
        _selected = ((index % _rows.Count) + _rows.Count) % _rows.Count;
        for (var i = 0; i < _rows.Count; i++)
        {
            // Resource reference so the highlight follows a theme change while the bar is open.
            if (i == _selected) _rows[i].SetResourceReference(Border.BackgroundProperty, "CardHoverBrush");
            else _rows[i].Background = Brushes.Transparent;
        }
        _rows[_selected].BringIntoView();
    }

    // ───────────── Keyboard / execution ─────────────

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                HideBar();
                break;
            case Key.Down:
                e.Handled = true;
                Select(_selected + 1);
                break;
            case Key.Up:
                e.Handled = true;
                Select(_selected - 1);
                break;
            case Key.Enter:
                e.Handled = true;
                _ = ExecuteWhenCurrentAsync(ModeFromModifiers());
                break;
        }
    }

    static ExecMode ModeFromModifiers()
    {
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Shift) != 0) return ExecMode.Elevated;
        if ((mods & ModifierKeys.Control) != 0) return ExecMode.RevealFolder;
        return ExecMode.Normal;
    }

    /// <summary>Enter right after typing must act on what's typed, not on the previous keystroke's results.</summary>
    async Task ExecuteWhenCurrentAsync(ExecMode mode)
    {
        if (_renderedQuery != QueryBox.Text)
        {
            _debounce.Stop();
            await RefreshAsync();
            if (_renderedQuery != QueryBox.Text) return;
        }
        Execute(mode);
    }

    void Execute(ExecMode mode)
    {
        if (_selected < 0 || _selected >= _results.Count) return;
        var r = _results[_selected];
        // Ctrl+Enter / Ctrl+Shift+Enter must never fall back to the normal action (e.g. locking the PC).
        if ((mode == ExecMode.RevealFolder && r.Path is null) || (mode == ExecMode.Elevated && r.ExecuteElevated is null))
        {
            AppServices.Notifier.Flash("Недоступно для этого результата", r.Title, Glyphs.Info);
            return;
        }
        if (r.KeepsBarOpen)
        {
            try
            {
                r.Execute();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            return;
        }
        // Hide first so the launched app (or the previous window) gets the foreground.
        HideBar();
        try
        {
            switch (mode)
            {
                case ExecMode.RevealFolder when r.Path is not null:
                    ShellRun.Reveal(r.Path);
                    break;
                case ExecMode.Elevated when r.ExecuteElevated is not null:
                    r.ExecuteElevated();
                    break;
                default:
                    r.Execute();
                    break;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // The user said no to UAC; nothing to report.
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppServices.Notifier.Flash("Не удалось выполнить", r.Title, Glyphs.Info);
        }
    }
}
