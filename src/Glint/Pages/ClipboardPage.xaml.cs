using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Glint.Core;

namespace Glint.Pages;

public partial class ClipboardPage : UserControl
{
    // Relative times ("5 мин") only age when the view is rebuilt.
    static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(20);

    readonly ListCollectionView _view;
    string _query = string.Empty;
    ClipEntry? _pressed;
    DateTime _lastRefresh = DateTime.Now;

    public ClipboardPage()
    {
        InitializeComponent();

        // Own view instead of the default one, so other consumers of Items (command bar) keep their own filter.
        _view = new ListCollectionView(AppServices.Clipboard.Items) { Filter = Matches };
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => UpdateEmptyState();
        ClipList.ItemsSource = _view;

        IsVisibleChanged += OnVisibleChanged;
        UpdateEmptyState();
    }

    bool Matches(object item)
    {
        if (_query.Length == 0) return true;
        return item is ClipEntry { Kind: ClipKind.Text, Text: { } text }
            && text.Contains(_query, StringComparison.CurrentCultureIgnoreCase);
    }

    void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || DateTime.Now - _lastRefresh < RefreshAfter) return;
        _lastRefresh = DateTime.Now;
        _view.Refresh();
    }

    void UpdateEmptyState()
    {
        if (_view is null) return;
        var empty = _view.IsEmpty;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = AppServices.Clipboard.Items.Count == 0
            ? "Скопируйте что-нибудь — оно появится здесь"
            : "Ничего не найдено";
    }

    // ───────────── Search ─────────────

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _query = SearchBox.Text.Trim();
        if (_view is null) return;
        _view.Refresh();
        ListScroll.ScrollToTop();
    }

    void OnSearchMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The panel window is shown without activation; it has to be active for keystrokes to reach the box.
        var window = Window.GetWindow(this);
        if (window is { IsActive: false }) window.Activate();
    }

    void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when SearchBox.Text.Length > 0:
                SearchBox.Clear();
                e.Handled = true;
                break;
            case Key.Enter:
                if (_view.Cast<ClipEntry>().FirstOrDefault() is { } first)
                    Run(() => AppServices.Clipboard.PasteToPreviousWindow(first));
                e.Handled = true;
                break;
        }
    }

    void OnClearClick(object sender, RoutedEventArgs e) => Run(() => AppServices.Clipboard.ClearUnpinned());

    // ───────────── Rows ─────────────

    void OnRowMouseDown(object sender, MouseButtonEventArgs e) => _pressed = EntryOf(sender);

    void OnRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        var entry = EntryOf(sender);
        var pressed = _pressed;
        _pressed = null;
        // Only a press and release on the same row counts; ignores releases after scrolling or dragging in.
        if (entry is null || !ReferenceEquals(entry, pressed)) return;
        e.Handled = true;

        if (IsShiftDown())
            Run(() => AppServices.Clipboard.CopyOnly(entry));
        else
            Run(() => AppServices.Clipboard.PasteToPreviousWindow(entry));
    }

    void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) Run(() => AppServices.Clipboard.TogglePin(entry));
    }

    void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) Run(() => AppServices.Clipboard.CopyOnly(entry));
    }

    void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) Run(() => AppServices.Clipboard.Remove(entry));
    }

    static ClipEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipEntry;

    static bool IsShiftDown() =>
        (Keyboard.Modifiers & ModifierKeys.Shift) != 0 || (Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0;

    static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static class Win32
    {
        public const int VK_SHIFT = 0x10;

        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    }
}
