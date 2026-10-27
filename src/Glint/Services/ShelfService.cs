using System.Collections.ObjectModel;
using System.Windows.Threading;
using Glint.Core;

namespace Glint.Services;

/// <summary>Persisted shape of a shelf entry (ShelfItem has computed members we don't want to round-trip).</summary>
internal sealed class ShelfRecord
{
    public string Path { get; set; } = string.Empty;
    public DateTime Added { get; set; }
}

/// <summary>Keeps references to files parked on the pill. Nothing is copied; missing paths are dropped on load.</summary>
public sealed class ShelfService : IShelfService
{
    const string FileName = "shelf.json";

    // Created by App on the UI thread; every mutation of Items is funneled back here.
    readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public ObservableCollection<ShelfItem> Items { get; } = new();

    public ShelfService()
    {
        try
        {
            var stored = SettingsStore.ReadJson<List<ShelfRecord>>(FileName);
            if (stored is null) return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in stored)
            {
                if (string.IsNullOrWhiteSpace(r.Path) || !Exists(r.Path) || !seen.Add(Key(r.Path))) continue;
                Items.Add(new ShelfItem { Path = r.Path, Added = r.Added });
            }
            if (Items.Count != stored.Count) Save();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    public void Add(IEnumerable<string> paths)
    {
        if (!_dispatcher.CheckAccess())
        {
            var copy = paths.ToList();
            _dispatcher.InvokeAsync(() => Add(copy));
            return;
        }

        try
        {
            var existing = new HashSet<string>(Items.Select(i => Key(i.Path)), StringComparer.OrdinalIgnoreCase);
            var added = new List<ShelfItem>();
            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string full;
                try { full = Path.GetFullPath(raw); }
                catch (Exception) { continue; }
                if (!Exists(full) || !existing.Add(Key(full))) continue;

                var item = new ShelfItem { Path = full };
                // Newest on top, but a multi-file drop keeps its own order.
                Items.Insert(added.Count, item);
                added.Add(item);
            }
            if (added.Count == 0) return;

            Save();
            var subtitle = added.Count == 1 ? added[0].Name : $"{added.Count} {FilesWord(added.Count)}";
            AppServices.Notifier?.Flash("На полке", subtitle, Glyphs.Shelf);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    public void Remove(ShelfItem item)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => Remove(item));
            return;
        }
        if (Items.Remove(item)) Save();
    }

    public void Clear()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(Clear);
            return;
        }
        if (Items.Count == 0) return;
        Items.Clear();
        Save();
    }

    /// <summary>Russian plural: 1 файл, 2 файла, 5 файлов, 21 файл, 11 файлов.</summary>
    internal static string FilesWord(int n)
    {
        n = Math.Abs(n);
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return "файлов";
        return (n % 10) switch
        {
            1 => "файл",
            2 or 3 or 4 => "файла",
            _ => "файлов",
        };
    }

    void Save()
    {
        try
        {
            SettingsStore.WriteJson(FileName, Items.Select(i => new ShelfRecord { Path = i.Path, Added = i.Added }).ToList());
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    static string Key(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception) { return path.TrimEnd('\\', '/'); }
    }
}
