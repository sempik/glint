using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glint.Core;

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
    readonly SystemCommands _system = new();
    readonly WebSearchProvider _web = new();

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
        Hide();
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
            return Run(_timers, q, ct)
                .Concat(SystemCommands.QuickSuggestions())
                .Concat(SafeList(() => AppIndex.Shared.Recent(3)))
                .Take(MaxRows)
                .ToList();
        }

        if (q.StartsWith('>')) return Run(_system, q, ct).Take(MaxRows).ToList();

        // Fixed group order: calculator, timers, apps, files, system; the web fallback always keeps the last slot.
        var groups = new IEnumerable<CommandResult>[]
        {
            Run(_calculator, q, ct),
            Run(_timers, q, ct),
            Run(AppIndex.Shared, q, ct).Take(5),
            Run(FileIndex.Shared, q, ct).Take(5),
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
        return row;
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
