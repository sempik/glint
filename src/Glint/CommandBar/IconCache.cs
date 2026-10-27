using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glint.CommandBar;

/// <summary>Shell icons as frozen bitmaps, safe to create on any thread.</summary>
internal static class IconCache
{
    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // These carry their own icon; everything else shares one icon per extension.
    static readonly HashSet<string> PerFile = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".url", ".ico", ".appref-ms" };

    static string KeyFor(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length == 0 || PerFile.Contains(ext) ? path : ext;
    }

    public static bool TryGet(string path, out ImageSource? image) => Cache.TryGetValue(KeyFor(path), out image);

    /// <summary>Slow (disk + shell); call off the UI thread. Returns null if the file has no usable icon.</summary>
    public static ImageSource? Load(string path)
    {
        var key = KeyFor(path);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                image = bmp;
            }
        }
        catch (Exception)
        {
            // Missing target, network path, access denied: the glyph stays.
        }
        Cache[key] = image;
        return image;
    }
}
