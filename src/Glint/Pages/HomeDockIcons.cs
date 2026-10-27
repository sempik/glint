using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glint.Pages;

/// <summary>Associated icons for dock entries, extracted off the UI thread and cached per path.</summary>
internal static class HomeDockIcons
{
    static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? TryGetCached(string path) => Cache.TryGetValue(path, out var src) ? src : null;

    public static async Task<ImageSource?> LoadAsync(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;

        // Prefer the 48 px shell image (same source as the shelf); ExtractAssociatedIcon is only 32 px and looks soft at high DPI.
        if (File.Exists(path) && ShelfIcons.Get(path) is { } shell)
        {
            Cache[path] = shell;
            return shell;
        }

        // GDI work happens on a pool thread; only raw BGRA bytes cross back, so no
        // WPF objects get created on a thread without a dispatcher.
        var pixels = await Task.Run(() => ExtractPixels(path));
        if (pixels is not { } p) return null;

        var src = BitmapSource.Create(p.Width, p.Height, 96, 96, PixelFormats.Bgra32, null, p.Bytes, p.Stride);
        src.Freeze();
        Cache[path] = src;
        return src;
    }

    static (int Width, int Height, int Stride, byte[] Bytes)? ExtractPixels(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            using var bmp = icon.ToBitmap();
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var stride = Math.Abs(data.Stride);
                var bytes = new byte[stride * bmp.Height];
                for (var y = 0; y < bmp.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * stride, stride);
                return (bmp.Width, bmp.Height, stride, bytes);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch (Exception)
        {
            // Unreadable or exotic files just keep the placeholder glyph.
            return null;
        }
    }
}
