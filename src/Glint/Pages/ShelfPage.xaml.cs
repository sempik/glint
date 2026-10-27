using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Glint.Core;

namespace Glint.Pages;

public partial class ShelfPage : UserControl
{
    Point _dragStart;
    ShelfItem? _pressedItem;

    public ShelfPage()
    {
        InitializeComponent();
        Tiles.ItemsSource = AppServices.Shelf.Items;
        ClearButton.Click += (_, _) => AppServices.Shelf.Clear();

        // The page is cached by the pill, so only listen while it is on screen.
        Loaded += (_, _) =>
        {
            AppServices.Shelf.Items.CollectionChanged -= OnItemsChanged;
            AppServices.Shelf.Items.CollectionChanged += OnItemsChanged;
            UpdateHeader();
        };
        Unloaded += (_, _) => AppServices.Shelf.Items.CollectionChanged -= OnItemsChanged;
        UpdateHeader();
    }

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateHeader();

    void UpdateHeader()
    {
        var count = AppServices.Shelf.Items.Count;
        CountText.Text = count > 0 ? count.ToString() : string.Empty;
        ClearButton.IsEnabled = count > 0;
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static ShelfItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ShelfItem;

    // ───────────── Tile mouse handling ─────────────

    void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemOf(sender);
        if (item is null) return;

        if (e.ClickCount == 2)
        {
            _pressedItem = null;
            Open(item);
            e.Handled = true;
            return;
        }
        _pressedItem = item;
        _dragStart = e.GetPosition(this);
    }

    void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _pressedItem = null;

    void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedItem = null;
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _pressedItem;
        _pressedItem = null;
        StartDrag((DependencyObject)sender, item);
    }

    void StartDrag(DependencyObject source, ShelfItem item)
    {
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            var result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

            // Only an explicit move counts: a WPF target that never sets Effects reports all allowed flags.
            // Explorer's optimized moves may report None, so also check whether the file is gone.
            if (result == DragDropEffects.Move || !Exists(item.Path))
                AppServices.Shelf.Remove(item);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) AppServices.Shelf.Remove(item);
        e.Handled = true;
    }

    // ───────────── Context menu ─────────────

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Open(item);
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        try
        {
            System.Windows.Clipboard.SetText(item.Path);
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process for a moment.
            App.LogError(ex);
        }
    }

    void RemoveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) AppServices.Shelf.Remove(item);
    }

    static void Open(ShelfItem item)
    {
        if (!Exists(item.Path))
        {
            AppServices.Shelf.Remove(item);
            AppServices.Notifier.Flash("Файл не найден", item.Name, Glyphs.Info);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
